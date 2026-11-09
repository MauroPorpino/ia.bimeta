using IaBimeta.ContactCenter.Application;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.Api.ContactCenter;

/// <summary>
/// Abre o arquivo de uma mensagem com o contexto de SISTEMA do mesmo tenant (ADR 0028), no mesmo molde de <see cref="ElevatedConversationGateway"/>: o tenant vem do contexto confiável da requisição em curso, o contexto
/// da requisição não é alterado e só existe esta operação de leitura. É o ContactCenter quem decide, antes, que o atendente pode ver a mensagem (as regras do histórico); aqui o Conversations só confere que a
/// mensagem é da sessão e que o arquivo está disponível.
/// </summary>
public sealed class ElevatedAttachmentGateway : IAttachmentGateway
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IRequestContextAccessor _context;

    public ElevatedAttachmentGateway(IServiceScopeFactory scopes, IRequestContextAccessor context)
    {
        _scopes = scopes;
        _context = context;
    }

    public async Task<AttachmentContent?> OpenAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken)
    {
        // Sem contexto confiável na operação em curso não há tenant a elevar: falha fechada.
        var atual = _context.GetRequired();

        await using var escopo = _scopes.CreateAsyncScope();
        escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(atual.TenantId, ElevatedConversationGateway.Origin));
        return await escopo.ServiceProvider.GetRequiredService<IAttachmentQueries>().OpenAsync(sessionId, messageId, cancellationToken);
    }
}
