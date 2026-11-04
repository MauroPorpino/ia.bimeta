using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// A política de erro do AiGateway (ADR 0017, seções C.4 e G.4): transforma o erro normalizado do adaptador na falha que o consumidor recebe. Só a falha
/// <see cref="AiErrorKind.Transient"/> é retentável, e quem tenta de novo, quantas vezes e com que espera é o consumidor (o Agents); todas as outras são
/// PERMANENTES: credencial inválida, cota ou cobrança (mesmo com espera sugerida), pedido inválido, contexto longo demais, recusa, limite do tenant e
/// configuração ausente. A política nunca troca de provedor (não há fallback).
/// </summary>
public static class AiErrorPolicy
{
    private const int MaxCodeLength = 100;

    /// <summary>Se a categoria é retentável.</summary>
    public static bool IsRetryable(AiErrorKind kind) => kind == AiErrorKind.Transient;

    /// <param name="error">O erro que o adaptador (ou o AiGateway) produziu.</param>
    /// <param name="maxRetryAfter">O teto da espera sugerida: uma espera maior que isso é reduzida ao teto.</param>
    public static AiFailure Apply(AiError error, TimeSpan maxRetryAfter)
    {
        ArgumentNullException.ThrowIfNull(error);

        // Uma categoria que o AiGateway não conhece falha para o lado seguro: pedido inválido e permanente, nunca "tente de novo".
        if (!Enum.IsDefined(error.Kind))
        {
            return new AiFailure(AiErrorKind.InvalidRequest, false, null, AiFailureCodes.ProviderErrorKindUnknown);
        }

        var retentavel = IsRetryable(error.Kind);
        TimeSpan? espera = null;
        if (retentavel && error.RetryAfter is { } sugerida && sugerida >= TimeSpan.Zero)
        {
            espera = sugerida > maxRetryAfter ? maxRetryAfter : sugerida;
        }

        return new AiFailure(error.Kind, retentavel, espera, SanitizeCode(error.Code));
    }

    /// <summary>
    /// O código do provedor serve só ao diagnóstico: fica com até 100 caracteres e sem caractere de controle. Um código que não obedece é descartado, para
    /// que um valor inesperado do adaptador não vire texto livre em log.
    /// </summary>
    public static string? SanitizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength || code.Any(char.IsControl))
        {
            return null;
        }

        return code;
    }
}
