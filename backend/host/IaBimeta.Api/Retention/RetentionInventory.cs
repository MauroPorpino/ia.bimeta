namespace IaBimeta.Api.Retention;

/// <summary>A natureza dos dados de uma tabela, para a política de retenção (ADR 0025).</summary>
internal enum DataClass
{
    /// <summary>Estado operacional sem valor de negócio depois de um prazo: o expurgo por política atua aqui (e só aqui).</summary>
    Technical,

    /// <summary>Dado pessoal ou conteúdo de cliente/colaborador: nenhum expurgo automático; depende de decisão jurídica e das regras de referência.</summary>
    Personal,

    /// <summary>Registro de referência (trilha de auditoria, consumo de IA): não se apaga por prazo; o que se pode fazer (pseudonimizar) depende de decisão jurídica.</summary>
    LegalRecord,

    /// <summary>Cadastro e configuração do tenant: não expira por tempo; sai com o encerramento da empresa, por procedimento próprio.</summary>
    Configuration,

    /// <summary>Controle do próprio banco (histórico de migrations).</summary>
    Infrastructure,
}

/// <summary>
/// O inventário das tabelas do banco com a classe de cada uma. Toda tabela precisa estar aqui: um teste falha quando uma tabela nova aparece sem classificação, para a decisão de retenção nunca ser esquecida. O
/// inventário é só LEITURA (relatório): classificar uma tabela como pessoal não cria expurgo, e classificá-la como técnica só vale se houver tarefa de expurgo registrada.
/// </summary>
internal static class RetentionInventory
{
    /// <summary>
    /// As tarefas de expurgo que agem sobre OBJETOS do armazenamento privado (e não sobre as linhas de uma tabela técnica). Cada uma é uma EXCEÇÃO nomeada e decidida: o objeto expira, mas a linha do banco (dado
    /// pessoal) nunca é apagada por prazo. Hoje: o conteúdo dos arquivos que o atendente enviou (ADR 0031). O teste de inventário exige que toda tarefa seja de uma tabela técnica OU esteja aqui.
    /// </summary>
    public static readonly IReadOnlySet<string> ObjectStoreTasks = new HashSet<string>(StringComparer.Ordinal) { "conversations.outbound-attachment-objects", "conversations.inbound-attachment-objects" };

    public static readonly IReadOnlyDictionary<string, (DataClass Class, string Note)> Tables = new Dictionary<string, (DataClass, string)>(StringComparer.Ordinal)
    {
        // Técnicas (expurgadas por política)
        ["identity.sessions"] = (DataClass.Technical, "sessões vencidas ou revogadas; só o hash do token"),
        ["identity.password_reset_tokens"] = (DataClass.Technical, "tokens de redefinição encerrados; só o hash"),
        ["identity.login_throttles"] = (DataClass.Technical, "contadores de falha de login parados"),
        ["conversations.outbox_message"] = (DataClass.Technical, "eventos processados; mortos ficam até reenfileirar ou descartar com autorização"),
        ["channels.outbox_message"] = (DataClass.Technical, "o payload do evento do canal carrega o texto do cliente; mortos ficam até reenfileirar ou descartar com autorização"),
        ["channels.inbox_message"] = (DataClass.Technical, "marcas de idempotência (só identificadores)"),
        ["agents.inbox_message"] = (DataClass.Technical, "marcas de idempotência (só identificadores)"),
        ["contact_center.inbox_message"] = (DataClass.Technical, "marcas de idempotência (só identificadores)"),

        // Pessoais ou conteúdo de cliente (somente relatório)
        ["conversations.messages"] = (DataClass.Personal, "conteúdo das mensagens do cliente e das respostas"),
        ["conversations.message_translations"] = (DataClass.Personal, "traduções do conteúdo das mensagens"),
        ["conversations.message_attachments"] = (DataClass.Personal, "metadados dos arquivos do cliente e do atendente (nome, tipo, tamanho, hash); os bytes ficam no armazenamento privado de anexos, FORA do banco e do backup do banco (ADR 0028). O conteúdo do arquivo ENVIADO expira (ObjectStoreTasks, ADR 0031); a linha nunca é apagada por prazo"),
        ["conversations.contacts"] = (DataClass.Personal, "identificadores do contato (telefone, nome)"),
        ["conversations.conversations"] = (DataClass.Personal, "conversas do contato"),
        ["conversations.service_sessions"] = (DataClass.Personal, "atendimentos (numeração nunca reemitida: ADR 0012)"),
        ["conversations.handling_transitions"] = (DataClass.Personal, "histórico de IA/humano do atendimento"),
        ["conversations.session_transfers"] = (DataClass.Personal, "transferências do atendimento"),
        ["contact_center.queue_items"] = (DataClass.Personal, "atendimentos na fila (resumo e atribuição)"),
        ["contact_center.queue_item_history"] = (DataClass.Personal, "histórico do atendimento na fila"),
        ["contact_center.queue_operations"] = (DataClass.Personal, "operações dos atendentes (recibos)"),
        ["contact_center.agent_presences"] = (DataClass.Personal, "presença dos atendentes (colaboradores)"),
        ["channels.outbound_messages"] = (DataClass.Personal, "despacho das respostas (identificadores do provedor)"),
        ["agents.ai_turns"] = (DataClass.Personal, "turnos de IA ligados ao atendimento"),
        ["agents.knowledge_gaps"] = (DataClass.Personal, "lacunas do FAQ (ADR 0030): hash com segredo do tenant de uma pergunta de cliente, contagem de datas e a decisão do administrador; nenhum texto de cliente"),
        ["agents.knowledge_gap_occurrences"] = (DataClass.Personal, "ocorrências das lacunas (ADR 0030): só REFERÊNCIAS ao atendimento, à conversa e à mensagem (o texto fica no Conversations); a referência a mensagem removida é tratada na revisão"),
        ["identity.users"] = (DataClass.Personal, "cadastro dos colaboradores (e-mail, nome, hash de senha)"),
        ["identity.user_organization_grants"] = (DataClass.Personal, "papéis dos colaboradores"),

        // Registros de referência (não se apagam por prazo)
        ["audit.audit_events"] = (DataClass.LegalRecord, "trilha de auditoria"),
        ["contact_center.audit_outbox"] = (DataClass.LegalRecord, "auditoria pendente de entrega ao Audit"),
        ["ai_gateway.ai_requests"] = (DataClass.LegalRecord, "ledger de consumo de IA (a correlação com o atendimento é a parte a pseudonimizar)"),

        // Cadastro e configuração
        ["tenancy.tenants"] = (DataClass.Configuration, "empresas"),
        ["tenancy.organizations"] = (DataClass.Configuration, "organizações"),
        ["tenancy.business_units"] = (DataClass.Configuration, "unidades"),
        ["tenancy.departments"] = (DataClass.Configuration, "setores"),
        ["channels.channel_accounts"] = (DataClass.Configuration, "contas de canal"),
        ["channels.channel_instances"] = (DataClass.Configuration, "números/instâncias"),
        ["channels.channel_instance_units"] = (DataClass.Configuration, "unidades autorizadas por instância"),
        ["channels.channel_routes"] = (DataClass.Configuration, "rotas do canal"),
        ["channels.channel_secrets"] = (DataClass.Configuration, "credenciais cifradas (não são dado pessoal)"),
        ["channels.channel_webhooks"] = (DataClass.Configuration, "identificador opaco do webhook"),
        ["channels.channel_templates"] = (DataClass.Configuration, "modelos de mensagem aprovados na Meta (texto e estrutura de parâmetros; os valores preenchidos ficam na mensagem, não aqui)"),
        ["channels.account_send_state"] = (DataClass.Configuration, "estado de envio da conta"),
        ["contact_center.service_queues"] = (DataClass.Configuration, "filas"),
        ["contact_center.routing_rules"] = (DataClass.Configuration, "regras de roteamento"),
        ["contact_center.queue_memberships"] = (DataClass.Configuration, "atendentes por fila"),
        ["contact_center.queue_schedule_windows"] = (DataClass.Configuration, "horários das filas"),
        ["contact_center.queue_closures"] = (DataClass.Configuration, "fechamentos das filas"),
        ["agents.agent_definition_versions"] = (DataClass.Configuration, "versões da definição do agente"),
        ["ai_gateway.ai_credentials"] = (DataClass.Configuration, "chaves de IA do tenant, cifradas"),
        ["ai_gateway.ai_tenant_settings"] = (DataClass.Configuration, "configuração de IA do tenant"),
        ["ai_gateway.usage_windows"] = (DataClass.Configuration, "janelas de consumo (limites)"),

        // Controle do banco
        ["agents.__ef_migrations_history"] = (DataClass.Infrastructure, "histórico de migrations"),
        ["ai_gateway.__ef_migrations_history"] = (DataClass.Infrastructure, "histórico de migrations"),
        ["audit.__ef_migrations_history"] = (DataClass.Infrastructure, "histórico de migrations"),
        ["channels.__ef_migrations_history"] = (DataClass.Infrastructure, "histórico de migrations"),
        ["contact_center.__ef_migrations_history"] = (DataClass.Infrastructure, "histórico de migrations"),
        ["conversations.__ef_migrations_history"] = (DataClass.Infrastructure, "histórico de migrations"),
        ["identity.__ef_migrations_history"] = (DataClass.Infrastructure, "histórico de migrations"),
        ["tenancy.__ef_migrations_history"] = (DataClass.Infrastructure, "histórico de migrations"),
    };

    public static string Describe(DataClass classe) => classe switch
    {
        DataClass.Technical => "técnica (expurgo por política)",
        DataClass.Personal => "PESSOAL (só relatório)",
        DataClass.LegalRecord => "REFERÊNCIA (só relatório)",
        DataClass.Configuration => "configuração",
        _ => "controle do banco",
    };
}
