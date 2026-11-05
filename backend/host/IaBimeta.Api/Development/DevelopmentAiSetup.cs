using IaBimeta.Agents.Application;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Identity.Application;
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

        // O canal de demonstração do WhatsApp (credenciais SINTÉTICAS, só em desenvolvimento) e o atendente de demonstração na fila, para o fluxo ponta a ponta pela interface.
        if (ai.DemoChannel)
        {
            try
            {
                await EnsureWhatsAppChannelAsync(servicos, organizacao.Id, ai, cancellationToken);
            }
            catch (ChannelSecretsNotConfiguredException)
            {
                _log.LogWarning("O canal de demonstração do WhatsApp NÃO foi cadastrado: falta a chave mestra dos canais (Channels:Secrets). Rode tools/dev/Set-ChaveMestraDosCanais.ps1 e reinicie o Api.");
            }

            await EnsureAttendantAsync(servicos, organizacao.Id, fila.Id, cancellationToken);
        }

        _log.LogInformation(
            "IA de desenvolvimento pronta no tenant '{Slug}': organização {OrganizationId}, fila {QueueId}. Confira em GET /simulator/ai/status e GET /simulator/whatsapp/status.",
            ai.TenantSlug, organizacao.Id, fila.Id);
    }

    // O canal de demonstração: valores sintéticos, nunca de produção (a partida falha fora de Development, DevelopmentOnlyGuard).
    internal const string DefaultDemoWabaId = "waba-dev-demo";
    internal const string DefaultDemoPhoneNumberId = "100000000000001";
    internal const string DemoAppSecret = "dev-app-secret-whatsapp-demo";
    internal const string DemoAccessToken = "dev-access-token-whatsapp-demo";
    internal const string DemoVerifyToken = "dev-verify-token-whatsapp-demo";
    internal const string DemoAttendantEmail = "admin@demo-ia.local";

    /// <summary>Cadastra, de forma idempotente, a unidade, a conta, o número ativo, as três credenciais cifradas e o identificador do webhook do canal de demonstração.</summary>
    private static async Task EnsureWhatsAppChannelAsync(IServiceProvider servicos, Guid organizationId, DevelopmentAiOptions ai, CancellationToken cancellationToken)
    {
        var unidades = servicos.GetRequiredService<BusinessUnitService>();
        var contas = servicos.GetRequiredService<ChannelAccountService>();
        var instancias = servicos.GetRequiredService<ChannelInstanceService>();

        const string nomeDaUnidade = "Matriz (demonstração)";
        var unidade = (await unidades.ListByOrganizationAsync(organizationId, cancellationToken)).FirstOrDefault(u => u.Name == nomeDaUnidade)
            ?? await unidades.CreateAsync(organizationId, nomeDaUnidade, IaBimeta.Tenancy.Domain.BusinessUnitKind.Headquarters, cancellationToken);

        var conta = (await contas.ListAsync(cancellationToken)).FirstOrDefault(c => c.ProviderAccountId == ai.DemoWabaId);
        if (conta is null)
        {
            conta = await contas.CreateAsync(organizationId, ChannelProvider.WhatsAppCloud, ai.DemoWabaId, "Conta de demonstração", cancellationToken);
            await contas.SetSecretAsync(conta.Id, ChannelSecretKind.AppSecret, DemoAppSecret, cancellationToken);
            await contas.SetSecretAsync(conta.Id, ChannelSecretKind.AccessToken, DemoAccessToken, cancellationToken);
            await contas.SetSecretAsync(conta.Id, ChannelSecretKind.WebhookVerifyToken, DemoVerifyToken, cancellationToken);
        }

        var instancia = (await instancias.ListAsync(cancellationToken)).FirstOrDefault(i => i.PhoneNumberId == ai.DemoPhoneNumberId)
            ?? await instancias.RegisterAsync(conta.Id, "Número de demonstração", ai.DemoPhoneNumberId, "55 11 3000-0000", null, cancellationToken);
        if (instancia.Status != ChannelStatus.Active)
        {
            await instancias.AuthorizeUnitAsync(instancia.Id, unidade.Id, cancellationToken);
            await instancias.SetDefaultsAsync(instancia.Id, unidade.Id, null, cancellationToken);
            await instancias.ActivateAsync(instancia.Id, cancellationToken);
        }

        await servicos.GetRequiredService<ChannelWebhookAdminService>().EnsureAsync(conta.Id, cancellationToken);
    }

    /// <summary>Põe o administrador de demonstração do tenant (criado pela preparação do Identity) como atendente da fila: é com ele que se entra na interface para assumir, responder e encerrar.</summary>
    private static async Task EnsureAttendantAsync(IServiceProvider servicos, Guid organizationId, Guid queueId, CancellationToken cancellationToken)
    {
        var usuario = (await servicos.GetRequiredService<UserService>().ListAsync(organizationId, cancellationToken)).FirstOrDefault(u => string.Equals(u.Email, DemoAttendantEmail, StringComparison.OrdinalIgnoreCase));
        if (usuario is null)
        {
            return;
        }

        var filas = servicos.GetRequiredService<ServiceQueueService>();
        if ((await filas.ListMembersAsync(queueId, cancellationToken))?.Any(m => m.UserId == usuario.UserId) != true)
        {
            await filas.AddMemberAsync(queueId, usuario.UserId, QueueMemberRole.Agent, cancellationToken);
        }
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
