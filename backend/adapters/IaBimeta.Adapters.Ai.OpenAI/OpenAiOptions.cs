using System.Text.RegularExpressions;

namespace IaBimeta.Adapters.Ai.OpenAI;

/// <summary>
/// O PERFIL de um modelo no adaptador (ADR 0017, seção C.2): os parâmetros que sustentam a qualidade e a latência, escolhidos pelo nome do perfil que o catálogo do AiGateway dá ao modelo. O contrato
/// comum não tem <c>temperature</c>, <c>top_p</c> nem controle de raciocínio: isso fica aqui. O armazenamento de estado da resposta (<c>store</c>) NÃO é configurável: está sempre desligado.
/// </summary>
public sealed class OpenAiProfile
{
    /// <summary>
    /// O esforço de raciocínio (<c>reasoning.effort</c>), por exemplo <c>none</c> (o recomendado para o atendimento curto: sem tokens de raciocínio, menos latência e custo), ou <c>low</c>. Nulo não envia o campo e
    /// vale o padrão do modelo. Os valores aceitos dependem do modelo: o catálogo escolhe o perfil certo para cada um.
    /// </summary>
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// A configuração do adaptador OpenAI (seção <c>AiGateway:Providers:OpenAi</c>). Não tem segredo: a credencial é BYOK, do tenant, e chega a cada chamada pelo AiGateway, decifrada na hora. Os valores padrão
/// são os da documentação oficial verificada em 03/11/2026 (ADR 0017, Anexo A.1).
/// </summary>
public sealed class OpenAiOptions
{
    /// <summary>O identificador estável do provedor no catálogo do AiGateway.</summary>
    public const string ProviderId = "openai";

    /// <summary>O mínimo de <c>max_output_tokens</c> que a Responses API aceita (Anexo A.1, item 2).</summary>
    public const int MinOutputTokens = 16;

    private static readonly Regex EffortPattern = new("^[a-z]{3,12}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A base da API. Só HTTPS (exceto laço local, para teste): a credencial do tenant nunca vai em claro.</summary>
    public string BaseAddress { get; set; } = "https://api.openai.com/v1/";

    /// <summary>O máximo de bytes que se lê de uma resposta: acima disso a resposta é inválida (proteção contra corpo gigante).</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;

    /// <summary>Quanto tempo a conexão pode esperar para abrir.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Quanto tempo uma conexão do pool vive (renova o DNS de um cliente que vive o tempo do processo).</summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Os perfis por nome (sem diferenciar maiúsculas). O padrão traz o perfil de atendimento rápido do catálogo de referência.</summary>
    public Dictionary<string, OpenAiProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["atendimento-rapido"] = new OpenAiProfile { ReasoningEffort = "none" },
    };

    /// <summary>O endereço da Responses API, já resolvido.</summary>
    internal Uri ResponsesUri => new(new Uri(BaseAddress, UriKind.Absolute), "responses");

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

        if (MaxResponseBytes is < 1024 or > 16_777_216 || ConnectTimeout <= TimeSpan.Zero || PooledConnectionLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("A configuração do adaptador de IA é inválida (tamanho máximo da resposta, tempo de conexão ou vida da conexão).");
        }

        foreach (var (nome, perfil) in Profiles)
        {
            if (string.IsNullOrWhiteSpace(nome) || perfil is null || (perfil.ReasoningEffort is { } esforco && !EffortPattern.IsMatch(esforco)))
            {
                throw new InvalidOperationException("O perfil de modelo do adaptador de IA é inválido (nome vazio ou esforço de raciocínio fora do formato).");
            }
        }
    }
}
