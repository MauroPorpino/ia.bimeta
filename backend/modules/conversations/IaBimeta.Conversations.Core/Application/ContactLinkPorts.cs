using IaBimeta.Audit.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// Persistência do vínculo entre o cliente do sistema externo e o contato (ADR 0041, seção 2). Opera sempre dentro do tenant do contexto. Toda alteração acontece numa unidade de trabalho (uma transação) que grava o
/// vínculo e a auditoria JUNTOS, e que serializa as operações do mesmo cliente e do mesmo contato (trava consultiva da transação).
/// </summary>
public interface IContactLinkStore
{
    Task<IContactLinkUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>A sessão de atendimento como o vínculo a enxerga: só a organização e o contato.</summary>
public sealed record LinkSessionInfo(Guid OrganizationId, Guid ContactId);

public interface IContactLinkUnitOfWork : IAsyncDisposable
{
    /// <summary>
    /// Os contatos do tenant cuja referência é o telefone informado (só dígitos, o formato do <c>wa_id</c>; aceita também a forma com <c>+</c>) E que têm sessão na organização. Um contato sem sessão na organização da
    /// conexão nunca é devolvido: a conexão não enxerga contatos de outra organização.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindContactIdsByPhoneAsync(string phoneDigits, Guid organizationId, CancellationToken cancellationToken);

    /// <summary>A organização e o contato da sessão, ou <c>null</c> se ela não existe no tenant.</summary>
    Task<LinkSessionInfo?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Trava (até o fim da transação) as chaves do cliente e dos contatos, sempre na MESMA ordem (primeiro o cliente, depois os contatos em ordem crescente), então duas operações concorrentes se serializam sem deadlock e
    /// o cálculo de "compartilhado" nunca vê meia operação.
    /// </summary>
    Task LockAsync(Guid connectionId, string customerRef, IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken);

    /// <summary>Os vínculos ATIVOS do cliente nesta conexão (rastreados para alteração).</summary>
    Task<IReadOnlyList<ContactExternalLink>> ListActiveByCustomerAsync(Guid connectionId, string customerRef, CancellationToken cancellationToken);

    /// <summary>Os vínculos ATIVOS dos contatos nesta conexão (de qualquer cliente), rastreados.</summary>
    Task<IReadOnlyList<ContactExternalLink>> ListActiveByContactsAsync(Guid connectionId, IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken);

    void Add(ContactExternalLink link);

    /// <summary>
    /// Grava as alterações e os eventos de auditoria na MESMA transação e a confirma. Devolve <c>false</c> (e descarta tudo) se outro vínculo ativo igual foi gravado por uma transação concorrente (índice único
    /// parcial): quem chama relê e decide de novo. Qualquer outra falha propaga, e o descarte desfaz a transação.
    /// </summary>
    Task<bool> CommitAsync(IReadOnlyCollection<EventEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>
/// O pedido de gravação de auditoria do módulo, guardado no outbox na MESMA transação do efeito (ADR 0041, seção 8): se a transação é desfeita a entrada some junto, e a trilha nunca diz "sucesso" do que não
/// aconteceu. O relay a entrega ao handler do módulo, que a grava na trilha com o autor, a origem e o instante ORIGINAIS e com o identificador do evento como chave de idempotência. Interno ao Conversations: nenhum outro
/// módulo o consome.
/// </summary>
public sealed record ConversationAuditRequestedV1(string Operation, string ResourceType, string? ResourceId, AuditOutcome Outcome, Guid? ActorUserId, string ActorOrigin, DateTimeOffset OccurredAt);

public static class ConversationAuditEvents
{
    /// <summary>O nome estável do evento de auditoria do módulo (sem versão).</summary>
    public const string AuditRequested = "conversations.audit-requested";
}

/// <summary>Não foi possível concluir a operação do vínculo por contenção persistente entre transações. Nada foi gravado: repetir é seguro (503 com <c>Retry-After</c>).</summary>
public sealed class ContactLinkContentionException : Exception
{
    public ContactLinkContentionException()
        : base("Serviço temporariamente ocupado.")
    {
    }
}
