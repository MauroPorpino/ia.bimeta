namespace IaBimeta.SharedKernel;

/// <summary>
/// O usuário autenticado de uma operação e suas permissões efetivas, carregadas do Identity ao validar a sessão.
/// Só tem efeito dentro de um <see cref="RequestContext"/>, que os hosts criam (TEN-1).
/// </summary>
public sealed class ActorPrincipal
{
    public ActorPrincipal(Guid userId, Guid sessionId, IEnumerable<PermissionGrant> grants, EmbeddedScope? embedded = null)
    {
        ArgumentNullException.ThrowIfNull(grants);

        UserId = userId;
        SessionId = sessionId;
        Grants = grants.ToList();
        Embedded = embedded;
    }

    /// <summary>
    /// Presente só na sessão embutida (ADR 0039): o limite DURO de organização da conexão e o modo. Tudo o que não é permissão por concessão (participação em fila, responsável pela conversa, recibo de operação)
    /// também precisa respeitá-lo; quem decide acesso por outra via consulta <see cref="AccessPolicy.IsOutsideEmbeddedScope"/>.
    /// </summary>
    public EmbeddedScope? Embedded { get; }

    public Guid UserId { get; }

    public Guid SessionId { get; }

    public IReadOnlyList<PermissionGrant> Grants { get; }
}

/// <summary>O limite de uma sessão embutida: a organização da conexão e se o modo é só do cliente em foco (<c>CustomerOnly</c>), sem acesso à fila geral.</summary>
public sealed record EmbeddedScope(Guid OrganizationId, bool CustomerOnly);
