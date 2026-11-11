using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// A persistência da resposta por similaridade (ADR 0034) com PostgreSQL real (não precisa de pgvector: o índice vetorial tem testes próprios, que exigem a extensão): a configuração por tenant (criação,
/// concorrência, isolamento), o registro idempotente das decisões, sem nenhum texto de cliente, e as métricas por tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SemanticAnswerPersistenceTests(PostgresFixture banco)
{
    private static SemanticDecisionRecord Decisao(CenarioDeAgentes a, SemanticOutcome desfecho, Guid? turno = null, int embedding = 10, int busca = 5) =>
        new(a.Tenant.TenantId, turno ?? Guid.NewGuid(), DateTimeOffset.UtcNow, SemanticAnswerMode.Active, desfecho, 0, 0.95, 0.2, 1, embedding, busca, null);

    private static Task GravarAsync(CenarioDeAgentes a, SemanticDecisionRecord registro) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<ISemanticDecisionRecorder>().RecordAsync(registro, default);
            return 0;
        });

    [Fact]
    public async Task Sem_configuracao_o_tenant_esta_desligado_e_a_primeira_alteracao_cria_a_linha_auditada()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semcfg");

        var antes = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticAnswerSettingsService>().GetAsync());
        Assert.Equal((SemanticAnswerMode.Off, true), (antes.Mode, antes.IsDefault));

        var depois = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticAnswerSettingsService>().UpdateAsync(SemanticAnswerMode.Evaluate, 0.92, 0.06));
        Assert.Equal((SemanticAnswerMode.Evaluate, 0.92, 0.06, false), (depois.Mode, depois.MinScore, depois.MinMargin, depois.IsDefault));

        var lido = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticAnswerSettingsService>().GetAsync());
        Assert.Equal(SemanticAnswerMode.Evaluate, lido.Mode);
    }

    [Fact]
    public async Task A_configuracao_de_um_tenant_nunca_aparece_para_outro()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semiso-a");
        var b = await CenarioDeAgentes.CriarAsync(banco, "semiso-b");

        await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticAnswerSettingsService>().UpdateAsync(SemanticAnswerMode.Active, 0.95, 0.05));

        var deB = await b.C.ComoAsync(b.Admin, sp => sp.GetRequiredService<SemanticAnswerSettingsService>().GetAsync());
        Assert.Equal((SemanticAnswerMode.Off, true), (deB.Mode, deB.IsDefault));
    }

    [Fact]
    public async Task Duas_primeiras_configuracoes_ao_mesmo_tempo_uma_vence_e_a_outra_recebe_conflito()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semconc");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticAnswerSettingsService>().UpdateAsync(SemanticAnswerMode.Active, 0.9, 0.05));
                return true;
            }
            catch (AgentDefinitionConflictException)
            {
                return false;
            }
        }));

        Assert.Contains(true, resultados);
        var linhas = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().SemanticSettings.AsNoTracking().CountAsync());
        Assert.Equal(1, linhas);
    }

    [Fact]
    public async Task O_banco_recusa_limiar_inseguro_mesmo_sem_passar_pela_aplicacao()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semchk");
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "INSERT INTO agents.semantic_answer_settings (tenant_id, mode, min_score, min_margin, updated_at, revision) VALUES (@t, 2, 0.3, 0.05, now(), 1)", conexao);
        comando.Parameters.AddWithValue("t", a.Tenant.TenantId.Value);
        await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Decisao_repetida_do_mesmo_turno_conta_uma_vez_e_nada_de_texto_e_gravado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semrec");
        var turno = Guid.NewGuid();

        await GravarAsync(a, Decisao(a, SemanticOutcome.Answered, turno));
        await GravarAsync(a, Decisao(a, SemanticOutcome.NoCandidates, turno)); // reentrega: o primeiro registro vale

        var linhas = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().SemanticDecisions.AsNoTracking().ToListAsync());
        var unica = Assert.Single(linhas);
        Assert.Equal((SemanticOutcome.Answered, turno), (unica.Outcome, unica.TurnId));

        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var colunas = new NpgsqlCommand("SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_schema = 'agents' AND table_name = 'semantic_decisions'", conexao);
        var nomes = (string)(await colunas.ExecuteScalarAsync())!;
        Assert.DoesNotContain("text", nomes, StringComparison.OrdinalIgnoreCase); // so numeros e codigos
        Assert.DoesNotContain("message", nomes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gravar_decisao_de_outro_tenant_que_o_do_contexto_e_bloqueado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semxa");
        var b = await CenarioDeAgentes.CriarAsync(banco, "semxb");
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => GravarAsync(a, Decisao(b, SemanticOutcome.Answered)));
    }

    [Fact]
    public async Task Metricas_contam_so_o_tenant_do_contexto_e_calculam_a_latencia()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semmet-a");
        var b = await CenarioDeAgentes.CriarAsync(banco, "semmet-b");
        await GravarAsync(a, Decisao(a, SemanticOutcome.Answered, embedding: 20, busca: 10));
        await GravarAsync(a, Decisao(a, SemanticOutcome.NoCandidates, embedding: 40, busca: 20));
        await GravarAsync(b, Decisao(b, SemanticOutcome.Answered));
        await GravarAsync(b, Decisao(b, SemanticOutcome.Ambiguous));

        var deA = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticMetricsService>().GetAsync(7));
        Assert.Equal((2, 1, 1, 0), (deA.Decisions, deA.Answered, deA.NoCandidates, deA.Ambiguous));
        Assert.Equal(45.0, deA.LatencyAverageMilliseconds); // (30 + 60) / 2
        Assert.Equal(60, deA.LatencyP95Milliseconds);

        var deB = await b.C.ComoAsync(b.Admin, sp => sp.GetRequiredService<SemanticMetricsService>().GetAsync(7));
        Assert.Equal((2, 1, 0, 1), (deB.Decisions, deB.Answered, deB.NoCandidates, deB.Ambiguous));
    }

    [Fact]
    public async Task Leitura_da_versao_publicada_devolve_o_numero_e_o_conteudo_da_mesma_leitura()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semver");
        var publicada = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetPublishedAsync());
        Assert.NotNull(publicada);

        var lida = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<IAgentDefinitionReader>().GetPublishedVersionAsync(default));
        Assert.Equal(publicada!.Number, lida!.Number);
        Assert.Equal(publicada.Content.Instructions, lida.Content.Instructions);
    }
}
