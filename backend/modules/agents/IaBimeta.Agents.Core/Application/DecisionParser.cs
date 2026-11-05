using System.Text.Json;
using IaBimeta.Agents.Domain;

namespace IaBimeta.Agents.Application;

/// <summary>
/// Valida a saída do modelo contra a LISTA FECHADA de ações (ADR 0017, seção C.5): o modelo sugere, o Agents valida e falha fechado. Saída que não é um objeto JSON, com campo faltando, campo a mais,
/// ação fora da lista, texto vazio ou longo demais, ou motivo fora da lista NÃO executa nada e conta como falha da tentativa. A saída bruta nunca é repassada: só a <see cref="AgentDecision"/> validada.
/// </summary>
public static class DecisionParser
{
    /// <summary>O teto do texto de uma resposta (o mesmo que o Conversations aceita; ele recusaria acima disso).</summary>
    public const int ReplyMaxLength = 4096;

    private static readonly string[] Fields = { "action", "reply_text", "handoff_reason" };

    /// <summary>Tenta ler a decisão. Em caso de falha, <paramref name="invalidCode"/> é um código curto e estável (nunca o conteúdo da saída).</summary>
    public static bool TryParse(string? output, out AgentDecision? decision, out string? invalidCode)
    {
        decision = null;
        invalidCode = null;

        if (string.IsNullOrWhiteSpace(output))
        {
            invalidCode = "empty-output";
            return false;
        }

        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(output, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        }
        catch (JsonException)
        {
            invalidCode = "invalid-json";
            return false;
        }

        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                invalidCode = "not-an-object";
                return false;
            }

            var propriedades = raiz.EnumerateObject().ToList();
            if (propriedades.Count != Fields.Length
                || !Fields.All(campo => propriedades.Count(p => p.Name == campo) == 1)
                || propriedades.Any(p => p.Value.ValueKind != JsonValueKind.String))
            {
                invalidCode = "invalid-shape";
                return false;
            }

            string? acao;
            string texto;
            string motivo;
            try
            {
                acao = raiz.GetProperty("action").GetString();
                texto = raiz.GetProperty("reply_text").GetString() ?? string.Empty;
                motivo = raiz.GetProperty("handoff_reason").GetString() ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                // Um escape de substituto UTF-16 sem par (\ud83d) que o modelo emitiu: o texto não é válido, e a saída falha fechada em vez de vazar exceção.
                invalidCode = "invalid-json";
                return false;
            }

            switch (acao)
            {
                case "reply":
                    var resposta = Sanitize(texto).Trim();
                    if (resposta.Length == 0)
                    {
                        invalidCode = "empty-reply";
                        return false;
                    }

                    if (resposta.Length > ReplyMaxLength)
                    {
                        invalidCode = "reply-too-long";
                        return false;
                    }

                    decision = new AgentDecision(AgentAction.Reply, resposta, null);
                    return true;

                case "handoff":
                    if (!HandoffReasons.Selectable.Contains(motivo, StringComparer.Ordinal))
                    {
                        invalidCode = "invalid-handoff-reason";
                        return false;
                    }

                    decision = new AgentDecision(AgentAction.Handoff, null, motivo);
                    return true;

                default:
                    invalidCode = "unknown-action";
                    return false;
            }
        }
    }

    /// <summary>Troca o substituto UTF-16 sem par (que o modelo pode emitir) por U+FFFD, para o texto ser sempre válido para o Conversations e o provedor do canal.</summary>
    internal static string Sanitize(string texto)
    {
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            var sozinho = char.IsHighSurrogate(c) ? i + 1 >= texto.Length || !char.IsLowSurrogate(texto[i + 1]) : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(texto[i - 1]));
            if (!sozinho)
            {
                continue;
            }

            var corrigido = texto.ToCharArray();
            for (var j = i; j < corrigido.Length; j++)
            {
                var d = corrigido[j];
                if (char.IsHighSurrogate(d) && j + 1 < corrigido.Length && char.IsLowSurrogate(corrigido[j + 1]))
                {
                    j++;
                }
                else if (char.IsSurrogate(d))
                {
                    corrigido[j] = '�';
                }
            }

            return new string(corrigido);
        }

        return texto;
    }
}
