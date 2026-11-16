namespace IaBimeta.Identity.Contracts.Queries;

/// <summary>
/// O modo de uma conexão de integração do Embedded (ADR 0039). <see cref="FullAttendant"/>: a fila completa do atendente mais o contexto do cliente. <see cref="CustomerOnly"/>: só o cliente em foco.
/// </summary>
public enum EmbeddedMode
{
    FullAttendant = 1,
    CustomerOnly = 2,
}

/// <summary>O que o ia.bimeta sabe de uma sessão embutida: a conexão, o usuário externo e os clientes em foco que o servidor do ERP autorizou (ticket assinado).</summary>
public sealed record EmbeddedSessionInfo(Guid SessionId, Guid ConnectionId, Guid OrganizationId, EmbeddedMode Mode, string ExternalUserId, IReadOnlyList<string> CustomerRefs);

/// <summary>
/// Consulta do contexto da sessão embutida corrente (tenant e sessão vêm do <c>RequestContext</c>; nunca de parâmetro). Consumida por outros módulos (por exemplo, o Capabilities e o ContactCenter) para
/// decidir o que o cliente em foco permite. Sessão interativa (login por senha) devolve <c>null</c>: a ausência de contexto de cliente não amplia nem restringe nada por si.
/// </summary>
public interface IEmbeddedContextQuery
{
    /// <summary>A sessão embutida do contexto corrente, ou <c>null</c> se a sessão é interativa ou não há sessão.</summary>
    Task<EmbeddedSessionInfo?> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>Se o cliente do ERP está entre os autorizados da sessão embutida corrente. Sessão interativa devolve <c>false</c>.</summary>
    Task<bool> IsCustomerGrantedAsync(string customerRef, CancellationToken cancellationToken = default);
}
