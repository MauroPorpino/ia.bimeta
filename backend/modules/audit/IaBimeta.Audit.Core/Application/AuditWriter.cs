using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Audit.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Audit.Application;

/// <summary>
/// Grava na trilha. Tenant, ator e origem vêm do contexto confiável e a data vem do relógio: o chamador só informa
/// o que aconteceu.
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    public const string AuthenticationOrigin = "authentication";

    private readonly IAuditRepository _eventos;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;

    public AuditWriter(IAuditRepository eventos, IRequestContextAccessor context, TimeProvider clock)
    {
        _eventos = eventos;
        _context = context;
        _clock = clock;
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var evento = AuditEvent.Create(contexto.TenantId, contexto.Principal?.UserId, contexto.Origin, entry, _clock.GetUtcNow());
        await _eventos.AddAsync(evento, cancellationToken);
    }

    public async Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var evento = AuditEvent.Create(tenantId, actorUserId, AuthenticationOrigin, entry, _clock.GetUtcNow());
        await _eventos.AddForAuthenticationAsync(evento, cancellationToken);
    }
}
