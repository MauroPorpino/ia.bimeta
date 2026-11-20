using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// O vínculo do cliente em foco com o contato da conversa, pelo ATENDENTE em sessão embutida (ADR 0041, seção 2; E2.2). O ContactCenter decide o que é dele: o item no escopo do usuário (404 fora) e a permissão de
/// operar (<c>ConversationManage</c> sobre a organização do item: 403). O restante (sessão embutida, conexão e cliente autorizado na sessão assinada pelo sistema externo) é decidido pelo Conversations, dono do vínculo, que
/// devolve a mesma resposta "não encontrado" para qualquer recusa que não seja de permissão. Nada aqui concede acesso: o vínculo apenas localiza o contato.
/// </summary>
public sealed class CustomerLinkService
{
    private readonly QueueItemService _items;
    private readonly IContactLinkCommands _links;
    private readonly IRequestContextAccessor _context;

    public CustomerLinkService(QueueItemService items, IContactLinkCommands links, IRequestContextAccessor context)
    {
        _items = items;
        _links = links;
        _context = context;
    }

    /// <summary>Vincula o cliente ao contato do item. <c>null</c> = não encontrado (item fora do escopo, contexto que não é de sessão embutida ou cliente não autorizado na sessão).</summary>
    /// <exception cref="ContactCenterValidationException">O identificador do cliente é inválido.</exception>
    /// <exception cref="AccessDeniedException">O usuário não tem <c>ConversationManage</c> sobre a organização do item.</exception>
    public async Task<AgentLinkResult?> LinkAsync(Guid itemId, string? customerRef, CancellationToken cancellationToken = default)
    {
        var cliente = ValidateCustomerRef(customerRef);
        var item = await FindAuthorizedItemAsync(itemId, cancellationToken);
        return item is null ? null : await _links.LinkSessionContactAsync(item.SessionId, cliente, cancellationToken);
    }

    /// <summary>Desvincula (lógico, idempotente). <c>false</c> = não encontrado, com as mesmas causas do vínculo.</summary>
    public async Task<bool> UnlinkAsync(Guid itemId, string? customerRef, CancellationToken cancellationToken = default)
    {
        var cliente = ValidateCustomerRef(customerRef);
        var item = await FindAuthorizedItemAsync(itemId, cancellationToken);
        return item is not null && await _links.UnlinkSessionContactAsync(item.SessionId, cliente, cancellationToken);
    }

    // Na ordem do plano: o item no escopo (404), depois a permissão (403). Só então o Conversations confere a sessão embutida e o cliente (404).
    private async Task<QueueItemView?> FindAuthorizedItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var item = await _items.GetAsync(itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        contexto.Require(Permission.ConversationManage, item.OrganizationId);
        return item;
    }

    /// <summary>A mesma regra do Conversations (1 a 128 caracteres, sem controle, aparado), para que a recusa seja 400 aqui e nunca uma exceção de outro módulo.</summary>
    private static string ValidateCustomerRef(string? customerRef)
    {
        var valor = (customerRef ?? string.Empty).Trim();
        if (valor.Length == 0 || valor.Length > ContactLinkLimits.CustomerRefMaxLength || valor.Any(char.IsControl))
        {
            throw new ContactCenterValidationException($"O identificador do cliente deve ter de 1 a {ContactLinkLimits.CustomerRefMaxLength} caracteres, sem controle.");
        }

        return valor;
    }
}
