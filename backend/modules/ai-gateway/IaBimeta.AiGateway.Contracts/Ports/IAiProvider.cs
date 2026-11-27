namespace IaBimeta.AiGateway.Contracts.Ports;

/// <summary>
/// A porta de adaptador de um provedor de IA (BOUNDARIES §7): existe um adaptador por provedor, e só os hosts os registram. O contrato é independente
/// de provedor: o AiGateway e o Agents conhecem o provedor só pelo <see cref="ProviderId"/>.
/// </summary>
public interface IAiProvider
{
    /// <summary>O identificador do provedor (minúsculas, números e <c>._-</c>), o mesmo que o catálogo usa nos modelos dele. Constante.</summary>
    string ProviderId { get; }

    /// <summary>
    /// O provedor exige a credencial BYOK do tenant? Verdadeiro por padrão: os provedores comerciais não declaram nada e continuam falhando fechado sem credencial. Só um provedor que NÃO tem chave comercial
    /// (um servidor de IA local ou de rede privada, Etapa 04) declara falso: o AiGateway então não resolve credencial, e o adaptador recebe <see cref="AiCredential.None"/> (vazia) e nunca a usa.
    /// </summary>
    bool RequiresCredential => true;

    /// <summary>
    /// Faz UMA chamada ao provedor e devolve sempre um resultado: falha do provedor (rede, tempo esgotado, 4xx, 5xx, recusa) é um
    /// <see cref="AiCompletionResult"/> com <see cref="AiCompletionResult.Error"/>, mapeada para a lista normalizada. O adaptador respeita o
    /// <paramref name="cancellationToken"/> (o AiGateway o cancela no tempo limite e quando o chamador desiste), não tenta de novo por conta própria,
    /// não troca de provedor e não grava a credencial, o prompt nem a resposta em log.
    /// </summary>
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken);
}
