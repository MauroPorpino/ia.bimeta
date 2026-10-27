namespace IaBimeta.SharedKernel;

/// <summary>
/// Contexto confiável da operação em curso (BOUNDARIES.md, TEN-1). É imutável e só os hosts o criam
/// (construtor interno com <c>InternalsVisibleTo</c> para Api e Worker), sempre a partir de fonte autenticada
/// e confiável. Nunca vem de corpo, query string ou saída de modelo de IA.
/// </summary>
public sealed class RequestContext
{
    internal RequestContext(TenantId tenantId, string origin)
        : this(tenantId, origin, null)
    {
    }

    internal RequestContext(TenantId tenantId, string origin, ActorPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        TenantId = tenantId;
        Origin = origin;
        Principal = principal;
    }

    public TenantId TenantId { get; }

    /// <summary>Fonte confiável que criou o contexto (por exemplo, "session" ou "development-header"). Serve a auditoria e diagnóstico.</summary>
    public string Origin { get; }

    /// <summary>O usuário autenticado e suas permissões, quando a fonte é uma sessão. Nulo em contexto de sistema.</summary>
    public ActorPrincipal? Principal { get; }

    /// <summary>Contexto sem usuário (desenvolvimento, seed, jobs): as permissões de usuário não se aplicam (<see cref="AccessPolicy"/>).</summary>
    public bool IsSystem => Principal is null;
}
