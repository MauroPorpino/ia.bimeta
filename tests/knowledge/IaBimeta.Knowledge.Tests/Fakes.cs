using IaBimeta.Knowledge.Application;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.Knowledge.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Knowledge.Tests;

internal sealed class FakeAccessor : IRequestContextAccessor
{
    public RequestContext? Current { get; set; }

    public static FakeAccessor For(TenantId tenant) => new() { Current = new RequestContext(tenant, "test") };
}

/// <summary>Um provedor de embeddings determinístico: o vetor vem de um dicionário de textos conhecidos (ou de um vetor ortogonal novo). Registra as chamadas.</summary>
internal sealed class FakeEmbeddings : IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _known = new(StringComparer.Ordinal);
    private readonly int _dimension;

    public FakeEmbeddings(int dimension = 8)
    {
        _dimension = dimension;
    }

    public List<(IReadOnlyList<string> Texts, EmbeddingPurpose Purpose)> Calls { get; } = [];

    public EmbeddingUnavailableException? Failure { get; set; }

    public int? ForcedDimension { get; set; }

    public EmbeddingModelInfo Model => new("fake", _dimension);

    public void Know(string text, params float[] vector) => _known[text] = Pad(vector);

    private float[] Pad(float[] v)
    {
        var saida = new float[_dimension];
        Array.Copy(v, saida, Math.Min(v.Length, _dimension));
        return saida;
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default)
    {
        Calls.Add((texts, purpose));
        if (Failure is not null)
        {
            throw Failure;
        }

        var dim = ForcedDimension ?? _dimension;
        IReadOnlyList<float[]> vetores = texts.Select(t => _known.TryGetValue(t, out var v) ? v.Take(dim).Concat(new float[Math.Max(0, dim - v.Length)]).ToArray() : new float[dim]).ToList();
        return Task.FromResult(vetores);
    }
}

/// <summary>Um armazenamento em memória que honra o tenant e o modelo (só para testar a lógica do serviço; o pgvector real tem testes próprios, que exigem o banco com a extensão).</summary>
internal sealed class FakeStore : ISemanticStore
{
    internal sealed class Row
    {
        public required TenantId Tenant { get; init; }
        public int Entry { get; init; }
        public SemanticTextKind Kind { get; init; }
        public int Alias { get; init; }
        public int Definition { get; set; }
        public string Locale { get; set; } = "pt-BR";
        public bool Auto { get; set; }
        public string Text { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Model { get; set; } = "";
        public int Dim { get; set; }
        public int Status { get; set; } = 1;
        public float[]? Vector { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset? Next { get; set; }
        public DateTimeOffset? Claimed { get; set; }
        public string? Error { get; set; }
    }

    public List<Row> Rows { get; } = [];

    public bool FailSearch { get; set; }

    public Task<SemanticSyncResult> ReplaceAsync(TenantId tenant, SemanticSource source, string modelId, int dimension, Func<string, string> hash, CancellationToken cancellationToken)
    {
        var chaves = source.Texts.Select(t => (t.EntryOrdinal, t.Kind, t.AliasOrdinal)).ToHashSet();
        var removidos = Rows.RemoveAll(r => r.Tenant == tenant && !chaves.Contains((r.Entry, r.Kind, r.Alias)));
        var iguais = 0;
        foreach (var t in source.Texts)
        {
            var h = hash(t.Text);
            var existente = Rows.FirstOrDefault(r => r.Tenant == tenant && r.Entry == t.EntryOrdinal && r.Kind == t.Kind && r.Alias == t.AliasOrdinal);
            if (existente is null)
            {
                var reuso = Rows.FirstOrDefault(r => r.Tenant == tenant && r.Hash == h && r.Status == 2);
                Rows.Add(new Row { Tenant = tenant, Entry = t.EntryOrdinal, Kind = t.Kind, Alias = t.AliasOrdinal, Definition = source.DefinitionNumber, Locale = source.Locale, Auto = t.AutoReplyEligible, Text = t.Text, Hash = h, Model = modelId, Dim = dimension, Status = reuso is null ? 1 : 2, Vector = reuso?.Vector });
                continue;
            }

            existente.Definition = source.DefinitionNumber;
            existente.Locale = source.Locale;
            existente.Auto = t.AutoReplyEligible;
            existente.Text = t.Text;
            if (existente.Hash == h && existente.Status != 3)
            {
                iguais++;
                continue;
            }

            (existente.Hash, existente.Model, existente.Dim, existente.Status, existente.Vector, existente.Attempts) = (h, modelId, dimension, 1, null, 0);
        }

        return Task.FromResult(new SemanticSyncResult(source.Texts.Count, iguais, Rows.Count(r => r.Tenant == tenant && r.Status == 1), removidos));
    }

    public Task DeleteAllAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        Rows.RemoveAll(r => r.Tenant == tenant);
        return Task.CompletedTask;
    }

    public Task<SemanticIndexStatus> StatusAsync(TenantId tenant, string modelId, int dimension, CancellationToken cancellationToken)
    {
        var minhas = Rows.Where(r => r.Tenant == tenant).ToList();
        return Task.FromResult(new SemanticIndexStatus(
            minhas.Count == 0 ? null : minhas.Max(r => r.Definition), modelId, dimension,
            minhas.Count(r => r.Status == 2 && r.Model == modelId), minhas.Count(r => r.Status == 1 && r.Model == modelId), minhas.Count(r => r.Status == 3 && r.Model == modelId), minhas.Count(r => r.Model != modelId)));
    }

    public Task<int> RequeueOtherModelsAsync(int limit, string modelId, int dimension, Func<string, string> hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outros = Rows.Where(r => r.Model != modelId || r.Dim != dimension).Take(limit).ToList();
        foreach (var r in outros)
        {
            (r.Model, r.Dim, r.Hash, r.Status, r.Vector, r.Attempts, r.Next, r.Claimed) = (modelId, dimension, hash(r.Text), 1, null, 0, null, null);
        }

        return Task.FromResult(outros.Count);
    }

    public Task<IReadOnlyList<PendingSemanticText>> ClaimPendingAsync(int batch, string modelId, int dimension, TimeSpan lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var prontos = Rows.Where(r => r.Status == 1 && r.Model == modelId && r.Dim == dimension && (r.Next is null || r.Next <= now) && (r.Claimed is null || r.Claimed <= now)).Take(batch).ToList();
        foreach (var r in prontos)
        {
            r.Claimed = now + lease;
        }

        IReadOnlyList<PendingSemanticText> itens = prontos.Select(r => new PendingSemanticText(r.Tenant, r.Entry, r.Kind, r.Alias, r.Text, r.Hash, r.Attempts)).ToList();
        return Task.FromResult(itens);
    }

    private Row? Find(PendingSemanticText i) => Rows.FirstOrDefault(r => r.Tenant == i.Tenant && r.Entry == i.EntryOrdinal && r.Kind == i.Kind && r.Alias == i.AliasOrdinal);

    public Task<bool> CompleteAsync(PendingSemanticText item, float[] embedding, string modelId, int dimension, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var r = Find(item);
        if (r is null || r.Hash != item.TextHash || r.Status != 1)
        {
            return Task.FromResult(false);
        }

        (r.Status, r.Vector, r.Claimed, r.Next, r.Attempts) = (2, embedding, null, null, 0);
        return Task.FromResult(true);
    }

    public Task DeferAsync(PendingSemanticText item, DateTimeOffset nextAttempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var r = Find(item);
        if (r is not null && r.Status == 1 && r.Hash == item.TextHash)
        {
            (r.Claimed, r.Next) = (null, nextAttempt);
        }

        return Task.CompletedTask;
    }

    public Task FailAsync(PendingSemanticText item, string code, DateTimeOffset? nextAttempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var r = Find(item);
        if (r is not null)
        {
            (r.Attempts, r.Error, r.Claimed, r.Next, r.Status) = (r.Attempts + 1, code, null, nextAttempt, nextAttempt is null ? 3 : 1);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SemanticHit>> SearchAsync(TenantId tenant, float[] query, string locale, bool onlyAutoReply, string modelId, int dimension, int limit, CancellationToken cancellationToken)
    {
        if (FailSearch)
        {
            throw new InvalidOperationException("falha simulada");
        }

        IReadOnlyList<SemanticHit> acertos = Rows
            .Where(r => r.Tenant == tenant && r.Status == 2 && r.Model == modelId && r.Dim == dimension && r.Locale == locale && (!onlyAutoReply || r.Auto))
            .Select(r => new SemanticHit(r.Definition, r.Entry, r.Kind, r.Alias, Cosine(query, r.Vector!)))
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();
        return Task.FromResult(acertos);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Options
{
    public static KnowledgeSemanticOptions On(int dimension = 8) => new() { Enabled = true, ModelId = "fake", Dimension = dimension, MaxAttempts = 3, RetryBackoff = TimeSpan.FromSeconds(30), MaxRetryBackoff = TimeSpan.FromMinutes(2) };
}
