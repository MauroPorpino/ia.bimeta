using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// O que o usuário pode fazer com os itens de UMA fila. O acesso vem de três fontes explícitas e nenhuma se soma às outras sem
/// querer: a permissão por organização (<see cref="Permission.QueueManage"/> e <see cref="Permission.QueueRead"/>), o papel na
/// fila (atendente ou supervisor) e o fato de ser o responsável pela conversa. Estar em uma fila não dá acesso a nenhuma outra
/// fila, unidade ou conversa do tenant, e o contexto de sistema (o worker, o Agents) passa por todas as portas.
/// </summary>
internal sealed record QueueAccess(RequestContext Context, ServiceQueue Queue, QueueMemberRole? Role)
{
    public Guid? UserId => Context.Principal?.UserId;

    public bool IsMember => Role is not null;

    /// <summary>Gere as filas da organização (ou é o contexto de sistema).</summary>
    public bool IsManager => Context.IsAllowed(Permission.QueueManage, Queue.OrganizationId);

    /// <summary>Supervisiona a fila: quem a gere, ou o supervisor dela.</summary>
    public bool IsSupervisor => IsManager || Role == QueueMemberRole.Supervisor;

    /// <summary>Lê as filas da organização (visão de todos os itens, sem operar).</summary>
    public bool IsOrganizationReader => Context.IsAllowed(Permission.QueueRead, Queue.OrganizationId);

    /// <summary>
    /// O item aparece para o usuário? Quem está na fila (atendente ou supervisor), quem gere as filas da organização e quem as lê
    /// veem a situação dos itens dela (quem é o responsável, em que estado está). O que não aparece responde como inexistente
    /// (TEN-2): uma fila de que o usuário não participa e que ele não lê não existe para ele. O conteúdo da conversa tem regra
    /// própria (<see cref="CanReadConversation"/>).
    /// </summary>
    public bool CanSee(QueueItem item) => CanSeeQueue;

    /// <summary>
    /// A fila atende o lugar onde o item diz que a conversa está (organização, unidade e setor)? Quem lê o conteúdo por uma fila não pode ler
    /// uma conversa que, pelo próprio item, está fora do que a fila cobre.
    /// </summary>
    public bool Covers(QueueItem item) => Queue.Covers(item.OrganizationId, item.BusinessUnitId, item.DepartmentId);

    /// <summary>A fila (e os itens dela) aparece para o usuário: quem está nela, quem a gere e quem lê as filas da organização.</summary>
    public bool CanSeeQueue => IsSupervisor || IsOrganizationReader || IsMember;

    /// <summary>
    /// Opera sobre o atendimento (devolver à fila, transferir, passar para outro atendente, devolver à IA): o responsável pela
    /// conversa ou quem a supervisiona. Um atendente comum não mexe na conversa de outro.
    /// </summary>
    public bool CanWork(QueueItem item) => CanWork(item.AssignedUserId);

    /// <summary>
    /// A mesma regra de <see cref="CanWork(QueueItem)"/> sobre o responsável EFETIVO, o que o Conversations tem a autoridade de dizer
    /// (BOUNDARIES.md §6), e não o que a projeção local do item ainda mostra. As operações que mudam o atendimento autorizam sobre este
    /// responsável e, além disso, sobre o do item: onde os dois divergem, quem só é responsável por um deles não opera.
    /// </summary>
    public bool CanWork(Guid? responderUserId) => IsSupervisor || (UserId is not null && responderUserId == UserId);

    /// <summary>
    /// Lê o conteúdo da conversa: o responsável, o supervisor, ou um atendente da fila enquanto a conversa espera (que poderia
    /// assumi-la de qualquer modo). Quem só lê as filas da organização não lê o conteúdo por aqui.
    /// </summary>
    public bool CanReadConversation(QueueItem item) => CanReadConversation(item.Status, item.AssignedUserId);

    /// <summary>
    /// A mesma regra de <see cref="CanReadConversation(QueueItem)"/> sobre um estado de atendimento que NÃO é o gravado no item: quem a
    /// leitura da conversa consulta é o responsável efetivo, que o Conversations tem a autoridade de dizer (BOUNDARIES.md §6), e não o que a
    /// projeção local ainda mostra.
    /// </summary>
    public bool CanReadConversation(QueueItemStatus status, Guid? assignedUserId) =>
        IsSupervisor
        || (UserId is not null && assignedUserId == UserId)
        || (IsMember && status == QueueItemStatus.Waiting);
}
