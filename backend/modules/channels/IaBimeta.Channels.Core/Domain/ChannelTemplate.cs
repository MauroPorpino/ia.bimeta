using System.Text.RegularExpressions;
using IaBimeta.Channels.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>Um parâmetro de um modelo, na estrutura que o provedor aprovou.</summary>
/// <param name="Key">A chave estável (<c>header.1</c>, <c>body.2</c>, <c>body.nome</c>, <c>button.0</c>): é o que o atendente preenche e o que vai gravado com a mensagem.</param>
/// <param name="Component"><c>header</c>, <c>body</c> ou <c>button</c>.</param>
/// <param name="Index">A posição do marcador no texto (1, 2...) ou o índice do botão (0, 1...).</param>
/// <param name="Name">O nome nos modelos de parâmetro nomeado; nulo nos posicionais.</param>
/// <param name="Example">O exemplo que a Meta recebeu na aprovação, se houver.</param>
/// <param name="MaxLength">O tamanho máximo do valor.</param>
public sealed record TemplateParameterSpec(string Key, string Component, int Index, string? Name, string? Example, int MaxLength)
{
    public const string Header = "header";
    public const string Body = "body";
    public const string Button = "button";

    /// <summary>Um rótulo para o formulário: o nome do parâmetro nomeado ou "Variável n".</summary>
    public string Label => Name ?? (Component == Button ? $"Botão {Index + 1} (final do link)" : $"Variável {Index}");
}

/// <summary>
/// Um modelo de mensagem (template) que JÁ EXISTE na conta do provedor (na Meta), associado à conta do Channels: nome, idioma, categoria, situação e a estrutura de parâmetros. O Channels não cria nem aprova modelos
/// (isso é na Meta); só guarda o que foi aprovado para o atendente poder enviar, e o Sender o usa para montar a chamada. Um modelo é único por conta, nome e idioma. Só texto: cabeçalho de mídia, botões com
/// parâmetro próprio (copiar código) e a categoria de autenticação não são suportados e o cadastro os recusa.
/// </summary>
public sealed class ChannelTemplate : ITenantOwned
{
    public const int NameMaxLength = 512;
    public const int LanguageMaxLength = 16;
    public const int TextMaxLength = 1024;
    public const int MetaIdMaxLength = 64;

    /// <summary>O teto de parâmetros por modelo: o bloco de canal do Conversations guarda no máximo 32 pares, e três deles são do próprio modelo.</summary>
    public const int MaxParameters = 28;

    private static readonly Regex NamePattern = new("^[a-z0-9_]{1,512}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LanguagePattern = new("^[a-z]{2,3}(_[A-Za-z]{2,4})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private ChannelTemplate(
        Guid id, TenantId tenantId, Guid accountId, string name, string language, TemplateCategory category, TemplateStatus status, string? metaTemplateId,
        string? headerText, string bodyText, string? footerText, IReadOnlyList<TemplateParameterSpec> parameters, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        AccountId = accountId;
        Name = name;
        Language = language;
        Category = category;
        Status = status;
        MetaTemplateId = metaTemplateId;
        HeaderText = headerText;
        BodyText = bodyText;
        FooterText = footerText;
        Parameters = parameters;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid AccountId { get; }

    public string Name { get; }

    public string Language { get; }

    public TemplateCategory Category { get; private set; }

    public TemplateStatus Status { get; private set; }

    /// <summary>O identificador do modelo na Meta, se o administrador o informou (só referência).</summary>
    public string? MetaTemplateId { get; private set; }

    public string? HeaderText { get; private set; }

    public string BodyText { get; private set; }

    public string? FooterText { get; private set; }

    /// <summary>A estrutura de parâmetros, na ordem: cabeçalho, corpo, botões.</summary>
    public IReadOnlyList<TemplateParameterSpec> Parameters { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsSendable => Status == TemplateStatus.Approved;

    public static ChannelTemplate Create(
        TenantId tenantId, Guid accountId, string name, string language, TemplateCategory category, TemplateStatus status, string? metaTemplateId,
        string? headerText, string bodyText, string? footerText, IReadOnlyList<TemplateParameterSpec> parameters, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        Validate(accountId, name, language, category, status, metaTemplateId, bodyText, parameters);
        return new ChannelTemplate(Guid.CreateVersion7(), tenantId, accountId, name, language, category, status, metaTemplateId, headerText, bodyText, footerText, parameters.ToList(), now, now);
    }

    /// <summary>Atualiza o que a Meta pode ter mudado no mesmo modelo (nome e idioma identificam o modelo: não mudam).</summary>
    public void Refresh(TemplateCategory category, TemplateStatus status, string? metaTemplateId, string? headerText, string bodyText, string? footerText, IReadOnlyList<TemplateParameterSpec> parameters, DateTimeOffset now)
    {
        Validate(AccountId, Name, Language, category, status, metaTemplateId, bodyText, parameters);
        Category = category;
        Status = status;
        MetaTemplateId = metaTemplateId;
        HeaderText = headerText;
        BodyText = bodyText;
        FooterText = footerText;
        Parameters = parameters.ToList();
        UpdatedAt = now;
    }

    /// <summary>Muda só a situação (por exemplo, a Meta pausou ou desativou o modelo). Devolve se mudou.</summary>
    public bool SetStatus(TemplateStatus status, DateTimeOffset now)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ChannelsValidationException("A situação do modelo é inválida.");
        }

        if (Status == status)
        {
            return false;
        }

        Status = status;
        UpdatedAt = now;
        return true;
    }

    private static void Validate(
        Guid accountId, string name, string language, TemplateCategory category, TemplateStatus status, string? metaTemplateId, string bodyText, IReadOnlyList<TemplateParameterSpec> parameters)
    {
        if (accountId == Guid.Empty)
        {
            throw new ChannelsValidationException("O modelo precisa pertencer a uma conta.");
        }

        if (!NamePattern.IsMatch(name ?? string.Empty))
        {
            throw new ChannelsValidationException("O nome do modelo deve ter só letras minúsculas, números e sublinhado (até 512 caracteres).");
        }

        if (!LanguagePattern.IsMatch(language ?? string.Empty))
        {
            throw new ChannelsValidationException("O idioma do modelo é inválido (por exemplo, pt_BR ou en_US).");
        }

        if (!Enum.IsDefined(category) || category == TemplateCategory.Authentication)
        {
            throw new ChannelsValidationException("A categoria do modelo não é suportada (modelos de autenticação ainda não são enviados por aqui).");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ChannelsValidationException("A situação do modelo é inválida.");
        }

        if (metaTemplateId is { Length: > MetaIdMaxLength })
        {
            throw new ChannelsValidationException("O identificador do modelo na Meta é longo demais.");
        }

        if (string.IsNullOrWhiteSpace(bodyText) || bodyText.Length > TextMaxLength)
        {
            throw new ChannelsValidationException($"O corpo do modelo deve ter de 1 a {TextMaxLength} caracteres.");
        }

        if (parameters.Count > MaxParameters || parameters.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
        {
            throw new ChannelsValidationException("A estrutura de parâmetros do modelo é inválida.");
        }
    }
}
