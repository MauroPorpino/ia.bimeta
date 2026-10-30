namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// O recurso não existe no tenant ou o usuário não pode nem enxergá-lo. As duas situações são indistinguíveis de propósito,
/// para não revelar a existência do recurso (TEN-2).
/// </summary>
public sealed class ContactCenterResourceNotFoundException : Exception
{
    public ContactCenterResourceNotFoundException(string resource)
        : base($"{resource} não encontrada.")
    {
    }
}

/// <summary>Já existe uma fila com este nome na organização (o nome não diferencia maiúsculas de minúsculas).</summary>
public sealed class QueueNameInUseException : Exception
{
    public QueueNameInUseException()
        : base("Já existe uma fila com este nome na organização.")
    {
    }
}

/// <summary>Outra alteração do mesmo recurso aconteceu primeiro: releia e repita a operação.</summary>
public sealed class ContactCenterConflictException : Exception
{
    public ContactCenterConflictException()
        : base("O recurso foi alterado por outra operação. Releia e tente de novo.")
    {
    }
}

public enum QueueItemConflict
{
    /// <summary>Outro atendente é o responsável pela conversa (ou venceu a disputa por ela).</summary>
    AlreadyAssigned = 1,

    /// <summary>Há outra operação em andamento na conversa: só quem a pediu (com o mesmo identificador) a repete.</summary>
    OperationInProgress = 2,

    /// <summary>A conversa não está mais em atendimento na fila.</summary>
    ItemClosed = 3,

    /// <summary>A conversa já está em outra fila: o caminho é transferir, não entrar de novo.</summary>
    ActiveItemExists = 4,

    /// <summary>O item mudou durante a operação (outra operação venceu): releia e repita.</summary>
    ConcurrentChange = 5,

    /// <summary>A conversa já está com este atendente, ou já está nesta fila.</summary>
    NothingToChange = 6,

    /// <summary>
    /// O identificador do pedido já foi usado por OUTRA operação, outro atendente, outra conversa ou outra fila. O identificador é a
    /// chave de idempotência de UMA operação: reusá-lo para outra coisa nunca devolve nem conclui o que era da primeira.
    /// </summary>
    RequestIdReused = 7,
}

/// <summary>
/// Outro pedido gravou o recibo com este mesmo identificador primeiro (a chave de idempotência é única por tenant). Quem chama relê
/// o recibo: se é o mesmo pedido do mesmo autor, é repetição; senão é reuso indevido do identificador.
/// </summary>
public sealed class OperationIdInUseException : Exception
{
    public OperationIdInUseException()
        : base("O identificador do pedido já foi gravado por outra operação.")
    {
    }
}

/// <summary>A operação não cabe no estado atual do item. Quem chama relê o item e decide (resposta 409).</summary>
public sealed class QueueItemConflictException : Exception
{
    public QueueItemConflictException(QueueItemConflict conflict, string message)
        : base(message)
    {
        Conflict = conflict;
    }

    public QueueItemConflict Conflict { get; }
}

/// <summary>Regras de negócio que recusam a operação (resposta 422). Nada foi alterado na conversa.</summary>
public enum ContactCenterRule
{
    /// <summary>A fila não atende a organização, a unidade ou o setor da conversa.</summary>
    QueueDoesNotCoverSession = 1,

    QueuePaused = 2,

    /// <summary>O atendente de destino não está na fila de destino.</summary>
    TargetNotQueueMember = 3,

    /// <summary>A unidade da fila de destino não está entre as autorizadas do número pelo qual o cliente fala (ADR 0008), ou o Channels não pôde confirmar.</summary>
    UnitNotAuthorizedForChannel = 4,

    /// <summary>A unidade ou o setor de destino não existem na organização da conversa.</summary>
    TargetUnitNotAllowed = 5,

    /// <summary>A conversa já tem um atendente humano definido fora do ContactCenter: ela não entra na fila.</summary>
    ConversationAlreadyHandledByHuman = 6,

    SessionClosed = 7,

    /// <summary>A fila está fora do horário de atendimento e a política dela é recusar (ou não há contingência que atenda).</summary>
    QueueClosed = 8,

    /// <summary>Nenhuma regra de roteamento ligada serve a conversa, ou nenhuma fila que as regras indicam pode recebê-la.</summary>
    NoRoute = 9,

    /// <summary>O atendente já tem o máximo de conversas que a fila permite ao mesmo tempo.</summary>
    AgentAtCapacity = 10,

    /// <summary>
    /// O atendimento no Conversations já não é o sobre o qual a operação foi autorizada (o responsável mudou, ou a conversa saiu do atendimento
    /// humano daquele período) ou a operação não registrou esse estado. Nada foi alterado na conversa: releia e peça de novo.
    /// </summary>
    ConversationChanged = 11,
}

public sealed class ContactCenterRuleException : Exception
{
    public ContactCenterRuleException(ContactCenterRule rule, string message)
        : base(message)
    {
        Rule = rule;
    }

    public ContactCenterRule Rule { get; }
}
