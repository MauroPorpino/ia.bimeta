using IaBimeta.Audit.Application;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.IntegrationTests.Conversations;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// Atalhos para os casos de uso do Channels sobre o cenário de conversas (tenant, organização, unidades), cada chamada em um
/// escopo próprio (uma "requisição") com o contexto informado e PostgreSQL real.
/// </summary>
internal static class ChannelsTestSupport
{
    public static string Unico(string prefixo) => $"{prefixo}-{Guid.NewGuid():N}";

    /// <summary>Quem gere os canais da organização: o que o papel de administrador da organização concede, e só nela.</summary>
    public static RequestContext Administrador(this CenarioDeConversas c, Guid? organizacao = null) =>
        c.Usuario(organizacao ?? c.OrganizacaoId, Permission.OrganizationRead, Permission.ChannelRead, Permission.ChannelManage, Permission.ConversationRead, Permission.ConversationManage);

    /// <summary>Quem só lê os canais (como o gerente): sem a permissão de gerir.</summary>
    public static RequestContext Leitor(this CenarioDeConversas c, Guid? organizacao = null) =>
        c.Usuario(organizacao ?? c.OrganizacaoId, Permission.OrganizationRead, Permission.ChannelRead);

    public static Task<ChannelAccountView> CriarContaAsync(this CenarioDeConversas c, RequestContext? contexto = null, Guid? organizacao = null, string? contaNoProvedor = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelAccountService>().CreateAsync(
            organizacao ?? c.OrganizacaoId, ChannelProvider.WhatsAppCloud, contaNoProvedor ?? Unico("waba"), "Conta de teste"));

    public static Task<ChannelInstanceView> RegistrarNumeroAsync(this CenarioDeConversas c, Guid conta, RequestContext? contexto = null, string? numero = null, Locale? idioma = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().RegisterAsync(
            conta, "Número de teste", numero ?? Unico("num"), "+55 11 99999-0000", idioma));

    /// <summary>Uma instância cadastrada (conta nova) e ainda inativa, sem unidades.</summary>
    public static async Task<ChannelInstanceView> NovoNumeroAsync(this CenarioDeConversas c, RequestContext? contexto = null, Locale? idioma = null)
    {
        var conta = await c.CriarContaAsync(contexto);
        return await c.RegistrarNumeroAsync(conta.Id, contexto, idioma: idioma);
    }

    public static Task<ChannelInstanceView> AutorizarAsync(this CenarioDeConversas c, Guid instancia, Guid unidade, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().AuthorizeUnitAsync(instancia, unidade));

    public static Task<ChannelInstanceView> RetirarAsync(this CenarioDeConversas c, Guid instancia, Guid unidade, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().RevokeUnitAsync(instancia, unidade));

    public static Task<ChannelInstanceView?> LerNumeroAsync(this CenarioDeConversas c, Guid instancia, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().GetAsync(instancia));

    /// <summary>Os eventos de auditoria do tenant para a operação (e o recurso, se informado), lidos como o administrador do tenant leria.</summary>
    public static Task<IReadOnlyList<AuditEventView>> AuditoriaAsync(this CenarioDeConversas c, string operacao, string? recurso = null) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<AuditQueryService>().ListAsync(200, operacao, recurso));
}
