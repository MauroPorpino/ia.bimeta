using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Domain;

/// <summary>
/// Uma entrada de auditoria que ainda não chegou à trilha. Quem grava um estado que precisa de auditoria (a conclusão de um atendimento, a
/// mudança de uma fila, de um membro, de uma regra, de uma política, ou uma negação que o contrato manda auditar) a guarda na MESMA
/// transação do que gravou: se a transação é desfeita, a entrada some junto e a trilha nunca diz "sucesso" para o que não aconteceu; se é
/// confirmada, a entrada é entregue à trilha depois (e, se o processo cair antes, quem reconcilia a entrega). A entrada guarda também QUEM
/// agiu, por qual origem e QUANDO (<see cref="ActorUserId"/>, <see cref="ActorOrigin"/>, <see cref="CreatedAt"/>), capturados do contexto
/// confiável e do relógio na hora da operação: a entrega, que pode vir de outro processo e muito depois, grava com esses valores e não
/// com os de quem entrega. A chave é <c>(tenant_id, id)</c>, e o <c>id</c> é a chave de idempotência da entrega.
/// </summary>
public sealed class PendingAudit : ITenantOwned
{
    public const int OperationMaxLength = 100;
    public const int ResourceTypeMaxLength = 60;
    public const int ResourceIdMaxLength = 100;
    public const int ActorOriginMaxLength = 40;

    /// <summary>A origem das entradas guardadas antes de a autoria ser registrada (a linha não sabe quem agiu).</summary>
    public const string UnknownOrigin = "recovered";

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

    /// <summary>A origem do contexto quando a operação aconteceu (por exemplo, <c>session</c> ou <c>worker-reconciler</c>).</summary>
    public string ActorOrigin { get; private set; }

    /// <summary>Quando a operação aconteceu (o instante em que a entrada foi guardada, não o da entrega).</summary>
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
            throw new ContactCenterValidationException("A entrada de auditoria não cabe no que se guarda até a entrega.");
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

    /// <summary>
    /// A entrada para a trilha, com o identificador desta linha como chave de idempotência: entregar a mesma linha duas vezes (o processo
    /// caiu entre gravar na trilha e apagar a linha) grava UM evento só.
    /// </summary>
    public AuditEntry ToEntry() => new(Operation, ResourceType, ResourceId, Outcome, Id);

    /// <summary>Quem agiu, por qual origem e quando: o que a entrega grava no lugar do contexto e do relógio de quem entrega.</summary>
    public AuditProvenance ToProvenance() => new(ActorUserId, ActorOrigin, CreatedAt);
}
