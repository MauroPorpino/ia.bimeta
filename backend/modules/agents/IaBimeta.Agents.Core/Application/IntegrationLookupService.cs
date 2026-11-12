using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using IaBimeta.Agents.Domain;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Capabilities.Contracts;

namespace IaBimeta.Agents.Application;

/// <summary>O desfecho de UMA consulta do agente ao Motor de Integração (ADR 0036). <see cref="Code"/> é curto e fixo (nunca texto do sistema externo nem valor de parâmetro).</summary>
public sealed record LookupOutcome(bool Succeeded, string Code, CapabilityDescriptor? Operation, string? OutputJson, long ElapsedMs);

/// <summary>
/// A consulta do agente a operações PÚBLICAS de leitura do Motor de Integração (ADR 0036, fase 1). Faz três coisas e nenhuma além delas: (1) monta o conjunto OFERECIDO ao modelo no turno (só publicada, habilitada,
/// autorizada para <c>Agent</c>, leitura, <c>DataClass = Public</c> e do escopo de organização da sessão: o motor já filtra, e aqui se confere de novo); (2) executa a consulta que o modelo pediu, depois de conferir de novo que
/// a chave foi oferecida, sempre como <c>Agent</c>, com a sessão, o <c>TurnId</c> e o escopo vindos do código confiável (nunca do modelo) e dentro de um ORÇAMENTO de tempo; (3) registra auditoria e métricas próprias, sem dado
/// pessoal. Qualquer falha vira um desfecho com código (o turno então transfere para um atendente); nada lança, a não ser o cancelamento do próprio turno. Roda no contexto de SISTEMA do tenant do turno.
/// </summary>
public sealed class IntegrationLookupService
{
    /// <summary>O teto do resultado que vai ao prompt: só campos mapeados, mas a lista pode ser grande.</summary>
    public const int MaxResultJsonLength = 8192;

    private static readonly Meter Meter = new("IaBimeta.Agents");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("agents.integration_lookup.attempts", description: "Consultas do agente ao motor de integração, por desfecho (sem dado pessoal).");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("agents.integration_lookup.duration_ms", "ms", "Duração da consulta do agente ao motor de integração.");

    private readonly ICapabilityCatalog _catalog;
    private readonly ICapabilityExecutor _executor;
    private readonly IAuditWriter _audit;
    private readonly AgentsOptions _options;

    public IntegrationLookupService(ICapabilityCatalog catalog, ICapabilityExecutor executor, IAuditWriter audit, AgentsOptions options)
    {
        _catalog = catalog;
        _executor = executor;
        _audit = audit;
        _options = options;
    }

    public bool Enabled => _options.IntegrationLookupsEnabled;

    /// <summary>
    /// As operações que o modelo pode pedir neste turno. Desligado, sem escopo de organização ou com qualquer falha do catálogo, devolve vazio (o turno segue pelo fluxo de sempre: oferecer consultas nunca pode derrubar o
    /// atendimento). Confere as condições de novo mesmo depois do filtro do motor (defesa em profundidade).
    /// </summary>
    public async Task<IReadOnlyList<CapabilityDescriptor>> ListOfferedAsync(Guid? organizationId, CancellationToken cancellationToken)
    {
        if (!_options.IntegrationLookupsEnabled || organizationId is null)
        {
            return [];
        }

        try
        {
            var catalogo = await _catalog.ListAsync(CapabilityCaller.Agent, cancellationToken, organizationId);
            return catalogo
                .Where(d => d.Effect == CapabilityEffect.Read && d.DataClass == CapabilityDataClass.Public)
                .OrderBy(d => d.Key, StringComparer.Ordinal)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Executa a consulta pedida pelo modelo. Nunca lança por falha (só o cancelamento do turno).</summary>
    public async Task<LookupOutcome> ExecuteAsync(
        Guid turnId,
        Guid sessionId,
        Guid? organizationId,
        AgentDecision decision,
        IReadOnlyList<CapabilityDescriptor> offered,
        CancellationToken cancellationToken)
    {
        var cronometro = Stopwatch.StartNew();
        var resultado = await RunAsync(turnId, sessionId, organizationId, decision, offered, cancellationToken);
        resultado = resultado with { ElapsedMs = cronometro.ElapsedMilliseconds };

        Attempts.Add(1, new KeyValuePair<string, object?>("outcome", resultado.Code));
        Duration.Record(resultado.ElapsedMs, new KeyValuePair<string, object?>("outcome", resultado.Code));
        await AuditAsync(turnId, resultado);
        return resultado;
    }

    private async Task<LookupOutcome> RunAsync(Guid turnId, Guid sessionId, Guid? organizationId, AgentDecision decision, IReadOnlyList<CapabilityDescriptor> offered, CancellationToken cancellationToken)
    {
        if (decision.Action != AgentAction.Lookup || decision.LookupKey is null || decision.LookupParameters is null)
        {
            return Fail("invalid-decision");
        }

        if (!_options.IntegrationLookupsEnabled)
        {
            return Fail("disabled");
        }

        if (organizationId is null)
        {
            return Fail("scope-required");
        }

        // A chave precisa ter sido OFERECIDA neste turno, e a operação ainda precisa ser de leitura e pública (o que foi oferecido não é confiado às cegas).
        var operacao = offered.FirstOrDefault(d => d.Key == decision.LookupKey);
        if (operacao is null || operacao.Effect != CapabilityEffect.Read || operacao.DataClass != CapabilityDataClass.Public)
        {
            return Fail("not-offered");
        }

        using var orcamento = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        orcamento.CancelAfter(_options.LookupBudget);

        CapabilityResult resposta;
        try
        {
            resposta = await _executor.ExecuteAsync(
                new CapabilityInvocation(operacao.Key, decision.LookupParameters, CapabilityCaller.Agent, null, sessionId, turnId.ToString(), organizationId),
                orcamento.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail("budget-exceeded", operacao);
        }
        catch (Exception)
        {
            // Defeito ou permissão do motor: a mensagem pode trazer dado; só o código sai.
            return Fail("exception", operacao);
        }

        if (resposta.Status != CapabilityExecutionStatus.Succeeded || resposta.Output is not { } saida)
        {
            return Fail(resposta.Error?.Code ?? ("status-" + resposta.Status.ToString().ToLowerInvariant()), operacao);
        }

        var json = saida.GetRawText();
        if (json.Length > MaxResultJsonLength)
        {
            return Fail("result-too-large", operacao);
        }

        return new LookupOutcome(true, "ok", operacao, json, 0);
    }

    private static LookupOutcome Fail(string code, CapabilityDescriptor? operation = null) => new(false, code, operation, null, 0);

    /// <summary>Auditoria da consulta (sucesso ou falha), com o TurnId como recurso: a trilha não tem campo livre, então nada de parâmetro, resposta ou texto de cliente. Melhor esforço: nunca derruba o turno.</summary>
    private async Task AuditAsync(Guid turnId, LookupOutcome resultado)
    {
        try
        {
            var codigo = resultado.Code.Length > 40 ? resultado.Code[..40] : resultado.Code;
            var seguro = new string(codigo.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
            await _audit.WriteAsync(
                new AuditEntry($"agents.integration-lookup.{seguro}", "ai-turn", turnId.ToString(), resultado.Succeeded ? AuditOutcome.Success : AuditOutcome.Failed),
                CancellationToken.None);
        }
        catch (Exception)
        {
            // A evidência durável também está no registro de execuções do motor.
        }
    }
}
