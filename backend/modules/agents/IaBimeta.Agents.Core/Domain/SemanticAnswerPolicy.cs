using IaBimeta.Knowledge.Contracts;

namespace IaBimeta.Agents.Domain;

/// <summary>A decisão da política: o desfecho e, quando houve candidato, qual entrada, com que similaridade e a margem para a segunda.</summary>
public sealed record SemanticPolicyDecision(SemanticOutcome Outcome, int? EntryOrdinal, double? Score, double? Margin, FaqEntry? Entry)
{
    public bool ShouldAnswer => Outcome is SemanticOutcome.Answered or SemanticOutcome.WouldAnswer;
}

/// <summary>
/// A política da resposta direta por similaridade (ADR 0034), pura: sem I/O, relógio nem modelo. A similaridade NUNCA é, sozinha, a garantia de que a resposta está certa; ela só pode ESTREITAR a escolha entre
/// entradas já aprovadas, e a resposta é sempre o texto aprovado da entrada (nunca reescrito). Responde direto só quando TODAS as condições valem, na ordem:
/// índice utilizável e da MESMA versão da definição publicada agora; melhor candidato com similaridade de pelo menos o limiar do tenant; nenhuma OUTRA entrada a menos da margem do melhor (concorrência);
/// a entrada com <c>AutoReply</c> (a mesma regra do FAQ determinístico); e as salvaguardas determinísticas da mensagem (<see cref="SemanticGuards"/>). Qualquer dúvida devolve um desfecho que NÃO responde.
/// </summary>
public static class SemanticAnswerPolicy
{
    public static SemanticPolicyDecision Decide(
        SemanticSearchResult search, AgentDefinitionContent published, int? publishedNumber, string customerText, double minScore, double minMargin, bool answer)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(published);

        if (search.Outcome is SemanticSearchOutcome.Unavailable or SemanticSearchOutcome.Disabled)
        {
            return Of(SemanticOutcome.Unavailable);
        }

        if (search.Outcome == SemanticSearchOutcome.NoCandidates || search.Candidates.Count == 0)
        {
            return Of(SemanticOutcome.NoCandidates);
        }

        // Índice de outra versão: os números de entrada podem apontar para outra pergunta. Nunca responde (a próxima publicação ou a vetorização o conserta).
        if (publishedNumber is null || search.DefinitionNumber != publishedNumber)
        {
            return Of(SemanticOutcome.Stale);
        }

        var melhor = search.Candidates[0];
        if (melhor.EntryOrdinal < 0 || melhor.EntryOrdinal >= published.Faq.Count)
        {
            return Of(SemanticOutcome.Stale);
        }

        // O melhor candidato de OUTRA entrada (a busca já devolve uma linha por entrada, ordenada).
        var concorrente = search.Candidates.Skip(1).FirstOrDefault(c => c.EntryOrdinal != melhor.EntryOrdinal);
        var margem = concorrente is null ? (double?)null : Math.Round(melhor.Score - concorrente.Score, 4);

        if (melhor.Score < minScore)
        {
            return new SemanticPolicyDecision(SemanticOutcome.BelowThreshold, melhor.EntryOrdinal, melhor.Score, margem, null);
        }

        if (concorrente is not null && melhor.Score - concorrente.Score < minMargin)
        {
            return new SemanticPolicyDecision(SemanticOutcome.Ambiguous, melhor.EntryOrdinal, melhor.Score, margem, null);
        }

        var entrada = published.Faq[melhor.EntryOrdinal];
        if (!entrada.AutoReply)
        {
            return new SemanticPolicyDecision(SemanticOutcome.NotAutoReply, melhor.EntryOrdinal, melhor.Score, margem, null);
        }

        var texto = IndexedText(entrada, melhor);
        if (texto is null || !SemanticGuards.Passes(customerText, texto))
        {
            return new SemanticPolicyDecision(SemanticOutcome.GuardRejected, melhor.EntryOrdinal, melhor.Score, margem, null);
        }

        return new SemanticPolicyDecision(answer ? SemanticOutcome.Answered : SemanticOutcome.WouldAnswer, melhor.EntryOrdinal, melhor.Score, margem, entrada);
    }

    /// <summary>O texto aprovado (pergunta ou alias) que o candidato representa, na mesma posição em que a publicação o indexou.</summary>
    private static string? IndexedText(FaqEntry entry, SemanticCandidate candidate)
    {
        if (candidate.Kind == SemanticTextKind.Question)
        {
            return entry.Question;
        }

        var aliases = entry.GetCleanAliases();
        var posicao = candidate.AliasOrdinal - 1;
        return posicao >= 0 && posicao < aliases.Count ? aliases[posicao] : null;
    }

    private static SemanticPolicyDecision Of(SemanticOutcome outcome) => new(outcome, null, null, null, null);
}

/// <summary>
/// As salvaguardas DETERMINÍSTICAS que valem mesmo com similaridade alta (ADR 0034), porque um vetor aproxima "preciso de jejum" de "NÃO preciso de jejum" e "exame 2" de "exame 3". Recusam (e o turno segue ao
/// modelo, que lê o FAQ inteiro) quando a mensagem: tem várias perguntas; é longa demais para uma pergunta única; muda a polaridade (negação presente de um lado só); ou cita números diferentes dos do texto aprovado.
/// </summary>
public static class SemanticGuards
{
    public const int MaxWords = 30;

    public const int MaxCharacters = 240;

    // Já normalizadas (caixa baixa, sem acento). Só palavras inteiras.
    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "nao", "nunca", "jamais", "sem", "nem", "nada", "ninguem", "nenhum", "nenhuma", "tampouco",
        "not", "no", "never", "without", "dont", "cant", "wont", "cannot", "none", "nobody", "nothing",
        "nunca", "ni", "sin", "tampoco", "nadie", "ninguno", "ninguna",
    };

    public static bool Passes(string customerText, string approvedText)
    {
        if (string.IsNullOrWhiteSpace(customerText) || string.IsNullOrWhiteSpace(approvedText))
        {
            return false;
        }

        if (customerText.Length > MaxCharacters || customerText.Count(c => c == '?' || c == '？') > 1)
        {
            return false;
        }

        var cliente = Tokens(customerText);
        var aprovado = Tokens(approvedText);
        if (cliente.Count == 0 || cliente.Count > MaxWords)
        {
            return false;
        }

        if (cliente.Any(Negations.Contains) != aprovado.Any(Negations.Contains))
        {
            return false;
        }

        var numerosCliente = cliente.Where(t => t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var numerosAprovado = aprovado.Where(t => t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        return numerosCliente.SetEquals(numerosAprovado);
    }

    private static List<string> Tokens(string text) =>
        FaqMatcher.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
}
