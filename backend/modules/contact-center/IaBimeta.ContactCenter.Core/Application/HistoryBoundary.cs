using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.ContactCenter.Application;

/// <summary>A fronteira decidida para um item que acaba de ser encerrado: a sequência e se ela foi CERTIFICADA pelo caminho que o encerrou. Sem certificação, o histórico fica fechado.</summary>
internal readonly record struct EndedBoundary(long? Seq, bool Certified);

/// <summary>
/// A fronteira do histórico que um item pode expor depois de encerrado (ADR 0010, decisões A, G e H). O encerramento NÃO amplia o que o atendimento podia
/// ver: se a sessão já tinha saído do escopo do item (foi encaminhada para outra unidade ou setor, mesmo que tenha voltado, ou o atendimento que ele
/// representava acabou antes), o que a conversa recebeu depois não é da fila dele, mesmo que o ContactCenter só tenha sabido do fim agora, e mesmo que nunca
/// tenha projetado a saída.
/// </summary>
internal static class HistoryBoundary
{
    /// <summary>
    /// A sequência até a qual o item encerrado pode ler, dado o estado da sessão quando ela TERMINOU, e a certificação dela. A sequência da última mensagem só vale
    /// como fronteira se a sessão terminou no escopo do item, e o escopo exige PROVA de continuidade (<see cref="ScopeProof"/>), nunca ausência de prova:
    /// <list type="bullet">
    /// <item><description>
    /// <b>O mesmo atendimento humano, sem interrupção</b> (<see cref="ScopeProof.AttendanceOfSession"/> = <c>Same</c>): a sessão terminou em atendimento humano e no
    /// período que o item guardou. Período desconhecido NÃO é correspondência: a sequência final de um atendimento posterior (humano → IA → humano) não é fronteira
    /// do item antigo. Sem isto, fechado.
    /// </description></item>
    /// <item><description>
    /// <b>A mesma colocação contínua</b> (<see cref="ScopeProof.PlacementOfSession"/> = <c>Same</c>): a geração da colocação é a que o item prendeu, e a unidade e o setor
    /// são os dele. A volta A → B → A é outra geração. Dentro dele, a fronteira é a sequência final.
    /// </description></item>
    /// <item><description>
    /// <b>Saiu da colocação, mas o atendimento humano foi o mesmo:</b> a única fronteira demonstrável é o limite que o item guardou ao ver a saída, e só se ele tem
    /// ORIGEM comprovada (<see cref="QueueItem.HistoryLimitIsValid"/>: certificado e da geração do item). Sem origem, ou sem ele, fechado.
    /// </description></item>
    /// </list>
    /// A conferência é feita contra a sessão, não depende de a saída ter sido projetada. O item ainda aplica o seu limite (<c>min</c>) ao encerrar.
    /// </summary>
    /// <param name="sessao">A sessão encerrada, ou <c>null</c> se ela já não existe (não há como demonstrar nada).</param>
    /// <param name="reportedLastSeq">A sequência final que o evento de encerramento informou, se ele a trouxe (a da própria sessão, se omitida).</param>
    public static EndedBoundary ForEndedSession(QueueItem item, SessionView? sessao, long? reportedLastSeq = null)
    {
        if (sessao is null)
        {
            return default;
        }

        var atendimento = ScopeProof.AttendanceOfSession(item.HandlingPeriod, sessao.HandlingMode == HandlingMode.HumanAssisted, sessao.HandlingPeriod);
        if (atendimento != ProofVerdict.Same)
        {
            return default;
        }

        var colocacao = ScopeProof.PlacementOfSession(
            item.PlacementEpoch, item.BusinessUnitId, item.DepartmentId, sessao.PlacementEpoch, sessao.BusinessUnitId, sessao.DepartmentId);
        if (colocacao == ProofVerdict.Same)
        {
            return new EndedBoundary(reportedLastSeq ?? sessao.LastMessageSeq, true);
        }

        return colocacao == ProofVerdict.Other && item.HistoryLimitIsValid && item.HistoryLimitSeq is > 0 and var limite
            ? new EndedBoundary(limite, true)
            : default;
    }
}
