using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace IaBimeta.Adapters.Ai.Ollama;

/// <summary>
/// O PERFIL de um modelo no adaptador conversacional do Ollama: os parâmetros de geração escolhidos pelo nome do perfil que o catálogo do AiGateway dá ao modelo. O contrato comum não tem
/// <c>temperature</c> nem controle de raciocínio: eles ficam aqui, sob controle da infraestrutura, nunca do tenant.
/// </summary>
public sealed class OllamaChatProfile
{
    /// <summary>
    /// A janela de contexto pedida ao servidor (<c>options.num_ctx</c>). É o que o servidor realmente aloca: o <c>ContextWindowTokens</c> do catálogo para o modelo DEVE ser igual a este valor, para o
    /// AiGateway recusar antes de chamar uma entrada que o servidor truncaria em silêncio. Memória e latência crescem com ele.
    /// </summary>
    public int NumCtx { get; set; } = 8192;

    /// <summary>A temperatura (<c>options.temperature</c>). Zero é o padrão: o atendimento prefere respostas estáveis.</summary>
    public double Temperature { get; set; }

    /// <summary>
    /// O parâmetro <c>think</c>. Nulo NÃO envia o campo (vale o padrão do modelo): é o certo para modelo sem raciocínio, que pode recusar o parâmetro. Falso pede resposta sem raciocínio em modelo que o tem
    /// (a documentação diz que um modelo que não consegue desligar usa o nível mais baixo). O raciocínio devolvido nunca é lido nem repassado.
    /// </summary>
    public bool? Think { get; set; }
}

/// <summary>
/// A configuração do adaptador conversacional do Ollama (seção <c>AiGateway:Providers:Ollama</c>). O endereço é da INFRAESTRUTURA: nenhum tenant, usuário nem modelo de IA o informa. Sem segredo
/// no repositório: o token, se houver proxy autenticado na frente, vem do ambiente e vale para a instalação toda (não é credencial do tenant).
/// </summary>
public sealed class OllamaChatOptions
{
    /// <summary>O identificador estável do provedor no catálogo do AiGateway.</summary>
    public const string ProviderId = "ollama";

    private static readonly Regex KeepAlivePattern = new("^(-1|0|[0-9]{1,5}(s|m|h))$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A base da API do Ollama. HTTP só para destino confiável (laço local, rede privada ou nome de um rótulo só, como o serviço <c>ollama</c> de uma rede interna); qualquer outro destino exige HTTPS, com a
    /// validação de certificado intacta. A porta do Ollama não deve ser exposta à internet.
    /// </summary>
    public string BaseAddress { get; set; } = "http://127.0.0.1:11434/";

    /// <summary>Token de proxy autenticado na frente do Ollama (opcional; segredo do ambiente). Enviado em cada chamada, nunca guardado em cabeçalho padrão, e só por HTTPS ou destino confiável.</summary>
    public string? BearerToken { get; set; }

    /// <summary>Quanto o servidor mantém o modelo na memória após a chamada (<c>keep_alive</c>: <c>5m</c>, <c>1h</c>, <c>0</c>, <c>-1</c>). Nulo não envia o campo (padrão do servidor, 5 minutos).</summary>
    public string? KeepAlive { get; set; }

    /// <summary>Acrescenta o esquema da decisão ao texto do sistema quando a saída é estruturada (a documentação recomenda passar o esquema também no prompt). Ligado por padrão.</summary>
    public bool GroundStructuredOutputInPrompt { get; set; } = true;

    /// <summary>
    /// Caracteres por token da ESTIMATIVA do orçamento de contexto (heurística, sem tokenizador do modelo). Conservador (poucos caracteres por token = estimativa alta): o padrão é o mesmo do AiGateway.
    /// A estimativa só serve para RECUSAR uma requisição claramente acima da janela; não prova que um pedido aceito caiba nem que o histórico foi preservado.
    /// </summary>
    public double CharactersPerToken { get; set; } = 3.0;

    /// <summary>Margem multiplicativa da estimativa de entrada (nunca abaixo de 1).</summary>
    public double InputSafetyMargin { get; set; } = 1.3;

    /// <summary>Acréscimos fixos da estimativa: por mensagem (inclui a do sistema) e por pedido (modelo de conversa do servidor).</summary>
    public int PerMessageOverheadTokens { get; set; } = 8;

    public int RequestOverheadTokens { get; set; } = 16;

    /// <summary>O máximo de bytes que se lê de uma resposta: acima disso a resposta é inválida.</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Os perfis por nome (sem diferenciar maiúsculas). O padrão traz o perfil de atendimento do catálogo de referência.</summary>
    public Dictionary<string, OllamaChatProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["atendimento-local"] = new OllamaChatProfile(),
        ["atendimento-local-sem-raciocinio"] = new OllamaChatProfile { Think = false },
    };

    internal Uri ChatUri => new(Base, "api/chat");

    internal Uri Base => new(BaseAddress.EndsWith('/') ? BaseAddress : BaseAddress + "/", UriKind.Absolute);

    /// <exception cref="InvalidOperationException">Algum valor é inválido: falha na partida, nunca no meio do processamento.</exception>
    public void Validate()
    {
        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var endereco)
            || endereco.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endereco.UserInfo)
            || !string.IsNullOrEmpty(endereco.Query)
            || !string.IsNullOrEmpty(endereco.Fragment)
            || string.IsNullOrEmpty(endereco.Host))
        {
            throw new InvalidOperationException("O endereço do Ollama precisa ser uma URL absoluta http ou https, sem usuário, consulta nem fragmento.");
        }

        if (endereco.Scheme == "http" && !IsTrustedForPlainHttp(endereco))
        {
            throw new InvalidOperationException("O endereço do Ollama só pode usar HTTP em laço local, rede privada ou nome interno de um rótulo; para outro destino use HTTPS.");
        }

        if (BearerToken is not null && (BearerToken.Length is 0 or > 4096 || BearerToken.Any(c => c is < '!' or > '~')))
        {
            throw new InvalidOperationException("O token do Ollama é inválido (ASCII visível, sem espaço, de 1 a 4096 caracteres).");
        }

        if (KeepAlive is not null && !KeepAlivePattern.IsMatch(KeepAlive))
        {
            throw new InvalidOperationException("O keep_alive do Ollama é inválido (use -1, 0 ou um número seguido de s, m ou h).");
        }

        if (!(CharactersPerToken > 0) || !double.IsFinite(CharactersPerToken) || !(InputSafetyMargin >= 1) || !double.IsFinite(InputSafetyMargin) || PerMessageOverheadTokens < 0 || RequestOverheadTokens < 0)
        {
            throw new InvalidOperationException("A estimativa de contexto do adaptador do Ollama é inválida (caracteres por token positivos, margem não menor que 1, acréscimos não negativos).");
        }

        if (MaxResponseBytes is < 1024 or > 16_777_216 || ConnectTimeout <= TimeSpan.Zero || PooledConnectionLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("A configuração do adaptador do Ollama é inválida (tamanho máximo da resposta, tempo de conexão ou vida da conexão).");
        }

        foreach (var (nome, perfil) in Profiles)
        {
            if (string.IsNullOrWhiteSpace(nome) || perfil is null || perfil.NumCtx is < 512 or > 1_048_576 || !(perfil.Temperature is >= 0 and <= 2))
            {
                throw new InvalidOperationException("O perfil de modelo do adaptador do Ollama é inválido (nome vazio, num_ctx fora de 512 a 1048576 ou temperatura fora de 0 a 2).");
            }
        }
    }

    /// <summary>
    /// Destino em que o HTTP puro é aceitável: laço local, endereço de rede privada (10/8, 172.16/12, 192.168/16, 100.64/10, link-local, ULA <c>fc00::/7</c>) ou nome de um rótulo só (rede interna de contêineres).
    /// A URL é interpretada como URI, nunca por prefixo de texto. Um nome com ponto exige HTTPS (o nome pode resolver para um endereço público, que a configuração não consegue provar).
    /// </summary>
    internal static bool IsTrustedForPlainHttp(Uri uri)
    {
        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                return ip.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
            }

            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254);
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !host.Contains('.');
    }
}

/// <summary>Um modelo do catálogo, só com o que a conferência de coerência com o perfil precisa (o adaptador não conhece o catálogo do AiGateway).</summary>
public sealed record OllamaCatalogEntry(string ProviderId, string ModelId, string Profile, int ContextWindowTokens, int MaxOutputTokens, bool Active);

/// <summary>
/// A conferência, na PARTIDA, de que cada modelo ATIVO do Ollama no catálogo tem um perfil existente e a janela do catálogo IGUAL ao <c>NumCtx</c> do perfil (o que o servidor realmente aloca). Sem isso, o AiGateway
/// aceitaria uma entrada que o servidor pode truncar em silêncio. É uma conferência de CONFIGURAÇÃO: não prova o comportamento do servidor.
/// </summary>
public static class OllamaCatalogCoherence
{
    /// <summary>Os problemas encontrados (vazio = coerente). Só os modelos ativos do provedor <c>ollama</c> são conferidos; o texto nunca traz o endereço do servidor.</summary>
    public static IReadOnlyList<string> Find(OllamaChatOptions options, IEnumerable<OllamaCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(entries);
        var problemas = new List<string>();
        foreach (var e in entries.Where(e => e.Active && e.ProviderId == OllamaChatOptions.ProviderId))
        {
            if (!options.Profiles.TryGetValue(e.Profile, out var perfil))
            {
                problemas.Add($"O modelo '{e.ModelId}' do Ollama usa o perfil '{e.Profile}', que não existe na configuração do adaptador.");
                continue;
            }

            if (e.ContextWindowTokens != perfil.NumCtx)
            {
                problemas.Add($"O modelo '{e.ModelId}' do Ollama declara janela de {e.ContextWindowTokens} tokens no catálogo, mas o perfil '{e.Profile}' aloca {perfil.NumCtx} (devem ser iguais).");
            }

            if (e.MaxOutputTokens >= perfil.NumCtx)
            {
                problemas.Add($"O teto de saída de {e.MaxOutputTokens} tokens do modelo '{e.ModelId}' do Ollama não cabe na janela de {perfil.NumCtx} do perfil '{e.Profile}'.");
            }
        }

        return problemas;
    }

    /// <exception cref="InvalidOperationException">Há incoerência: o host não sobe (falha na partida, nunca no meio de um atendimento).</exception>
    public static void EnsureCoherent(OllamaChatOptions options, IEnumerable<OllamaCatalogEntry> entries)
    {
        var problemas = Find(options, entries);
        if (problemas.Count > 0)
        {
            throw new InvalidOperationException("Catálogo e perfil do Ollama incoerentes: " + string.Join(" ", problemas));
        }
    }
}
