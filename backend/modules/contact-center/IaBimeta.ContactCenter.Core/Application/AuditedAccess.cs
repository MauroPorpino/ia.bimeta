using IaBimeta.Audit.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>O usuário enxerga o recurso, mas o papel dele na fila não permite esta operação (resposta 403).</summary>
public sealed class QueueAccessDeniedException : Exception
{
    public QueueAccessDeniedException()
        : base("Acesso negado: o seu papel nesta fila não permite esta operação.")
    {
    }
}

/// <summary>
/// A auditoria das recusas e a entrega da auditoria das mudanças. Nada aqui escreve direto na trilha: tudo passa pelo outbox do módulo
/// (<see cref="IDeferredAudit"/>), que guarda a intenção de forma durável (junto da mudança, ou sozinha, no caso de uma negação) e a
/// entrega depois, com a autoria e o instante originais.
/// </summary>
internal static class AuditedAccess
{
    private const int FlushBatch = 50;

    /// <summary>
    /// Exige a permissão sobre a organização. Se for negada, a negação é GUARDADA de forma durável (e entregue à trilha, se ela responder)
    /// antes de a exceção subir: tentativa de acesso sem permissão é um evento relevante, e a trilha fora do ar não a perde nem muda a recusa.
    /// </summary>
    public static async Task RequireAsync(
        IDeferredAudit audit,
        RequestContext context,
        Permission permission,
        Guid organizationId,
        string operation,
        string resourceType,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        if (context.IsAllowed(permission, organizationId))
        {
            return;
        }

        await audit.RecordAsync(new AuditEntry(operation, resourceType, resourceId, AuditOutcome.Denied), cancellationToken);
        throw new AccessDeniedException(permission);
    }

    /// <summary>
    /// Registra a negação (de forma durável) e devolve a exceção a lançar. É para o acesso que depende do papel do usuário na fila (e não
    /// de uma permissão por organização): quem não é atendente da fila, ou não é o responsável, não faz a operação.
    /// </summary>
    public static async Task<QueueAccessDeniedException> DenyAsync(
        IDeferredAudit audit, string operation, string resourceType, string? resourceId, CancellationToken cancellationToken)
    {
        await audit.RecordAsync(new AuditEntry(operation, resourceType, resourceId, AuditOutcome.Denied), cancellationToken);
        return new QueueAccessDeniedException();
    }

    /// <summary>
    /// Entrega à trilha o que a mudança acabou de guardar, DEPOIS de a mudança estar confirmada. Se a trilha falhar, a entrada continua
    /// guardada e a reconciliação a entrega: a falha nunca desfaz nem contradiz o que já foi confirmado.
    /// </summary>
    public static async Task DeliverAsync(IDeferredAudit audit, CancellationToken cancellationToken)
    {
        try
        {
            await audit.FlushAsync(FlushBatch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fica para a reconciliação.
        }
    }
}
