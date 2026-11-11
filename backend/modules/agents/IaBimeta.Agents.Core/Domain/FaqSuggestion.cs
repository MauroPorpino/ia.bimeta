using System.Text.Json;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Domain;

/// <summary>O tipo de uma sugestão da IA local (ADR 0034). A IA só PROPÕE: nenhuma sugestão publica nada.</summary>
public enum FaqSuggestionKind : short
{
    /// <summary>Uma entrada nova de FAQ (pergunta, aliases e, quando há resposta humana autorizada, a resposta generalizada), a partir de uma lacuna ou de um grupo de lacunas parecidas.</summary>
    NewFaq = 1,

    /// <summary>Aliases (outras formas de perguntar) para uma entrada que já existe.</summary>
    Alias = 2,

    /// <summary>Duas entradas do FAQ que parecem perguntar a mesma coisa: o administrador decide qual manter.</summary>
    Duplicate = 3,

    /// <summary>Duas entradas que parecem dar respostas contraditórias para a mesma situação: o administrador confere.</summary>
    Contradiction = 4,
}

public enum FaqSuggestionStatus : short
{
    Pending = 1,

    /// <summary>O administrador aceitou: o efeito (uma entrada ou alias no RASCUNHO) já foi aplicado, ou, para duplicata e contradição, ele só reconheceu o achado.</summary>
    Approved = 2,

    Rejected = 3,
}

/// <summary>
/// O conteúdo de uma sugestão. É o que o administrador revisa e EDITA antes de aceitar. Nunca guarda o texto original do cliente nem da conversa: a pergunta é a reformulação genérica que o modelo propôs, e o
/// modelo só viu o texto depois da redação de dados pessoais (<see cref="PiiRedactor"/>).
/// </summary>
public sealed record SuggestionPayload(
    string? Question,
    string? Answer,
    IReadOnlyList<string>? Aliases,
    string? TargetQuestion,
    string? OtherQuestion,
    IReadOnlyList<Guid>? GapIds,
    string? Rationale);

/// <summary>
/// Uma sugestão da IA local para o FAQ do tenant (ADR 0034). Chave <c>(tenant, id)</c>. Nasce <see cref="FaqSuggestionStatus.Pending"/> e só o administrador a decide, uma vez
/// (<see cref="Revision"/> dá a concorrência otimista). Aceitar aplica o efeito ao RASCUNHO (nunca publica); rejeitar não muda nada. A chave de deduplicação impede sugestões pendentes repetidas para a mesma
/// origem (a mesma lacuna, o mesmo par de entradas).
/// </summary>
public sealed class FaqSuggestion : ITenantOwned
{
    public const int PayloadMaxLength = 8000;

    public const int DedupeKeyMaxLength = 80;

    private FaqSuggestion()
    {
        TenantId = null!;
        PayloadJson = "{}";
        ModelId = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    public Guid Id { get; private set; }

    public FaqSuggestionKind Kind { get; private set; }

    public FaqSuggestionStatus Status { get; private set; }

    /// <summary>A lacuna de origem (a principal, se for um grupo), quando há.</summary>
    public Guid? GapId { get; private set; }

    /// <summary>Chave de deduplicação enquanto pendente (por exemplo, <c>gap:&lt;id&gt;</c>).</summary>
    public string DedupeKey { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; }

    /// <summary>O modelo local que gerou (rastreabilidade: um modelo trocado não reescreve a história).</summary>
    public string ModelId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    /// <summary>A versão do rascunho que recebeu o efeito, quando aceita e aplicada.</summary>
    public int? AppliedDraftVersion { get; private set; }

    /// <summary>Quantas vezes o administrador editou o conteúdo antes da decisão.</summary>
    public int Edits { get; private set; }

    public int Revision { get; private set; }

    public static FaqSuggestion Create(TenantId tenant, FaqSuggestionKind kind, Guid? gapId, string dedupeKey, SuggestionPayload payload, string modelId, DateTimeOffset now, Guid? by)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(dedupeKey) || dedupeKey.Length > DedupeKeyMaxLength || string.IsNullOrWhiteSpace(modelId) || modelId.Length > 100)
        {
            throw new AgentsValidationException("A sugestão é inválida.");
        }

        return new FaqSuggestion
        {
            TenantId = tenant,
            Id = Guid.CreateVersion7(),
            Kind = kind,
            Status = FaqSuggestionStatus.Pending,
            GapId = gapId,
            DedupeKey = dedupeKey,
            PayloadJson = Serialize(payload),
            ModelId = modelId,
            CreatedAt = now,
            CreatedBy = by,
            Revision = 1,
        };
    }

    public SuggestionPayload Payload => JsonSerializer.Deserialize<SuggestionPayload>(PayloadJson, Json) ?? new SuggestionPayload(null, null, null, null, null, null, null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Serialize(SuggestionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var texto = JsonSerializer.Serialize(payload, Json);
        if (texto.Length > PayloadMaxLength)
        {
            throw new AgentsValidationException("A sugestão é grande demais.");
        }

        return texto;
    }

    /// <exception cref="FaqSuggestionStateException">A sugestão já foi decidida.</exception>
    public void Edit(SuggestionPayload payload, DateTimeOffset now)
    {
        EnsurePending();
        PayloadJson = Serialize(payload);
        Edits++;
        Revision++;
    }

    /// <exception cref="FaqSuggestionStateException">A sugestão já foi decidida.</exception>
    public void Approve(DateTimeOffset now, Guid? by, int? appliedDraftVersion)
    {
        EnsurePending();
        Status = FaqSuggestionStatus.Approved;
        DecidedAt = now;
        DecidedBy = by;
        AppliedDraftVersion = appliedDraftVersion;
        Revision++;
    }

    /// <exception cref="FaqSuggestionStateException">A sugestão já foi decidida.</exception>
    public void Reject(DateTimeOffset now, Guid? by)
    {
        EnsurePending();
        Status = FaqSuggestionStatus.Rejected;
        DecidedAt = now;
        DecidedBy = by;
        Revision++;
    }

    private void EnsurePending()
    {
        if (Status != FaqSuggestionStatus.Pending)
        {
            throw new FaqSuggestionStateException("A sugestão já foi decidida.");
        }
    }
}

public sealed class FaqSuggestionStateException : Exception
{
    public FaqSuggestionStateException(string message)
        : base(message)
    {
    }
}
