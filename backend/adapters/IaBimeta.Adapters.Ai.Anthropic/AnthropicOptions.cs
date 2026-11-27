using System.Text.RegularExpressions;

namespace IaBimeta.Adapters.Ai.Anthropic;

/// <summary>
/// O PERFIL de um modelo no adaptador (ADR 0017, seção C.2): os parâmetros que sustentam a qualidade e a latência, escolhidos pelo nome do perfil que o catálogo do AiGateway dá ao modelo.
/// O contrato comum não tem <c>temperature</c>, <c>top_p</c> nem controle de raciocínio (e a API da Anthropic, na versão 2026-10-21, os recusa com HTTP 400): o esforço fica aqui.
/// </summary>
public sealed class AnthropicProfile
{
    /// <summary>
    /// O esforço (<c>output_config.effort</c>): <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> ou <c>max</c>. Nulo não envia o campo e vale o padrão do modelo. O raciocínio adaptativo
    /// fica no padrão do modelo (o adaptador nunca envia <c>thinking</c>: nem todo modelo aceita desligá-lo) e seus tokens contam em <c>max_tokens</c> e são cobrados como saída.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Marca o prompt de sistema como cacheável (<c>cache_control</c> efêmero de 5 minutos). DESLIGADO por padrão: a escrita no cache custa 1,25x a entrada e só compensa com prompt estático acima do
    /// mínimo cacheável do modelo e chamadas frequentes. Ligue só depois de medir.
    /// </summary>
    public bool CacheSystemPrompt { get; set; }
}

/// <summary>
/// A configuração do adaptador da Anthropic (seção <c>AiGateway:Providers:Anthropic</c>). Não tem segredo: a credencial é BYOK, do tenant, e chega a cada chamada pelo AiGateway, decifrada na hora.
/// Os valores padrão são os da documentação oficial lida em 27/11/2026 (docs/operations/ETAPA-03-ANTHROPIC.md).
/// </summary>
public sealed class AnthropicOptions
{
    /// <summary>O identificador estável do provedor no catálogo do AiGateway.</summary>
    public const string ProviderId = "anthropic";

    private static readonly Regex EffortPattern = new("^(low|medium|high|xhigh|max)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A base da API. Só HTTPS (exceto laço local, para teste): a credencial do tenant nunca vai em claro.</summary>
    public string BaseAddress { get; set; } = "https://api.anthropic.com/v1/";

    /// <summary>O valor do cabeçalho obrigatório <c>anthropic-version</c>. A mais recente documentada é <c>2026-10-21</c> (nela <c>temperature</c>, <c>top_p</c> e <c>top_k</c> são removidos).</summary>
    public string ApiVersion { get; set; } = "2026-10-21";

    /// <summary>O máximo de bytes que se lê de uma resposta: acima disso a resposta é inválida (proteção contra corpo gigante).</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;

    /// <summary>Quanto tempo a conexão pode esperar para abrir.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Quanto tempo uma conexão do pool vive (renova o DNS de um cliente que vive o tempo do processo).</summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Os perfis por nome (sem diferenciar maiúsculas). O padrão traz o perfil de atendimento rápido do catálogo de referência: esforço baixo, sem cache do prompt.</summary>
    public Dictionary<string, AnthropicProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["atendimento-rapido"] = new AnthropicProfile { Effort = "low" },
    };

    /// <summary>O endereço da Messages API, já resolvido.</summary>
    internal Uri MessagesUri => new(new Uri(BaseAddress, UriKind.Absolute), "messages");

    /// <exception cref="InvalidOperationException">Algum valor é inválido: falha na partida, nunca no meio do processamento.</exception>
    public void Validate()
    {
        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var endereco)
            || (endereco.Scheme != Uri.UriSchemeHttps && !(endereco.Scheme == Uri.UriSchemeHttp && endereco.IsLoopback))
            || !string.IsNullOrEmpty(endereco.UserInfo)
            || !string.IsNullOrEmpty(endereco.Query))
        {
            throw new InvalidOperationException("O endereço do provedor de IA precisa ser HTTPS absoluto, sem usuário nem consulta (HTTP só em laço local).");
        }

        if (!VersionPattern.IsMatch(ApiVersion ?? string.Empty))
        {
            throw new InvalidOperationException("A versão da API do provedor de IA precisa ter o formato AAAA-MM-DD.");
        }

        if (MaxResponseBytes is < 1024 or > 16_777_216 || ConnectTimeout <= TimeSpan.Zero || PooledConnectionLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("A configuração do adaptador de IA é inválida (tamanho máximo da resposta, tempo de conexão ou vida da conexão).");
        }

        foreach (var (nome, perfil) in Profiles)
        {
            if (string.IsNullOrWhiteSpace(nome) || perfil is null || (perfil.Effort is { } esforco && !EffortPattern.IsMatch(esforco)))
            {
                throw new InvalidOperationException("O perfil de modelo do adaptador de IA é inválido (nome vazio ou esforço fora de low, medium, high, xhigh, max).");
            }
        }
    }
}
