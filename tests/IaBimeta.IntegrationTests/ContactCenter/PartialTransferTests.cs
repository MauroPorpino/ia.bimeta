using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A transferência de unidade é feita em dois comandos no Conversations (a unidade e depois quem responde), e cada um é confirmado sozinho.
/// Se o segundo é recusado porque o atendimento mudou, a unidade JÁ está aplicada: o ContactCenter não a desfaz por conta própria, e não pode
/// cancelar a intenção deixando o item na fila de origem com a unidade de destino (a fila de origem leria a conversa de outra unidade). A
/// operação é concluída em parte: o item vai para a fila, a unidade e o setor de destino, e espelha o atendimento que o Conversations tem agora
/// (o responsável atual é preservado). Enquanto a intenção está pendente, o acesso fica fechado. PostgreSQL real; as intercalações são
/// determinísticas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PartialTransferTests(PostgresFixture banco)
{
    /// <summary>A fila A é da matriz e a fila B, da filial (o número autoriza as duas unidades). Cada supervisor só vê a fila dele.</summary>
    private sealed record Cena(
        CenarioDeConversas C, Guid Matriz, Guid Filial, ServiceQueueView FilaA, ServiceQueueView FilaB,
        Agente SupervisorDeA, Agente SupervisorDeB, Agente SupervisorDasDuas, Agente Ana, Agente Carla,
        Guid Sessao, QueueItemView Item);

    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        var filaA = await c.CriarFilaAsync("A", unidade: matriz);
        var filaB = await c.CriarFilaAsync("B", unidade: filial);
        var supervisorDeA = await c.AgenteNaFilaAsync(banco, filaA.Id, QueueMemberRole.Supervisor);
        var supervisorDeB = await c.AgenteNaFilaAsync(banco, filaB.Id, QueueMemberRole.Supervisor);
        var supervisorDasDuas = await c.AgenteNaFilaAsync(banco, filaA.Id, QueueMemberRole.Supervisor);
        await c.AdicionarAtendenteAsync(filaB.Id, supervisorDasDuas.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var carla = await c.AgenteNaFilaAsync(banco, filaB.Id);

        // A Ana atende na fila A, na matriz, uma conversa que já tem conteúdo.
        var sessao = await c.NovaConversaAsync(unidade: matriz, canal: canal);
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        item = (await c.AssumirAsync(item.Id, ana.Contexto)).Item;
        return new Cena(c, matriz, filial, filaA, filaB, supervisorDeA, supervisorDeB, supervisorDasDuas, ana, carla, sessao, item);
    }

    /// <summary>O atendente é o responsável DIRETO no Conversations (por fora do ContactCenter).</summary>
    private static async Task MudarPorForaAsync(Cena s, Guid? responsavel) =>
        Assert.Equal(
            TransitionOutcome.Applied,
            (await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, responsavel, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);

    /// <summary>A Ana pede a transferência para a fila B, com o Conversations em que o teste intercala outra operação.</summary>
    private static Task<QueueItemOperationResult> TransferirAsync(Cena s, Action<Interceptador<IConversationGateway>> ponto, Guid pedido) =>
        s.C.NaExecucaoAsync(s.Ana.Contexto, ponto, svc => svc.TransferAsync(s.Item.Id, s.FilaB.Id, null, pedido));

    /// <summary>O Conversations CONFIRMA a unidade e a resposta se perde: a intenção fica pendente, com a unidade já aplicada na sessão.</summary>
    private static async Task<QueueItemOperationResult> UnidadeAplicadaRespostaPerdidaAsync(Cena s, Guid pedido)
    {
        var interrompida = await TransferirAsync(s, g => g.Depois(nameof(IConversationGateway.TransferAsync), () => throw new InvalidOperationException("a resposta se perdeu")), pedido);
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        Assert.Equal(s.Filial, (await s.C.SessaoAsync(s.Sessao))!.BusinessUnitId);
        return interrompida;
    }

    /// <summary>Nenhum dos usuários lê o conteúdo da conversa: o item é inexistente para ele, a leitura é negada ou a página vem vazia.</summary>
    private static async Task SemConteudoAsync(Cena s, params Agente[] agentes)
    {
        foreach (var agente in agentes)
        {
            Page<MessageView>? pagina = null;
            try
            {
                pagina = await s.C.MensagensDoItemAsync(s.Item.Id, agente.Contexto);
            }
            catch (QueueAccessDeniedException)
            {
            }

            Assert.True(pagina is null || pagina.Items.Count == 0, "o conteúdo da conversa não pode ser lido por este usuário");

            SessionView? conversa = null;
            try
            {
                conversa = await s.C.ConversaDoItemAsync(s.Item.Id, agente.Contexto);
            }
            catch (QueueAccessDeniedException)
            {
            }

            Assert.Null(conversa);
        }
    }

    private static async Task ComConteudoAsync(Cena s, Agente agente)
    {
        var pagina = await s.C.MensagensDoItemAsync(s.Item.Id, agente.Contexto);
        Assert.NotNull(pagina);
        Assert.NotEmpty(pagina.Items);
    }

    private static async Task<int> AuditoriasAsync(Cena s, string operacao, AuditOutcome resultado) =>
        (await s.C.AuditoriaAsync(operacao, s.Item.Id.ToString())).Count(e => e.Outcome == resultado);

    /// <summary>O estado em que a transferência concluída em parte deixa tudo: fila, unidade e setor coerentes, o responsável de agora preservado.</summary>
    /// <param name="responsavelDaFilaDeDestino">O responsável de agora é um atendente da fila de destino (a Carla) e, por isso, lê a conversa por lá.</param>
    /// <param name="geracaoConhecida">A autorização trouxe a geração da colocação: o item prende a geração de destino e a fila de destino lê. Sem ela (intenção legada), a geração fica desconhecida e NINGUÉM lê: não se inventa continuidade.</param>
    private static async Task AfirmarConcluidaEmParteAsync(Cena s, Guid responsavel, bool responsavelDaFilaDeDestino = true, bool geracaoConhecida = true)
    {
        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaB.Id, s.Filial, QueueItemStatus.Assigned, responsavel, null), (item.QueueId, item.BusinessUnitId, item.Status, item.AssignedUserId, item.Pending));

        // O Conversations ficou como estava: a unidade que ele confirmou e o responsável que não foi desfeito.
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((s.Filial, HandlingMode.HumanAssisted, (Guid?)responsavel), (sessao.BusinessUnitId, sessao.HandlingMode, sessao.ResponderRef));

        // A fila de origem perdeu o acesso: ninguém dela vê o item nem lê a conversa, que já é de outra unidade.
        Assert.Null(await s.C.ItemAsync(s.Item.Id, s.SupervisorDeA.Contexto));
        Assert.Null(await s.C.ItemAsync(s.Item.Id, s.Ana.Contexto));
        await SemConteudoAsync(s, s.SupervisorDeA, s.Ana);

        // A fila de destino enxerga e lê (se a geração da colocação é conhecida): o supervisor dela e o responsável de agora (que o Conversations preservou).
        if (!geracaoConhecida)
        {
            await SemConteudoAsync(s, s.SupervisorDeB, s.SupervisorDasDuas);
            return;
        }

        await ComConteudoAsync(s, s.SupervisorDeB);
        await ComConteudoAsync(s, s.SupervisorDasDuas);
        if (responsavelDaFilaDeDestino)
        {
            await ComConteudoAsync(s, s.Carla);
        }
    }

    // A intercalação completa

    [Fact]
    public async Task A_transferencia_confirmada_em_parte_e_concluida_na_fila_de_destino_e_preserva_o_responsavel_que_surgiu_depois()
    {
        var s = await MontarAsync("parcial1");
        var pedido = Guid.NewGuid();
        var intercalou = false;

        // 1) A Ana, responsável, transfere para a fila B: o Conversations confirma a unidade da filial. 2) ANTES da transição de quem
        // responde, outra operação autorizada atribui a conversa à Carla. 3) A transição, autorizada sobre a Ana, é recusada.
        var resultado = await TransferirAsync(s, g => g.Depois(nameof(IConversationGateway.TransferAsync), async () =>
        {
            await MudarPorForaAsync(s, s.Carla.Id);
            intercalou = true;
        }), pedido);

        Assert.True(intercalou);

        // O caminho antigo cancelava a intenção e alinhava só a unidade, deixando o item na fila A com a unidade da filial.
        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        await AfirmarConcluidaEmParteAsync(s, s.Carla.Id);

        // O histórico diz o que aconteceu: a transferência foi concluída em parte, e o responsável passou à Carla por fora.
        var historico = (await s.C.HistoricoDoItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!;
        var transferencia = Assert.Single(historico, h => h.Kind == HistoryKind.TransferredQueue);
        Assert.Equal((s.FilaA.Id, s.FilaB.Id, s.Ana.Id, QueueItem.ResponderChangedSuffix), (transferencia.FromQueueId, transferencia.ToQueueId, transferencia.ActorUserId, transferencia.Reason[^QueueItem.ResponderChangedSuffix.Length..]));
        var espelho = Assert.Single(historico, h => h.Reason == QueueItem.ExternalChangeReason);
        Assert.Equal((HistoryKind.Reassigned, s.Ana.Id, s.Carla.Id), (espelho.Kind, espelho.FromUserId, espelho.ToUserId));

        // A trilha tem o que foi confirmado e o que não foi.
        Assert.Equal(1, await AuditoriasAsync(s, "contact-center.item.transfer", AuditOutcome.Success));
        Assert.Equal(1, await AuditoriasAsync(s, HandlingExecutor.ResponderChangedOperation, AuditOutcome.Denied));

        // O recibo reflete o efeito confirmado: repetir o mesmo pedido devolve o mesmo resultado, sem tocar de novo no Conversations.
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);
        var repetida = await s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaB.Id, null, s.Ana.Contexto, pedido);
        Assert.Equal(s.FilaB.Id, repetida.Item.QueueId);
        Assert.Equal(versao, await s.C.VersaoDaSessaoAsync(s.Sessao));

        // A Carla, a responsável de agora, opera a própria conversa (o item a espelha); a Ana já não nem enxerga o item (ele saiu da fila dela).
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => s.C.DevolverAFilaAsync(s.Item.Id, s.Ana.Contexto));
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAFilaAsync(s.Item.Id, s.Carla.Contexto)).Outcome);
    }

    // Perda de resposta, interrupção e reconciliação

    [Fact]
    public async Task Com_a_resposta_da_unidade_perdida_o_acesso_fica_fechado_e_a_reconciliacao_conclui_em_parte_uma_vez_so()
    {
        var s = await MontarAsync("parcial2");
        var pedido = Guid.NewGuid();
        var interrompida = await UnidadeAplicadaRespostaPerdidaAsync(s, pedido);

        // Estado intermediário: a intenção está pendente, o item ainda na fila A e na unidade da matriz, e a sessão já na filial. Ninguém lê a
        // conversa: a fila de origem não a lê (ela já saiu da unidade), a de destino ainda não é dela, e nem quem vê as duas.
        var pendente = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaA.Id, s.Matriz, PendingKind.TransferQueue), (pendente.QueueId, pendente.BusinessUnitId, pendente.Pending!.Kind));
        Assert.Equal(interrompida.Item.Pending!.OperationId, pendente.Pending.OperationId);
        await SemConteudoAsync(s, s.SupervisorDeA, s.SupervisorDeB, s.SupervisorDasDuas, s.Ana);

        // Antes de a reconciliação chegar, a Carla é a responsável (por fora).
        await MudarPorForaAsync(s, s.Carla.Id);

        await s.C.LiberarRecuoAsync(s.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());
        await AfirmarConcluidaEmParteAsync(s, s.Carla.Id);

        // Idempotente: outra rodada não encontra nada, e nada muda (nem o item, nem o Conversations, nem a trilha).
        var item = (await s.C.ItemAsync(s.Item.Id))!;
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);
        Assert.Equal(0, await s.C.ReconciliarAsync());
        Assert.Equal(item, await s.C.ItemAsync(s.Item.Id));
        Assert.Equal(versao, await s.C.VersaoDaSessaoAsync(s.Sessao));
        Assert.Equal(1, await AuditoriasAsync(s, HandlingExecutor.ResponderChangedOperation, AuditOutcome.Denied));
        Assert.Equal(1, (await s.C.HistoricoDoItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!.Count(h => h.Kind == HistoryKind.TransferredQueue));

        // Repetir o pedido original da Ana devolve o resultado gravado.
        Assert.Equal(s.FilaB.Id, (await s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaB.Id, null, s.Ana.Contexto, pedido)).Item.QueueId);
    }

    [Fact]
    public async Task Com_a_unidade_aplicada_e_o_responsavel_inalterado_a_reconciliacao_conclui_a_transferencia_normalmente()
    {
        var s = await MontarAsync("parcial3");
        await UnidadeAplicadaRespostaPerdidaAsync(s, Guid.NewGuid());

        // Nada mudou no atendimento: a condição vale, a transição de quem responde é aplicada e a transferência é concluída por inteiro.
        await s.C.LiberarRecuoAsync(s.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());

        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaB.Id, s.Filial, QueueItemStatus.Waiting, null), (item.QueueId, item.BusinessUnitId, item.Status, item.Pending));
        Assert.Equal((HandlingMode.HumanAssisted, (Guid?)null), ((await s.C.SessaoAsync(s.Sessao))!.HandlingMode, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef));
        Assert.Equal(0, await AuditoriasAsync(s, HandlingExecutor.ResponderChangedOperation, AuditOutcome.Denied));
        Assert.DoesNotContain((await s.C.HistoricoDoItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!, h => h.Reason.EndsWith(QueueItem.ResponderChangedSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Com_a_unidade_aplicada_e_a_conversa_devolvida_a_IA_o_item_e_encerrado_ja_na_fila_de_destino()
    {
        var s = await MontarAsync("parcial4");
        await UnidadeAplicadaRespostaPerdidaAsync(s, Guid.NewGuid());

        // A conversa volta para a IA por fora: o atendimento que o item representava acabou.
        Assert.Equal(
            TransitionOutcome.Applied,
            (await s.C.TransicionarAsync(s.Sessao, HandlingMode.AiAutonomous, null, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);

        await s.C.LiberarRecuoAsync(s.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());

        // O item fecha na fila onde a conversa estava (a de destino), com a fronteira do histórico desconhecida: nada do conteúdo se abre.
        var item = (await s.C.ItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!;
        Assert.Equal((s.FilaB.Id, s.Filial, QueueItemStatus.Closed, null), (item.QueueId, item.BusinessUnitId, item.Status, item.Pending));
        Assert.Equal(HandlingMode.AiAutonomous, (await s.C.SessaoAsync(s.Sessao))!.HandlingMode);
        Assert.Null(await s.C.ItemAsync(s.Item.Id, s.SupervisorDeA.Contexto));
        await SemConteudoAsync(s, s.SupervisorDeA, s.SupervisorDeB, s.SupervisorDasDuas, s.Ana, s.Carla);
    }

    [Fact]
    public async Task Com_a_unidade_aplicada_e_a_sessao_encerrada_o_item_e_encerrado_na_fila_de_destino_e_o_historico_e_da_fila_de_destino()
    {
        var s = await MontarAsync("parcial5");
        await UnidadeAplicadaRespostaPerdidaAsync(s, Guid.NewGuid());
        await s.C.EncerrarSessaoAsync(s.Sessao);

        await s.C.LiberarRecuoAsync(s.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());

        var item = (await s.C.ItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!;
        Assert.Equal((s.FilaB.Id, s.Filial, QueueItemStatus.Closed, null), (item.QueueId, item.BusinessUnitId, item.Status, item.Pending));
        var historico = (await s.C.HistoricoDoItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!;
        Assert.Contains(historico, h => h.Kind == HistoryKind.TransferredQueue && h.Reason.EndsWith(QueueItem.SessionEndedSuffix, StringComparison.Ordinal));
        Assert.Contains(historico, h => h.Kind == HistoryKind.SessionClosed);

        // O que a conversa viveu na filial até o fim é da fila de destino: a de origem não o lê.
        Assert.Null(await s.C.ItemAsync(s.Item.Id, s.SupervisorDeA.Contexto));
        await SemConteudoAsync(s, s.SupervisorDeA, s.Ana);
        await ComConteudoAsync(s, s.SupervisorDeB);

        // O efeito confirmado no Conversations (a transferência) está na trilha, com a identidade, a origem e o instante do PEDIDO (a Ana), e é uma
        // entrada SEPARADA do encerramento da sessão, que é do contexto de quem o projeta (o job), sem ator.
        var transferencia = Assert.Single(await AuditoriasDoItemAsync(s, "contact-center.item.transfer"));
        Assert.Equal((AuditOutcome.Success, s.Ana.Id, s.Ana.Contexto.Origin), (transferencia.Outcome, transferencia.ActorUserId, transferencia.ActorOrigin));
        var encerramento = Assert.Single(await AuditoriasDoItemAsync(s, "contact-center.item.session-closed"));
        Assert.Null(encerramento.ActorUserId);
        Assert.NotEqual(transferencia.Id, encerramento.Id);
        Assert.True(transferencia.OccurredAt <= encerramento.OccurredAt);

        // Reconciliar de novo, ou conferir o item, não duplica nada: nem o histórico, nem a trilha.
        Assert.Equal(0, await s.C.ReconciliarAsync());
        Assert.Single(await AuditoriasDoItemAsync(s, "contact-center.item.transfer"));
        Assert.Single(await AuditoriasDoItemAsync(s, "contact-center.item.session-closed"));
        Assert.Equal(1, (await s.C.HistoricoDoItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!.Count(h => h.Kind == HistoryKind.TransferredQueue));
    }

    private static async Task<IReadOnlyList<AuditEventView>> AuditoriasDoItemAsync(Cena s, string operacao) =>
        (await s.C.AuditoriaAsync(operacao, s.Item.Id.ToString())).Where(e => e.Outcome == AuditOutcome.Success).ToList();

    // T6: o período do item desconhecido não é correspondência na conclusão parcial

    [Fact]
    public async Task T6_a_conclusao_parcial_com_o_periodo_do_item_desconhecido_acompanha_a_unidade_mas_nao_espelha_o_responsavel_nem_encerra_o_item()
    {
        var s = await MontarAsync("parcial9");
        await UnidadeAplicadaRespostaPerdidaAsync(s, Guid.NewGuid());

        // O item como os anteriores ao campo (a geração da colocação é conhecida; o período do atendimento, não). Depois de a unidade já aplicada, o responsável
        // passa à Carla por fora, e a transição da Ana é recusada.
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>().Database
            .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET handling_period = NULL WHERE id = {s.Item.Id}"));
        await MudarPorForaAsync(s, s.Carla.Id);
        await s.C.LiberarRecuoAsync(s.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());

        // A parte confirmada (a unidade) é acompanhada, mas SEM a prova de que o atendimento da sessão é o do item nada é espelhado (a Carla) nem encerrado: a
        // identidade desconhecida nunca é "o mesmo atendimento".
        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaB.Id, s.Filial, QueueItemStatus.Assigned, s.Ana.Id, null), (item.QueueId, item.BusinessUnitId, item.Status, item.AssignedUserId, item.Pending));
        Assert.DoesNotContain((await s.C.HistoricoDoItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!, h => h.Reason == QueueItem.ExternalChangeReason);
    }

    // Operações pendentes legadas (sem a base da autorização)

    /// <summary>Uma intenção de transferência gravada antes de a base existir, e o Conversations com a unidade já aplicada (ou não).</summary>
    private static async Task GravarTransferenciaSemBaseAsync(Cena s)
    {
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            (await itens.FindItemAsync(s.Item.Id, default))!.BeginTransferToQueue(s.FilaB.Id, null, s.Filial, null, Guid.NewGuid(), null, "transfer", DateTimeOffset.UtcNow);
            await itens.SaveChangesAsync(default);
            return 0;
        });
    }

    /// <summary>Aplica no Conversations o encaminhamento com o <c>TransferId</c> que a intenção do item guardou (a evidência correlacionada da operação), como a requisição que caiu depois de escrever.</summary>
    private static async Task AplicarEncaminhamentoDaIntencaoAsync(Cena s)
    {
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var pendente = (await sp.GetRequiredService<IQueueItemRepository>().FindItemReadOnlyAsync(s.Item.Id, default))!.Pending!;
            var gateway = sp.GetRequiredService<IConversationGateway>();
            var versao = (await gateway.GetSessionAsync(s.Sessao, default))!.StateVersion;
            var resultado = await gateway.TransferAsync(new TransferSessionCommand(s.Sessao, s.Filial, null, versao, pendente.TransferId, "queue-transfer"), default);
            Assert.Equal(TransferOutcome.Applied, resultado.Outcome);
            return 0;
        });
    }

    [Fact]
    public async Task A_intencao_legada_sem_base_com_a_unidade_comprovada_pelo_TransferId_e_concluida_em_parte_sem_escrever_no_Conversations()
    {
        var s = await MontarAsync("parcial6");
        await GravarTransferenciaSemBaseAsync(s);
        await AplicarEncaminhamentoDaIntencaoAsync(s);
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);

        // Sem a base não há autorização a conferir, e nenhum comando é enviado; mas o efeito JÁ aplicado é reconhecido por EVIDÊNCIA (a consulta de efeito, pelo
        // TransferId da intenção): o item acompanha a conversa.
        Assert.Equal(1, await s.C.ReconciliarAsync());

        // A Ana continua a responsável no Conversations (a transição não foi enviada) e o item a espelha, mas a fila de destino não é dela: quem
        // lê e resolve agora é a fila de destino. A geração da colocação é desconhecida (a base não a trouxe): a leitura continua fechada.
        await AfirmarConcluidaEmParteAsync(s, s.Ana.Id, responsavelDaFilaDeDestino: false, geracaoConhecida: false);
        Assert.Equal(versao, await s.C.VersaoDaSessaoAsync(s.Sessao)); // nada foi escrito no Conversations
    }

    [Fact]
    public async Task A_intencao_legada_sem_base_com_a_sessao_na_unidade_por_OUTRO_encaminhamento_nao_presume_o_efeito_e_fica_inerte()
    {
        var s = await MontarAsync("parcial6b");
        await GravarTransferenciaSemBaseAsync(s);
        Assert.Equal(
            TransferOutcome.Applied,
            (await s.C.EncaminharAsync(s.Sessao, s.Filial, null, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome); // outro TransferId: não é o desta intenção
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);

        // A sessão está na unidade pedida, mas igualdade de estado NÃO é prova de que foi esta operação: nada é concluído nem escrito, e a intenção segue pendente.
        Assert.Equal(0, await s.C.ReconciliarAsync());

        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaA.Id, s.Matriz, PendingKind.TransferQueue), (item.QueueId, item.BusinessUnitId, item.Pending!.Kind));
        Assert.Equal(versao, await s.C.VersaoDaSessaoAsync(s.Sessao));
        Assert.Equal(0, await AuditoriasAsync(s, "contact-center.item.transfer", AuditOutcome.Success));
        await s.C.LimparPendenciasAsync(); // a intenção inerte não termina sozinha: não deixa trabalho elegível no banco compartilhado
    }

    [Fact]
    public async Task A_intencao_legada_sem_base_e_sem_efeito_comprovado_fica_inerte_sem_escrever_e_sem_ser_desfeita()
    {
        var s = await MontarAsync("parcial7");
        await GravarTransferenciaSemBaseAsync(s);
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);

        Assert.Equal(0, await s.C.ReconciliarAsync());

        // NotFound não presume falha definitiva nem concede autoridade nova: a intenção permanece (com a evidência), o item não muda de fila e nada é escrito.
        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaA.Id, s.Matriz, QueueItemStatus.Assigned, s.Ana.Id, PendingKind.TransferQueue), (item.QueueId, item.BusinessUnitId, item.Status, item.AssignedUserId, item.Pending!.Kind));
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((s.Matriz, s.Ana.Id, versao), (sessao.BusinessUnitId, sessao.ResponderRef, sessao.StateVersion));
        Assert.Equal(0, await AuditoriasAsync(s, "contact-center.item.transfer", AuditOutcome.Denied));
        await s.C.LimparPendenciasAsync();
    }

    // Defesa em profundidade: o que o item guarda fora da cobertura da fila dele fecha o acesso

    [Fact]
    public async Task Um_item_cuja_unidade_esta_fora_da_cobertura_da_fila_dele_nao_deixa_ler_a_conversa()
    {
        var s = await MontarAsync("parcial8");
        await ComConteudoAsync(s, s.SupervisorDeA);

        // Um registro incoerente (por qualquer causa): a conversa e o item dizem "filial", mas a fila do item é a da matriz.
        Assert.Equal(
            TransferOutcome.Applied,
            (await s.C.EncaminharAsync(s.Sessao, s.Filial, null, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>().Database
                .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET business_unit_id = {s.Filial} WHERE id = {s.Item.Id}");
            return 0;
        });

        // Quem lê por uma fila que não atende a unidade do item não lê a conversa, nem o responsável.
        await SemConteudoAsync(s, s.SupervisorDeA, s.Ana);
    }
}
