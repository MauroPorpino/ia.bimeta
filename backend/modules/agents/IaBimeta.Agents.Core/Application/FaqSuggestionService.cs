using System.Security.Cryptography;
using System.Text;
using IaBimeta.Agents.Domain;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Application;

/// <summary>O armazenamento das sugestões do tenant do contexto (filtro global por tenant). A entidade volta RASTREADA para a decisão.</summary>
public interface IFaqSuggestionStore
{
    Task<IReadOnlyList<FaqSuggestion>> ListAsync(FaqSuggestionStatus? status, FaqSuggestionKind? kind, int take, int skip, CancellationToken cancellationToken);

    Task<int> CountAsync(FaqSuggestionStatus? status, FaqSuggestionKind? kind, CancellationToken cancellationToken);

    Task<FaqSuggestion?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A sugestão PENDENTE com esta chave de deduplicação, se houver.</summary>
    Task<FaqSuggestion?> FindPendingByKeyAsync(string dedupeKey, CancellationToken cancellationToken);

    void Add(FaqSuggestion suggestion);

    /// <exception cref="FaqSuggestionConflictException">Outra decisão ou outra sugestão igual aconteceu primeiro.</exception>
    Task SaveAsync(CancellationToken cancellationToken);
}

public sealed class FaqSuggestionConflictException : Exception
{
    public FaqSuggestionConflictException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary><c>suggestion-changed</c>, <c>suggestion-decided</c>, <c>target-not-found</c>, <c>faq-conflict</c>, <c>answer-required</c>, <c>no-definition</c>, <c>gap-not-open</c>.</summary>
    public string Code { get; }
}

/// <summary>A IA local não pode gerar agora (desligada, não configurada, fora do ar, tempo esgotado). Nada foi gravado; o código é curto e fixo, nunca texto do provedor nem da conversa.</summary>
public sealed class FaqSuggestionUnavailableException : Exception
{
    public FaqSuggestionUnavailableException(string code)
        : base("A IA local não está disponível agora. Tente de novo mais tarde.")
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record FaqSuggestionView(
    Guid Id,
    FaqSuggestionKind Kind,
    FaqSuggestionStatus Status,
    Guid? GapId,
    SuggestionPayload Payload,
    string ModelId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    int? AppliedDraftVersion,
    int Edits,
    int Revision);

public sealed record FaqSuggestionPage(IReadOnlyList<FaqSuggestionView> Items, int Total);

/// <summary>O resultado de gerar: as sugestões criadas (ou a já pendente, sem nova chamada) e quantas o modelo recusou, sem texto.</summary>
public sealed record FaqSuggestionGeneration(IReadOnlyList<FaqSuggestionView> Suggestions, int Skipped, string? SkipReason);

/// <summary>O que o administrador pode ajustar antes de aceitar. Campo nulo = não muda.</summary>
public sealed record FaqSuggestionEdit(string? Question, string? Answer, IReadOnlyList<string>? Aliases);

/// <summary>
/// As sugestões da IA LOCAL para o FAQ (ADR 0034). A IA só PROPÕE; esta classe é a revisão humana: gerar (sob pedido, nunca por conta própria), listar, editar, aceitar (o efeito vai para o RASCUNHO, nunca publica) e
/// rejeitar, tudo auditado SEM conteúdo, com permissão de configurar o tenant. O texto da conversa só chega ao modelo depois da redação de dados pessoais; a pergunta e a resposta humana são lidas pelo
/// <see cref="KnowledgeGapService"/>, com a autorização de quem pede. Aceitar uma entrada nova nunca liga a resposta direta (<c>AutoReply</c> é decisão manual, depois) e uma entrada sem resposta escrita por
/// uma pessoa nunca é aceita. O modelo nunca recebe instrução vinda de cliente: o texto é dado, a saída é JSON de esquema fechado e cada campo é conferido (<see cref="SuggestionGeneration"/>).
/// </summary>
public sealed class FaqSuggestionService
{
    private const string Resource = "agents.faq-suggestion";
    private const int MaxTake = 100;

    private readonly IRequestContextAccessor _context;
    private readonly IFaqSuggestionStore _store;
    private readonly KnowledgeGapService _gaps;
    private readonly AgentDefinitionService _definitions;
    private readonly IAuditWriter _audit;
    private readonly AgentsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILocalGenerationProvider? _local;

    public FaqSuggestionService(
        IRequestContextAccessor context,
        IFaqSuggestionStore store,
        KnowledgeGapService gaps,
        AgentDefinitionService definitions,
        IAuditWriter audit,
        AgentsOptions options,
        TimeProvider clock,
        ILocalGenerationProvider? local = null)
    {
        _context = context;
        _store = store;
        _gaps = gaps;
        _definitions = definitions;
        _audit = audit;
        _options = options;
        _clock = clock;
        _local = local;
    }

    public async Task<FaqSuggestionPage> ListAsync(FaqSuggestionStatus? status, FaqSuggestionKind? kind, int take, int skip, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        if (take is < 1 or > MaxTake || skip < 0 || (status is { } s && !Enum.IsDefined(s)) || (kind is { } k && !Enum.IsDefined(k)))
        {
            throw new AgentsValidationException($"A página aceita de 1 a {MaxTake} itens e um deslocamento não negativo, com situação e tipo válidos.");
        }

        var itens = await _store.ListAsync(status, kind, take, skip, cancellationToken);
        return new FaqSuggestionPage(itens.Select(ToView).ToList(), await _store.CountAsync(status, kind, cancellationToken));
    }

    public async Task<FaqSuggestionView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        var sugestao = await _store.FindAsync(id, cancellationToken);
        return sugestao is null ? null : ToView(sugestao);
    }

    /// <summary>
    /// Pede à IA local uma sugestão para UMA lacuna aberta: uma entrada nova, aliases para uma entrada existente, ou nada. Já havendo uma sugestão pendente para a lacuna, devolve-a SEM nova chamada ao modelo.
    /// </summary>
    /// <exception cref="FaqSuggestionUnavailableException">A IA local está desligada ou indisponível.</exception>
    public async Task<FaqSuggestionGeneration?> GenerateForGapAsync(Guid gapId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);
        EnsureAvailable();

        var chave = $"gap:{gapId}";
        var existente = await _store.FindPendingByKeyAsync(chave, cancellationToken);
        if (existente is not null)
        {
            return new FaqSuggestionGeneration([ToView(existente)], 0, null);
        }

        var detalhe = await _gaps.GetAsync(gapId, cancellationToken);
        if (detalhe is null)
        {
            return null;
        }

        if (detalhe.Gap.Status != KnowledgeGapStatus.Open)
        {
            throw new FaqSuggestionConflictException("gap-not-open", "Só uma lacuna aberta recebe sugestão.");
        }

        if (detalhe.Source.State != KnowledgeGapSourceState.Available || string.IsNullOrWhiteSpace(detalhe.Source.Question))
        {
            return new FaqSuggestionGeneration([], 1, "source-unavailable");
        }

        var faq = await CurrentFaqAsync(cancellationToken);
        var pergunta = PiiRedactor.Redact(detalhe.Source.Question);
        var resposta = detalhe.Source.HumanReply is { CustomerMessagesBetween: 0 } r ? PiiRedactor.Redact(r.Text) : null; // resposta com mensagens do cliente no meio é incerta: não vai ao modelo
        if (pergunta.Length == 0)
        {
            return new FaqSuggestionGeneration([], 1, "empty-after-redaction");
        }

        var saida = await GenerateAsync(SuggestionGeneration.BuildSingle(faq, pergunta, resposta), cancellationToken);
        var analise = SuggestionGeneration.ParseSingle(saida.Json, faq, !string.IsNullOrWhiteSpace(resposta));
        if (analise.Kind is null)
        {
            return new FaqSuggestionGeneration([], 1, analise.SkipReason);
        }

        var payload = analise.Payload! with { GapIds = [gapId] };
        var criada = await AddAsync(analise.Kind.Value, gapId, chave, payload, cancellationToken);
        return new FaqSuggestionGeneration([ToView(criada)], 0, null);
    }

    /// <summary>
    /// Agrupa as lacunas ABERTAS mais frequentes que perguntam a mesma coisa e cria uma sugestão de entrada nova por grupo (a resposta fica em branco: quem escreve é uma pessoa). Lê o texto de cada lacuna com a
    /// autorização de quem pede e o redige antes de enviar ao modelo. No máximo <see cref="SuggestionGeneration.MaxClusterQuestions"/> lacunas por pedido.
    /// </summary>
    public async Task<FaqSuggestionGeneration> ClusterOpenGapsAsync(CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        EnsureAvailable();
        var abertas = await _gaps.ListAsync(KnowledgeGapStatus.Open, SuggestionGeneration.MaxClusterQuestions, 0, cancellationToken);

        var perguntas = new List<(Guid GapId, string Text)>();
        foreach (var resumo in abertas.Items)
        {
            var detalhe = await _gaps.GetAsync(resumo.Id, cancellationToken);
            if (detalhe?.Source is { State: KnowledgeGapSourceState.Available, Question: { } texto } && PiiRedactor.Redact(texto) is { Length: > 0 } limpo)
            {
                perguntas.Add((resumo.Id, limpo));
            }
        }

        if (perguntas.Count < 2)
        {
            return new FaqSuggestionGeneration([], 0, "not-enough-gaps");
        }

        var faq = await CurrentFaqAsync(cancellationToken);
        var saida = await GenerateAsync(SuggestionGeneration.BuildCluster(faq, perguntas.Select(p => p.Text).ToList()), cancellationToken);
        var grupos = SuggestionGeneration.ParseCluster(saida.Json, faq, perguntas.Count);

        var criadas = new List<FaqSuggestionView>();
        foreach (var grupo in grupos)
        {
            var ids = grupo.Members.Select(i => perguntas[i].GapId).OrderBy(g => g).ToList();
            var chave = "cluster:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', ids)))).ToLowerInvariant()[..32];
            if (await _store.FindPendingByKeyAsync(chave, cancellationToken) is { } ja)
            {
                criadas.Add(ToView(ja));
                continue;
            }

            var principal = grupo.Members.Select(i => perguntas[i].GapId).First();
            criadas.Add(ToView(await AddAsync(FaqSuggestionKind.NewFaq, principal, chave, grupo.Payload with { GapIds = ids }, cancellationToken)));
        }

        return new FaqSuggestionGeneration(criadas, 0, criadas.Count == 0 ? "no-groups" : null);
    }

    /// <summary>Analisa o FAQ do rascunho (senão o publicado) e cria uma sugestão para cada par de entradas duplicadas ou contraditórias que o modelo apontar. Só informativas: aceitar é reconhecer o achado.</summary>
    public async Task<FaqSuggestionGeneration> AnalyzeFaqAsync(CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        EnsureAvailable();
        var faq = await CurrentFaqAsync(cancellationToken);
        if (faq.Count < 2)
        {
            return new FaqSuggestionGeneration([], 0, "not-enough-entries");
        }

        var saida = await GenerateAsync(SuggestionGeneration.BuildAnalysis(faq), cancellationToken);
        var achados = SuggestionGeneration.ParseAnalysis(saida.Json, faq.Count);
        var criadas = new List<FaqSuggestionView>();
        foreach (var achado in achados)
        {
            var a = faq[achado.A].Question;
            var b = faq[achado.B].Question;
            var par = string.CompareOrdinal(FaqMatcher.Normalize(a), FaqMatcher.Normalize(b)) <= 0 ? (a, b) : (b, a);
            var chave = $"{(achado.Kind == FaqSuggestionKind.Duplicate ? "dup" : "contra")}:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FaqMatcher.Normalize(par.Item1) + "|" + FaqMatcher.Normalize(par.Item2)))).ToLowerInvariant()[..32];
            if (await _store.FindPendingByKeyAsync(chave, cancellationToken) is { } ja)
            {
                criadas.Add(ToView(ja));
                continue;
            }

            criadas.Add(ToView(await AddAsync(achado.Kind, null, chave, new SuggestionPayload(null, null, null, par.Item1, par.Item2, null, achado.Rationale), cancellationToken)));
        }

        return new FaqSuggestionGeneration(criadas, 0, criadas.Count == 0 ? "nothing-found" : null);
    }

    /// <summary>Edita o conteúdo de uma sugestão pendente. Os limites são os do FAQ; o texto editado por uma pessoa não passa pela redação (a responsabilidade é de quem revisa).</summary>
    public async Task<FaqSuggestionView?> EditAsync(Guid id, FaqSuggestionEdit edit, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        ArgumentNullException.ThrowIfNull(edit);
        var sugestao = await _store.FindAsync(id, cancellationToken);
        if (sugestao is null)
        {
            return null;
        }

        var atual = sugestao.Payload;
        var novo = atual with
        {
            Question = edit.Question is null ? atual.Question : edit.Question.Trim(),
            Answer = edit.Answer is null ? atual.Answer : edit.Answer.Trim(),
            Aliases = edit.Aliases is null ? atual.Aliases : edit.Aliases.Select(a => a?.Trim() ?? string.Empty).Where(a => a.Length > 0).ToList(),
        };
        if ((novo.Question?.Length ?? 0) > AgentDefinitionContent.QuestionMaxLength || (novo.Answer?.Length ?? 0) > SuggestionGeneration.MaxAnswerLength || (novo.Aliases?.Count ?? 0) > AgentDefinitionContent.AliasMaxCount
            || (novo.Aliases?.Any(a => a.Length > AgentDefinitionContent.QuestionMaxLength) ?? false))
        {
            throw new AgentsValidationException("A pergunta, a resposta ou os aliases passam do tamanho permitido.");
        }

        try
        {
            sugestao.Edit(novo, _clock.GetUtcNow());
        }
        catch (FaqSuggestionStateException ex)
        {
            throw new FaqSuggestionConflictException("suggestion-decided", ex.Message);
        }

        await _store.SaveAsync(cancellationToken);
        await _audit.WriteAsync(new AuditEntry("agents.faq-suggestion.edited", Resource, id.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(sugestao);
    }

    /// <summary>
    /// Aceita a sugestão: uma entrada nova ou os aliases vão para o RASCUNHO (versionado, auditado; nada é publicado, e <c>AutoReply</c> fica desligado); duplicata e contradição só são reconhecidas. As demais lacunas
    /// de um grupo aceito são marcadas como descartadas (já cobertas pela entrada nova).
    /// </summary>
    public async Task<FaqSuggestionView?> ApproveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);
        var sugestao = await _store.FindAsync(id, cancellationToken);
        if (sugestao is null)
        {
            return null;
        }

        if (sugestao.Status != FaqSuggestionStatus.Pending)
        {
            throw new FaqSuggestionConflictException("suggestion-decided", "A sugestão já foi decidida.");
        }

        var payload = sugestao.Payload;
        int? versao = sugestao.Kind switch
        {
            FaqSuggestionKind.NewFaq => await ApplyNewFaqAsync(sugestao, payload, cancellationToken),
            FaqSuggestionKind.Alias => await ApplyAliasAsync(payload, cancellationToken),
            _ => null,
        };

        try
        {
            sugestao.Approve(_clock.GetUtcNow(), contexto.Principal?.UserId, versao);
        }
        catch (FaqSuggestionStateException ex)
        {
            throw new FaqSuggestionConflictException("suggestion-decided", ex.Message);
        }

        await _store.SaveAsync(cancellationToken);
        await _audit.WriteAsync(new AuditEntry("agents.faq-suggestion.approved", Resource, id.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(sugestao);
    }

    public async Task<FaqSuggestionView?> RejectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);
        var sugestao = await _store.FindAsync(id, cancellationToken);
        if (sugestao is null)
        {
            return null;
        }

        try
        {
            sugestao.Reject(_clock.GetUtcNow(), contexto.Principal?.UserId);
        }
        catch (FaqSuggestionStateException ex)
        {
            throw new FaqSuggestionConflictException("suggestion-decided", ex.Message);
        }

        await _store.SaveAsync(cancellationToken);
        await _audit.WriteAsync(new AuditEntry("agents.faq-suggestion.rejected", Resource, id.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(sugestao);
    }

    private async Task<int?> ApplyNewFaqAsync(FaqSuggestion sugestao, SuggestionPayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.Question) || string.IsNullOrWhiteSpace(payload.Answer))
        {
            throw new FaqSuggestionConflictException("answer-required", "Escreva a resposta aprovada antes de aceitar: a IA nunca responde por conta própria.");
        }

        if (sugestao.GapId is not { } lacuna)
        {
            throw new FaqSuggestionConflictException("gap-not-open", "A sugestão não tem lacuna de origem.");
        }

        KnowledgeGapPromotion? promocao;
        try
        {
            promocao = await _gaps.PromoteAsync(lacuna, new PromoteKnowledgeGapRequest(payload.Question, payload.Aliases, payload.Answer, AutoReply: false), cancellationToken);
        }
        catch (KnowledgeGapConflictException ex)
        {
            throw new FaqSuggestionConflictException(ex.Code, ex.Message);
        }

        if (promocao is null)
        {
            throw new FaqSuggestionConflictException("gap-not-open", "A lacuna de origem não existe mais.");
        }

        // As outras lacunas do grupo ficam cobertas pela entrada nova: a decisão é do hash, então as próximas ocorrências só contam.
        foreach (var outra in (payload.GapIds ?? []).Where(g => g != lacuna))
        {
            try
            {
                await _gaps.DismissAsync(outra, cancellationToken);
            }
            catch (KnowledgeGapConflictException)
            {
                // Já decidida por outra operação: nada a fazer.
            }
        }

        return promocao.DraftVersion;
    }

    private async Task<int?> ApplyAliasAsync(SuggestionPayload payload, CancellationToken cancellationToken)
    {
        var baseDoRascunho = (await _definitions.GetDraftAsync(cancellationToken))?.Content
            ?? (await _definitions.GetPublishedAsync(cancellationToken))?.Content
            ?? throw new FaqSuggestionConflictException("no-definition", "A empresa ainda não tem definição do agente.");

        var alvo = FaqMatcher.Normalize(payload.TargetQuestion);
        var indice = alvo.Length == 0 ? -1 : baseDoRascunho.Faq.ToList().FindIndex(e => FaqMatcher.Normalize(e.Question) == alvo);
        if (indice < 0)
        {
            throw new FaqSuggestionConflictException("target-not-found", "A entrada que receberia os aliases não existe mais no FAQ.");
        }

        var novos = (payload.Aliases ?? []).Where(a => FaqMatcher.Normalize(a).Length > 0).ToList();
        if (novos.Count == 0)
        {
            throw new AgentsValidationException("Não há alias para acrescentar.");
        }

        var usados = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < baseDoRascunho.Faq.Count; i++)
        {
            usados.Add(FaqMatcher.Normalize(baseDoRascunho.Faq[i].Question));
            foreach (var a in baseDoRascunho.Faq[i].GetAliases())
            {
                usados.Add(FaqMatcher.Normalize(a));
            }
        }

        if (novos.Any(a => usados.Contains(FaqMatcher.Normalize(a))))
        {
            throw new FaqSuggestionConflictException("faq-conflict", "Um dos aliases já existe no FAQ: duas entradas para a mesma pergunta nunca dão resposta direta segura.");
        }

        var entrada = baseDoRascunho.Faq[indice];
        var faq = baseDoRascunho.Faq.ToList();
        faq[indice] = entrada with { Aliases = entrada.GetAliases().Concat(novos).ToList() };
        var salvo = await _definitions.SaveDraftAsync(baseDoRascunho with { Faq = faq }, cancellationToken);
        return salvo.Number;
    }

    private async Task<IReadOnlyList<FaqEntry>> CurrentFaqAsync(CancellationToken cancellationToken)
    {
        var conteudo = (await _definitions.GetDraftAsync(cancellationToken))?.Content ?? (await _definitions.GetPublishedAsync(cancellationToken))?.Content;
        return conteudo?.Faq ?? [];
    }

    /// <summary>
    /// A IA local precisa estar ligada e configurada ANTES de qualquer leitura de conversa: desligada, nada de texto de cliente e nenhuma auditoria de leitura de fonte acontecem, e a resposta é sempre a mesma
    /// (503 com código fixo), qualquer que seja o estado das lacunas ou do FAQ.
    /// </summary>
    private void EnsureAvailable()
    {
        if (!_options.LocalSuggestionsEnabled)
        {
            throw new FaqSuggestionUnavailableException("disabled");
        }

        if (_local is null)
        {
            throw new FaqSuggestionUnavailableException("not-configured");
        }
    }

    private async Task<LocalGenerationResult> GenerateAsync(LocalGenerationRequest pedido, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        if (_local is null)
        {
            throw new FaqSuggestionUnavailableException("not-configured");
        }

        try
        {
            return await _local.GenerateJsonAsync(pedido, cancellationToken);
        }
        catch (LocalGenerationUnavailableException ex)
        {
            throw new FaqSuggestionUnavailableException(ex.Code);
        }
    }

    private async Task<FaqSuggestion> AddAsync(FaqSuggestionKind tipo, Guid? lacuna, string chave, SuggestionPayload payload, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var sugestao = FaqSuggestion.Create(contexto.TenantId, tipo, lacuna, chave, payload, _local?.ModelId is { Length: > 0 } m ? m : "local", _clock.GetUtcNow(), contexto.Principal?.UserId);
        _store.Add(sugestao);
        await _store.SaveAsync(cancellationToken);
        await _audit.WriteAsync(new AuditEntry("agents.faq-suggestion.generated", Resource, sugestao.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return sugestao;
    }

    private static FaqSuggestionView ToView(FaqSuggestion s) =>
        new(s.Id, s.Kind, s.Status, s.GapId, s.Payload, s.ModelId, s.CreatedAt, s.DecidedAt, s.AppliedDraftVersion, s.Edits, s.Revision);
}
