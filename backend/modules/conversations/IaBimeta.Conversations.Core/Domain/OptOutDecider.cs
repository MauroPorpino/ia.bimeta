using System.Globalization;
using System.Text;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// A política da saída do cliente (ADR 0041, seção 5; D2): as palavras e as finalidades que a decisão considera. Valor imutável com UM único <see cref="Default"/> fixo (PARAR, SAIR e CANCELAR; todas as finalidades).
/// Não existe chave de configuração, tabela nem leitura de configuração na E2: quando a configuração por empresa vier (ADR próprio), basta uma fonte que produza a política, e o decisor, as regras e os testes não mudam.
/// </summary>
public sealed record OptOutPolicy
{
    private OptOutPolicy()
    {
    }

    /// <summary>As palavras que, SOZINHAS na mensagem, são o comando (já normalizadas: minúsculas, sem acento nem pontuação).</summary>
    public IReadOnlySet<string> Words { get; private init; } = new HashSet<string>();

    /// <summary>As palavras CONTEXTUAIS: respondem naturalmente a uma pergunta ("Deseja cancelar seu pedido?"), então só confirmam fora de uma conversa em curso. PARAR não está aqui: é inequívoca.</summary>
    public IReadOnlySet<string> ContextualWords { get; private init; } = new HashSet<string>();

    /// <summary>As expressões de intenção em linguagem natural (texto já normalizado), com ou sem a borda do fim da palavra. É código revisado, nunca saída de modelo.</summary>
    public IReadOnlyList<(string Phrase, bool Prefix)> Intents { get; private init; } = [];

    /// <summary>As finalidades que a revogação por palavra-chave cobre: todas.</summary>
    public IReadOnlyList<ConsentPurpose> Purposes { get; private init; } = [];

    public static OptOutPolicy Default { get; } = new()
    {
        Words = new HashSet<string>(StringComparer.Ordinal) { "parar", "sair", "cancelar" },
        ContextualWords = new HashSet<string>(StringComparer.Ordinal) { "sair", "cancelar" },
        Intents =
        [
            ("pare de", false),
            ("parar de", false),
            ("nao quero receber", false),
            ("nao quero mais receber", false),
            ("nao me envie", false),
            ("nao me ligue", false),
            ("nao me mande", false),
            ("descadastr", true),
            ("cancelar inscricao", false),
            ("cancelar minha inscricao", false),
            ("sair da lista", false),
            ("remover meu numero", false),
        ],
        Purposes = [ConsentPurpose.Utility, ConsentPurpose.Marketing],
    };
}

/// <summary>O que o Conversations já sabe, de forma EXPLÍCITA, sobre a última saída da sessão (ADR 0041, seção 5). Nenhum texto de IA é interpretado e nenhum marcador vindo de saída de modelo entra aqui.</summary>
/// <param name="HasOutbound">A sessão já teve alguma saída.</param>
/// <param name="LastAuthor">O autor da última saída (<c>Ai</c>, <c>Human</c> ou <c>Bot</c>).</param>
/// <param name="LastIsTemplate">A última saída foi enviada por modelo de mensagem (<c>ChannelData</c> <c>tpl.name</c>, ADR 0027).</param>
/// <param name="LastExpectsFreeText">A última saída é de fluxo e termina numa pergunta de texto livre (marcador explícito do nó).</param>
/// <param name="InboundSinceLastOutbound">Já houve outra entrada do cliente depois da última saída.</param>
public sealed record OptOutContext(bool HasOutbound, MessageAuthor? LastAuthor, bool LastIsTemplate, bool LastExpectsFreeText, bool InboundSinceLastOutbound)
{
    public static OptOutContext NoOutbound { get; } = new(false, null, false, false, false);
}

public enum OptOutDecisionKind
{
    /// <summary>Não é comando e não sugere saída: processamento normal, sem rastro de saída.</summary>
    NotCommand = 1,

    /// <summary>Comando confirmado: revoga todas as finalidades e suprime a resposta automática.</summary>
    Confirmed = 2,

    /// <summary>Possível saída: NÃO revoga, o processamento segue normal e uma pessoa com <c>ConsentManage</c> decide.</summary>
    PossibleOptOut = 3,
}

/// <param name="Keyword">A palavra normalizada em maiúsculas (PARAR, SAIR, CANCELAR) quando a mensagem inteira é uma delas; nulo na intenção em linguagem natural. Nunca o texto livre.</param>
/// <param name="Reason">O motivo da possível saída; só com <see cref="OptOutDecisionKind.PossibleOptOut"/>.</param>
public readonly record struct OptOutDecision(OptOutDecisionKind Kind, string? Keyword = null, OptOutReviewReason? Reason = null)
{
    public static OptOutDecision NotCommand => new(OptOutDecisionKind.NotCommand);
}

/// <summary>
/// A decisão de saída do cliente (ADR 0041, seção 5; D3): função PURA e DETERMINÍSTICA (sem relógio, sem I/O, sem estado, sem LLM, sem referência a Agents, AiGateway ou Workflows). Recebe o tipo e o texto da mensagem, o
/// contexto explícito da última saída e a política; a PRIMEIRA regra que se aplica encerra a decisão:
/// <list type="number">
/// <item>Mensagem que não é texto (mídia, localização, reação...) → não é comando.</item>
/// <item>Normaliza (minúsculas, sem acento, sem pontuação, espaços colapsados). Se a mensagem INTEIRA não é exatamente uma palavra da política → não é comando; se contém uma expressão de intenção da lista fixa → possível saída (<c>natural-language-intent</c>); senão nada.</item>
/// <item>Fluxo aguardando texto livre (última saída de fluxo com o marcador e nenhuma outra entrada desde ela) → ambígua <c>flow-free-text</c> para qualquer das três palavras.</item>
/// <item>Palavra contextual (SAIR, CANCELAR) com a última saída CONVERSACIONAL (IA, atendente ou fluxo, e não modelo de mensagem) → ambígua <c>conversational-context</c>.</item>
/// <item>Todo o resto é comando confirmado: PARAR fora da regra 3; SAIR/CANCELAR em resposta a modelo de mensagem ou sem nenhuma saída.</item>
/// </list>
/// </summary>
public static class OptOutDecider
{
    public static OptOutDecision Decide(MessageKind kind, string? text, OptOutContext context, OptOutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        // 1. Só texto.
        if (kind != MessageKind.Text || string.IsNullOrWhiteSpace(text))
        {
            return OptOutDecision.NotCommand;
        }

        // 2. A mensagem inteira, exatamente uma palavra da política.
        var normalizado = Normalize(text);
        if (!policy.Words.Contains(normalizado))
        {
            return ContainsIntent(normalizado, policy)
                ? new OptOutDecision(OptOutDecisionKind.PossibleOptOut, null, OptOutReviewReason.NaturalLanguageIntent)
                : OptOutDecision.NotCommand;
        }

        var palavra = normalizado.ToUpperInvariant();

        // 3. O fluxo pediu um texto livre e esta é a primeira entrada desde então: a palavra pode ser a resposta.
        if (context is { HasOutbound: true, LastAuthor: MessageAuthor.Bot, LastExpectsFreeText: true, InboundSinceLastOutbound: false })
        {
            return new OptOutDecision(OptOutDecisionKind.PossibleOptOut, palavra, OptOutReviewReason.FlowFreeText);
        }

        // 4. Dentro de uma conversa em curso, SAIR e CANCELAR respondem naturalmente à última saída (independe de terminar em "?").
        if (policy.ContextualWords.Contains(normalizado) && context.HasOutbound && !context.LastIsTemplate)
        {
            return new OptOutDecision(OptOutDecisionKind.PossibleOptOut, palavra, OptOutReviewReason.ConversationalContext);
        }

        // 5. Confirmado.
        return new OptOutDecision(OptOutDecisionKind.Confirmed, palavra);
    }

    /// <summary>
    /// Minúsculas, sem acentos (decomposição e remoção das marcas), sem pontuação nem caracteres invisíveis, e espaços colapsados e aparados. Símbolos e emojis ficam: "PARAR 🛑" NÃO é a palavra inteira (nunca se
    /// adivinha). Cultura invariante: o resultado não depende da máquina.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposto = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposto.Length);
        var ultimoEspaco = true;
        foreach (var c in decomposto)
        {
            var categoria = CharUnicodeInfo.GetUnicodeCategory(c);
            if (categoria is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format)
            {
                continue;
            }

            if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
            {
                // Pontuação também separa palavras ("parar,agora" não vira "pararagora").
                if (!ultimoEspaco)
                {
                    sb.Append(' ');
                    ultimoEspaco = true;
                }

                continue;
            }

            sb.Append(char.ToLowerInvariant(c));
            ultimoEspaco = false;
        }

        return sb.ToString().Trim();
    }

    private static bool ContainsIntent(string normalizado, OptOutPolicy policy)
    {
        var texto = " " + normalizado + " ";
        foreach (var (frase, prefixo) in policy.Intents)
        {
            // Borda de palavra no início; no fim só quando a expressão é uma palavra completa ("descadastr" casa "descadastrar", "descadastre").
            if (texto.Contains(prefixo ? " " + frase : " " + frase + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
