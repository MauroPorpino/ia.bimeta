using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// A persistencia das sugestoes da IA local (ADR 0034) com PostgreSQL real: uma pendente por chave de deduplicacao (indice unico parcial), concorrencia otimista da decisao, isolamento entre tenants, o rascunho
/// versionado que a aceitacao produz e o expurgo por prazo SO das ja decididas (as pendentes esperam a revisao) e das decisoes semanticas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FaqSuggestionPersistenceTests(PostgresFixture banco)
{
    private static SuggestionPayload Payload(string pergunta = "Posso tomar cafe antes do exame?") => new(pergunta, "Pode, sem acucar.", ["cafe antes da coleta"], null, null, null, "duvida recorrente");

    private static FaqSuggestion Nova(CenarioDeAgentes a, string chave, FaqSuggestionKind tipo = FaqSuggestionKind.NewFaq, Guid? lacuna = null) =>
        FaqSuggestion.Create(a.Tenant.TenantId, tipo, lacuna, chave, Payload(), "qwen-teste", DateTimeOffset.UtcNow, null);

    private static Task<T> NoStoreAsync<T>(CenarioDeAgentes a, Func<IFaqSuggestionStore, Task<T>> acao) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => acao(sp.GetRequiredService<IFaqSuggestionStore>()));

    private static Task AdicionarAsync(CenarioDeAgentes a, FaqSuggestion sugestao) =>
        NoStoreAsync(a, async store =>
        {
            store.Add(sugestao);
            await store.SaveAsync(default);
            return 0;
        });

    private static async Task<int> ExecutarAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor);
        }

        return await comando.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Cria_le_e_o_conteudo_volta_igual_com_o_tenant_do_contexto()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "sugcria");
        var sugestao = Nova(a, $"gap:{Guid.NewGuid()}");
        await AdicionarAsync(a, sugestao);

        var lida = await NoStoreAsync(a, s => s.FindAsync(sugestao.Id, default));
        Assert.NotNull(lida);
        Assert.Equal((FaqSuggestionKind.NewFaq, FaqSuggestionStatus.Pending, "qwen-teste", 1), (lida!.Kind, lida.Status, lida.ModelId, lida.Revision));
        Assert.Equal("Posso tomar cafe antes do exame?", lida.Payload.Question);
        Assert.Equal(new[] { "cafe antes da coleta" }, lida.Payload.Aliases);
    }

    [Fact]
    public async Task Duas_pendentes_com_a_mesma_chave_o_banco_recusa_e_depois_de_decidida_a_chave_libera()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "sugkey");
        var chave = $"gap:{Guid.NewGuid()}";
        var primeira = Nova(a, chave);
        await AdicionarAsync(a, primeira);

        var ex = await Assert.ThrowsAsync<FaqSuggestionConflictException>(() => AdicionarAsync(a, Nova(a, chave)));
        Assert.Equal("suggestion-changed", ex.Code);

        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var store = sp.GetRequiredService<IFaqSuggestionStore>();
            var viva = await store.FindAsync(primeira.Id, default);
            viva!.Reject(DateTimeOffset.UtcNow, null);
            await store.SaveAsync(default);
            return 0;
        });

        await AdicionarAsync(a, Nova(a, chave)); // a anterior foi decidida: outra pendente com a mesma chave e permitida
        Assert.Equal(2, await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().FaqSuggestions.AsNoTracking().CountAsync()));
    }

    [Fact]
    public async Task Duas_decisoes_ao_mesmo_tempo_uma_vence_e_a_outra_recebe_conflito()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "sugconc");
        var sugestao = Nova(a, $"gap:{Guid.NewGuid()}");
        await AdicionarAsync(a, sugestao);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
                {
                    var store = sp.GetRequiredService<IFaqSuggestionStore>();
                    var viva = await store.FindAsync(sugestao.Id, default);
                    await Task.Delay(100);
                    viva!.Approve(DateTimeOffset.UtcNow, null, null);
                    await store.SaveAsync(default);
                    return 0;
                });
                return true;
            }
            catch (FaqSuggestionConflictException)
            {
                return false;
            }
        }));

        Assert.Equal(1, resultados.Count(r => r));
        var final = await NoStoreAsync(a, s => s.FindAsync(sugestao.Id, default));
        Assert.Equal((FaqSuggestionStatus.Approved, 2), (final!.Status, final.Revision));
    }

    [Fact]
    public async Task Uma_sugestao_de_um_tenant_nunca_aparece_para_outro()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "sugiso-a");
        var b = await CenarioDeAgentes.CriarAsync(banco, "sugiso-b");
        var sugestao = Nova(a, $"gap:{Guid.NewGuid()}");
        await AdicionarAsync(a, sugestao);

        Assert.Null(await NoStoreAsync(b, s => s.FindAsync(sugestao.Id, default)));
        Assert.Equal(0, await NoStoreAsync(b, s => s.CountAsync(null, null, default)));
        Assert.Equal(1, await NoStoreAsync(a, s => s.CountAsync(null, null, default)));
    }

    [Fact]
    public async Task O_banco_recusa_sugestao_pendente_com_data_de_decisao_e_versao_aplicada_sem_aceite()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "sugchk");
        const string cabecalho = "INSERT INTO agents.faq_suggestions (tenant_id, id, kind, status, dedupe_key, payload_json, model_id, created_at, decided_at, applied_draft_version, edits, revision) VALUES (@t, @i, 1, @s, @k, '{}', 'm', now(), ";
        await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(cabecalho + "now(), NULL, 0, 1)", ("t", a.Tenant.TenantId.Value), ("i", Guid.NewGuid()), ("s", (short)1), ("k", "k1")));
        await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(cabecalho + "now(), 3, 0, 1)", ("t", a.Tenant.TenantId.Value), ("i", Guid.NewGuid()), ("s", (short)3), ("k", "k2")));
    }

    [Fact]
    public async Task Expurgo_apaga_so_sugestoes_decididas_antigas_e_decisoes_semanticas_antigas_e_nunca_as_pendentes()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "sugret");
        var velhaRejeitada = Nova(a, "gap:" + Guid.NewGuid());
        var velhaPendente = Nova(a, "gap:" + Guid.NewGuid());
        var recenteRejeitada = Nova(a, "gap:" + Guid.NewGuid());
        foreach (var s in new[] { velhaRejeitada, velhaPendente, recenteRejeitada })
        {
            await AdicionarAsync(a, s);
        }

        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var store = sp.GetRequiredService<IFaqSuggestionStore>();
            foreach (var id in new[] { velhaRejeitada.Id, recenteRejeitada.Id })
            {
                (await store.FindAsync(id, default))!.Reject(DateTimeOffset.UtcNow, null);
            }

            await store.SaveAsync(default);
            return 0;
        });

        // Envelhece a decisao de duas e a criacao de uma delas e de uma pendente (a pendente nunca vence).
        await ExecutarAsync("UPDATE agents.faq_suggestions SET decided_at = now() - interval '200 days', created_at = now() - interval '200 days' WHERE id = @i", ("i", velhaRejeitada.Id));
        await ExecutarAsync("UPDATE agents.faq_suggestions SET created_at = now() - interval '200 days' WHERE id = @i", ("i", velhaPendente.Id));

        var tarefa = new FaqSuggestionRetentionTask(a.C.Banco.ServicosComAgentes.GetRequiredService<IServiceScopeFactory>());
        var corte = new RetentionPass(a.Tenant.TenantId.Value, [], 90, DateTimeOffset.UtcNow.AddDays(-90), "teste");
        var relatorio = await tarefa.RunAsync(corte, dryRun: true);
        Assert.Equal((1, 0), (relatorio.Eligible, relatorio.Deleted)); // dryRun so conta

        var real = await tarefa.RunAsync(corte, dryRun: false);
        Assert.Equal((1, 1), (real.Eligible, real.Deleted));

        var restantes = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().FaqSuggestions.AsNoTracking().Select(s => s.Id).ToListAsync());
        Assert.DoesNotContain(velhaRejeitada.Id, restantes);
        Assert.Contains(velhaPendente.Id, restantes);
        Assert.Contains(recenteRejeitada.Id, restantes);
    }

    [Fact]
    public async Task Expurgo_das_decisoes_semanticas_respeita_a_empresa_da_passada()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "semret-a");
        var b = await CenarioDeAgentes.CriarAsync(banco, "semret-b");
        foreach (var t in new[] { a, b })
        {
            await t.C.ComoAsync(t.C.ContextoDeSistema, async sp =>
            {
                await sp.GetRequiredService<ISemanticDecisionRecorder>().RecordAsync(
                    new SemanticDecisionRecord(t.Tenant.TenantId, Guid.NewGuid(), DateTimeOffset.UtcNow, SemanticAnswerMode.Active, SemanticOutcome.Answered, 0, 0.95, 0.2, 1, 5, 5, null), default);
                return 0;
            });
        }

        await ExecutarAsync("UPDATE agents.semantic_decisions SET decided_at = now() - interval '200 days' WHERE tenant_id = @t OR tenant_id = @u", ("t", a.Tenant.TenantId.Value), ("u", b.Tenant.TenantId.Value));

        var tarefa = new SemanticDecisionRetentionTask(a.C.Banco.ServicosComAgentes.GetRequiredService<IServiceScopeFactory>());
        var soA = new RetentionPass(a.Tenant.TenantId.Value, [], 90, DateTimeOffset.UtcNow.AddDays(-90), "so A");
        Assert.Equal(1, (await tarefa.RunAsync(soA, dryRun: false)).Deleted);

        Assert.Equal(0, await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().SemanticDecisions.IgnoreQueryFilters().CountAsync(d => d.TenantId == a.Tenant.TenantId)));
        Assert.Equal(1, await b.C.ComoAsync(b.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().SemanticDecisions.IgnoreQueryFilters().CountAsync(d => d.TenantId == b.Tenant.TenantId)));
    }
}
