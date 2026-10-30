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
/// A leitura do conteúdo da conversa respeita a AUTORIDADE EFETIVA sobre responsável e fila, não só a projeção local do item: o responsável
/// é o do Conversations (BOUNDARIES.md §6), e a fila de uma transferência pendente é ambígua (o Conversations não conhece filas), então vale
/// o acesso nas duas. O que o Conversations já confirmou e o ContactCenter ainda não concluiu (a resposta se perdeu, o processo caiu) vale
/// desde a confirmação. O período de atendimento (<see cref="HandlingPeriodAccessTests"/>) continua valendo junto. PostgreSQL real; os
/// pontos de interrupção são determinísticos.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EffectiveAuthorityReadTests(PostgresFixture banco)
{
    /// <summary>Duas filas da MESMA organização, unidade e setor (A e B), com supervisor e atendentes de cada uma.</summary>
    private sealed record Cena(
        CenarioDeConversas C, ServiceQueueView FilaA, ServiceQueueView FilaB, Agente SupervisorA, Agente SupervisorB, Agente Ana, Agente Carla, Agente Beto,
        string Contato, Guid Sessao, QueueItemView Item);

    /// <summary>
    /// O item na fila A. A Ana e a Carla são da fila A, o Beto é da fila B. Com <paramref name="assumida"/>, a Ana é a responsável; senão a conversa
    /// espera na fila A, sem responsável.
    /// </summary>
    private async Task<Cena> MontarAsync(string prefixo, bool assumida = true)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var filaA = await c.CriarFilaAsync("A");
        var filaB = await c.CriarFilaAsync("B");
        var supervisorA = await c.AgenteNaFilaAsync(banco, filaA.Id, QueueMemberRole.Supervisor);
        var supervisorB = await c.AgenteNaFilaAsync(banco, filaB.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var carla = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var beto = await c.AgenteNaFilaAsync(banco, filaB.Id);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da transferencia")).SessionId;
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        if (assumida)
        {
            await c.AssumirAsync(item.Id, ana.Contexto);
        }

        return new Cena(c, filaA, filaB, supervisorA, supervisorB, ana, carla, beto, contato, sessao, item);
    }

    /// <summary>
    /// Roda a operação com uma porta do Conversations que PERDE a resposta depois de ele confirmar a transição: o efeito existe no
    /// Conversations, e o ContactCenter fica com a intenção pendente.
    /// </summary>
    private static Task<QueueItemOperationResult> ComRespostaPerdidaAsync(
        CenarioDeConversas c, RequestContext contexto, Func<QueueItemService, Task<QueueItemOperationResult>> operacao) =>
        ComFalhaAsync(c, contexto, g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")), operacao);

    private static Task<QueueItemOperationResult> ComFalhaAsync(
        CenarioDeConversas c, RequestContext contexto, Action<Interceptador<IConversationGateway>> falha, Func<QueueItemService, Task<QueueItemOperationResult>> operacao) =>
        c.ComoAsync(contexto, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            falha(controle);
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await operacao(ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor));
        });

    /// <summary>Uma leitura do item cujo Conversations intercala outra operação no ponto indicado.</summary>
    private static Task<T> LerIntercaladoAsync<T>(
        Cena s, RequestContext leitor, Action<Interceptador<IConversationGateway>> ponto, Func<QueueItemService, Task<T>> leitura) =>
        s.C.ComoAsync(leitor, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            ponto(controle);
            return await leitura(ActivatorUtilities.CreateInstance<QueueItemService>(sp, gateway));
        });

    /// <summary>O job de reconciliação conclui a intenção que ficou pendente (o recuo da tentativa que falhou é liberado).</summary>
    private static async Task ReconciliarAsync(Cena s)
    {
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>().Database
                .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET pending_next_attempt_at = NULL WHERE id = {s.Item.Id}");
            return 0;
        });
        Assert.Equal(1, await s.C.ReconciliarAsync());
        Assert.Null((await s.C.ItemAsync(s.Item.Id))!.Pending);
    }

    /// <summary>Lê o histórico inteiro E a conversa do item: o que a pessoa lê pela fila.</summary>
    private static async Task LeAsync(Cena s, RequestContext quem, params string[] mensagens)
    {
        var pagina = await s.C.MensagensDoItemAsync(s.Item.Id, quem);
        Assert.NotNull(pagina);
        Assert.Equal(mensagens, pagina.Items.Select(m => m.Text));
        Assert.Equal(s.Sessao, (await s.C.ConversaDoItemAsync(s.Item.Id, quem))?.Id);
    }

    /// <summary>A fila do item (ou uma das duas, na transferência pendente) não é visível para a pessoa: o item responde como inexistente.</summary>
    private static async Task NaoEnxergaAsync(Cena s, RequestContext quem)
    {
        Assert.Null(await s.C.MensagensDoItemAsync(s.Item.Id, quem));
        Assert.Null(await s.C.ConversaDoItemAsync(s.Item.Id, quem));
    }

    /// <summary>A pessoa enxerga a fila e não pode ler o conteúdo: negado (e auditado), nas duas leituras.</summary>
    private static async Task NegadoAsync(Cena s, RequestContext quem)
    {
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.MensagensDoItemAsync(s.Item.Id, quem));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.ConversaDoItemAsync(s.Item.Id, quem));
    }

    [Fact]
    public async Task Transferencia_entre_filas_confirmada_no_Conversations_com_a_resposta_perdida_nao_deixa_o_supervisor_da_fila_de_origem_ler_as_mensagens_novas()
    {
        var s = await MontarAsync("efetransf");
        await LeAsync(s, s.SupervisorA.Contexto, "antes da transferencia");

        // O supervisor da fila A transfere a conversa para a B, com o Beto. O Conversations CONFIRMA e a resposta se perde antes de o
        // ContactCenter concluir: o responsável já é o Beto lá, e o item continua na fila A, com a Ana, e uma intenção pendente.
        var interrompida = await ComRespostaPerdidaAsync(s.C, s.SupervisorA.Contexto, svc => svc.TransferAsync(s.Item.Id, s.FilaB.Id, s.Beto.Id, Guid.NewGuid()));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        Assert.Equal(s.Beto.Id, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef);
        var local = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaA.Id, s.Ana.Id, PendingKind.TransferQueue), (local.QueueId, local.AssignedUserId, local.Pending!.Kind));

        await s.C.EntrarAsync(s.Contato, texto: "depois da transferencia");

        // Autorização local aceitaria o supervisor da fila A (período, modo, unidade e setor coincidem). A fila da transferência pendente é
        // ambígua, então ninguém lê por uma fila só: nem quem lia pela de origem, nem quem só chega pela de destino.
        await NaoEnxergaAsync(s, s.SupervisorA.Contexto);
        await NaoEnxergaAsync(s, s.Ana.Contexto);
        await NaoEnxergaAsync(s, s.Carla.Contexto);
        await NaoEnxergaAsync(s, s.Beto.Contexto);
        await NaoEnxergaAsync(s, s.SupervisorB.Contexto);

        // Reconciliada, a conversa é da fila B: o novo responsável e o supervisor de B leem tudo; ninguém lê mais pela fila A.
        await ReconciliarAsync(s);
        var concluido = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaB.Id, s.Beto.Id), (concluido.QueueId, concluido.AssignedUserId));
        await LeAsync(s, s.Beto.Contexto, "antes da transferencia", "depois da transferencia");
        await LeAsync(s, s.SupervisorB.Contexto, "antes da transferencia", "depois da transferencia");
        await NaoEnxergaAsync(s, s.SupervisorA.Contexto);
        await NaoEnxergaAsync(s, s.Ana.Contexto);
        await NaoEnxergaAsync(s, s.Carla.Contexto);
    }

    [Fact]
    public async Task Reatribuicao_confirmada_no_Conversations_antes_da_conclusao_local_tira_o_acesso_do_responsavel_anterior()
    {
        var s = await MontarAsync("efereatrib");
        await LeAsync(s, s.Ana.Contexto, "antes da transferencia");

        // Dentro da mesma fila: o supervisor passa a conversa da Ana para a Carla; o Conversations confirma e a resposta se perde.
        var interrompida = await ComRespostaPerdidaAsync(s.C, s.SupervisorA.Contexto, svc => svc.AssignAsync(s.Item.Id, s.Carla.Id, Guid.NewGuid()));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        Assert.Equal(s.Carla.Id, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef);
        Assert.Equal(s.Ana.Id, (await s.C.ItemAsync(s.Item.Id))!.AssignedUserId); // a projeção local ainda diz Ana

        await s.C.EntrarAsync(s.Contato, texto: "depois da reatribuicao");

        // A Ana deixou de ser a responsável desde a confirmação: sem acesso, e a negação fica na trilha. A nova responsável e o supervisor leem.
        await NegadoAsync(s, s.Ana.Contexto);
        await LeAsync(s, s.Carla.Contexto, "antes da transferencia", "depois da reatribuicao");
        await LeAsync(s, s.SupervisorA.Contexto, "antes da transferencia", "depois da reatribuicao");
        await NaoEnxergaAsync(s, s.Beto.Contexto);
        var negadas = (await s.C.AuditoriaAsync("contact-center.item.read-conversation", s.Item.Id.ToString())).Where(e => e.ActorUserId == s.Ana.Id).ToList();
        Assert.Equal(2, negadas.Count);
        Assert.All(negadas, e => Assert.Equal(AuditOutcome.Denied, e.Outcome));

        // Depois da reconciliação, o mesmo.
        await ReconciliarAsync(s);
        Assert.Equal(s.Carla.Id, (await s.C.ItemAsync(s.Item.Id))!.AssignedUserId);
        await NegadoAsync(s, s.Ana.Contexto);
        await LeAsync(s, s.Carla.Contexto, "antes da transferencia", "depois da reatribuicao");
        await LeAsync(s, s.SupervisorA.Contexto, "antes da transferencia", "depois da reatribuicao");
    }

    [Fact]
    public async Task Atribuicao_confirmada_no_Conversations_com_o_item_local_ainda_em_espera_so_deixa_o_novo_responsavel_e_o_supervisor_lerem()
    {
        var s = await MontarAsync("efeespera", assumida: false);
        await LeAsync(s, s.Carla.Contexto, "antes da transferencia"); // esperando, qualquer atendente da fila lê

        var interrompida = await ComRespostaPerdidaAsync(s.C, s.SupervisorA.Contexto, svc => svc.AssignAsync(s.Item.Id, s.Ana.Id, Guid.NewGuid()));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        Assert.Equal(s.Ana.Id, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef);
        Assert.Equal(QueueItemStatus.Waiting, (await s.C.ItemAsync(s.Item.Id))!.Status); // a projeção local ainda diz "em espera"

        await s.C.EntrarAsync(s.Contato, texto: "depois da atribuicao");

        // A conversa já tem dono: quem só poderia lê-la por estar "esperando" não lê.
        await NegadoAsync(s, s.Carla.Contexto);
        await LeAsync(s, s.Ana.Contexto, "antes da transferencia", "depois da atribuicao");
        await LeAsync(s, s.SupervisorA.Contexto, "antes da transferencia", "depois da atribuicao");

        await ReconciliarAsync(s);
        var concluido = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, s.Ana.Id), (concluido.Status, concluido.AssignedUserId));
        await NegadoAsync(s, s.Carla.Contexto);
        await LeAsync(s, s.Ana.Contexto, "antes da transferencia", "depois da atribuicao");
    }

    [Fact]
    public async Task A_mudanca_de_responsavel_feita_direto_no_Conversations_tira_o_acesso_do_anterior_antes_de_qualquer_projecao()
    {
        var s = await MontarAsync("efedireta");

        // Sem intenção pendente no ContactCenter: o responsável muda por fora (mesmo período), e o item ainda diz Ana.
        Assert.Equal(TransitionOutcome.Applied, (await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, s.Carla.Id, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
        await s.C.EntrarAsync(s.Contato, texto: "depois da mudanca");
        Assert.Equal(s.Ana.Id, (await s.C.ItemAsync(s.Item.Id))!.AssignedUserId);

        await NegadoAsync(s, s.Ana.Contexto);
        await LeAsync(s, s.Carla.Contexto, "antes da transferencia", "depois da mudanca");
        await LeAsync(s, s.SupervisorA.Contexto, "antes da transferencia", "depois da mudanca"); // a fila não mudou: o supervisor continua
    }

    [Fact]
    public async Task Transferencia_pendente_de_item_em_espera_exige_acesso_nas_duas_filas_e_a_conclusao_deixa_so_a_de_destino()
    {
        var s = await MontarAsync("efeambigua", assumida: false);
        var gestor = s.C.GestorDeFilas();

        // Nada foi aplicado no Conversations (a conversa espera, sem responsável, antes e depois): não há evidência de que a transferência
        // já valeu. A fila é ambígua enquanto ela está pendente.
        var interrompida = await ComFalhaAsync(
            s.C, s.SupervisorA.Contexto, g => g.Substituir(nameof(IConversationGateway.FenceAsync), _ => Task.FromException<FenceHandlingResult>(new InvalidOperationException("fora do ar"))),
            svc => svc.TransferAsync(s.Item.Id, s.FilaB.Id, null, Guid.NewGuid()));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        Assert.Equal(s.FilaA.Id, (await s.C.ItemAsync(s.Item.Id))!.QueueId);

        await s.C.EntrarAsync(s.Contato, texto: "durante a transferencia");

        await NaoEnxergaAsync(s, s.Carla.Contexto); // só da fila de origem
        await NaoEnxergaAsync(s, s.SupervisorA.Contexto);
        await NaoEnxergaAsync(s, s.Beto.Contexto); // só da fila de destino
        await LeAsync(s, gestor, "antes da transferencia", "durante a transferencia"); // quem gere as duas filas

        await ReconciliarAsync(s);
        var concluido = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaB.Id, QueueItemStatus.Waiting), (concluido.QueueId, concluido.Status));
        await NaoEnxergaAsync(s, s.Carla.Contexto);
        await LeAsync(s, s.Beto.Contexto, "antes da transferencia", "durante a transferencia"); // em espera na fila B
        await LeAsync(s, s.SupervisorB.Contexto, "antes da transferencia", "durante a transferencia");
        await LeAsync(s, gestor, "antes da transferencia", "durante a transferencia");
    }

    [Fact]
    public async Task Transferencia_concluida_sem_falha_deixa_ler_so_o_novo_responsavel_e_a_supervisao_da_fila_de_destino()
    {
        var s = await MontarAsync("efenormal");

        var feita = await s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaB.Id, s.Beto.Id, s.SupervisorA.Contexto);
        Assert.Equal(OperationOutcome.Completed, feita.Outcome);
        await s.C.EntrarAsync(s.Contato, texto: "depois da transferencia");

        await LeAsync(s, s.Beto.Contexto, "antes da transferencia", "depois da transferencia");
        await LeAsync(s, s.SupervisorB.Contexto, "antes da transferencia", "depois da transferencia");
        await NaoEnxergaAsync(s, s.SupervisorA.Contexto);
        await NaoEnxergaAsync(s, s.Ana.Contexto);
    }

    [Fact]
    public async Task Transferencia_com_resposta_perdida_durante_a_leitura_do_historico_fecha_a_pagina_de_quem_lia_pela_fila_de_origem()
    {
        var s = await MontarAsync("efeconchist");
        var intercalou = false;

        // O supervisor da fila A já foi autorizado e a sessão já foi vista; ANTES de o histórico ser lido, a conversa é transferida para a
        // fila B (o Conversations confirma e a resposta se perde: o item só chega à intenção pendente) e chega uma mensagem.
        var pagina = await LerIntercaladoAsync(
            s, s.SupervisorA.Contexto,
            g => g.Antes(nameof(IConversationGateway.GetHistoryAsync), async () =>
            {
                Assert.Equal(
                    OperationOutcome.Pending,
                    (await ComRespostaPerdidaAsync(s.C, s.SupervisorA.Contexto, svc => svc.TransferAsync(s.Item.Id, s.FilaB.Id, s.Beto.Id, Guid.NewGuid()))).Outcome);
                await s.C.EntrarAsync(s.Contato, texto: "ja na fila B");
                intercalou = true;
            }),
            svc => svc.GetMessagesAsync(s.Item.Id, 50, null, HistoryOrder.OldestFirst));

        // O que decide o acesso mudou durante a consulta: o que foi lido não é devolvido.
        Assert.True(intercalou);
        Assert.NotNull(pagina);
        Assert.Empty(pagina.Items);
    }

    [Fact]
    public async Task Reatribuicao_com_resposta_perdida_durante_a_leitura_do_historico_fecha_a_pagina_do_responsavel_anterior()
    {
        var s = await MontarAsync("efeconcresp");
        var intercalou = false;

        var pagina = await LerIntercaladoAsync(
            s, s.Ana.Contexto,
            g => g.Antes(nameof(IConversationGateway.GetHistoryAsync), async () =>
            {
                Assert.Equal(
                    OperationOutcome.Pending,
                    (await ComRespostaPerdidaAsync(s.C, s.SupervisorA.Contexto, svc => svc.AssignAsync(s.Item.Id, s.Carla.Id, Guid.NewGuid()))).Outcome);
                intercalou = true;
            }),
            svc => svc.GetMessagesAsync(s.Item.Id, 50, null, HistoryOrder.OldestFirst));

        Assert.True(intercalou);
        Assert.NotNull(pagina);
        Assert.Empty(pagina.Items);
    }

    [Fact]
    public async Task Reatribuicao_concluida_durante_a_consulta_da_conversa_nao_devolve_a_conversa_ao_responsavel_anterior()
    {
        var s = await MontarAsync("efeconcsessao");
        var intercalou = false;

        // A Ana foi autorizada e a sessão já foi vista (com ela como responsável); logo depois, o supervisor passa a conversa para a Carla.
        var sessao = await LerIntercaladoAsync(
            s, s.Ana.Contexto,
            g => g.Depois(nameof(IConversationGateway.GetSessionAsync), async () =>
            {
                Assert.Equal(OperationOutcome.Completed, (await s.C.AtribuirAsync(s.Item.Id, s.Carla.Id, s.SupervisorA.Contexto)).Outcome);
                intercalou = true;
            }),
            svc => svc.GetConversationAsync(s.Item.Id));

        Assert.True(intercalou);
        Assert.Null(sessao);
        await NegadoAsync(s, s.Ana.Contexto);
        await LeAsync(s, s.Carla.Contexto, "antes da transferencia");
    }
}
