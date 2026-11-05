namespace IaBimeta.Api.Development;

/// <summary>
/// Recursos de desenvolvimento (seção <c>Tenancy:Development</c>). Todos desligados por padrão e proibidos fora do
/// ambiente Development (<see cref="DevelopmentOnlyGuard"/>).
/// </summary>
internal sealed class DevelopmentTenancyOptions
{
    public const string SectionName = "Tenancy:Development";

    /// <summary>
    /// Variável de ambiente (ou segredo de usuário) com a senha dos administradores de desenvolvimento. Quando existe, vale
    /// no lugar da senha de <c>SeedAdministrators</c>, para que nenhuma senha precise ficar no arquivo. Fora de Development
    /// a partida falha se ela estiver definida (<see cref="DevelopmentOnlyGuard"/>).
    /// </summary>
    public const string AdminPasswordVariable = "IABIMETA_DEV_ADMIN_PASSWORD";

    /// <summary>Habilita a escolha do tenant pelo cabeçalho X-Dev-Tenant (só loopback).</summary>
    public bool HeaderEnabled { get; set; }

    /// <summary>Aplica as migrations pendentes na partida (só cria esquema e tabelas novas).</summary>
    public bool ApplyMigrations { get; set; }

    /// <summary>
    /// Liga as rotas <c>/simulator</c>, que simulam o Channels (mensagem do cliente) e o Agents (turno e resposta de IA)
    /// para validar o Conversations à mão. É um SIMULADOR: não fala com WhatsApp nem com IA, e as rotas dizem isso.
    /// </summary>
    public bool SimulatorEnabled { get; set; }

    /// <summary>Tenants de demonstração cadastrados na partida quando ainda não existem.</summary>
    public List<SeedTenant> SeedTenants { get; set; } = new();

    /// <summary>
    /// A IA de desenvolvimento (seção <c>Tenancy:Development:Ai</c>): prepara um tenant de demonstração para exercitar o fluxo mensagem, Agents, provedor de IA e atendimento humano pelo simulador. Desligada por padrão.
    /// </summary>
    public DevelopmentAiOptions Ai { get; set; } = new();

    /// <summary>
    /// Administradores de tenant criados na partida quando ainda não existem (credencial sintética de desenvolvimento).
    /// Nunca existe credencial padrão fora do ambiente Development.
    /// </summary>
    public List<SeedAdministrator> SeedAdministrators { get; set; } = new();
}

internal sealed class SeedTenant
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Idioma padrão do tenant (BCP 47, por exemplo <c>en-US</c>). Vazio usa o inicial da plataforma.</summary>
    public string? DefaultLocale { get; set; }
}

internal sealed class SeedAdministrator
{
    public string TenantSlug { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// O tenant de desenvolvimento preparado para o primeiro teste de IA (seção <c>Tenancy:Development:Ai</c>). Na partida, e de forma idempotente, o Api configura o provedor e o modelo do tenant, publica uma
/// definição de agente de demonstração e cria uma organização, uma fila de atendimento humano e a regra que a escolhe (o handoff precisa de uma fila). NÃO guarda nem aceita a chave do provedor aqui: ela é
/// cadastrada pelo serviço da plataforma (cifrada), pela rota <c>PUT /simulator/ai/credential</c>. Proibida fora do ambiente Development (<see cref="DevelopmentOnlyGuard"/>).
/// </summary>
internal sealed class DevelopmentAiOptions
{
    /// <summary>Liga a preparação da IA de desenvolvimento.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// O tenant da IA (já cadastrado por <c>SeedTenants</c>). É um tenant PRÓPRIO, sem conversas antigas: o Worker entrega todo o backlog do outbox do banco, e só um tenant sem histórico garante que
    /// nenhuma conversa antiga do simulador vire, sem querer, uma chamada paga ao provedor.
    /// </summary>
    public string TenantSlug { get; set; } = "demo-ia";

    /// <summary>O provedor do tenant, como o catálogo do AiGateway o conhece.</summary>
    public string ProviderId { get; set; } = "openai";

    /// <summary>
    /// O modelo que a CONTA do provedor realmente tem. Não há valor padrão de propósito: um identificador presumido que a conta não tem faz a primeira chamada falhar. Vazio: o tenant não é configurado (as
    /// demais preparações seguem) e o log diz o que fazer. O modelo precisa estar no catálogo (<c>AiGateway:Catalog:Models</c>).
    /// </summary>
    public string? ModelId { get; set; }

    public string OrganizationName { get; set; } = "Organização de demonstração";

    public string QueueName { get; set; } = "Atendimento humano (demonstração)";

    /// <summary>As instruções do agente. Vazio: um texto de demonstração, sem nenhum dado clínico nem transacional.</summary>
    public string? AgentInstructions { get; set; }
}
