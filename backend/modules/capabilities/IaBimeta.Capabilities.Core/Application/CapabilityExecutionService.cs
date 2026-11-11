using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Audit.Contracts;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Application;

/// <summary>
/// O motor de execução (ADR 0035, §2 e §3): o ÚNICO caminho para uma operação externa. A ordem é fixa e falha fechada: estado e origem → permissão → parâmetros → idempotência → limites → registro ANTES da chamada →
/// confirmação (escrita) → conector → resultado. Escrita só repete com idempotência comprovada; o que ficou ambíguo vira <c>Unknown</c> e nunca é reenviado. O resultado nunca traz texto do sistema externo além dos campos
/// mapeados. Nenhum parâmetro, resposta ou segredo vai à trilha nem ao registro (a única exceção são os parâmetros de uma confirmação pendente, cifrados e apagados ao concluir).
/// </summary>
public sealed partial class CapabilityExecutionService : ICapabilityExecutor, ICapabilityCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CapabilitiesAuthorizer _auth;
    private readonly IDefinitionStore _definitions;
    private readonly IConnectionStore _connections;
    private readonly IExecutionStore _executions;
    private readonly ISecretProtector _protector;
    private readonly IConnectorRegistry _connectors;
    private readonly CapabilitiesOptions _options;
    private readonly TimeProvider _clock;

    internal CapabilityExecutionService(
        CapabilitiesAuthorizer auth,
        IDefinitionStore definitions,
        IConnectionStore connections,
        IExecutionStore executions,
        ISecretProtector protector,
        IConnectorRegistry connectors,
        CapabilitiesOptions options,
        TimeProvider clock)
    {
        _auth = auth;
        _definitions = definitions;
        _connections = connections;
        _executions = executions;
        _protector = protector;
        _connectors = connectors;
        _options = options;
        _clock = clock;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{8,100}$")]
    private static partial Regex IdempotencyKeyRegex();

    // ---------------------------------------------------------------- catálogo

    public async Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(CapabilityCaller caller, CancellationToken cancellationToken = default)
    {
        var contexto = _auth.Context;
        var conexoes = (await _connections.ListAsync(cancellationToken)).Where(c => c.Enabled).ToDictionary(c => c.Id);
        var lista = new List<CapabilityDescriptor>();
        foreach (var definicao in await _definitions.ListPublishedAsync(cancellationToken))
        {
            if (!definicao.Enabled || (definicao.AllowedCallers & caller) == 0 || !conexoes.TryGetValue(definicao.ConnectionId, out var conexao))
            {
                continue;
            }

            var permissao = definicao.Effect == CapabilityEffect.Write ? Permission.IntegrationExecuteWrite : Permission.IntegrationExecute;
            if (!contexto.IsAllowed(permissao, conexao.OrganizationId))
            {
                continue;
            }

            lista.Add(Describe(definicao));
        }

        return lista.OrderBy(d => d.Key, StringComparer.Ordinal).ToList();
    }

    public async Task<CapabilityDescriptor?> GetAsync(string key, CapabilityCaller caller, CancellationToken cancellationToken = default) =>
        (await ListAsync(caller, cancellationToken)).FirstOrDefault(d => d.Key == key);

    private static CapabilityDescriptor Describe(CapabilityDefinition d)
    {
        var spec = d.Spec;
        return new CapabilityDescriptor(
            d.Key,
            d.Version,
            d.Name,
            d.Description,
            d.Effect,
            d.RequiresConfirmation,
            spec.Parameters.Select(p => new CapabilityParameterInfo(p.Name, p.Type, p.Required && p.Default is null, p.Description, p.EnumValues)).ToList(),
            spec.ResponseRoot is null ? spec.Response.Select(f => f.Name).ToList() : ["items", "count", "truncated"]);
    }

    // ---------------------------------------------------------------- execução

    public async Task<CapabilityResult> ExecuteAsync(CapabilityInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (!_options.Enabled)
        {
            return await RejectAsync(null, "module-disabled", cancellationToken);
        }

        if (invocation.Parameters is null || invocation.Parameters.Count > 40 || !IsSingleCaller(invocation.Caller))
        {
            return await RejectAsync(null, "validation-failed", cancellationToken);
        }

        if (!DefinitionService.KeyRegex().IsMatch(invocation.Key ?? string.Empty))
        {
            return await RejectAsync(null, "not-found", cancellationToken);
        }

        var definicao = await _definitions.FindPublishedAsync(invocation.Key!, cancellationToken);
        if (definicao is null)
        {
            return await RejectAsync(null, "not-found", cancellationToken);
        }

        return await RunInvocationAsync(definicao, invocation.Parameters, invocation.Caller, invocation.IdempotencyKey, invocation.ServiceSessionId, isTest: false, cancellationToken);
    }

    /// <summary>Executa, como TESTE, uma operação de LEITURA (rascunho, publicada ou aposentada). Exige <c>IntegrationTest</c>; escrita nunca é executada em teste. O teste fica no histórico, marcado como teste.</summary>
    public async Task<CapabilityResult> TestOperationAsync(Guid definitionId, IReadOnlyDictionary<string, JsonElement> parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var definicao = await _definitions.FindAsync(definitionId, cancellationToken);
        if (definicao is null || parameters.Count > 40)
        {
            return await RejectAsync(null, definicao is null ? "not-found" : "validation-failed", cancellationToken);
        }

        return await RunInvocationAsync(definicao, parameters, CapabilityCaller.Operator, null, null, isTest: true, cancellationToken);
    }

    /// <summary>Só valida os parâmetros contra o molde (inclusive de escrita), sem executar nada: o administrador confere o esquema.</summary>
    public async Task<IReadOnlyList<CapabilityParameterProblem>?> ValidateParametersAsync(Guid definitionId, IReadOnlyDictionary<string, JsonElement> parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var definicao = await _definitions.FindAsync(definitionId, cancellationToken);
        if (definicao is null)
        {
            return null;
        }

        var conexao = await _connections.FindAsync(definicao.ConnectionId, cancellationToken);
        await _auth.RequireAsync(Permission.IntegrationTest, conexao?.OrganizationId, "capabilities.definition.validate", CapabilitiesAuthorizer.DefinitionResource, definitionId, cancellationToken);
        return ParameterValidator.Validate(definicao.Spec.Parameters, parameters).Problems;
    }

    private async Task<CapabilityResult> RunInvocationAsync(
        CapabilityDefinition definicao,
        IReadOnlyDictionary<string, JsonElement> parametros,
        CapabilityCaller origem,
        string? idempotencyKey,
        Guid? serviceSessionId,
        bool isTest,
        CancellationToken cancellationToken)
    {
        var contexto = _auth.Context;
        var conexao = await _connections.FindAsync(definicao.ConnectionId, cancellationToken);
        if (conexao is null)
        {
            return await RejectAsync(definicao, "disabled", cancellationToken);
        }

        // Estado: só a versão publicada e habilitada, de conexão habilitada, executa de verdade. O teste ignora o "habilitada" (é como se prepara) mas nunca a existência.
        if (!isTest && (definicao.Status != DefinitionStatus.Published || !definicao.Enabled || !conexao.Enabled))
        {
            return await RejectAsync(definicao, definicao.Status == DefinitionStatus.Published ? "disabled" : "not-found", cancellationToken);
        }

        if (!isTest && (definicao.AllowedCallers & origem) == 0)
        {
            return await RejectAsync(definicao, "caller-not-allowed", cancellationToken);
        }

        // "Operator" é uma PESSOA: sem usuário autenticado, a origem não vale (um job de sistema não se passa por operador).
        if (origem == CapabilityCaller.Operator && contexto.Principal is null)
        {
            return await RejectAsync(definicao, "caller-not-allowed", cancellationToken);
        }

        var escrita = definicao.Effect == CapabilityEffect.Write;
        if (isTest && escrita)
        {
            return await RejectAsync(definicao, "write-not-testable", cancellationToken);
        }

        // Permissão: o usuário (inclusive o do assistente interno) executa só o que a SUA concessão cobre; a conta técnica da integração nunca amplia isso. Um contexto de sistema (agente, fluxo) já foi limitado pelas origens permitidas.
        var permissao = isTest ? Permission.IntegrationTest : escrita ? Permission.IntegrationExecuteWrite : Permission.IntegrationExecute;
        await _auth.RequireAsync(permissao, conexao.OrganizationId, "capabilities.execute", CapabilitiesAuthorizer.ExecutionResource, definicao.Id, cancellationToken);

        var spec = definicao.Spec;
        var validacao = ParameterValidator.Validate(spec.Parameters, parametros);
        if (!validacao.IsValid)
        {
            return await RejectAsync(definicao, "validation-failed", cancellationToken, problems: validacao.Problems);
        }

        if (escrita && (idempotencyKey is null || !IdempotencyKeyRegex().IsMatch(idempotencyKey)))
        {
            return await RejectAsync(definicao, "idempotency-required", cancellationToken);
        }

        if (await IsOverLimitAsync(conexao, escrita, cancellationToken))
        {
            return await RejectAsync(definicao, "rate-limited", cancellationToken, retryable: true);
        }

        var agora = _clock.GetUtcNow();
        var impressao = escrita ? _protector.Fingerprint(Canonical(definicao, validacao.Values)) : null;
        var execucao = CapabilityExecution.Start(
            contexto.TenantId, definicao, conexao.OrganizationId, origem, contexto.Principal?.UserId, serviceSessionId, escrita ? idempotencyKey : null, impressao, isTest, agora);

        var aguardaConfirmacao = escrita && definicao.RequiresConfirmation;
        if (aguardaConfirmacao)
        {
            // Os parâmetros ficam cifrados, SÓ até a confirmação: sem chave configurada, nada é aceito (falha fechada).
            try
            {
                var protegido = _protector.Protect(JsonSerializer.Serialize(validacao.Values, Json), new SecretBinding("pending-params", contexto.TenantId, execucao.Id, string.Empty));
                execucao.AwaitConfirmation(protegido.Ciphertext, protegido.KeyId, agora.AddMinutes(_options.ConfirmationTtlMinutes));
            }
            catch (CapabilitiesUnavailableException)
            {
                return await RejectAsync(definicao, "secret-unavailable", cancellationToken);
            }
        }

        var (adicionada, registrada) = await _executions.TryAddAsync(execucao, cancellationToken);
        if (!adicionada)
        {
            return await ReplayAsync(definicao, registrada, impressao, cancellationToken);
        }

        if (aguardaConfirmacao)
        {
            await _auth.WriteAsync("capabilities.execution.request", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, AuditOutcome.Success, cancellationToken);
            return new CapabilityResult(execucao.Id, CapabilityExecutionStatus.AwaitingConfirmation, ConfirmationExpiresAt: execucao.ConfirmationExpiresAt);
        }

        return await RunAsync(execucao, definicao, conexao, validacao.Values, cancellationToken);
    }

    // ---------------------------------------------------------------- confirmação

    /// <summary>Confirma uma escrita pendente. Exige um USUÁRIO com <c>IntegrationExecuteWrite</c> sobre a organização da conexão; um contexto de sistema (agente, fluxo) nunca confirma.</summary>
    public async Task<CapabilityResult> ConfirmAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var execucao = await _executions.FindAsync(executionId, cancellationToken);
        if (execucao is null)
        {
            return await RejectAsync(null, "not-found", cancellationToken);
        }

        await _auth.RequireUserAsync(Permission.IntegrationExecuteWrite, execucao.OrganizationId, "capabilities.execution.confirm", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, cancellationToken);
        var usuario = _auth.Context.Principal!.UserId;

        if (execucao.Status != CapabilityExecutionStatus.AwaitingConfirmation)
        {
            return await RejectAsync(null, "not-pending", cancellationToken, executionId: execucao.Id);
        }

        var agora = _clock.GetUtcNow();
        if (execucao.ConfirmationExpiresAt is { } validade && agora > validade)
        {
            execucao.Cancel("confirmation-expired", agora);
            await TrySaveAsync(cancellationToken);
            await _auth.WriteAsync("capabilities.execution.confirm", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, AuditOutcome.Failed, cancellationToken);
            return new CapabilityResult(execucao.Id, CapabilityExecutionStatus.Rejected, Error: CapabilityRuntime.Error("confirmation-expired"));
        }

        // A versão pedida precisa continuar publicada e habilitada, e a conexão também: confirmar não ressuscita uma operação aposentada ou desligada.
        var definicao = await _definitions.FindAsync(execucao.DefinitionId, cancellationToken);
        var conexao = await _connections.FindAsync(execucao.ConnectionId, cancellationToken);
        if (definicao is null || conexao is null || definicao.Status != DefinitionStatus.Published || !definicao.Enabled || !conexao.Enabled || !_options.Enabled)
        {
            execucao.Cancel("definition-changed", agora);
            await TrySaveAsync(cancellationToken);
            await _auth.WriteAsync("capabilities.execution.confirm", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, AuditOutcome.Failed, cancellationToken);
            return new CapabilityResult(execucao.Id, CapabilityExecutionStatus.Rejected, Error: CapabilityRuntime.Error("definition-changed"));
        }

        IReadOnlyDictionary<string, object> valores;
        try
        {
            var texto = _protector.Unprotect(new ProtectedSecret(execucao.PendingKeyId ?? string.Empty, execucao.PendingCiphertext ?? string.Empty), new SecretBinding("pending-params", execucao.TenantId, execucao.Id, string.Empty));
            var bruto = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(texto, Json) ?? [];
            var revalidado = ParameterValidator.Validate(definicao.Spec.Parameters, bruto);
            if (!revalidado.IsValid)
            {
                throw new CapabilitiesUnavailableException("pending-invalid");
            }

            valores = revalidado.Values;
        }
        catch (Exception ex) when (ex is CapabilitiesUnavailableException or JsonException)
        {
            // Falha segura: parâmetros ilegíveis nunca viram chamada.
            execucao.Complete(CapabilityExecutionStatus.Failed, "secret-unavailable", null, 0, 0, agora);
            await TrySaveAsync(cancellationToken);
            await _auth.WriteAsync("capabilities.execution.confirm", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, AuditOutcome.Failed, cancellationToken);
            return new CapabilityResult(execucao.Id, CapabilityExecutionStatus.Failed, Error: CapabilityRuntime.Error("secret-unavailable"));
        }

        if (await IsOverLimitAsync(conexao, true, cancellationToken))
        {
            return await RejectAsync(definicao, "rate-limited", cancellationToken, retryable: true, executionId: execucao.Id);
        }

        try
        {
            execucao.Confirm(usuario, agora);
            await _executions.SaveAsync(cancellationToken);
        }
        catch (CapabilitiesConflictException)
        {
            // Outra confirmação (ou o cancelamento) chegou primeiro: só uma executa.
            return await RejectAsync(definicao, "not-pending", cancellationToken, executionId: execucao.Id);
        }

        await _auth.WriteAsync("capabilities.execution.confirm", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, AuditOutcome.Success, cancellationToken);
        return await RunAsync(execucao, definicao, conexao, valores, cancellationToken);
    }

    public async Task<CapabilityResult> CancelAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var execucao = await _executions.FindAsync(executionId, cancellationToken);
        if (execucao is null)
        {
            return await RejectAsync(null, "not-found", cancellationToken);
        }

        await _auth.RequireUserAsync(Permission.IntegrationExecuteWrite, execucao.OrganizationId, "capabilities.execution.cancel", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, cancellationToken);
        if (execucao.Status != CapabilityExecutionStatus.AwaitingConfirmation)
        {
            return await RejectAsync(null, "not-pending", cancellationToken, executionId: execucao.Id);
        }

        try
        {
            execucao.Cancel("cancelled", _clock.GetUtcNow());
            await _executions.SaveAsync(cancellationToken);
        }
        catch (CapabilitiesConflictException)
        {
            return await RejectAsync(null, "not-pending", cancellationToken, executionId: execucao.Id);
        }

        await _auth.WriteAsync("capabilities.execution.cancel", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, AuditOutcome.Success, cancellationToken);
        return new CapabilityResult(execucao.Id, CapabilityExecutionStatus.Cancelled);
    }

    // ---------------------------------------------------------------- chamada

    private async Task<CapabilityResult> RunAsync(
        CapabilityExecution execucao,
        CapabilityDefinition definicao,
        IntegrationConnection conexao,
        IReadOnlyDictionary<string, object> valores,
        CancellationToken cancellationToken)
    {
        var spec = definicao.Spec;
        var escrita = definicao.Effect == CapabilityEffect.Write;

        var conector = _connectors.Get(conexao.Kind);
        if (conector is null)
        {
            return await FinishAsync(execucao, CapabilityExecutionStatus.Failed, "connector-unavailable", false, null, 0, 0, null);
        }

        ConnectorSecret segredo;
        try
        {
            segredo = CapabilityRuntime.ReadSecret(_protector, conexao);
        }
        catch (CapabilitiesUnavailableException)
        {
            return await FinishAsync(execucao, CapabilityExecutionStatus.Failed, "secret-unavailable", false, null, 0, 0, null);
        }

        // Retentativa automática só para leitura ou escrita com idempotência COMPROVADA. Qualquer outra escrita sai uma única vez.
        var comprovada = spec.Idempotency == IdempotencySupport.Proven;
        var repete = !escrita || comprovada;
        var chamada = new ConnectorCall(CapabilityRuntime.ToConnector(conexao, segredo), spec, valores, escrita ? execucao.IdempotencyKey : null, repete, escrita, repete ? spec.MaxAttempts : 1);

        ConnectorResult resultado;
        try
        {
            resultado = await conector.InvokeAsync(chamada, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // O pedido foi cancelado no meio: numa escrita a chamada pode ter chegado ao destino.
            return escrita
                ? await FinishAsync(execucao, CapabilityExecutionStatus.Unknown, "outcome-unknown", false, null, 1, 0, null)
                : await FinishAsync(execucao, CapabilityExecutionStatus.Cancelled, "cancelled", false, null, 1, 0, null);
        }
        catch (Exception)
        {
            // Defeito do conector (nunca deveria lançar): a mensagem pode trazer destino ou segredo, então nada dela sai.
            return escrita
                ? await FinishAsync(execucao, CapabilityExecutionStatus.Unknown, "outcome-unknown", false, null, 1, 0, null)
                : await FinishAsync(execucao, CapabilityExecutionStatus.Failed, "internal-error", false, null, 1, 0, null);
        }

        var (status, codigo, retentavel, saida) = Interpret(escrita, comprovada, spec, resultado);
        return await FinishAsync(execucao, status, codigo, retentavel, saida, resultado.Attempts, resultado.ElapsedMs, resultado.UpstreamStatus);
    }

    private static (CapabilityExecutionStatus Status, string? Code, bool Retryable, JsonElement? Output) Interpret(bool escrita, bool comprovada, CapabilityOperationSpec spec, ConnectorResult r)
    {
        var ambiguo = (CapabilityExecutionStatus.Unknown, (string?)"outcome-unknown", false, (JsonElement?)null);

        switch (r.Outcome)
        {
            case ConnectorOutcome.Success:
            {
                if (spec.Response.Count == 0)
                {
                    return (CapabilityExecutionStatus.Succeeded, null, false, EmptyObject());
                }

                if (r.Body is { } corpo)
                {
                    var saida = ResponseMapper.Map(corpo, spec, out var invalida);
                    if (!invalida)
                    {
                        return (CapabilityExecutionStatus.Succeeded, null, false, saida);
                    }
                }

                // 2xx sem a resposta esperada: numa escrita não há confirmação inequívoca do sistema de origem.
                return escrita ? ambiguo : (CapabilityExecutionStatus.Failed, "response-invalid", false, null);
            }

            case ConnectorOutcome.Rejected:
                return (CapabilityExecutionStatus.Failed, "upstream-rejected", false, null);
            case ConnectorOutcome.AuthFailed:
                return (CapabilityExecutionStatus.Failed, "upstream-auth", false, null);
            case ConnectorOutcome.Unavailable:
                return escrita && !comprovada ? ambiguo : (CapabilityExecutionStatus.Failed, "unavailable", true, null);
            case ConnectorOutcome.Timeout:
                return escrita && r.MayHaveExecuted ? ambiguo : (CapabilityExecutionStatus.Failed, "timeout", true, null);
            case ConnectorOutcome.NetworkError:
                return escrita && r.MayHaveExecuted ? ambiguo : (CapabilityExecutionStatus.Failed, "unavailable", true, null);
            case ConnectorOutcome.Blocked:
                return (CapabilityExecutionStatus.Failed, "blocked-destination", false, null);
            case ConnectorOutcome.Redirected:
                return escrita ? ambiguo : (CapabilityExecutionStatus.Failed, "redirect-blocked", false, null);
            case ConnectorOutcome.TooLarge:
                return escrita ? ambiguo : (CapabilityExecutionStatus.Failed, "response-too-large", false, null);
            case ConnectorOutcome.InvalidResponse:
                return escrita ? ambiguo : (CapabilityExecutionStatus.Failed, "response-invalid", false, null);
            case ConnectorOutcome.SecretUnavailable:
                return (CapabilityExecutionStatus.Failed, "secret-unavailable", false, null);
            default:
                return escrita ? ambiguo : (CapabilityExecutionStatus.Failed, "internal-error", false, null);
        }
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Grava o desfecho (sem token de cancelamento: o que foi feito precisa ficar registrado mesmo que o pedido tenha sido cancelado) e audita.</summary>
    private async Task<CapabilityResult> FinishAsync(
        CapabilityExecution execucao,
        CapabilityExecutionStatus status,
        string? codigo,
        bool retentavel,
        JsonElement? saida,
        int tentativas,
        long duracaoMs,
        int? statusExterno)
    {
        execucao.Complete(status, codigo, statusExterno, tentativas, duracaoMs, _clock.GetUtcNow());
        await _executions.SaveAsync(CancellationToken.None);
        await _auth.WriteAsync("capabilities.execute", CapabilitiesAuthorizer.ExecutionResource, execucao.Id, status == CapabilityExecutionStatus.Succeeded ? AuditOutcome.Success : AuditOutcome.Failed, CancellationToken.None);

        return new CapabilityResult(
            execucao.Id,
            status,
            saida,
            codigo is null ? null : CapabilityRuntime.Error(codigo, retentavel, statusExterno));
    }

    // ---------------------------------------------------------------- idempotência

    private async Task<CapabilityResult> ReplayAsync(CapabilityDefinition definicao, CapabilityExecution existente, string? impressao, CancellationToken cancellationToken)
    {
        // A mesma chave com outros parâmetros (ou outra operação) é uso indevido: recusa, nunca "reaproveita" um resultado alheio.
        if (existente.DefinitionKey != definicao.Key || !string.Equals(existente.Fingerprint, impressao, StringComparison.Ordinal))
        {
            return await RejectAsync(definicao, "idempotency-conflict", cancellationToken);
        }

        var status = existente.Status;
        if (status == CapabilityExecutionStatus.InProgress && _clock.GetUtcNow() - existente.CreatedAt > TimeSpan.FromSeconds(_options.RunningLeaseSeconds))
        {
            // O processo que começou esta execução não terminou dentro do prazo: o destino pode ou não ter recebido. Ambíguo, e nunca reenviado.
            existente.Complete(CapabilityExecutionStatus.Unknown, "outcome-unknown", null, existente.Attempts, 0, _clock.GetUtcNow());
            await TrySaveAsync(cancellationToken);
            status = CapabilityExecutionStatus.Unknown;
        }

        return status switch
        {
            CapabilityExecutionStatus.InProgress => new CapabilityResult(existente.Id, status, Error: CapabilityRuntime.Error("in-progress"), Replayed: true),
            CapabilityExecutionStatus.AwaitingConfirmation => new CapabilityResult(existente.Id, status, Replayed: true, ConfirmationExpiresAt: existente.ConfirmationExpiresAt),
            CapabilityExecutionStatus.Succeeded => new CapabilityResult(existente.Id, status, Replayed: true),
            _ => new CapabilityResult(existente.Id, status, Error: existente.ErrorCode is null ? null : CapabilityRuntime.Error(existente.ErrorCode, false, existente.UpstreamStatus), Replayed: true),
        };
    }

    // ---------------------------------------------------------------- apoio

    private async Task<bool> IsOverLimitAsync(IntegrationConnection conexao, bool escrita, CancellationToken cancellationToken)
    {
        var agora = _clock.GetUtcNow();
        var minuto = agora.AddMinutes(-1);
        if (await _executions.CountSinceAsync(minuto, null, false, cancellationToken) >= _options.PerTenantPerMinute)
        {
            return true;
        }

        if (escrita && await _executions.CountSinceAsync(minuto, null, true, cancellationToken) >= _options.WritesPerTenantPerMinute)
        {
            return true;
        }

        if (await _executions.CountSinceAsync(minuto, conexao.Id, false, cancellationToken) >= _options.PerConnectionPerMinute)
        {
            return true;
        }

        return await _executions.CountInFlightAsync(conexao.Id, agora.AddSeconds(-_options.RunningLeaseSeconds), cancellationToken) >= _options.PerConnectionMaxInFlight;
    }

    /// <summary>O texto canônico dos parâmetros (chaves ordenadas), só para a impressão digital: nunca é guardado nem logado.</summary>
    private static string Canonical(CapabilityDefinition definicao, IReadOnlyDictionary<string, object> valores) =>
        $"{definicao.Key}|{definicao.Version}|{JsonSerializer.Serialize(new SortedDictionary<string, object>(valores.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal), Json)}";

    private static bool IsSingleCaller(CapabilityCaller caller)
    {
        var valor = (int)caller;
        return valor != 0 && (valor & (valor - 1)) == 0 && Enum.IsDefined(caller);
    }

    private async Task TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _executions.SaveAsync(cancellationToken);
        }
        catch (CapabilitiesConflictException)
        {
            // Outra operação mudou a execução primeiro: o estado dela vale.
        }
    }

    /// <summary>Recusa de negócio antes de chamar: volta como resultado (não exceção) e fica na trilha (sem valores).</summary>
    private async Task<CapabilityResult> RejectAsync(
        CapabilityDefinition? definicao,
        string codigo,
        CancellationToken cancellationToken,
        bool retryable = false,
        IReadOnlyList<CapabilityParameterProblem>? problems = null,
        Guid? executionId = null)
    {
        await _auth.WriteAsync("capabilities.execute", CapabilitiesAuthorizer.ExecutionResource, executionId ?? definicao?.Id, AuditOutcome.Failed, cancellationToken);
        return new CapabilityResult(executionId ?? Guid.Empty, CapabilityExecutionStatus.Rejected, Error: CapabilityRuntime.Error(codigo, retryable, null, problems));
    }
}
