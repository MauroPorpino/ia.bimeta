using System.Text.RegularExpressions;

namespace IaBimeta.ArchitectureTests;

/// <summary>
/// As regras do adaptador da OpenAI (ADR 0017, seção B e C; BOUNDARIES §7, DEP-2 e DEP-3): referencia só o <c>AiGateway.Contracts</c>, não tem SDK nem pacote, só os hosts o referenciam, usa a credencial em UM
/// ponto, não conhece <c>temperature</c> (o contrato não tem) e nenhum módulo de negócio conhece o provedor. Cada regra negativa tem entrada real: os arquivos varridos não podem ser vazios.
/// </summary>
public class AdaptadorOpenAiArquiteturaTests
{
    private const string Adaptador = "IaBimeta.Adapters.Ai.OpenAI";

    private static IReadOnlyList<string> Arquivos()
    {
        var pasta = Path.Combine(RepositorioDeProjetos.Raiz, "backend", "adapters", Adaptador);
        var arquivos = Directory.EnumerateFiles(pasta, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(arquivos.Count >= 4, "A varredura precisa ter os arquivos do adaptador para provar a regra negativa.");
        return arquivos;
    }

    private static string Codigo(string arquivo)
    {
        var texto = File.ReadAllText(arquivo);
        texto = Regex.Replace(texto, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(texto, @"//.*$", string.Empty, RegexOptions.Multiline);
    }

    [Fact]
    public void O_adaptador_referencia_so_o_Contracts_do_AiGateway_e_so_os_hosts_o_referenciam()
    {
        var projetos = RepositorioDeProjetos.LerProjetos();
        var adaptador = Assert.Single(projetos, p => p.Nome == Adaptador);

        Assert.Equal(new[] { "IaBimeta.AiGateway.Contracts" }, adaptador.Referencias.OrderBy(r => r, StringComparer.Ordinal).ToArray());

        var consumidores = projetos.Where(p => p.Referencias.Contains(Adaptador)).Select(p => p.Nome).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Contains("IaBimeta.Api", consumidores);
        Assert.Contains("IaBimeta.Worker", consumidores);
        Assert.All(consumidores, nome => Assert.True(nome is "IaBimeta.Api" or "IaBimeta.Worker" || nome.EndsWith("Tests", StringComparison.Ordinal), $"{nome} não pode referenciar o adaptador (só os hosts e os testes, DEP-3)."));
    }

    [Fact]
    public void O_adaptador_nao_tem_pacote_nem_SDK_so_HttpClient_e_System_Text_Json_da_plataforma()
    {
        var csproj = File.ReadAllText(Path.Combine(RepositorioDeProjetos.Raiz, "backend", "adapters", Adaptador, $"{Adaptador}.csproj"));

        Assert.DoesNotContain("<PackageReference", csproj);
        Assert.Contains("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", csproj);
    }

    [Fact]
    public void A_credencial_do_tenant_so_e_lida_em_um_ponto_e_nunca_vai_a_log_ou_cabecalho_padrao()
    {
        var arquivos = Arquivos().ToDictionary(a => Path.GetFileName(a)!, a => Codigo(a));

        var quemLeOSegredo = arquivos.Where(a => a.Value.Contains(".Secret", StringComparison.Ordinal)).Select(a => a.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

        // Só a montagem do pedido toca o valor da credencial, e só para o cabeçalho DAQUELA requisição.
        Assert.Equal(new[] { "OpenAiRequestFactory.cs" }, quemLeOSegredo);
        foreach (var (nome, codigo) in arquivos)
        {
            Assert.DoesNotContain("DefaultRequestHeaders", codigo);
            Assert.DoesNotContain("ILogger", codigo);
            Assert.DoesNotContain("Console.", codigo);
            Assert.False(Regex.IsMatch(codigo, @"\.Message\b"), $"{nome} usa a mensagem de uma exceção ou do provedor: ela pode carregar a credencial ou trechos do pedido.");
        }
    }

    [Fact]
    public void O_contrato_nao_tem_temperature_nem_top_p_e_o_adaptador_nunca_os_envia_nem_guarda_estado()
    {
        foreach (var arquivo in Arquivos())
        {
            var codigo = Codigo(arquivo);
            Assert.DoesNotContain("temperature", codigo, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("top_p", codigo, StringComparison.OrdinalIgnoreCase);
        }

        // O armazenamento de estado da resposta fica SEMPRE desligado (a privacidade do tenant não depende de configuração).
        var fabrica = Codigo(Arquivos().Single(a => Path.GetFileName(a) == "OpenAiRequestFactory.cs"));
        Assert.Contains("[\"store\"] = false", fabrica);
    }

    [Fact]
    public void Nenhum_modulo_de_negocio_conhece_o_adaptador_nem_o_nome_do_provedor_nos_modulos_do_AiGateway_e_Agents()
    {
        var projetos = RepositorioDeProjetos.LerProjetos();
        Assert.DoesNotContain(projetos.Where(p => p.Nome.StartsWith("IaBimeta.Agents.", StringComparison.Ordinal) || p.Nome.StartsWith("IaBimeta.AiGateway.", StringComparison.Ordinal) && !p.Nome.EndsWith("Tests", StringComparison.Ordinal)),
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
                Assert.DoesNotContain("OpenAI", Codigo(arquivo), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
