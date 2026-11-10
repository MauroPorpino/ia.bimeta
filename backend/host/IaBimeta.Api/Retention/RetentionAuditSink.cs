using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.Retention;

/// <summary>
/// Registra na trilha de auditoria o que um expurgo removeu (ADR 0032). O expurgo roda sem requisição: o host abre, para cada empresa, o contexto de SISTEMA dela (origem <c>retention</c>, sem usuário) e usa o
/// <see cref="IAuditWriter"/> de sempre. Só identificadores vão para a trilha: nunca nome de arquivo, legenda nem conteúdo.
/// </summary>
internal sealed class RetentionAuditSink : IRetentionAuditSink
{
    public const string Origin = "retention";

    private readonly IServiceScopeFactory _scopes;

    public RetentionAuditSink(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task RecordAsync(IReadOnlyCollection<RetentionAuditItem> items, CancellationToken cancellationToken = default)
    {
        foreach (var daEmpresa in items.GroupBy(i => i.TenantId))
        {
            await using var escopo = _scopes.CreateAsyncScope();
            escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(new TenantId(daEmpresa.Key), Origin));
            var auditoria = escopo.ServiceProvider.GetRequiredService<IAuditWriter>();
            foreach (var item in daEmpresa)
            {
                await auditoria.WriteAsync(new AuditEntry(item.Operation, item.ResourceType, item.ResourceId, AuditOutcome.Success), cancellationToken);
            }
        }
    }
}
