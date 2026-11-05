using System.Text.RegularExpressions;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// A configuração do adaptador WhatsApp Cloud API (seção <c>Channels:Providers:WhatsApp</c>). Não tem segredo: o token de acesso e o App Secret são da conta, ficam cifrados no <c>Channels</c> e chegam a cada
/// chamada, decifrados na hora. Os valores padrão são os da documentação oficial da Meta consultada em 05/11/2026 (ver <see cref="WhatsAppCloudAdapter"/>).
/// </summary>
public sealed class WhatsAppOptions
{
    /// <summary>
    /// A versão da Graph API, no caminho de cada chamada. Padrão: <c>v26.0</c>, a mais recente com data de lançamento (29/07/2026) na tabela oficial de versões da Graph API (consulta de 05/11/2026). A página
    /// de referência do WhatsApp já cita <c>v27.0</c> nos exemplos, mas ela não consta da tabela de versões: por isso NÃO é o padrão. Trocar é configuração, não código.
    /// </summary>
    public const string DefaultGraphApiVersion = "v26.0";

    private static readonly Regex VersionPattern = new(@"^v[0-9]{1,3}\.[0-9]{1,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A base da Graph API. Só HTTPS (exceto laço local, para teste): o token da conta nunca vai em claro.</summary>
    public string BaseAddress { get; set; } = "https://graph.facebook.com/";

    /// <summary>A versão da Graph API (por exemplo, <c>v26.0</c>).</summary>
    public string GraphApiVersion { get; set; } = DefaultGraphApiVersion;

    /// <summary>
    /// Quanto uma chamada de envio pode durar, do início à leitura do corpo. Esgotado, o resultado é <c>Unknown</c> (a mensagem PODE ter sido enviada). A Meta não documenta um tempo de resposta do envio:
    /// é parâmetro nosso, ajustável.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>O máximo de bytes que se lê de uma resposta: acima disso a resposta é ilegível (proteção contra corpo gigante).</summary>
    public int MaxResponseBytes { get; set; } = 262_144;

    /// <summary>Quanto tempo a conexão pode esperar para abrir.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Quanto tempo uma conexão do pool vive (renova o DNS de um cliente que vive o tempo do processo).</summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A base já resolvida, com a versão: <c>https://graph.facebook.com/v26.0/</c>.</summary>
    internal Uri VersionedBase => new(new Uri(BaseAddress, UriKind.Absolute), GraphApiVersion + "/");

    /// <exception cref="InvalidOperationException">Algum valor é inválido: falha na partida, nunca no meio do processamento.</exception>
    public void Validate()
    {
        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var endereco)
            || (endereco.Scheme != Uri.UriSchemeHttps && !(endereco.Scheme == Uri.UriSchemeHttp && endereco.IsLoopback))
            || !string.IsNullOrEmpty(endereco.UserInfo)
            || !string.IsNullOrEmpty(endereco.Query)
            || !string.IsNullOrEmpty(endereco.Fragment))
        {
            throw new InvalidOperationException("O endereço da Graph API precisa ser HTTPS absoluto, sem usuário, consulta nem fragmento (HTTP só em laço local).");
        }

        if (string.IsNullOrEmpty(GraphApiVersion) || !VersionPattern.IsMatch(GraphApiVersion))
        {
            throw new InvalidOperationException("A versão da Graph API precisa ter o formato vNN.N (por exemplo, v26.0).");
        }

        if (RequestTimeout <= TimeSpan.Zero
            || RequestTimeout > TimeSpan.FromMinutes(5)
            || MaxResponseBytes is < 1024 or > 16_777_216
            || ConnectTimeout <= TimeSpan.Zero
            || PooledConnectionLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("A configuração do adaptador WhatsApp é inválida (tempo limite, tamanho máximo da resposta, tempo de conexão ou vida da conexão).");
        }
    }
}
