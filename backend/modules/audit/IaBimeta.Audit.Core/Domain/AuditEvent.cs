using System.Text.RegularExpressions;
using IaBimeta.Audit.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Audit.Domain;

/// <summary>
/// Evento da trilha de auditoria: tenant, ator, operação, recurso, resultado e data. É imutável e a trilha só recebe
/// inclusões. Os campos aceitam apenas formatos curtos e sem espaços, para que senha, token ou texto livre não
/// tenham por onde entrar.
/// </summary>
public sealed class AuditEvent : ITenantOwned
{
    public const int OperationMaxLength = 100;
    public const int ResourceTypeMaxLength = 60;
    public const int ResourceIdMaxLength = 100;
    public const int ActorOriginMaxLength = 40;

    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_.-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ResourceIdPattern = new("^[A-Za-z0-9_.:-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private AuditEvent(
        Guid id,
        TenantId tenantId,
        DateTimeOffset occurredAt,
        Guid? actorUserId,
        string actorOrigin,
        string operation,
        string resourceType,
        string? resourceId,
        AuditOutcome outcome)
    {
        Id = id;
        TenantId = tenantId;
        OccurredAt = occurredAt;
        ActorUserId = actorUserId;
        ActorOrigin = actorOrigin;
        Operation = operation;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Outcome = outcome;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>O usuário que agiu. Nulo em contexto de sistema ou quando a autenticação falhou.</summary>
    public Guid? ActorUserId { get; }

    /// <summary>A fonte do contexto (por exemplo, "session", "development-header" ou "authentication").</summary>
    public string ActorOrigin { get; }

    public string Operation { get; }

    public string ResourceType { get; }

    public string? ResourceId { get; }

    public AuditOutcome Outcome { get; }

    public static AuditEvent Create(TenantId tenantId, Guid? actorUserId, string actorOrigin, AuditEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(actorOrigin) || actorOrigin.Length > ActorOriginMaxLength || !NamePattern.IsMatch(actorOrigin))
        {
            throw new AuditValidationException("A origem do ator da auditoria está fora do formato permitido.");
        }

        if (!IsValidName(entry.Operation, OperationMaxLength))
        {
            throw new AuditValidationException($"A operação de auditoria deve ter até {OperationMaxLength} caracteres, em minúsculas, sem espaços.");
        }

        if (!IsValidName(entry.ResourceType, ResourceTypeMaxLength))
        {
            throw new AuditValidationException($"O tipo de recurso da auditoria deve ter até {ResourceTypeMaxLength} caracteres, em minúsculas, sem espaços.");
        }

        if (entry.ResourceId is not null
            && (entry.ResourceId.Length is 0 or > ResourceIdMaxLength || !ResourceIdPattern.IsMatch(entry.ResourceId)))
        {
            throw new AuditValidationException($"O identificador do recurso da auditoria deve ter até {ResourceIdMaxLength} caracteres, só letras, números e '_.:-'.");
        }

        if (!Enum.IsDefined(entry.Outcome))
        {
            throw new AuditValidationException("O resultado da auditoria é inválido.");
        }

        if (entry.IdempotencyKey == Guid.Empty)
        {
            throw new AuditValidationException("A chave de idempotência da auditoria não pode ser vazia.");
        }

        // A chave de idempotência é o próprio identificador do evento: a chave primária (tenant, id) é quem impede a duplicidade.
        return new AuditEvent(
            entry.IdempotencyKey ?? Guid.CreateVersion7(), tenantId, now, actorUserId, actorOrigin, entry.Operation, entry.ResourceType, entry.ResourceId, entry.Outcome);
    }

    private static bool IsValidName(string? valor, int tamanhoMaximo) =>
        !string.IsNullOrEmpty(valor) && valor.Length <= tamanhoMaximo && NamePattern.IsMatch(valor);
}
