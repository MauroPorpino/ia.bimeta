using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Contracts.Commands;

/// <summary>
/// Estado de atendimento e recuperação de falha de envio. Chamado pelo ContactCenter (e por administração). Quem decide
/// se e para quem transferir é o ContactCenter; o Conversations só impõe a máquina de estados e a <c>StateVersion</c>.
/// </summary>
public interface IHandlingCommands
{
    /// <summary>
    /// Muda o modo de atendimento e o responsável de forma idempotente por <c>TransitionId</c>, com a versão esperada
    /// do estado. Não toca no turno em andamento: a nova versão do estado e o novo responsável fazem a resposta dele ser
    /// recusada (<c>NotAiResponder</c> ou <c>StaleState</c>), e o Agents cancela o turno ao receber
    /// <c>HandlingModeChangedV1</c>.
    /// </summary>
    Task<TransitionResult> TransitionHandlingModeAsync(TransitionHandlingModeCommand command, CancellationToken cancellationToken = default);

    Task<CloseSessionResult> CloseSessionAsync(CloseSessionCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ação do atendente: nova solicitação de saída vinculada a uma mensagem em falha definitiva (CONVERSATION-LIFECYCLE.md
    /// §11, COV-7). O autor é o usuário autenticado do <c>RequestContext</c>, nunca um parâmetro, e nenhum componente
    /// automático chama este comando. Não cria turno de IA e não altera a cobertura.
    /// </summary>
    Task<RequestResendResult> RequestResendAsync(RequestResendCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Muda o idioma efetivo da conversa durante o atendimento, com a versão esperada do estado. A mudança avança a
    /// <c>StateVersion</c>: a resposta de um turno em andamento, escrita no idioma anterior, passa a ser recusada
    /// (<c>StaleState</c>), e o Agents cancela o turno ao receber <c>ConversationLocaleChangedV1</c>. As mensagens já
    /// gravadas mantêm o idioma e o conteúdo originais. O autor da escolha vem do <c>RequestContext</c>: um usuário
    /// autorizado (<see cref="LocaleSource.OperatorChoice"/>) ou um componente de sistema que relata a escolha do cliente
    /// (<see cref="LocaleSource.CustomerChoice"/>).
    /// </summary>
    Task<SetLocaleResult> SetConversationLocaleAsync(SetConversationLocaleCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda (ou limpa) a preferência de idioma do contato da sessão. Vale para as conversas futuras do contato; não muda o
    /// idioma da conversa em andamento nem a <c>StateVersion</c>.
    /// </summary>
    Task<SetContactLocaleResult> SetContactPreferredLocaleAsync(SetContactPreferredLocaleCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Encaminha a sessão para outra unidade (filial ou matriz) e setor da MESMA organização, de forma idempotente por
    /// <c>TransferId</c> e com a versão esperada do estado. Exige autorização (<c>ConversationManage</c> sobre a organização
    /// da sessão, ou contexto de sistema do ContactCenter) e deixa rastro: a linha do encaminhamento, o evento
    /// <c>SessionTransferredV1</c> e a auditoria. Avança a <c>StateVersion</c>: o turno de IA em andamento, montado para a
    /// unidade anterior, tem a resposta recusada. Quem decide se e para onde encaminhar é o ContactCenter. A unidade de
    /// destino precisa estar entre as unidades autorizadas do número da sessão, e quem manda é o Channels (a fonte de
    /// verdade): a lista é consultada a cada pedido, sem cópia local que possa ficar velha, e um pedido para unidade não
    /// autorizada devolve <see cref="TransferOutcome.TargetNotAuthorizedForChannel"/>.
    /// </summary>
    Task<TransferSessionResult> TransferSessionAsync(TransferSessionCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra, na sessão, a cerca (<c>fencing token</c>) de quem vai executar operações do ContactCenter sobre ela e devolve a sessão
    /// DEPOIS disso. Uma cerca menor que a maior já vista é recusada (<see cref="FenceOutcome.FencedOut"/>): quem a perdeu foi
    /// substituído por outro executor e nunca mais escreve. Como o registro e a leitura vêm depois de tudo o que um executor mais antigo
    /// conseguiu gravar (a sessão é travada em cada comando), a decisão de quem tem a cerca se baseia no estado verdadeiro da sessão.
    /// </summary>
    Task<FenceHandlingResult> FenceHandlingAsync(FenceHandlingCommand command, CancellationToken cancellationToken = default);
}

/// <param name="FenceToken">O token monotônico de quem executa (maior é mais novo). Positivo.</param>
public sealed record FenceHandlingCommand(Guid SessionId, long FenceToken);

public enum FenceOutcome
{
    /// <summary>A cerca foi registrada (ou já era esta): quem a tem é o executor vigente.</summary>
    Fenced = 1,

    /// <summary>Há uma cerca maior: outro executor assumiu, e este não pode mais escrever.</summary>
    FencedOut = 2,

    SessionNotFound = 3,
}

/// <param name="Session">A sessão lida depois de registrar a cerca; <c>null</c> se não foi registrada.</param>
public sealed record FenceHandlingResult(FenceOutcome Outcome, SessionView? Session);

/// <param name="TargetBusinessUnitId">A unidade de destino, da mesma organização da sessão.</param>
/// <param name="TargetDepartmentId">O setor de destino, se houver. Precisa pertencer à unidade de destino.</param>
/// <param name="ExpectedStateVersion">A versão do estado em que quem decidiu se baseou.</param>
/// <param name="TransferId">Chave de idempotência: repetir o encaminhamento não muda nada.</param>
/// <param name="Reason">Código curto do motivo (por exemplo, <c>specialty-unit</c>), sem texto livre.</param>
/// <param name="FenceToken">
/// A cerca de quem executa (ver <see cref="IHandlingCommands.FenceHandlingAsync"/>), validada DENTRO da trava da sessão: um token menor
/// que o maior já visto é recusado com <see cref="TransferOutcome.FencedOut"/>, antes de qualquer outra regra. Opcional: quem não tem
/// executor concorrente (outros chamadores) não a informa.
/// </param>
/// <param name="Precondition">
/// O estado de atendimento sobre o qual quem comanda AUTORIZOU a operação (ver <see cref="HandlingPrecondition"/>). Conferido dentro da trava
/// da sessão, junto da gravação: se o responsável ou o período já são outros, nada é gravado (<see cref="TransferOutcome.HandlingChanged"/>).
/// Opcional: quem não decide a autorização sobre o responsável atual não a informa.
/// </param>
public sealed record TransferSessionCommand(
    Guid SessionId,
    Guid TargetBusinessUnitId,
    Guid? TargetDepartmentId,
    long ExpectedStateVersion,
    Guid TransferId,
    string Reason,
    long? FenceToken = null,
    HandlingPrecondition? Precondition = null);

/// <summary>
/// A condição do estado de atendimento sobre a qual quem comanda (o ContactCenter) decidiu que a operação é autorizada: a sessão no modo
/// <see cref="Mode"/> (atendimento humano, se não se disser outro), no período <see cref="HandlingPeriod"/>, com o responsável
/// <see cref="ResponderRef"/> (nulo = a conversa espera, sem atendente, ou está com a IA). A autorização sobre o responsável é do chamador;
/// o Conversations só faz a conferência atômica: a condição é checada na MESMA trava e transação da gravação, então a autorização vale para
/// o estado que de fato é alterado, e não para um que já mudou. Isto NÃO substitui a <c>ExpectedStateVersion</c> (que detecta qualquer
/// mudança de estado e pode ser renovada por quem relê a sessão): é o que impede que a renovação da versão transforme uma autorização dada
/// sobre o estado antigo em permissão para alterar o novo.
/// <para>
/// A ENTRADA na fila parte da conversa com a IA (ou esperando, sem atendente) e nunca deve retirar um atendente humano que apareceu depois de
/// autorizada: a condição dela é o modo <see cref="HandlingMode.AiAutonomous"/>, sem responsável, no período lido.
/// </para>
/// <para>
/// Só vale para o comando que MUDARIA o estado: um comando que já encontra a sessão no estado pedido não altera nada e dispensa a condição, e
/// a repetição de um comando já aplicado (mesmo identificador) é reconhecida antes dela (repetição idempotente, mesmo depois de a
/// condição deixar de valer por causa do próprio efeito).
/// </para>
/// </summary>
/// <param name="ResponderRef">O responsável sobre o qual a operação foi autorizada; nulo se a conversa esperava na fila ou estava com a IA.</param>
/// <param name="HandlingPeriod">O período de atendimento humano (<see cref="SessionView.HandlingPeriod"/>) sobre o qual foi autorizada (0 se a sessão nunca teve atendimento humano).</param>
/// <param name="Mode">O modo de atendimento sobre o qual foi autorizada. Atendimento humano, se omitido: o que quem não o informa já assumia.</param>
/// <param name="PlacementEpoch">
/// A geração da colocação da sessão (<see cref="SessionView.PlacementEpoch"/>) sobre a qual foi autorizada. Opcional: nula, a colocação não é conferida
/// (quem não a conhece). O comando que muda a unidade ou o setor (<c>TransferSession</c>) a confere ANTES de mudá-la, e o que vem depois dele numa operação
/// composta espera a geração seguinte: assim a operação só vale sobre a colocação que foi autorizada, e uma mudança de unidade ou setor feita por outro
/// no intervalo (inclusive A → B → A) não passa despercebida.
/// </param>
public sealed record HandlingPrecondition(Guid? ResponderRef, long HandlingPeriod, HandlingMode Mode = HandlingMode.HumanAssisted, long? PlacementEpoch = null);

public enum TransferOutcome
{
    Applied = 1,

    /// <summary>Este <c>TransferId</c> já tinha sido aplicado (repetição idempotente).</summary>
    AlreadyApplied = 2,

    SessionNotFound = 3,

    SessionClosed = 4,

    /// <summary>A versão esperada não é a atual: outra mudança venceu.</summary>
    StaleState = 5,

    /// <summary>A unidade ou o setor de destino não existem, não são da organização da sessão ou o setor não é da unidade. Nada foi gravado.</summary>
    TargetNotAllowed = 6,

    /// <summary>A sessão já estava nesta unidade e neste setor: nada mudou e nenhum rastro foi criado.</summary>
    Unchanged = 7,

    /// <summary>
    /// A unidade de destino não está entre as unidades autorizadas do número (instância de canal) pelo qual o cliente fala, na
    /// consulta atual ao Channels, ou essa consulta não pôde confirmar a autorização (falha fechada). Nada foi gravado: não se
    /// escolhe outra unidade no lugar.
    /// </summary>
    TargetNotAuthorizedForChannel = 8,

    /// <summary>Quem pede perdeu a cerca: outro executor, com token maior, assumiu a sessão. Nada foi gravado.</summary>
    FencedOut = 9,

    /// <summary>
    /// A <see cref="HandlingPrecondition"/> não vale mais: a sessão já tem outro responsável (ou está em outro período ou em outro modo: um atendente humano que apareceu, a volta para a IA,
    /// etc.). A autorização foi dada sobre um estado que mudou. Nada foi gravado, e repetir com a versão renovada não muda isto.
    /// </summary>
    HandlingChanged = 10,
}

public sealed record TransferSessionResult(TransferOutcome Outcome, long? StateVersion);

/// <param name="SessionId">A sessão cuja conversa muda de idioma.</param>
/// <param name="Locale">O novo idioma efetivo (BCP 47).</param>
/// <param name="ExpectedStateVersion">A versão do estado em que quem decidiu se baseou.</param>
public sealed record SetConversationLocaleCommand(Guid SessionId, Locale Locale, long ExpectedStateVersion);

public enum SetLocaleOutcome
{
    /// <summary>O idioma (ou a origem da escolha) mudou e a <c>StateVersion</c> avançou.</summary>
    Applied = 1,

    /// <summary>A conversa já estava neste idioma, com esta mesma origem: nada mudou.</summary>
    Unchanged = 2,

    SessionNotFound = 3,

    SessionClosed = 4,

    /// <summary>A versão esperada não é a atual: outra mudança venceu.</summary>
    StaleState = 5,
}

public sealed record SetLocaleResult(SetLocaleOutcome Outcome, long? StateVersion, Locale? EffectiveLocale, LocaleSource? Source);

/// <param name="PreferredLocale">A preferência do contato, ou <c>null</c> para limpá-la.</param>
public sealed record SetContactPreferredLocaleCommand(Guid SessionId, Locale? PreferredLocale);

public enum SetContactLocaleOutcome
{
    Applied = 1,

    Unchanged = 2,

    SessionNotFound = 3,
}

public sealed record SetContactLocaleResult(SetContactLocaleOutcome Outcome);

/// <param name="ResponderRef">O atendente, para <see cref="HandlingMode.HumanAssisted"/>. Vazio significa atendimento pendente na fila.</param>
/// <param name="ExpectedStateVersion">A versão do estado em que quem decidiu se baseou.</param>
/// <param name="TransitionId">
/// Chave de idempotência: repetir a transição não muda nada. Vale também para a transição que não mudou nada (a sessão já estava no
/// estado pedido): o identificador fica registrado, e uma repetição tardia devolve <see cref="TransitionOutcome.AlreadyApplied"/> em
/// vez de reavaliar a transição contra um estado que mudou depois.
/// </param>
/// <param name="FenceToken">A cerca de quem executa (ver <see cref="IHandlingCommands.FenceHandlingAsync"/>), validada dentro da trava da sessão. Opcional.</param>
/// <param name="Precondition">
/// O estado de atendimento sobre o qual quem comanda AUTORIZOU a transição (ver <see cref="HandlingPrecondition"/>), conferido dentro da trava
/// da sessão, junto da gravação: se o responsável ou o período já são outros, nada é gravado (<see cref="TransitionOutcome.HandlingChanged"/>).
/// Opcional.
/// </param>
public sealed record TransitionHandlingModeCommand(
    Guid SessionId,
    HandlingMode To,
    Guid? ResponderRef,
    long ExpectedStateVersion,
    Guid TransitionId,
    string Reason,
    long? FenceToken = null,
    HandlingPrecondition? Precondition = null);

public enum TransitionOutcome
{
    Applied = 1,

    /// <summary>Este <c>TransitionId</c> já tinha sido aplicado (repetição idempotente).</summary>
    AlreadyApplied = 2,

    SessionNotFound = 3,

    SessionClosed = 4,

    /// <summary>A versão esperada não é a atual: outra transição venceu.</summary>
    StaleState = 5,

    /// <summary>O modo pedido ainda não está especificado (<see cref="HandlingMode.AiSupervised"/>).</summary>
    ModeNotSupported = 6,

    /// <summary>Combinação de modo e responsável inválida (por exemplo, responsável humano em modo de IA), ou a sessão já está nesse estado.</summary>
    InvalidTransition = 7,

    /// <summary>Quem pede perdeu a cerca: outro executor, com token maior, assumiu a sessão. Nada foi gravado.</summary>
    FencedOut = 8,

    /// <summary>
    /// A <see cref="HandlingPrecondition"/> não vale mais: a sessão já tem outro responsável (ou está em outro período ou em outro modo: um atendente humano que apareceu, a volta para a IA,
    /// etc.). A autorização foi dada sobre um estado que mudou. Nada foi gravado, e repetir com a versão renovada não muda isto.
    /// </summary>
    HandlingChanged = 9,
}

/// <param name="MessageSeq">
/// A sequência da última mensagem da sessão NO INSTANTE em que a transição MUDOU o estado (<c>Applied</c>, e <c>AlreadyApplied</c> da
/// repetição dela): a fronteira estável entre o que veio antes e o que veio depois, que não depende do relógio nem de quando alguém a
/// processou. É <b>nula</b> quando a transição não mudou nada (a sessão já estava no estado pedido por outra via, e sua repetição tardia
/// também) e nas transições anteriores a este campo: a fronteira é DESCONHECIDA, e quem a usa deve tratá-la como tal (nunca a trocar pela
/// sequência do momento em que perguntou).
/// </param>
/// <param name="HandlingPeriod">
/// A identidade do período de atendimento humano em que a sessão ficou DEPOIS desta transição (<c>Applied</c>, e <c>AlreadyApplied</c> da
/// repetição dela): quem abre um acesso por este período (o item do ContactCenter) o guarda. Nula, como <paramref name="MessageSeq"/>,
/// quando a transição não mudou nada e nas transições anteriores a este campo: a identidade é desconhecida e não deve ser inventada.
/// </param>
public sealed record TransitionResult(TransitionOutcome Outcome, long? StateVersion, long? MessageSeq = null, long? HandlingPeriod = null);

public sealed record CloseSessionCommand(Guid SessionId, string Reason);

public sealed record CloseSessionResult(bool SessionFound, bool Closed, long? StateVersion);

/// <param name="RequestId">Chave de idempotência da ação do atendente.</param>
public sealed record RequestResendCommand(Guid MessageId, Guid RequestId);

public enum ResendOutcome
{
    Requested = 1,

    /// <summary>O mesmo <c>RequestId</c> já tinha gerado uma solicitação: devolve a mesma.</summary>
    AlreadyRequested = 2,

    /// <summary>Sem usuário autenticado, ou sem permissão sobre o atendimento.</summary>
    NotAuthorized = 3,

    /// <summary>A mensagem não está em falha definitiva (inclui <c>SendUnknown</c>, que nunca é reenviado sem confirmação).</summary>
    NotDefinitiveFailure = 4,

    /// <summary>Já há um reenvio ativo desta mensagem.</summary>
    ResendInProgress = 5,

    SessionClosed = 6,

    MessageNotFound = 7,
}

public sealed record RequestResendResult(ResendOutcome Outcome, Guid? NewMessageId);
