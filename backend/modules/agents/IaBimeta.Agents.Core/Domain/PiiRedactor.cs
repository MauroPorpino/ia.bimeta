using System.Text.RegularExpressions;

namespace IaBimeta.Agents.Domain;

/// <summary>
/// A minimização de dados antes de o texto de uma conversa ir ao modelo local (ADR 0034): troca por marcadores o que identifica uma pessoa de forma inequívoca por PADRÃO (e-mail, CPF, CNPJ, telefone, endereço
/// de internet, sequências longas de dígitos como cartão ou protocolo). Não é uma garantia de anonimização (um nome próprio ou um endereço por extenso não têm padrão): por isso o texto só vai ao modelo
/// quando o administrador pede, o modelo roda na infraestrutura do próprio tenant, a saída é conferida de novo, e a sugestão é revisada por uma pessoa antes de valer. Puro, sem I/O.
/// </summary>
public static class PiiRedactor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly (Regex Pattern, string Marker)[] Rules =
    [
        (new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout), "[email]"),
        (new Regex(@"\b(?:https?://|www\.)\S+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout), "[link]"),
        (new Regex(@"\b\d{2}\.?\d{3}\.?\d{3}/?\d{4}-?\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout), "[cnpj]"),
        (new Regex(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout), "[cpf]"),
        (new Regex(@"(?<!\d)(?:\+?55\s?)?\(?\d{2}\)?[\s\-]?9?\d{4}[\s\-]?\d{4}(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout), "[telefone]"),
        (new Regex(@"\b\d(?:[\s\-]?\d){6,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout), "[numero]"),
    ];

    /// <summary>O texto sem os padrões de dado pessoal. Texto nulo ou em branco vira vazio. Se uma expressão estourar o tempo, o texto INTEIRO é descartado (falha fechada: nunca devolve o original).</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            var saida = text;
            foreach (var (padrao, marcador) in Rules)
            {
                saida = padrao.Replace(saida, marcador);
            }

            return saida;
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    /// <summary>Se o texto ainda tem algum padrão de dado pessoal (usado para recusar uma SAÍDA do modelo que copiou dado do cliente).</summary>
    public static bool ContainsPii(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            return Rules.Any(r => r.Pattern.IsMatch(text));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
