using System.Security.Cryptography;
using System.Text;
using IaBimeta.Agents.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Agents.Domain;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Domain;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Api.Provisioning;

/// <summary>De onde o provisionamento lê os segredos (variáveis de ambiente, ou um dicionário nos testes). Nunca um arquivo.</summary>
internal interface IProvisioningSecrets
{
    string? Get(string name);
}

internal sealed class EnvironmentProvisioningSecrets : IProvisioningSecrets
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

internal enum StepOutcome
{
    Created,
    Exists,
    Updated,
    Skipped,
    Failed,
}

internal sealed record ProvisioningStep(string Name, StepOutcome Outcome, string Detail);

/// <summary>Uma senha gerada (não veio de variável de ambiente): aparece aqui e na saída do comando UMA vez, e em nenhum outro lugar.</summary>
internal sealed record GeneratedSecret(string Owner, string Kind, string Value);

/// <summary>O que o provisionamento fez e o que o operador precisa para seguir (nunca traz segredo vindo do ambiente).</summary>
internal sealed class ProvisioningReport
{
    public List<ProvisioningStep> Steps { get; } = new();

    public List<GeneratedSecret> GeneratedSecrets { get; } = new();

    public Guid? TenantId { get; set; }

    public Guid? OrganizationId { get; set; }

    public Guid? QueueId { get; set; }

    public Guid? ChannelAccountId { get; set; }

    public Guid? ChannelInstanceId { get; set; }

    /// <summary>O caminho do webhook da conta (<c>/webhooks/whatsapp/&lt;id&gt;</c>), para a Meta (ou o script que a simula).</summary>
    public string? WebhookPath { get; set; }

    public bool WhatsAppIsActive { get; set; }

    public bool Succeeded => Steps.All(s => s.Outcome != StepOutcome.Failed);
}

/// <summary>
/// Coloca uma empresa em funcionamento, de forma IDEMPOTENTE (rodar de novo completa o que falta e nunca duplica): tenant, administrador, organização, unidade, fila de atendimento humano com a regra que a escolhe,
/// agente (instruções e perguntas frequentes) publicado, IA do tenant (provedor, modelo e a chave BYOK cifrada), conta de WhatsApp (simulada ou real) com o número, as credenciais cifradas e o webhook, e os atendentes
/// na fila. Tudo pelos casos de uso dos próprios módulos, com o contexto de sistema do tenant (código confiável do host: este comando roda na máquina do operador, com acesso ao banco, e não expõe rota alguma). Nenhum
/// segredo é lido de arquivo, gravado em log ou devolvido: as senhas e chaves vêm de variáveis de ambiente; senha que não veio é GERADA e entregue uma única vez no relatório.
/// </summary>
internal sealed class TenantProvisioner
{
    private const string Origin = "provisioning-cli";
    private const string RuleName = "Todas as conversas";

    internal const string DefaultInstructions =
        "Você é a assistente virtual desta empresa. Responda em português do Brasil, de forma curta, cordial e objetiva. " +
        "Você não conhece preços, agenda, resultados nem orientações clínicas: para esses assuntos, ou quando o cliente pedir para falar com uma pessoa, " +
        "peça a transferência para um atendente humano. Nunca invente informações.";

    internal const string DefaultHandoffNotice = "Vou transferir você para um atendente. Um momento, por favor.";

    // As credenciais SINTÉTICAS do canal simulado: as mesmas do canal de demonstração do desenvolvimento, para o script que faz o papel da Meta assinar o webhook.
    internal const string SimulatedAppSecret = "dev-app-secret-whatsapp-demo";
    internal const string SimulatedAccessToken = "dev-access-token-whatsapp-demo";
    internal const string SimulatedVerifyToken = "dev-verify-token-whatsapp-demo";
    internal const string SimulatedWabaPrefix = "waba-sim-";

    private readonly IServiceProvider _root;
    private readonly IProvisioningSecrets _secrets;

    public TenantProvisioner(IServiceProvider root, IProvisioningSecrets secrets)
    {
        _root = root;
        _secrets = secrets;
    }

    /// <summary>Os identificadores do canal simulado de um tenant, estáveis (derivados do slug) e únicos na plataforma.</summary>
    internal static (string WabaId, string PhoneNumberId) SimulatedIdentifiers(string slug)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(slug));
        var digitos = new StringBuilder("9");
        foreach (var b in hash.Take(11))
        {
            digitos.Append(b % 10);
        }

        return (SimulatedWabaPrefix + slug, digitos.ToString());
    }

    public async Task<ProvisioningReport> ProvisionAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default)
    {
        var report = new ProvisioningReport();
        try
        {
            await using var escopo = _root.CreateAsyncScope();
            var sp = escopo.ServiceProvider;

            var tenant = await EnsureTenantAsync(sp, spec, report, cancellationToken);
            sp.GetRequiredService<RequestContextHolder>().Set(new RequestContext(tenant.TenantId, Origin));
            report.TenantId = tenant.TenantId.Value;

            await RunAsync(report, "Política de usuários", () => EnsurePolicyAsync(sp, tenant, spec, report, cancellationToken));

            var organizacao = await RunAsync(report, "Organização", () => EnsureOrganizationAsync(sp, spec, report, cancellationToken));
            var unidade = await RunAsync(report, "Unidade", () => EnsureUnitAsync(sp, organizacao.Id, spec, report, cancellationToken));
            var fila = await RunAsync(report, "Fila de atendimento humano", () => EnsureQueueAsync(sp, organizacao.Id, spec, report, cancellationToken));
            await RunAsync(report, "Regra de roteamento", () => EnsureRoutingRuleAsync(sp, organizacao.Id, fila.Id, report, cancellationToken));
            await RunAsync(report, "Administrador", () => EnsureAdministratorAsync(sp, organizacao.Id, spec, report, cancellationToken));
            await RunAsync(report, "Agente de IA", () => EnsureAgentAsync(sp, spec, report, cancellationToken));
            await RunAsync(report, "IA do tenant", () => EnsureTenantAiAsync(sp, spec, report, cancellationToken));

            if (spec.WhatsApp is { } whatsApp)
            {
                await RunAsync(report, "WhatsApp", () => EnsureWhatsAppAsync(sp, organizacao.Id, unidade.Id, spec, whatsApp, report, cancellationToken));
                if (whatsApp.Templates.Count > 0)
                {
                    await RunAsync(report, "Modelos de mensagem", () => EnsureTemplatesAsync(sp, whatsApp, report, cancellationToken));
                }
            }
            else
            {
                report.Steps.Add(new ProvisioningStep("WhatsApp", StepOutcome.Skipped, "Não descrito no arquivo (whatsApp). Nenhuma conta foi cadastrada."));
            }

            foreach (var atendente in spec.Attendants)
            {
                await RunAsync(report, $"Atendente {atendente.Email}", () => EnsureAttendantAsync(sp, organizacao.Id, fila.Id, atendente, report, cancellationToken));
            }
        }
        catch (StepFailedException)
        {
            // A etapa já registrou a falha no relatório; o que vem depois dela depende dela e não roda. Repetir o comando, depois de corrigir, completa o resto.
        }

        return report;
    }

    // Execução de uma etapa: a falha vira uma linha do relatório (só o tipo e a mensagem do nosso código, nunca segredo) e interrompe o que depende dela.
    private static async Task<T> RunAsync<T>(ProvisioningReport report, string name, Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StepFailedException)
        {
            report.Steps.Add(new ProvisioningStep(name, StepOutcome.Failed, Describe(ex)));
            throw new StepFailedException(ex);
        }
    }

    private static async Task RunAsync(ProvisioningReport report, string name, Func<Task> step) =>
        await RunAsync<object?>(report, name, async () =>
        {
            await step();
            return null;
        });

    private static string Describe(Exception ex) => ex switch
    {
        ChannelSecretsNotConfiguredException => "A chave mestra dos canais não está configurada (Channels:Secrets). Rode tools/dev/Set-ChaveMestraDosCanais.ps1 e repita.",
        AiGatewayValidationException v => v.Message + " (o modelo precisa constar em AiGateway:Catalog:Models; tools/dev/Set-ConfiguracaoDeIa.ps1 o inclui).",
        AiSecretsUnavailableException => "A chave mestra da IA não está configurada (AiGateway:Secrets). Rode tools/dev/Set-ConfiguracaoDeIa.ps1 e repita.",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private sealed class StepFailedException : Exception
    {
        public StepFailedException(Exception inner)
            : base("etapa falhou", inner)
        {
        }
    }

    // Tenant e administrador

    private async Task<TenantInfo> EnsureTenantAsync(IServiceProvider sp, ProvisioningSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        try
        {
            var diretorio = sp.GetRequiredService<ITenantDirectory>();
            var existente = await diretorio.FindBySlugAsync(spec.Tenant.Slug!.Trim(), ct);
            if (existente is not null)
            {
                report.Steps.Add(new ProvisioningStep("Tenant", StepOutcome.Exists, $"'{existente.Slug}' já existe."));
                return existente;
            }

            Locale? idioma = null;
            if (!string.IsNullOrWhiteSpace(spec.Tenant.DefaultLocale) && !Locale.TryParse(spec.Tenant.DefaultLocale, out idioma))
            {
                throw new InvalidOperationException($"O idioma '{spec.Tenant.DefaultLocale}' (tenant.defaultLocale) não é BCP 47 bem formado.");
            }

            var criado = await sp.GetRequiredService<TenantRegistrationService>().RegisterAsync(spec.Tenant.Slug!, spec.Tenant.Name!, idioma, ct);
            report.Steps.Add(new ProvisioningStep("Tenant", StepOutcome.Created, $"'{criado.Slug}' cadastrado."));
            return criado;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Steps.Add(new ProvisioningStep("Tenant", StepOutcome.Failed, Describe(ex)));
            throw new StepFailedException(ex);
        }
    }

    /// <summary>
    /// A política de usuários da empresa (limite contratado ou a exceção explícita "unlimitedUsers") é criada ANTES de qualquer organização, usuário ou administrador: o provisionamento comercial só prossegue com ela, e o primeiro
    /// administrador exige que ela já exista (o Identity recusa). Repetível: não altera a de uma empresa que já tem. Uma empresa anterior à Etapa 01 (sem política) que o arquivo não descreve fica como estava (ilimitada, compatibilidade).
    /// </summary>
    private async Task EnsurePolicyAsync(IServiceProvider sp, TenantInfo tenant, ProvisioningSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        var servico = sp.GetRequiredService<TenantUserPolicyService>();
        var atual = await servico.GetAsync(tenant.Slug, ct);
        if (atual is { Policy.IsExplicit: true })
        {
            report.Steps.Add(new ProvisioningStep("Política de usuários", StepOutcome.Exists, "já definida (use tenant-users para alterar)."));
            return;
        }

        var informada = spec.Tenant.MaxUsers is not null || spec.Tenant.UnlimitedUsers;
        if (spec.Tenant.MaxUsers is not null && spec.Tenant.UnlimitedUsers)
        {
            throw new InvalidOperationException("tenant.maxUsers e tenant.unlimitedUsers não podem ser usados juntos.");
        }

        if (!informada)
        {
            var nova = report.Steps.Any(s => s.Name == "Tenant" && s.Outcome == StepOutcome.Created);
            if (nova)
            {
                throw new InvalidOperationException("Informe o limite de usuários da empresa nova (tenant.maxUsers) ou a exceção explícita tenant.unlimitedUsers = true. Repita o comando depois de corrigir o arquivo: a empresa não foi liberada para uso.");
            }

            report.Steps.Add(new ProvisioningStep("Política de usuários", StepOutcome.Skipped, "empresa anterior à Etapa 01, sem política: continua sem limite (use tenant-users para definir)."));
            return;
        }

        var limite = spec.Tenant.UnlimitedUsers ? null : spec.Tenant.MaxUsers;
        await servico.EnsureAsync(tenant.TenantId, limite, spec.Tenant.UserRegistrationEnabled, ct);
        sp.GetRequiredService<RequestContextHolder>().Set(new RequestContext(tenant.TenantId, Origin));
        await sp.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(limite is null ? "identity.user-limit.unlimited" : "identity.user-limit.set", "tenant", tenant.TenantId.ToString(), AuditOutcome.Success), ct);
        report.Steps.Add(new ProvisioningStep("Política de usuários", StepOutcome.Created, limite is null ? "ilimitada (exceção explícita e auditada)." : $"limite de {limite} usuário(s); cadastro {(spec.Tenant.UserRegistrationEnabled ? "habilitado" : "desabilitado")}."));
    }

    private async Task EnsureAdministratorAsync(IServiceProvider sp, Guid organizationId, ProvisioningSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        var usuarios = sp.GetRequiredService<UserService>();
        var email = spec.Administrator.Email!.Trim();
        var existente = (await usuarios.ListAsync(organizationId, ct)).FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
        if (existente is not null)
        {
            report.Steps.Add(new ProvisioningStep("Administrador", StepOutcome.Exists, $"{email} já existe (a senha não é alterada)."));
            return;
        }

        var (senha, gerada) = PasswordFor(spec.Administrator.PasswordEnv);
        await usuarios.EnsureFirstTenantAdministratorAsync(email, spec.Administrator.DisplayName, senha, ct);
        if (gerada)
        {
            report.GeneratedSecrets.Add(new GeneratedSecret(email, "senha do administrador", senha));
        }

        report.Steps.Add(new ProvisioningStep("Administrador", StepOutcome.Created, $"{email} criado, com acesso a todo o tenant ({(gerada ? "senha gerada, mostrada uma vez" : "senha da variável de ambiente")})."));
    }

    private async Task EnsureAttendantAsync(IServiceProvider sp, Guid organizationId, Guid queueId, AttendantSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        // Seed de atendentes é de ambiente INTERNO: só no ambiente Development (a flag unlimitedUsers sozinha não torna uma empresa comercial interna). Numa empresa comercial, quem cadastra os usuários é o administrador dela.
        if (!sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().IsDevelopment())
        {
            throw new InvalidOperationException("A criação de atendentes pelo provisionamento só é permitida no ambiente de desenvolvimento (Development): numa empresa comercial, o administrador da empresa cadastra os usuários.");
        }

        var usuarios = sp.GetRequiredService<UserService>();
        var email = spec.Email!.Trim();
        var existente = (await usuarios.ListAsync(organizationId, ct)).FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
        Guid userId;
        string detalhe;
        if (existente is null)
        {
            var (senha, gerada) = PasswordFor(spec.PasswordEnv);
            var criado = await usuarios.CreateAsync(organizationId, email, spec.DisplayName, senha, Role.User, null, ct);
            userId = criado.Id;
            if (gerada)
            {
                report.GeneratedSecrets.Add(new GeneratedSecret(email, "senha do atendente", senha));
            }

            detalhe = $"criado ({(gerada ? "senha gerada, mostrada uma vez" : "senha da variável de ambiente")})";
        }
        else
        {
            userId = existente.UserId;
            detalhe = "já existia (a senha não é alterada)";
        }

        var filas = sp.GetRequiredService<ServiceQueueService>();
        var jaNaFila = (await filas.ListMembersAsync(queueId, ct))?.Any(m => m.UserId == userId) == true;
        if (!jaNaFila)
        {
            await filas.AddMemberAsync(queueId, userId, QueueMemberRole.Agent, ct);
        }

        report.Steps.Add(new ProvisioningStep($"Atendente {email}", existente is null ? StepOutcome.Created : jaNaFila ? StepOutcome.Exists : StepOutcome.Updated, $"{detalhe}; {(jaNaFila ? "já estava na fila" : "colocado na fila")}."));
    }

    // Estrutura de atendimento

    private static async Task<OrganizationView> EnsureOrganizationAsync(IServiceProvider sp, ProvisioningSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        var organizacoes = sp.GetRequiredService<OrganizationService>();
        var nome = spec.Organization.Name!.Trim();
        var existente = (await organizacoes.ListAsync(ct)).FirstOrDefault(o => string.Equals(o.Name, nome, StringComparison.Ordinal));
        var organizacao = existente ?? await organizacoes.CreateAsync(nome, ct);
        report.OrganizationId = organizacao.Id;
        report.Steps.Add(new ProvisioningStep("Organização", existente is null ? StepOutcome.Created : StepOutcome.Exists, $"'{nome}'."));
        return organizacao;
    }

    private static async Task<BusinessUnitView> EnsureUnitAsync(IServiceProvider sp, Guid organizationId, ProvisioningSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        var unidades = sp.GetRequiredService<BusinessUnitService>();
        var nome = string.IsNullOrWhiteSpace(spec.Organization.UnitName) ? "Matriz" : spec.Organization.UnitName.Trim();
        var existente = (await unidades.ListByOrganizationAsync(organizationId, ct)).FirstOrDefault(u => string.Equals(u.Name, nome, StringComparison.Ordinal));
        var unidade = existente ?? await unidades.CreateAsync(organizationId, nome, IaBimeta.Tenancy.Domain.BusinessUnitKind.Headquarters, ct);
        report.Steps.Add(new ProvisioningStep("Unidade", existente is null ? StepOutcome.Created : StepOutcome.Exists, $"'{nome}'."));
        return unidade;
    }

    private static async Task<ServiceQueueView> EnsureQueueAsync(IServiceProvider sp, Guid organizationId, ProvisioningSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        var filas = sp.GetRequiredService<ServiceQueueService>();
        var nome = spec.Queue.Name!.Trim();
        var existente = (await filas.ListAsync(ct)).FirstOrDefault(f => f.OrganizationId == organizationId && string.Equals(f.Name, nome, StringComparison.Ordinal));
        var fila = existente ?? await filas.CreateAsync(organizationId, null, null, nome, ct);
        report.QueueId = fila.Id;
        report.Steps.Add(new ProvisioningStep("Fila de atendimento humano", existente is null ? StepOutcome.Created : StepOutcome.Exists, $"'{nome}'."));
        return fila;
    }

    /// <summary>A regra que serve a qualquer conversa da organização: sem ela o handoff da IA não encontra fila e é recusado.</summary>
    private static async Task EnsureRoutingRuleAsync(IServiceProvider sp, Guid organizationId, Guid queueId, ProvisioningReport report, CancellationToken ct)
    {
        var regras = sp.GetRequiredService<RoutingRuleService>();
        var existe = (await regras.ListAsync(ct)).Any(r => r.OrganizationId == organizationId && r.QueueId == queueId && r.Enabled);
        if (!existe)
        {
            await regras.CreateAsync(organizationId, RuleName, null, null, null, queueId, 0, 0, ct);
        }

        report.Steps.Add(new ProvisioningStep("Regra de roteamento", existe ? StepOutcome.Exists : StepOutcome.Created, "Toda conversa da organização vai para a fila de atendimento humano."));
    }

    // IA

    /// <summary>Publica a definição do agente se o tenant ainda não tem nenhuma ou se as instruções, o aviso ou as perguntas frequentes do arquivo diferem das publicadas (versão nova).</summary>
    private static async Task EnsureAgentAsync(IServiceProvider sp, ProvisioningSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        var definicoes = sp.GetRequiredService<AgentDefinitionService>();
        var desejado = new AgentDefinitionContent(
            string.IsNullOrWhiteSpace(spec.Ai.Instructions) ? DefaultInstructions : spec.Ai.Instructions.Trim(),
            spec.Ai.Faq.Select(f => new FaqEntry(f.Question!.Trim(), f.Answer!.Trim(), f.Aliases?.Select(a => a.Trim()).ToList(), f.AutoReply)).ToList(),
            HandoffNoticeText: string.IsNullOrWhiteSpace(spec.Ai.HandoffNotice) ? DefaultHandoffNotice : spec.Ai.HandoffNotice.Trim(),
            FaqLocale: string.IsNullOrWhiteSpace(spec.Ai.FaqLocale) ? null : Locale.Parse(spec.Ai.FaqLocale).ToString(),
            IntegrationUnavailableNoticeText: string.IsNullOrWhiteSpace(spec.Ai.IntegrationUnavailableNotice) ? null : spec.Ai.IntegrationUnavailableNotice.Trim());

        var publicada = await definicoes.GetPublishedAsync(ct);
        if (publicada is not null && Same(publicada.Content, desejado))
        {
            report.Steps.Add(new ProvisioningStep("Agente de IA", StepOutcome.Exists, $"Versão {publicada.Number} publicada, igual ao arquivo."));
            return;
        }

        await definicoes.SaveDraftAsync(desejado, ct);
        var nova = await definicoes.PublishAsync(ct);
        report.Steps.Add(new ProvisioningStep("Agente de IA", publicada is null ? StepOutcome.Created : StepOutcome.Updated, $"Versão {nova.Number} publicada ({desejado.Faq.Count} pergunta(s) frequente(s), {desejado.Faq.Count(f => f.AutoReply)} com resposta direta, sem chamar a IA)."));
    }

    private static bool Same(AgentDefinitionContent a, AgentDefinitionContent b) =>
        string.Equals(a.Instructions, b.Instructions, StringComparison.Ordinal)
        && string.Equals(a.HandoffNoticeText, b.HandoffNoticeText, StringComparison.Ordinal)
        && string.Equals(a.IntegrationUnavailableNoticeText, b.IntegrationUnavailableNoticeText, StringComparison.Ordinal)
        && a.EffectiveFaqLocale.Equals(b.EffectiveFaqLocale)
        && a.Faq.Count == b.Faq.Count
        && a.Faq.Zip(b.Faq).All(par => SameFaqEntry(par.First, par.Second));

    /// <summary>
    /// Pergunta, resposta, a resposta direta e os aliases (na ordem, depois de aparados e sem repetição, como a definição os guarda). Sem isto, reprovisionar com um alias novo ou com <c>autoReply</c> ligado
    /// seria "sem mudança" e a definição publicada continuaria a antiga. O idioma ausente e <c>pt-BR</c> são o mesmo (<see cref="AgentDefinitionContent.EffectiveFaqLocale"/>).
    /// </summary>
    private static bool SameFaqEntry(FaqEntry a, FaqEntry b) =>
        string.Equals(a.Question, b.Question, StringComparison.Ordinal)
        && string.Equals(a.Answer, b.Answer, StringComparison.Ordinal)
        && a.AutoReply == b.AutoReply
        && a.GetCleanAliases().SequenceEqual(b.GetCleanAliases(), StringComparer.Ordinal);

    /// <summary>Escolhe o provedor e o modelo do tenant e, se a variável de ambiente existir, cadastra a chave BYOK (cifrada). Sem a chave a IA fica configurada e o atendimento humano assume tudo até ela existir.</summary>
    private async Task EnsureTenantAiAsync(IServiceProvider sp, ProvisioningSpec spec, ProvisioningReport report, CancellationToken ct)
    {
        var ia = sp.GetRequiredService<AiTenantConfigurationService>();
        var provedor = spec.Ai.ProviderId!.Trim();
        var modelo = spec.Ai.ModelId!.Trim();

        var situacao = await ia.GetStatusAsync(ct);
        var igual = situacao is { Configured: true, Status: AiTenantStatus.Enabled } && situacao.ProviderId == provedor && situacao.ModelId == modelo;
        if (!igual)
        {
            await ia.SetConfigurationAsync(provedor, modelo, AiTenantStatus.Enabled, ct);
        }

        var nomeDaVariavel = spec.Ai.ApiKeyEnv;
        var chave = string.IsNullOrWhiteSpace(nomeDaVariavel) ? null : _secrets.Get(nomeDaVariavel.Trim());
        string credencial;
        if (!string.IsNullOrWhiteSpace(chave))
        {
            await ia.SetCredentialAsync(provedor, chave.Trim(), ct);
            credencial = "chave do provedor cadastrada (cifrada)";
        }
        else
        {
            situacao = await ia.GetStatusAsync(ct);
            credencial = !situacao.CredentialRequired
                ? "este provedor não usa chave (servidor local ou de rede privada)"
                : situacao.CredentialPresent
                ? "chave do provedor já cadastrada"
                : "SEM chave do provedor: a IA transfere tudo ao atendente até ela ser cadastrada (defina a variável indicada em ai.apiKeyEnv e repita)";
        }

        report.Steps.Add(new ProvisioningStep("IA do tenant", igual ? StepOutcome.Exists : StepOutcome.Created, $"{provedor}/{modelo}; {credencial}."));
    }

    // Modelos de mensagem (templates) aprovados na Meta

    private static async Task EnsureTemplatesAsync(IServiceProvider sp, WhatsAppSpec wa, ProvisioningReport report, CancellationToken ct)
    {
        var contaId = report.ChannelAccountId ?? throw new InvalidOperationException("A conta de WhatsApp ainda não foi cadastrada.");
        var registro = await sp.GetRequiredService<ChannelTemplateService>().RegisterAsync(contaId, System.Text.Json.JsonSerializer.SerializeToElement(wa.Templates), ct);

        var detalhe = $"{registro.Created.Count} novo(s), {registro.Updated.Count} atualizado(s), {registro.Unchanged.Count} sem mudança.";
        var aprovados = registro.Created.Concat(registro.Updated).Concat(registro.Unchanged).Count(m => m.Status == TemplateStatus.Approved);
        detalhe += $" {aprovados} aprovado(s) e enviável(is) pelo atendente.";
        if (registro.Rejected.Count > 0)
        {
            detalhe += $" Recusado(s): {string.Join(" | ", registro.Rejected)}";
        }

        report.Steps.Add(new ProvisioningStep(
            "Modelos de mensagem", registro.Created.Count > 0 ? StepOutcome.Created : registro.Updated.Count > 0 ? StepOutcome.Updated : StepOutcome.Exists, detalhe));
    }

    // WhatsApp

    private async Task EnsureWhatsAppAsync(IServiceProvider sp, Guid organizationId, Guid unitId, ProvisioningSpec spec, WhatsAppSpec wa, ProvisioningReport report, CancellationToken ct)
    {
        var contas = sp.GetRequiredService<ChannelAccountService>();
        var instancias = sp.GetRequiredService<ChannelInstanceService>();
        var simulado = wa.Mode == WhatsAppMode.Simulated;

        var (wabaPadrao, numeroPadrao) = SimulatedIdentifiers(spec.Tenant.Slug!.Trim());
        var wabaId = (string.IsNullOrWhiteSpace(wa.WabaId) ? (simulado ? wabaPadrao : null) : wa.WabaId.Trim()) ?? throw new InvalidOperationException("whatsApp.wabaId é obrigatório.");
        var numeroId = (string.IsNullOrWhiteSpace(wa.PhoneNumberId) ? (simulado ? numeroPadrao : null) : wa.PhoneNumberId.Trim()) ?? throw new InvalidOperationException("whatsApp.phoneNumberId é obrigatório.");
        var nomeDaConta = string.IsNullOrWhiteSpace(wa.AccountName) ? (simulado ? "Conta simulada" : "Conta WhatsApp") : wa.AccountName.Trim();

        var conta = (await contas.ListAsync(ct)).FirstOrDefault(c => c.ProviderAccountId == wabaId);
        var contaCriada = conta is null;
        if (conta is null)
        {
            // As credenciais são resolvidas ANTES de criar a conta: faltando uma variável de ambiente, nada fica pela metade. Só entram na criação da conta:
            // repetir o comando nunca troca a credencial (nem o token que a Meta já conhece).
            string appSecret;
            string accessToken;
            string verifyToken;
            if (simulado)
            {
                (appSecret, accessToken, verifyToken) = (SimulatedAppSecret, SimulatedAccessToken, SimulatedVerifyToken);
            }
            else
            {
                appSecret = Required(wa.AppSecretEnv, "whatsApp.appSecretEnv");
                accessToken = Required(wa.AccessTokenEnv, "whatsApp.accessTokenEnv");
                var doAmbiente = string.IsNullOrWhiteSpace(wa.VerifyTokenEnv) ? null : _secrets.Get(wa.VerifyTokenEnv.Trim());
                verifyToken = string.IsNullOrWhiteSpace(doAmbiente) ? RandomToken(32) : doAmbiente.Trim();
                if (string.IsNullOrWhiteSpace(doAmbiente))
                {
                    report.GeneratedSecrets.Add(new GeneratedSecret(wabaId, "token de verificação do webhook (cole no painel da Meta)", verifyToken));
                }
            }

            conta = await contas.CreateAsync(organizationId, ChannelProvider.WhatsAppCloud, wabaId, nomeDaConta, ct);
            await contas.SetSecretAsync(conta.Id, ChannelSecretKind.AppSecret, appSecret, ct);
            await contas.SetSecretAsync(conta.Id, ChannelSecretKind.AccessToken, accessToken, ct);
            await contas.SetSecretAsync(conta.Id, ChannelSecretKind.WebhookVerifyToken, verifyToken, ct);
        }

        var instancia = (await instancias.ListAsync(ct)).FirstOrDefault(i => i.PhoneNumberId == numeroId)
            ?? await instancias.RegisterAsync(conta.Id, simulado ? "Número simulado" : "Número do WhatsApp", numeroId, string.IsNullOrWhiteSpace(wa.DisplayPhoneNumber) ? (simulado ? "55 11 3000-0000" : numeroId) : wa.DisplayPhoneNumber.Trim(), null, ct);

        if (instancia.Status != ChannelStatus.Active)
        {
            await instancias.AuthorizeUnitAsync(instancia.Id, unitId, ct);
            await instancias.SetDefaultsAsync(instancia.Id, unitId, null, ct);

            // O número REAL só é ativado depois do teste de prontidão (nada sai para clientes de verdade antes de provar a credencial); o simulado nasce ativo.
            if (simulado)
            {
                await instancias.ActivateAsync(instancia.Id, ct);
            }
        }

        var ativo = simulado || instancia.Status == ChannelStatus.Active;
        var webhook = await sp.GetRequiredService<ChannelWebhookAdminService>().EnsureAsync(conta.Id, ct);
        report.ChannelAccountId = conta.Id;
        report.ChannelInstanceId = instancia.Id;
        report.WebhookPath = webhook.Path;
        report.WhatsAppIsActive = ativo;

        var detalhe = simulado
            ? $"Conta simulada (credenciais sintéticas) e número {numeroId} ativos. Webhook: {webhook.Path}."
            : $"Conta {wabaId}, número {numeroId} e credenciais cifradas. Webhook: {webhook.Path}. O número está {(ativo ? "ATIVO" : "INATIVO: rode tools/ops/Test-ProntidaoWhatsApp.ps1 -Live e só então ative")}.";
        report.Steps.Add(new ProvisioningStep("WhatsApp", contaCriada ? StepOutcome.Created : StepOutcome.Exists, detalhe));

        string Required(string? nomeDaVariavel, string campo)
        {
            var valor = string.IsNullOrWhiteSpace(nomeDaVariavel) ? null : _secrets.Get(nomeDaVariavel.Trim());
            return string.IsNullOrWhiteSpace(valor)
                ? throw new InvalidOperationException($"A variável de ambiente '{nomeDaVariavel}' ({campo}) não está definida ou está vazia.")
                : valor.Trim();
        }
    }

    // Segredos

    /// <summary>A senha da variável de ambiente, ou uma gerada (24 caracteres, letras, números e símbolos) quando a variável não existe.</summary>
    private (string Password, bool Generated) PasswordFor(string? envName)
    {
        var doAmbiente = string.IsNullOrWhiteSpace(envName) ? null : _secrets.Get(envName.Trim());
        return string.IsNullOrEmpty(doAmbiente) ? (RandomPassword(), true) : (doAmbiente, false);
    }

    private static string RandomPassword()
    {
        const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_@#%";
        return RandomToken(24, alfabeto);
    }

    private static string RandomToken(int length, string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789")
    {
        var caracteres = new char[length];
        for (var i = 0; i < length; i++)
        {
            caracteres[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(caracteres);
    }
}
