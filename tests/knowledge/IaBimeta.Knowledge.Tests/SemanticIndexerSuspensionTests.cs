using IaBimeta.Knowledge.Application;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Knowledge.Tests;

/// <summary>BL-29 (ADR 0043): empresa suspensa não vai ao provedor de vetores; o adiamento não gasta tentativa nem apaga nada; as outras empresas seguem; a reativação retoma.</summary>
public sealed class SemanticIndexerSuspensionTests
{
    private static readonly TenantId A = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly TenantId B = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));

    private sealed class FakeGate : IIndexingTenantGate
    {
        public HashSet<TenantId> Suspended { get; } = [];

        public bool Broken { get; set; }

        public int Calls { get; private set; }

        public Task<bool> CanIndexAsync(TenantId tenant, CancellationToken cancellationToken)
        {
            Calls++;
            return Broken ? throw new InvalidOperationException("consulta indisponível") : Task.FromResult(!Suspended.Contains(tenant));
        }
    }

    private static SemanticSource Fonte() => new(1, "pt-BR",
    [
        new SemanticSourceText(0, SemanticTextKind.Question, 0, "Preciso de jejum para glicose?", true),
        new SemanticSourceText(1, SemanticTextKind.Question, 0, "Qual o horário de coleta?", true),
    ]);

    private static async Task<(SemanticIndexer Indexer, FakeStore Store, FakeEmbeddings Embeddings, FakeClock Clock, FakeGate Gate)> BuildAsync()
    {
        var store = new FakeStore();
        var embeddings = new FakeEmbeddings();
        var clock = new FakeClock();
        var gate = new FakeGate();
        var opcoes = Options.On();
        foreach (var tenant in new[] { A, B })
        {
            await new SemanticIndexService(FakeAccessor.For(tenant), store, embeddings, opcoes).SyncAsync(Fonte());
        }

        return (new SemanticIndexer(store, embeddings, opcoes, clock, gate), store, embeddings, clock, gate);
    }

    [Fact]
    public async Task Empresa_suspensa_nao_chega_ao_provedor_e_a_outra_segue()
    {
        var (indexer, store, embeddings, _, gate) = await BuildAsync();
        gate.Suspended.Add(A);

        var run = await indexer.RunOnceAsync();

        Assert.Equal(2, run.Deferred);
        Assert.Equal(2, run.Embedded);
        var chamadas = Assert.Single(embeddings.Calls);
        Assert.Equal(2, chamadas.Texts.Count);
        Assert.All(store.Rows.Where(r => r.Tenant == A), r => Assert.Equal(1, r.Status));
        Assert.All(store.Rows.Where(r => r.Tenant == B), r => Assert.Equal(2, r.Status));
    }

    [Fact]
    public async Task Adiamento_nao_gasta_tentativa_nem_registra_erro_e_preserva_os_textos()
    {
        var (indexer, store, embeddings, _, gate) = await BuildAsync();
        gate.Suspended.Add(A);
        gate.Suspended.Add(B);

        var run = await indexer.RunOnceAsync();

        Assert.Equal(4, run.Deferred);
        Assert.Equal(0, run.Embedded);
        Assert.Empty(embeddings.Calls);
        Assert.Equal(4, store.Rows.Count);
        Assert.All(store.Rows, r =>
        {
            Assert.Equal(1, r.Status);
            Assert.Equal(0, r.Attempts);
            Assert.Null(r.Error);
            Assert.Null(r.Claimed);
            Assert.NotNull(r.Next);
        });
    }

    [Fact]
    public async Task Suspensa_nao_e_reconsultada_a_cada_ciclo_e_a_reativacao_retoma_sem_reprocessar_o_que_ja_estava_pronto()
    {
        var (indexer, store, embeddings, clock, gate) = await BuildAsync();
        gate.Suspended.Add(A);
        await indexer.RunOnceAsync();
        var consultas = gate.Calls;

        // Dentro do adiamento nada é reservado: sem consulta nova, sem chamada ao provedor.
        clock.Now += TimeSpan.FromSeconds(30);
        var ocioso = await indexer.RunOnceAsync();
        Assert.Equal(0, ocioso.Claimed);
        Assert.Equal(consultas, gate.Calls);
        Assert.Single(embeddings.Calls);

        // Reativada e passado o adiamento: os textos pendentes são vetorizados; os de B (já prontos) não voltam.
        gate.Suspended.Clear();
        clock.Now += TimeSpan.FromMinutes(3);
        var retomada = await indexer.RunOnceAsync();
        Assert.Equal(2, retomada.Embedded);
        Assert.Equal(0, retomada.Deferred);
        Assert.All(store.Rows, r => Assert.Equal(2, r.Status));
        Assert.Equal(2, embeddings.Calls.Count);
    }

    [Fact]
    public async Task Consulta_da_situacao_indisponivel_impede_o_processamento_sem_gastar_tentativa()
    {
        var (indexer, store, embeddings, clock, gate) = await BuildAsync();
        gate.Broken = true;

        var run = await indexer.RunOnceAsync();

        Assert.Equal(4, run.Deferred);
        Assert.Equal(0, run.Embedded);
        Assert.Empty(embeddings.Calls);
        Assert.All(store.Rows, r =>
        {
            Assert.Equal(1, r.Status);
            Assert.Equal(0, r.Attempts);
        });

        // Voltando a consulta, tudo prossegue.
        gate.Broken = false;
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Equal(4, (await indexer.RunOnceAsync()).Embedded);
    }

    [Fact]
    public async Task Vetor_ja_gravado_de_empresa_suspensa_continua_intacto()
    {
        var (indexer, store, _, clock, gate) = await BuildAsync();
        await indexer.RunOnceAsync();
        Assert.All(store.Rows, r => Assert.Equal(2, r.Status));

        gate.Suspended.Add(A);
        clock.Now += TimeSpan.FromMinutes(5);
        await indexer.RunOnceAsync();

        Assert.All(store.Rows.Where(r => r.Tenant == A), r =>
        {
            Assert.Equal(2, r.Status);
            Assert.NotNull(r.Vector);
        });
    }

    [Fact]
    public async Task Sem_a_porta_registrada_a_empresa_e_considerada_operacional()
    {
        var store = new FakeStore();
        var embeddings = new FakeEmbeddings();
        var opcoes = Options.On();
        await new SemanticIndexService(FakeAccessor.For(A), store, embeddings, opcoes).SyncAsync(Fonte());

        var run = await new SemanticIndexer(store, embeddings, opcoes, new FakeClock()).RunOnceAsync();

        Assert.Equal(2, run.Embedded);
        Assert.Equal(0, run.Deferred);
    }
}
