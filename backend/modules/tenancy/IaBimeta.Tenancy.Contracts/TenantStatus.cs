namespace IaBimeta.Tenancy.Contracts;

/// <summary>
/// Situação do tenant. Só um tenant ativo pode receber um contexto de operação (login, sessão, Embedded e processamento).
/// <see cref="Provisioning"/> (Etapa 02): a empresa foi cadastrada pela plataforma, mas o provisionamento necessário ainda não terminou; não opera.
/// <see cref="Suspended"/>: suspensão efetiva decidida pela plataforma (motivo e histórico em <c>tenant_status_changes</c>), distinta da desabilitação de novos cadastros.
/// </summary>
public enum TenantStatus
{
    Active = 1,
    Suspended = 2,
    Provisioning = 3,
}
