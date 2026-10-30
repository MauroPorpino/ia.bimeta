using System.Collections.Concurrent;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A CONTINUIDADE da colocação da sessão. Unidade, setor e período de atendimento iguais não provam que a sessão ficou o tempo todo onde estava: A → B → A
/// devolve os três ao que eram, e a mensagem que chegou na unidade B no meio não é da fila A. O que prova é a identidade DURÁVEL da colocação, uma geração
/// monotônica que o Conversations avança, na mesma transação, a cada mudança de unidade ou setor, e que o item prende quando a entrada (ou a transferência do
/// próprio ContactCenter) é autorizada. Nada disto depende de o evento do encaminhamento ter chegado ao ContactCenter. Identidade desconhecida (período ou geração
/// de itens anteriores ao campo) fecha, nunca autoriza. PostgreSQL real; as intercalações são determinísticas e há uma prova de concorrência real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlacementContinuityTests(PostgresFixture banco)
{
    private sealed record Cena(
        CenarioDeConversas C, string Contato, ChannelInstanceId Canal, Guid Matriz, Guid Filial, Guid SetorUm, Guid SetorDois,
        ServiceQueueView FilaA, ServiceQueueView FilaB, Agente SupervisorDeA, Agente SupervisorDeB, Agente Ana, Agente Carla, Guid Sessao, QueueItemView Item);

    private const string DaMatriz = "da matriz";
    private const string DaFilial = "da filial";

    /// <summary>O item está ativo na fila A (matriz, setor um), com a Ana; a mensagem 1 é "da matriz". A fila B é da filial. Os eventos das operações do próprio ContactCenter já foram entregues.</summary>
    private async Task<Cena> MontarAsync(string prefixo)
    {
        banco.RelogioDaMensageria.Sincronizar();
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true, mensageria: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var setorUm = await c.CriarSetorAsync(matriz, "Um");
        var setorDois = await c.CriarSetorAsync(matriz, "Dois");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        var filaA = await c.CriarFilaAsync("A", unidade: matriz);
        var filaB = await c.CriarFilaAsync("B", unidade: filial);
        var supervisorDeA = await c.AgenteNaFilaAsync(banco, filaA.Id, QueueMemberRole.Supervisor);
        var supervisorDeB = await c.AgenteNaFilaAsync(banco, filaB.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var carla = await c.AgenteNaFilaAsync(banco, filaB.Id);

        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: DaMatriz, unidade: matriz, setor: setorUm, canal: canal)).SessionId;
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        item = (await c.AssumirAsync(item.Id, ana.Contexto)).Item;
        await c.RodarRelayAsync();
        return new Cena(c, contato, canal, matriz, filial, setorUm, setorDois, filaA, filaB, supervisorDeA, supervisorDeB, ana, carla, sessao, item);
    }

    private static async Task<TransferSessionResult> PorForaAsync(Cena s, Guid unidade, Guid? setor = null)
    {
        var resultado = await s.C.EncaminharAsync(s.Sessao, unidade, setor, await s.C.VersaoDaSessaoAsync(s.Sessao));
        Assert.Equal(TransferOutcome.Applied, resultado.Outcome);
        return resultado;
    }

    /// <summary>A → B (unidade ou setor), uma mensagem do cliente lá, e a volta, DIRETO no Conversations: nenhum evento chega ao ContactCenter.</summary>
    private static async Task IdaEVoltaAsync(Cena s, Guid unidadeDeIda, Guid? setorDeIda)
    {
        await PorForaAsync(s, unidadeDeIda, setorDeIda);
        Assert.True((await s.C.EntrarAsync(s.Contato, texto: DaFilial, canal: s.Canal)).SessionId == s.Sessao);
        await PorForaAsync(s, s.Matriz, s.SetorUm);
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;

        // A unidade, o setor e o período são de novo os do item: o que NÃO é, é a geração da colocação.
        Assert.Equal((s.Matriz, (Guid?)s.SetorUm, HandlingMode.HumanAssisted, 3L), (sessao.BusinessUnitId, sessao.DepartmentId, sessao.HandlingMode, sessao.PlacementEpoch));
    }

    private static async Task<List<string?>?> TextosAsync(Cena s, Agente quem, int tamanho = 50, HistoryOrder ordem = HistoryOrder.OldestFirst)
    {
        var todos = new List<string?>();
        string? cursor = null;
        for (var pagina = 0; pagina < 30; pagina++)
        {
            Page<MessageView>? lida;
            try
            {
                lida = await s.C.ComoAsync(quem.Contexto, sp => sp.GetRequiredService<QueueItemService>().GetMessagesAsync(s.Item.Id, tamanho, cursor, ordem));
            }
            catch (QueueAccessDeniedException)
            {
                return null;
            }

            if (lida is null)
            {
                return null;
            }

            todos.AddRange(lida.Items.Select(m => m.Text));
            if (lida.NextCursor is null)
            {
                return todos;
            }

            cursor = lida.NextCursor;
        }

        Assert.Fail("A paginação não terminou.");
        return todos;
    }

    private static Task<QueueItem> LinhaAsync(Cena s) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Items.AsNoTracking().SingleAsync(i => i.Id == s.Item.Id));

    private static Task SqlAsync(Cena s, FormattableString sql) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<ContactCenterDbContext>().Database.ExecuteSqlAsync(sql);
            return 0;
        });

    private static async Task NaoEntregarAsync(Cena s, string tipo)
    {
        foreach (var linha in (await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao)).Where(o => o.EventType == tipo))
        {
            await s.C.AlterarOutboxAsync(linha.Id, o => o.State = OutboxState.Processed);
        }
    }

    private async Task LiberarAsync(Cena s)
    {
        banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(1));
        await s.C.LiberarRecuoAsync(s.Item.Id);
    }

    // A → B → A

    [Fact]
    public async Task A_ida_e_volta_entre_unidades_sem_nenhum_evento_projetado_nao_entrega_a_mensagem_da_outra_unidade_a_fila_de_origem()
    {
        var s = await MontarAsync("aba1");
        await IdaEVoltaAsync(s, s.Filial, null);

        // Antes de QUALQUER evento chegar ao ContactCenter, o supervisor só da fila A consulta: a validação encontra a unidade, o setor e o período de antes,
        // mas a geração da colocação não é a do item, e o limite ainda é nulo. A mensagem da unidade B nunca é entregue. A autorização, aliás, fecha tudo.
        var linha = await LinhaAsync(s);
        Assert.Equal((1L, null), (linha.PlacementEpoch, linha.HistoryLimitSeq));
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
        Assert.Null(await s.C.ConversaDoItemAsync(s.Item.Id, s.SupervisorDeA.Contexto));

        // Os eventos chegam depois: o limite, a sequência da última mensagem no primeiro encaminhamento, passa a valer, e a volta não o amplia.
        await s.C.RodarRelayAsync();
        Assert.Equal(1L, (await LinhaAsync(s)).HistoryLimitSeq);
        foreach (var (tamanho, ordem) in new[] { (50, HistoryOrder.OldestFirst), (1, HistoryOrder.OldestFirst), (1, HistoryOrder.NewestFirst) })
        {
            Assert.Equal(new List<string?> { DaMatriz }, await TextosAsync(s, s.SupervisorDeA, tamanho, ordem));
        }
    }

    [Fact]
    public async Task A_ida_e_volta_entre_setores_da_mesma_unidade_tambem_nao_entrega_a_mensagem_do_outro_setor()
    {
        var s = await MontarAsync("aba2");
        await IdaEVoltaAsync(s, s.Matriz, s.SetorDois);

        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
        await s.C.RodarRelayAsync();
        Assert.Equal(new List<string?> { DaMatriz }, await TextosAsync(s, s.SupervisorDeA));
    }

    [Fact]
    public async Task A_ida_e_volta_com_o_encerramento_antes_da_projecao_fecha_o_historico_ou_o_limita_a_fronteira_demonstrada()
    {
        // Os eventos chegam depois do encerramento, em ordem: o encaminhamento limita, e o encerramento não o amplia.
        var projetado = await MontarAsync("aba3a");
        await IdaEVoltaAsync(projetado, projetado.Filial, null);
        await projetado.C.EncerrarSessaoAsync(projetado.Sessao);
        await projetado.C.RodarRelayAsync();
        var fechado = await LinhaAsync(projetado);
        Assert.Equal((QueueItemStatus.Closed, 1L, 1L), (fechado.Status, fechado.HistoryLimitSeq, fechado.ClosedThroughMessageSeq));
        Assert.Equal(new List<string?> { DaMatriz }, await TextosAsync(projetado, projetado.SupervisorDeA));

        // O encaminhamento nunca foi projetado: sem fronteira demonstrável, o histórico fecha em vez de usar a sequência final.
        var naoProjetado = await MontarAsync("aba3b");
        await IdaEVoltaAsync(naoProjetado, naoProjetado.Filial, null);
        await naoProjetado.C.EncerrarSessaoAsync(naoProjetado.Sessao);
        await NaoEntregarAsync(naoProjetado, ConversationEventTypes.SessionTransferred);
        await naoProjetado.C.RodarRelayAsync();
        var semFronteira = await LinhaAsync(naoProjetado);
        Assert.Equal((QueueItemStatus.Closed, null, null), (semFronteira.Status, semFronteira.HistoryLimitSeq, semFronteira.ClosedThroughMessageSeq));
        Assert.Empty((await TextosAsync(naoProjetado, naoProjetado.SupervisorDeA))!);
    }

    [Fact]
    public async Task A_ida_e_volta_com_o_encerramento_pela_ressincronizacao_nao_amplia_a_fronteira()
    {
        var s = await MontarAsync("aba4");
        await IdaEVoltaAsync(s, s.Filial, null);
        await s.C.EncerrarSessaoAsync(s.Sessao);

        // Nenhum evento chega ao ContactCenter (nem o do encerramento): o fim só é conhecido pela ressincronização (a marca de um evento adiado).
        foreach (var linha in await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao))
        {
            await s.C.AlterarOutboxAsync(linha.Id, o => o.State = OutboxState.Processed);
        }

        await SqlAsync(s, $"UPDATE contact_center.queue_items SET resync_state_version = observed_state_version + 1 WHERE id = {s.Item.Id}");
        Assert.Equal(1, await s.C.ReconciliarAsync());

        var fechado = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, null), (fechado.Status, fechado.ClosedThroughMessageSeq)); // a sequência final (2) NÃO é a fronteira
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
    }

    [Fact]
    public async Task O_evento_do_encaminhamento_que_chega_atrasado_depois_do_encerramento_nao_muda_a_fronteira_nem_reabre_nada()
    {
        var s = await MontarAsync("aba5");
        await IdaEVoltaAsync(s, s.Filial, null);
        await s.C.EncerrarSessaoAsync(s.Sessao);

        // O encerramento é entregue ANTES dos encaminhamentos (a entrega atrasou): o item é encerrado sem fronteira demonstrável.
        var eventos = await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao);
        var transferencias = eventos.Where(o => o.EventType == ConversationEventTypes.SessionTransferred).ToList();
        foreach (var transferencia in transferencias)
        {
            await s.C.AlterarOutboxAsync(transferencia.Id, o => o.State = OutboxState.Processed);
        }

        await s.C.RodarRelayAsync();
        var fechado = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, null, null), (fechado.Status, fechado.HistoryLimitSeq, fechado.ClosedThroughMessageSeq));

        // O evento atrasado, enfim projetado, encontra o item encerrado: nada muda, e nada reabre.
        foreach (var transferencia in transferencias)
        {
            var evento = (SessionTransferredV1)MessagingJson.Deserialize(transferencia.Payload, typeof(SessionTransferredV1));
            Assert.Equal(
                ProjectionOutcome.Ignored,
                await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ConversationProjectionService>().ApplySessionTransferredAsync(evento, Guid.NewGuid())));
        }

        Assert.Equal((fechado.Version, fechado.ClosedThroughMessageSeq), ((await LinhaAsync(s)).Version, (await LinhaAsync(s)).ClosedThroughMessageSeq));
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
    }

    // Período e geração desconhecidos (registros legados)

    [Fact]
    public async Task Item_legado_sem_periodo_conhecido_nao_recebe_a_sequencia_final_de_outro_atendimento_humano()
    {
        var s = await MontarAsync("aba6");
        await SqlAsync(s, $"UPDATE contact_center.queue_items SET handling_period = NULL WHERE id = {s.Item.Id}");

        // Humano → IA → humano (outro período): os eventos nunca chegam ao ContactCenter. Depois, o encerramento.
        Assert.Equal(TransitionOutcome.Applied, (await s.C.TransicionarAsync(s.Sessao, HandlingMode.AiAutonomous, null, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
        Assert.True((await s.C.EntrarAsync(s.Contato, texto: "com a IA", canal: s.Canal)).SessionId == s.Sessao);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, s.Carla.Id, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
        Assert.True((await s.C.EntrarAsync(s.Contato, texto: "segundo atendimento", canal: s.Canal)).SessionId == s.Sessao);
        await s.C.EncerrarSessaoAsync(s.Sessao);
        await NaoEntregarAsync(s, ConversationEventTypes.HandlingModeChanged);
        await s.C.RodarRelayAsync();

        // O período desconhecido NUNCA é correspondência: a fronteira não é a sequência final de um atendimento posterior, e o histórico fecha.
        var fechado = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, null), (fechado.Status, fechado.ClosedThroughMessageSeq));
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
    }

    [Fact]
    public async Task Item_legado_sem_geracao_de_colocacao_nao_le_e_o_encerramento_nao_inventa_continuidade()
    {
        var s = await MontarAsync("aba7");
        await SqlAsync(s, $"UPDATE contact_center.queue_items SET placement_epoch = NULL WHERE id = {s.Item.Id}");

        // Sem nenhuma transferência: a sessão está onde sempre esteve, mas nada PROVA isso para este item.
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
        Assert.Null(await s.C.ConversaDoItemAsync(s.Item.Id, s.SupervisorDeA.Contexto));

        await s.C.EncerrarSessaoAsync(s.Sessao);
        await s.C.RodarRelayAsync();
        var fechado = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, null), (fechado.Status, fechado.ClosedThroughMessageSeq));
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
    }

    [Fact]
    public async Task O_item_sem_nenhuma_saida_continua_lendo_e_o_encerramento_normal_continua_valendo_com_a_sequencia_final()
    {
        var s = await MontarAsync("aba8");
        Assert.True((await s.C.EntrarAsync(s.Contato, texto: "ainda na matriz", canal: s.Canal)).SessionId == s.Sessao);

        // Colocação contínua, mesmo atendimento: a geração do item é a da sessão, e a leitura ativa vai até a última mensagem.
        Assert.Equal((await s.C.SessaoAsync(s.Sessao))!.PlacementEpoch, (await LinhaAsync(s)).PlacementEpoch);
        Assert.Equal(new List<string?> { DaMatriz, "ainda na matriz" }, await TextosAsync(s, s.SupervisorDeA));

        await s.C.EncerrarSessaoAsync(s.Sessao);
        await s.C.RodarRelayAsync();
        Assert.Equal((QueueItemStatus.Closed, 2L), ((await LinhaAsync(s)).Status, (await LinhaAsync(s)).ClosedThroughMessageSeq));
        Assert.Equal(new List<string?> { DaMatriz, "ainda na matriz" }, await TextosAsync(s, s.SupervisorDeA));
    }

    // A transferência legítima do próprio ContactCenter

    [Fact]
    public async Task A_transferencia_do_proprio_ContactCenter_prende_a_geracao_de_destino_e_a_fila_de_destino_le_tudo()
    {
        var s = await MontarAsync("aba9");
        var geracaoDeOrigem = (await s.C.SessaoAsync(s.Sessao))!.PlacementEpoch;

        Assert.Equal(OperationOutcome.Completed, (await s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaB.Id, null, s.Ana.Contexto)).Outcome);
        Assert.True((await s.C.EntrarAsync(s.Contato, texto: DaFilial, canal: s.Canal)).SessionId == s.Sessao);
        await s.C.RodarRelayAsync();

        // A geração do item é a da sessão (a da autorização, mais uma: a operação mudou a unidade), e a fila de destino não é bloqueada.
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((geracaoDeOrigem + 1, (long?)(geracaoDeOrigem + 1)), (sessao.PlacementEpoch, (await LinhaAsync(s)).PlacementEpoch));
        Assert.Null((await LinhaAsync(s)).HistoryLimitSeq);
        var tudo = new List<string?> { DaMatriz, DaFilial };
        Assert.Equal(tudo, await TextosAsync(s, s.SupervisorDeB));

        // Daí em diante, uma saída por fora tira o item da B do mesmo jeito (e a volta não o reabre).
        await PorForaAsync(s, s.Matriz, s.SetorUm);
        Assert.Empty((await TextosAsync(s, s.SupervisorDeB))!);
        await PorForaAsync(s, s.Filial);
        Assert.Empty((await TextosAsync(s, s.SupervisorDeB))!);
    }

    [Fact]
    public async Task A_transferencia_confirmada_em_parte_e_recuperada_prende_a_geracao_e_repetir_tudo_nao_muda_nada()
    {
        var s = await MontarAsync("aba10");
        var pedido = Guid.NewGuid();
        var geracaoDeOrigem = (await s.C.SessaoAsync(s.Sessao))!.PlacementEpoch;

        // O Conversations CONFIRMA a unidade e a resposta se perde; a Carla assume por fora antes da recuperação.
        var interrompida = await s.C.NaExecucaoAsync(
            s.Ana.Contexto,
            g => g.Depois(nameof(IConversationGateway.TransferAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.TransferAsync(s.Item.Id, s.FilaB.Id, null, pedido));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, s.Carla.Id, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
        await s.C.RodarRelayAsync();

        await LiberarAsync(s);
        Assert.Equal(1, await s.C.ReconciliarAsync());
        var linha = await LinhaAsync(s);
        Assert.Equal(((long?)(geracaoDeOrigem + 1), s.FilaB.Id, s.Carla.Id), (linha.PlacementEpoch, linha.QueueId, linha.AssignedUserId));
        Assert.Equal((await s.C.SessaoAsync(s.Sessao))!.PlacementEpoch, linha.PlacementEpoch);
        Assert.Equal(new List<string?> { DaMatriz }, await TextosAsync(s, s.SupervisorDeB));

        // Repetição: a reconciliação, o relay e o pedido original não mudam o item, a geração nem o histórico.
        Assert.Equal(0, await s.C.ReconciliarAsync());
        await s.C.RodarRelayAsync();
        var repetido = await LinhaAsync(s);
        Assert.Equal((linha.Version, linha.PlacementEpoch, linha.QueueId, linha.AssignedUserId, linha.UpdatedAt), (repetido.Version, repetido.PlacementEpoch, repetido.QueueId, repetido.AssignedUserId, repetido.UpdatedAt));
        Assert.Equal(s.FilaB.Id, (await s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaB.Id, null, s.Ana.Contexto, pedido)).Item.QueueId);
    }

    [Fact]
    public async Task A_mudanca_de_unidade_e_volta_entre_a_autorizacao_e_a_execucao_desfaz_a_operacao_pela_geracao_da_colocacao()
    {
        var s = await MontarAsync("aba11");
        var intercalou = false;

        // A Ana é autorizada a devolver a conversa à fila (a sessão está na geração 1); antes da execução, a conversa vai para a filial e VOLTA, por fora. A
        // unidade, o setor, o período e o responsável são de novo os da autorização; a geração (3) não é. O comando recusa, e nada é alterado.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.NaAutorizacaoAsync(
            s.Ana.Contexto,
            g => g.Depois(nameof(IConversationGateway.GetSessionAsync), async () =>
            {
                await PorForaAsync(s, s.Filial);
                await PorForaAsync(s, s.Matriz, s.SetorUm);
                intercalou = true;
            }),
            svc => svc.ReleaseAsync(s.Item.Id, Guid.NewGuid())));

        Assert.True(intercalou);
        Assert.Equal(ContactCenterRule.ConversationChanged, recusa.Rule);
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((HandlingMode.HumanAssisted, (Guid?)s.Ana.Id), (sessao.HandlingMode, sessao.ResponderRef)); // a conversa não voltou a esperar
    }

    // A revalidação depois da leitura e a concorrência

    [Fact]
    public async Task A_mudanca_de_colocacao_durante_a_busca_do_conteudo_nao_devolve_a_mensagem_da_outra_unidade()
    {
        var s = await MontarAsync("aba12");
        var intercalou = false;

        // A leitura é autorizada (a sessão está na colocação do item); ANTES de a consulta ao histórico rodar, a conversa vai para a filial, recebe uma
        // mensagem e volta. A consulta é limitada pela sequência vista na autorização, e a revalidação da colocação fecha o que foi buscado.
        var pagina = await s.C.NaAutorizacaoAsync(
            s.SupervisorDeA.Contexto,
            g => g.Antes(nameof(IConversationGateway.GetHistoryAsync), async () =>
            {
                await PorForaAsync(s, s.Filial);
                Assert.True((await s.C.EntrarAsync(s.Contato, texto: DaFilial, canal: s.Canal)).SessionId == s.Sessao);
                await PorForaAsync(s, s.Matriz, s.SetorUm);
                intercalou = true;
            }),
            svc => svc.GetMessagesAsync(s.Item.Id, 50, null, HistoryOrder.OldestFirst));

        Assert.True(intercalou);
        Assert.DoesNotContain(DaFilial, pagina!.Items.Select(m => m.Text));
        Assert.Empty(pagina.Items); // a colocação mudou durante a busca: nada é devolvido
    }

    [Fact]
    public async Task Sob_concorrencia_real_nenhuma_leitura_da_fila_de_origem_alcanca_a_mensagem_da_outra_unidade()
    {
        var s = await MontarAsync("aba13");
        var terminou = false;
        var lidos = new ConcurrentBag<string?>();
        var leituras = 0;

        async Task LeitorAsync()
        {
            while (!Volatile.Read(ref terminou))
            {
                try
                {
                    var pagina = await s.C.MensagensDoItemAsync(s.Item.Id, s.SupervisorDeA.Contexto);
                    foreach (var mensagem in pagina?.Items ?? Array.Empty<MessageView>())
                    {
                        lidos.Add(mensagem.Text);
                    }
                }
                catch (QueueAccessDeniedException)
                {
                }

                Interlocked.Increment(ref leituras);
            }
        }

        async Task EscritorAsync()
        {
            await PorForaAsync(s, s.Filial);
            Assert.True((await s.C.EntrarAsync(s.Contato, texto: DaFilial, canal: s.Canal)).SessionId == s.Sessao);
            await PorForaAsync(s, s.Matriz, s.SetorUm);
            await s.C.RodarRelayAsync();
            await s.C.EncerrarSessaoAsync(s.Sessao);
            await s.C.RodarRelayAsync();
            Volatile.Write(ref terminou, true);
        }

        await Task.WhenAll(LeitorAsync(), LeitorAsync(), LeitorAsync(), EscritorAsync());

        // Em qualquer intercalação entre as leituras, os encaminhamentos, a mensagem, o relay e o encerramento: a mensagem da unidade B nunca foi lida.
        Assert.True(leituras > 0);
        Assert.DoesNotContain(DaFilial, lidos);
        Assert.Equal(new List<string?> { DaMatriz }, await TextosAsync(s, s.SupervisorDeA));
    }
}
