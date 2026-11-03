using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// Leitura de sessões e do histórico (TEN-2): o recurso é buscado por <c>(tenant, id)</c>, a organização que decide a
/// permissão vem do recurso carregado, e sem a permissão de leitura sobre ela a sessão responde como inexistente. A lista já
/// sai filtrada pelo escopo efetivo do usuário. Nenhuma consulta aceita mais de uma sessão (TEN-5).
/// </summary>
public sealed class ConversationQueryService : IConversationQueries
{
    private readonly IConversationStore _store;
    private readonly IRequestContextAccessor _context;

    public ConversationQueryService(IConversationStore store, IRequestContextAccessor context)
    {
        _store = store;
        _context = context;
    }

    public async Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var linha = await _store.FindSessionAsync(sessionId, cancellationToken);
        return linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId)
            ? null
            : linha.ToView();
    }

    public async Task<Page<SessionView>> ListSessionsAsync(ListSessionsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var contexto = _context.GetRequired();
        var take = ValidateTake(query.Take);
        var depois = CursorCodec.DecodeSession(query.Cursor);

        var visiveis = contexto.OrganizationsWithPermission(Permission.ConversationRead);
        if (visiveis is { Count: 0 })
        {
            // Sem leitura de conversas em organização alguma: a operação em si é negada, em vez de uma lista vazia que esconderia isso.
            throw new AccessDeniedException(Permission.ConversationRead);
        }

        // Uma a mais que o pedido: se vier, há próxima página.
        var linhas = await _store.ListSessionsAsync(new SessionListFilter(visiveis, query.Status, depois, take + 1), cancellationToken);
        var pagina = linhas.Take(take).ToList();
        var proximo = linhas.Count > take ? CursorCodec.Encode(new SessionCursor(pagina[^1].Session.LastMessageAt, pagina[^1].Session.Id)) : null;
        return new Page<SessionView>(pagina.Select(l => l.ToView()).ToList(), proximo);
    }

    public async Task<Page<MessageView>?> GetHistoryAsync(GetHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var contexto = _context.GetRequired();
        var take = ValidateTake(query.Take);
        var depois = CursorCodec.DecodeMessage(query.Cursor);
        if (!Enum.IsDefined(query.Order))
        {
            throw new ConversationsValidationException("A ordem do histórico é inválida.");
        }

        var linha = await _store.FindSessionAsync(query.SessionId, cancellationToken);
        if (linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId))
        {
            return null;
        }

        if (query.ThroughMessageSeq is < 0)
        {
            throw new ConversationsValidationException("O limite de sequência do histórico é inválido.");
        }

        var mensagens = await _store.ListMessagesAsync(query.SessionId, new MessagePageFilter(query.Order, depois, take + 1, query.ThroughMessageSeq), cancellationToken);
        var pagina = mensagens.Take(take).ToList();
        var proximo = mensagens.Count > take ? CursorCodec.Encode(new MessageCursor(pagina[^1].OccurredAt, pagina[^1].MessageSeq)) : null;
        return new Page<MessageView>(pagina.Select(m => m.ToView()).ToList(), proximo);
    }

    public async Task<IReadOnlyList<MessageTranslationView>?> GetMessageTranslationsAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var linha = await _store.FindSessionAsync(sessionId, cancellationToken);
        if (linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId))
        {
            return null;
        }

        // A mensagem precisa ser desta sessão: o id de uma mensagem de outra sessão (ou de outro tenant) é inexistente.
        if (await _store.FindMessageAsync(sessionId, messageId, cancellationToken) is null)
        {
            return null;
        }

        var traducoes = await _store.ListTranslationsAsync(messageId, cancellationToken);
        return traducoes.Select(t => t.ToView()).ToList();
    }

    public async Task<IReadOnlyList<SessionTransferView>?> GetTransfersAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var linha = await _store.FindSessionAsync(sessionId, cancellationToken);
        if (linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId))
        {
            return null;
        }

        var rastro = await _store.ListTransfersAsync(sessionId, cancellationToken);
        return rastro.Select(t => t.ToView()).ToList();
    }

    public async Task<EffectView> GetEffectAsync(GetEffectQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var contexto = _context.GetRequired();
        if (query.OperationId == Guid.Empty || !Enum.IsDefined(query.Kind))
        {
            throw new ConversationsValidationException("A consulta de efeito precisa do tipo e do identificador da operação.");
        }

        // Só a transição e o encaminhamento têm consulta de efeito hoje. HumanReply e SessionClose já existem no contrato (ADR 0009 e ADR 0014), mas só passam a ser
        // respondidos junto das operações (fatias A4 e A6): até lá são RECUSADOS, e antes de ler qualquer coisa, em vez de tratados como transição. Qualquer valor
        // que não seja um dos dois tratados abaixo falha fechado, inclusive um tipo futuro que alguém defina sem tratá-lo aqui.
        if (query.Kind is not (EffectKind.Transition or EffectKind.Transfer))
        {
            throw new ConversationsValidationException("A consulta de efeito ainda não suporta este tipo de efeito: só a transição e o encaminhamento.");
        }

        // Sessão inexistente, de outro tenant ou fora do alcance do contexto: o efeito também "não existe" (TEN-2). Nada é escrito nem travado.
        var linha = await _store.FindSessionAsync(query.SessionId, cancellationToken);
        if (linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId))
        {
            return new EffectView(EffectOutcome.NotFound);
        }

        if (query.Kind == EffectKind.Transfer)
        {
            if (await _store.FindTransferReadOnlyAsync(query.SessionId, query.OperationId, cancellationToken) is { } encaminhamento)
            {
                return new EffectView(EffectOutcome.Applied, encaminhamento.StateVersionAfter);
            }

            // O encaminhamento que foi avaliado e não mudou nada fica registrado junto das transições: há registro, mas ele não prova uma mudança.
            return await _store.FindTransitionReadOnlyAsync(query.SessionId, query.OperationId, cancellationToken) is { } semMudanca
                ? new EffectView(EffectOutcome.Indeterminate, semMudanca.StateVersionAfter)
                : new EffectView(EffectOutcome.NotFound);
        }

        // EffectKind.Transition: o único tipo que sobra depois da recusa acima e do encaminhamento.
        if (await _store.FindTransitionReadOnlyAsync(query.SessionId, query.OperationId, cancellationToken) is not { } transicao)
        {
            return new EffectView(EffectOutcome.NotFound);
        }

        // A fronteira só existe quando a operação MUDOU o estado (ou no registro posterior a ela): sem ela, o registro é de uma operação sem efeito, ou
        // anterior ao campo, e não prova mudança alguma. O período pode faltar no registro anterior a ele: a identidade fica desconhecida, nunca inventada.
        return transicao.MessageSeqAt is null
            ? new EffectView(EffectOutcome.Indeterminate, transicao.StateVersionAfter)
            : new EffectView(EffectOutcome.Applied, transicao.StateVersionAfter, transicao.MessageSeqAt, transicao.HandlingPeriodAfter);
    }

    private static int ValidateTake(int take)
    {
        if (take < 1 || take > ConversationLimits.MaxPageSize)
        {
            throw new ConversationsValidationException($"O tamanho da página deve ser de 1 a {ConversationLimits.MaxPageSize}.");
        }

        return take;
    }
}
