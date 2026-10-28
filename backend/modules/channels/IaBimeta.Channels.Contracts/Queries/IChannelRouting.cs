using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Contracts.Queries;

/// <summary>
/// Consulta, de módulo para módulo, ao cadastro de roteamento das instâncias de canal. O Channels é a fonte de verdade das
/// unidades autorizadas de cada número: quem precisa validar uma unidade contra um canal (o Conversations, ao registrar a
/// mensagem e ao transferir o atendimento) pergunta aqui, em vez de guardar uma cópia que poderia ficar velha.
/// <para>
/// O tenant é sempre o do <see cref="RequestContext"/>: uma instância de outro tenant responde como inexistente. A consulta
/// NÃO aplica permissões de usuário: ela só devolve fatos de roteamento a código confiável do processo, e quem transfere um
/// atendimento (ConversationManage) não precisa ler o cadastro de canais (ChannelRead) para ser validado.
/// </para>
/// </summary>
public interface IChannelRouting
{
    /// <summary>O roteamento da instância, ou <c>null</c> se ela não existe no tenant do contexto.</summary>
    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task<ChannelRoutingView?> FindAsync(ChannelInstanceId channelInstanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verdadeiro só se a instância existe no tenant do contexto e a unidade está entre as autorizadas dela. Instância
    /// inexistente, de outro tenant ou sem a unidade respondem o mesmo: falso (falha fechada). Não depende da situação do
    /// canal: a associação vale mesmo com o canal suspenso.
    /// </summary>
    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task<bool> IsBusinessUnitAuthorizedAsync(ChannelInstanceId channelInstanceId, Guid businessUnitId, CancellationToken cancellationToken = default);
}

/// <param name="OrganizationId">A organização jurídica dona do canal. Toda unidade autorizada é dela.</param>
/// <param name="DefaultLocale">O idioma padrão do número (vale para uma conversa nova sem preferência do contato), ou <c>null</c> se o canal não define.</param>
/// <param name="DefaultBusinessUnitId">A unidade padrão, usada como último recurso quando nem o cliente nem uma regra escolhem: sempre uma das autorizadas.</param>
/// <param name="DefaultDepartmentId">O setor padrão, sempre da unidade padrão.</param>
/// <param name="AuthorizedBusinessUnitIds">As únicas unidades a que este número pode atender. Vazia: nenhuma unidade é aceita.</param>
public sealed record ChannelRoutingView(
    ChannelInstanceId ChannelInstanceId,
    Guid OrganizationId,
    ChannelStatus Status,
    Locale? DefaultLocale,
    Guid? DefaultBusinessUnitId,
    Guid? DefaultDepartmentId,
    IReadOnlyList<Guid> AuthorizedBusinessUnitIds);
