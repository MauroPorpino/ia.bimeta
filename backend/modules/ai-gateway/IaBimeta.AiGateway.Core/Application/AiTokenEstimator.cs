using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.AiGateway.Application;

/// <summary>
/// A estimativa de tokens da ENTRADA de uma chamada (ADR 0017, ajuste A do ledger). É uma HEURÍSTICA por caracteres, sem tokenizador e sem pacote: serve ao controle de
/// orçamento (a reserva), nunca como garantia de consumo. Sempre arredonda para cima e aplica a margem de segurança das opções, e o consumo real que o provedor informa é o que vale
/// na confirmação. A diferença entre a estimativa e o real é medida (<c>estimated_input_tokens</c> ao lado do consumo) para calibrar o divisor.
/// </summary>
public static class AiTokenEstimator
{
    /// <summary>Os caracteres da entrada: a instrução de sistema mais o texto de todas as mensagens.</summary>
    public static long CountInputCharacters(AiGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        long total = request.SystemInstruction?.Length ?? 0;
        foreach (var mensagem in request.Messages)
        {
            total += mensagem.Text?.Length ?? 0;
        }

        return total;
    }

    /// <summary>
    /// Os tokens estimados da entrada: <c>ceil(caracteres ÷ divisor × margem) + acréscimo por mensagem × mensagens + acréscimo da requisição</c>. Nunca é menor que o acréscimo da
    /// requisição, e cresce de forma monotônica com o texto.
    /// </summary>
    public static long EstimateInputTokens(AiGenerationRequest request, AiGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var caracteres = CountInputCharacters(request);
        var porTexto = (long)Math.Ceiling(caracteres / options.CharactersPerToken * options.InputSafetyMargin);
        return porTexto + (long)options.PerMessageOverheadTokens * request.Messages.Count + options.RequestOverheadTokens;
    }
}
