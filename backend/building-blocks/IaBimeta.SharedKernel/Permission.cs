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
}
