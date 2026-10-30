using System.Text.Json;
using IaBimeta.ContactCenter.Domain;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// O recibo de uma operação (<see cref="QueueOperation"/>) transformado na resposta que o autor recebe ao repetir o pedido: a mesma
/// resposta que ele já tinha recebido, gravada junto da operação, e nunca uma releitura do item (o item pode ter mudado depois, e o
/// autor pode já nem enxergá-lo).
/// </summary>
internal static class OperationReceipts
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static string Serialize(QueueItemView view) => JsonSerializer.Serialize(view, Options);

    /// <summary>
    /// A resposta de um pedido que já terminou, ou <c>null</c> se a operação ainda está pendente (quem chama a conclui). Um pedido que
    /// foi recusado de forma definitiva devolve a mesma recusa.
    /// </summary>
    /// <exception cref="ContactCenterRuleException">O pedido foi recusado antes.</exception>
    public static QueueItemOperationResult? ToResult(QueueOperation receipt)
    {
        switch (receipt.State)
        {
            case OperationState.Applied:
            case OperationState.NoChange:
                return new QueueItemOperationResult(Deserialize(receipt), OperationOutcome.AlreadyApplied);

            case OperationState.SessionEnded:
                return new QueueItemOperationResult(Deserialize(receipt), OperationOutcome.SessionEnded);

            case OperationState.Cancelled:
                throw new ContactCenterRuleException(
                    (ContactCenterRule)(receipt.FailureRule ?? (int)ContactCenterRule.NoRoute),
                    "O pedido foi recusado antes: o mesmo identificador devolve a mesma recusa. Faça um novo pedido para tentar de novo.");

            default:
                return null;
        }
    }

    private static QueueItemView Deserialize(QueueOperation receipt) =>
        JsonSerializer.Deserialize<QueueItemView>(receipt.Snapshot ?? throw new InvalidOperationException("O recibo terminado não guarda a resposta."), Options)
        ?? throw new InvalidOperationException("A resposta guardada no recibo é inválida.");
}
