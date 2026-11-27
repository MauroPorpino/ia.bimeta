using IaBimeta.Adapters.Ai.Ollama;
using IaBimeta.Agents.Domain;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;

namespace IaBimeta.Worker;

/// <summary>
/// Registra, na partida do Worker, o que ele enxerga da configuração de IA: quantos modelos há no catálogo (e quais), os adaptadores de provedor registrados, se a chave mestra das credenciais está
/// ativa e se os limites padrão estão completos. Nunca imprime segredo (nem o valor da chave mestra: só se há uma ativa) nem credencial de tenant. Existe porque o sintoma de uma configuração faltando no
/// Worker é discreto: toda chamada de IA termina em "não configurada", e o motivo está aqui, na partida, e não no turno.
/// </summary>
internal sealed class AiConfigurationReport : IHostedService
{
    private readonly AiModelCatalog _catalog;
    private readonly AiProviderRegistry _providers;
    private readonly AiSecretOptions _secrets;
    private readonly AiDefaultLimits _limits;
    private readonly AgentsOptions _agents;
    private readonly ILogger<AiConfigurationReport> _logger;
    private readonly OllamaServerProbe? _ollama;

    public AiConfigurationReport(AiModelCatalog catalog, AiProviderRegistry providers, AiSecretOptions secrets, AiDefaultLimits limits, AgentsOptions agents, ILogger<AiConfigurationReport> logger, OllamaServerProbe? ollama = null)
    {
        _ollama = ollama;
        _catalog = catalog;
        _providers = providers;
        _secrets = secrets;
        _limits = limits;
        _agents = agents;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var modelos = string.Join(", ", _catalog.Models.Select(m => _providers.Find(m.ProviderId) is null ? $"{m.ProviderId}/{m.ModelId} (SEM ADAPTADOR: indisponivel)" : $"{m.ProviderId}/{m.ModelId}"));
        var adaptadores = string.Join(", ", _providers.ProviderIds.Order(StringComparer.Ordinal));
        var chaveAtiva = !string.IsNullOrWhiteSpace(_secrets.ActiveKeyId) && _secrets.Keys.ContainsKey(_secrets.ActiveKeyId);
        var limitesCompletos = _limits.Merge(AiTenantLimitOverrides.None).IsValid;

        _logger.LogInformation(
            "Configuração de IA deste Worker: {ModelCount} modelos no catálogo ({Models}); adaptadores de provedor: {Providers}; chave mestra ativa: {MasterKey}; limites padrão completos: {Limits}.",
            _catalog.Models.Count, modelos, adaptadores.Length == 0 ? "nenhum" : adaptadores, chaveAtiva ? "sim" : "NÃO", limitesCompletos ? "sim" : "NÃO");

        if (_catalog.Models.Count == 0 || !limitesCompletos || !chaveAtiva || adaptadores.Length == 0)
        {
            _logger.LogWarning(
                "A configuração de IA deste Worker está incompleta: as chamadas ao modelo vão terminar em 'não configurada'. Confira o appsettings.json do Worker (catálogo e limites padrão) e AiGateway:Secrets (chave mestra, nos user-secrets ou no ambiente).");
        }

        // O registro das lacunas do FAQ (ADR 0030) exige a chave mestra do hash; sem ela fica desligado, em silêncio no turno, então a partida é o lugar de dizer isso. O valor da chave nunca é impresso.
        var lacunasLigadas = _agents.KnowledgeGapsEnabled && !string.IsNullOrWhiteSpace(_agents.KnowledgeGapHashKey);
        _logger.LogInformation(
            "Registro de lacunas do FAQ deste Worker: {Gaps} (chave do hash configurada: {Key}; Agents:Options:KnowledgeGapsEnabled: {Enabled}).",
            lacunasLigadas ? "LIGADO" : "desligado", string.IsNullOrWhiteSpace(_agents.KnowledgeGapHashKey) ? "NÃO" : "sim", _agents.KnowledgeGapsEnabled ? "sim" : "NÃO");

        await ReportOllamaAsync(cancellationToken);
    }

    /// <summary>
    /// Só quando o catálogo tem modelo ATIVO do Ollama (nenhum vem ativo de fábrica): sonda, em modo leitura e com tempo curto, se o servidor responde e se cada modelo está instalado e é compatível com conversa. Nunca baixa
    /// nem instala nada, nunca imprime o endereço, e a falha da sonda nunca impede a partida do Worker.
    /// </summary>
    private async Task ReportOllamaAsync(CancellationToken cancellationToken)
    {
        var ativos = _catalog.Models.Where(m => m.ProviderId == OllamaChatOptions.ProviderId && m.Status == AiModelStatus.Active).Select(m => m.ModelId).ToList();
        if (_ollama is null || ativos.Count == 0)
        {
            return;
        }

        try
        {
            var situacao = await _ollama.ProbeAsync(ativos, TimeSpan.FromSeconds(3), cancellationToken);
            if (!situacao.Reachable)
            {
                _logger.LogWarning("Ollama: servidor NÃO acessível ({Code}); os tenants que usam modelos do Ollama vão transferir ao atendente até ele voltar.", situacao.ErrorCode);
                return;
            }

            foreach (var modelo in situacao.Models)
            {
                _logger.LogInformation(
                    "Ollama: modelo {Model}: instalado {Installed}, compatível com conversa {Compatible}; versão do servidor {Version}.",
                    modelo.ModelId, modelo.Installed ? "sim" : "NÃO", modelo.Compatible ? "sim" : "NÃO", situacao.Version ?? "?");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ollama: a sondagem falhou ({Type}).", ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
