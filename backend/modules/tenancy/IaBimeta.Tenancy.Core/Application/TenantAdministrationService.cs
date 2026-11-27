using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>A empresa como a plataforma a vê (dados cadastrais e situação; nunca dados operacionais do cliente).</summary>
public sealed record CompanyView(
    Guid TenantId,
    string Slug,
    string Name,
    string Status,
    string? LegalName,
    string? Document,
    string? ContactName,
    string? ContactEmail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? SuspendedAt);

public sealed record CompanyPage(IReadOnlyList<CompanyView> Items, int Total, int Page, int PageSize);

public sealed record StatusChangeView(DateTimeOffset ChangedAt, string FromStatus, string ToStatus, string Reason, Guid? ActorUserId);

/// <summary>
/// Administração das empresas contratantes pela plataforma BImeta (Etapa 02, ADR 0043): cadastro, dados cadastrais, conclusão do provisionamento,
/// suspensão e reativação. Cada operação é ESPECÍFICA, exige a permissão de plataforma correspondente num contexto de USUÁRIO do tenant técnico
/// da plataforma (nunca de sistema, nunca de uma empresa) e entra na auditoria da plataforma. Não há executor genérico.
/// </summary>
public sealed class TenantAdministrationService
{
    public const int MaxPageSize = 100;

    private readonly ITenantRepository _tenants;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;

    public TenantAdministrationService(ITenantRepository tenants, IRequestContextAccessor context, IAuditWriter audit, TimeProvider clock)
    {
        _tenants = tenants;
        _context = context;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Exige: contexto de USUÁRIO, do tenant técnico da plataforma, com a permissão. Qualquer outro contexto (sistema, empresa contratante, sessão embutida) é negado
    /// e a negação é auditada no tenant do contexto.
    /// </summary>
    public async Task<RequestContext> RequirePlatformAsync(Permission permission, string operation, string? resourceId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var plataforma = contexto.Principal is not null
            && PlatformPermissions.IsPlatform(permission)
            && await _tenants.FindByIdAsync(contexto.TenantId, cancellationToken) is { Kind: TenantKind.Platform };

        if (!plataforma || !contexto.IsAllowed(permission, null))
        {
            await _audit.WriteAsync(new AuditEntry(operation, "tenant", resourceId, AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(permission);
        }

        return contexto;
    }

    public async Task<CompanyPage> ListAsync(string? search, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await RequirePlatformAsync(Permission.PlatformTenantRead, "platform.tenant.list", null, cancellationToken);
        var pagina = Math.Max(1, page);
        var tamanho = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, MaxPageSize);
        TenantStatus? situacao = ParseStatus(status);
        var (itens, total) = await _tenants.ListCommercialAsync(search, situacao, (pagina - 1) * tamanho, tamanho, cancellationToken);
        return new CompanyPage(itens.Select(ToView).ToList(), total, pagina, tamanho);
    }

    public async Task<IReadOnlyList<CompanyView>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await RequirePlatformAsync(Permission.PlatformOverviewRead, "platform.overview.read", null, cancellationToken);
        return (await _tenants.ListAllCommercialAsync(cancellationToken)).Select(ToView).ToList();
    }

    public async Task<CompanyView> GetAsync(string slug, CancellationToken cancellationToken = default)
    {
        await RequirePlatformAsync(Permission.PlatformTenantRead, "platform.tenant.read", slug, cancellationToken);
        return ToView(await FindCommercialAsync(slug, cancellationToken));
    }

    public async Task<IReadOnlyList<StatusChangeView>> GetStatusHistoryAsync(string slug, CancellationToken cancellationToken = default)
    {
        await RequirePlatformAsync(Permission.PlatformTenantRead, "platform.tenant.history", slug, cancellationToken);
        var tenant = await FindCommercialAsync(slug, cancellationToken);
        var mudancas = await _tenants.ListStatusChangesAsync(tenant.Id, 50, cancellationToken);
        return mudancas.Select(m => new StatusChangeView(m.ChangedAt, m.FromStatus.ToString(), m.ToStatus.ToString(), m.Reason, m.ActorUserId)).ToList();
    }

    /// <summary>
    /// Cadastra a empresa em PROVISIONAMENTO. Repetível: se o identificador já é de uma empresa ainda em provisionamento (cadastro anterior interrompido), devolve-a para a
    /// plataforma retomar; se é de uma empresa já provisionada, ou reservado, é recusado.
    /// </summary>
    /// <returns>A empresa e se ela foi criada agora (<c>false</c> = retomada).</returns>
    public async Task<(CompanyView Company, bool Created)> RegisterAsync(
        string? slug, string? name, string? legalName, string? document, string? contactName, string? contactEmail, CancellationToken cancellationToken = default)
    {
        var contexto = await RequirePlatformAsync(Permission.PlatformTenantCreate, "platform.tenant.register", slug, cancellationToken);
        if (Tenant.IsReservedSlug(slug))
        {
            throw new TenancyValidationException("Este identificador é reservado e não pode ser usado por uma empresa.");
        }

        var agora = _clock.GetUtcNow();
        var normalizado = (slug ?? string.Empty).Trim();

        var existente = await _tenants.FindBySlugAsync(normalizado, cancellationToken);
        if (existente is not null)
        {
            if (existente.Kind != TenantKind.Commercial || existente.Status != TenantStatus.Provisioning)
            {
                throw new DuplicateTenantSlugException(normalizado);
            }

            return (ToView(existente), false);
        }

        var tenant = Tenant.RegisterCommercial(new TenantId(Guid.CreateVersion7()), normalizado, name ?? string.Empty, agora);
        tenant.UpdateCadastral(name, legalName, document, contactName, contactEmail, agora);
        await _tenants.AddAsync(tenant, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("platform.tenant.register", "tenant", tenant.Id.ToString(), AuditOutcome.Success), cancellationToken);
        _ = contexto;
        return (ToView(tenant), true);
    }

    public async Task<CompanyView> UpdateCadastralAsync(
        string slug, string? name, string? legalName, string? document, string? contactName, string? contactEmail, CancellationToken cancellationToken = default)
    {
        await RequirePlatformAsync(Permission.PlatformTenantUpdate, "platform.tenant.update", slug, cancellationToken);
        var tenant = await FindCommercialAsync(slug, cancellationToken);
        tenant.UpdateCadastral(name, legalName, document, contactName, contactEmail, _clock.GetUtcNow());
        await _tenants.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditEntry("platform.tenant.update", "tenant", tenant.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(tenant);
    }

    /// <summary>Conclui o provisionamento (a empresa passa a poder operar). Quem chama garante antes que o necessário existe (política e primeiro administrador).</summary>
    public async Task<CompanyView> CompleteProvisioningAsync(string slug, CancellationToken cancellationToken = default)
    {
        await RequirePlatformAsync(Permission.PlatformTenantCreate, "platform.tenant.provisioning-complete", slug, cancellationToken);
        var tenant = await FindCommercialAsync(slug, cancellationToken);

        // Transição condicional e atômica no banco (P1-06): só Provisioning vira Active. Nunca sobrescreve Suspended (uma retomada atrasada, com a entidade já lida como "Provisioning", não reativa a empresa) nem
        // reativa por via indireta. Idempotente: se outra retomada já concluiu, não faz nada nem audita de novo.
        if (await _tenants.TryCompleteProvisioningAsync(tenant.Id, _clock.GetUtcNow(), cancellationToken))
        {
            await _audit.WriteAsync(new AuditEntry("platform.tenant.provisioning-complete", "tenant", tenant.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return ToView(tenant);
    }

    public async Task<CompanyView> SuspendAsync(string slug, string? reason, CancellationToken cancellationToken = default)
    {
        var contexto = await RequirePlatformAsync(Permission.PlatformTenantSuspend, "platform.tenant.suspend", slug, cancellationToken);
        var tenant = await FindCommercialAsync(slug, cancellationToken);
        var mudanca = tenant.SuspendWithReason(reason, contexto.Principal?.UserId, _clock.GetUtcNow());
        await _tenants.SaveStatusChangeAsync(mudanca, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("platform.tenant.suspend", "tenant", tenant.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(tenant);
    }

    public async Task<CompanyView> ReactivateAsync(string slug, string? reason, CancellationToken cancellationToken = default)
    {
        var contexto = await RequirePlatformAsync(Permission.PlatformTenantSuspend, "platform.tenant.reactivate", slug, cancellationToken);
        var tenant = await FindCommercialAsync(slug, cancellationToken);
        var mudanca = tenant.ReactivateWithReason(reason, contexto.Principal?.UserId, _clock.GetUtcNow());
        await _tenants.SaveStatusChangeAsync(mudanca, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("platform.tenant.reactivate", "tenant", tenant.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(tenant);
    }

    /// <summary>Cria o tenant técnico da plataforma (comando operacional do host, contexto de sistema). Idempotente: devolve o existente.</summary>
    public async Task<TenantInfo> EnsurePlatformTenantAsync(string name, CancellationToken cancellationToken = default)
    {
        var existente = await _tenants.FindPlatformAsync(cancellationToken);
        if (existente is not null)
        {
            return existente.ToInfo();
        }

        if (await _tenants.FindBySlugAsync(Tenant.PlatformSlug, cancellationToken) is not null)
        {
            throw new DuplicateTenantSlugException(Tenant.PlatformSlug);
        }

        var tenant = Tenant.RegisterPlatform(new TenantId(Guid.CreateVersion7()), name, _clock.GetUtcNow());
        await _tenants.AddAsync(tenant, cancellationToken);
        return tenant.ToInfo();
    }

    private async Task<Tenant> FindCommercialAsync(string slug, CancellationToken cancellationToken)
    {
        var normalizado = (slug ?? string.Empty).Trim().ToLowerInvariant();
        var tenant = await _tenants.FindBySlugAsync(normalizado, cancellationToken);
        return tenant is { Kind: TenantKind.Commercial } ? tenant : throw new TenancyResourceNotFoundException("Empresa");
    }

    private static TenantStatus? ParseStatus(string? status) => (status ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "active" or "ativa" => TenantStatus.Active,
        "suspended" or "suspensa" => TenantStatus.Suspended,
        "provisioning" or "provisionamento" => TenantStatus.Provisioning,
        _ => null,
    };

    private static CompanyView ToView(Tenant t) =>
        new(t.Id.Value, t.Slug, t.Name, t.Status.ToString(), t.LegalName, t.Document, t.ContactName, t.ContactEmail, t.CreatedAt, t.UpdatedAt, t.SuspendedAt);
}
