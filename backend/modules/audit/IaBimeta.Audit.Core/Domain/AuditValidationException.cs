namespace IaBimeta.Audit.Domain;

/// <summary>A entrada de auditoria viola o formato permitido (que existe justamente para não guardar segredo).</summary>
public sealed class AuditValidationException : Exception
{
    public AuditValidationException(string message)
        : base(message)
    {
    }
}
