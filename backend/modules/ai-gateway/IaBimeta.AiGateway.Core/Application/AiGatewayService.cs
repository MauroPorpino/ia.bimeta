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
/// tenant, desabilitada ou bloqueada, com origem de credencial não suportada (<see cref="AiCredentialSource.PlatformManaged"/>), com modelo fora do catálogo
/// ou desativado, ou sem adaptador, termina em <see cref="AiErrorKind.NotConfigured"/>;</item>
/// <item>aplica o limite de mensagens por turno e o teto de entrada, ESTIMA a entrada (heurística com margem), limita o teto de saída ao do tenant e ao do modelo, recusa
/// o que não cabe na janela do modelo ou no teto de tokens por chamada, e limita o tempo limite ao máximo configurado;</item>
/// <item>resolve a credencial do tenant (falha fechada sem ela, e sem a chave mestra), pede a vaga com a RESERVA de tokens à contabilidade (simultaneidade, chamadas e tokens
/// do período; contabilidade fora do ar é falha transitória e o provedor NÃO é chamado) e chama UM adaptador: o do provedor do tenant, sem fallback para outro;</item>
/// <item>normaliza o consumo, estima o custo, aplica a política de erro e conclui a vaga com o consumo CONFIRMADO ou INDETERMINADO (sem prompt e sem resposta).</item>
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
    private readonly ITenantOperationGate? _gate;

    public AiGatewayService(
        IRequestContextAccessor context,
        ITenantAiConfigurationReader configurations,
        AiModelSelector selector,
        IAiCredentialResolver credentials,
        IAiUsageLedger ledger,
        TimeProvider clock,
        AiGatewayOptions? options = null,
        ITenantOperationGate? gate = null)
    {
        _context = context;
        _configurations = configurations;
        _selector = selector;
        _credentials = credentials;
        _ledger = ledger;
        _clock = clock;
        _options = options ?? new AiGatewayOptions();
        _options.EnsureValid();
        _gate = gate;
    }

    public async Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);
        AiGenerationRequestRules.Validate(request);

        var tenant = contexto.TenantId;
        var aiRequestId = Guid.CreateVersion7();

        // Empresa suspensa (ou em provisionamento): nenhuma operação com custo de IA começa. Falha fechada e SEM tocar o provedor, a credencial nem o ledger.
        if (_gate is not null && !(await _gate.GetAsync(tenant, cancellationToken)).IsOperational)
        {
            return Fail(aiRequestId, null, AiErrorKind.NotConfigured, AiFailureCodes.TenantSuspended);
        }

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

        // O provedor não limita a entrada: o teto de caracteres, a janela do modelo e o teto por chamada do tenant são o que a limita. A estimativa é uma heurística com margem de
        // segurança, usada para a RESERVA de orçamento; nunca é apresentada como o consumo real, que o provedor informa.
        if (_options.MaxInputCharacters is { } tetoDeEntrada && AiTokenEstimator.CountInputCharacters(request) > tetoDeEntrada)
        {
            return Fail(aiRequestId, modelo, AiErrorKind.TenantLimitExceeded, AiFailureCodes.InputTooLarge);
        }

        var entradaEstimada = AiTokenEstimator.EstimateInputTokens(request, _options);
        var tetoDeSaida = Math.Min(request.MaxOutputTokens, Math.Min(limites.MaxOutputTokens, modelo.MaxOutputTokens));
        var reserva = entradaEstimada + tetoDeSaida;
        if (reserva > modelo.ContextWindowTokens)
        {
            return Fail(aiRequestId, modelo, AiErrorKind.ContextTooLong, AiFailureCodes.ContextWindowExceeded);
        }

        if (limites.MaxTokensPerCall is { } tetoPorChamada && reserva > tetoPorChamada)
        {
            return Fail(aiRequestId, modelo, AiErrorKind.TenantLimitExceeded, AiFailureCodes.PerCallTokenLimit);
        }

        AiCredential? credencial;
        try
        {
            credencial = await _credentials.ResolveAsync(tenant, modelo.ProviderId, cancellationToken);
        }
        catch (AiSecretsUnavailableException)
        {
            // A chave mestra não está disponível: falha da PLATAFORMA (não do tenant), fechada, e nada do conteúdo da chave é repetido.
            return Fail(aiRequestId, modelo, AiErrorKind.NotConfigured, AiFailureCodes.SecretsUnavailable);
        }

        if (credencial is null || string.IsNullOrWhiteSpace(credencial.Secret))
        {
            return Fail(aiRequestId, modelo, AiErrorKind.InvalidCredentials, AiFailureCodes.CredentialMissing);
        }

        var tempoLimite = request.Timeout > _options.MaxCallTimeout ? _options.MaxCallTimeout : request.Timeout;
        var inicioDaVaga = _clock.GetUtcNow();
        AiLeaseResult concessao;
        try
        {
            concessao = await _ledger.TryBeginAsync(
                new AiLeaseRequest(tenant, aiRequestId, request.CorrelationId, modelo.ProviderId, modelo.ModelId, limites, inicioDaVaga, entradaEstimada, tetoDeSaida, tempoLimite + _options.LeaseMargin),
                cancellationToken);
        }
        catch (AiLedgerUnavailableException)
        {
            // Sem contabilidade não há como saber o que o tenant já gastou: falha FECHADA, o provedor NÃO é chamado, e o consumidor trata como transitória.
            return Fail(aiRequestId, modelo, AiErrorKind.Transient, AiFailureCodes.LedgerUnavailable);
        }

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

        // A partir daqui a vaga está ocupada e PRECISA ser concluída, com sucesso, falha ou cancelamento. O teto de saída já limitado vai NA requisição ao provedor: o adaptador
        // o repassa sempre (o contrato de teste dos adaptadores confere), e é ele que limita a saída de fato.
        var pedido = new AiCompletionRequest(
            aiRequestId,
            request.CorrelationId,
            modelo.ModelId,
            modelo.Profile,
            request.SystemInstruction,
            request.Messages.ToArray(),
            tetoDeSaida,
            tempoLimite,
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
            // O chamador desistiu: o consumo é DESCONHECIDO (a chamada pode ter sido enviada e faturada), então fica indeterminado e cobra a reserva. A desistência segue como exceção.
            var cancelado = new AiRequestRecord(
                tenant, aiRequestId, request.CorrelationId, modelo.ProviderId, modelo.ModelId, AiUsage.None, ZeroCost(modelo), _clock.GetElapsedTime(inicio),
                AiRequestOutcome.Cancelled, null, null, null, null, _clock.GetUtcNow(), AiUsageCertainty.Unknown);
            await TryCompleteAsync(vaga, cancelado);
            throw;
        }
        catch (OperationCanceledException)
        {
            // O tempo limite esgotou: falha transitória, retentável pelo consumidor, e consumo DESCONHECIDO (a requisição pode ter sido processada e cobrada).
            bruto = AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, null, AiFailureCodes.Timeout), _clock.GetElapsedTime(inicio));
        }
        catch (Exception)
        {
            // Um adaptador que lança em vez de devolver a falha normalizada é tratado como falha transitória (a mensagem da exceção não é usada:
            // ela pode conter trechos do pedido), com consumo DESCONHECIDO. O consumidor limita as tentativas.
            bruto = AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, null, AiFailureCodes.ProviderException), _clock.GetElapsedTime(inicio));
        }

        // O adaptador devolve ou a resposta ou a falha. Qualquer outra combinação é defeito do adaptador, e falha fechada: nunca vira sucesso, e o consumo fica desconhecido.
        if (bruto is null || (bruto.Completion is null) == (bruto.Error is null))
        {
            bruto = AiCompletionResult.Failure(new AiError(AiErrorKind.InvalidRequest, null, AiFailureCodes.ProviderResultInvalid), _clock.GetElapsedTime(inicio));
        }

        var consumo = AiUsageNormalizer.Normalize(bruto.Usage);
        var certeza = NormalizeCertainty(bruto, consumo);
        var custo = AiCostEstimator.Estimate(consumo, modelo.Prices);
        var latencia = bruto.Latency < TimeSpan.Zero ? TimeSpan.Zero : bruto.Latency;
        var identificadorNoProvedor = AiErrorPolicy.SanitizeCode(bruto.ProviderRequestId);
        var falha = bruto.Error is null ? null : AiErrorPolicy.Apply(bruto.Error, _options.MaxRetryAfter);

        var registro = new AiRequestRecord(
            tenant, aiRequestId, request.CorrelationId, modelo.ProviderId, modelo.ModelId, consumo, custo, latencia,
            falha is null ? AiRequestOutcome.Succeeded : AiRequestOutcome.Failed,
            bruto.Completion?.StopReason, falha?.Kind, falha?.Code, identificadorNoProvedor, _clock.GetUtcNow(), certeza);
        await TryCompleteAsync(vaga, registro);

        return new AiGenerationResult(
            aiRequestId, modelo.ProviderId, modelo.ModelId, bruto.Completion, falha, consumo, custo, latencia, identificadorNoProvedor);
    }

    /// <summary>
    /// A certeza do consumo que vale para a contabilidade. Uma resposta SEMPRE tem consumo informado (<see cref="AiUsageCertainty.Reported"/>). Uma falha vale o que o adaptador declarou,
    /// mas nunca menos do que o consumo que ele próprio informou: <see cref="AiUsageCertainty.NoneIncurred"/> com tokens informados é incoerente e fica <see cref="AiUsageCertainty.Reported"/>
    /// (o lado seguro), e um valor fora do enum fica <see cref="AiUsageCertainty.Unknown"/>.
    /// </summary>
    private static AiUsageCertainty NormalizeCertainty(AiCompletionResult resultado, AiUsage consumo)
    {
        if (resultado.Completion is not null)
        {
            return AiUsageCertainty.Reported;
        }

        return resultado.UsageCertainty switch
        {
            AiUsageCertainty.Reported => AiUsageCertainty.Reported,
            AiUsageCertainty.NoneIncurred => consumo.InputTokens + consumo.OutputTokens > 0 ? AiUsageCertainty.Reported : AiUsageCertainty.NoneIncurred,
            _ => AiUsageCertainty.Unknown,
        };
    }

    /// <summary>
    /// Conclui a vaga. Se a contabilidade falhar DEPOIS da chamada ao provedor, a resposta (ou a falha) já obtida NÃO se perde: a linha da vaga continua aberta, expira e vira
    /// consumo indeterminado (cobra a reserva, que é o lado seguro), e uma conclusão posterior pode reconciliá-la. Só a falha de infraestrutura da contabilidade é absorvida aqui.
    /// </summary>
    private async Task TryCompleteAsync(AiLease vaga, AiRequestRecord registro)
    {
        try
        {
            await _ledger.CompleteAsync(vaga, registro, CancellationToken.None);
        }
        catch (AiLedgerUnavailableException)
        {
        }
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
