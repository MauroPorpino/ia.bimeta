namespace IaBimeta.SharedKernel;

/// <summary>
/// Permissões explícitas da plataforma. O Identity decide quais concessões dão quais permissões; os demais módulos
/// só perguntam se o contexto da operação tem a permissão sobre o recurso (<see cref="AccessPolicy"/>).
/// </summary>
public enum Permission
{
    /// <summary>Ler uma organização. Sem ela, a organização responde como se não existisse.</summary>
    OrganizationRead = 1,

    /// <summary>Criar organizações no tenant. Só existe com concessão de escopo do tenant.</summary>
    OrganizationCreate = 2,

    /// <summary>Ler unidades e setores de uma organização.</summary>
    StructureRead = 3,

    /// <summary>Criar e alterar unidades e setores de uma organização.</summary>
    StructureManage = 4,

    /// <summary>Ler os usuários com acesso a uma organização.</summary>
    UserRead = 5,

    /// <summary>Criar usuários e conceder ou revogar papéis em uma organização.</summary>
    UserManage = 6,

    /// <summary>Consultar a trilha de auditoria do tenant. Só existe com concessão de escopo do tenant.</summary>
    AuditRead = 7,

    /// <summary>Ler as sessões de atendimento e o histórico de mensagens de uma organização.</summary>
    ConversationRead = 8,

    /// <summary>Alterar o estado de uma sessão (transferir, devolver à IA, encerrar) e pedir reenvio de resposta que falhou.</summary>
    ConversationManage = 9,

    /// <summary>
    /// Operação de máquina para máquina (webhook, worker, turno de IA). NUNCA é concedida a usuário: só um contexto de
    /// sistema, criado pelo host, a tem. Protege os comandos que alimentam o estado da conversa (ingestão, turno e
    /// resposta da IA) de serem chamados por uma sessão de usuário.
    /// </summary>
    SystemOperation = 10,

    /// <summary>Alterar as configurações do tenant (por exemplo, o idioma padrão). Só existe com concessão de escopo do tenant.</summary>
    TenantSettingsManage = 11,

    /// <summary>Ler as contas, os números (instâncias de canal) e as unidades autorizadas de uma organização. Nunca dá acesso ao valor de uma credencial.</summary>
    ChannelRead = 12,

    /// <summary>Cadastrar contas e números, definir credenciais, autorizar unidades, mudar padrões e a situação dos canais de uma organização.</summary>
    ChannelManage = 13,

    /// <summary>Ler as filas de atendimento de uma organização e os atendentes de cada fila.</summary>
    QueueRead = 14,

    /// <summary>Criar filas, pausá-las ou retomá-las e gerir os atendentes de cada fila de uma organização.</summary>
    QueueManage = 15,

    /// <summary>Ler as conexões, as operações e o histórico de execuções do motor de integração de uma organização. Nunca dá acesso ao valor de um segredo.</summary>
    IntegrationRead = 16,

    /// <summary>Cadastrar e configurar conexões, definir segredos, criar e publicar operações e habilitar ou desabilitar integrações (ADR 0035).</summary>
    IntegrationManage = 17,

    /// <summary>Testar uma conexão e executar, como teste, operações de LEITURA. Nunca executa escrita.</summary>
    IntegrationTest = 18,

    /// <summary>Executar operações de LEITURA de integração em nome de uma pessoa.</summary>
    IntegrationExecute = 19,

    /// <summary>Executar e confirmar operações de integração que ALTERAM dados no sistema externo. Distinta de <see cref="IntegrationExecute"/> de propósito (autorização explícita de escrita).</summary>
    IntegrationExecuteWrite = 20,

    /// <summary>Ler os fluxos do chatbot visual (rascunhos e versões publicadas) de uma organização (ADR 0037). Nunca dá acesso a conteúdo de cliente.</summary>
    WorkflowRead = 21,

    /// <summary>Criar e editar rascunhos de fluxo de uma organização.</summary>
    WorkflowManage = 22,

    /// <summary>Publicar, aposentar e ativar fluxos de uma organização. Distinta de <see cref="WorkflowManage"/> de propósito: quem edita não coloca no ar.</summary>
    WorkflowPublish = 23,

    /// <summary>Simular fluxos (sem chamada real a IA, sistema externo ou canal).</summary>
    WorkflowTest = 24,

    /// <summary>Administrar as conexões de integração do Embedded (origens, chaves, identidades externas e sessões embutidas; ADR 0039). Só existe com concessão de escopo do tenant: quem administra a federação não é um atendente.</summary>
    EmbedManage = 25,

    /// <summary>
    /// Registrar e consultar o consentimento de contato (ADR 0041) e decidir os casos de possível revogação de uma organização. Por papel (gestor e administrador da organização), aplicada no backend. NUNCA entra em
    /// <c>EmbeddedSessionPolicy.AllowedPermissions</c>: a sessão embutida não a recebe, mesmo de um usuário gestor, e o sistema externo nunca a concede.
    /// </summary>
    ConsentManage = 26,

    /// <summary>Ativar e desativar usuários humanos de uma organização (Etapa 01). Desativar impede o acesso, libera a vaga e preserva o histórico.</summary>
    UserStatusManage = 27,

    /// <summary>Atribuir papéis acima de Atendente e personalizar as permissões individuais de um usuário (Etapa 01). Distinta de <see cref="UserManage"/> de propósito: quem cadastra atendentes não altera permissões administrativas.</summary>
    UserPermissionManage = 28,

    // ---- Plataforma BImeta (Etapa 02, ADR 0043). SÓ existem no tenant técnico da plataforma: nunca são concedidas nem calculadas em uma empresa contratante. ----

    /// <summary>Consultar as empresas contratantes, a situação operacional e a ocupação de vagas (agregados, nunca usuários nem conversas).</summary>
    PlatformTenantRead = 30,

    /// <summary>Cadastrar empresas e provisionar o primeiro administrador.</summary>
    PlatformTenantCreate = 31,

    /// <summary>Alterar os dados cadastrais permitidos de uma empresa.</summary>
    PlatformTenantUpdate = 32,

    /// <summary>Alterar o limite contratado de usuários e a habilitação de novos cadastros.</summary>
    PlatformTenantQuotaManage = 33,

    /// <summary>Reemitir o token de primeiro acesso do administrador inicial que ainda não concluiu a admissão.</summary>
    PlatformFirstAdminManage = 34,

    /// <summary>Suspender e reativar empresas.</summary>
    PlatformTenantSuspend = 35,

    /// <summary>Cadastrar, alterar, ativar e desativar operadores da plataforma, emitir primeiro acesso e revogar sessões deles.</summary>
    PlatformOperatorManage = 36,

    /// <summary>Atribuir o perfil de administrador da plataforma e personalizar as permissões individuais dos operadores.</summary>
    PlatformPermissionManage = 37,

    /// <summary>Consultar a auditoria administrativa da plataforma.</summary>
    PlatformAuditRead = 38,

    /// <summary>Consultar a visão geral (indicadores agregados).</summary>
    PlatformOverviewRead = 39,
}
