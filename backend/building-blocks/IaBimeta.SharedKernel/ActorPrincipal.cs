namespace IaBimeta.SharedKernel;

/// <summary>
/// O usuário autenticado de uma operação e suas permissões efetivas, carregadas do Identity ao validar a sessão.
/// Só tem efeito dentro de um <see cref="RequestContext"/>, que os hosts criam (TEN-1).
/// </summary>
public sealed class ActorPrincipal
{
    public ActorPrincipal(Guid userId, Guid sessionId, IEnumerable<PermissionGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        UserId = userId;
        SessionId = sessionId;
        Grants = grants.ToList();
    }

    public Guid UserId { get; }

    public Guid SessionId { get; }

    public IReadOnlyList<PermissionGrant> Grants { get; }
}
