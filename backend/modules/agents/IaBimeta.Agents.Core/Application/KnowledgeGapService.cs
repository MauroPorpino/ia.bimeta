using IaBimeta.Agents.Domain;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Application;

/// <summary>Uma lacuna na lista da revisão: nunca traz texto de cliente.</summary>
public sealed record KnowledgeGapSummary(
    Guid Id,
    KnowledgeGapKind Kind,
    KnowledgeGapStatus Status,
    int Occurrences,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DecidedAt,
    int? PromotedVersion);

public sealed record KnowledgeGapPage(IReadOnlyList<KnowledgeGapSummary> Items, int Total);

/// <summary>Uma referência: onde a pergunta apareceu. Só identificadores.</summary>
public sealed record KnowledgeGapOccurrenceView(Guid SessionId, Guid ConversationId, Guid MessageId, DateTimeOffset OccurredAt);

/// <summary>Se o texto da pergunta pôde ser lido agora, pelo Conversations e com a autorização de quem pede.</summary>
public enum KnowledgeGapSourceState
{
    /// <summary>A mensagem foi lida: <see cref="KnowledgeGapSource.Question"/> traz o texto.</summary>
    Available = 1,

    /// <summary>A mensagem não pôde ser lida: foi removida, nunca foi visível para quem pede, ou ainda não tem texto. A lacuna continua revisável: o administrador escreve a pergunta.</summary>
    Unavailable = 2,
}

/// <summary>A resposta HUMANA que veio depois da pergunta, como SUGESTÃO: texto de um atendente que o administrador pode usar, editar ou ignorar. Nunca é usada sozinha.</summary>
/// <param name="CustomerMessagesBetween">Quantas mensagens do cliente vieram entre a pergunta e a resposta: com mais de zero, a resposta pode ser de outra pergunta, e quem revisa precisa conferir.</param>
public sealed record KnowledgeGapHumanReply(string Text, DateTimeOffset OccurredAt, int CustomerMessagesBetween);

/// <summary>O que a revisão lê do Conversations: a pergunta (o texto do cliente) e a resposta humana que a seguiu, se houver.</summary>
public sealed record KnowledgeGapSource(KnowledgeGapSourceState State, string? Question, KnowledgeGapHumanReply? HumanReply);

public sealed record KnowledgeGapDetail(KnowledgeGapSummary Gap, IReadOnlyList<KnowledgeGapOccurrenceView> Occurrences, KnowledgeGapSource Source);

/// <summary>A entrada de FAQ que o administrador montou a partir da lacuna (já editada). <paramref name="AutoReply"/> é falso por padrão e só vale se o administrador o ligar de propósito.</summary>
public sealed record PromoteKnowledgeGapRequest(string Question, IReadOnlyList<string>? Aliases, string Answer, bool AutoReply = false);

/// <summary>O resultado do envio ao rascunho: a lacuna como ficou e a versão do rascunho que recebeu a entrada. NADA foi publicado.</summary>
public sealed record KnowledgeGapPromotion(KnowledgeGapSummary Gap, int DraftVersion, int FaqEntries);

/// <summary>
/// A revisão administrativa das lacunas do FAQ (ADR 0030): listar, consultar (com a pergunta e a resposta humana lidas do Conversations COM a autorização de quem pede), descartar e transformar em entrada do RASCUNHO
/// da definição do agente. Só <see cref="Permission.TenantSettingsManage"/>; o tenant e o ator vêm do contexto confiável, nunca de parâmetro. Nada é publicado aqui: publicar continua sendo o passo auditado do
/// <see cref="AgentDefinitionService"/>, e uma versão publicada nunca é alterada. Toda decisão é auditada SEM conteúdo (só identificadores e números).
/// </summary>
public sealed class KnowledgeGapService
{
    private const string Resource = "agents.knowledge-gap";
    private const int MaxTake = 100;
    private const int LatestOccurrences = 5;
    private const int HumanReplyScan = 30;

    private readonly IRequestContextAccessor _context;
    private readonly IKnowledgeGapStore _gaps;
    private readonly AgentDefinitionService _definitions;
    private readonly IAgentDefinitionStore _definitionStore;
    private readonly IConversationQueries _conversations;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;

    public KnowledgeGapService(
        IRequestContextAccessor context,
        IKnowledgeGapStore gaps,
        AgentDefinitionService definitions,
        IAgentDefinitionStore definitionStore,
        IConversationQueries conversations,
        IAuditWriter audit,
        TimeProvider clock)
    {
        _context = context;
        _gaps = gaps;
        _definitions = definitions;
        _definitionStore = definitionStore;
        _conversations = conversations;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>As lacunas do tenant, das mais frequentes para as menos. <paramref name="status"/> nulo traz as abertas e as decididas.</summary>
    /// <exception cref="AgentsValidationException">A paginação é inválida.</exception>
    public async Task<KnowledgeGapPage> ListAsync(KnowledgeGapStatus? status, int take, int skip, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        if (take is < 1 or > MaxTake || skip < 0)
        {
            throw new AgentsValidationException($"A página aceita de 1 a {MaxTake} itens e um deslocamento não negativo.");
        }

        if (status is { } s && !Enum.IsDefined(s))
        {
            throw new AgentsValidationException("A situação da lacuna é inválida.");
        }

        var linhas = await _gaps.ListAsync(status, take, skip, cancellationToken);
        var total = await _gaps.CountAsync(status, cancellationToken);
        return new KnowledgeGapPage(linhas.Select(ToSummary).ToList(), total);
    }

    /// <summary>
    /// A lacuna com as ocorrências mais recentes e a FONTE: a pergunta e a resposta humana, lidas do Conversations com a autorização de quem pede (a mesma do histórico: leitura de conversas na organização da
    /// sessão). Mensagem removida, fora do alcance ou sem texto: a fonte vem <see cref="KnowledgeGapSourceState.Unavailable"/> e a lacuna segue revisável. Ler a fonte é auditado (sem conteúdo).
    /// </summary>
    public async Task<KnowledgeGapDetail?> GetAsync(Guid gapId, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        var lacuna = await _gaps.FindAsync(gapId, cancellationToken);
        if (lacuna is null)
        {
            return null;
        }

        var ocorrencias = await _gaps.LatestOccurrencesAsync(gapId, LatestOccurrences, cancellationToken);
        var total = await _gaps.CountOccurrencesAsync(gapId, cancellationToken);
        var fonte = await ReadSourceAsync(ocorrencias, cancellationToken);
        if (fonte.State == KnowledgeGapSourceState.Available)
        {
            await _audit.WriteAsync(new AuditEntry("agents.knowledge-gap.source-read", Resource, gapId.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return new KnowledgeGapDetail(
            ToSummary(new KnowledgeGapRow(lacuna, total)),
            ocorrencias.Select(o => new KnowledgeGapOccurrenceView(o.SessionId, o.ConversationId, o.MessageId, o.OccurredAt)).ToList(),
            fonte);
    }

    /// <summary>Descarta a lacuna (repetir não muda nada). Uma já enviada ao rascunho: conflito.</summary>
    /// <exception cref="KnowledgeGapConflictException">A lacuna já foi enviada ao rascunho, ou outra decisão aconteceu primeiro.</exception>
    public async Task<KnowledgeGapSummary?> DismissAsync(Guid gapId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);
        var lacuna = await _gaps.FindAsync(gapId, cancellationToken);
        if (lacuna is null)
        {
            return null;
        }

        var jaDescartada = lacuna.Status == KnowledgeGapStatus.Dismissed;
        try
        {
            lacuna.Dismiss(_clock.GetUtcNow(), contexto.Principal?.UserId);
        }
        catch (KnowledgeGapStateException ex)
        {
            throw new KnowledgeGapConflictException("gap-not-open", ex.Message);
        }

        if (!jaDescartada)
        {
            await _gaps.SaveAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry("agents.knowledge-gap.dismissed", Resource, gapId.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return ToSummary(new KnowledgeGapRow(lacuna, await _gaps.CountOccurrencesAsync(gapId, cancellationToken)));
    }

    /// <summary>
    /// Transforma a lacuna numa entrada do RASCUNHO da definição (o que já existe: o rascunho, senão uma cópia da versão publicada) e a marca como enviada, na MESMA transação. A entrada é a que o administrador montou
    /// (pergunta, aliases e resposta editados; <c>AutoReply</c> falso, a menos que ele o ligue). Recusa uma pergunta ou alias IGUAL (normalizado) a qualquer um já existente no FAQ do rascunho: duas respostas para a
    /// mesma pergunta nunca dariam resposta direta segura. NÃO publica e NÃO altera a versão publicada: publicar é uma ação à parte do <see cref="AgentDefinitionService"/>.
    /// </summary>
    /// <exception cref="AgentsValidationException">A entrada ou o FAQ resultante viola algum limite.</exception>
    /// <exception cref="KnowledgeGapConflictException">A lacuna não está aberta, não há definição, a pergunta já existe, ou outra alteração aconteceu primeiro.</exception>
    /// <exception cref="AgentDefinitionConflictException">O rascunho foi alterado por outra operação.</exception>
    public async Task<KnowledgeGapPromotion?> PromoteAsync(Guid gapId, PromoteKnowledgeGapRequest request, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);
        ArgumentNullException.ThrowIfNull(request);

        var lacuna = await _gaps.FindAsync(gapId, cancellationToken);
        if (lacuna is null)
        {
            return null;
        }

        if (lacuna.Status != KnowledgeGapStatus.Open)
        {
            throw new KnowledgeGapConflictException("gap-not-open", "Só uma lacuna aberta pode ser enviada ao rascunho.");
        }

        var baseDoRascunho = (await _definitions.GetDraftAsync(cancellationToken))?.Content
            ?? (await _definitions.GetPublishedAsync(cancellationToken))?.Content
            ?? throw new KnowledgeGapConflictException("no-definition", "A empresa ainda não tem definição do agente: publique uma antes de enviar entradas ao FAQ.");

        if (string.IsNullOrWhiteSpace(request.Question) || string.IsNullOrWhiteSpace(request.Answer))
        {
            throw new AgentsValidationException("A pergunta e a resposta da entrada são obrigatórias.");
        }

        var entrada = new FaqEntry(request.Question.Trim(), request.Answer.Trim(), request.Aliases?.Select(a => a?.Trim() ?? string.Empty).ToList(), request.AutoReply);
        var conflito = FindConflict(baseDoRascunho.Faq, entrada);
        if (conflito is not null)
        {
            throw new KnowledgeGapConflictException("faq-conflict", conflito);
        }

        // Validação antecipada (falha rápido, sem abrir transação). A base que realmente recebe a entrada é RELIDA dentro da transação, abaixo.
        var previa = baseDoRascunho with { Faq = baseDoRascunho.Faq.Append(entrada).ToList() };
        previa.Validate();

        var agora = _clock.GetUtcNow();
        var rascunho = await _definitionStore.InTransactionAsync(
            async () =>
            {
                // Ler-modificar-gravar ATÔMICO: a base lida fora da transação pode já ter recebido a entrada de outra promoção concorrente, e gravar o FAQ inteiro por cima a apagaria em silêncio (a lacuna
                // ficaria "enviada" sem a entrada no rascunho). Com o bloqueio da definição do tenant, as promoções passam uma por vez e cada uma relê a base já com as anteriores confirmadas.
                await _definitionStore.LockDefinitionAsync(cancellationToken);
                var baseAtual = (await _definitions.GetDraftAsync(cancellationToken))?.Content
                    ?? (await _definitions.GetPublishedAsync(cancellationToken))?.Content
                    ?? throw new KnowledgeGapConflictException("no-definition", "A empresa ainda não tem definição do agente: publique uma antes de enviar entradas ao FAQ.");
                var conflitoAtual = FindConflict(baseAtual.Faq, entrada);
                if (conflitoAtual is not null)
                {
                    throw new KnowledgeGapConflictException("faq-conflict", conflitoAtual);
                }

                var novo = baseAtual with { Faq = baseAtual.Faq.Append(entrada).ToList() };
                novo.Validate();

                // A decisão primeiro: se outro administrador decidiu a mesma lacuna, o conflito sai antes de qualquer gravação do rascunho, e a transação desfaz tudo.
                try
                {
                    lacuna.MarkPromoted(agora, contexto.Principal?.UserId);
                }
                catch (KnowledgeGapStateException ex)
                {
                    throw new KnowledgeGapConflictException("gap-not-open", ex.Message);
                }

                await _gaps.SaveAsync(cancellationToken);
                var salvo = await _definitions.SaveDraftAsync(novo, cancellationToken);
                lacuna.SetPromotedVersion(salvo.Number);
                await _gaps.SaveAsync(cancellationToken);
                return salvo;
            },
            cancellationToken);

        await _audit.WriteAsync(new AuditEntry("agents.knowledge-gap.promoted", Resource, gapId.ToString(), AuditOutcome.Success), cancellationToken);
        return new KnowledgeGapPromotion(
            ToSummary(new KnowledgeGapRow(lacuna, await _gaps.CountOccurrencesAsync(gapId, cancellationToken))),
            rascunho.Number,
            rascunho.Content.Faq.Count);
    }

    /// <summary>A mensagem do cliente e a primeira resposta HUMANA depois dela, da ocorrência mais recente cuja mensagem ainda se lê. Sem leitura possível: indisponível.</summary>
    private async Task<KnowledgeGapSource> ReadSourceAsync(IReadOnlyList<KnowledgeGapOccurrence> ocorrencias, CancellationToken cancellationToken)
    {
        foreach (var ocorrencia in ocorrencias)
        {
            MessageView? mensagem;
            try
            {
                var pagina = await _conversations.GetHistoryAsync(
                    new GetHistoryQuery(ocorrencia.SessionId, 1, null, HistoryOrder.NewestFirst, MessageId: ocorrencia.MessageId), cancellationToken);
                mensagem = pagina?.Items.FirstOrDefault(m => m.Id == ocorrencia.MessageId);
            }
            catch (AccessDeniedException)
            {
                continue;
            }

            if (mensagem is null || mensagem.Direction != MessageDirection.Inbound || mensagem.Kind != MessageKind.Text
                || mensagem.ContentState != ContentState.Ready || string.IsNullOrWhiteSpace(mensagem.Text))
            {
                continue;
            }

            return new KnowledgeGapSource(KnowledgeGapSourceState.Available, mensagem.Text, await FindHumanReplyAsync(mensagem, cancellationToken));
        }

        return new KnowledgeGapSource(KnowledgeGapSourceState.Unavailable, null, null);
    }

    /// <summary>A primeira mensagem de saída de um ATENDENTE (não da IA) depois da pergunta, olhando poucas mensagens: a sugestão de resposta. As mensagens do cliente no meio são contadas, porque tornam a associação incerta.</summary>
    private async Task<KnowledgeGapHumanReply?> FindHumanReplyAsync(MessageView pergunta, CancellationToken cancellationToken)
    {
        Page<MessageView>? pagina;
        try
        {
            pagina = await _conversations.GetHistoryAsync(
                new GetHistoryQuery(pergunta.SessionId, HumanReplyScan, null, HistoryOrder.OldestFirst, AfterMessageSeq: pergunta.MessageSeq), cancellationToken);
        }
        catch (AccessDeniedException)
        {
            return null;
        }

        var entre = 0;
        foreach (var mensagem in pagina?.Items ?? Array.Empty<MessageView>())
        {
            if (mensagem.Direction == MessageDirection.Inbound)
            {
                entre++;
                continue;
            }

            if (mensagem.Author == MessageAuthor.Human && mensagem.Kind == MessageKind.Text && mensagem.ContentState == ContentState.Ready && !string.IsNullOrWhiteSpace(mensagem.Text))
            {
                return new KnowledgeGapHumanReply(mensagem.Text, mensagem.OccurredAt, entre);
            }
        }

        return null;
    }

    /// <summary>O motivo de a entrada nova colidir com o FAQ existente (a pergunta ou um alias igual, depois de normalizado, a qualquer pergunta ou alias), ou nulo.</summary>
    private static string? FindConflict(IReadOnlyList<FaqEntry> existentes, FaqEntry nova)
    {
        var usados = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existente in existentes)
        {
            usados.Add(FaqMatcher.Normalize(existente.Question));
            foreach (var alias in existente.GetAliases())
            {
                usados.Add(FaqMatcher.Normalize(alias));
            }
        }

        usados.Remove(string.Empty);
        var proprios = new[] { nova.Question }.Concat(nova.GetAliases()).Select(FaqMatcher.Normalize).Where(t => t.Length > 0);
        return proprios.Any(usados.Contains)
            ? "O FAQ já tem uma entrada com essa pergunta (ou um alias igual). Duas entradas para a mesma pergunta nunca dão resposta direta: ajuste a existente em vez de criar outra."
            : null;
    }

    private static KnowledgeGapSummary ToSummary(KnowledgeGapRow linha) =>
        new(linha.Gap.Id, linha.Gap.Kind, linha.Gap.Status, linha.Occurrences, linha.Gap.FirstSeenAt, linha.Gap.LastSeenAt, linha.Gap.DecidedAt, linha.Gap.PromotedVersion);
}
