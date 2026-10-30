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
/// O encerramento NÃO amplia o que um atendimento podia ver. Se a sessão já tinha saído do escopo do item (encaminhada, direto no Conversations,
/// para outra unidade ou setor, ou o atendimento que ele representava acabou antes), o que a conversa recebeu depois não é da fila dele, mesmo
/// que o ContactCenter só tenha sabido do fim agora e mesmo que nunca tenha projetado a saída. A fronteira é a que se demonstra (a sequência da
/// última mensagem no instante do encaminhamento); sem ela, o histórico fecha. PostgreSQL real; a entrega dos eventos é determinística.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeExitHistoryTests(PostgresFixture banco)
{
    private sealed record Cena(
        CenarioDeConversas C, string Contato, ChannelInstanceId Canal, Guid Matriz, Guid Filial, Guid SetorUm, Guid SetorDois,
        ServiceQueueView FilaA, ServiceQueueView FilaB, Agente SupervisorDeA, Agente SupervisorDeB, Agente Ana, Guid Sessao, QueueItemView Item);

    private const string DaMatriz = "da matriz";
    private const string DaFilial = "da filial";

    /// <summary>
    /// O item está ativo na fila A (matriz, setor 1), com a Ana, e a conversa tem a mensagem 1 ("da matriz"). A fila B é da filial. Os eventos das operações
    /// do próprio ContactCenter já foram entregues.
    /// </summary>
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

        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: DaMatriz, unidade: matriz, setor: setorUm, canal: canal)).SessionId;
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        item = (await c.AssumirAsync(item.Id, ana.Contexto)).Item;
        await c.RodarRelayAsync();
        return new Cena(c, contato, canal, matriz, filial, setorUm, setorDois, filaA, filaB, supervisorDeA, supervisorDeB, ana, sessao, item);
    }

    private static async Task<TransferSessionResult> TransferirPorForaAsync(Cena s, Guid unidade, Guid? setor = null) =>
        await s.C.EncaminharAsync(s.Sessao, unidade, setor, await s.C.VersaoDaSessaoAsync(s.Sessao));

    private static async Task NovaMensagemAsync(Cena s, string texto) =>
        Assert.True((await s.C.EntrarAsync(s.Contato, texto: texto, canal: s.Canal)).SessionId == s.Sessao);

    /// <summary>Os textos que o usuário consegue ler do item, ou <c>null</c> se não vê o item nem o conteúdo.</summary>
    private static async Task<List<string?>?> TextosAsync(Cena s, Agente quem)
    {
        try
        {
            var pagina = await s.C.MensagensDoItemAsync(s.Item.Id, quem.Contexto);
            return pagina?.Items.Select(m => m.Text).ToList();
        }
        catch (QueueAccessDeniedException)
        {
            return null;
        }
    }

    /// <summary>O Conversations publicou o evento, mas o ContactCenter NUNCA o recebe (simula a projeção que não aconteceu).</summary>
    private static async Task NaoEntregarAsync(Cena s, string tipo)
    {
        foreach (var linha in (await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao)).Where(o => o.EventType == tipo))
        {
            await s.C.AlterarOutboxAsync(linha.Id, o => o.State = OutboxState.Processed);
        }
    }

    /// <summary>O relógio de teste do Messaging é parado: avança um pouco para a operação pendente ficar elegível e tira o recuo dela.</summary>
    private async Task LiberarAsync(Cena s)
    {
        banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(1));
        await s.C.LiberarRecuoAsync(s.Item.Id);
    }

    private static Task<QueueItem> LinhaAsync(Cena s) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Items.AsNoTracking().SingleAsync(i => i.Id == s.Item.Id));

    private static Task<ProjectionOutcome> ProjetarAsync(Cena s, SessionTransferredV1 evento) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ConversationProjectionService>().ApplySessionTransferredAsync(evento, Guid.NewGuid()));

    // A reprodução da auditoria

    [Fact]
    public async Task A_mensagem_da_unidade_de_destino_nunca_chega_a_fila_de_origem_depois_do_encerramento_e_o_historico_anterior_continua_legivel()
    {
        var s = await MontarAsync("saida1");

        // O Conversations transfere DIRETO a sessão para a unidade B (por fora do ContactCenter); chega uma mensagem na unidade B; a sessão é encerrada.
        Assert.Equal(TransferOutcome.Applied, (await TransferirPorForaAsync(s, s.Filial)).Outcome);
        await NovaMensagemAsync(s, DaFilial);
        Assert.Equal(2, (await s.C.SessaoAsync(s.Sessao))!.LastMessageSeq);
        await s.C.EncerrarSessaoAsync(s.Sessao);

        // A leitura ATIVA da fila A já estava fechada; o encerramento não a reabre com a sequência final (2).
        await s.C.RodarRelayAsync();

        var item = (await s.C.ItemAsync(s.Item.Id, s.SupervisorDeA.Contexto))!;
        Assert.Equal(QueueItemStatus.Closed, item.Status);
        var linha = await LinhaAsync(s);
        Assert.Equal((1L, 1L), (linha.HistoryLimitSeq, linha.ClosedThroughMessageSeq)); // a fronteira demonstrada: a mensagem 1, que é da matriz

        // O supervisor só da fila A lê o que veio ANTES da saída e nunca a mensagem da unidade B.
        var textos = await TextosAsync(s, s.SupervisorDeA);
        Assert.Equal(new List<string?> { DaMatriz }, textos);
        Assert.DoesNotContain(DaFilial, textos!);
    }

    [Fact]
    public async Task Sem_a_projecao_do_encaminhamento_a_fronteira_nao_se_demonstra_e_o_historico_fecha_em_vez_de_abrir()
    {
        var s = await MontarAsync("saida2");
        Assert.Equal(TransferOutcome.Applied, (await TransferirPorForaAsync(s, s.Filial)).Outcome);
        await NovaMensagemAsync(s, DaFilial);
        await s.C.EncerrarSessaoAsync(s.Sessao);

        // O ContactCenter não recebeu o encaminhamento: só sabe do encerramento. A decisão não depende da unidade que o item guardou ter sido
        // projetada: ela é conferida contra a sessão, que terminou em outra unidade.
        await NaoEntregarAsync(s, ConversationEventTypes.SessionTransferred);
        await s.C.RodarRelayAsync();

        var linha = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, null, null), (linha.Status, linha.HistoryLimitSeq, linha.ClosedThroughMessageSeq));
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
    }

    [Fact]
    public async Task Encaminhamento_de_um_evento_antigo_sem_a_origem_nao_grava_limite_e_o_historico_fica_fechado_ate_chegar_o_evento_com_origem()
    {
        var s = await MontarAsync("saida3");
        Assert.Equal(TransferOutcome.Applied, (await TransferirPorForaAsync(s, s.Filial)).Outcome);
        await NovaMensagemAsync(s, DaFilial);

        // O evento, como era antes de existir o campo: nem a sequência nem a geração de ORIGEM são conhecidas. Sem origem comprovada nenhum limite é gravado
        // (um limite sem origem não autoriza conteúdo), e o acesso já está fechado: a sessão está em outra geração que a do item.
        var anterior = new SessionTransferredV1(s.Sessao, Guid.NewGuid(), s.Matriz, s.SetorUm, s.Filial, null, 5);
        Assert.Equal(ProjectionOutcome.Ignored, await ProjetarAsync(s, anterior));
        Assert.Null((await LinhaAsync(s)).HistoryLimitSeq);
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);

        // O evento real (com a sequência e a geração de origem) chega depois, e é a única fonte do limite: certificado, da geração do item.
        await s.C.RodarRelayAsync();
        var comLimite = await LinhaAsync(s);
        Assert.NotNull(comLimite.HistoryLimitSeq);
        Assert.Equal((QueueItemBoundaryModel.Certified, true), (comLimite.BoundaryModel, comLimite.HistoryLimitIsValid));

        // O encerramento nunca amplia o que o atendimento pôde ver: a fronteira é a do limite de saída certificado, e a mensagem da filial não é lida.
        await s.C.EncerrarSessaoAsync(s.Sessao);
        await s.C.RodarRelayAsync();
        var linha = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, comLimite.HistoryLimitSeq), (linha.Status, linha.ClosedThroughMessageSeq));
        Assert.DoesNotContain(DaFilial, (await TextosAsync(s, s.SupervisorDeA))!);
    }

    [Fact]
    public async Task O_encerramento_repetido_a_entrega_repetida_e_a_consulta_depois_da_reconciliacao_mantem_a_mesma_fronteira()
    {
        var s = await MontarAsync("saida4");
        Assert.Equal(TransferOutcome.Applied, (await TransferirPorForaAsync(s, s.Filial)).Outcome);
        await NovaMensagemAsync(s, DaFilial);
        await s.C.EncerrarSessaoAsync(s.Sessao);

        // O encaminhamento projetado duas vezes (a repetição não muda nada) e o encerramento entregue, repetido pelo relay e aplicado de novo direto.
        var real = (await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao)).First(o => o.EventType == ConversationEventTypes.SessionTransferred);
        var evento = (SessionTransferredV1)MessagingJson.Deserialize(real.Payload, typeof(SessionTransferredV1));
        Assert.Equal(ProjectionOutcome.Applied, await ProjetarAsync(s, evento));
        var versaoDoItem = (await LinhaAsync(s)).Version;
        Assert.Equal(ProjectionOutcome.Ignored, await ProjetarAsync(s, evento));
        Assert.Equal(versaoDoItem, (await LinhaAsync(s)).Version);

        await s.C.RodarRelayAsync();
        await s.C.RodarRelayAsync();
        var fechado = await LinhaAsync(s);
        Assert.Equal((1L, QueueItemStatus.Closed), (fechado.ClosedThroughMessageSeq, fechado.Status));
        var encerrar = new SessionClosedV1(s.Sessao, "manual", 2);
        Assert.Equal(
            ProjectionOutcome.Ignored,
            await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ConversationProjectionService>().ApplySessionClosedAsync(encerrar, Guid.NewGuid())));
        Assert.Equal(fechado.Version, (await LinhaAsync(s)).Version);

        // A consulta, depois de tudo reconciliado (e em qualquer ordem e tamanho de página), nunca devolve a mensagem da unidade B.
        foreach (var (ordem, tamanho) in new[] { (HistoryOrder.OldestFirst, 1), (HistoryOrder.NewestFirst, 1), (HistoryOrder.NewestFirst, 50) })
        {
            var pagina = await s.C.ComoAsync(s.SupervisorDeA.Contexto, sp => sp.GetRequiredService<QueueItemService>().GetMessagesAsync(s.Item.Id, tamanho, null, ordem));
            Assert.Equal(new List<string?> { DaMatriz }, pagina!.Items.Select(m => m.Text).ToList());
        }
    }

    // Os outros caminhos para fora do escopo

    [Fact]
    public async Task A_volta_da_sessao_ao_escopo_nao_reabre_o_que_veio_na_outra_unidade_para_o_item_ativo()
    {
        var s = await MontarAsync("saida5");
        Assert.Equal(TransferOutcome.Applied, (await TransferirPorForaAsync(s, s.Filial)).Outcome);
        await NovaMensagemAsync(s, DaFilial);
        Assert.Equal(TransferOutcome.Applied, (await TransferirPorForaAsync(s, s.Matriz, s.SetorUm)).Outcome);
        await NovaMensagemAsync(s, "de volta");
        await s.C.RodarRelayAsync();

        // A sessão voltou ao lugar do item (o item volta a casar com ela), mas o limite que ele guardou ao ver a saída vale: a mensagem 2, da
        // filial, e as seguintes, que vieram sem o item, não são dele.
        Assert.Equal(1L, (await LinhaAsync(s)).HistoryLimitSeq);
        Assert.Equal(new List<string?> { DaMatriz }, await TextosAsync(s, s.SupervisorDeA));
    }

    [Fact]
    public async Task O_encaminhamento_para_outro_setor_da_mesma_unidade_tambem_tira_a_sessao_do_escopo_do_item()
    {
        var s = await MontarAsync("saida6");
        Assert.Equal(TransferOutcome.Applied, (await TransferirPorForaAsync(s, s.Matriz, s.SetorDois)).Outcome);
        await NovaMensagemAsync(s, "do setor dois");
        await s.C.EncerrarSessaoAsync(s.Sessao);
        await s.C.RodarRelayAsync();

        Assert.Equal((QueueItemStatus.Closed, 1L, 1L), ((await LinhaAsync(s)).Status, (await LinhaAsync(s)).HistoryLimitSeq, (await LinhaAsync(s)).ClosedThroughMessageSeq));
        Assert.Equal(new List<string?> { DaMatriz }, await TextosAsync(s, s.SupervisorDeA));
    }

    [Fact]
    public async Task O_atendimento_que_acabou_antes_do_encerramento_nao_se_estende_ao_segundo_atendimento_humano_quando_o_fim_dele_nunca_foi_projetado()
    {
        var s = await MontarAsync("saida7");

        // Humano → IA → humano (outro período): os eventos da volta à IA e da nova atribuição NUNCA chegam ao ContactCenter.
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);
        Assert.Equal(TransitionOutcome.Applied, (await s.C.TransicionarAsync(s.Sessao, HandlingMode.AiAutonomous, null, versao)).Outcome);
        await NovaMensagemAsync(s, "com a IA");
        Assert.Equal(
            TransitionOutcome.Applied,
            (await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, Guid.NewGuid(), await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
        await NovaMensagemAsync(s, "segundo atendimento");
        await s.C.EncerrarSessaoAsync(s.Sessao);
        await NaoEntregarAsync(s, ConversationEventTypes.HandlingModeChanged);
        await s.C.RodarRelayAsync();

        // O período 2 não é o do item (1): a sequência final não é a fronteira do atendimento dele.
        var linha = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, null), (linha.Status, linha.ClosedThroughMessageSeq));
        Assert.Empty((await TextosAsync(s, s.SupervisorDeA))!);
    }

    // O que NÃO limita: o encaminhamento da própria operação do ContactCenter

    [Fact]
    public async Task O_encaminhamento_feito_pela_propria_transferencia_nao_limita_o_historico_da_fila_de_destino()
    {
        var s = await MontarAsync("saida8");

        Assert.Equal(OperationOutcome.Completed, (await s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaB.Id, null, s.Ana.Contexto)).Outcome);
        await NovaMensagemAsync(s, DaFilial);
        await s.C.RodarRelayAsync();

        Assert.Null((await LinhaAsync(s)).HistoryLimitSeq);
        Assert.Equal(new List<string?> { DaMatriz, DaFilial }, await TextosAsync(s, s.SupervisorDeB));
        Assert.Null(await TextosAsync(s, s.SupervisorDeA)); // a fila de origem não vê mais o item
    }

    [Fact]
    public async Task O_encaminhamento_da_propria_transferencia_pendente_chegando_antes_da_conclusao_tambem_nao_limita()
    {
        var s = await MontarAsync("saida9");

        // O Conversations confirma a unidade e a resposta se perde; o evento do encaminhamento chega com a transferência ainda pendente.
        var interrompida = await s.C.NaExecucaoAsync(
            s.Ana.Contexto,
            g => g.Depois(nameof(IConversationGateway.TransferAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.TransferAsync(s.Item.Id, s.FilaB.Id, null, Guid.NewGuid()));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        await s.C.RodarRelayAsync();
        Assert.Null((await LinhaAsync(s)).HistoryLimitSeq); // é a saída intencional da própria operação, não uma saída por fora

        await LiberarAsync(s);
        Assert.Equal(1, await s.C.ReconciliarAsync());
        await NovaMensagemAsync(s, DaFilial);
        Assert.Equal(new List<string?> { DaMatriz, DaFilial }, await TextosAsync(s, s.SupervisorDeB));
    }
}
