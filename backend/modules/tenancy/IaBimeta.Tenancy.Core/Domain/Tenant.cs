using System.Text.RegularExpressions;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Tenancy.Domain;

/// <summary>
/// Registro do tenant, a raiz da hierarquia (ARCHITECTURE.md, seção 3). Não pertence a um tenant: é dado da
/// plataforma, por isso não implementa <see cref="ITenantOwned"/> e não tem filtro global.
/// </summary>
public sealed class Tenant
{
    public const int SlugMinLength = 3;
    public const int SlugMaxLength = 40;
    public const int NameMaxLength = 120;
    public const int ReasonMaxLength = 500;

    /// <summary>O identificador técnico do tenant da plataforma (ADR 0043). Nenhuma empresa pode usá-lo.</summary>
    public const string PlatformSlug = PlatformTenant.Slug;

    /// <summary>
    /// Idioma padrão de um tenant cadastrado sem escolher um. É só o valor inicial da configuração: o tenant o troca quando
    /// quiser, e ele nunca diz o idioma de um cliente (a conversa tem o seu próprio idioma efetivo, no Conversations).
    /// </summary>
    public static readonly Locale FallbackDefaultLocale = Locale.Parse("pt-BR");

    private static readonly Regex SlugPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.Ordinal)
    {
        PlatformSlug, "platform", "plataforma", "admin", "administracao", "administrador", "api", "www", "bimeta", "iabimeta", "ia-bimeta", "bimeta-admin",
        "system", "sistema", "root", "health", "auth", "embed", "webhooks", "operations", "operacao", "suporte", "support",
    };

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private Tenant(TenantId id, string slug, string name, TenantStatus status, DateTimeOffset createdAt, Locale defaultLocale)
    {
        Id = id;
        Slug = slug;
        Name = name;
        Status = status;
        CreatedAt = createdAt;
        DefaultLocale = defaultLocale;
        Kind = TenantKind.Commercial;
    }

    public TenantId Id { get; }

    /// <summary>Identificador legível e único, em minúsculas (por exemplo, "lab-central"). Permanente: não muda depois do cadastro.</summary>
    public string Slug { get; }

    public string Name { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>O idioma padrão do tenant (BCP 47), configurável. Serve de último recurso quando nada melhor se sabe do cliente.</summary>
    public Locale DefaultLocale { get; private set; }

    /// <summary>Comercial (empresa contratante) ou da plataforma (tenant técnico dos operadores da BImeta).</summary>
    public TenantKind Kind { get; private set; }

    /// <summary>Razão social (dado cadastral da operação comercial).</summary>
    public string? LegalName { get; private set; }

    /// <summary>CNPJ, só dígitos (14). Opcional.</summary>
    public string? Document { get; private set; }

    public string? ContactName { get; private set; }

    public string? ContactEmail { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Quando a suspensão vigente começou (nulo se não está suspensa).</summary>
    public DateTimeOffset? SuspendedAt { get; private set; }

    /// <summary>Quando a operação foi reativada pela última vez. O que nasceu antes disso (turnos de IA, envios, mensagens recebidas na suspensão) não é processado automaticamente.</summary>
    public DateTimeOffset? ReactivatedAt { get; private set; }

    public static bool IsReservedSlug(string? slug) => ReservedSlugs.Contains((slug ?? string.Empty).Trim().ToLowerInvariant());

    /// <summary>A regra ÚNICA dos identificadores reservados para o cadastro COMERCIAL (todo caminho: a plataforma, o provisionamento operacional, o semeador). Só o tenant técnico usa <see cref="RegisterPlatform"/>.</summary>
    /// <exception cref="TenancyValidationException">O identificador é reservado.</exception>
    public static void EnsureNotReserved(string? slug)
    {
        if (IsReservedSlug(slug))
        {
            throw new TenancyValidationException("Este identificador é reservado e não pode ser usado por uma empresa.");
        }
    }

    public static Tenant Register(TenantId id, string slug, string name, DateTimeOffset now, Locale? defaultLocale = null)
    {
        ArgumentNullException.ThrowIfNull(id);

        var slugValido = (slug ?? string.Empty).Trim();
        if (slugValido.Length < SlugMinLength || slugValido.Length > SlugMaxLength || !SlugPattern.IsMatch(slugValido))
        {
            throw new TenancyValidationException(
                $"O slug do tenant deve ter de {SlugMinLength} a {SlugMaxLength} caracteres, só com letras minúsculas, números e hífens entre eles.");
        }

        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new TenancyValidationException($"O nome do tenant deve ter de 1 a {NameMaxLength} caracteres.");
        }

        return new Tenant(id, slugValido, nomeValido, TenantStatus.Active, now, defaultLocale ?? FallbackDefaultLocale);
    }

    /// <summary>
    /// Cadastra a empresa contratante em PROVISIONAMENTO: ela só passa a operar quando a plataforma conclui o provisionamento necessário. Recusa identificadores reservados.
    /// </summary>
    public static Tenant RegisterCommercial(TenantId id, string slug, string name, DateTimeOffset now, Locale? defaultLocale = null)
    {
        EnsureNotReserved(slug);

        var tenant = Register(id, slug, name, now, defaultLocale);
        tenant.Status = TenantStatus.Provisioning;
        return tenant;
    }

    /// <summary>Cria o tenant técnico da plataforma (único, garantido por índice parcial). Já nasce ativo.</summary>
    public static Tenant RegisterPlatform(TenantId id, string name, DateTimeOffset now)
    {
        var tenant = Register(id, PlatformSlug, name, now, null);
        tenant.Kind = TenantKind.Platform;
        return tenant;
    }

    public void ChangeDefaultLocale(Locale locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        DefaultLocale = locale;
    }

    /// <summary>Altera os dados cadastrais permitidos. O identificador técnico (slug) NÃO muda.</summary>
    public void UpdateCadastral(string? name, string? legalName, string? document, string? contactName, string? contactEmail, DateTimeOffset now)
    {
        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new TenancyValidationException($"O nome da empresa deve ter de 1 a {NameMaxLength} caracteres.");
        }

        var razao = Optional(legalName, 160, "A razão social");
        var contato = Optional(contactName, 120, "O nome do contato");

        string? email = null;
        if (!string.IsNullOrWhiteSpace(contactEmail))
        {
            email = contactEmail.Trim().ToLowerInvariant();
            if (email.Length > 254 || !EmailPattern.IsMatch(email))
            {
                throw new TenancyValidationException("O e-mail de contato não é válido.");
            }
        }

        string? cnpj = null;
        if (!string.IsNullOrWhiteSpace(document))
        {
            cnpj = new string(document.Where(char.IsDigit).ToArray());
            if (cnpj.Length != 14)
            {
                throw new TenancyValidationException("O CNPJ deve ter 14 dígitos.");
            }
        }

        Name = nomeValido;
        LegalName = razao;
        Document = cnpj;
        ContactName = contato;
        ContactEmail = email;
        UpdatedAt = now;
    }

    private static string? Optional(string? value, int max, string label)
    {
        var texto = (value ?? string.Empty).Trim();
        if (texto.Length == 0)
        {
            return null;
        }

        return texto.Length <= max ? texto : throw new TenancyValidationException($"{label} deve ter até {max} caracteres.");
    }

    /// <summary>Conclui o provisionamento: a empresa passa a poder operar. Só vale para uma empresa em provisionamento.</summary>
    public void CompleteProvisioning(DateTimeOffset now)
    {
        if (Status != TenantStatus.Provisioning)
        {
            throw new TenancyValidationException("Só uma empresa em provisionamento pode ter o provisionamento concluído.");
        }

        Status = TenantStatus.Active;
        UpdatedAt = now;
    }

    /// <summary>
    /// Suspende a operação (efetiva: login, sessões, Embedded e processamento automático deixam de valer). O tenant da plataforma nunca é suspenso.
    /// Devolve o registro da mudança de situação (motivo obrigatório).
    /// </summary>
    public TenantStatusChange SuspendWithReason(string? reason, Guid? actorUserId, DateTimeOffset now)
    {
        if (Kind == TenantKind.Platform)
        {
            throw new TenancyValidationException("O tenant técnico da plataforma não pode ser suspenso.");
        }

        if (Status != TenantStatus.Active)
        {
            throw new TenancyValidationException("Só uma empresa ativa pode ser suspensa.");
        }

        var motivo = RequireReason(reason);
        var mudanca = TenantStatusChange.Create(Id, Status, TenantStatus.Suspended, motivo, actorUserId, now);
        Status = TenantStatus.Suspended;
        SuspendedAt = now;
        UpdatedAt = now;
        return mudanca;
    }

    /// <summary>Reativa uma empresa suspensa (motivo obrigatório). Só muda a situação: as sessões revogadas continuam revogadas.</summary>
    public TenantStatusChange ReactivateWithReason(string? reason, Guid? actorUserId, DateTimeOffset now)
    {
        if (Status != TenantStatus.Suspended)
        {
            throw new TenancyValidationException("Só uma empresa suspensa pode ser reativada.");
        }

        var motivo = RequireReason(reason);
        var mudanca = TenantStatusChange.Create(Id, Status, TenantStatus.Active, motivo, actorUserId, now);
        Status = TenantStatus.Active;
        SuspendedAt = null;
        ReactivatedAt = now;
        UpdatedAt = now;
        return mudanca;
    }

    private static string RequireReason(string? reason)
    {
        var motivo = (reason ?? string.Empty).Trim();
        return motivo.Length is >= 5 and <= ReasonMaxLength
            ? motivo
            : throw new TenancyValidationException($"Informe o motivo, com 5 a {ReasonMaxLength} caracteres.");
    }

    public void Suspend()
    {
        Status = TenantStatus.Suspended;
    }

    public void Reactivate()
    {
        Status = TenantStatus.Active;
    }
}
