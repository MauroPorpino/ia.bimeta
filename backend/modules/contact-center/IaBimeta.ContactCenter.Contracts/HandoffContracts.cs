namespace IaBimeta.ContactCenter.Contracts;

/// <summary>
/// O único contrato do ContactCenter exposto a outros módulos: pedir o atendimento humano de uma conversa (<c>RequestHandoff</c>, ADR 0017, seção B).
/// O primeiro consumidor é o Agents (a IA decide transferir, ou a política de falha transfere). A implementação é uma fachada fina sobre o caso de uso
/// que o ContactCenter já tem (a conversa entra na fila e o Conversations passa a atendimento humano pendente): ela não cria regra de fila, de
/// roteamento nem de transferência, e a recusa de uma regra volta como <see cref="RequestHandoffOutcome"/>, nunca como exceção do ContactCenter.
/// O tenant e as permissões vêm do contexto confiável do host (nunca do pedido), e quem chama pela IA é um contexto de sistema.
/// </summary>
public interface IHandoffRequests
{
    /// <summary>
    /// Pede o atendimento humano da conversa. É idempotente pelo <see cref="RequestHandoffCommand.RequestId"/>: repetir o mesmo pedido devolve o
    /// mesmo resultado, e pedir de novo para uma conversa que já está em fila não cria um segundo item. Falha de infraestrutura, contexto de tenant
    /// ausente e cancelamento continuam sendo exceções.
    /// </summary>
    Task<RequestHandoffResult> RequestHandoffAsync(RequestHandoffCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// O pedido de atendimento humano. A IA nunca escolhe a unidade nem o atendente: <paramref name="QueueId"/> é opcional e, quando nulo (o caso
/// normal), o roteamento do ContactCenter decide a fila.
/// </summary>
/// <param name="SessionId">A sessão de atendimento (<c>ServiceSessionId</c>) que passa a atendimento humano.</param>
/// <param name="RequestId">A chave de idempotência do pedido (por exemplo, o <c>TurnId</c> do turno que decidiu transferir). Não pode ser vazio.</param>
/// <param name="Reason">Código do motivo: de 1 a 100 caracteres, letras minúsculas, números e <c>_.-</c> (por exemplo, <c>ai-decision.price</c>).</param>
/// <param name="QueueId">A fila pedida, ou nulo para o roteamento decidir.</param>
public sealed record RequestHandoffCommand(Guid SessionId, Guid RequestId, string Reason, Guid? QueueId = null);

/// <summary>
/// O que aconteceu com o pedido. Só os três primeiros valores significam que a conversa está (ou ficará, no <see cref="Pending"/>) em atendimento
/// humano; nenhuma recusa vira sucesso. O resultado nunca revela se uma conversa que o chamador não enxerga existe (TEN-2).
/// </summary>
public enum RequestHandoffOutcome
{
    /// <summary>O pedido foi aplicado agora: a conversa entrou na fila e está em atendimento humano pendente.</summary>
    Completed = 1,

    /// <summary>O mesmo pedido (mesmo identificador) já tinha sido aplicado, ou a conversa já estava na fila: nada mudou.</summary>
    AlreadyApplied = 2,

    /// <summary>A intenção foi registrada, mas o Conversations ainda não confirmou. Repetir o pedido com o MESMO identificador conclui.</summary>
    Pending = 3,

    /// <summary>A conversa já está encerrada (ou encerrou no meio do pedido): não há o que transferir e nada foi alterado.</summary>
    SessionEnded = 4,

    /// <summary>
    /// Uma regra do ContactCenter impediu o encaminhamento e nada foi alterado: nenhuma fila atende a conversa (cobertura, pausa, horário, rota,
    /// unidade não autorizada para o número, atendente sem vaga).
    /// </summary>
    NotRoutable = 5,

    /// <summary>
    /// O pedido foi recusado por outro motivo e nada foi alterado: a conversa não existe ou não é visível, o chamador não pode gerir a conversa, o
    /// pedido é inválido, a conversa já tem atendente humano definido, mudou durante o pedido, está em outra fila ou o identificador foi usado por
    /// outra operação.
    /// </summary>
    Refused = 6,
}

/// <summary>
/// O resultado do pedido. <paramref name="QueueItemId"/> e <paramref name="QueueId"/> só vêm preenchidos quando o pedido produziu ou reencontrou um
/// item de fila (<see cref="RequestHandoffOutcome.Completed"/>, <see cref="RequestHandoffOutcome.AlreadyApplied"/>,
/// <see cref="RequestHandoffOutcome.Pending"/> e <see cref="RequestHandoffOutcome.SessionEnded"/> com item).
/// <para>
/// <paramref name="Retryable"/> e <paramref name="Code"/> distinguem a recusa TRANSITÓRIA da DEFINITIVA, para o consumidor decidir sem interpretar o
/// motivo interno do ContactCenter. Só valem para <see cref="RequestHandoffOutcome.NotRoutable"/>, <see cref="RequestHandoffOutcome.Refused"/> e
/// <see cref="RequestHandoffOutcome.SessionEnded"/> produzido por uma recusa; nos demais desfechos são <c>false</c> e nulo.
/// </para>
/// </summary>
/// <param name="Outcome">O que aconteceu com o pedido.</param>
/// <param name="QueueItemId">O item de fila produzido ou reencontrado, quando houver.</param>
/// <param name="QueueId">A fila do item, quando houver.</param>
/// <param name="Retryable">
/// Verdadeiro só quando a recusa tem causa transitória (a fila pausada ou fora do horário, o atendente sem vaga, a conversa que mudou durante o
/// pedido, uma operação concorrente): repetir o pedido com o MESMO <see cref="RequestHandoffCommand.RequestId"/> depois de alguma espera pode ter
/// outro resultado. Falso nas recusas definitivas (nenhuma fila atende a conversa, a conversa já tem atendente humano, o pedido é inválido, o
/// identificador foi usado por outra operação, a conversa não existe ou não é visível): repetir não muda nada e o consumidor aplica a política de
/// falha dele. Quantas vezes e com que espera repetir é decisão do consumidor, nunca do ContactCenter.
/// </param>
/// <param name="Code">
/// Código estável e curto da causa da recusa (letras minúsculas, números e hífen), só para diagnóstico, auditoria e testes: o consumidor decide por
/// <paramref name="Outcome"/> e <paramref name="Retryable"/>, nunca por este código. Nunca contém dado da conversa nem segredo, e a conversa que não
/// existe e a que o chamador não pode ver têm o MESMO código (TEN-2).
/// </param>
public sealed record RequestHandoffResult(
    RequestHandoffOutcome Outcome,
    Guid? QueueItemId = null,
    Guid? QueueId = null,
    bool Retryable = false,
    string? Code = null);
