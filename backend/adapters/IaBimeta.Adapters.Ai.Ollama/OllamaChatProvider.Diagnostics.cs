using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.Adapters.Ai.Ollama;

/// <summary>
/// A verificação do servidor Ollama (Etapa 05), por <see cref="OllamaServerProbe"/> (<c>GET /api/version</c>, <c>GET /api/tags</c>, <c>POST /api/show</c>): nada é gerado, baixado ou instalado, e não há autenticação por tenant.
/// O resultado é DIAGNÓSTICO e instantâneo: "instalado" e "declara <c>completion</c>" NÃO são compatibilidade funcional validada. Quando <c>/api/show</c> falha, a capacidade é desconhecida (<c>Inconclusive</c>), nunca "incompatível".
/// </summary>
public sealed partial class OllamaChatProvider : IAiProviderDiagnostics
{
    public async Task<AiConnectivityResult> CheckConnectivityAsync(AiConnectivityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inicio = _clock.GetTimestamp();
        TimeSpan Decorrido() => _clock.GetElapsedTime(inicio);

        var sonda = new OllamaServerProbe(_http, _options);
        var situacao = await sonda.ProbeAsync([request.ModelId], request.Timeout > TimeSpan.Zero ? request.Timeout : TimeSpan.FromSeconds(10), cancellationToken);

        if (!situacao.Reachable)
        {
            return new AiConnectivityResult(AiConnectivityState.Unreachable, false, null, null, null, situacao.ErrorCode ?? "server-unavailable", Decorrido());
        }

        var modelo = situacao.Models.FirstOrDefault();
        if (situacao.ErrorCode is not null || modelo is null)
        {
            return new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, situacao.ErrorCode ?? "invalid-response", Decorrido());
        }

        if (!modelo.Installed)
        {
            return new AiConnectivityResult(AiConnectivityState.ModelUnavailable, true, null, false, null, "model-not-installed", Decorrido());
        }

        if (!modelo.ConversationCapabilityKnown)
        {
            return new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, true, null, "capability-unknown", Decorrido());
        }

        return modelo.Compatible
            ? new AiConnectivityResult(AiConnectivityState.Verified, true, null, true, true, "ok", Decorrido())
            : new AiConnectivityResult(AiConnectivityState.ModelUnavailable, true, null, true, false, "not-conversational", Decorrido());
    }
}
