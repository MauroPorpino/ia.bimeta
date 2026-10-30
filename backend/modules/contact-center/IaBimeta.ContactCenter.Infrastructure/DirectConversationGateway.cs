using IaBimeta.ContactCenter.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>
/// A porta padrão para o Conversations: chama os contratos com o contexto de QUEM CHAMOU. É o que vale quando o processo já roda
/// como sistema (o worker, o job de reconciliação). Em uma requisição de usuário comum, o Conversations recusa, porque o
/// atendente não tem permissão de conversa por organização: por isso o host registra, antes do <c>AddContactCenter</c>, a
/// implementação que age com o contexto de sistema do mesmo tenant, depois de o ContactCenter ter decidido o acesso pela fila.
/// Sem essa implementação nada é ampliado: a falha é fechada.
/// </summary>
internal sealed class DirectConversationGateway : IConversationGateway
{
    private readonly IConversationQueries _queries;
    private readonly IHandlingCommands _handling;

    public DirectConversationGateway(IConversationQueries queries, IHandlingCommands handling)
    {
        _queries = queries;
        _handling = handling;
    }

    public Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _queries.GetSessionAsync(sessionId, cancellationToken);

    public Task<Page<MessageView>?> GetHistoryAsync(GetHistoryQuery query, CancellationToken cancellationToken) =>
        _queries.GetHistoryAsync(query, cancellationToken);

    public Task<TransitionResult> TransitionAsync(TransitionHandlingModeCommand command, CancellationToken cancellationToken) =>
        _handling.TransitionHandlingModeAsync(command, cancellationToken);

    public Task<TransferSessionResult> TransferAsync(TransferSessionCommand command, CancellationToken cancellationToken) =>
        _handling.TransferSessionAsync(command, cancellationToken);

    public Task<FenceHandlingResult> FenceAsync(FenceHandlingCommand command, CancellationToken cancellationToken) =>
        _handling.FenceHandlingAsync(command, cancellationToken);

    public Task<EffectView> GetEffectAsync(GetEffectQuery query, CancellationToken cancellationToken) =>
        _queries.GetEffectAsync(query, cancellationToken);
}
