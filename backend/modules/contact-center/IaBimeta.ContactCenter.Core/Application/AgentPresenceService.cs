using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// A presença do atendente autenticado: se ele recebe atendimentos agora. Cada um informa a própria; ninguém muda a de outro. O que
/// nunca informou vale <see cref="AgentStatus.Offline"/>. A presença decide a disponibilidade para o roteamento, mas não impede um
/// atendente de assumir uma conversa: ele continua dono do que faz.
/// </summary>
public sealed class AgentPresenceService
{
    private readonly IRoutingRepository _routing;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;

    public AgentPresenceService(IRoutingRepository routing, IRequestContextAccessor context, TimeProvider clock)
    {
        _routing = routing;
        _context = context;
        _clock = clock;
    }

    /// <exception cref="AuthenticationRequiredException">O contexto não tem usuário autenticado.</exception>
    /// <exception cref="ContactCenterValidationException">Situação inválida.</exception>
    public async Task<AgentPresenceView> SetMineAsync(AgentStatus status, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var usuario = contexto.RequirePrincipal().UserId;
        var agora = _clock.GetUtcNow();

        var presenca = await _routing.FindPresenceAsync(usuario, cancellationToken);
        if (presenca is null)
        {
            presenca = AgentPresence.Create(contexto.TenantId, usuario, status, agora);
            try
            {
                await _routing.AddPresenceAsync(presenca, cancellationToken);
            }
            catch (ContactCenterConflictException)
            {
                // Outra requisição do mesmo atendente criou a presença primeiro: aplica a situação pedida sobre ela.
                presenca = await _routing.FindPresenceAsync(usuario, cancellationToken)
                    ?? throw new InvalidOperationException("A presença criada em paralelo desapareceu.");
                if (presenca.Change(status, agora))
                {
                    await _routing.SaveChangesAsync(cancellationToken);
                }
            }
        }
        else if (presenca.Change(status, agora))
        {
            await _routing.SaveChangesAsync(cancellationToken);
        }

        return new AgentPresenceView(usuario, presenca.Status, presenca.UpdatedAt);
    }

    /// <exception cref="AuthenticationRequiredException">O contexto não tem usuário autenticado.</exception>
    public async Task<AgentPresenceView> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var usuario = _context.GetRequired().RequirePrincipal().UserId;
        var presenca = await _routing.FindPresenceReadOnlyAsync(usuario, cancellationToken);
        return new AgentPresenceView(usuario, presenca?.Status ?? AgentStatus.Offline, presenca?.UpdatedAt);
    }
}
