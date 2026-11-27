using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>Uma permissão como a interface a mostra: código estável, rótulo e grupo funcional.</summary>
public sealed record PermissionInfo(Permission Permission, string Group, string Label, bool TenantOnly);

/// <summary>
/// O catálogo das permissões que podem ser MOSTRADAS e PERSONALIZADAS por usuário (Etapa 01). Só entram permissões efetivamente implementadas e verificadas no backend; ficam de fora as que nunca se concedem a pessoas
/// (<see cref="Permission.SystemOperation"/>). Os grupos funcionais sem permissão implementada (agentes de IA, conhecimento, consumo e custos) não aparecem: as próximas etapas acrescentam permissões aos grupos.
/// </summary>
public static class PermissionCatalog
{
    private static readonly PermissionInfo[] Items =
    {
        new(Permission.UserRead, "Usuários", "Consultar usuários", false),
        new(Permission.UserManage, "Usuários", "Cadastrar e editar usuários, emitir acessos e revogar sessões", false),
        new(Permission.UserStatusManage, "Usuários", "Ativar e desativar usuários", false),
        new(Permission.UserPermissionManage, "Usuários", "Atribuir perfis superiores e personalizar permissões", false),
        new(Permission.OrganizationRead, "Organizações", "Consultar a organização", false),
        new(Permission.OrganizationCreate, "Organizações", "Criar organizações", true),
        new(Permission.StructureRead, "Organizações", "Consultar unidades e setores", false),
        new(Permission.StructureManage, "Organizações", "Gerir unidades e setores", false),
        new(Permission.ConversationRead, "Atendimento", "Ver atendimentos e mensagens", false),
        new(Permission.ConversationManage, "Atendimento", "Atender, transferir e encerrar", false),
        new(Permission.ConsentManage, "Atendimento", "Registrar consentimento de contato", false),
        new(Permission.QueueRead, "Filas", "Consultar filas", false),
        new(Permission.QueueManage, "Filas", "Gerir filas e seus atendentes", false),
        new(Permission.ChannelRead, "Canais", "Consultar canais", false),
        new(Permission.ChannelManage, "Canais", "Gerir canais", false),
        new(Permission.IntegrationRead, "Integrações", "Consultar integrações", false),
        new(Permission.IntegrationManage, "Integrações", "Gerir integrações", false),
        new(Permission.IntegrationTest, "Integrações", "Testar integrações", false),
        new(Permission.IntegrationExecute, "Integrações", "Executar consultas de integração", false),
        new(Permission.IntegrationExecuteWrite, "Integrações", "Executar operações de integração que alteram dados", false),
        new(Permission.EmbedManage, "Integrações", "Administrar conexões do Embedded", true),
        new(Permission.WorkflowRead, "Fluxos", "Consultar fluxos", false),
        new(Permission.WorkflowManage, "Fluxos", "Editar fluxos", false),
        new(Permission.WorkflowPublish, "Fluxos", "Publicar fluxos", false),
        new(Permission.WorkflowTest, "Fluxos", "Simular fluxos", false),
        new(Permission.AuditRead, "Configurações", "Consultar a auditoria", true),
        new(Permission.TenantSettingsManage, "Configurações", "Alterar as configurações do tenant", true),
    };

    private static readonly Dictionary<Permission, PermissionInfo> ByPermission = Items.ToDictionary(i => i.Permission);

    private static readonly PermissionInfo[] PlatformItems =
    {
        new(Permission.PlatformOverviewRead, "Visão geral", "Consultar os indicadores agregados", true),
        new(Permission.PlatformTenantRead, "Empresas", "Consultar empresas e a ocupação de vagas", true),
        new(Permission.PlatformTenantCreate, "Empresas", "Cadastrar empresas e provisionar o primeiro administrador", true),
        new(Permission.PlatformTenantUpdate, "Empresas", "Alterar os dados cadastrais das empresas", true),
        new(Permission.PlatformTenantQuotaManage, "Empresas", "Alterar o limite de usuários e a habilitação de cadastros", true),
        new(Permission.PlatformFirstAdminManage, "Empresas", "Reemitir o primeiro acesso do administrador inicial", true),
        new(Permission.PlatformTenantSuspend, "Empresas", "Suspender e reativar empresas", true),
        new(Permission.PlatformOperatorManage, "Operadores", "Gerir operadores da plataforma", true),
        new(Permission.PlatformPermissionManage, "Operadores", "Atribuir o perfil de administrador e personalizar permissões de operadores", true),
        new(Permission.PlatformAuditRead, "Auditoria", "Consultar a auditoria administrativa", true),
    };

    private static readonly Dictionary<Permission, PermissionInfo> PlatformByPermission = PlatformItems.ToDictionary(i => i.Permission);

    /// <summary>As permissões da plataforma que a interface de operadores mostra e personaliza (nunca aparecem no catálogo de uma empresa).</summary>
    public static IReadOnlyList<PermissionInfo> Platform => PlatformItems;

    public static IReadOnlyList<PermissionInfo> All => Items;

    public static bool IsDelegable(Permission permission) => ByPermission.ContainsKey(permission) || PlatformByPermission.ContainsKey(permission);

    /// <summary>Permissão de escopo do tenant: só existe sem organização.</summary>
    public static bool IsTenantOnly(Permission permission) => (ByPermission.TryGetValue(permission, out var info) || PlatformByPermission.TryGetValue(permission, out info)) && info.TenantOnly;

    /// <summary>A permissão só de tenant exige escopo do tenant; as demais valem em organização OU no tenant.</summary>
    public static bool IsValidInScope(Permission permission, Guid? organizationId) =>
        IsDelegable(permission) && (!IsTenantOnly(permission) || organizationId is null);
}
