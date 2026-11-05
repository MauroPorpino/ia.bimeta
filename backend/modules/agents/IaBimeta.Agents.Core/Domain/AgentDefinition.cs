using System.Text.Json;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Domain;

/// <summary>A entrada de um FAQ do tenant: uma pergunta e a resposta aprovada.</summary>
public sealed record FaqEntry(string Question, string Answer);

/// <summary>Uma regra de formato ou de tamanho da definição do agente foi violada.</summary>
public sealed class AgentsValidationException : Exception
{
    public AgentsValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// O conteúdo de uma definição de agente publicável (ADR 0017, seção F): instruções do tenant, FAQ (pares pergunta e resposta), o texto da resposta informativa de áudio (MED-3) e o texto do
/// aviso de transferência. Cabe INTEIRO no prompt (sem busca), por isso tem limites de tamanho. NÃO é o Knowledge Engine: dado transacional (preço, agenda, cobertura, resultado) não entra, e
/// a IA transfere a pergunta.
/// </summary>
public sealed record AgentDefinitionContent(
    string Instructions,
    IReadOnlyList<FaqEntry> Faq,
    string? AudioInformativeText = null,
    string? HandoffNoticeText = null)
{
    public const int InstructionsMaxLength = 8000;
    public const int FaqMaxEntries = 50;
    public const int QuestionMaxLength = 300;
    public const int AnswerMaxLength = 1500;
    public const int FaqMaxTotalLength = 30000;
    public const int NoticeMaxLength = 600;

    /// <exception cref="AgentsValidationException">Algum limite foi violado.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Instructions) || Instructions.Length > InstructionsMaxLength)
        {
            throw new AgentsValidationException($"As instruções são obrigatórias e têm no máximo {InstructionsMaxLength} caracteres.");
        }

        if (Faq is null || Faq.Count > FaqMaxEntries)
        {
            throw new AgentsValidationException($"O FAQ aceita no máximo {FaqMaxEntries} entradas.");
        }

        var total = 0;
        foreach (var entrada in Faq)
        {
            if (entrada is null
                || string.IsNullOrWhiteSpace(entrada.Question) || entrada.Question.Length > QuestionMaxLength
                || string.IsNullOrWhiteSpace(entrada.Answer) || entrada.Answer.Length > AnswerMaxLength)
            {
                throw new AgentsValidationException(
                    $"Cada entrada do FAQ precisa de pergunta (até {QuestionMaxLength} caracteres) e de resposta (até {AnswerMaxLength}).");
            }

            total += entrada.Question.Length + entrada.Answer.Length;
        }

        if (total > FaqMaxTotalLength)
        {
            throw new AgentsValidationException($"O FAQ inteiro aceita no máximo {FaqMaxTotalLength} caracteres.");
        }

        ValidateOptionalText(AudioInformativeText, "A resposta informativa de áudio");
        ValidateOptionalText(HandoffNoticeText, "O aviso de transferência");
    }

    private static void ValidateOptionalText(string? texto, string nome)
    {
        if (texto is not null && (string.IsNullOrWhiteSpace(texto) || texto.Length > NoticeMaxLength))
        {
            throw new AgentsValidationException($"{nome}, quando informado, não pode ser vazio e tem no máximo {NoticeMaxLength} caracteres.");
        }
    }
}

public enum AgentDefinitionStatus : short
{
    /// <summary>O rascunho: só o tenant vê; o turno NUNCA o usa.</summary>
    Draft = 1,

    /// <summary>A versão publicada: a única que o turno usa. No máximo uma por tenant.</summary>
    Published = 2,

    /// <summary>Uma publicação anterior, substituída por outra. Fica guardada (histórico).</summary>
    Retired = 3,
}

/// <summary>
/// Uma versão da definição do agente do tenant, com rascunho e publicação (ADR 0017, seção F). No máximo um rascunho e uma publicada por tenant. O número da versão cresce a cada rascunho novo e
/// nunca é reaproveitado. Publicar é auditado e exige <c>TenantSettingsManage</c> (no serviço de aplicação).
/// </summary>
public sealed class AgentDefinitionVersion : ITenantOwned
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private AgentDefinitionVersion()
    {
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    /// <summary>O número da versão (1, 2, 3...), único por tenant.</summary>
    public int Number { get; private set; }

    public AgentDefinitionStatus Status { get; private set; }

    public string Instructions { get; private set; } = string.Empty;

    /// <summary>O FAQ em JSON (<c>jsonb</c>), lista de pares pergunta e resposta.</summary>
    public string FaqJson { get; private set; } = "[]";

    public string? AudioInformativeText { get; private set; }

    public string? HandoffNoticeText { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    /// <summary>A concorrência otimista: duas alterações do mesmo ponto de partida não se sobrescrevem em silêncio.</summary>
    public int Revision { get; private set; }

    public AgentDefinitionContent Content => new(
        Instructions,
        JsonSerializer.Deserialize<List<FaqEntry>>(FaqJson, Json) ?? new List<FaqEntry>(),
        AudioInformativeText,
        HandoffNoticeText);

    public static AgentDefinitionVersion CreateDraft(TenantId tenant, int number, AgentDefinitionContent content, DateTimeOffset now, Guid? userId)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(content);
        content.Validate();
        if (number < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        var versao = new AgentDefinitionVersion
        {
            TenantId = tenant,
            Id = Guid.NewGuid(),
            Number = number,
            Status = AgentDefinitionStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = userId,
            Revision = 1,
        };
        versao.Apply(content);
        return versao;
    }

    /// <summary>Troca o conteúdo do rascunho. Só o rascunho muda: uma versão publicada nunca é alterada (publicar de novo cria outra).</summary>
    public void ReplaceDraft(AgentDefinitionContent content, DateTimeOffset now, Guid? userId)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Status != AgentDefinitionStatus.Draft)
        {
            throw new AgentsValidationException("Só o rascunho pode ser alterado.");
        }

        content.Validate();
        Apply(content);
        UpdatedAt = now;
        UpdatedBy = userId;
        Revision++;
    }

    /// <summary>Publica o rascunho: passa a ser a versão que o turno usa.</summary>
    public void Publish(DateTimeOffset now, Guid? userId)
    {
        if (Status != AgentDefinitionStatus.Draft)
        {
            throw new AgentsValidationException("Só o rascunho pode ser publicado.");
        }

        Status = AgentDefinitionStatus.Published;
        PublishedAt = now;
        PublishedBy = userId;
        UpdatedAt = now;
        UpdatedBy = userId;
        Revision++;
    }

    /// <summary>A publicação anterior sai de uso quando outra é publicada.</summary>
    public void Retire(DateTimeOffset now)
    {
        if (Status != AgentDefinitionStatus.Published)
        {
            throw new AgentsValidationException("Só a versão publicada pode ser aposentada.");
        }

        Status = AgentDefinitionStatus.Retired;
        UpdatedAt = now;
        Revision++;
    }

    private void Apply(AgentDefinitionContent content)
    {
        Instructions = content.Instructions.Trim();
        FaqJson = JsonSerializer.Serialize(content.Faq.Select(f => new FaqEntry(f.Question.Trim(), f.Answer.Trim())).ToList(), Json);
        AudioInformativeText = content.AudioInformativeText?.Trim();
        HandoffNoticeText = content.HandoffNoticeText?.Trim();
    }
}
