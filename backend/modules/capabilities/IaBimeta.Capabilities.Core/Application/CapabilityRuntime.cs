using System.Text.Json;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;

namespace IaBimeta.Capabilities.Application;

/// <summary>Pequenas conversões comuns ao teste de conexão e à execução. O segredo decifrado só existe em memória e só durante a chamada.</summary>
internal static class CapabilityRuntime
{
    /// <exception cref="CapabilitiesUnavailableException">Chave ausente ou texto alterado: nunca tenta a chamada sem credencial.</exception>
    public static ConnectorSecret ReadSecret(ISecretProtector protector, IntegrationConnection conexao)
    {
        if (conexao.AuthType == ConnectorAuthType.None)
        {
            return ConnectorSecret.Empty;
        }

        if (!conexao.HasSecret || conexao.SecretKeyId is null)
        {
            throw new CapabilitiesUnavailableException("secret-missing");
        }

        var texto = protector.Unprotect(
            new ProtectedSecret(conexao.SecretKeyId, conexao.SecretCiphertext!),
            new SecretBinding("connection-secret", conexao.TenantId, conexao.Id, $"{(int)conexao.AuthType}:{conexao.SecretVersion}"));
        var valores = JsonSerializer.Deserialize<Dictionary<string, string>>(texto) ?? throw new CapabilitiesUnavailableException("secret-invalid");
        return new ConnectorSecret(valores);
    }

    public static ConnectorConnection ToConnector(IntegrationConnection c, ConnectorSecret secret) => new(
        c.Id, c.TenantId.Value, c.SecretVersion, c.BaseUrl, c.AuthType, c.AuthSettings, c.DefaultHeaders, c.AllowedHeaders, c.TimeoutSeconds, c.MaxResponseBytes, c.HealthPath, secret);

    public static string OutcomeCode(ConnectorOutcome outcome) => outcome switch
    {
        ConnectorOutcome.Success => "ok",
        ConnectorOutcome.Rejected => "upstream-rejected",
        ConnectorOutcome.AuthFailed => "upstream-auth",
        ConnectorOutcome.Unavailable => "unavailable",
        ConnectorOutcome.Timeout => "timeout",
        ConnectorOutcome.NetworkError => "unavailable",
        ConnectorOutcome.Blocked => "blocked-destination",
        ConnectorOutcome.Redirected => "redirect-blocked",
        ConnectorOutcome.TooLarge => "response-too-large",
        ConnectorOutcome.InvalidResponse => "response-invalid",
        ConnectorOutcome.SecretUnavailable => "secret-unavailable",
        _ => "internal-error",
    };

    /// <summary>As frases fixas dos erros (nunca texto do sistema externo nem valores).</summary>
    public static string MessageFor(string code) => code switch
    {
        "validation-failed" => "Os parâmetros não respeitam o esquema da operação.",
        "not-found" => "Operação ou execução não encontrada.",
        "disabled" => "A operação ou a conexão está desabilitada.",
        "caller-not-allowed" => "Esta origem não pode usar a operação.",
        "rate-limited" => "Limite de execuções atingido. Tente de novo em instantes.",
        "idempotency-required" => "Uma operação de escrita exige uma chave de idempotência válida.",
        "idempotency-conflict" => "A chave de idempotência já foi usada com outros parâmetros.",
        "outcome-unknown" => "O resultado é ambíguo: confira no sistema de origem antes de repetir.",
        "confirmation-expired" => "A confirmação venceu. Peça a operação de novo.",
        "definition-changed" => "A operação mudou desde o pedido. Peça de novo.",
        "not-pending" => "A execução não está aguardando confirmação.",
        "timeout" => "O sistema externo não respondeu a tempo.",
        "unavailable" => "O sistema externo está indisponível.",
        "upstream-rejected" => "O sistema externo recusou a operação.",
        "upstream-auth" => "O sistema externo recusou as credenciais da conexão.",
        "redirect-blocked" => "O sistema externo respondeu com redirecionamento, que não é seguido.",
        "blocked-destination" => "O destino da conexão não é permitido.",
        "response-too-large" => "A resposta do sistema externo excede o limite.",
        "response-invalid" => "A resposta do sistema externo não tem o formato esperado.",
        "secret-unavailable" => "A credencial da conexão não está disponível.",
        "module-disabled" => "O motor de integração está desligado neste ambiente.",
        "connector-unavailable" => "Não há conector para esta conexão.",
        "write-not-testable" => "Operações de escrita não são executadas em teste.",
        "in-progress" => "Outra chamada com a mesma chave de idempotência ainda está em curso.",
        "cancelled" => "A execução foi cancelada.",
        _ => "Falha interna ao executar a operação.",
    };

    public static CapabilityError Error(string code, bool retryable = false, int? upstreamStatus = null, IReadOnlyList<CapabilityParameterProblem>? problems = null) =>
        new(code, MessageFor(code), retryable, upstreamStatus, problems);
}
