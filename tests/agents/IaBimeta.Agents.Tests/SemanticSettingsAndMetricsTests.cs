using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Tests;

/// <summary>A configuracao semantica do tenant (permissao, auditoria sem conteudo, kill switch) e as metricas (so contagens e latencias).</summary>
public sealed class SemanticSettingsAndMetricsTests
{
    private sealed class FakeMetrics : ISemanticMetricsStore
    {
        public SemanticMetricsRaw Raw { get; set; } = new(new Dictionary<SemanticOutcome, int>(), 0, 0, 0, 0, []);

        public DateTimeOffset? Since { get; private set; }

        public Task<SemanticMetricsRaw> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken)
        {
            Since = since;
            return Task.FromResult(Raw);
        }
    }

    private static (SemanticAnswerSettingsService Service, FakeSemanticSettings Store, FakeAudit Audit) Build(RequestContextHolder? contexto = null)
    {
        var store = new FakeSemanticSettings();
        var audit = new FakeAudit();
        return (new SemanticAnswerSettingsService(contexto ?? TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage), store, audit, new FakeClock()), store, audit);
    }

    [Fact]
    public async Task Sem_configuracao_o_tenant_esta_desligado_com_os_limiares_padrao()
    {
        var (service, _, _) = Build();
        var v = await service.GetAsync();
        Assert.Equal((SemanticAnswerMode.Off, SemanticAnswerSettings.DefaultMinScore, SemanticAnswerSettings.DefaultMinMargin, true), (v.Mode, v.MinScore, v.MinMargin, v.IsDefault));
    }

    [Fact]
    public async Task Ligar_a_avaliacao_cria_a_configuracao_e_audita_o_modo_sem_conteudo()
    {
        var (service, store, audit) = Build();
        var v = await service.UpdateAsync(SemanticAnswerMode.Evaluate, 0.92, 0.06);
        Assert.Equal((SemanticAnswerMode.Evaluate, 0.92, false), (v.Mode, v.MinScore, v.IsDefault));
        Assert.Equal(SemanticAnswerMode.Evaluate, store.Current!.Mode);
        var entrada = Assert.Single(audit.Entries);
        Assert.Equal("agents.semantic-settings.mode-evaluate", entrada.Operation);
    }

    [Fact]
    public async Task Desligar_e_o_kill_switch_e_so_ajustar_limiar_audita_outra_operacao()
    {
        var (service, _, audit) = Build();
        await service.UpdateAsync(SemanticAnswerMode.Active, 0.9, 0.05);
        await service.UpdateAsync(SemanticAnswerMode.Active, 0.95, 0.05);
        await service.UpdateAsync(SemanticAnswerMode.Off, 0.95, 0.05);
        Assert.Equal(
            new[] { "agents.semantic-settings.mode-active", "agents.semantic-settings.thresholds-updated", "agents.semantic-settings.mode-off" },
            audit.Entries.Select(e => e.Operation).ToArray());
    }

    [Fact]
    public async Task Sem_a_permissao_nada_e_lido_nem_gravado()
    {
        var (service, store, audit) = Build(TestContexts.User(TestContexts.TenantA, Permission.ConversationRead));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.GetAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.UpdateAsync(SemanticAnswerMode.Active, 0.9, 0.05));
        Assert.Null(store.Current);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Limiar_inseguro_e_recusado_e_nada_e_auditado()
    {
        var (service, _, audit) = Build();
        await Assert.ThrowsAsync<AgentsValidationException>(() => service.UpdateAsync(SemanticAnswerMode.Active, 0.3, 0.05));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void Metricas_resumem_contagens_exatas_taxa_e_latencia()
    {
        var raw = new SemanticMetricsRaw(
            new Dictionary<SemanticOutcome, int> { [SemanticOutcome.Answered] = 6, [SemanticOutcome.NoCandidates] = 3, [SemanticOutcome.Ambiguous] = 1, [SemanticOutcome.Unavailable] = 2 },
            FaqHits: 10, SemanticHits: 6, ModelReplies: 4, Handoffs: 2, LatenciesMilliseconds: [10, 20, 30, 40, 50, 60, 70, 80, 90, 100]);

        var v = SemanticMetricsService.Summarize(7, raw);

        Assert.Equal((12, 6, 3, 1, 2), (v.Decisions, v.Answered, v.NoCandidates, v.Ambiguous, v.Unavailable));
        Assert.Equal(16, v.CommercialCallsAvoided); // FAQ + busca semantica: contagem exata
        Assert.Equal(30.0, v.SemanticShareOfAiReplies); // 6 de 20 respostas da IA
        Assert.Equal(55.0, v.LatencyAverageMilliseconds);
        Assert.Equal(100, v.LatencyP95Milliseconds);
    }

    [Fact]
    public void Metricas_sem_dados_nao_inventam_numero()
    {
        var v = SemanticMetricsService.Summarize(7, new SemanticMetricsRaw(new Dictionary<SemanticOutcome, int>(), 0, 0, 0, 0, []));
        Assert.Equal((0, 0.0), (v.CommercialCallsAvoided, v.SemanticShareOfAiReplies));
        Assert.Null(v.LatencyAverageMilliseconds);
        Assert.Null(v.LatencyP95Milliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task Janela_de_metricas_fora_do_intervalo_e_recusada(int dias)
    {
        var service = new SemanticMetricsService(TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage), new FakeMetrics(), new FakeClock());
        await Assert.ThrowsAsync<AgentsValidationException>(() => service.GetAsync(dias));
    }

    [Fact]
    public async Task Metricas_exigem_a_permissao_e_usam_a_janela_pedida()
    {
        var loja = new FakeMetrics();
        var clock = new FakeClock();
        await Assert.ThrowsAsync<AccessDeniedException>(() => new SemanticMetricsService(TestContexts.User(TestContexts.TenantA, Permission.ConversationRead), loja, clock).GetAsync(7));

        await new SemanticMetricsService(TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage), loja, clock).GetAsync(30);
        Assert.Equal(clock.GetUtcNow().AddDays(-30), loja.Since);
    }
}
