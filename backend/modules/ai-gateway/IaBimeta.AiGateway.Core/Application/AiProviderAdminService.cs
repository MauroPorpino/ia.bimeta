using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Application;

/// <summary>
/// Os casos de uso da tela "Configurações → Inteligência artificial" (Etapa 05). NÃO duplica nada: a credencial BYOK, o padrão e a auditoria são o <see cref="AiTenantConfigurationService"/>; o teste de geração é o
/// <see cref="AiGatewayService"/> (suspensão, limites, ledger); o teste de conectividade é a porta opcional <see cref="IAiProviderDiagnostics"/> de cada adaptador. O tenant e o usuário vêm do contexto confiável, nunca do
/// pedido; cada operação exige a permissão específica (ou a ampla de configurar o tenant) NO SERVIDOR; nenhum valor de credencial, prompt nem resposta de cliente é devolvido, registrado ou auditado. O tenant só escolhe entre
/// provedores e modelos do CATÁLOGO da plataforma: endereço de servidor, preços e limites comerciais não são dele.
/// </summary>
public sealed class AiProviderAdminService
{
    private static readonly TimeSpan ConnectivityTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Depois deste tempo um resultado de diagnóstico deixa de valer como confirmação (aparece como desatualizado).</summary>
    public static readonly TimeSpan DiagnosticFreshness = TimeSpan.FromHours(24);

    private readonly IRequestContextAccessor _context;
    private readonly AiTenantConfigurationService _configuration;
    private readonly IAiTenantConfigurationStore _store;
    private readonly AiModelSelector _selector;
    private readonly IAiCredentialResolver _credentials;
    private readonly AiGatewayService _gateway;
    private readonly AiDiagnosticStore _diagnostics;
    private readonly AiDiagnosticThrottle _throttle;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ITenantOperationGate? _gate;

    public AiProviderAdminService(
        IRequestContextAccessor context,
        AiTenantConfigurationService configuration,
        IAiTenantConfigurationStore store,
        AiModelSelector selector,
        IAiCredentialResolver credentials,
        AiGatewayService gateway,
        AiDiagnosticStore diagnostics,
        AiDiagnosticThrottle throttle,
        IAuditWriter audit,
        TimeProvider clock,
        ITenantOperationGate? gate = null)
    {
        _context = context;
        _configuration = configuration;
        _store = store;
        _selector = selector;
        _credentials = credentials;
        _gateway = gateway;
        _diagnostics = diagnostics;
        _throttle = throttle;
        _audit = audit;
        _clock = clock;
        _gate = gate;
    }

    // Consulta

    public async Task<AiSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireAsync(contexto, Permission.AiSettingsRead, "ai-gateway.settings.read", cancellationToken);
        var tenant = contexto.TenantId.Value;
        var agora = _clock.GetUtcNow();

        var padrao = await _store.FindSettingsAsync(cancellationToken);
        var providers = new List<AiProviderView>();
        foreach (var providerId in _selector.Catalog.ProviderIds.Order(StringComparer.Ordinal))
        {
            var adaptador = _selector.Providers.Find(providerId);
            var exige = adaptador?.RequiresCredential ?? true;
            var info = exige ? await _store.ReadCredentialInfoAsync(providerId, cancellationToken) : null;
            var marcador = Marker(exige, info);
            var ativos = _selector.Catalog.ActiveModelsOf(providerId);
            var ehPadrao = padrao is not null && padrao.ProviderId == providerId;

            var modelos = ativos.Select(m => new AiModelView(
                m.ModelId,
                m.Profile,
                m.ContextWindowTokens,
                m.MaxOutputTokens,
                ehPadrao && padrao!.ModelId == m.ModelId,
                ToView(_diagnostics.Get(tenant, providerId, m.ModelId, AiDiagnosticKind.Connectivity), marcador, agora),
                ToView(_diagnostics.Get(tenant, providerId, m.ModelId, AiDiagnosticKind.Generation), marcador, agora))).ToList();

            providers.Add(new AiProviderView(
                providerId,
                adaptador is not null,
                ativos.Count > 0,
                exige ? "Byok" : "Infrastructure",
                new AiCredentialView(exige, info is not null, info?.Fingerprint, info?.UpdatedAt),
                ehPadrao,
                adaptador is IAiProviderDiagnostics,
                modelos));
        }

        var avisos = new List<string>();
        if (padrao is null)
        {
            avisos.Add("no-default");
        }
        else
        {
            var atual = providers.FirstOrDefault(p => p.ProviderId == padrao.ProviderId);
            if (atual is null || !atual.Implemented || !atual.Models.Any(m => m.ModelId == padrao.ModelId))
            {
                avisos.Add("default-unavailable");
            }
            else if (atual.Credential.Required && !atual.Credential.Present)
            {
                avisos.Add("default-credential-missing");
            }

            if (padrao.Status == AiTenantStatus.Disabled)
            {
                avisos.Add("ai-disabled");
            }
            else if (padrao.Status == AiTenantStatus.Blocked)
            {
                avisos.Add("ai-blocked");
            }
        }

        return new AiSettingsView(padrao is not null, padrao?.ProviderId, padrao?.ModelId, padrao?.Status.ToString(), padrao?.UpdatedAt, avisos, providers);
    }

    // Padrão

    /// <summary>
    /// Escolhe o provedor e o modelo padrão. Valida no servidor: adaptador registrado, modelo ATIVO do catálogo e credencial presente quando o provedor a exige. NÃO exige chamada paga: o que foi ou não testado volta em
    /// <see cref="AiDefaultSelectionResult.Warnings"/> (configuração ELEGÍVEL ≠ efetivamente testada). Um resultado fresco que diga que o modelo não serve à conversa recusa a escolha; sondagem negativa ou inconclusiva NUNCA é
    /// confirmação de disponibilidade. Nunca faz fallback; a situação habilitada/desabilitada da IA é preservada.
    /// </summary>
    public async Task<AiDefaultSelectionResult> SetDefaultAsync(string providerId, string modelId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireAsync(contexto, Permission.AiSettingsManage, "ai-gateway.settings.set", cancellationToken);
        var tenant = contexto.TenantId.Value;

        var (provedor, modelo) = ResolveModel(providerId, modelId, requireModel: true);
        var exige = provedor.RequiresCredential;
        var info = exige ? await _store.ReadCredentialInfoAsync(providerId, cancellationToken) : null;
        if (exige && info is null)
        {
            throw new AiAdminRefusedException(AiAdminRefusalKind.Conflict, "credential-required", "Cadastre a credencial deste provedor antes de escolhê-lo como padrão.");
        }

        var agora = _clock.GetUtcNow();
        var marcador = Marker(exige, info);
        var conectividade = ToView(_diagnostics.Get(tenant, providerId, modelo.ModelId, AiDiagnosticKind.Connectivity), marcador, agora);
        var geracao = ToView(_diagnostics.Get(tenant, providerId, modelo.ModelId, AiDiagnosticKind.Generation), marcador, agora);

        if (conectividade is { Stale: false, ConversationCapable: false })
        {
            throw new AiAdminRefusedException(AiAdminRefusalKind.Validation, "model-not-conversational", "O último diagnóstico indica que este modelo não serve à conversa.");
        }

        var avisos = new List<string>();
        if (conectividade is null || conectividade.Stale || conectividade.State != nameof(AiConnectivityState.Verified))
        {
            avisos.Add(conectividade is { Stale: false, State: nameof(AiConnectivityState.ModelUnavailable) } ? "model-unavailable-per-last-check" : "connectivity-not-verified");
        }

        if (geracao is null || geracao.Stale || geracao.State != "Succeeded")
        {
            avisos.Add("generation-not-tested");
        }

        var atual = await _store.FindSettingsAsync(cancellationToken);
        var situacao = atual?.Status is AiTenantStatus.Disabled ? AiTenantStatus.Disabled : AiTenantStatus.Enabled;
        await _configuration.SetConfigurationAsync(providerId, modelo.ModelId, situacao, cancellationToken);
        return new AiDefaultSelectionResult(avisos);
    }

    // Credenciais

    public async Task SetCredentialAsync(string providerId, string secret, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireAsync(contexto, Permission.AiCredentialManage, "ai-gateway.credential.set", cancellationToken);
        var provedor = _selector.Providers.Find(providerId) ?? throw new AiAdminRefusedException(AiAdminRefusalKind.Validation, "provider-not-implemented", "O provedor não está disponível neste ambiente.");
        if (!provedor.RequiresCredential)
        {
            throw new AiAdminRefusedException(AiAdminRefusalKind.Validation, "credential-not-used", "Este provedor não usa credencial do tenant.");
        }

        await _configuration.SetCredentialAsync(providerId, secret, cancellationToken);

        // Credencial nova: os diagnósticos feitos com a anterior não valem mais.
        _diagnostics.ClearProvider(contexto.TenantId.Value, providerId);
    }

    /// <summary>
    /// Remove a credencial (apaga o texto cifrado). SEMPRE permitido, mesmo com o provedor sendo o padrão (a revogação por segurança nunca é bloqueada): o aviso é da interface, e o atendimento falha fechado (a IA não responde e
    /// o cliente vai ao atendente pelos mecanismos existentes), sem fallback para outro provedor. A resposta diz se era o provedor padrão.
    /// </summary>
    public async Task<AiCredentialRevocationResult> RevokeCredentialAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireAsync(contexto, Permission.AiCredentialManage, "ai-gateway.credential.revoke", cancellationToken);
        var padrao = await _store.FindSettingsAsync(cancellationToken);
        var revogada = await _configuration.RevokeCredentialAsync(providerId, cancellationToken);
        _diagnostics.ClearProvider(contexto.TenantId.Value, providerId);
        return new AiCredentialRevocationResult(revogada, padrao is not null && padrao.ProviderId == providerId);
    }

    // Diagnósticos

    /// <summary>
    /// O teste de CONECTIVIDADE: o adaptador do provedor verifica acessibilidade, autenticação e o modelo SEM gerar texto (documentação oficial de cada provedor). Provedor sem verificação própria responde "não suportado"
    /// e NADA é verificado: a geração nunca a substitui. Não altera o padrão, não registra consumo, tem tempo limite, cancelamento e proteção contra repetição. Um resultado obtido com uma credencial que foi trocada ou
    /// removida DURANTE o teste não é guardado como validado.
    /// </summary>
    public async Task<AiConnectivityTestView> TestConnectivityAsync(string providerId, string? modelId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireAsync(contexto, Permission.AiConnectivityTest, "ai-gateway.connectivity.test", cancellationToken);
        var tenant = contexto.TenantId;
        var (provedor, modelo) = ResolveModel(providerId, modelId, requireModel: false);
        await EnsureOperationalAsync(tenant, cancellationToken);
        using var vaga = _throttle.Enter(tenant.Value, AiDiagnosticKind.Connectivity, providerId);

        var recurso = AiAudit.Id(providerId, modelo.ModelId);
        if (provedor is not IAiProviderDiagnostics diagnostico)
        {
            await _audit.WriteAsync(new AuditEntry("ai-gateway.connectivity.test", "ai-gateway.provider", recurso, AuditOutcome.Failed), cancellationToken);
            return new AiConnectivityTestView(providerId, modelo.ModelId, Supported: false, null, Superseded: false);
        }

        var (credencial, marcador) = await ResolveCredentialAsync(tenant, providerId, provedor, cancellationToken);
        AiConnectivityResult resultado;
        try
        {
            resultado = await diagnostico.CheckConnectivityAsync(new AiConnectivityRequest(modelo.ModelId, credencial, ConnectivityTimeout), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Um adaptador que lança em vez de devolver o resultado: inconclusivo, sem o texto da exceção (pode carregar endereço ou trechos do pedido).
            resultado = new AiConnectivityResult(AiConnectivityState.Inconclusive, null, null, null, null, "adapter-error", TimeSpan.Zero);
        }

        var superado = await IsSupersededAsync(provedor, providerId, marcador, cancellationToken);
        var entrada = new AiDiagnosticEntry(
            AiDiagnosticKind.Connectivity, providerId, modelo.ModelId, _clock.GetUtcNow(), resultado.State.ToString(), resultado.Code, marcador, (long)resultado.Latency.TotalMilliseconds,
            resultado.Reachable, resultado.Authenticated, resultado.ModelAvailable, resultado.ConversationCapable, null, null, null, null);
        if (!superado)
        {
            _diagnostics.Put(tenant.Value, entrada);
        }

        await _audit.WriteAsync(
            new AuditEntry("ai-gateway.connectivity.test", "ai-gateway.provider", recurso, resultado.State == AiConnectivityState.Verified ? AuditOutcome.Success : AuditOutcome.Failed), cancellationToken);
        return new AiConnectivityTestView(providerId, modelo.ModelId, Supported: resultado.State != AiConnectivityState.NotSupported, ToView(entrada, marcador, _clock.GetUtcNow()), superado);
    }

    /// <summary>
    /// O teste de GERAÇÃO de IA: mensagem sintética FIXA do sistema, provedor e modelo do catálogo escolhidos explicitamente, passando pelo AiGateway (suspensão, limites reduzidos, credencial, ledger). Exige confirmação
    /// explícita (pode gerar cobrança no provedor comercial). Não altera o padrão, não usa conversa, canal, fluxo nem ferramenta, e nunca faz fallback. O consumo, quando há chamada, é REAL e fica no ledger.
    /// </summary>
    public async Task<AiGenerationTestView> TestGenerationAsync(string providerId, string modelId, bool confirmed, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireAsync(contexto, Permission.AiGenerationTest, "ai-gateway.generation.test", cancellationToken);
        var tenant = contexto.TenantId;
        if (!confirmed)
        {
            throw new AiAdminRefusedException(AiAdminRefusalKind.Validation, "confirmation-required", "Confirme que entende que o teste de geração pode gerar cobrança no provedor.");
        }

        var (provedor, modelo) = ResolveModel(providerId, modelId, requireModel: true);
        await EnsureOperationalAsync(tenant, cancellationToken);
        var (_, marcador) = await ResolveCredentialAsync(tenant, providerId, provedor, cancellationToken, needSecret: false);
        using var vaga = _throttle.Enter(tenant.Value, AiDiagnosticKind.Generation, providerId);

        var resultado = await _gateway.GenerateDiagnosticAsync(providerId, modelo.ModelId, cancellationToken);

        // Falha ANTES de chamar o provedor (limite, suspensão, configuração): não testou o provedor, então não vira "falha do provedor" guardada.
        var chamou = resultado.IsSuccess || resultado.Failure is { Kind: not (AiErrorKind.NotConfigured or AiErrorKind.TenantLimitExceeded) };
        var estado = resultado.IsSuccess ? "Succeeded" : chamou ? "Failed" : "NotExecuted";
        var superado = await IsSupersededAsync(provedor, providerId, marcador, cancellationToken);
        var previa = resultado.Completion?.Text is { } texto ? (texto.Length > 80 ? texto[..80] : texto) : null;
        var codigo = resultado.IsSuccess ? null : resultado.Failure?.Code ?? resultado.Failure?.Kind.ToString();
        var entrada = new AiDiagnosticEntry(
            AiDiagnosticKind.Generation, providerId, modelo.ModelId, _clock.GetUtcNow(), estado, codigo, marcador, (long)resultado.Latency.TotalMilliseconds, null, null, null, null,
            resultado.IsSuccess ? resultado.Usage.InputTokens : null, resultado.IsSuccess ? resultado.Usage.OutputTokens : null, resultado.IsSuccess ? nameof(AiUsageCertainty.Reported) : null, previa);
        if (chamou && !superado)
        {
            _diagnostics.Put(tenant.Value, entrada);
        }

        await _audit.WriteAsync(
            new AuditEntry("ai-gateway.generation.test", "ai-gateway.provider", AiAudit.Id(providerId, modelo.ModelId), resultado.IsSuccess ? AuditOutcome.Success : AuditOutcome.Failed), cancellationToken);
        return new AiGenerationTestView(providerId, modelo.ModelId, resultado.IsSuccess, ToView(entrada, marcador, _clock.GetUtcNow())!, resultado.AiRequestId, superado);
    }

    // Auxiliares
    /// <summary>
    /// A autorização de cada operação, com a RECUSA auditada (o padrão dos demais módulos): quem não tem a permissão recebe o 403 e fica uma entrada <c>Denied</c> (operação e tipo de recurso, sem provedor, modelo nem dado).
    /// </summary>
    private async Task RequireAsync(RequestContext contexto, Permission permissao, string operacao, CancellationToken cancellationToken)
    {
        if (AiAccess.IsAllowed(contexto, permissao))
        {
            return;
        }

        await _audit.WriteAsync(new AuditEntry(operacao, "ai-gateway.provider", null, AuditOutcome.Denied), cancellationToken);
        AiAccess.Require(contexto, permissao);
    }


    private (IAiProvider Provider, AiModelDescriptor Model) ResolveModel(string providerId, string? modelId, bool requireModel)
    {
        var provedor = _selector.Providers.Find(providerId);
        if (provedor is null || !_selector.Catalog.ProviderIds.Contains(providerId, StringComparer.Ordinal))
        {
            throw new AiAdminRefusedException(AiAdminRefusalKind.Validation, "provider-not-implemented", "O provedor não está disponível neste ambiente.");
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            if (requireModel)
            {
                throw new AiAdminRefusedException(AiAdminRefusalKind.Validation, "model-required", "Escolha um modelo.");
            }

            // Sem modelo: o primeiro modelo ativo do provedor (determinístico), nunca um identificador livre.
            var primeiro = _selector.Catalog.ActiveModelsOf(providerId).FirstOrDefault()
                ?? throw new AiAdminRefusedException(AiAdminRefusalKind.Validation, "model-not-authorized", "O provedor não tem modelo autorizado.");
            return (provedor, primeiro);
        }

        var modelo = _selector.Catalog.Find(providerId, modelId);
        if (modelo is null || modelo.Status != AiModelStatus.Active)
        {
            throw new AiAdminRefusedException(AiAdminRefusalKind.Validation, "model-not-authorized", "O modelo não está autorizado para este provedor.");
        }

        return (provedor, modelo);
    }

    private async Task EnsureOperationalAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        if (_gate is not null && !(await _gate.GetAsync(tenant, cancellationToken)).IsOperational)
        {
            throw new AiAdminRefusedException(AiAdminRefusalKind.Conflict, "tenant-suspended", "A empresa está suspensa: o teste não pode ser executado.");
        }
    }

    private async Task<(AiCredential Credential, string Marker)> ResolveCredentialAsync(TenantId tenant, string providerId, IAiProvider provider, CancellationToken cancellationToken, bool needSecret = true)
    {
        if (!provider.RequiresCredential)
        {
            return (AiCredential.None, "none");
        }

        var info = await _store.ReadCredentialInfoAsync(providerId, cancellationToken)
            ?? throw new AiAdminRefusedException(AiAdminRefusalKind.Conflict, "credential-required", "Cadastre a credencial deste provedor antes de testar.");
        if (!needSecret)
        {
            return (AiCredential.None, Marker(true, info));
        }

        AiCredential? credencial;
        try
        {
            credencial = await _credentials.ResolveAsync(tenant, providerId, cancellationToken);
        }
        catch (AiSecretsUnavailableException)
        {
            throw new AiAdminRefusedException(AiAdminRefusalKind.Conflict, "secrets-unavailable", "O armazenamento seguro de credenciais está indisponível.");
        }

        return credencial is null || string.IsNullOrWhiteSpace(credencial.Secret)
            ? throw new AiAdminRefusedException(AiAdminRefusalKind.Conflict, "credential-required", "Cadastre a credencial deste provedor antes de testar.")
            : (credencial, Marker(true, info));
    }

    /// <summary>A credencial mudou (ou foi removida) desde o início do teste? Lê SEM rastreamento, para ver o que outra operação gravou.</summary>
    private async Task<bool> IsSupersededAsync(IAiProvider provider, string providerId, string markerAtStart, CancellationToken cancellationToken)
    {
        if (!provider.RequiresCredential)
        {
            return false;
        }

        var agora = Marker(true, await _store.ReadCredentialInfoAsync(providerId, cancellationToken));
        return agora != markerAtStart;
    }

    private static string Marker(bool requiresCredential, AiCredentialInfo? info) => !requiresCredential ? "none" : info?.Fingerprint ?? "absent";

    private AiDiagnosticView? ToView(AiDiagnosticEntry? entry, string currentMarker, DateTimeOffset now)
    {
        if (entry is null)
        {
            return null;
        }

        string? motivo = entry.CredentialMarker != currentMarker ? "credential-changed" : now - entry.At > DiagnosticFreshness ? "age" : null;
        return new AiDiagnosticView(
            entry.State, entry.Code, entry.At, motivo is not null, motivo, entry.LatencyMs, entry.Reachable, entry.Authenticated, entry.ModelAvailable, entry.ConversationCapable,
            entry.InputTokens, entry.OutputTokens, entry.UsageCertainty, entry.ReplyPreview);
    }
}
