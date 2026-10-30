using System.Security.Cryptography;
using System.Text;

namespace IaBimeta.ContactCenter.Domain;

/// <summary>
/// O resultado de uma prova de identidade. <see cref="Unknown"/> é o valor PADRÃO (<c>default</c>) e <b>nunca é tratado como <see cref="Same"/></b>: só
/// <c>== Same</c> autoriza o que quer que seja. <see cref="Other"/> é a prova de que é outra coisa (e pode justificar encerrar o item do atendimento
/// anterior); <see cref="Unknown"/> é a ausência de prova (não espelha, não fecha por inferência, não lê, não emite comando).
/// </summary>
public enum ProofVerdict
{
    Unknown = 0,

    Same = 1,

    Other = 2,
}

/// <summary>A origem de uma fronteira de histórico (ver <see cref="ScopeProof.StoredLimit"/> e <see cref="ScopeProof.CandidateLimit"/>).</summary>
public enum BoundaryOrigin
{
    /// <summary>Não há fronteira registrada.</summary>
    None = 0,

    /// <summary>Conhecida e válida: certificada e da geração a que o item está preso. Só esta autoriza conteúdo.</summary>
    Valid = 1,

    /// <summary>Sem prova de origem (registro anterior ao modelo certificado, ou geração do item desconhecida): não autoriza nada. O número é preservado.</summary>
    Unknown = 2,

    /// <summary>Pertence a OUTRA geração da colocação que não a do item: ignorada, nunca substitui nem amplia a do item.</summary>
    OtherGeneration = 3,

    /// <summary>Anterior ao vínculo atual do item (o item mudou de geração por uma operação própria): não vale para o vínculo novo.</summary>
    BeforeBindingChange = 4,

    /// <summary>Contradita pela evidência (uma origem MAIOR que a geração do item, que não pode existir): nunca autoriza.</summary>
    Invalidated = 5,
}

/// <summary>De quem é o identificador de uma operação que o Conversations aplicou: da operação pendente do próprio item, ou de outra. <see cref="Foreign"/> é o padrão.</summary>
public enum OperationVerdict
{
    Foreign = 0,

    Own = 1,
}

/// <summary>Faltas na base de autorização de uma intenção (ver <see cref="ScopeProof.BaseOf"/>).</summary>
[Flags]
public enum BaseGaps
{
    None = 0,

    /// <summary>A intenção não registrou o estado de atendimento sobre o qual foi autorizada.</summary>
    NoBase = 1,

    /// <summary>A base não traz a geração da colocação (intenção anterior ao campo).</summary>
    NoPlacementEpoch = 2,
}

/// <summary>O resultado da prova de completude da base: só a base COMPLETA autoriza o executor a emitir um comando que altera o Conversations.</summary>
public readonly record struct BaseAssessment(BaseGaps Gaps)
{
    public bool IsComplete => Gaps == BaseGaps.None;
}

/// <summary>
/// As CINCO provas de identidade que toda leitura, projeção, reconciliação, fronteira de histórico e execução privilegiada usa, cada uma uma função
/// <b>pequena e pura</b> (sem I/O, sem relógio, sem contexto): recebem FATOS e devolvem um veredito. Nenhuma decide papel ou permissão de usuário ou
/// de fila (isso continua na aplicação) nem substitui a conferência atômica do Conversations (<c>HandlingPrecondition</c>): fornecem os fatos sobre os
/// quais a autorização é dada.
/// <list type="number">
/// <item><description><b>Identidade do atendimento</b> (<see cref="AttendanceOfSession"/>, <see cref="AttendanceOfEvent"/>): o item representa o atendimento humano que a sessão (ou o evento) diz?</description></item>
/// <item><description><b>Continuidade da colocação</b> (<see cref="PlacementOfSession"/>, <see cref="PlacementOfEvent"/>): a sessão está na MESMA colocação CONTÍNUA que o item prendeu (a geração durável, não só a unidade)?</description></item>
/// <item><description><b>Origem da fronteira</b> (<see cref="StoredLimit"/>, <see cref="CandidateLimit"/>): o limite de histórico é da geração do item?</description></item>
/// <item><description><b>Identidade da operação</b> (<see cref="TransitionOwner"/>, <see cref="TransferOwner"/>): o efeito aplicado é o da operação pendente do item?</description></item>
/// <item><description><b>Completude da base</b> (<see cref="BaseOf"/>): a intenção tem tudo o que o executor precisa para escrever no Conversations?</description></item>
/// </list>
/// Regra comum: <see cref="ProofVerdict.Unknown"/> nunca vira <see cref="ProofVerdict.Same"/>, e o desconhecido NEGA (leitura, comando, fronteira) sem
/// concluir que é "outro": não espelha, não fecha por inferência.
/// </summary>
public static class ScopeProof
{
    // 1. Identidade do atendimento

    /// <summary>
    /// O atendimento humano que o item representa continua, sem interrupção, na sessão lida agora: a sessão está em atendimento humano e no MESMO período que o
    /// item guardou. Período do item desconhecido (item anterior ao campo) é <see cref="ProofVerdict.Unknown"/>. Fora do atendimento humano, o atendimento
    /// que o item representava acabou: <see cref="ProofVerdict.Other"/>.
    /// </summary>
    public static ProofVerdict AttendanceOfSession(long? itemPeriod, bool sessionInHumanMode, long sessionPeriod)
    {
        if (itemPeriod is not { } periodo)
        {
            return ProofVerdict.Unknown;
        }

        return sessionInHumanMode && sessionPeriod == periodo ? ProofVerdict.Same : ProofVerdict.Other;
    }

    /// <summary>O evento diz respeito ao atendimento que o item representa? Os dois períodos precisam ser conhecidos; qualquer um desconhecido é <see cref="ProofVerdict.Unknown"/>.</summary>
    public static ProofVerdict AttendanceOfEvent(long? itemPeriod, long? eventPeriod)
    {
        if (itemPeriod is not { } doItem || eventPeriod is not { } doEvento)
        {
            return ProofVerdict.Unknown;
        }

        return doItem == doEvento ? ProofVerdict.Same : ProofVerdict.Other;
    }

    // 2. Continuidade da colocação

    /// <summary>
    /// A sessão está na colocação CONTÍNUA que o item prendeu: a geração é a mesma e a unidade e o setor são os do item. A volta A → B → A é outra geração
    /// (<see cref="ProofVerdict.Other"/>) mesmo com a unidade e o setor de antes. Geração do item desconhecida é <see cref="ProofVerdict.Unknown"/>.
    /// </summary>
    public static ProofVerdict PlacementOfSession(long? itemEpoch, Guid? itemUnit, Guid? itemDepartment, long sessionEpoch, Guid? sessionUnit, Guid? sessionDepartment)
    {
        if (itemEpoch is not { } geracao)
        {
            return ProofVerdict.Unknown;
        }

        return sessionEpoch == geracao && sessionUnit == itemUnit && sessionDepartment == itemDepartment ? ProofVerdict.Same : ProofVerdict.Other;
    }

    /// <summary>A geração da qual a sessão SAIU (no evento) é a que o item prendeu? Qualquer das duas desconhecida é <see cref="ProofVerdict.Unknown"/>.</summary>
    public static ProofVerdict PlacementOfEvent(long? itemEpoch, long? eventFromEpoch)
    {
        if (itemEpoch is not { } geracao || eventFromEpoch is not { } deOnde)
        {
            return ProofVerdict.Unknown;
        }

        return geracao == deOnde ? ProofVerdict.Same : ProofVerdict.Other;
    }

    // 3. Origem da fronteira

    /// <summary>
    /// A origem do limite de histórico que o item guarda. <see cref="BoundaryOrigin.Valid"/> exige o modelo certificado (<paramref name="boundaryModel"/> = 1, que significa
    /// que TODAS as fronteiras positivas do item são certificadas) e a origem igual à geração a que o item está preso. Sem isso é desconhecida, e o número fica só
    /// como metadado: nunca autoriza conteúdo.
    /// </summary>
    public static BoundaryOrigin StoredLimit(long? limitSeq, long? originEpoch, short boundaryModel, long? itemEpoch)
    {
        if (limitSeq is null)
        {
            return BoundaryOrigin.None;
        }

        if (boundaryModel != QueueItemBoundaryModel.Certified || originEpoch is not { } origem || itemEpoch is not { } geracao)
        {
            return BoundaryOrigin.Unknown;
        }

        if (origem == geracao)
        {
            return BoundaryOrigin.Valid;
        }

        return origem < geracao ? BoundaryOrigin.BeforeBindingChange : BoundaryOrigin.Invalidated;
    }

    /// <summary>A fronteira NOVA (de um evento) é da geração a que o item está preso? Só então pode ser gravada; de outra geração é ignorada, e sem prova de origem não se grava.</summary>
    public static BoundaryOrigin CandidateLimit(long? candidateFromEpoch, long? itemEpoch)
    {
        if (candidateFromEpoch is not { } deOnde || itemEpoch is not { } geracao)
        {
            return BoundaryOrigin.Unknown;
        }

        return deOnde == geracao ? BoundaryOrigin.Valid : BoundaryOrigin.OtherGeneration;
    }

    // 4. Identidade da operação

    /// <summary>
    /// A transição aplicada é a da operação pendente do item? O identificador da própria transição ou o DERIVADO dela (a transição "humano sem responsável" da
    /// transferência sem o atendente) é do item; qualquer outro, ou a ausência de operação pendente, é de outra operação (<see cref="OperationVerdict.Foreign"/>).
    /// </summary>
    public static OperationVerdict TransitionOwner(PendingOperation? pending, Guid transitionId) =>
        pending is not null && transitionId != Guid.Empty && (pending.TransitionId == transitionId || DerivedTransitionId(pending.TransitionId) == transitionId)
            ? OperationVerdict.Own
            : OperationVerdict.Foreign;

    /// <summary>O encaminhamento aplicado é o da operação pendente do item (o <c>TransferId</c> que a intenção guardou)? Comparar só o destino NÃO é prova.</summary>
    public static OperationVerdict TransferOwner(PendingOperation? pending, Guid transferId) =>
        pending is not null && transferId != Guid.Empty && pending.TransferId == transferId ? OperationVerdict.Own : OperationVerdict.Foreign;

    /// <summary>O identificador, derivado e estável, da transição "humano sem responsável" que substitui a original na transferência sem o atendente.</summary>
    public static Guid DerivedTransitionId(Guid transitionId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"contact-center:target-unavailable:{transitionId:N}"))[..16]);

    // 5. Completude da base

    /// <summary>
    /// A base da intenção está COMPLETA? Sem ela (a base do atendimento, e nela a geração da colocação) o executor não emite comando algum que altere o
    /// Conversations, nem a cerca (<c>FenceAsync</c>): a validação vem ANTES do fencing. A intenção fica inerte, e o efeito que o Conversations já aplicou é
    /// reconhecido só por evidência (consulta de efeito), nunca por um comando.
    /// </summary>
    public static BaseAssessment BaseOf(PendingOperation pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var faltas = BaseGaps.None;
        if (pending.Basis is not { } basis)
        {
            faltas |= BaseGaps.NoBase | BaseGaps.NoPlacementEpoch;
        }
        else if (basis.PlacementEpoch is null)
        {
            faltas |= BaseGaps.NoPlacementEpoch;
        }

        return new BaseAssessment(faltas);
    }

    /// <summary>
    /// A geração da colocação a que o item fica preso quando a operação conclui: a da AUTORIZAÇÃO, mais uma se a própria operação muda a unidade ou o setor (a
    /// pré-condição atômica garante que foi ela quem a avançou). Nula se a base não a trouxe.
    /// </summary>
    public static long? ExpectedEpoch(PendingOperation pending) =>
        pending.Basis?.PlacementEpoch is { } geracao ? geracao + (pending.BusinessUnitId is null ? 0 : 1) : null;

    /// <summary>
    /// O período de atendimento humano em que a sessão fica DEPOIS da operação, derivado da base: a devolução à IA mantém o período (ele só avança ao entrar em
    /// humano vindo de outro modo); entrar em humano vindo da IA abre o seguinte (o anterior mais um); uma operação já autorizada em atendimento humano o mantém.
    /// Nulo sem base. É o que o recibo original do Conversations precisa confirmar: o período CORRENTE da sessão nunca o substitui.
    /// </summary>
    public static long? ExpectedPeriod(PendingOperation pending)
    {
        if (pending.Basis is not { } basis)
        {
            return null;
        }

        return pending.Kind != PendingKind.ReturnToAi && basis.InAiMode ? basis.HandlingPeriod + 1 : basis.HandlingPeriod;
    }

    /// <summary>Combina provas: só é <see cref="ProofVerdict.Same"/> se TODAS forem; qualquer <see cref="ProofVerdict.Other"/> é <c>Other</c>; do contrário, <see cref="ProofVerdict.Unknown"/>.</summary>
    public static ProofVerdict All(ProofVerdict first, ProofVerdict second)
    {
        if (first == ProofVerdict.Other || second == ProofVerdict.Other)
        {
            return ProofVerdict.Other;
        }

        return first == ProofVerdict.Same && second == ProofVerdict.Same ? ProofVerdict.Same : ProofVerdict.Unknown;
    }
}

/// <summary>
/// O modelo de fronteira de um item (<c>boundary_model</c>): <see cref="Legacy"/> (0) é o padrão e quer dizer que as fronteiras positivas que o item
/// guarda NÃO têm prova de origem; <see cref="Certified"/> (1) quer dizer que TODAS as fronteiras positivas presentes foram certificadas pelo caminho que as
/// gravou. Certificar uma não promove a outra: antes de 0 → 1 cada fronteira positiva é revalidada ou deixa de autorizar conteúdo. Nenhuma promoção automática
/// de registros existentes.
/// </summary>
public static class QueueItemBoundaryModel
{
    public const short Legacy = 0;

    public const short Certified = 1;
}
