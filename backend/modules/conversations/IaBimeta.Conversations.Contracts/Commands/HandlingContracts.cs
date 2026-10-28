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
}

/// <param name="TargetBusinessUnitId">A unidade de destino, da mesma organização da sessão.</param>
/// <param name="TargetDepartmentId">O setor de destino, se houver. Precisa pertencer à unidade de destino.</param>
/// <param name="ExpectedStateVersion">A versão do estado em que quem decidiu se baseou.</param>
/// <param name="TransferId">Chave de idempotência: repetir o encaminhamento não muda nada.</param>
/// <param name="Reason">Código curto do motivo (por exemplo, <c>specialty-unit</c>), sem texto livre.</param>
public sealed record TransferSessionCommand(
    Guid SessionId,
    Guid TargetBusinessUnitId,
    Guid? TargetDepartmentId,
    long ExpectedStateVersion,
    Guid TransferId,
    string Reason);

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
/// <param name="TransitionId">Chave de idempotência: repetir a transição não muda nada.</param>
public sealed record TransitionHandlingModeCommand(
    Guid SessionId,
    HandlingMode To,
    Guid? ResponderRef,
    long ExpectedStateVersion,
    Guid TransitionId,
    string Reason);

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

    /// <summary>Combinação de modo e responsável inválida (por exemplo, responsável humano em modo de IA).</summary>
    InvalidTransition = 7,
}

public sealed record TransitionResult(TransitionOutcome Outcome, long? StateVersion);

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
