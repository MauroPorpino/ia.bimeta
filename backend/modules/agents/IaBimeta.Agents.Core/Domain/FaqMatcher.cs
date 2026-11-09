using System.Globalization;
using System.Text;

namespace IaBimeta.Agents.Domain;

/// <summary>O desfecho da busca de uma mensagem no FAQ (ADR 0029).</summary>
public enum FaqMatchKind
{
    /// <summary>Nenhuma entrada tem a pergunta ou um alias igual à mensagem: vai ao modelo.</summary>
    NoMatch = 1,

    /// <summary>Exatamente UMA entrada corresponde e ela liga a resposta direta: responde sem chamar o modelo.</summary>
    Direct = 2,

    /// <summary>Exatamente uma entrada corresponde, mas ela NÃO liga a resposta direta: vai ao modelo (que já tem o FAQ no prompt).</summary>
    NotAutoReply = 3,

    /// <summary>Duas ou mais entradas correspondem à mesma mensagem: não há resposta direta segura, vai ao modelo.</summary>
    Ambiguous = 4,
}

/// <summary>O resultado da busca; <see cref="Entry"/> só existe quando <see cref="Kind"/> é <see cref="FaqMatchKind.Direct"/> ou <see cref="FaqMatchKind.NotAutoReply"/>.</summary>
public sealed record FaqMatch(FaqMatchKind Kind, FaqEntry? Entry = null)
{
    public static readonly FaqMatch None = new(FaqMatchKind.NoMatch);

    public static readonly FaqMatch Conflict = new(FaqMatchKind.Ambiguous);
}

/// <summary>
/// A busca DETERMINÍSTICA da mensagem do cliente no FAQ publicado do tenant (ADR 0029), sem I/O, sem relógio e sem modelo. A única regra de correspondência é a IGUALDADE do texto normalizado com a pergunta da
/// entrada ou com um alias cadastrado de propósito: nada de percentual de termos, distância de edição, sinônimo implícito ou embedding, que acertam "quase" e erram em silêncio. A normalização só remove o que
/// não muda o que o cliente quis dizer (caixa, acento, pontuação, símbolos e espaços repetidos); números e letras ficam, então "exame 2" e "exame 3" nunca se confundem.
/// Na dúvida (nada igual, mais de uma entrada igual, entrada sem resposta direta) o resultado nunca é <see cref="FaqMatchKind.Direct"/>, e quem decide o que fazer é o chamador (ir ao modelo).
/// </summary>
public static class FaqMatcher
{
    /// <summary>Texto maior que isto não pode ser igual a uma pergunta (limitada a <see cref="AgentDefinitionContent.QuestionMaxLength"/>): nem se normaliza.</summary>
    private const int MaxInputLength = 4 * AgentDefinitionContent.QuestionMaxLength;

    /// <summary>
    /// Caixa baixa invariante, sem acentos (decomposição canônica e descarte das marcas combinantes), só letras e números, com qualquer outro caractere (pontuação, símbolo, emoji, espaço) virando UM espaço
    /// entre palavras. Devolve vazio para texto nulo, em branco, só de símbolos ou malformado (par substituto solto): vazio nunca corresponde a nada.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string decomposto;
        try
        {
            decomposto = text.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // Unicode malformado (par substituto solto): não há o que comparar.
            return string.Empty;
        }

        var saida = new StringBuilder(decomposto.Length);
        var separador = false;
        foreach (var c in decomposto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (separador && saida.Length > 0)
                {
                    saida.Append(' ');
                }

                separador = false;
                saida.Append(char.ToLowerInvariant(c));
            }
            else
            {
                separador = true;
            }
        }

        return saida.ToString();
    }

    /// <summary>
    /// Procura <paramref name="text"/> no <paramref name="faq"/>. Só uma entrada, e só se ela liga a resposta direta, dá <see cref="FaqMatchKind.Direct"/>. A ambiguidade conta TODAS as entradas, ligadas ou não:
    /// uma entrada sem resposta direta com a mesma pergunta de outra, ligada, também impede a resposta direta, porque o tenant escreveu duas respostas para a mesma pergunta.
    /// </summary>
    public static FaqMatch Match(IReadOnlyList<FaqEntry> faq, string? text)
    {
        ArgumentNullException.ThrowIfNull(faq);
        if (text is null || text.Length > MaxInputLength)
        {
            return FaqMatch.None;
        }

        var alvo = Normalize(text);
        if (alvo.Length == 0)
        {
            return FaqMatch.None;
        }

        FaqEntry? achada = null;
        foreach (var entrada in faq)
        {
            if (entrada is null || !Corresponde(entrada, alvo))
            {
                continue;
            }

            if (achada is not null)
            {
                return FaqMatch.Conflict;
            }

            achada = entrada;
        }

        if (achada is null)
        {
            return FaqMatch.None;
        }

        return new FaqMatch(achada.AutoReply ? FaqMatchKind.Direct : FaqMatchKind.NotAutoReply, achada);
    }

    private static bool Corresponde(FaqEntry entrada, string alvo)
    {
        if (string.Equals(Normalize(entrada.Question), alvo, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var alias in entrada.GetAliases())
        {
            if (string.Equals(Normalize(alias), alvo, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
