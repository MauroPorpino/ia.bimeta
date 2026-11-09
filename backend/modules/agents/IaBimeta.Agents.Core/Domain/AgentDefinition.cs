using System.Text.Json;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Domain;

/// <summary>
/// A entrada de um FAQ do tenant: uma pergunta e a resposta aprovada. <paramref name="Aliases"/> são outras formas, cadastradas de propósito, de fazer a MESMA pergunta (ADR 0029); só servem à resposta
/// direta e nunca vão ao prompt. <paramref name="AutoReply"/> liga a resposta direta, sem chamar o modelo, quando a mensagem do cliente é IGUAL (depois de normalizada) à pergunta ou a um alias: é opt-in
/// por entrada e fica desligada nas definições que não a conhecem (JSON antigo sem o campo). Uma resposta escrita para orientar o modelo pode não servir, palavra por palavra, ao cliente: só marque
/// <c>AutoReply</c> numa resposta que se sustenta sozinha.
/// </summary>
public sealed record FaqEntry(string Question, string Answer, IReadOnlyList<string>? Aliases = null, bool AutoReply = false)
{
    private static readonly IReadOnlyList<string> NoAliases = Array.Empty<string>();

    /// <summary>Os aliases, nunca nulo (o JSON antigo não os tem).</summary>
    public IReadOnlyList<string> GetAliases() => Aliases ?? NoAliases;

    /// <summary>Igualdade por VALOR: sem aliases (nulo) é o mesmo que uma lista vazia, e a lista se compara pelos itens, na ordem (a lista em si é referência, e o JSON antigo não a tem).</summary>
    public bool Equals(FaqEntry? other) =>
        other is not null
        && string.Equals(Question, other.Question, StringComparison.Ordinal)
        && string.Equals(Answer, other.Answer, StringComparison.Ordinal)
        && AutoReply == other.AutoReply
        && GetAliases().SequenceEqual(other.GetAliases(), StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var codigo = new HashCode();
        codigo.Add(Question, StringComparer.Ordinal);
        codigo.Add(Answer, StringComparer.Ordinal);
        codigo.Add(AutoReply);
        foreach (var alias in GetAliases())
        {
            codigo.Add(alias, StringComparer.Ordinal);
        }

        return codigo.ToHashCode();
    }

    /// <summary>Os aliases aparados e sem repetição (repetição é a MESMA pergunta depois de normalizada), na ordem do cadastro: a forma em que a definição os guarda.</summary>
    public IReadOnlyList<string> GetCleanAliases()
    {
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        var limpos = new List<string>();
        foreach (var alias in GetAliases())
        {
            if (vistos.Add(FaqMatcher.Normalize(alias)))
            {
                limpos.Add(alias.Trim());
            }
        }

        return limpos;
    }
}

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
/// a IA transfere a pergunta. <paramref name="FaqLocale"/> é o idioma em que o FAQ está escrito (ADR 0029): a resposta direta só vale numa conversa cujo idioma efetivo é esse; vazio é <c>pt-BR</c>.
/// </summary>
public sealed record AgentDefinitionContent(
    string Instructions,
    IReadOnlyList<FaqEntry> Faq,
    string? AudioInformativeText = null,
    string? HandoffNoticeText = null,
    string? FaqLocale = null)
{
    public const int InstructionsMaxLength = 8000;
    public const int FaqMaxEntries = 50;
    public const int QuestionMaxLength = 300;
    public const int AnswerMaxLength = 1500;
    public const int FaqMaxTotalLength = 30000;
    public const int NoticeMaxLength = 600;

    /// <summary>Quantos aliases uma entrada aceita (ADR 0029). Eles não entram no prompt: o limite protege o armazenamento e a comparação.</summary>
    public const int AliasMaxCount = 10;

    /// <summary>O idioma do FAQ quando a definição não informa outro.</summary>
    public const string DefaultFaqLocale = "pt-BR";

    /// <summary>O idioma do FAQ já resolvido (o padrão quando não informado ou inválido; a validação recusa o inválido).</summary>
    public Locale EffectiveFaqLocale => FaqLocale is not null && Locale.TryParse(FaqLocale, out var idioma) ? idioma : Locale.Parse(DefaultFaqLocale);

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
            ValidateDirectAnswerFields(entrada);
        }

        if (total > FaqMaxTotalLength)
        {
            throw new AgentsValidationException($"O FAQ inteiro aceita no máximo {FaqMaxTotalLength} caracteres.");
        }

        if (FaqLocale is not null && !Locale.TryParse(FaqLocale, out _))
        {
            throw new AgentsValidationException("O idioma do FAQ, quando informado, deve ser um identificador BCP 47 bem formado, como pt-BR ou en-US.");
        }

        ValidateOptionalText(AudioInformativeText, "A resposta informativa de áudio");
        ValidateOptionalText(HandoffNoticeText, "O aviso de transferência");
    }

    /// <summary>Os campos da resposta direta (ADR 0029): aliases com texto de verdade, e uma entrada com resposta direta tem de poder ser encontrada.</summary>
    private static void ValidateDirectAnswerFields(FaqEntry entrada)
    {
        var aliases = entrada.GetAliases();
        if (aliases.Count > AliasMaxCount)
        {
            throw new AgentsValidationException($"Cada entrada do FAQ aceita no máximo {AliasMaxCount} aliases.");
        }

        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias) || alias.Length > QuestionMaxLength || FaqMatcher.Normalize(alias).Length == 0)
            {
                throw new AgentsValidationException($"Cada alias do FAQ precisa ter letras ou números e no máximo {QuestionMaxLength} caracteres.");
            }
        }

        if (entrada.AutoReply && FaqMatcher.Normalize(entrada.Question).Length == 0)
        {
            throw new AgentsValidationException("Uma entrada com resposta direta precisa de uma pergunta com letras ou números.");
        }
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

    /// <summary>O idioma do FAQ (BCP 47 canônico), ou nulo para o padrão <c>pt-BR</c> (as versões anteriores ao ADR 0029 não o têm).</summary>
    public string? FaqLocale { get; private set; }

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
        HandoffNoticeText,
        FaqLocale);

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
        FaqJson = JsonSerializer.Serialize(content.Faq.Select(f => new FaqEntry(f.Question.Trim(), f.Answer.Trim(), f.GetCleanAliases(), f.AutoReply)).ToList(), Json);
        AudioInformativeText = content.AudioInformativeText?.Trim();
        HandoffNoticeText = content.HandoffNoticeText?.Trim();
        FaqLocale = content.FaqLocale is null ? null : Locale.Parse(content.FaqLocale).ToString();
    }
}
