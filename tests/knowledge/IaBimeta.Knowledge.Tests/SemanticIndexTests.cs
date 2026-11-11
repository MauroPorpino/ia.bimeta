using IaBimeta.Knowledge.Application;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Knowledge.Tests;

public sealed class SemanticIndexTests
{
    private static readonly TenantId A = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly TenantId B = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));

    private static SemanticSource Fonte(int versao = 1, string locale = "pt-BR", bool auto = true) => new(versao, locale,
    [
        new SemanticSourceText(0, SemanticTextKind.Question, 0, "Preciso de jejum para glicose?", auto),
        new SemanticSourceText(0, SemanticTextKind.Alias, 1, "tem que ficar sem comer para glicemia?", auto),
        new SemanticSourceText(1, SemanticTextKind.Question, 0, "Qual o horário de coleta?", auto),
    ]);

    private static (SemanticIndexService Service, SemanticIndexer Indexer, FakeStore Store, FakeEmbeddings Embeddings, FakeClock Clock, FakeAccessor Accessor) Build(Guid? tenant = null, Action<IaBimeta.Knowledge.Domain.KnowledgeSemanticOptions>? ajusta = null)
    {
        var store = new FakeStore();
        var embeddings = new FakeEmbeddings();
        var clock = new FakeClock();
        var accessor = FakeAccessor.For(tenant is null ? A : new TenantId(tenant.Value));
        var opcoes = Options.On();
        ajusta?.Invoke(opcoes);
        return (new SemanticIndexService(accessor, store, embeddings, opcoes), new SemanticIndexer(store, embeddings, opcoes, clock), store, embeddings, clock, accessor);
    }

    private static void Ensina(FakeEmbeddings e)
    {
        e.Know("Preciso de jejum para glicose?", 1, 0, 0);
        e.Know("tem que ficar sem comer para glicemia?", 0.9f, 0.1f, 0);
        e.Know("Qual o horário de coleta?", 0, 1, 0);
    }

    [Fact]
    public async Task Sync_grava_pendente_sem_chamar_o_provedor_e_a_vetorizacao_completa()
    {
        var (service, indexer, store, embeddings, _, _) = Build();
        Ensina(embeddings);

        var sync = await service.SyncAsync(Fonte());
        Assert.Equal(3, sync.Texts);
        Assert.Equal(3, sync.Pending);
        Assert.Empty(embeddings.Calls);

        var run = await indexer.RunOnceAsync();
        Assert.Equal(3, run.Embedded);
        Assert.All(store.Rows, r => Assert.Equal(2, r.Status));
        Assert.Single(embeddings.Calls);
        Assert.Equal(EmbeddingPurpose.Document, embeddings.Calls[0].Purpose);
    }

    [Fact]
    public async Task Sync_repetido_e_idempotente_e_reaproveita_vetores()
    {
        var (service, indexer, _, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        await indexer.RunOnceAsync();

        var segunda = await service.SyncAsync(Fonte(versao: 2));
        Assert.Equal(3, segunda.Unchanged);
        Assert.Equal(0, segunda.Pending);
        Assert.Equal(0, (await indexer.RunOnceAsync()).Claimed);
    }

    [Fact]
    public async Task Sync_remove_o_que_saiu_do_conhecimento_publicado()
    {
        var (service, indexer, store, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        await indexer.RunOnceAsync();

        var menor = new SemanticSource(2, "pt-BR", Fonte().Texts.Take(1).ToList());
        var resultado = await service.SyncAsync(menor);
        Assert.Equal(2, resultado.Removed);
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task Sync_ignora_textos_vazios_ou_longos_demais()
    {
        var (service, _, store, _, _, _) = Build();
        var fonte = new SemanticSource(1, "pt-BR",
        [
            new SemanticSourceText(0, SemanticTextKind.Question, 0, "  ", true),
            new SemanticSourceText(1, SemanticTextKind.Question, 0, new string('x', 501), true),
            new SemanticSourceText(2, SemanticTextKind.Question, 0, "  ok  ", true),
        ]);
        await service.SyncAsync(fonte);
        var linha = Assert.Single(store.Rows);
        Assert.Equal("ok", linha.Text);
    }

    [Fact]
    public async Task Desligado_nao_grava_nem_busca_nem_consulta_o_provedor()
    {
        var (service, indexer, store, embeddings, _, _) = Build(ajusta: o => o.Enabled = false);
        await service.SyncAsync(Fonte());
        Assert.Empty(store.Rows);
        Assert.Equal(SemanticSearchOutcome.Disabled, (await service.SearchAsync(new SemanticSearchRequest("jejum", "pt-BR", false))).Outcome);
        Assert.Equal(0, (await indexer.RunOnceAsync()).Claimed);
        Assert.Empty(embeddings.Calls);
    }

    [Fact]
    public async Task Busca_usa_proposito_de_consulta_e_devolve_o_melhor_texto_por_entrada()
    {
        var (service, indexer, _, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        await indexer.RunOnceAsync();
        embeddings.Know("jejum glicemia?", 0.95f, 0.05f, 0);

        var r = await service.SearchAsync(new SemanticSearchRequest("jejum glicemia?", "pt-BR", false));
        Assert.Equal(SemanticSearchOutcome.Found, r.Outcome);
        Assert.Equal(1, r.DefinitionNumber);
        Assert.Equal(0, r.Candidates[0].EntryOrdinal);
        Assert.Single(r.Candidates, c => c.EntryOrdinal == 0);
        Assert.Equal(EmbeddingPurpose.Query, embeddings.Calls[^1].Purpose);
    }

    [Fact]
    public async Task Busca_so_ve_o_tenant_do_contexto()
    {
        var (service, indexer, store, embeddings, _, accessor) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        await indexer.RunOnceAsync();

        accessor.Current = new RequestContext(B, "test");
        embeddings.Know("jejum?", 1, 0, 0);
        var r = await service.SearchAsync(new SemanticSearchRequest("jejum?", "pt-BR", false));
        Assert.NotEqual(SemanticSearchOutcome.Found, r.Outcome);
        Assert.Empty(r.Candidates);
        Assert.Equal(3, store.Rows.Count(x => x.Tenant == A));
    }

    [Fact]
    public async Task Busca_respeita_idioma_e_elegibilidade_de_resposta_automatica()
    {
        var (service, indexer, _, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte(locale: "en", auto: false));
        await indexer.RunOnceAsync();
        embeddings.Know("jejum?", 1, 0, 0);

        Assert.Empty((await service.SearchAsync(new SemanticSearchRequest("jejum?", "pt-BR", false))).Candidates);
        Assert.NotEmpty((await service.SearchAsync(new SemanticSearchRequest("jejum?", "en", false))).Candidates);
        Assert.Empty((await service.SearchAsync(new SemanticSearchRequest("jejum?", "en", true))).Candidates);
    }

    [Fact]
    public async Task Provedor_fora_do_ar_vira_indisponivel_sem_excecao()
    {
        var (service, _, _, embeddings, _, _) = Build();
        embeddings.Failure = new EmbeddingUnavailableException("unreachable", "x");
        var r = await service.SearchAsync(new SemanticSearchRequest("jejum?", "pt-BR", false));
        Assert.Equal(SemanticSearchOutcome.Unavailable, r.Outcome);
        Assert.Equal("unreachable", r.FailureCode);
    }

    [Fact]
    public async Task Indice_ainda_sem_vetores_e_indisponivel_e_nao_sem_candidatos()
    {
        var (service, _, _, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        embeddings.Know("jejum?", 1, 0, 0);
        var r = await service.SearchAsync(new SemanticSearchRequest("jejum?", "pt-BR", false));
        Assert.Equal(SemanticSearchOutcome.Unavailable, r.Outcome);
        Assert.Equal("index-not-ready", r.FailureCode);
    }

    [Fact]
    public async Task Falha_do_armazenamento_vira_indisponivel()
    {
        var (service, indexer, store, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        await indexer.RunOnceAsync();
        store.FailSearch = true;
        embeddings.Know("jejum?", 1, 0, 0);
        var r = await service.SearchAsync(new SemanticSearchRequest("jejum?", "pt-BR", false));
        Assert.Equal(SemanticSearchOutcome.Unavailable, r.Outcome);
        Assert.Equal("store-failed", r.FailureCode);
    }

    [Fact]
    public async Task Piso_de_candidato_descarta_textos_pouco_parecidos()
    {
        var (service, indexer, _, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        await indexer.RunOnceAsync();
        embeddings.Know("outra coisa", 0, 0, 1);
        var r = await service.SearchAsync(new SemanticSearchRequest("outra coisa", "pt-BR", false));
        Assert.Equal(SemanticSearchOutcome.NoCandidates, r.Outcome);
    }

    [Fact]
    public async Task Falha_do_provedor_agenda_nova_tentativa_com_espera_crescente_e_so_depois_fica_falha_definitiva()
    {
        var (service, indexer, store, embeddings, clock, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        embeddings.Failure = new EmbeddingUnavailableException("timeout", "x");

        var r1 = await indexer.RunOnceAsync();
        Assert.Equal(3, r1.Retried);
        Assert.All(store.Rows, r => Assert.Equal(1, r.Status));
        Assert.Equal(0, (await indexer.RunOnceAsync()).Claimed); // ainda dentro da espera

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal(3, (await indexer.RunOnceAsync()).Retried);
        clock.Now += TimeSpan.FromMinutes(5);
        var r3 = await indexer.RunOnceAsync();
        Assert.Equal(3, r3.Failed);
        Assert.All(store.Rows, r => { Assert.Equal(3, r.Status); Assert.Equal("timeout", r.Error); });

        // O provedor volta e uma nova publicação recoloca os falhos na fila.
        embeddings.Failure = null;
        await service.SyncAsync(Fonte(versao: 2));
        Assert.Equal(3, (await indexer.RunOnceAsync()).Embedded);
    }

    [Fact]
    public async Task Vetor_com_dimensao_errada_nunca_e_gravado()
    {
        var (service, indexer, store, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        embeddings.ForcedDimension = 4;
        var r = await indexer.RunOnceAsync();
        Assert.Equal(0, r.Embedded);
        Assert.All(store.Rows, x => Assert.Null(x.Vector));
        Assert.All(store.Rows, x => Assert.Equal("dimension-mismatch", x.Error));
    }

    [Fact]
    public async Task Troca_de_modelo_recoloca_os_textos_na_fila_e_nao_mistura_vetores()
    {
        var (service, indexer, store, embeddings, _, _) = Build();
        Ensina(embeddings);
        await service.SyncAsync(Fonte());
        await indexer.RunOnceAsync();

        var novas = Options.On();
        novas.ModelId = "fake-v2";
        var service2 = new SemanticIndexService(FakeAccessor.For(A), store, embeddings, novas);
        var indexer2 = new SemanticIndexer(store, embeddings, novas, new FakeClock());
        embeddings.Know("jejum?", 1, 0, 0);

        // Antes da nova vetorização o modelo novo não enxerga vetor algum.
        var antes = await service2.SearchAsync(new SemanticSearchRequest("jejum?", "pt-BR", false));
        Assert.Equal(SemanticSearchOutcome.Unavailable, antes.Outcome);

        Assert.Equal(3, (await indexer2.RunOnceAsync()).Embedded);
        Assert.All(store.Rows, r => Assert.Equal("fake-v2", r.Model));
        Assert.Equal(SemanticSearchOutcome.Found, (await service2.SearchAsync(new SemanticSearchRequest("jejum?", "pt-BR", false))).Outcome);
    }

    [Fact]
    public void Resumo_muda_com_texto_modelo_e_dimensao()
    {
        var baseHash = SemanticIndexService.HashOf("a", "m", 8);
        Assert.NotEqual(baseHash, SemanticIndexService.HashOf("b", "m", 8));
        Assert.NotEqual(baseHash, SemanticIndexService.HashOf("a", "n", 8));
        Assert.NotEqual(baseHash, SemanticIndexService.HashOf("a", "m", 16));
        Assert.Equal(baseHash, SemanticIndexService.HashOf("a", "m", 8));
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_a_operacao_falha_fechada()
    {
        var (service, _, _, _, _, accessor) = Build();
        accessor.Current = null;
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => service.SearchAsync(new SemanticSearchRequest("x", "pt-BR", false)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => service.SyncAsync(Fonte()));
    }

    [Fact]
    public async Task Gerenciador_Clear_remove_so_o_tenant_do_contexto()
    {
        var (service, _, store, _, _, accessor) = Build();
        await service.SyncAsync(Fonte());
        accessor.Current = new RequestContext(B, "test");
        await service.SyncAsync(Fonte());
        await service.ClearAsync();
        Assert.All(store.Rows, r => Assert.Equal(A, r.Tenant));
        Assert.Equal(3, store.Rows.Count);
    }
}
