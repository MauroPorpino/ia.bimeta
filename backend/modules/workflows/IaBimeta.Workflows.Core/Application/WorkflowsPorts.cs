using System.Collections.Concurrent;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

public interface IWorkflowStore
{
    Task<WorkflowVersion?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<WorkflowVersion?> FindPublishedAsync(string key, CancellationToken cancellationToken);

    Task<WorkflowVersion?> FindDraftAsync(string key, CancellationToken cancellationToken);

    /// <summary>Todas as versões de uma chave, da mais nova para a mais antiga.</summary>
    Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(string key, CancellationToken cancellationToken);

    /// <summary>A versão mais recente de cada chave (publicada, rascunho ou aposentada).</summary>
    Task<IReadOnlyList<WorkflowVersion>> ListLatestAsync(CancellationToken cancellationToken);

    Task<int> CountKeysAsync(CancellationToken cancellationToken);

    void Add(WorkflowVersion version);

    /// <exception cref="WorkflowsConflictException"><c>key-in-use</c> ou <c>changed</c>.</exception>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Executa o trabalho numa transação (tudo ou nada): publicar aposenta a anterior e publica a nova juntas.</summary>
    Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken);
}

/// <summary>
/// A permissão do módulo, sempre sobre o <c>RequestContext</c> confiável e a organização DO FLUXO (derivada do recurso carregado, nunca da requisição). A negativa é auditada, sem valores (ADR 0037 §7).
/// </summary>
internal sealed class WorkflowsAuthorizer
{
    public const string DefinitionResource = "workflows.definition";

    private readonly IRequestContextAccessor _context;
    private readonly IAuditWriter _audit;

    public WorkflowsAuthorizer(IRequestContextAccessor context, IAuditWriter audit)
    {
        _context = context;
        _audit = audit;
    }

    public RequestContext Context => _context.GetRequired();

    public async Task RequireAsync(Permission permission, Guid? organizationId, string operation, Guid? resourceId, CancellationToken cancellationToken)
    {
        if (Context.IsAllowed(permission, organizationId))
        {
            return;
        }

        await WriteAsync(operation, resourceId, AuditOutcome.Denied, cancellationToken);
        throw new AccessDeniedException(permission);
    }

    /// <summary>O portão de FUNÇÃO: quem não tem a permissão em organização nenhuma é recusado ANTES de qualquer consulta (sem sondar chaves nem ids, sem 404 que diferencie).</summary>
    public void RequireAny(Permission permission)
    {
        var contexto = Context;
        if (contexto.Principal is null)
        {
            throw new AccessDeniedException(permission);
        }

        if (contexto.OrganizationsWithPermission(permission) is { Count: 0 })
        {
            throw new AccessDeniedException(permission);
        }
    }

    public Task WriteAsync(string operation, Guid? resourceId, AuditOutcome outcome, CancellationToken cancellationToken) =>
        _audit.WriteAsync(new AuditEntry(operation, DefinitionResource, resourceId?.ToString(), outcome), cancellationToken);
}

/// <summary>O catálogo de operações como o validador o enxerga (só leitura, `Public` e autorizada para fluxos; ADR 0037 §2).</summary>
internal sealed class SnapshotCatalogView : IFlowCatalogView
{
    private readonly Dictionary<string, CapabilityDescriptor> _byKey;

    public SnapshotCatalogView(IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        _byKey = descriptors.ToDictionary(d => d.Key, StringComparer.Ordinal);
    }

    public static bool IsEligible(CapabilityDescriptor d) => d.Effect == CapabilityEffect.Read && d.DataClass == CapabilityDataClass.Public;

    public FlowOperationInfo? Operation(string key) =>
        _byKey.TryGetValue(key, out var d)
            ? new FlowOperationInfo(true, IsEligible(d), d.Parameters.Select(p => new FlowOperationParameter(p.Name, p.Required)).ToList())
            : null;
}

/// <summary>Limita as simulações por tenant (por minuto), em memória do processo: a simulação é barata, mas não deve virar canal de carga.</summary>
public sealed class SimulationThrottle
{
    private readonly ConcurrentDictionary<Guid, (long Minute, int Count)> _windows = new();
    private readonly TimeProvider _clock;

    public SimulationThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    public bool TryAcquire(TenantId tenant, int perMinute)
    {
        var minuto = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var allowed = true;
        _windows.AddOrUpdate(
            tenant.Value,
            _ => (minuto, 1),
            (_, atual) =>
            {
                if (atual.Minute != minuto)
                {
                    return (minuto, 1);
                }

                allowed = atual.Count < perMinute;
                return allowed ? (minuto, atual.Count + 1) : atual;
            });
        return allowed;
    }
}
