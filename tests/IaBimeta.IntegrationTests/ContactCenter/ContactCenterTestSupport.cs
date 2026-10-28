using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// Atalhos para os casos de uso das filas sobre o cenário de conversas (tenant, organização, unidades), cada chamada em um
/// escopo próprio (uma "requisição") com o contexto informado e PostgreSQL real. Sem contexto informado, vale o de sistema do
/// tenant, que as permissões de usuário não limitam.
/// </summary>
internal static class ContactCenterTestSupport
{
    public static string Unico(string prefixo) => $"{prefixo}-{Guid.NewGuid():N}";

    /// <summary>Quem gere as filas da organização: o que o papel de administrador da organização concede, e só nela.</summary>
    public static RequestContext GestorDeFilas(this CenarioDeConversas c, Guid? organizacao = null) =>
        c.Usuario(organizacao ?? c.OrganizacaoId, Permission.OrganizationRead, Permission.QueueRead, Permission.QueueManage);

    /// <summary>Quem só lê as filas (como o gerente): sem a permissão de gerir.</summary>
    public static RequestContext LeitorDeFilas(this CenarioDeConversas c, Guid? organizacao = null) =>
        c.Usuario(organizacao ?? c.OrganizacaoId, Permission.OrganizationRead, Permission.QueueRead);

    public static Task<ServiceQueueView> CriarFilaAsync(
        this CenarioDeConversas c,
        string? nome = null,
        Guid? unidade = null,
        Guid? setor = null,
        Guid? organizacao = null,
        RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().CreateAsync(
            organizacao ?? c.OrganizacaoId, unidade, setor, nome ?? Unico("fila")));

    public static Task<ServiceQueueView?> LerFilaAsync(this CenarioDeConversas c, Guid fila, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().GetAsync(fila));

    public static Task<IReadOnlyList<ServiceQueueView>> ListarFilasAsync(this CenarioDeConversas c, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().ListAsync());

    public static Task<ServiceQueueView> PausarAsync(this CenarioDeConversas c, Guid fila, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().PauseAsync(fila));

    public static Task<ServiceQueueView> RetomarAsync(this CenarioDeConversas c, Guid fila, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().ResumeAsync(fila));

    public static Task<QueueMemberView> AdicionarAtendenteAsync(
        this CenarioDeConversas c, Guid fila, Guid usuario, QueueMemberRole papel = QueueMemberRole.Agent, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().AddMemberAsync(fila, usuario, papel));

    public static Task<bool> RemoverAtendenteAsync(this CenarioDeConversas c, Guid fila, Guid usuario, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().RemoveMemberAsync(fila, usuario));

    public static Task<IReadOnlyList<QueueMemberView>?> AtendentesAsync(this CenarioDeConversas c, Guid fila, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().ListMembersAsync(fila));
}
