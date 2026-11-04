using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Application;

/// <summary>
/// O AiGateway (ADR 0017): gera a resposta de IA para o tenant do contexto confiável. Só um contexto de sistema chama
/// (<see cref="Permission.SystemOperation"/>), e o tenant NUNCA vem do pedido. A sequência é fixa:
/// <list type="number">
/// <item>valida a forma do pedido (nada é chamado se ela falhar);</item>
/// <item>lê a configuração do tenant e escolhe o modelo do catálogo e o adaptador (<see cref="AiModelSelector"/>): configuração ausente, de outro
/// tenant, com origem de credencial não suportada (<see cref="AiCredentialSource.PlatformManaged"/>), com modelo fora do catálogo ou desativado, ou sem
/// adaptador, termina em <see cref="AiErrorKind.NotConfigured"/>;</item>
/// <item>aplica o limite de mensagens por turno, limita o teto de saída ao do tenant e ao do modelo e o tempo limite ao máximo configurado;</item>
/// <item>resolve a credencial do tenant (falha fechada sem ela), pede a vaga à contabilidade (simultaneidade, chamadas e tokens do período) e chama UM
/// adaptador: o do provedor do tenant, sem fallback para outro;</item>
/// <item>normaliza o consumo, estima o custo, aplica a política de erro e registra o consumo (sem prompt e sem resposta), devolvendo a vaga SEMPRE.</item>
/// </list>
/// Pedido inválido, contexto de tenant ausente, acesso negado e cancelamento são exceções; tudo o que vem do provedor, dos limites e da configuração
/// volta em <see cref="AiGenerationResult.Failure"/>.
/// </summary>
public sealed class AiGatewayService : IAiGateway
{
    private readonly IRequestContextAccessor _context;
    private readonly ITenantAiConfigurationReader _configurations;
    private readonly AiModelSelector _selector;
    private readonly IAiCredentialResolver _credentials;
    private readonly IAiUsageLedger _ledger;
    private readonly TimeProvider _clock;
    private readonly AiGatewayOptions _options;

    public AiGatewayService(
        IRequestContextAccessor context,
        ITenantAiConfigurationReader configurations,
        AiModelSelector selector,
        IAiCredentialResolver credentials,
        IAiUsageLedger ledger,
        TimeProvider clock,
        AiGatewayOptions? options = null)
    {
        _context = context;
        _configurations = configurations;
        _selector = selector;
        _credentials = credentials;
        _ledger = ledger;
        _clock = clock;
        _options = options ?? new AiGatewayOptions();
        _options.EnsureValid();
    }

    public async Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);
        AiGenerationRequestRules.Validate(request);

        var tenant = contexto.TenantId;
        var aiRequestId = Guid.CreateVersion7();

        // A escolha depende só da configuração do TENANT do contexto: a de outro tenant, ou nenhuma, nunca é usada.
        var configuracao = await _configurations.GetAsync(tenant, cancellationToken);
        var selecao = _selector.Select(tenant, configuracao);
        if (!selecao.IsSelected)
        {
            return Fail(aiRequestId, null, AiErrorKind.NotConfigured, selecao.Code);
        }

        var modelo = selecao.Model!;
        var provedor = selecao.Provider!;
        var limites = selecao.Configuration!.Limits;

        if (request.Messages.Count > limites.MaxInputMessages)
        {
            return Fail(aiRequestId, modelo, AiErrorKind.TenantLimitExceeded, AiFailureCodes.InputMessageLimit);
        }

        var credencial = await _credentials.ResolveAsync(tenant, modelo.ProviderId, cancellationToken);
        if (credencial is null || string.IsNullOrWhiteSpace(credencial.Secret))
        {
            return Fail(aiRequestId, modelo, AiErrorKind.InvalidCredentials, AiFailureCodes.CredentialMissing);
        }

        var concessao = await _ledger.TryBeginAsync(new AiLeaseRequest(tenant, aiRequestId, limites, _clock.GetUtcNow()), cancellationToken);
        if (concessao.Lease is not { } vaga)
        {
            return Fail(aiRequestId, modelo, AiErrorKind.TenantLimitExceeded, concessao.Refusal switch
            {
                AiLimitRefusal.ConcurrentCalls => AiFailureCodes.ConcurrencyLimit,
                AiLimitRefusal.CallsPerPeriod => AiFailureCodes.PeriodCallLimit,
                AiLimitRefusal.TokensPerPeriod => AiFailureCodes.PeriodTokenLimit,
                _ => AiFailureCodes.LeaseDenied,
            });
        }

        // A partir daqui a vaga está ocupada e PRECISA ser devolvida, com sucesso, falha ou cancelamento.
        var pedido = new AiCompletionRequest(
            aiRequestId,
            request.CorrelationId,
            modelo.ModelId,
            modelo.Profile,
            request.SystemInstruction,
            request.Messages.ToArray(),
            Math.Min(request.MaxOutputTokens, Math.Min(limites.MaxOutputTokens, modelo.MaxOutputTokens)),
            request.Timeout > _options.MaxCallTimeout ? _options.MaxCallTimeout : request.Timeout,
            request.OutputMode,
            request.StructuredOutput,
            credencial);

        var inicio = _clock.GetTimestamp();
        AiCompletionResult bruto;
        try
        {
            using var limiteDeTempo = new CancellationTokenSource(pedido.Timeout, _clock);
            using var ligado = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limiteDeTempo.Token);
            bruto = await provedor.CompleteAsync(pedido, ligado.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // O chamador desistiu: a vaga volta, o consumo (nenhum conhecido) é registrado como cancelado e a desistência segue como exceção.
            var cancelado = new AiRequestRecord(
                tenant, aiRequestId, request.CorrelationId, modelo.ProviderId, modelo.ModelId, AiUsage.None, ZeroCost(modelo), _clock.GetElapsedTime(inicio),
                AiRequestOutcome.Cancelled, null, null, null, null, _clock.GetUtcNow());
            await _ledger.CompleteAsync(vaga, cancelado, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            // O tempo limite esgotou: falha transitória, retentável pelo consumidor.
            bruto = AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, null, AiFailureCodes.Timeout), _clock.GetElapsedTime(inicio));
        }
        catch (Exception)
        {
            // Um adaptador que lança em vez de devolver a falha normalizada é tratado como falha transitória (a mensagem da exceção não é usada:
            // ela pode conter trechos do pedido). O consumidor limita as tentativas.
            bruto = AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, null, AiFailureCodes.ProviderException), _clock.GetElapsedTime(inicio));
        }

        // O adaptador devolve ou a resposta ou a falha. Qualquer outra combinação é defeito do adaptador, e falha fechada: nunca vira sucesso.
        if (bruto is null || (bruto.Completion is null) == (bruto.Error is null))
        {
            bruto = AiCompletionResult.Failure(new AiError(AiErrorKind.InvalidRequest, null, AiFailureCodes.ProviderResultInvalid), _clock.GetElapsedTime(inicio));
        }

        var consumo = AiUsageNormalizer.Normalize(bruto.Usage);
        var custo = AiCostEstimator.Estimate(consumo, modelo.Prices);
        var latencia = bruto.Latency < TimeSpan.Zero ? TimeSpan.Zero : bruto.Latency;
        var identificadorNoProvedor = AiErrorPolicy.SanitizeCode(bruto.ProviderRequestId);
        var falha = bruto.Error is null ? null : AiErrorPolicy.Apply(bruto.Error, _options.MaxRetryAfter);

        var registro = new AiRequestRecord(
            tenant, aiRequestId, request.CorrelationId, modelo.ProviderId, modelo.ModelId, consumo, custo, latencia,
            falha is null ? AiRequestOutcome.Succeeded : AiRequestOutcome.Failed,
            bruto.Completion?.StopReason, falha?.Kind, falha?.Code, identificadorNoProvedor, _clock.GetUtcNow());
        await _ledger.CompleteAsync(vaga, registro, CancellationToken.None);

        return new AiGenerationResult(
            aiRequestId, modelo.ProviderId, modelo.ModelId, bruto.Completion, falha, consumo, custo, latencia, identificadorNoProvedor);
    }

    private static AiCostEstimate ZeroCost(AiModelDescriptor modelo) => new(0m, modelo.Prices.Currency);

    private static AiGenerationResult Fail(Guid aiRequestId, AiModelDescriptor? modelo, AiErrorKind kind, string? code) =>
        new(
            aiRequestId,
            modelo?.ProviderId,
            modelo?.ModelId,
            null,
            new AiFailure(kind, AiErrorPolicy.IsRetryable(kind), null, code),
            AiUsage.None,
            modelo is null ? null : ZeroCost(modelo),
            TimeSpan.Zero);
}
