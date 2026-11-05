using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Domain;
using IaBimeta.ContactCenter.Application;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Api.Development;

/// <summary>
/// SOMENTE DESENVOLVIMENTO. Prepara o tenant de demonstração para o primeiro teste de IA pelo simulador, de forma idempotente (rodar de novo não duplica nada): uma organização, uma fila de atendimento
/// humano com a regra de roteamento que a escolhe (o handoff precisa de uma fila), a configuração de IA do tenant (provedor e modelo) e uma definição de agente publicada. Tudo pelos casos de uso dos
/// próprios módulos, com o contexto de sistema do tenant (código confiável do host). NÃO cadastra a credencial do provedor: a chave é cadastrada à parte, pela rota do simulador, e nunca passa por
/// arquivo de configuração. Uma falha é registrada em log e não derruba a API, e nada que entra no log carrega segredo.
/// </summary>
internal sealed class DevelopmentAiSetup : IHostedService
{
    private const string Origin = "development-ai-setup";
    private const string RuleName = "Todas as conversas (demonstração)";

    /// <summary>As instruções de demonstração: nenhum dado de negócio, só o comportamento. O restante do prompt (formato da decisão, regras de segurança) é do módulo Agents.</summary>
    internal const string DefaultInstructions =
        "Você é a assistente virtual de demonstração do ia.bimeta. Responda em português do Brasil, de forma curta, cordial e objetiva. " +
        "Este é um ambiente de testes: você não conhece preços, agenda, resultados de exames nem orientações clínicas. " +
        "Para qualquer pergunta sobre esses assuntos, ou quando o cliente pedir para falar com uma pessoa, peça a transferência para um atendente humano.";

    /// <summary>O aviso que o cliente recebe antes da transferência. É texto do TENANT (o Agents não tem aviso embutido): sem ele a conversa é transferida em silêncio.</summary>
    internal const string HandoffNotice = "Vou transferir você para um atendente. Um momento, por favor.";

    private readonly IServiceProvider _servicos;
    private readonly DevelopmentTenancyOptions _opcoes;
    private readonly ILogger<DevelopmentAiSetup> _log;

    public DevelopmentAiSetup(IServiceProvider servicos, DevelopmentTenancyOptions opcoes, ILogger<DevelopmentAiSetup> log)
    {
        _servicos = servicos;
        _opcoes = opcoes;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PrepareAsync(_opcoes.Ai, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Só o tipo e a mensagem do nosso código: nada aqui toca segredo.
            _log.LogWarning(ex, "A preparação da IA de desenvolvimento falhou (as migrations do AiGateway e do Agents foram aplicadas?). A API continua no ar.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task PrepareAsync(DevelopmentAiOptions ai, CancellationToken cancellationToken)
    {
        await using var escopo = _servicos.CreateAsyncScope();
        var servicos = escopo.ServiceProvider;

        var tenant = await servicos.GetRequiredService<ITenantDirectory>().FindBySlugAsync(ai.TenantSlug, cancellationToken);
        if (tenant is null)
        {
            _log.LogWarning("IA de desenvolvimento ignorada: o tenant '{Slug}' não existe (confira Tenancy:Development:SeedTenants).", ai.TenantSlug);
            return;
        }

        servicos.GetRequiredService<RequestContextHolder>().Set(new RequestContext(tenant.TenantId, Origin));

        var organizacao = await EnsureOrganizationAsync(servicos, ai, cancellationToken);
        var fila = await EnsureQueueAsync(servicos, organizacao.Id, ai, cancellationToken);
        await EnsureRoutingRuleAsync(servicos, organizacao.Id, fila.Id, cancellationToken);
        await EnsureAgentAsync(servicos, ai, cancellationToken);
        await EnsureTenantAiAsync(servicos, ai, cancellationToken);

        _log.LogInformation(
            "IA de desenvolvimento pronta no tenant '{Slug}': organização {OrganizationId}, fila {QueueId}. Confira em GET /simulator/ai/status.",
            ai.TenantSlug, organizacao.Id, fila.Id);
    }

    private static async Task<OrganizationView> EnsureOrganizationAsync(IServiceProvider servicos, DevelopmentAiOptions ai, CancellationToken cancellationToken)
    {
        var organizacoes = servicos.GetRequiredService<OrganizationService>();
        var existente = (await organizacoes.ListAsync(cancellationToken)).FirstOrDefault(o => string.Equals(o.Name, ai.OrganizationName, StringComparison.Ordinal));
        return existente ?? await organizacoes.CreateAsync(ai.OrganizationName, cancellationToken);
    }

    private static async Task<ServiceQueueView> EnsureQueueAsync(IServiceProvider servicos, Guid organizationId, DevelopmentAiOptions ai, CancellationToken cancellationToken)
    {
        var filas = servicos.GetRequiredService<ServiceQueueService>();
        var existente = (await filas.ListAsync(cancellationToken)).FirstOrDefault(f => f.OrganizationId == organizationId && string.Equals(f.Name, ai.QueueName, StringComparison.Ordinal));
        return existente ?? await filas.CreateAsync(organizationId, null, null, ai.QueueName, cancellationToken);
    }

    /// <summary>A regra que serve a qualquer conversa da organização (sem canal, unidade nem setor): sem ela o handoff não encontra fila e é recusado.</summary>
    private static async Task EnsureRoutingRuleAsync(IServiceProvider servicos, Guid organizationId, Guid queueId, CancellationToken cancellationToken)
    {
        var regras = servicos.GetRequiredService<RoutingRuleService>();
        var existe = (await regras.ListAsync(cancellationToken)).Any(r => r.OrganizationId == organizationId && r.QueueId == queueId && r.Enabled);
        if (!existe)
        {
            await regras.CreateAsync(organizationId, RuleName, null, null, null, queueId, 0, 0, cancellationToken);
        }
    }

    /// <summary>
    /// Publica a definição do agente se o tenant ainda não tem nenhuma. Se as instruções foram informadas na configuração e diferem das publicadas, publica uma versão nova (é assim que se ajusta o texto
    /// do teste sem painel administrativo). Sem instruções na configuração, uma definição já publicada nunca é tocada.
    /// </summary>
    private async Task EnsureAgentAsync(IServiceProvider servicos, DevelopmentAiOptions ai, CancellationToken cancellationToken)
    {
        var definicoes = servicos.GetRequiredService<AgentDefinitionService>();
        var publicada = await definicoes.GetPublishedAsync(cancellationToken);
        var instrucoes = string.IsNullOrWhiteSpace(ai.AgentInstructions) ? null : ai.AgentInstructions.Trim();

        if (publicada is not null && (instrucoes is null || string.Equals(publicada.Content.Instructions, instrucoes, StringComparison.Ordinal)))
        {
            return;
        }

        var conteudo = new AgentDefinitionContent(
            instrucoes ?? DefaultInstructions,
            new[]
            {
                new FaqEntry(
                    "Como funciona este atendimento de demonstração?",
                    "É um ambiente de testes do ia.bimeta: as respostas são geradas por inteligência artificial e, quando necessário, a conversa é transferida para um atendente humano."),
            },
            HandoffNoticeText: HandoffNotice);

        await definicoes.SaveDraftAsync(conteudo, cancellationToken);
        var nova = await definicoes.PublishAsync(cancellationToken);
        _log.LogInformation("Definição do agente de demonstração publicada (versão {Version}).", nova.Number);
    }

    /// <summary>Escolhe o provedor e o modelo do tenant. Sem modelo configurado não se inventa um: o log diz o que fazer e o resto da preparação segue.</summary>
    private async Task EnsureTenantAiAsync(IServiceProvider servicos, DevelopmentAiOptions ai, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ai.ModelId))
        {
            _log.LogWarning(
                "A IA do tenant NÃO foi configurada: defina Tenancy:Development:Ai:ModelId (o modelo que a SUA conta do provedor tem, e que esteja em AiGateway:Catalog:Models). " +
                "Veja tools/dev/README.md.");
            return;
        }

        var configuracao = servicos.GetRequiredService<AiTenantConfigurationService>();
        var situacao = await configuracao.GetStatusAsync(cancellationToken);
        if (situacao is { Configured: true, Status: AiTenantStatus.Enabled } && situacao.ProviderId == ai.ProviderId && situacao.ModelId == ai.ModelId)
        {
            return;
        }

        try
        {
            await configuracao.SetConfigurationAsync(ai.ProviderId, ai.ModelId, AiTenantStatus.Enabled, cancellationToken);
            _log.LogInformation("IA do tenant configurada: provedor '{Provider}', modelo '{Model}'.", ai.ProviderId, ai.ModelId);
        }
        catch (AiGatewayValidationException ex)
        {
            _log.LogWarning("A IA do tenant NÃO foi configurada: {Reason} Inclua o modelo em AiGateway:Catalog:Models (tools/dev/Set-ConfiguracaoDeIa.ps1 faz isso).", ex.Message);
        }
    }
}
