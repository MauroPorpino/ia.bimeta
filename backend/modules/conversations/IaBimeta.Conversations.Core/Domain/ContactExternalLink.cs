using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// O vínculo entre um cliente do sistema externo (<see cref="CustomerRef"/>, identificador opaco) e um <see cref="Contact"/> do tenant (ADR 0041, seção 2). Dá DESCOBRIBILIDADE, nunca visibilidade: o que cada usuário vê
/// continua decidido pelas concessões e pelo escopo. A conexão e a organização são identificadores opacos do Identity (sem chave estrangeira entre esquemas); a chave estrangeira ao contato é dentro do esquema e carrega o tenant.
/// Só se desvincula de forma LÓGICA (<see cref="Unlink"/>): a linha, o histórico de conversas e os consentimentos nunca são apagados; revincular insere outra linha.
/// </summary>
public sealed class ContactExternalLink : ITenantOwned
{
    public const int CustomerRefMaxLength = IaBimeta.Conversations.Contracts.Commands.ContactLinkLimits.CustomerRefMaxLength;

    private ContactExternalLink()
    {
        TenantId = null!;
        CustomerRef = string.Empty;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>A organização da conexão que originou o vínculo: o contato só pode ser resolvido dentro dela.</summary>
    public Guid OrganizationId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public string CustomerRef { get; private set; }

    public Guid ContactId { get; private set; }

    public LinkOrigin Origin { get; private set; }

    public DateTimeOffset LinkedAt { get; private set; }

    public ActorKind LinkedByKind { get; private set; }

    public Guid LinkedById { get; private set; }

    public DateTimeOffset? UnlinkedAt { get; private set; }

    public ActorKind? UnlinkedByKind { get; private set; }

    public Guid? UnlinkedById { get; private set; }

    public bool IsActive => UnlinkedAt is null;

    public static ContactExternalLink Create(
        TenantId tenantId, Guid organizationId, Guid connectionId, string? customerRef, Guid contactId, LinkOrigin origin, ActorKind linkedByKind, Guid linkedById, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (organizationId == Guid.Empty || connectionId == Guid.Empty || contactId == Guid.Empty || linkedById == Guid.Empty)
        {
            throw new ConversationsValidationException("O vínculo precisa de organização, conexão, contato e autor.");
        }

        // A origem e o autor andam juntos: o servidor do sistema externo é uma conexão; o atendente é um usuário. Nenhum outro par existe.
        if (!Enum.IsDefined(origin) || (origin == LinkOrigin.Integration) != (linkedByKind == ActorKind.Integration) || linkedByKind == ActorKind.System)
        {
            throw new ConversationsValidationException("A origem do vínculo não combina com o autor.");
        }

        return new ContactExternalLink
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrganizationId = organizationId,
            ConnectionId = connectionId,
            CustomerRef = ValidateCustomerRef(customerRef),
            ContactId = contactId,
            Origin = origin,
            LinkedAt = now,
            LinkedByKind = linkedByKind,
            LinkedById = linkedById,
        };
    }

    /// <summary>
    /// Desvincula (lógico). Idempotente: se já estava desvinculado devolve <c>false</c> e NÃO altera quem nem quando (a primeira desvinculação é a que vale).
    /// </summary>
    public bool Unlink(ActorKind byKind, Guid byId, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return false;
        }

        if (byKind == ActorKind.System || byId == Guid.Empty)
        {
            throw new ConversationsValidationException("A desvinculação precisa de um autor (conexão ou usuário).");
        }

        UnlinkedAt = now;
        UnlinkedByKind = byKind;
        UnlinkedById = byId;
        return true;
    }

    /// <summary>O identificador do cliente no sistema externo: de 1 a 128 caracteres, sem controle (a mesma regra do ticket do Embedded).</summary>
    public static string ValidateCustomerRef(string? customerRef)
    {
        var valor = (customerRef ?? string.Empty).Trim();
        if (valor.Length == 0 || valor.Length > CustomerRefMaxLength || valor.Any(char.IsControl))
        {
            throw new ConversationsValidationException($"O identificador do cliente deve ter de 1 a {CustomerRefMaxLength} caracteres, sem controle.");
        }

        return valor;
    }
}
