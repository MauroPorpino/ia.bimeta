using IaBimeta.Agents.Domain;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.Workflows.Contracts;

namespace IaBimeta.Agents.Application;

/// <summary>
/// A porta de conhecimento do chatbot visual (ADR 0037, nó <c>knowledge</c>): REUTILIZA o conhecimento aprovado que o agente já tem (o FAQ publicado do tenant e, se as três chaves estiverem ligadas, o índice semântico
/// do Knowledge). Não há outro mecanismo de conhecimento. É determinística e somente leitura: o FAQ responde só quando EXATAMENTE UMA entrada (com resposta direta) é igual à pergunta normalizada; senão, a busca
/// semântica só estreita a escolha entre as entradas APROVADAS (mesma política do turno de IA, sem registrar decisão nem lacuna); em qualquer dúvida, "não achou". Nunca chama modelo de geração. O conhecimento
/// consultado é o do tenant do contexto (o <c>RequestContext</c> do host), nunca de outro.
/// </summary>
public sealed class FlowKnowledgeLookup : IFlowKnowledgeLookup
{
    private readonly IAgentDefinitionReader _definitions;
    private readonly AgentsOptions _options;
    private readonly IKnowledgeSearch? _search;
    private readonly ISemanticAnswerSettingsStore? _semanticSettings;

    public FlowKnowledgeLookup(IAgentDefinitionReader definitions, AgentsOptions options, IKnowledgeSearch? search = null, ISemanticAnswerSettingsStore? semanticSettings = null)
    {
        _definitions = definitions;
        _options = options;
        _search = search;
        _semanticSettings = semanticSettings;
    }

    public async Task<FlowKnowledgeResult> LookupAsync(FlowKnowledgeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return new FlowKnowledgeResult(false, null);
        }

        var publicada = await _definitions.GetPublishedVersionAsync(cancellationToken);
        if (publicada is null)
        {
            var conteudo = await _definitions.GetPublishedAsync(cancellationToken);
            return conteudo is null ? new FlowKnowledgeResult(false, null) : Direct(FaqMatcher.Match(conteudo.Faq, query.Text));
        }

        var achado = FaqMatcher.Match(publicada.Content.Faq, query.Text);
        if (achado.Kind == FaqMatchKind.Direct)
        {
            return Direct(achado);
        }

        // Só quando o FAQ determinístico não achou NADA igual a busca semântica pode estreitar a escolha (nunca com duplicata nem entrada sem resposta direta).
        if (achado.Kind != FaqMatchKind.NoMatch || !_options.SemanticAnswerEnabled || _search is null || _semanticSettings is null)
        {
            return new FlowKnowledgeResult(false, null);
        }

        try
        {
            var configuracao = await _semanticSettings.FindAsync(cancellationToken);
            var modo = configuracao?.Mode ?? SemanticAnswerMode.Off;
            if (modo != SemanticAnswerMode.Active)
            {
                return new FlowKnowledgeResult(false, null);
            }

            var busca = await _search.SearchAsync(new SemanticSearchRequest(PiiRedactor.Redact(query.Text), publicada.Content.EffectiveFaqLocale.ToString(), OnlyAutoReplyEligible: false, Limit: 5), cancellationToken);
            if (busca.Outcome == SemanticSearchOutcome.Disabled)
            {
                return new FlowKnowledgeResult(false, null);
            }

            var decisao = SemanticAnswerPolicy.Decide(
                busca, publicada.Content, publicada.Number, query.Text,
                configuracao?.MinScore ?? SemanticAnswerSettings.DefaultMinScore, configuracao?.MinMargin ?? SemanticAnswerSettings.DefaultMinMargin, answer: true);
            return decisao.Outcome == SemanticOutcome.Answered && decisao.Entry is { } entrada ? new FlowKnowledgeResult(true, entrada.Answer) : new FlowKnowledgeResult(false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Melhor esforço: a busca semântica nunca derruba o fluxo; sem ela, "não achou".
            return new FlowKnowledgeResult(false, null);
        }
    }

    private static FlowKnowledgeResult Direct(FaqMatch achado) =>
        achado.Kind == FaqMatchKind.Direct && achado.Entry is { } entrada ? new FlowKnowledgeResult(true, entrada.Answer) : new FlowKnowledgeResult(false, null);
}
