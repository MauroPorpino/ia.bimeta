using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A ENTRADA na fila parte da conversa com a IA, e a autorização dela é sobre esse estado: a base (o modo de IA, sem responsável, no período lido)
/// fica na intenção e vai com cada comando como pré-condição, conferida dentro da trava da sessão. Quem assumiu a conversa entre a leitura e a
/// gravação, ou entre a consulta do executor e a aplicação, não é retirado pela entrada, e a renovação da versão esperada não renova a
/// autorização. A repetição de um efeito já confirmado (resposta perdida, reconciliação) continua reconhecida. PostgreSQL real; as intercalações
/// são determinísticas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnterAuthorityTests(PostgresFixture banco)
{
    private sealed record Cena(CenarioDeConversas C, ServiceQueueView Fila, Agente Beto, Agente Carla, Guid Sessao);

    /// <summary>Uma fila com o Beto e a Carla; a conversa está com a IA (período 0), sem item no ContactCenter.</summary>
    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var fila = await c.CriarFilaAsync("Geral");
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var carla = await c.AgenteNaFilaAsync(banco, fila.Id);
        return new Cena(c, fila, beto, carla, await c.NovaConversaAsync());
    }

    private sealed record CenaComUnidades(CenarioDeConversas C, Guid Matriz, Guid Filial, ServiceQueueView FilaDaFilial, Agente Carla, Guid Sessao);

    /// <summary>
    /// O número autoriza a matriz e a filial; uma regra leva toda conversa para a fila da filial. A conversa está com a IA, na matriz: a entrada
    /// por regra a leva para a filial junto (o encaminhamento da unidade e depois quem responde).
    /// </summary>
    private async Task<CenaComUnidades> MontarComUnidadesAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        var filaDaFilial = await c.CriarFilaAsync("Filial", unidade: filial);
        await c.CriarRegraAsync(filaDaFilial.Id, "Tudo para a filial");
        var carla = await c.AgenteNaFilaAsync(banco, filaDaFilial.Id);
        return new CenaComUnidades(c, matriz, filial, filaDaFilial, carla, await c.NovaConversaAsync(unidade: matriz, canal: canal));
    }

    /// <summary>O atendente assume DIRETO no Conversations (por fora do ContactCenter): período 1, com ele como responsável.</summary>
    private static async Task AssumirPorForaAsync(CenarioDeConversas c, Guid sessao, Guid responsavel) =>
        Assert.Equal(
            TransitionOutcome.Applied,
            (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, responsavel, await c.VersaoDaSessaoAsync(sessao))).Outcome);

    private static async Task EstadoAsync(CenarioDeConversas c, Guid sessao, HandlingMode modo, Guid? responsavel)
    {
        var visao = (await c.SessaoAsync(sessao))!;
        Assert.Equal((modo, responsavel), (visao.HandlingMode, visao.ResponderRef));
    }

    private static async Task<int> AuditoriasAsync(CenarioDeConversas c, string operacao, AuditOutcome resultado) =>
        (await c.AuditoriaAsync(operacao)).Count(e => e.Outcome == resultado);

    // As duas intercalações do bloqueio

    [Fact]
    public async Task A_entrada_autorizada_com_a_conversa_na_IA_nao_retira_a_Carla_que_assumiu_entre_a_leitura_e_a_gravacao()
    {
        var s = await MontarAsync("entrada1");
        var intercalou = false;
        var eventosDaCarla = -1;

        // O pedido lê a sessão (com a IA) e é autorizado; logo depois, a Carla assume a conversa por fora. O caminho antigo gravava a
        // intenção sem a base e o executor, relendo a sessão, passava a conversa da Carla para "espera, sem responsável".
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.NaAutorizacaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.GetSessionAsync), async () =>
            {
                await AssumirPorForaAsync(s.C, s.Sessao, s.Carla.Id);
                eventosDaCarla = s.C.EventosDe(IaBimeta.Conversations.Contracts.Events.ConversationEventTypes.HandlingModeChanged, s.Sessao);
                intercalou = true;
            }),
            svc => svc.RequestHandoffAsync(s.Sessao, s.Fila.Id, Guid.NewGuid(), "ai-requested")));

        Assert.True(intercalou);
        Assert.Equal(ContactCenterRule.ConversationChanged, recusa.Rule);
        await EstadoAsync(s.C, s.Sessao, HandlingMode.HumanAssisted, s.Carla.Id); // a Carla continua com a conversa
        Assert.Equal(eventosDaCarla, s.C.EventosDe(IaBimeta.Conversations.Contracts.Events.ConversationEventTypes.HandlingModeChanged, s.Sessao)); // a entrada não gravou nada
        Assert.Empty(await s.C.ItensAsync()); // nenhum item ativo
        Assert.Equal(1, await AuditoriasAsync(s.C, "contact-center.item.enter", AuditOutcome.Denied));

        // Pedir de novo enxerga a Carla: a conversa já tem atendente humano.
        var de_novo = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.PedirAtendimentoAsync(s.Sessao, s.Fila.Id));
        Assert.Equal(ContactCenterRule.ConversationAlreadyHandledByHuman, de_novo.Rule);
        await EstadoAsync(s.C, s.Sessao, HandlingMode.HumanAssisted, s.Carla.Id);
    }

    [Fact]
    public async Task A_Carla_que_assume_entre_a_consulta_do_executor_e_a_aplicacao_nao_e_retirada_pela_versao_renovada()
    {
        var s = await MontarAsync("entrada2");
        var intercalou = false;

        // O executor registra a cerca e lê a sessão (com a IA); ANTES de aplicar, a Carla assume. A versão que ele leu fica velha: o caminho
        // antigo recebia "versão defasada", relia, renovava a versão e aplicava a entrada sobre a conversa da Carla.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.NaExecucaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.FenceAsync), async () =>
            {
                await AssumirPorForaAsync(s.C, s.Sessao, s.Carla.Id);
                intercalou = true;
            }),
            svc => svc.RequestHandoffAsync(s.Sessao, s.Fila.Id, Guid.NewGuid(), "ai-requested")));

        Assert.True(intercalou);
        Assert.Equal(ContactCenterRule.ConversationChanged, recusa.Rule);
        await EstadoAsync(s.C, s.Sessao, HandlingMode.HumanAssisted, s.Carla.Id);
        Assert.Empty(await s.C.ItensAsync());
        Assert.Equal(1, await AuditoriasAsync(s.C, "contact-center.item.enter", AuditOutcome.Denied));
    }

    [Fact]
    public async Task A_entrada_que_inclui_a_unidade_respeita_a_mesma_condicao_e_nao_move_a_conversa_da_Carla()
    {
        var s = await MontarComUnidadesAsync("entrada3");
        var intercalou = false;

        // A regra leva a conversa para a filial. A Carla assume entre a leitura e a gravação: a unidade não é aplicada sobre a conversa dela.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.NaAutorizacaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.GetSessionAsync), async () =>
            {
                await AssumirPorForaAsync(s.C, s.Sessao, s.Carla.Id);
                intercalou = true;
            }),
            svc => svc.RequestHandoffAsync(s.Sessao, null, Guid.NewGuid(), "ai-requested")));

        Assert.True(intercalou);
        Assert.Equal(ContactCenterRule.ConversationChanged, recusa.Rule);
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((s.Matriz, HandlingMode.HumanAssisted, (Guid?)s.Carla.Id), (sessao.BusinessUnitId, sessao.HandlingMode, sessao.ResponderRef));
        Assert.Empty((await s.C.RastroAsync(s.Sessao))!);
        Assert.Equal(0, s.C.EventosDe(IaBimeta.Conversations.Contracts.Events.ConversationEventTypes.SessionTransferred, s.Sessao));
        Assert.Empty(await s.C.ItensAsync());
    }

    // Perda de resposta e reconciliação

    [Fact]
    public async Task Entrada_cuja_resposta_se_perdeu_e_concluida_pela_reconciliacao_sem_repetir_o_efeito()
    {
        var s = await MontarAsync("entrada4");
        var pedido = Guid.NewGuid();

        // O Conversations CONFIRMA a entrada (humano, sem responsável) e a resposta se perde: o item fica pendente, com a base gravada.
        var interrompida = await s.C.NaExecucaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.RequestHandoffAsync(s.Sessao, s.Fila.Id, pedido, "ai-requested"));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        await EstadoAsync(s.C, s.Sessao, HandlingMode.HumanAssisted, null);
        var pendente = interrompida.Item.Pending!;
        Assert.Equal(PendingKind.Enter, pendente.Kind);
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);

        // A reconciliação conclui: reconhece a transição já aplicada e não escreve de novo no Conversations.
        await s.C.LiberarRecuoAsync(interrompida.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());
        var item = (await s.C.ItemAsync(interrompida.Item.Id))!;
        Assert.Equal((QueueItemStatus.Waiting, null), (item.Status, item.Pending));
        Assert.Equal(versao, await s.C.VersaoDaSessaoAsync(s.Sessao));

        // Repetir o mesmo pedido devolve o resultado, sem outro item nem outro efeito.
        var repetida = await s.C.PedirAtendimentoAsync(s.Sessao, s.Fila.Id, pedido);
        Assert.Equal(item.Id, repetida.Item.Id);
        Assert.Equal(new[] { HistoryKind.Entered }, (await s.C.HistoricoDoItemAsync(item.Id))!.Select(h => h.Kind).ToArray());
        Assert.Equal(versao, await s.C.VersaoDaSessaoAsync(s.Sessao));
    }

    [Fact]
    public async Task Entrada_com_a_resposta_perdida_e_a_Carla_assumindo_depois_e_reconhecida_sem_retirar_a_Carla()
    {
        var s = await MontarAsync("entrada5");
        var pedido = Guid.NewGuid();

        var interrompida = await s.C.NaExecucaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.RequestHandoffAsync(s.Sessao, s.Fila.Id, pedido, "ai-requested"));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);

        // Antes de a reconciliação chegar, a Carla assume por fora.
        await AssumirPorForaAsync(s.C, s.Sessao, s.Carla.Id);
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);

        // A transição da entrada JÁ foi aplicada (mesmo identificador): é reconhecida antes da condição, que o efeito dela e a Carla já não
        // satisfazem. A entrada conclui, e a conversa da Carla não é tocada.
        await s.C.LiberarRecuoAsync(interrompida.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());
        await EstadoAsync(s.C, s.Sessao, HandlingMode.HumanAssisted, s.Carla.Id);
        Assert.Equal(versao, await s.C.VersaoDaSessaoAsync(s.Sessao));
        Assert.Null((await s.C.ItemAsync(interrompida.Item.Id))!.Pending);

        // O item ainda diz "em espera" (o evento da Carla chegou com a entrada pendente), mas quem manda é o Conversations: ninguém tira a
        // conversa da Carla pela projeção local.
        var conflito = await Assert.ThrowsAsync<QueueItemConflictException>(() => s.C.AssumirAsync(interrompida.Item.Id, s.Beto.Contexto));
        Assert.Equal(QueueItemConflict.AlreadyAssigned, conflito.Conflict);
        await EstadoAsync(s.C, s.Sessao, HandlingMode.HumanAssisted, s.Carla.Id);
    }

    // A transferência de unidade associada à entrada, confirmada em parte

    [Fact]
    public async Task A_entrada_desfeita_depois_de_a_unidade_ser_aplicada_mantem_a_unidade_deixa_o_registro_e_nao_retira_a_Carla()
    {
        var s = await MontarComUnidadesAsync("entrada6");
        var intercalou = false;

        // O executor aplica a unidade (a conversa vai para a filial); ANTES da transição de quem responde, a Carla assume.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.NaExecucaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.TransferAsync), async () =>
            {
                await AssumirPorForaAsync(s.C, s.Sessao, s.Carla.Id);
                intercalou = true;
            }),
            svc => svc.RequestHandoffAsync(s.Sessao, null, Guid.NewGuid(), "ai-requested")));

        Assert.True(intercalou);
        Assert.Equal(ContactCenterRule.ConversationChanged, recusa.Rule);

        // A unidade que o Conversations confirmou não se desfaz daqui, e a Carla continua: a entrada é que foi desfeita (sem item ativo).
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((s.Filial, HandlingMode.HumanAssisted, (Guid?)s.Carla.Id), (sessao.BusinessUnitId, sessao.HandlingMode, sessao.ResponderRef));
        Assert.Single((await s.C.RastroAsync(s.Sessao))!);
        Assert.Empty(await s.C.ItensAsync());

        // O registro do efeito confirmado fica na trilha, ao lado da negação da entrada.
        Assert.Equal(1, await AuditoriasAsync(s.C, "contact-center.item.enter", AuditOutcome.Denied));
        Assert.Equal(1, await AuditoriasAsync(s.C, HandlingExecutor.EnterUnitKeptOperation, AuditOutcome.Success));
    }

    // Intenção anterior à base

    [Fact]
    public async Task A_entrada_pendente_gravada_sem_a_base_nao_e_executada_fica_inerte_e_a_conversa_continua_com_a_IA()
    {
        var s = await MontarAsync("entrada7");

        // Uma intenção de entrada de antes de a base existir (sem o estado sobre o qual foi autorizada).
        var item = await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var sessao = (await sp.GetRequiredService<IConversationGateway>().GetSessionAsync(s.Sessao, default))!;
            var pedido = Guid.NewGuid();
            var agora = DateTimeOffset.UtcNow;
            var novo = QueueItem.Enter(s.C.Tenant.TenantId, sessao.Id, sessao.OrganizationId, sessao.BusinessUnitId, sessao.DepartmentId, s.Fila.Id, pedido, null, "handoff", agora);
            await itens.AddItemAsync(novo, QueueOperation.Begin(s.C.Tenant.TenantId, pedido, OperationKind.Enter, novo.Id, sessao.Id, null, s.Fila.Id, null, agora), default);
            return novo.Id;
        });
        Assert.Equal(PendingKind.Enter, (await s.C.ItemAsync(item))!.Pending!.Kind);

        // A reconciliação não a executa: sem base não há autorização a conferir, e nem a cerca é registrada. Nenhum efeito foi comprovado (a consulta devolve
        // NotFound, que não presume falha nem concede autoridade): a intenção fica INERTE, com a evidência preservada, e a conversa continua com a IA.
        Assert.Equal(0, await s.C.ReconciliarAsync());
        await EstadoAsync(s.C, s.Sessao, HandlingMode.AiAutonomous, null);
        Assert.Equal(PendingKind.Enter, (await s.C.ItemAsync(item))!.Pending!.Kind);
        Assert.Equal(0, await AuditoriasAsync(s.C, "contact-center.item.enter", AuditOutcome.Denied));
        await s.C.LimparPendenciasAsync(); // a intenção inerte não termina sozinha: não deixa trabalho elegível no banco compartilhado
    }
}
