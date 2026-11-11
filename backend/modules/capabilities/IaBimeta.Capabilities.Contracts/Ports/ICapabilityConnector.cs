using System.Text.Json;

namespace IaBimeta.Capabilities.Contracts.Ports;

/// <summary>
/// Um TIPO de conector (a porta de BOUNDARIES.md §5). O conector genérico REST/JSON (<c>rest-json</c>) vem no módulo; um adaptador futuro (por exemplo, de um ERP com protocolo próprio) implementa esta porta e se
/// registra com outro <see cref="Kind"/>. O conector SÓ transporta: validação, autorização, idempotência, limites e mapeamento da resposta são do motor. Nunca lança por falha do sistema externo: devolve um
/// <see cref="ConnectorResult"/>.
/// </summary>
public interface ICapabilityConnector
{
    string Kind { get; }

    Task<ConnectorResult> InvokeAsync(ConnectorCall call, CancellationToken cancellationToken = default);

    /// <summary>Sonda a conexão (alcance e autenticação) sem executar nenhuma operação do catálogo.</summary>
    Task<ConnectorResult> ProbeAsync(ConnectorConnection connection, CancellationToken cancellationToken = default);
}

public enum ConnectorAuthType
{
    None = 0,
    ApiKey = 1,
    Basic = 2,
    OAuth2ClientCredentials = 3,
}

/// <summary>O segredo da conexão já decifrado, só em memória e só durante a chamada. <see cref="ToString"/> nunca imprime o conteúdo.</summary>
public sealed class ConnectorSecret
{
    public ConnectorSecret(IReadOnlyDictionary<string, string> values)
    {
        Values = values;
    }

    public IReadOnlyDictionary<string, string> Values { get; }

    public static ConnectorSecret Empty { get; } = new(new Dictionary<string, string>());

    public override string ToString() => "ConnectorSecret { valores ocultos }";
}

/// <summary>A configuração de uma conexão como o conector a vê (sem o segredo, que vai à parte).</summary>
public sealed record ConnectorConnection(
    Guid ConnectionId,
    Guid TenantId,
    int SecretVersion,
    string BaseUrl,
    ConnectorAuthType AuthType,
    IReadOnlyDictionary<string, string> AuthSettings,
    IReadOnlyDictionary<string, string> DefaultHeaders,
    IReadOnlyList<string> AllowedHeaders,
    int TimeoutSeconds,
    int MaxResponseBytes,
    string HealthPath,
    ConnectorSecret Secret);

/// <param name="Values">Os valores JÁ validados e normalizados, por nome do parâmetro (<c>string</c>, <c>long</c>, <c>decimal</c> ou <c>bool</c>).</param>
/// <param name="IdempotencyKey">A chave a enviar ao sistema externo quando a operação declara um cabeçalho de idempotência.</param>
/// <param name="AllowRetries">Verdadeiro só para leitura ou escrita <c>Proven</c>: o conector não repete nada sem isto.</param>
/// <param name="IsWrite">Escrita: o conector marca <see cref="ConnectorResult.MayHaveExecuted"/> se a chamada pode ter chegado ao destino sem resposta definitiva.</param>
public sealed record ConnectorCall(
    ConnectorConnection Connection,
    CapabilityOperationSpec Operation,
    IReadOnlyDictionary<string, object> Values,
    string? IdempotencyKey,
    bool AllowRetries,
    bool IsWrite,
    int MaxAttempts);

public enum ConnectorOutcome
{
    Success = 1,

    /// <summary>O sistema externo recusou (4xx, exceto autenticação).</summary>
    Rejected = 2,

    /// <summary>Autenticação ou autorização recusadas (401 ou 403, ou falha ao obter o token).</summary>
    AuthFailed = 3,

    /// <summary>Resposta definitiva de falha do servidor (5xx, 429) depois das tentativas permitidas.</summary>
    Unavailable = 4,

    Timeout = 5,

    NetworkError = 6,

    /// <summary>O destino não é permitido (SSRF).</summary>
    Blocked = 7,

    /// <summary>O destino respondeu com redirecionamento: nunca seguido.</summary>
    Redirected = 8,

    TooLarge = 9,

    InvalidResponse = 10,

    /// <summary>O segredo da conexão não está disponível (chave ausente, formato inválido).</summary>
    SecretUnavailable = 11,
}

/// <param name="Body">O JSON da resposta (já limitado e validado como JSON), quando houve corpo.</param>
/// <param name="MayHaveExecuted">A requisição pode ter sido processada pelo destino sem que haja resposta definitiva (timeout, queda depois do envio, 5xx). O motor trata como resultado ambíguo numa escrita sem idempotência comprovada.</param>
public sealed record ConnectorResult(
    ConnectorOutcome Outcome,
    int? UpstreamStatus,
    JsonElement? Body,
    int Attempts,
    long ElapsedMs,
    bool MayHaveExecuted)
{
    /// <summary>A espera sugerida pelo destino (Retry-After), usada só para escolher o intervalo entre tentativas.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
