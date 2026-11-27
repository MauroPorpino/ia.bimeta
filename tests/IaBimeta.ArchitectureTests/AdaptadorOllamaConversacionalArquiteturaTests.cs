using System.Text.RegularExpressions;

namespace IaBimeta.ArchitectureTests;

/// <summary>
/// As regras do adaptador CONVERSACIONAL do Ollama (Etapa 04; BOUNDARIES §7, DEP-2 e DEP-3): referencia só os Contracts dos módulos donos das portas (Knowledge e AiGateway), não tem pacote nem SDK, nunca lê a credencial do
/// tenant, não envia streaming nem ferramentas, não baixa, cria nem apaga modelos, não segue redirecionamentos, e nenhum módulo de negócio o conhece. Cada regra negativa tem entrada real: os arquivos varridos não podem ser vazios.
/// </summary>
public class AdaptadorOllamaConversacionalArquiteturaTests
{
    private const string Adaptador = "IaBimeta.Adapters.Ai.Ollama";

    private static readonly string[] ArquivosDaConversa = ["OllamaChatOptions.cs", "OllamaChatProvider.cs", "OllamaChatRequestFactory.cs", "OllamaChatResponseReader.cs", "OllamaReasoningFilter.cs", "OllamaServerProbe.cs"];

    private static Dictionary<string, string> Codigos()
    {
        var pasta = Path.Combine(RepositorioDeProjetos.Raiz, "backend", "adapters", Adaptador);
        var arquivos = ArquivosDaConversa.ToDictionary(n => n, n => Path.Combine(pasta, n));
        Assert.Equal(ArquivosDaConversa.Length, arquivos.Count(a => File.Exists(a.Value)));
        return arquivos.ToDictionary(a => a.Key, a => Sem(File.ReadAllText(a.Value)));
    }

    private static string Sem(string texto)
    {
        texto = Regex.Replace(texto, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(texto, @"//.*$", string.Empty, RegexOptions.Multiline);
    }

    [Fact]
    public void O_adaptador_referencia_so_os_Contracts_dos_modulos_donos_das_portas_e_so_os_hosts_o_referenciam()
    {
        var projetos = RepositorioDeProjetos.LerProjetos();
        var adaptador = Assert.Single(projetos, p => p.Nome == Adaptador);

        Assert.Equal(new[] { "IaBimeta.AiGateway.Contracts", "IaBimeta.Knowledge.Contracts" }, adaptador.Referencias.OrderBy(r => r, StringComparer.Ordinal).ToArray());

        var consumidores = projetos.Where(p => p.Referencias.Contains(Adaptador)).Select(p => p.Nome).ToList();
        Assert.Contains("IaBimeta.Api", consumidores);
        Assert.Contains("IaBimeta.Worker", consumidores);
        Assert.All(consumidores, nome => Assert.True(nome is "IaBimeta.Api" or "IaBimeta.Worker" || nome.EndsWith("Tests", StringComparison.Ordinal), $"{nome} não pode referenciar o adaptador (só os hosts e os testes, DEP-3)."));
    }

    [Fact]
    public void O_adaptador_nao_tem_pacote_nem_SDK()
    {
        var csproj = File.ReadAllText(Path.Combine(RepositorioDeProjetos.Raiz, "backend", "adapters", Adaptador, $"{Adaptador}.csproj"));
        Assert.DoesNotContain("<PackageReference", csproj);
    }

    [Fact]
    public void A_conversa_nunca_le_a_credencial_do_tenant_nem_loga_nem_usa_cabecalho_padrao_nem_mensagem_de_excecao()
    {
        foreach (var (nome, codigo) in Codigos())
        {
            Assert.False(Regex.IsMatch(codigo, @"\.Credential\b|\.Secret\b"), $"{nome} lê a credencial do tenant: o Ollama não tem chave comercial.");
            Assert.DoesNotContain("DefaultRequestHeaders", codigo);
            Assert.DoesNotContain("ILogger", codigo);
            Assert.DoesNotContain("Console.", codigo);
            Assert.False(Regex.IsMatch(codigo, @"\.Message\b"), $"{nome} usa a mensagem de uma exceção ou do servidor: ela pode carregar o endereço interno ou trechos do pedido.");
        }
    }

    [Fact]
    public void A_conversa_nao_usa_streaming_nem_ferramentas_nem_gerencia_modelos_nem_segue_redirecionamento_nem_desliga_o_TLS()
    {
        var codigos = Codigos();
        foreach (var (nome, codigo) in codigos)
        {
            Assert.DoesNotContain("\"tools\"", codigo, StringComparison.Ordinal);
            Assert.DoesNotContain("\"tool_calls\"", codigo, StringComparison.Ordinal);
            Assert.False(Regex.IsMatch(codigo, @"\[""stream""\]\s*=\s*true"), $"{nome} liga streaming.");
            foreach (var proibido in new[] { "api/pull", "api/create", "api/delete", "api/copy", "api/push" })
            {
                Assert.DoesNotContain(proibido, codigo, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("ServerCertificateCustomValidationCallback", codigo, StringComparison.Ordinal);
            Assert.DoesNotContain("DangerousAcceptAnyServerCertificateValidator", codigo, StringComparison.Ordinal);
        }

        Assert.Contains("AllowAutoRedirect = false", codigos["OllamaChatProvider.cs"]);
    }

    [Fact]
    public void O_raciocinio_do_modelo_nunca_e_lido()
    {
        Assert.DoesNotContain("\"thinking\"", Codigos()["OllamaChatResponseReader.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public void Nenhum_modulo_de_negocio_conhece_o_adaptador_nem_o_nome_do_provedor_no_AiGateway_e_no_Agents()
    {
        var projetos = RepositorioDeProjetos.LerProjetos();
        Assert.DoesNotContain(projetos.Where(p => (p.Nome.StartsWith("IaBimeta.Agents.", StringComparison.Ordinal) || p.Nome.StartsWith("IaBimeta.AiGateway.", StringComparison.Ordinal)) && !p.Nome.EndsWith("Tests", StringComparison.Ordinal)),
            p => p.Referencias.Contains(Adaptador));

        foreach (var modulo in new[] { "ai-gateway", "agents" })
        {
            var pasta = Path.Combine(RepositorioDeProjetos.Raiz, "backend", "modules", modulo);
            var arquivos = Directory.EnumerateFiles(pasta, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.NotEmpty(arquivos);
            foreach (var arquivo in arquivos)
            {
                Assert.DoesNotContain("Ollama", Sem(File.ReadAllText(arquivo)), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Nenhum_modelo_do_Ollama_vem_ativo_de_fabrica_nos_hosts()
    {
        foreach (var host in new[] { "IaBimeta.Api", "IaBimeta.Worker" })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(RepositorioDeProjetos.Raiz, "backend", "host", host, "appsettings.json")))!;
            var modelos = json["AiGateway"]!["Catalog"]!["Models"]!.AsArray().Where(m => (string?)m!["ProviderId"] == "ollama").ToList();
            Assert.NotEmpty(modelos);
            Assert.All(modelos, m => Assert.Equal("Disabled", (string?)m!["Status"]));
        }
    }
}
