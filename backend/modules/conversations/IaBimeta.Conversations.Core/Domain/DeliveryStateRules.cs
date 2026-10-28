using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Máquina monotônica do estado de envio e entrega (CONVERSATION-LIFECYCLE.md §3.2, BOUNDARIES.md SND-3 e ORD-6): o status
/// só avança, nunca regride. Os estados de falha definitiva são terminais. <c>SendUnknown</c> (resultado ambíguo) é um
/// desvio de <c>Sending</c> e só sai dele por confirmação segura do Channels.
/// </summary>
public static class DeliveryStateRules
{
    public static bool IsDefinitiveFailure(DeliveryState state) =>
        state is DeliveryState.Failed or DeliveryState.Expired or DeliveryState.Dead;

    /// <summary>Estados em que ainda há o que acontecer com a mensagem (um reenvio ativo bloqueia outro).</summary>
    public static bool IsInFlight(DeliveryState state) =>
        state is DeliveryState.Requested or DeliveryState.Queued or DeliveryState.Sending or DeliveryState.SendUnknown;

    public static bool CanAdvance(DeliveryState from, DeliveryState to)
    {
        if (from == to || IsDefinitiveFailure(from))
        {
            return false;
        }

        if (IsDefinitiveFailure(to))
        {
            // Depois de enviada, a mensagem não "falha": o status final do envio já foi decidido.
            return IsInFlight(from);
        }

        if (to == DeliveryState.SendUnknown)
        {
            return from is DeliveryState.Queued or DeliveryState.Sending;
        }

        return Rank(to) > Rank(from);
    }

    private static int Rank(DeliveryState state) => state switch
    {
        DeliveryState.Requested => 0,
        DeliveryState.Queued => 1,
        DeliveryState.Sending => 2,
        DeliveryState.SendUnknown => 3,
        DeliveryState.Sent => 4,
        DeliveryState.Delivered => 5,
        DeliveryState.Read => 6,
        _ => -1,
    };
}
