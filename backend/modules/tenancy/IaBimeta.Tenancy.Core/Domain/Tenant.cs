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

    /// <summary>
    /// Idioma padrão de um tenant cadastrado sem escolher um. É só o valor inicial da configuração: o tenant o troca quando
    /// quiser, e ele nunca diz o idioma de um cliente (a conversa tem o seu próprio idioma efetivo, no Conversations).
    /// </summary>
    public static readonly Locale FallbackDefaultLocale = Locale.Parse("pt-BR");

    private static readonly Regex SlugPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private Tenant(TenantId id, string slug, string name, TenantStatus status, DateTimeOffset createdAt, Locale defaultLocale)
    {
        Id = id;
        Slug = slug;
        Name = name;
        Status = status;
        CreatedAt = createdAt;
        DefaultLocale = defaultLocale;
    }

    public TenantId Id { get; }

    /// <summary>Identificador legível e único, em minúsculas (por exemplo, "lab-central").</summary>
    public string Slug { get; }

    public string Name { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>O idioma padrão do tenant (BCP 47), configurável. Serve de último recurso quando nada melhor se sabe do cliente.</summary>
    public Locale DefaultLocale { get; private set; }

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

    public void ChangeDefaultLocale(Locale locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        DefaultLocale = locale;
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
