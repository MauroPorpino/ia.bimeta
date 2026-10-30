using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A PROVENIÊNCIA das fronteiras de histórico: todo limite que autoriza conteúdo tem origem comprovada (a geração da colocação da qual a sessão saiu é a que o item
/// prendeu) e é certificado pelo caminho que o gravou. O limite de OUTRA geração nunca substitui nem amplia o do item; o encaminhamento de OUTRA operação para o mesmo
/// destino não é o da operação do item (a identidade é do identificador, não do destino); o registro anterior ao modelo certificado preserva o número como metadado e
/// NEGA o conteúdo; o item sem geração conhecida nunca usa um limite positivo; e o fim de outro atendimento nunca é a fronteira deste. O histórico anterior LEGÍTIMO
/// continua legível. PostgreSQL real; a entrega dos eventos é determinística.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BoundaryProvenanceTests(PostgresFixture banco)
{
    private sealed record Cena(
        CenarioDeConversas C, string Contato, ChannelInstanceId Canal, Guid Matriz, Guid Filial, Guid SetorUm, Guid SetorDois,
        ServiceQueueView FilaA, ServiceQueueView FilaB, Agente SupervisorDeA, Agente Ana, Guid Sessao, QueueItemView Item);

    private const string DaMatriz = "da matriz";
    private const string DaFilial = "da filial";

    /// <summary>O item está ativo na fila A (matriz, setor 1), com a Ana, e a conversa tem a mensagem 1 ("da matriz"). Os eventos das operações do próprio ContactCenter já foram entregues.</summary>
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
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);

        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: DaMatriz, unidade: matriz, setor: setorUm, canal: canal)).SessionId;
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        item = (await c.AssumirAsync(item.Id, ana.Contexto)).Item;
        await c.RodarRelayAsync();
        return new Cena(c, contato, canal, matriz, filial, setorUm, setorDois, filaA, filaB, supervisorDeA, ana, sessao, item);
    }

    private static async Task<TransferSessionResult> PorForaAsync(Cena s, Guid unidade, Guid? setor = null) =>
        await s.C.EncaminharAsync(s.Sessao, unidade, setor, await s.C.VersaoDaSessaoAsync(s.Sessao));

    private static async Task NovaMensagemAsync(Cena s, string texto) =>
        Assert.True((await s.C.EntrarAsync(s.Contato, texto: texto, canal: s.Canal)).SessionId == s.Sessao);

    /// <summary>Os textos que o supervisor da fila A consegue ler do item (vazio se a leitura é negada ou fechada).</summary>
    private static async Task<List<string?>> TextosAsync(Cena s)
    {
        try
        {
            return (await s.C.MensagensDoItemAsync(s.Item.Id, s.SupervisorDeA.Contexto))?.Items.Select(m => m.Text).ToList() ?? new List<string?>();
        }
        catch (QueueAccessDeniedException)
        {
            return new List<string?>();
        }
    }

    private static Task<QueueItem> LinhaAsync(Cena s) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Items.AsNoTracking().SingleAsync(i => i.Id == s.Item.Id));

    private static Task<int> ExecutarSqlAsync(Cena s, string sql) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database.ExecuteSqlRawAsync(sql));

    private static Task<ProjectionOutcome> ProjetarAsync(Cena s, SessionTransferredV1 evento) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ConversationProjectionService>().ApplySessionTransferredAsync(evento, Guid.NewGuid()));

    private static Task<ProjectionOutcome> ProjetarAsync(Cena s, HandlingModeChangedV1 evento) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ConversationProjectionService>().ApplyHandlingModeChangedAsync(evento, Guid.NewGuid()));

    /// <summary>Grava a intenção de transferir o item para a fila B (da filial), com a base COMPLETA da sessão: a intenção PRÓPRIA do item, com o TransferId dela.</summary>
    private static Task GravarTransferenciaPropriaAsync(Cena s) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            (await itens.FindItemAsync(s.Item.Id, default))!.BeginTransferToQueue(
                s.FilaB.Id, null, s.Filial, null, Guid.NewGuid(), null, "transfer", DateTimeOffset.UtcNow, basis: await s.C.BaseDaSessaoAsync(s.Sessao));
            await itens.SaveChangesAsync(default);
            return 0;
        });

    private async Task LiberarAsync(Cena s)
    {
        banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(1));
        await s.C.LiberarRecuoAsync(s.Item.Id);
    }

    // T3: o encaminhamento externo para o MESMO destino da intenção não é o da operação do item

    [Fact]
    public async Task T3_o_encaminhamento_de_outra_operacao_para_o_destino_da_intencao_grava_o_limite_da_geracao_do_item_e_a_mensagem_da_outra_unidade_nunca_chega()
    {
        var s = await MontarAsync("prov1");
        await GravarTransferenciaPropriaAsync(s); // a intenção PRÓPRIA do item: transferir para a fila B (filial)
        var geracaoDoItem = (await LinhaAsync(s)).PlacementEpoch;
        Assert.NotNull(geracaoDoItem);

        // E1: OUTRO encaminhamento (outro TransferId) leva a sessão a A → B, o mesmo destino da intenção, e chega ao ContactCenter com a intenção pendente. O
        // caminho antigo o classificava como "da própria operação" só pelo destino e o ignorava, e o limite acabava sendo o de um encaminhamento posterior.
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Filial)).Outcome);
        await s.C.RodarRelayAsync();
        await NovaMensagemAsync(s, DaFilial); // a mensagem da filial (sequência 2)
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Matriz, s.SetorUm)).Outcome); // B → A: a colocação volta ao lugar de antes (outra geração)

        // A intenção do item é executada: a colocação de agora não é a autorizada (A → B → A), e ela é desfeita, sem escrever nada.
        await LiberarAsync(s);
        await s.C.ReconciliarAsync();
        Assert.Null((await s.C.ItemAsync(s.Item.Id))!.Pending);

        // E3: a sessão sai de novo (setor 2), de OUTRA geração. A saída posterior não substitui nem amplia a do item.
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Matriz, s.SetorDois)).Outcome);
        await s.C.RodarRelayAsync();

        // O limite é o da PRIMEIRA saída da geração do item (a mensagem 1), com a origem comprovada e certificada.
        var linha = await LinhaAsync(s);
        Assert.Equal((1L, geracaoDoItem, QueueItemBoundaryModel.Certified), (linha.HistoryLimitSeq, linha.HistoryLimitOriginEpoch, linha.BoundaryModel));

        // O histórico anterior LEGÍTIMO continua legível, e nada da outra unidade (nem a mensagem 2) é lido.
        var textos = await TextosAsync(s);
        Assert.Contains(DaMatriz, textos);
        Assert.DoesNotContain(DaFilial, textos);
    }

    // T4: o limite sem origem, ou de outra geração, nunca é gravado

    [Fact]
    public async Task T4_a_saida_posterior_de_outra_geracao_com_um_limite_que_incluiria_a_mensagem_indevida_nao_e_gravada()
    {
        var s = await MontarAsync("prov2");
        var geracaoDoItem = (await LinhaAsync(s)).PlacementEpoch!.Value;

        // A PRIMEIRA saída (A → B) nunca chega ao ContactCenter (os eventos reais não são entregues neste teste). A mensagem da filial chega, a sessão volta, e sai
        // de novo: só esta última saída é projetada, com a sequência que INCLUI a mensagem da filial, mas de uma geração que não é a do item.
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Filial)).Outcome);
        await NovaMensagemAsync(s, DaFilial);
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Matriz, s.SetorUm)).Outcome);
        var geracaoDeAgora = (await s.C.SessaoAsync(s.Sessao))!.PlacementEpoch;
        Assert.True(geracaoDeAgora > geracaoDoItem + 1);
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);

        var posterior = new SessionTransferredV1(s.Sessao, Guid.NewGuid(), s.Matriz, s.SetorUm, s.Matriz, s.SetorDois, versao + 1, MessageSeq: 2, FromPlacementEpoch: geracaoDeAgora, ToPlacementEpoch: geracaoDeAgora + 1);
        Assert.Equal(ProjectionOutcome.Ignored, await ProjetarAsync(s, posterior));

        // Nenhum limite foi gravado (a origem não é a geração do item), e a leitura fica fechada: a sessão está em outra geração. A mensagem da filial não é lida.
        var linha = await LinhaAsync(s);
        Assert.Equal((null, null, QueueItemBoundaryModel.Legacy), (linha.HistoryLimitSeq, linha.HistoryLimitOriginEpoch, linha.BoundaryModel));
        Assert.DoesNotContain(DaFilial, await TextosAsync(s));
    }

    [Fact]
    public async Task T4_o_evento_antigo_sem_a_geracao_de_origem_nao_grava_limite_algum()
    {
        var s = await MontarAsync("prov3");
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Filial)).Outcome);

        // Como era antes de existir o campo: a geração de origem é desconhecida, e a sequência também. Sem origem comprovada, nada é gravado.
        var antigo = new SessionTransferredV1(s.Sessao, Guid.NewGuid(), s.Matriz, s.SetorUm, s.Filial, null, 5);
        Assert.Equal(ProjectionOutcome.Ignored, await ProjetarAsync(s, antigo));
        Assert.Null((await LinhaAsync(s)).HistoryLimitSeq);
    }

    // T5: o registro anterior ao modelo certificado, e o item sem geração conhecida

    [Fact]
    public async Task T5_o_limite_positivo_do_item_sem_geracao_conhecida_nao_autoriza_a_leitura()
    {
        var s = await MontarAsync("prov4");
        await ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET placement_epoch = NULL, history_limit_seq = 1 WHERE id = '{s.Item.Id}'");
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Filial)).Outcome);

        // A sessão saiu da colocação, e o item não sabe de qual geração é: o número 1 é metadado, não autorização.
        Assert.Empty(await TextosAsync(s));
        Assert.Equal(1L, (await LinhaAsync(s)).HistoryLimitSeq); // o número é preservado
    }

    [Fact]
    public async Task T5_o_limite_do_modelo_legado_nega_o_conteudo_mesmo_com_a_geracao_conhecida_e_o_certificado_o_autoriza()
    {
        var s = await MontarAsync("prov5");
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Filial)).Outcome);
        await NovaMensagemAsync(s, DaFilial);
        await ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET history_limit_seq = 1 WHERE id = '{s.Item.Id}'"); // sem origem, no modelo legado (0)

        Assert.Empty(await TextosAsync(s));

        // O mesmo número, com a origem comprovada (a geração do item) e o modelo certificado: autoriza o que veio ATÉ a saída, e só isso.
        var geracao = (await LinhaAsync(s)).PlacementEpoch!.Value;
        await ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET history_limit_origin_epoch = {geracao}, boundary_model = 1 WHERE id = '{s.Item.Id}'");
        var textos = await TextosAsync(s);
        Assert.Contains(DaMatriz, textos);
        Assert.DoesNotContain(DaFilial, textos);
    }

    [Fact]
    public async Task T5_o_item_encerrado_por_versao_anterior_preserva_o_numero_e_nega_o_conteudo_e_o_certificado_le()
    {
        var s = await MontarAsync("prov6");
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAIAAsync(s.Item.Id, s.Ana.Contexto)).Outcome);

        // Encerrado pela própria operação, com o recibo dela: a fronteira é certificada, e o histórico do atendimento é legível.
        var certificado = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, QueueItemBoundaryModel.Certified, 1L), (certificado.Status, certificado.BoundaryModel, certificado.ClosedThroughMessageSeq));
        Assert.Contains(DaMatriz, await TextosAsync(s));

        // O MESMO registro como os encerrados por versões anteriores (sem a certificação): o número fica, o conteúdo não é autorizado.
        await ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET boundary_model = 0 WHERE id = '{s.Item.Id}'");
        Assert.Equal(1L, (await LinhaAsync(s)).ClosedThroughMessageSeq);
        Assert.Empty(await TextosAsync(s));
    }

    [Fact]
    public async Task T5_o_encerramento_do_item_sem_geracao_conhecida_nao_usa_o_limite_positivo_como_fronteira()
    {
        var s = await MontarAsync("prov7");
        await ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET placement_epoch = NULL, history_limit_seq = 1 WHERE id = '{s.Item.Id}'");
        Assert.Equal(TransferOutcome.Applied, (await PorForaAsync(s, s.Filial)).Outcome);
        await NovaMensagemAsync(s, DaFilial);

        await s.C.EncerrarSessaoAsync(s.Sessao);
        await s.C.RodarRelayAsync();

        // O fim da sessão não amplia nem "promove" o limite sem origem: a fronteira é desconhecida, e o histórico fica fechado.
        var linha = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, null, QueueItemBoundaryModel.Legacy), (linha.Status, linha.ClosedThroughMessageSeq, linha.BoundaryModel));
        Assert.Empty(await TextosAsync(s));
    }

    // T6: a devolução externa à IA só usa a fronteira do atendimento que o item representa

    [Fact]
    public async Task T6_a_devolucao_externa_de_outro_periodo_encerra_o_item_sem_usar_a_fronteira_do_evento()
    {
        var s = await MontarAsync("prov8");
        var antes = await LinhaAsync(s);
        var evento = new HandlingModeChangedV1(
            s.Sessao, HandlingMode.HumanAssisted, HandlingMode.AiAutonomous, null, antes.ObservedStateVersion + 10, Guid.NewGuid(), 7,
            HandlingPeriod: antes.HandlingPeriod!.Value + 5, PlacementEpoch: antes.PlacementEpoch);

        Assert.Equal(ProjectionOutcome.Applied, await ProjetarAsync(s, evento));

        // O fim é de OUTRO atendimento: a sequência 7 não é a fronteira do item, que fecha o histórico.
        var linha = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, "returned-to-ai", null), (linha.Status, linha.CloseReason, linha.ClosedThroughMessageSeq));
    }

    [Fact]
    public async Task T6_a_devolucao_externa_sem_a_identidade_do_atendimento_adia_e_nao_encerra_nem_usa_a_fronteira()
    {
        var s = await MontarAsync("prov9");
        var antes = await LinhaAsync(s);
        var versao = antes.ObservedStateVersion + 10;
        var antigo = new HandlingModeChangedV1(s.Sessao, HandlingMode.HumanAssisted, HandlingMode.AiAutonomous, null, versao, Guid.NewGuid(), 7);

        Assert.Equal(ProjectionOutcome.Deferred, await ProjetarAsync(s, antigo));

        var linha = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Assigned, versao, null), (linha.Status, linha.ResyncStateVersion, linha.ClosedThroughMessageSeq));
        await s.C.LimparPendenciasAsync(); // a obrigação aberta não termina sozinha: não deixa trabalho elegível no banco compartilhado
    }

    [Fact]
    public async Task T6_a_devolucao_externa_do_proprio_atendimento_e_encerrada_com_a_fronteira_certificada_e_o_historico_e_legivel()
    {
        var s = await MontarAsync("prov10");
        var antes = await LinhaAsync(s);
        var evento = new HandlingModeChangedV1(
            s.Sessao, HandlingMode.HumanAssisted, HandlingMode.AiAutonomous, null, antes.ObservedStateVersion + 10, Guid.NewGuid(), 1,
            HandlingPeriod: antes.HandlingPeriod, PlacementEpoch: antes.PlacementEpoch);

        Assert.Equal(ProjectionOutcome.Applied, await ProjetarAsync(s, evento));

        var linha = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, 1L, QueueItemBoundaryModel.Certified), (linha.Status, linha.ClosedThroughMessageSeq, linha.BoundaryModel));
        Assert.Contains(DaMatriz, await TextosAsync(s));
    }

    // T10: o banco recusa a origem sem o modelo certificado, e o item nasce no modelo legado

    [Fact]
    public async Task T10_o_banco_recusa_origem_sem_limite_ou_sem_o_modelo_certificado_e_o_item_nasce_no_modelo_legado()
    {
        var s = await MontarAsync("prov11");
        var linha = await LinhaAsync(s);
        Assert.Equal((QueueItemBoundaryModel.Legacy, null, null), (linha.BoundaryModel, linha.HistoryLimitOriginEpoch, linha.HistoryLimitSeq));

        var semModelo = await Assert.ThrowsAnyAsync<Exception>(() => ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET history_limit_seq = 1, history_limit_origin_epoch = 1 WHERE id = '{s.Item.Id}'"));
        Assert.Contains("ck_queue_items_boundary_provenance", semModelo.ToString(), StringComparison.Ordinal);

        var semLimite = await Assert.ThrowsAnyAsync<Exception>(() => ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET boundary_model = 1, history_limit_origin_epoch = 1 WHERE id = '{s.Item.Id}'"));
        Assert.Contains("ck_queue_items_boundary_provenance", semLimite.ToString(), StringComparison.Ordinal);

        var modeloInvalido = await Assert.ThrowsAnyAsync<Exception>(() => ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET boundary_model = 2 WHERE id = '{s.Item.Id}'"));
        Assert.Contains("ck_queue_items_boundary_provenance", modeloInvalido.ToString(), StringComparison.Ordinal);
    }
}
