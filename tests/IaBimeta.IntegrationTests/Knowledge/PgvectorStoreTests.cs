using IaBimeta.Knowledge.Application;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.Knowledge.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IaBimeta.IntegrationTests.Knowledge;

/// <summary>
/// Marca um teste que EXIGE um PostgreSQL com a extensão pgvector (ADR 0034). Fica ignorado quando <c>IABIMETA_TEST_PGVECTOR_CONNECTION</c> não está definida (o PostgreSQL local do Windows não tem pgvector).
/// Na homologação (VM com a imagem <c>pgvector/pgvector:pg17</c>) a variável aponta para um banco DESCARTÁVEL: os testes aplicam a migration do Knowledge e apagam as linhas dos tenants que criam. Nunca aponte para um banco de uso real.
/// </summary>
public sealed class PgvectorFactAttribute : FactAttribute
{
    public const string Variable = "IABIMETA_TEST_PGVECTOR_CONNECTION";

    public PgvectorFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Exige PostgreSQL com pgvector: defina {Variable} (banco descartável). Pendente para a homologação na VM.";
        }
    }
}

public sealed class PgvectorStoreTests : IAsyncLifetime
{
    private const int Dim = 1024;
    private const string Model = "qwen3-embedding:0.6b";

    private readonly TenantId _a = new(Guid.NewGuid());
    private readonly TenantId _b = new(Guid.NewGuid());
    private PostgresSemanticStore? _store;

    private static string? ConnectionString => Environment.GetEnvironmentVariable(PgvectorFactAttribute.Variable);

    private static string Hash(string text) => SemanticIndexService.HashOf(text, Model, Dim);

    private static float[] Vec(params (int Index, float Value)[] pares)
    {
        var v = new float[Dim];
        foreach (var (i, x) in pares)
        {
            v[i] = x;
        }

        var norma = (float)Math.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norma).ToArray();
    }

    private static SemanticSource Fonte(int versao = 1, string locale = "pt-BR", bool auto = true, params string[] textos) => new(versao, locale,
        textos.Select((t, i) => new SemanticSourceText(i, SemanticTextKind.Question, 0, t, auto)).ToList());

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var opcoes = new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__ef_migrations_history", KnowledgeDbContext.Schema)).Options;
        await using (var contexto = new KnowledgeDbContext(opcoes))
        {
            await contexto.Database.MigrateAsync();
        }

        _store = new PostgresSemanticStore(() => ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_store is null)
        {
            return;
        }

        await _store.DeleteAllAsync(_a, CancellationToken.None);
        await _store.DeleteAllAsync(_b, CancellationToken.None);
    }

    private async Task<PendingSemanticText> ClaimOneAsync(TenantId tenant)
    {
        var lote = await _store!.ClaimPendingAsync(1000, Model, Dim, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None);
        return lote.First(l => l.Tenant == tenant);
    }

    [PgvectorFact]
    public async Task Migration_cria_a_extensao_e_a_tabela()
    {
        await using var conexao = new NpgsqlConnection(ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("SELECT (SELECT count(*) FROM pg_extension WHERE extname = 'vector'), (SELECT count(*) FROM information_schema.tables WHERE table_schema = 'knowledge' AND table_name = 'semantic_texts')", conexao);
        await using var linha = await comando.ExecuteReaderAsync();
        await linha.ReadAsync();
        Assert.Equal(1, linha.GetInt64(0));
        Assert.Equal(1, linha.GetInt64(1));
    }

    [PgvectorFact]
    public async Task Ciclo_completo_sincroniza_reserva_grava_e_busca_por_cosseno()
    {
        var textos = new[] { "pergunta zero", "pergunta um" };
        var sync = await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, textos), Model, Dim, Hash, CancellationToken.None);
        Assert.Equal(2, sync.Pending);

        var lote = (await _store.ClaimPendingAsync(1000, Model, Dim, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None)).Where(l => l.Tenant == _a).ToList();
        Assert.Equal(2, lote.Count);
        foreach (var item in lote)
        {
            Assert.True(await _store.CompleteAsync(item, item.Text == "pergunta zero" ? Vec((0, 1)) : Vec((1, 1)), Model, Dim, DateTimeOffset.UtcNow, CancellationToken.None));
        }

        var acertos = await _store.SearchAsync(_a, Vec((0, 1), (1, 0.1f)), "pt-BR", false, Model, Dim, 5, CancellationToken.None);
        Assert.Equal(2, acertos.Count);
        Assert.Equal(0, acertos[0].EntryOrdinal);
        Assert.True(acertos[0].Score > 0.99);
        Assert.True(acertos[1].Score < 0.2);
    }

    [PgvectorFact]
    public async Task Busca_nunca_atravessa_tenants()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, "texto do A"), Model, Dim, Hash, CancellationToken.None);
        await _store.ReplaceAsync(_b, Fonte(1, "pt-BR", true, "texto do B"), Model, Dim, Hash, CancellationToken.None);
        foreach (var t in new[] { _a, _b })
        {
            await _store.CompleteAsync(await ClaimOneAsync(t), Vec((5, 1)), Model, Dim, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        var doA = await _store.SearchAsync(_a, Vec((5, 1)), "pt-BR", false, Model, Dim, 10, CancellationToken.None);
        var doB = await _store.SearchAsync(_b, Vec((5, 1)), "pt-BR", false, Model, Dim, 10, CancellationToken.None);
        Assert.Single(doA);
        Assert.Single(doB);

        await _store.DeleteAllAsync(_a, CancellationToken.None);
        Assert.Empty(await _store.SearchAsync(_a, Vec((5, 1)), "pt-BR", false, Model, Dim, 10, CancellationToken.None));
        Assert.Single(await _store.SearchAsync(_b, Vec((5, 1)), "pt-BR", false, Model, Dim, 10, CancellationToken.None));
    }

    [PgvectorFact]
    public async Task Sync_repetido_reaproveita_vetor_e_so_recoloca_o_texto_alterado()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, "igual", "muda"), Model, Dim, Hash, CancellationToken.None);
        foreach (var item in (await _store.ClaimPendingAsync(1000, Model, Dim, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None)).Where(l => l.Tenant == _a))
        {
            await _store.CompleteAsync(item, Vec((2, 1)), Model, Dim, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        var segunda = await _store.ReplaceAsync(_a, Fonte(2, "pt-BR", true, "igual", "mudou"), Model, Dim, Hash, CancellationToken.None);
        Assert.Equal(1, segunda.Unchanged);
        Assert.Equal(1, segunda.Pending);
        var status = await _store.StatusAsync(_a, Model, Dim, CancellationToken.None);
        Assert.Equal(2, status.DefinitionNumber);
        Assert.Equal(1, status.Ready);
    }

    [PgvectorFact]
    public async Task Sync_remove_o_que_saiu_da_publicacao()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, "um", "dois", "tres"), Model, Dim, Hash, CancellationToken.None);
        var depois = await _store.ReplaceAsync(_a, Fonte(2, "pt-BR", true, "um"), Model, Dim, Hash, CancellationToken.None);
        Assert.Equal(2, depois.Removed);
    }

    [PgvectorFact]
    public async Task Busca_filtra_idioma_e_elegibilidade_de_resposta_automatica()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "en", false, "only manual"), Model, Dim, Hash, CancellationToken.None);
        await _store.CompleteAsync(await ClaimOneAsync(_a), Vec((3, 1)), Model, Dim, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(await _store.SearchAsync(_a, Vec((3, 1)), "pt-BR", false, Model, Dim, 5, CancellationToken.None));
        Assert.Single(await _store.SearchAsync(_a, Vec((3, 1)), "en", false, Model, Dim, 5, CancellationToken.None));
        Assert.Empty(await _store.SearchAsync(_a, Vec((3, 1)), "en", true, Model, Dim, 5, CancellationToken.None));
    }

    [PgvectorFact]
    public async Task Troca_de_modelo_recoloca_na_fila_e_o_modelo_antigo_nao_e_buscado()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, "texto"), Model, Dim, Hash, CancellationToken.None);
        await _store.CompleteAsync(await ClaimOneAsync(_a), Vec((4, 1)), Model, Dim, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(await _store.SearchAsync(_a, Vec((4, 1)), "pt-BR", false, "outro-modelo", Dim, 5, CancellationToken.None));
        var recolocados = await _store.RequeueOtherModelsAsync(1000, "outro-modelo", Dim, t => SemanticIndexService.HashOf(t, "outro-modelo", Dim), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(recolocados >= 1);
        var status = await _store.StatusAsync(_a, "outro-modelo", Dim, CancellationToken.None);
        Assert.Equal(1, status.Pending);
        Assert.Equal(0, status.Ready);

        // Volta ao modelo original para não deixar linhas de outro modelo no banco de teste.
        await _store.DeleteAllAsync(_a, CancellationToken.None);
    }

    [PgvectorFact]
    public async Task Duas_reservas_simultaneas_nunca_pegam_o_mesmo_texto()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, Enumerable.Range(0, 40).Select(i => $"texto {i}").ToArray()), Model, Dim, Hash, CancellationToken.None);
        var agora = DateTimeOffset.UtcNow;
        var lotes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _store.ClaimPendingAsync(15, Model, Dim, TimeSpan.FromMinutes(1), agora, CancellationToken.None)));
        var meus = lotes.SelectMany(l => l).Where(l => l.Tenant == _a).Select(l => l.EntryOrdinal).ToList();
        Assert.Equal(meus.Count, meus.Distinct().Count());
    }

    [PgvectorFact]
    public async Task Falha_com_nova_tentativa_adia_e_sem_nova_tentativa_fica_falha()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, "texto"), Model, Dim, Hash, CancellationToken.None);
        var item = await ClaimOneAsync(_a);
        var agora = DateTimeOffset.UtcNow;
        await _store.FailAsync(item, "timeout", agora.AddMinutes(5), agora, CancellationToken.None);
        Assert.DoesNotContain(await _store.ClaimPendingAsync(1000, Model, Dim, TimeSpan.FromMinutes(1), agora, CancellationToken.None), l => l.Tenant == _a);

        await _store.FailAsync(item, "timeout", null, agora, CancellationToken.None);
        var status = await _store.StatusAsync(_a, Model, Dim, CancellationToken.None);
        Assert.Equal(1, status.Failed);
    }

    [PgvectorFact]
    public async Task Adiar_libera_a_reserva_sem_gastar_tentativa_e_o_texto_volta_depois_do_adiamento()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, "texto"), Model, Dim, Hash, CancellationToken.None);
        var item = await ClaimOneAsync(_a);
        var agora = DateTimeOffset.UtcNow;

        await _store.DeferAsync(item, agora.AddMinutes(2), agora, CancellationToken.None);

        Assert.DoesNotContain(await _store.ClaimPendingAsync(1000, Model, Dim, TimeSpan.FromMinutes(1), agora, CancellationToken.None), l => l.Tenant == _a);
        var depois = (await _store.ClaimPendingAsync(1000, Model, Dim, TimeSpan.FromMinutes(1), agora.AddMinutes(3), CancellationToken.None)).Where(l => l.Tenant == _a).ToList();
        Assert.Single(depois);
        Assert.Equal(item.Attempts, depois[0].Attempts);
        var status = await _store.StatusAsync(_a, Model, Dim, CancellationToken.None);
        Assert.Equal(0, status.Failed);
    }

    [PgvectorFact]
    public async Task Gravar_vetor_de_texto_que_mudou_e_rejeitado()
    {
        await _store!.ReplaceAsync(_a, Fonte(1, "pt-BR", true, "antigo"), Model, Dim, Hash, CancellationToken.None);
        var item = await ClaimOneAsync(_a);
        await _store.ReplaceAsync(_a, Fonte(2, "pt-BR", true, "novo"), Model, Dim, Hash, CancellationToken.None);
        Assert.False(await _store.CompleteAsync(item, Vec((0, 1)), Model, Dim, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [PgvectorFact]
    public async Task Restricao_do_banco_recusa_pronto_sem_vetor()
    {
        await using var conexao = new NpgsqlConnection(ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "INSERT INTO knowledge.semantic_texts (tenant_id, entry_ordinal, kind, alias_ordinal, definition_number, locale, auto_reply, text, text_hash, model_id, dimension, status, created_at, updated_at) VALUES (@t, 0, 1, 0, 1, 'pt-BR', true, 'x', 'h', 'm', 1024, 2, now(), now())", conexao);
        comando.Parameters.AddWithValue("t", _a.Value);
        await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
    }
}
