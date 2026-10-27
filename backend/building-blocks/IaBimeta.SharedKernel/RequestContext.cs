namespace IaBimeta.SharedKernel;

/// <summary>
/// Contexto confiável da operação em curso (BOUNDARIES.md, TEN-1). É imutável e só os hosts o criam
/// (construtor interno com <c>InternalsVisibleTo</c> para Api e Worker), sempre a partir de fonte autenticada
/// e confiável. Nunca vem de corpo, query string ou saída de modelo de IA.
/// </summary>
public sealed class RequestContext
{
    internal RequestContext(TenantId tenantId, string origin)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        TenantId = tenantId;
        Origin = origin;
    }

    public TenantId TenantId { get; }

    /// <summary>Fonte confiável que criou o contexto (por exemplo, "development-header"). Serve a auditoria e diagnóstico.</summary>
    public string Origin { get; }
}
