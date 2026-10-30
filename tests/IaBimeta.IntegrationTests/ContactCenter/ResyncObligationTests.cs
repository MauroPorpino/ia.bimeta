using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A OBRIGAÇÃO de ressincronização (o item ficou atrás do Conversations: um evento externo ficou por aplicar) é um fato durável e independente da versão
/// observada. Avançar <c>ObservedStateVersion</c> NÃO a liquida: só uma liquidação explícita (o espelho da sessão com a prova de identidade, o encerramento, a
/// cobertura comprovada de uma operação, ou a liquidação NÃO VERIFICADA do legado sem prova) a zera. Enquanto aberta, um evento posterior não é projetado
/// normalmente (adia), e o Worker continua achando o tenant e o item. PostgreSQL real; as intercalações são determinísticas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ResyncObligationTests(PostgresFixture banco)
{
    private sealed record Cena(CenarioDeConversas C, ServiceQueueView Fila, Agente Ana, Agente Beto, Agente Dora, Guid Sessao, QueueItemView Item);

    /// <summary>O item está ativo na fila A, com a Ana, no período 1 do atendimento humano. O relay do Conversations está ligado, e os eventos do próprio ContactCenter já foram entregues.</summary>
    private async Task<Cena> MontarAsync(string prefixo)
    {
        banco.RelogioDaMensageria.Sincronizar();
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true, mensageria: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var canal = await c.CriarCanalAsync(new[] { matriz });
        var fila = await c.CriarFilaAsync("A", unidade: matriz);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var dora = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync(unidade: matriz, canal: canal);
        var item = await c.NaFilaAsync(sessao, fila.Id);
        item = (await c.AssumirAsync(item.Id, ana.Contexto)).Item;
        await c.RodarRelayAsync();
        return new Cena(c, fila, ana, beto, dora, sessao, item);
    }

    private static Task<QueueItem> LinhaAsync(Cena s) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Items.AsNoTracking().SingleAsync(i => i.Id == s.Item.Id));

    private static Task<long> VersaoAsync(Cena s) => s.C.VersaoDaSessaoAsync(s.Sessao);

    private static Task<int> ExecutarSqlAsync(Cena s, string sql) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database.ExecuteSqlRawAsync(sql));

    private static async Task<int> AuditoriasAsync(Cena s, string operacao, AuditOutcome resultado) =>
        (await s.C.AuditoriaAsync(operacao, s.Item.Id.ToString())).Count(e => e.Outcome == resultado);

    private static async Task TransicionarAsync(Cena s, HandlingMode para, Guid? responsavel) =>
        Assert.Equal(TransitionOutcome.Applied, (await s.C.TransicionarAsync(s.Sessao, para, responsavel, await VersaoAsync(s))).Outcome);

    /// <summary>A operação pendente termina por desistência (desfeita), como o executor faz ao recusar um destino: nada do que ficou por aplicar foi aplicado.</summary>
    private static Task DesfazerPendenciaAsync(Cena s) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            (await itens.FindItemAsync(s.Item.Id, default))!.CancelPending("operation-rejected", DateTimeOffset.UtcNow);
            await itens.SaveChangesAsync(default);
            return 0;
        });

    // T7 (identidades conhecidas): a recuperação converge ao ESTADO esperado, não só à marca nula

    [Fact]
    public async Task T7_a_obrigacao_aberta_nao_e_neutralizada_por_um_evento_posterior_e_a_recuperacao_encerra_o_item_do_atendimento_que_acabou()
    {
        var s = await MontarAsync("resync1");
        var antes = await LinhaAsync(s);

        // 1) A operação do item está pendente (atribuir ao Beto), e o atendimento da Ana ACABA por fora: humano -> IA. O evento é externo e chega com a
        // operação pendente: a projeção é ADIADA, de forma durável.
        await s.C.GravarIntencaoDeAssumirAsync(s.Item.Id, s.Beto.Id);
        await TransicionarAsync(s, HandlingMode.AiAutonomous, null);
        var versaoDaIa = await VersaoAsync(s);
        await s.C.RodarRelayAsync();
        var adiado = await LinhaAsync(s);
        Assert.Equal((versaoDaIa, true), (adiado.ResyncStateVersion, adiado.HasPending));

        // 2) A operação pendente termina SEM aplicar o que ficou por aplicar: a obrigação continua aberta.
        await DesfazerPendenciaAsync(s);
        Assert.True((await LinhaAsync(s)).NeedsResync);

        // 3) ANTES da recuperação outro atendimento começa por fora (IA -> humano, com a Dora: período 2). Com a obrigação aberta o evento posterior NÃO é
        // projetado normalmente: a marca sobe, o responsável continua sendo o da Ana e a versão observada não avançou (o caminho antigo espelhava a Dora no
        // atendimento da Ana e dava a obrigação por cumprida).
        await TransicionarAsync(s, HandlingMode.HumanAssisted, s.Dora.Id);
        var versaoDaDora = await VersaoAsync(s);
        await s.C.RodarRelayAsync();
        var aberto = await LinhaAsync(s);
        Assert.Equal((versaoDaDora, s.Ana.Id, antes.ObservedStateVersion, true), (aberto.ResyncStateVersion, aberto.AssignedUserId, aberto.ObservedStateVersion, aberto.NeedsResync));

        // 4) A recuperação alinha o item À SESSÃO, com a prova de identidade: o atendimento atual é o período 2 e o do item é o 1, que acabou. O item é
        // encerrado SEM fronteira (o fim dele não é o que o evento posterior trouxe), a obrigação é liquidada, e a Dora não é espelhada no item.
        Assert.True(await s.C.ReconciliarAsync() >= 1);
        var final = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Closed, "returned-to-ai", null, null), (final.Status, final.CloseReason, final.ClosedThroughMessageSeq, final.ResyncStateVersion));
        Assert.Null(final.AssignedUserId);
        Assert.Equal(await VersaoAsync(s), final.ObservedStateVersion);
        Assert.Contains((await s.C.HistoricoDoItemAsync(s.Item.Id))!, h => h.Kind == HistoryKind.ReturnedToAi && h.Reason == QueueItem.ExternalChangeReason);
    }

    /// <summary>
    /// A obrigação aberta com a versão observada JÁ alcançada: a operação pendente (atribuir ao Beto) conclui por "a sessão já estava no estado pedido" (sem efeito:
    /// não cobre a obrigação) enquanto o evento externo, do mesmo Beto, ficou adiado. A versão observada iguala a marca, e nada foi aplicado.
    /// </summary>
    private async Task<(Cena Cena, long Versao)> ObrigacaoComAVersaoObservadaAlcancadaAsync(string prefixo)
    {
        var s = await MontarAsync(prefixo);
        await s.C.GravarIntencaoDeAssumirAsync(s.Item.Id, s.Beto.Id);
        await TransicionarAsync(s, HandlingMode.HumanAssisted, s.Beto.Id); // o Beto passa a responder POR FORA (outro identificador)
        var versao = await VersaoAsync(s);
        await s.C.RodarRelayAsync();
        Assert.Equal(versao, (await LinhaAsync(s)).ResyncStateVersion); // adiado: há operação pendente

        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var item = await sp.GetRequiredService<IQueueItemRepository>().FindItemAsync(s.Item.Id, default);
            await sp.GetRequiredService<HandlingExecutor>().DriveAsync(item!, default);
            return 0;
        });
        return (s, versao);
    }

    [Fact]
    public async Task T7_a_versao_observada_que_alcanca_a_marca_nao_liquida_a_obrigacao_o_item_continua_precisando_de_alinhamento()
    {
        var (s, versao) = await ObrigacaoComAVersaoObservadaAlcancadaAsync("resync2");

        var linha = await LinhaAsync(s);
        Assert.Equal((versao, versao, false), (linha.ResyncStateVersion, linha.ObservedStateVersion, linha.HasPending));
        Assert.True(linha.NeedsResync); // a obrigação independe da versão observada
        await s.C.LimparPendenciasAsync(); // a obrigação aberta não termina sozinha: não deixa trabalho elegível no banco compartilhado
    }

    [Fact]
    public async Task T7_a_selecao_de_itens_continua_achando_o_item_com_a_versao_observada_igual_a_marca()
    {
        var (s, _) = await ObrigacaoComAVersaoObservadaAlcancadaAsync("resync3");

        var itens = await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IQueueItemRepository>().ListStaleProjectionsAsync(100, default));
        Assert.Contains(s.Item.Id, itens.Select(i => i.Id));
        await s.C.LimparPendenciasAsync();
    }

    [Fact]
    public async Task T7_a_descoberta_de_tenants_continua_achando_o_tenant_com_a_versao_observada_igual_a_marca()
    {
        var (s, _) = await ObrigacaoComAVersaoObservadaAlcancadaAsync("resync4");

        var depois = DateTimeOffset.UtcNow.AddDays(1);
        var tenants = await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IPendingWorkSource>().ListTenantsWithPendingAsync(depois, depois, 1000, default));
        Assert.Contains(s.C.Tenant.TenantId.Value, tenants);
        await s.C.LimparPendenciasAsync();
    }

    [Fact]
    public async Task T7_a_recuperacao_converge_ao_estado_da_sessao_liquida_explicitamente_e_so_entao_o_Worker_para_de_enxergar_o_item()
    {
        var (s, versao) = await ObrigacaoComAVersaoObservadaAlcancadaAsync("resync5");
        var depois = DateTimeOffset.UtcNow.AddDays(1);
        Task<IReadOnlyList<Guid>> Tenants() => s.C.ComoAsync(
            s.C.ContextoDeSistema, sp => sp.GetRequiredService<IPendingWorkSource>().ListTenantsWithPendingAsync(depois, depois, 1000, default));
        Assert.Contains(s.C.Tenant.TenantId.Value, await Tenants());

        Assert.True(await s.C.ReconciliarAsync() >= 1);

        // O estado esperado, com as identidades: o item espelha o que a sessão tem (o Beto, no período 1, na mesma colocação), a obrigação foi liquidada e a
        // versão observada é a da sessão. Só depois da liquidação a elegibilidade cai.
        var item = await LinhaAsync(s);
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((QueueItemStatus.Assigned, s.Beto.Id, 1L, null), (item.Status, item.AssignedUserId, item.HandlingPeriod, item.ResyncStateVersion));
        Assert.Equal((s.Beto.Id, 1L, versao), (sessao.ResponderRef, sessao.HandlingPeriod, sessao.StateVersion));
        Assert.Equal(sessao.StateVersion, item.ObservedStateVersion);
        Assert.False(item.NeedsResync);
        Assert.DoesNotContain(s.C.Tenant.TenantId.Value, await Tenants());
        Assert.Equal(0, await s.C.ReconciliarAsync()); // idempotente: uma segunda rodada não encontra nada
    }

    // A cobertura COMPROVADA: o recibo da própria operação, aplicada com a base completa, com as identidades conferidas

    [Fact]
    public async Task T7_a_operacao_aplicada_com_o_recibo_e_a_base_completa_cobre_a_obrigacao_cujo_evento_ela_ja_alcancou()
    {
        var s = await MontarAsync("resync7");

        // A operação do item está pendente (atribuir ao Dora) e, por fora, a Ana passa o atendimento ao Beto e o recebe de volta: o responsável e o período
        // da base (a Ana, período 1) valem de novo. Os dois eventos chegam com a operação pendente e ficam adiados (a marca é a versão do último).
        await s.C.GravarIntencaoDeAssumirAsync(s.Item.Id, s.Dora.Id);
        await TransicionarAsync(s, HandlingMode.HumanAssisted, s.Beto.Id);
        await TransicionarAsync(s, HandlingMode.HumanAssisted, s.Ana.Id);
        var versaoExterna = await VersaoAsync(s);
        await s.C.RodarRelayAsync();
        Assert.Equal(versaoExterna, (await LinhaAsync(s)).ResyncStateVersion);

        // O executor aplica a operação: a pré-condição (a Ana, período 1) confere, e o recibo é o da PRÓPRIA operação, com o efeito (a fronteira e o período 1
        // que a base confirma) e a versão MAIOR que a marca. Isso cobre a obrigação (o estado que a operação produziu já inclui os dois eventos).
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var item = await sp.GetRequiredService<IQueueItemRepository>().FindItemAsync(s.Item.Id, default);
            Assert.Equal(OperationOutcome.Completed, (await sp.GetRequiredService<HandlingExecutor>().DriveAsync(item!, default)).Outcome);
            return 0;
        });

        var linha = await LinhaAsync(s);
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.True(sessao.StateVersion > versaoExterna);
        Assert.Equal((QueueItemStatus.Assigned, s.Dora.Id, null, false), (linha.Status, linha.AssignedUserId, linha.ResyncStateVersion, linha.NeedsResync));
        Assert.Equal(sessao.StateVersion, linha.ObservedStateVersion);
        Assert.Equal(0, await s.C.ReconciliarAsync()); // nada ficou por alinhar
    }

    // T7 (legado sem prova): a liquidação NÃO VERIFICADA

    [Fact]
    public async Task T7_legado_sem_prova_liquida_como_nao_verificado_nao_espelha_nao_encerra_e_continua_sem_conteudo_nem_comando_depois_do_reinicio()
    {
        var s = await MontarAsync("resync6");

        // O item como os anteriores ao campo: o período do atendimento que ele representa é DESCONHECIDO.
        await ExecutarSqlAsync(s, $"UPDATE contact_center.queue_items SET handling_period = NULL WHERE id = '{s.Item.Id}'");

        // O Beto passa a ser o responsável por fora. Sem a identidade do item o evento não se projeta por inferência: a obrigação fica aberta.
        await TransicionarAsync(s, HandlingMode.HumanAssisted, s.Beto.Id);
        var versao = await VersaoAsync(s);
        await s.C.RodarRelayAsync();
        var adiado = await LinhaAsync(s);
        Assert.Equal((versao, s.Ana.Id, true), (adiado.ResyncStateVersion, adiado.AssignedUserId, adiado.NeedsResync));

        // A recuperação não consegue produzir a prova que falta (nem uma nova leitura a produz): a obrigação é liquidada como NÃO VERIFICADA. Nada é espelhado
        // (a Ana continua sendo o responsável do item), nada é encerrado, nenhuma identidade é preenchida, e a auditoria registra que não houve convergência.
        Assert.True(await s.C.ReconciliarAsync() >= 1);
        var liquidado = await LinhaAsync(s);
        Assert.Equal((QueueItemStatus.Assigned, s.Ana.Id, null, null), (liquidado.Status, liquidado.AssignedUserId, liquidado.ResyncStateVersion, liquidado.HandlingPeriod));
        Assert.Equal(1, await AuditoriasAsync(s, HandlingExecutor.ResyncUnverifiedOperation, AuditOutcome.Denied));
        Assert.Equal(0, await AuditoriasAsync(s, HandlingExecutor.ExternalChangeOperation, AuditOutcome.Success));

        // Depois do "reinício" (outra rodada, outro escopo): nada a refazer, o conteúdo continua fechado e o comando privilegiado continua negado.
        Assert.Equal(0, await s.C.ReconciliarAsync());
        Assert.Empty((await s.C.MensagensDoItemAsync(s.Item.Id, s.Beto.Contexto))!.Items);
        await Assert.ThrowsAsync<QueueItemConflictException>(() => s.C.AssumirAsync(s.Item.Id, s.Dora.Contexto));
        Assert.Equal(s.Beto.Id, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef); // nada foi escrito no Conversations
    }
}
