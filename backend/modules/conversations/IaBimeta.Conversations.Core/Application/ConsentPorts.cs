using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>A sessão de atendimento como o consentimento a enxerga: a organização, o contato e a referência do contato (o telefone, quando a referência é um).</summary>
public sealed record ConsentSessionInfo(Guid OrganizationId, Guid ContactId, string ContactRef);

/// <summary>
/// Persistência do consentimento e das ocorrências de possível saída (ADR 0041, seções 4 e 5; E2.4). Opera sempre dentro do tenant do contexto. Toda alteração acontece numa unidade de trabalho (uma transação) que grava o
/// efeito e a auditoria JUNTOS e que serializa as operações do mesmo telefone na mesma organização (trava consultiva da transação).
/// </summary>
public interface IConsentStore
{
    Task<IConsentUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

public interface IConsentUnitOfWork : IAsyncDisposable
{
    /// <summary>A organização, o contato e a referência do contato da sessão, ou <c>null</c> se ela não existe no tenant.</summary>
    Task<ConsentSessionInfo?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Trava (até o fim da transação) o telefone na organização: todo registro, repetição e revogação do mesmo telefone se serializa, e "a última linha" que se lê em seguida nunca é de uma transação em curso. A ordem
    /// das travas é fixa (primeiro a ocorrência, depois o telefone), então não há ciclo de espera.
    /// </summary>
    Task LockPhoneAsync(Guid organizationId, string phone, CancellationToken cancellationToken);

    /// <summary>Trava (até o fim da transação) a ocorrência de possível saída: duas decisões sobre a mesma ocorrência se serializam.</summary>
    Task LockReviewAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>A última linha (maior <c>seq</c>) da chave (organização, telefone, finalidade), ou <c>null</c>.</summary>
    Task<ContactConsent?> GetLatestAsync(Guid organizationId, string phone, ConsentPurpose purpose, CancellationToken cancellationToken);

    /// <summary>A MAIOR data de coleta entre as linhas <c>Revoked</c> da chave (organização, telefone, finalidade), ou <c>null</c>: a referência contra a qual uma concessão é comparada. Uma revogação atrasada nunca a reduz.</summary>
    Task<DateTimeOffset?> GetLatestRevocationCollectedAtAsync(Guid organizationId, string phone, ConsentPurpose purpose, CancellationToken cancellationToken);

    void Add(ContactConsent consent);

    /// <summary>A ocorrência da mensagem NESTA sessão e organização (rastreada para a resolução), ou <c>null</c>.</summary>
    Task<OptOutReview?> FindReviewAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>Grava o efeito e os eventos de auditoria na MESMA transação e a confirma. Qualquer falha propaga (e desanexa o que foi adicionado); o descarte da unidade desfaz a transação.</summary>
    Task CommitAsync(IReadOnlyCollection<EventEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>As leituras do consentimento (sem transação, sem trava).</summary>
public interface IConsentReader
{
    Task<ConsentSessionInfo?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>A última linha de cada finalidade da chave (organização, telefone).</summary>
    Task<IReadOnlyList<ContactConsent>> LatestPerPurposeAsync(Guid organizationId, string phone, CancellationToken cancellationToken);

    /// <summary>As linhas mais recentes da chave, da mais nova para a mais antiga (por <c>seq</c>).</summary>
    Task<IReadOnlyList<ContactConsent>> HistoryAsync(Guid organizationId, string phone, int limit, CancellationToken cancellationToken);

    /// <summary>As ocorrências da sessão, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<OptOutReview>> ListReviewsAsync(Guid sessionId, CancellationToken cancellationToken);
}
