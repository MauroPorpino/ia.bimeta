namespace IaBimeta.Tenancy.Contracts;

/// <summary>Situação do tenant. Só um tenant ativo pode receber um contexto de operação.</summary>
public enum TenantStatus
{
    Active = 1,
    Suspended = 2,
}
