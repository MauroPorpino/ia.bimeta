using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Configurações do tenant do contexto (hoje, o idioma padrão). O tenant é o do <see cref="RequestContext"/>, nunca um
/// parâmetro (TEN-1). Ler é permitido a qualquer contexto do tenant, inclusive de sistema; alterar exige
/// <see cref="Permission.TenantSettingsManage"/>, que só a concessão de escopo do tenant tem.
/// </summary>
public sealed class TenantSettingsService : ITenantSettings
{
    private readonly ITenantRepository _tenants;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditWriter _audit;

    public TenantSettingsService(ITenantRepository tenants, IRequestContextAccessor context, IAuditWriter audit)
    {
        _tenants = tenants;
        _context = context;
        _audit = audit;
    }

    public async Task<Locale> GetDefaultLocaleAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var tenant = await _tenants.FindByIdAsync(contexto.TenantId, cancellationToken)
            ?? throw new TenancyResourceNotFoundException("Tenant");
        return tenant.DefaultLocale;
    }

    /// <exception cref="AccessDeniedException">Falta a permissão de alterar as configurações do tenant.</exception>
    /// <exception cref="TenancyValidationException">O idioma não é um identificador BCP 47 bem formado.</exception>
    public async Task<Locale> SetDefaultLocaleAsync(string? locale, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var recurso = contexto.TenantId.Value.ToString();
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.TenantSettingsManage, null,
            "tenancy.tenant.default-locale.set", "tenant", recurso, cancellationToken);

        if (!Locale.TryParse(locale, out var novo))
        {
            throw new TenancyValidationException("O idioma deve ser um identificador BCP 47 bem formado, como pt-BR, en-US, es-ES ou fr-FR.");
        }

        var tenant = await _tenants.FindByIdAsync(contexto.TenantId, cancellationToken)
            ?? throw new TenancyResourceNotFoundException("Tenant");
        if (tenant.DefaultLocale == novo)
        {
            return novo;
        }

        tenant.ChangeDefaultLocale(novo);
        await _tenants.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(new AuditEntry("tenancy.tenant.default-locale.set", "tenant", recurso, AuditOutcome.Success), cancellationToken);
        return novo;
    }
}
