using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Cadastro de tenants. É operação de plataforma e, por isso, não tem rota HTTP: quem a chama é o código de
/// administração (e o semeador de desenvolvimento). Quando o Identity existir, a permissão de administrar
/// tenants será verificada aqui.
/// </summary>
public sealed class TenantRegistrationService
{
    private readonly ITenantRepository _tenants;
    private readonly TimeProvider _clock;

    public TenantRegistrationService(ITenantRepository tenants, TimeProvider clock)
    {
        _tenants = tenants;
        _clock = clock;
    }

    /// <summary>Cadastra o tenant com o idioma padrão inicial (<see cref="Tenant.FallbackDefaultLocale"/>).</summary>
    /// <exception cref="TenancyValidationException">Slug ou nome inválido.</exception>
    /// <exception cref="DuplicateTenantSlugException">O slug já está em uso.</exception>
    public Task<TenantInfo> RegisterAsync(string slug, string name, CancellationToken cancellationToken = default) =>
        RegisterAsync(slug, name, null, cancellationToken);

    /// <summary>Cadastra o tenant com o idioma padrão escolhido (<c>null</c> usa o inicial).</summary>
    /// <exception cref="TenancyValidationException">Slug ou nome inválido.</exception>
    /// <exception cref="DuplicateTenantSlugException">O slug já está em uso.</exception>
    public async Task<TenantInfo> RegisterAsync(string slug, string name, Locale? defaultLocale, CancellationToken cancellationToken = default)
    {
        var tenant = Tenant.Register(new TenantId(Guid.CreateVersion7()), slug, name, _clock.GetUtcNow(), defaultLocale);

        if (await _tenants.FindBySlugAsync(tenant.Slug, cancellationToken) is not null)
        {
            throw new DuplicateTenantSlugException(tenant.Slug);
        }

        await _tenants.AddAsync(tenant, cancellationToken);
        return tenant.ToInfo();
    }

    public async Task<bool> SuspendAsync(string slug, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants.FindBySlugAsync(slug, cancellationToken);
        if (tenant is null)
        {
            return false;
        }

        tenant.Suspend();
        await _tenants.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReactivateAsync(string slug, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants.FindBySlugAsync(slug, cancellationToken);
        if (tenant is null)
        {
            return false;
        }

        tenant.Reactivate();
        await _tenants.SaveChangesAsync(cancellationToken);
        return true;
    }
}
