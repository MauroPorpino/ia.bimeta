using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Uma conexão de integração do Embedded (ADR 0039): liga um ERP (ou outro sistema web) a UMA organização do tenant. Guarda o modo, a política de provisionamento, as origens permitidas do ERP (onde o painel
/// pode ser embutido e de onde o ticket pode ser trocado) e a situação. O tenant da conexão é o que toda chamada assinada com as chaves dela usa: nunca parâmetro de requisição.
/// </summary>
public sealed class IntegrationConnection : ITenantOwned
{
    public const int NameMaxLength = 120;
    public const int MaxOrigins = 10;
    public const int OriginMaxLength = 200;

    private IntegrationConnection(
        Guid id, TenantId tenantId, Guid organizationId, string name, EmbeddedMode mode, ProvisioningMode provisioning, string[] allowedOrigins,
        bool allowStartConversation, ConnectionStatus status, DateTimeOffset createdAt, Guid? createdByUserId, DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        OrganizationId = organizationId;
        Name = name;
        Mode = mode;
        Provisioning = provisioning;
        AllowedOrigins = allowedOrigins;
        AllowStartConversation = allowStartConversation;
        Status = status;
        CreatedAt = createdAt;
        CreatedByUserId = createdByUserId;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid OrganizationId { get; }

    public string Name { get; private set; }

    public EmbeddedMode Mode { get; private set; }

    public ProvisioningMode Provisioning { get; private set; }

    /// <summary>Origens normalizadas (<c>https://erp.empresa.com.br</c>), sem curinga, sem caminho.</summary>
    public string[] AllowedOrigins { get; private set; }

    /// <summary>Se o início ativo de conversas (ADR 0040) é permitido a partir desta integração. Nasce desligado.</summary>
    public bool AllowStartConversation { get; private set; }

    public ConnectionStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public Guid? CreatedByUserId { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == ConnectionStatus.Active;

    public static IntegrationConnection Create(
        TenantId tenantId, Guid organizationId, string? name, EmbeddedMode mode, ProvisioningMode provisioning, IEnumerable<string>? allowedOrigins,
        bool allowStartConversation, DateTimeOffset now, Guid? createdByUserId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (organizationId == Guid.Empty)
        {
            throw new IdentityValidationException("A conexão precisa de uma organização.");
        }

        ValidateEnums(mode, provisioning);
        return new IntegrationConnection(
            Guid.CreateVersion7(), tenantId, organizationId, NormalizeName(name), mode, provisioning, NormalizeOrigins(allowedOrigins), allowStartConversation,
            ConnectionStatus.Active, now, createdByUserId, now);
    }

    public void UpdateSettings(string? name, EmbeddedMode mode, ProvisioningMode provisioning, IEnumerable<string>? allowedOrigins, bool allowStartConversation, DateTimeOffset now)
    {
        ValidateEnums(mode, provisioning);
        Name = NormalizeName(name);
        Mode = mode;
        Provisioning = provisioning;
        AllowedOrigins = NormalizeOrigins(allowedOrigins);
        AllowStartConversation = allowStartConversation;
        UpdatedAt = now;
    }

    /// <summary>Devolve <c>false</c> se já estava nesta situação.</summary>
    public bool SetStatus(ConnectionStatus status, DateTimeOffset now)
    {
        if (Status == status)
        {
            return false;
        }

        Status = status;
        UpdatedAt = now;
        return true;
    }

    public bool IsOriginAllowed(string? origin) => TryNormalizeOrigin(origin, out var normalizada) && AllowedOrigins.Contains(normalizada, StringComparer.Ordinal);

    /// <summary>
    /// Normaliza uma origem web: <c>esquema://host[:porta]</c> em minúsculas, sem caminho, consulta, fragmento nem usuário. Só <c>https</c> (o painel exige HTTPS), exceto <c>http</c> para <c>localhost</c>, endereços de
    /// loopback e nomes <c>*.localhost</c> (desenvolvimento e testes). Sem curinga.
    /// </summary>
    public static bool TryNormalizeOrigin(string? texto, out string origin)
    {
        origin = string.Empty;
        var bruto = (texto ?? string.Empty).Trim();
        if (bruto.Length is 0 or > OriginMaxLength || bruto.Contains('*') || bruto.Any(char.IsWhiteSpace) || !Uri.TryCreate(bruto, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var semCaminho = uri.AbsolutePath is "" or "/" && !bruto.EndsWith("//", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !semCaminho || bruto.Contains('?') || bruto.Contains('#'))
        {
            return false;
        }

        var local = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        var esquemaOk = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && local);
        if (!esquemaOk || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // Uri.Authority já omite a porta padrão do esquema.
        origin = $"{uri.Scheme}://{uri.Authority}".ToLowerInvariant();
        return true;
    }

    private static string NormalizeName(string? name)
    {
        var nome = (name ?? string.Empty).Trim();
        return nome.Length is 0 or > NameMaxLength
            ? throw new IdentityValidationException($"O nome da conexão deve ter de 1 a {NameMaxLength} caracteres.")
            : nome;
    }

    private static string[] NormalizeOrigins(IEnumerable<string>? origens)
    {
        var lista = new List<string>();
        foreach (var item in origens ?? Array.Empty<string>())
        {
            if (!TryNormalizeOrigin(item, out var normalizada))
            {
                throw new IdentityValidationException("Cada origem deve ser https://host[:porta] (http só para localhost), sem caminho, consulta nem curinga.");
            }

            if (!lista.Contains(normalizada, StringComparer.Ordinal))
            {
                lista.Add(normalizada);
            }
        }

        return lista.Count is 0 or > MaxOrigins
            ? throw new IdentityValidationException($"Informe de 1 a {MaxOrigins} origens permitidas.")
            : lista.ToArray();
    }

    private static void ValidateEnums(EmbeddedMode mode, ProvisioningMode provisioning)
    {
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(provisioning))
        {
            throw new IdentityValidationException("Modo ou política de provisionamento inválidos.");
        }
    }
}
