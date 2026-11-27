namespace IaBimeta.AiGateway.Contracts.Ports;

/// <summary>O que a verificação de conectividade concluiu. Só <see cref="Verified"/> é um resultado positivo; as outras NUNCA são apresentadas como disponibilidade.</summary>
public enum AiConnectivityState
{
    /// <summary>O servidor respondeu, a autenticação foi aceita e o modelo existe (e, quando o provedor declara capacidades, serve à conversa).</summary>
    Verified = 1,

    /// <summary>O provedor recusou a credencial.</summary>
    AuthenticationFailed = 2,

    /// <summary>O servidor não foi alcançado (rede, tempo esgotado).</summary>
    Unreachable = 3,

    /// <summary>O servidor respondeu, mas o modelo não existe ou não está instalado, ou não declara a capacidade de conversa.</summary>
    ModelUnavailable = 4,

    /// <summary>O provedor não oferece uma verificação que não gere texto: nada foi verificado, e a geração NÃO é usada no lugar dela.</summary>
    NotSupported = 5,

    /// <summary>A resposta não permite concluir (erro do servidor, limite de taxa, resposta inesperada, falta de permissão da chave).</summary>
    Inconclusive = 6,
}

/// <summary>
/// O resultado do teste de conectividade de UM modelo de UM provedor. Cada campo booleano é tri-estado: nulo = "não foi possível verificar", nunca "falso presumido". Nunca traz o corpo do servidor, o endereço nem a credencial.
/// </summary>
/// <param name="Code">Um código estável e curto (por exemplo <c>unauthorized</c>, <c>model-not-found</c>), nunca texto do servidor.</param>
public sealed record AiConnectivityResult(
    AiConnectivityState State,
    bool? Reachable,
    bool? Authenticated,
    bool? ModelAvailable,
    bool? ConversationCapable,
    string? Code,
    TimeSpan Latency)
{
    public static AiConnectivityResult NotSupported(TimeSpan latency = default) => new(AiConnectivityState.NotSupported, null, null, null, null, "not-supported", latency);
}

/// <summary>O pedido de verificação: o modelo do catálogo e a credencial do tenant (vazia para o provedor que não exige).</summary>
public sealed record AiConnectivityRequest(string ModelId, AiCredential Credential, TimeSpan Timeout)
{
    public override string ToString() => $"AiConnectivityRequest {{ ModelId = {ModelId} }}";
}

/// <summary>
/// A porta OPCIONAL de diagnóstico de um adaptador (Etapa 05): verifica acessibilidade, autenticação, existência do modelo e capacidade declarada SEM gerar texto e sem consumir tokens, usando só o que a documentação
/// oficial do provedor oferece. Um adaptador que não tem como verificar simplesmente não a implementa (ou devolve <see cref="AiConnectivityResult.NotSupported"/>). O endereço vem da configuração da infraestrutura;
/// o adaptador não segue redirecionamentos, não repete a chamada e nunca devolve texto do servidor.
/// </summary>
public interface IAiProviderDiagnostics
{
    Task<AiConnectivityResult> CheckConnectivityAsync(AiConnectivityRequest request, CancellationToken cancellationToken);
}
