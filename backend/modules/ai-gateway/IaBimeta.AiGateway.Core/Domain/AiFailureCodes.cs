namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// Os códigos estáveis que o AiGateway põe em <c>AiFailure.Code</c> quando a falha é dele (não do provedor). Servem ao diagnóstico e aos testes; nenhum
/// contém segredo, prompt nem resposta.
/// </summary>
public static class AiFailureCodes
{
    // Seleção (NotConfigured)
    public const string NotConfigured = "not-configured";
    public const string ConfigurationTenantMismatch = "configuration-tenant-mismatch";
    public const string LimitsInvalid = "limits-invalid";
    public const string CredentialSourceNotSupported = "credential-source-not-supported";
    public const string ModelNotSupported = "model-not-supported";
    public const string ModelDisabled = "model-disabled";
    public const string ProviderNotRegistered = "provider-not-registered";

    // Credencial (InvalidCredentials)
    public const string CredentialMissing = "credential-missing";

    // Limites do tenant (TenantLimitExceeded)
    public const string InputMessageLimit = "input-message-limit";
    public const string ConcurrencyLimit = "concurrency-limit";
    public const string PeriodCallLimit = "period-call-limit";
    public const string PeriodTokenLimit = "period-token-limit";
    public const string LeaseDenied = "lease-denied";

    // Falhas ao chamar o adaptador
    public const string Timeout = "timeout";
    public const string ProviderException = "provider-exception";
    public const string ProviderResultInvalid = "provider-result-invalid";
    public const string ProviderErrorKindUnknown = "provider-error-kind-unknown";
}
