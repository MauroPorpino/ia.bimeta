using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// A consulta do consentimento do telefone do contato da sessão e das ocorrências de possível saída (ADR 0041, seção 4; E2.4), com a MESMA autorização dos comandos: usuário do <c>RequestContext</c>, sessão no escopo
/// (404 fora) e <c>ConsentManage</c> sobre a organização da sessão (403; toda sessão embutida). O estado efetivo é a ÚLTIMA linha de cada finalidade por <c>seq</c>; a ausência de linha não é consentimento nem revogação.
/// A evidência completa só vem no estado efetivo; no histórico vai resumida. Nunca devolve linha de outra organização: a chave inclui a organização da sessão.
/// </summary>
public sealed class ConsentQueryService : IConsentQueries
{
    private readonly IConsentReader _reader;
    private readonly IRequestContextAccessor _context;

    public ConsentQueryService(IConsentReader reader, IRequestContextAccessor context)
    {
        _reader = reader;
        _context = context;
    }

    /// <exception cref="ConversationsValidationException">O limite do histórico está fora de 1 a <see cref="ConsentLimits.MaxHistory"/>.</exception>
    public async Task<SessionConsentView?> GetSessionContactConsentsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > ConsentLimits.MaxHistory)
        {
            throw new ConversationsValidationException($"O limite deve estar entre 1 e {ConsentLimits.MaxHistory}.");
        }

        var sessao = await AuthorizeAsync(sessionId, cancellationToken);
        if (sessao is null)
        {
            return null;
        }

        if (!E164PhoneNumber.TryNormalize(sessao.ContactRef, out var telefone))
        {
            return new SessionConsentView(sessao.OrganizationId, null, Array.Empty<ConsentRecordView>(), Array.Empty<ConsentRecordView>());
        }

        var efetivas = await _reader.LatestPerPurposeAsync(sessao.OrganizationId, telefone, cancellationToken);
        var historico = await _reader.HistoryAsync(sessao.OrganizationId, telefone, limit, cancellationToken);
        return new SessionConsentView(
            sessao.OrganizationId,
            telefone,
            efetivas.OrderBy(c => c.Purpose).Select(c => ToView(c, excerpt: false)).ToList(),
            historico.Select(c => ToView(c, excerpt: true)).ToList());
    }

    public async Task<IReadOnlyList<OptOutReviewView>?> ListOptOutReviewsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var sessao = await AuthorizeAsync(sessionId, cancellationToken);
        if (sessao is null)
        {
            return null;
        }

        var ocorrencias = await _reader.ListReviewsAsync(sessionId, cancellationToken);
        return ocorrencias
            .Where(o => o.OrganizationId == sessao.OrganizationId)
            .Select(o => new OptOutReviewView(o.Id, o.SessionId, o.MessageId, o.Reason, o.Keyword, o.OpenedAt, o.Resolution, o.ResolvedAt, o.ResolvedBy))
            .ToList();
    }

    private async Task<ConsentSessionInfo?> AuthorizeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        if (contexto.Principal is null)
        {
            throw new AccessDeniedException(Permission.ConsentManage);
        }

        var sessao = await _reader.FindSessionAsync(sessionId, cancellationToken);
        if (sessao is null || contexto.IsOutsideEmbeddedScope(sessao.OrganizationId))
        {
            return null;
        }

        contexto.Require(Permission.ConsentManage, sessao.OrganizationId);
        return sessao;
    }

    private static ConsentRecordView ToView(ContactConsent c, bool excerpt) => new(
        c.Id,
        c.OrganizationId,
        c.Phone,
        c.Purpose,
        c.Status,
        c.Origin,
        excerpt && c.Evidence.Length > ConsentLimits.EvidenceExcerptLength ? c.Evidence[..ConsentLimits.EvidenceExcerptLength] + "…" : c.Evidence,
        c.CollectedAt,
        c.RecordedAt,
        c.ActorKind,
        c.ActorId,
        c.CustomerRef,
        c.SessionId,
        c.SourceMessageId);
}
