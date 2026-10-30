using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.ContactCenter;

/// <summary>
/// A integração mediada pelo host entre o ContactCenter e o Conversations (autorização delegada e elevação de contexto: ADR 0009). O
/// atendente comum não tem permissão de conversa por
/// organização (o acesso dele vem da fila, no ContactCenter), então o Conversations recusaria os comandos que o ContactCenter
/// faz em nome dele. O ContactCenter decide o acesso (pela fila, pela unidade e pelo papel) ANTES de chamar esta porta; esta
/// classe só executa a chamada com o contexto de SISTEMA do mesmo tenant, que o Conversations aceita para o ContactCenter (a
/// documentação dos contratos de <c>TransitionHandlingMode</c> e <c>TransferSession</c> prevê esse chamador).
/// <para>
/// O contexto elevado nasce em um escopo de DI PRÓPRIO e curto, criado aqui, no host (TEN-1): o tenant é copiado do contexto
/// confiável da operação em curso, nunca de um parâmetro, e o contexto da requisição não é alterado, então nada fica elevado
/// depois da chamada nem em chamadas concorrentes. Só as seis operações da porta existem (a sexta, a consulta de efeito, só lê): o ContactCenter não
/// ganha um executor genérico de código privilegiado. A matriz de dependências não muda: o ContactCenter já referencia o
/// <c>Conversations.Contracts</c>.
/// </para>
/// </summary>
public sealed class ElevatedConversationGateway : IConversationGateway
{
    /// <summary>A origem do contexto elevado, para a auditoria e o diagnóstico.</summary>
    public const string Origin = "contact-center";

    private readonly IServiceScopeFactory _scopes;
    private readonly IRequestContextAccessor _context;

    public ElevatedConversationGateway(IServiceScopeFactory scopes, IRequestContextAccessor context)
    {
        _scopes = scopes;
        _context = context;
    }

    public Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        RunAsync((queries, _) => queries.GetSessionAsync(sessionId, cancellationToken));

    public async Task<Page<MessageView>?> GetHistoryAsync(GetHistoryQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync((queries, _) => queries.GetHistoryAsync(query, cancellationToken));
        }
        catch (ConversationsValidationException ex)
        {
            // O tamanho da página e o cursor vêm do cliente: parâmetro inválido é erro do pedido (400), nunca falha do servidor.
            throw new ContactCenterValidationException(ex.Message);
        }
    }

    public Task<TransitionResult> TransitionAsync(TransitionHandlingModeCommand command, CancellationToken cancellationToken) =>
        RunAsync((_, handling) => handling.TransitionHandlingModeAsync(command, cancellationToken));

    public Task<TransferSessionResult> TransferAsync(TransferSessionCommand command, CancellationToken cancellationToken) =>
        RunAsync((_, handling) => handling.TransferSessionAsync(command, cancellationToken));

    public Task<FenceHandlingResult> FenceAsync(FenceHandlingCommand command, CancellationToken cancellationToken) =>
        RunAsync((_, handling) => handling.FenceHandlingAsync(command, cancellationToken));

    public Task<EffectView> GetEffectAsync(GetEffectQuery query, CancellationToken cancellationToken) =>
        RunAsync((queries, _) => queries.GetEffectAsync(query, cancellationToken));

    private async Task<T> RunAsync<T>(Func<IConversationQueries, IHandlingCommands, Task<T>> action)
    {
        // Sem contexto confiável na operação em curso não há tenant a elevar: falha fechada.
        var atual = _context.GetRequired();

        await using var escopo = _scopes.CreateAsyncScope();
        escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(atual.TenantId, Origin));
        return await action(
            escopo.ServiceProvider.GetRequiredService<IConversationQueries>(),
            escopo.ServiceProvider.GetRequiredService<IHandlingCommands>());
    }
}
