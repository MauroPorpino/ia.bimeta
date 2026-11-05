using System.Text.RegularExpressions;

namespace IaBimeta.ArchitectureTests;

/// <summary>
/// As regras do adaptador do WhatsApp Cloud API (ADR 0011, seção B.2 e E; BOUNDARIES §7, DEP-2 e DEP-3): referencia só o <c>Channels.Contracts</c>, não tem SDK nem pacote, só os hosts o referenciam, usa o token da
/// conta em UM ponto (o pedido de envio) e o App Secret em UM arquivo, nunca baixa mídia nem escreve em log. Cada regra negativa tem entrada real: os arquivos varridos não podem ser vazios.
/// </summary>
public class AdaptadorWhatsAppArquiteturaTests
{
    private const string Adaptador = "IaBimeta.Adapters.WhatsApp";

    private static IReadOnlyList<string> Arquivos()
    {
        var pasta = Path.Combine(RepositorioDeProjetos.Raiz, "backend", "adapters", Adaptador);
        var arquivos = Directory.EnumerateFiles(pasta, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(arquivos.Count >= 5, "A varredura precisa ter os arquivos do adaptador para provar a regra negativa.");
        return arquivos;
    }

    private static string Codigo(string arquivo)
    {
        var texto = File.ReadAllText(arquivo);
        texto = Regex.Replace(texto, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(texto, @"//.*$", string.Empty, RegexOptions.Multiline);
    }

    [Fact]
    public void O_adaptador_referencia_so_o_Contracts_do_Channels_e_so_os_hosts_o_referenciam()
    {
        var projetos = RepositorioDeProjetos.LerProjetos();
        var adaptador = Assert.Single(projetos, p => p.Nome == Adaptador);

        Assert.Equal(new[] { "IaBimeta.Channels.Contracts" }, adaptador.Referencias.OrderBy(r => r, StringComparer.Ordinal).ToArray());

        var consumidores = projetos.Where(p => p.Referencias.Contains(Adaptador)).Select(p => p.Nome).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(consumidores);
        Assert.All(consumidores, nome => Assert.True(nome is "IaBimeta.Api" or "IaBimeta.Worker" || nome.EndsWith("Tests", StringComparison.Ordinal), $"{nome} não pode referenciar o adaptador (só os hosts e os testes, DEP-3)."));
    }

    [Fact]
    public void Nenhum_modulo_do_Channels_referencia_o_adaptador()
    {
        var projetos = RepositorioDeProjetos.LerProjetos();
        var doChannels = projetos.Where(p => p.Nome.StartsWith("IaBimeta.Channels.", StringComparison.Ordinal) && !p.Nome.EndsWith("Tests", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(doChannels);
        Assert.DoesNotContain(doChannels, p => p.Referencias.Contains(Adaptador));
    }

    [Fact]
    public void O_adaptador_nao_tem_pacote_nem_SDK_so_HttpClient_HMAC_e_System_Text_Json_da_plataforma()
    {
        var csproj = File.ReadAllText(Path.Combine(RepositorioDeProjetos.Raiz, "backend", "adapters", Adaptador, $"{Adaptador}.csproj"));

        Assert.DoesNotContain("<PackageReference", csproj);
        Assert.Contains("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", csproj);
    }

    [Fact]
    public void O_token_da_conta_so_e_lido_na_montagem_do_pedido_e_nunca_vai_a_log_ou_cabecalho_padrao()
    {
        var arquivos = Arquivos().ToDictionary(a => Path.GetFileName(a)!, a => Codigo(a));

        var quemLeOToken = arquivos.Where(a => a.Value.Contains(".AccessToken", StringComparison.Ordinal)).Select(a => a.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

        // Só a montagem do pedido toca o valor do token, e só para o cabeçalho DAQUELA requisição.
        Assert.Equal(new[] { "WhatsAppSendRequestFactory.cs" }, quemLeOToken);
        foreach (var (nome, codigo) in arquivos)
        {
            Assert.DoesNotContain("DefaultRequestHeaders", codigo);
            Assert.DoesNotContain("ILogger", codigo);
            Assert.DoesNotContain("Console.", codigo);
            Assert.False(Regex.IsMatch(codigo, @"\.Message\b"), $"{nome} usa a mensagem de uma exceção ou do provedor: ela pode carregar o token ou trechos do pedido.");
        }
    }

    [Fact]
    public void O_App_Secret_so_aparece_na_verificacao_da_assinatura_e_nao_e_guardado_em_campo()
    {
        var arquivos = Arquivos().ToDictionary(a => Path.GetFileName(a)!, a => Codigo(a));

        var quemUsa = arquivos.Where(a => a.Value.Contains("appSecret", StringComparison.Ordinal)).Select(a => a.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(new[] { "WhatsAppCloudAdapter.cs" }, quemUsa);
        Assert.All(arquivos.Values, codigo => Assert.False(Regex.IsMatch(codigo, @"(private|internal|public)[^;(=]*\b_?(appSecret|AppSecret|verifyToken|VerifyToken|accessToken)\b\s*[;=]"), "Segredo guardado em campo ou propriedade."));
    }

    [Fact]
    public void O_adaptador_nunca_baixa_midia_nem_le_a_url_ou_o_hash_dela_e_so_faz_o_envio_de_texto_e_a_leitura_do_numero()
    {
        var arquivos = Arquivos().ToDictionary(a => Path.GetFileName(a)!, a => Codigo(a));

        foreach (var (nome, codigo) in arquivos)
        {
            Assert.False(Regex.IsMatch(codigo, @"\.(GetAsync|GetStreamAsync|GetByteArrayAsync|GetStringAsync|DownloadAsync|DeleteAsync|PutAsync)\b"), $"{nome} faz uma chamada HTTP além do envio e da leitura do número.");
        }

        // As ÚNICAS chamadas HTTP: o envio (POST .../messages) e a sonda do teste de prontidão (um GET de leitura do número, sem mensagem nem mídia). Cada uma monta o pedido num só lugar.
        Assert.Equal(2, arquivos.Values.Sum(c => Regex.Matches(c, @"_http\.SendAsync\(").Count));
        var fabrica = arquivos["WhatsAppSendRequestFactory.cs"];
        Assert.Single(Regex.Matches(fabrica, @"HttpMethod\.Post"));
        Assert.Single(Regex.Matches(fabrica, @"HttpMethod\.Get"));
        Assert.DoesNotContain(arquivos, a => a.Key != "WhatsAppSendRequestFactory.cs" && Regex.IsMatch(a.Value, @"HttpMethod\.(Get|Post|Put|Delete|Patch)\b"));

        // A leitura do número pede só os campos de identificação e qualidade: nunca mídia, perfil de cliente ou conversa.
        Assert.Contains("fields=id,display_phone_number,verified_name,quality_rating", fabrica);

        // O parser não lê a url nem o sha256 da mídia (só metadados de texto).
        Assert.DoesNotContain("\"url\"", arquivos["WhatsAppWebhookParser.cs"]);
        Assert.DoesNotContain("\"sha256\"", arquivos["WhatsAppWebhookParser.cs"]);
    }

    [Fact]
    public void A_estrategia_de_efeito_declarada_e_nenhuma_porque_a_Meta_nao_documenta_idempotencia_de_envio()
    {
        var adaptador = Codigo(Arquivos().Single(a => Path.GetFileName(a) == "WhatsAppCloudAdapter.cs"));

        Assert.Contains("EffectStrategy => ProviderEffectStrategy.None", adaptador);
        Assert.DoesNotContain("ProviderEffectStrategy.Idempotent", adaptador);
        Assert.DoesNotContain("ProviderEffectStrategy.Reconcilable", adaptador);
    }
}
