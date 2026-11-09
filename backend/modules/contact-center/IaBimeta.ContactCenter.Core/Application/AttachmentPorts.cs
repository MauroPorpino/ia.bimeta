using IaBimeta.Conversations.Contracts.Queries;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// A porta do ContactCenter para ABRIR o arquivo de uma mensagem (ADR 0028). Separada de <see cref="IConversationGateway"/> (cujas operações são fixas pelo ADR 0009) e com a mesma ideia: o host registra uma
/// implementação que age com o contexto de sistema do mesmo tenant, DEPOIS de o ContactCenter ter decidido, pelas regras do histórico, que o atendente enxerga a mensagem. A padrão usa o contexto de quem chamou
/// (falha fechada para o atendente comum, que não tem permissão de conversa por organização).
/// </summary>
public interface IAttachmentGateway
{
    /// <summary>O arquivo aberto, ou nulo se a sessão, a mensagem ou o arquivo disponível não existem para o contexto. Quem recebe DEVE descartar o resultado.</summary>
    Task<AttachmentContent?> OpenAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken);
}
