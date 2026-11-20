using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// O consentimento do contato da conversa e as ocorrências de possível saída, pelo ATENDENTE com <c>ConsentManage</c> (ADR 0041, seções 4 e 5; E2.4). O ContactCenter decide o que é dele: o item no escopo do usuário
/// (404 fora) e a permissão <c>ConsentManage</c> sobre a organização do item (403: administrador e gestor; nunca atendente comum nem sessão embutida). O restante (a sessão, o telefone do contato, a ordem dos registros,
/// a idempotência e a auditoria) é do Conversations, dono do consentimento. O telefone é SEMPRE o do contato do item, nunca de campo livre. Nenhum modelo de IA participa.
/// </summary>
public sealed class ItemConsentService
{
    private readonly QueueItemService _items;
    private readonly IConsentCommands _commands;
    private readonly IConsentQueries _queries;
    private readonly IRequestContextAccessor _context;

    public ItemConsentService(QueueItemService items, IConsentCommands commands, IConsentQueries queries, IRequestContextAccessor context)
    {
        _items = items;
        _commands = commands;
        _queries = queries;
        _context = context;
    }

    /// <summary>Registra o consentimento ou a revogação do telefone do contato do item. <c>null</c> = item fora do escopo ou inexistente.</summary>
    /// <exception cref="AccessDeniedException">Sem <c>ConsentManage</c> sobre a organização do item.</exception>
    public async Task<ConsentRecordResult?> RecordAsync(Guid itemId, ConsentPurpose purpose, ConsentStatus status, string? evidence, DateTimeOffset? collectedAt, CancellationToken cancellationToken = default)
    {
        var item = await FindAuthorizedItemAsync(itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var resultado = await _commands.RecordForSessionContactAsync(item.SessionId, new ConsentRecordRequest(purpose, status, evidence, collectedAt), cancellationToken);
        return resultado.Outcome == ConsentRecordOutcome.NotFound ? null : resultado;
    }

    /// <summary>O estado efetivo por finalidade e o histórico (evidência resumida) do telefone do contato do item. <c>null</c> = não encontrado.</summary>
    /// <exception cref="ContactCenterValidationException">O limite do histórico está fora de 1 a 100.</exception>
    public async Task<SessionConsentView?> GetAsync(Guid itemId, int limit, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > ConsentLimits.MaxHistory)
        {
            throw new ContactCenterValidationException("invalid-limit", $"O limite deve estar entre 1 e {ConsentLimits.MaxHistory}.");
        }

        var item = await FindAuthorizedItemAsync(itemId, cancellationToken);
        return item is null ? null : await _queries.GetSessionContactConsentsAsync(item.SessionId, limit, cancellationToken);
    }

    /// <summary>As ocorrências de possível saída da conversa do item. <c>null</c> = não encontrado.</summary>
    public async Task<IReadOnlyList<OptOutReviewView>?> ListOptOutReviewsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await FindAuthorizedItemAsync(itemId, cancellationToken);
        return item is null ? null : await _queries.ListOptOutReviewsAsync(item.SessionId, cancellationToken);
    }

    /// <summary>Resolve (uma única vez) a ocorrência de possível saída da mensagem. <c>null</c> = item, sessão ou ocorrência inexistente ou fora do escopo.</summary>
    public async Task<OptOutResolveResult?> ResolveOptOutReviewAsync(Guid itemId, Guid messageId, OptOutResolution resolution, CancellationToken cancellationToken = default)
    {
        var item = await FindAuthorizedItemAsync(itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var resultado = await _commands.ResolveOptOutReviewAsync(item.SessionId, messageId, resolution, cancellationToken);
        return resultado.Outcome == OptOutResolveOutcome.NotFound ? null : resultado;
    }

    // Na ordem do plano: o item no escopo (404), depois a permissão (403). O Conversations repete as duas conferências (defesa em profundidade) sobre a sessão do item.
    private async Task<QueueItemView?> FindAuthorizedItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var item = await _items.GetAsync(itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        contexto.Require(Permission.ConsentManage, item.OrganizationId);
        return item;
    }
}
