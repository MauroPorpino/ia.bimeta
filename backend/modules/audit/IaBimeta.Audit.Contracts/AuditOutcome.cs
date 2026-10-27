namespace IaBimeta.Audit.Contracts;

/// <summary>Resultado da operação registrada na trilha de auditoria.</summary>
public enum AuditOutcome
{
    Success = 1,

    /// <summary>A operação foi negada: credencial inválida ou falta de permissão.</summary>
    Denied = 2,

    Failed = 3,
}
