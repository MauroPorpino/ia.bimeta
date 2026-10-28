namespace IaBimeta.Conversations.Contracts.Queries;

/// <summary>
/// Leitura de sessões e do histórico para quem está autenticado como usuário (ou para um contexto de sistema). Sempre
/// dentro do tenant do <c>RequestContext</c> e das organizações em que o usuário tem a permissão de leitura: sessão ou
/// mensagem fora desse alcance responde como inexistente (TEN-2).
/// </summary>
public interface IConversationQueries
{
    /// <summary>A sessão, ou <c>null</c> se não existe no tenant ou o usuário não pode lê-la.</summary>
    Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>As sessões visíveis, da mais recente para a mais antiga.</summary>
    /// <exception cref="IaBimeta.SharedKernel.AccessDeniedException">O usuário não tem permissão de leitura em nenhuma organização.</exception>
    Task<Page<SessionView>> ListSessionsAsync(ListSessionsQuery query, CancellationToken cancellationToken = default);

    /// <summary>O histórico de UMA sessão, paginado, ou <c>null</c> se a sessão não existe ou não é visível.</summary>
    Task<Page<MessageView>?> GetHistoryAsync(GetHistoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// As traduções guardadas de UMA mensagem da sessão, ou <c>null</c> se a sessão ou a mensagem não existem ou não são
    /// visíveis. O original da mensagem não faz parte delas e nunca muda.
    /// </summary>
    Task<IReadOnlyList<MessageTranslationView>?> GetMessageTranslationsAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// O rastro de encaminhamentos de UMA sessão, do mais antigo para o mais novo, ou <c>null</c> se a sessão não existe ou
    /// não é visível.
    /// </summary>
    Task<IReadOnlyList<SessionTransferView>?> GetTransfersAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

/// <param name="Take">De 1 a 100.</param>
/// <param name="Cursor">O cursor opaco da página anterior; nulo na primeira.</param>
/// <param name="Status">Filtro opcional por estado.</param>
public sealed record ListSessionsQuery(int Take, string? Cursor, SessionStatus? Status);

/// <param name="SessionId">Uma única sessão: nenhuma consulta aceita várias (TEN-5).</param>
public sealed record GetHistoryQuery(Guid SessionId, int Take, string? Cursor, HistoryOrder Order);
