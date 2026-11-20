namespace IaBimeta.Conversations.Contracts.Commands;

/// <summary>O que o atendente autorizado registra: a finalidade, a situação, a evidência e a data da COLETA. O telefone nunca vem aqui: é o do contato da sessão.</summary>
public sealed record ConsentRecordRequest(ConsentPurpose Purpose, ConsentStatus Status, string? Evidence, DateTimeOffset? CollectedAt);

/// <summary>
/// O desfecho de um registro de consentimento (ADR 0041, seção 4; E2.4). <see cref="Unchanged"/>: repetição idêntica à última linha efetiva, nada foi gravado e não há auditoria nova.
/// <see cref="NotFound"/>: sessão fora do escopo ou inexistente (a mesma resposta). <see cref="PhoneUnavailable"/>: o contato tem referência opaca (sem telefone E.164). <see cref="Superseded"/>: um consentimento
/// (<c>Granted</c>) com data da coleta que não é posterior à revogação já registrada: um registro atrasado ou repetido fora de ordem nunca reativa o consentimento revogado.
/// </summary>
public enum ConsentRecordOutcome
{
    Recorded = 1,
    Unchanged = 2,
    NotFound = 3,
    PhoneUnavailable = 4,
    Superseded = 5,

    /// <summary>Um campo foi recusado (evidência, data da coleta, finalidade ou situação): o código estável está em <see cref="ConsentRecordResult.RejectionCode"/>. Nada foi gravado.</summary>
    Invalid = 6,
}

public sealed record ConsentRecordResult(ConsentRecordOutcome Outcome, ConsentPurpose? Purpose, ConsentStatus? Status, string? RejectionCode = null);

/// <summary>A decisão da pessoa autorizada sobre uma possível saída (<c>PossibleOptOut</c>): revogar o consentimento do titular ou mantê-lo.</summary>
public enum OptOutResolution
{
    Revoked = 1,
    Kept = 2,
}

/// <summary>
/// O desfecho da resolução. <see cref="AlreadyResolved"/>: a MESMA decisão repetida (idempotente, sem efeito nem auditoria novos). <see cref="Conflict"/>: a ocorrência já foi resolvida com OUTRA decisão e nunca é
/// resolvida duas vezes. <see cref="NotFound"/>: sessão fora do escopo ou ocorrência inexistente.
/// </summary>
public enum OptOutResolveOutcome
{
    Resolved = 1,
    AlreadyResolved = 2,
    Conflict = 3,
    NotFound = 4,

    /// <summary>A decisão <c>Revoked</c> precisa do telefone do titular, e o contato tem referência opaca (sem telefone E.164). Nada foi gravado; a ocorrência continua pendente.</summary>
    PhoneUnavailable = 5,
}

public sealed record OptOutResolveResult(OptOutResolveOutcome Outcome, OptOutResolution? Resolution);

/// <summary>
/// O registro e a resolução de consentimento pelo ATENDENTE autorizado (ADR 0041, seção 4; E2.4). Quem decide é o Conversations: o usuário e o tenant vêm do <c>RequestContext</c> (nunca de parâmetro), a organização e
/// o telefone vêm da SESSÃO (nunca de campo livre) e a permissão exigida é <c>ConsentManage</c> sobre a organização da sessão. Sem a permissão: <see cref="IaBimeta.SharedKernel.AccessDeniedException"/> (403). Sessão fora do
/// escopo ou inexistente: <see cref="ConsentRecordOutcome.NotFound"/> (404). Nenhum modelo de IA participa.
/// </summary>
public interface IConsentCommands
{
    /// <exception cref="IaBimeta.SharedKernel.AccessDeniedException">Sem <c>ConsentManage</c> sobre a organização da sessão (inclui toda sessão embutida).</exception>
    Task<ConsentRecordResult> RecordForSessionContactAsync(Guid sessionId, ConsentRecordRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resolve uma ocorrência de <c>PossibleOptOut</c> da sessão: uma única vez. <c>Revoked</c> revoga TODAS as finalidades do telefone na organização (origem <c>AgentRecorded</c>); <c>Kept</c> só registra a decisão.</summary>
    Task<OptOutResolveResult> ResolveOptOutReviewAsync(Guid sessionId, Guid messageId, OptOutResolution resolution, CancellationToken cancellationToken = default);
}

/// <summary>
/// O estado do consentimento do telefone do contato da sessão: a última linha por finalidade (estado efetivo, com a evidência) e as linhas mais recentes (evidência resumida, da mais nova para a mais antiga).
/// <c>Phone</c> nulo = o contato tem referência opaca (sem telefone E.164): não há consentimento a consultar. <c>Effective</c> traz só as finalidades que têm linha: a AUSÊNCIA de linha não é consentimento nem
/// revogação (quem decide o que isso significa é a política da empresa, na E3).
/// </summary>
public sealed record SessionConsentView(Guid OrganizationId, string? Phone, IReadOnlyList<ConsentRecordView> Effective, IReadOnlyList<ConsentRecordView> History);

/// <summary>Uma ocorrência de possível saída do cliente. Nunca traz o texto livre da mensagem: só o motivo e a palavra normalizada (quando houve).</summary>
public sealed record OptOutReviewView(Guid Id, Guid SessionId, Guid MessageId, OptOutReviewReason Reason, string? Keyword, DateTimeOffset OpenedAt, OptOutResolution? Resolution, DateTimeOffset? ResolvedAt, Guid? ResolvedBy);

public enum OptOutReviewReason
{
    FlowFreeText = 1,
    ConversationalContext = 2,
    NaturalLanguageIntent = 3,
}

/// <summary>A consulta do consentimento e das ocorrências de possível saída, com a mesma autorização dos comandos (<c>ConsentManage</c>; <c>null</c> = não encontrado).</summary>
public interface IConsentQueries
{
    Task<SessionConsentView?> GetSessionContactConsentsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OptOutReviewView>?> ListOptOutReviewsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Os limites do consentimento que os módulos consumidores precisam conhecer.</summary>
public static class ConsentLimits
{
    public const int EvidenceMaxLength = 2000;

    /// <summary>Quantas linhas do histórico a consulta devolve por padrão e no máximo.</summary>
    public const int DefaultHistory = 50;

    public const int MaxHistory = 100;

    /// <summary>O tamanho da evidência nas LISTAS (a evidência completa só no estado efetivo, para quem tem <c>ConsentManage</c>).</summary>
    public const int EvidenceExcerptLength = 80;
}
