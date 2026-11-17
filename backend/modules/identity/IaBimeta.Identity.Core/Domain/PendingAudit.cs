using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Uma entrada de auditoria do Embedded que ainda não chegou à trilha (E1-R, quarta rodada; mesmo padrão do <c>contact_center.audit_outbox</c>, ADR 0010 decisão C). A troca do ticket de abertura e o resgate do ticket de
/// contexto guardam a entrada na MESMA transação que consome o ticket e grava a sessão (ou o cliente em foco): se a transação é desfeita, a entrada some junto e a trilha nunca diz "sucesso" para o que não aconteceu; se é
/// confirmada, a entrada é entregue à trilha depois pelo Worker, então uma falha transitória da trilha nunca mais acontece DEPOIS do efeito, no meio da resposta (o que fazia o ERP receber 503 com o ticket já consumido).
/// A entrada guarda QUEM agiu, por qual origem e QUANDO, capturados na hora da operação: a entrega, que vem de outro processo e depois, grava com esses valores. A chave é <c>(tenant_id, id)</c>, e o <c>id</c> é a chave de
/// idempotência da entrega (a repetição não duplica o evento na trilha).
/// </summary>
public sealed class PendingAudit : ITenantOwned
{
    public const int OperationMaxLength = 100;
    public const int ResourceTypeMaxLength = 60;
    public const int ResourceIdMaxLength = 100;
    public const int ActorOriginMaxLength = 40;

    /// <summary>A origem da entrada guardada na troca do ticket de abertura (anterior ao contexto): a mesma que a trilha usa para eventos de autenticação.</summary>
    public const string AuthenticationOrigin = "authentication";

    // Usado pela fábrica e pelo EF Core.
    private PendingAudit()
    {
        TenantId = null!;
        Operation = null!;
        ResourceType = null!;
        ActorOrigin = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string Operation { get; private set; }

    public string ResourceType { get; private set; }

    public string? ResourceId { get; private set; }

    public AuditOutcome Outcome { get; private set; }

    /// <summary>O usuário que agiu quando a operação aconteceu; nulo em contexto de sistema.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>A origem do contexto quando a operação aconteceu (por exemplo, <c>authentication</c> ou <c>embedded-session</c>).</summary>
    public string ActorOrigin { get; private set; }

    /// <summary>Quando a operação aconteceu (não o instante da entrega).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public static PendingAudit Create(TenantId tenantId, AuditEntry entry, Guid? actorUserId, string actorOrigin, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(entry);

        // A trilha valida o formato quando recebe a entrada; aqui só se garante que ela cabe no que se guarda até lá.
        if (string.IsNullOrWhiteSpace(entry.Operation) || entry.Operation.Length > OperationMaxLength
            || string.IsNullOrWhiteSpace(entry.ResourceType) || entry.ResourceType.Length > ResourceTypeMaxLength
            || (entry.ResourceId is not null && (entry.ResourceId.Length == 0 || entry.ResourceId.Length > ResourceIdMaxLength))
            || string.IsNullOrWhiteSpace(actorOrigin) || actorOrigin.Length > ActorOriginMaxLength)
        {
            throw new IdentityValidationException("A entrada de auditoria não cabe no que se guarda até a entrega.");
        }

        return new PendingAudit
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Operation = entry.Operation,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            Outcome = entry.Outcome,
            ActorUserId = actorUserId,
            ActorOrigin = actorOrigin,
            CreatedAt = now,
        };
    }

    /// <summary>A entrada para a trilha, com o identificador desta linha como chave de idempotência.</summary>
    public AuditEntry ToEntry() => new(Operation, ResourceType, ResourceId, Outcome, Id);

    /// <summary>Quem agiu, por qual origem e quando: o que a entrega grava no lugar do contexto e do relógio de quem entrega.</summary>
    public AuditProvenance ToProvenance() => new(ActorUserId, ActorOrigin, CreatedAt);
}
