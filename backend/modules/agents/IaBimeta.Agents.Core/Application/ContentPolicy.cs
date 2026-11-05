using IaBimeta.Agents.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;

namespace IaBimeta.Agents.Application;

/// <summary>O que fazer com o grupo de mensagens pendentes de um turno, conforme o estado do conteúdo (MED-1 e MED-2).</summary>
public enum ContentVerdict
{
    /// <summary>Todo o conteúdo do grupo está pronto: o turno segue e chama o modelo.</summary>
    Proceed = 1,

    /// <summary>Há conteúdo pendente dentro da espera adicional: o turno espera (sem modelo e sem gastar tentativa).</summary>
    Wait = 2,

    /// <summary>Só áudio ficou sem conteúdo: resposta informativa determinística do tenant, sem modelo (MED-3).</summary>
    InformativeReply = 3,

    /// <summary>Imagem, documento, vídeo ou outro tipo sem conteúdo: encaminhamento ao atendente com o motivo <c>content-unavailable</c> (MED-4).</summary>
    Handoff = 4,
}

public readonly record struct ContentDecision(ContentVerdict Verdict);

/// <summary>
/// A política do conteúdo pendente (CONVERSATION-LIFECYCLE.md §10). Conteúdo diferente de <c>Ready</c> NUNCA entra no prompt; o turno espera dentro do prazo adicional por tipo e, vencido o prazo (ou
/// com a falha definitiva do conteúdo), aplica-se a política do tipo ao GRUPO inteiro: o modelo nunca vê o conteúdo indisponível, nem o texto que veio junto (MID-6). Sem I/O.
/// </summary>
public static class ContentPolicy
{
    /// <param name="pendingGroup">As mensagens do cliente ainda sem resposta (do snapshot).</param>
    /// <param name="waitStartedAt">Desde quando o turno espera conteúdo (nulo na primeira avaliação).</param>
    public static ContentDecision Evaluate(IReadOnlyList<SnapshotMessage> pendingGroup, DateTimeOffset? waitStartedAt, DateTimeOffset now, AgentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(pendingGroup);
        ArgumentNullException.ThrowIfNull(options);

        var semConteudo = pendingGroup.Where(m => m.ContentState != ContentState.Ready).ToList();
        if (semConteudo.Count == 0)
        {
            return new ContentDecision(ContentVerdict.Proceed);
        }

        // A espera adicional conta do instante em que o turno passou a esperar (o desta avaliação, na primeira vez).
        var inicio = waitStartedAt ?? now;
        var esgotado = semConteudo.All(m =>
            m.ContentState == ContentState.Failed
            || now - inicio >= options.ExtraWaitFor(m.Kind));
        if (!esgotado)
        {
            return new ContentDecision(ContentVerdict.Wait);
        }

        // Prazo esgotado: só áudio, resposta informativa; qualquer outro tipo, o atendente (o grupo inteiro segue a política mais restritiva).
        return semConteudo.All(m => m.Kind == MessageKind.Audio)
            ? new ContentDecision(ContentVerdict.InformativeReply)
            : new ContentDecision(ContentVerdict.Handoff);
    }
}
