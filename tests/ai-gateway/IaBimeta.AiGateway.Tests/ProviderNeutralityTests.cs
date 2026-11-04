using System.Xml.Linq;

namespace IaBimeta.AiGateway.Tests;

/// <summary>
/// A regra de arquitetura do ADR 0017 (seção C.1 e critério 1): o <c>AiGateway.Core</c> e o contrato (e, quando existir, o <c>Agents</c>) NÃO contêm nome de
/// provedor. Um provedor novo exige só um adaptador e entradas no catálogo. O conjunto avaliado nunca é vazio. Examina o texto-fonte (código, comentários e
/// documentação XML), porque um nome citado em comentário já é acoplamento de documentação.
/// </summary>
public class ProviderNeutralityTests
{
    private static readonly string[] NomesDeProvedor = { "openai", "anthropic", "claude", "chatgpt", "gemini", "mistral", "cohere", "ollama" };

    private static readonly string Raiz = EncontrarRaiz();

    private static string EncontrarRaiz()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null)
        {
            if (File.Exists(Path.Combine(diretorio.FullName, "docs", "architecture", "module-dependencies.json")))
            {
                return diretorio.FullName;
            }

            diretorio = diretorio.Parent;
        }

        throw new InvalidOperationException("Não achei a raiz do repositório acima de " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> ArquivosDeCodigo(string pastaRelativa)
    {
        var pasta = Path.Combine(Raiz, pastaRelativa.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(pasta))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(pasta, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
    }

    [Theory]
    [InlineData("backend/modules/ai-gateway/IaBimeta.AiGateway.Core")]
    [InlineData("backend/modules/ai-gateway/IaBimeta.AiGateway.Contracts")]
    public void O_codigo_do_AiGateway_nao_cita_provedor_de_IA(string pasta)
    {
        var arquivos = ArquivosDeCodigo(pasta).ToList();
        Assert.True(arquivos.Count >= 3, $"o conjunto avaliado em {pasta} não pode ser vazio");

        foreach (var arquivo in arquivos)
        {
            var texto = File.ReadAllText(arquivo);
            foreach (var provedor in NomesDeProvedor)
            {
                Assert.False(texto.Contains(provedor, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(arquivo)} cita o provedor '{provedor}'.");
            }
        }
    }

    [Fact]
    public void O_Agents_quando_existir_tambem_nao_cita_provedor_de_IA()
    {
        // O Agents nasce na fatia C2: a regra já vale para ele. Sem a pasta ainda, não há o que verificar (a regra do AiGateway acima tem conjunto não vazio).
        foreach (var arquivo in ArquivosDeCodigo("backend/modules/agents"))
        {
            var texto = File.ReadAllText(arquivo);
            foreach (var provedor in NomesDeProvedor)
            {
                Assert.False(texto.Contains(provedor, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(arquivo)} cita o provedor '{provedor}'.");
            }
        }
    }

    [Fact]
    public void O_Core_e_o_Contracts_do_AiGateway_nao_tem_pacote_e_so_referenciam_o_permitido()
    {
        var core = XDocument.Load(Path.Combine(Raiz, "backend", "modules", "ai-gateway", "IaBimeta.AiGateway.Core", "IaBimeta.AiGateway.Core.csproj"));
        var contratos = XDocument.Load(Path.Combine(Raiz, "backend", "modules", "ai-gateway", "IaBimeta.AiGateway.Contracts", "IaBimeta.AiGateway.Contracts.csproj"));

        Assert.Empty(core.Descendants("PackageReference"));
        Assert.Empty(core.Descendants("FrameworkReference"));
        Assert.Empty(contratos.Descendants("PackageReference"));
        Assert.Empty(contratos.Descendants("FrameworkReference"));
        Assert.Empty(contratos.Descendants("ProjectReference"));

        var referenciasDoCore = core.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "IaBimeta.AiGateway.Contracts", "IaBimeta.SharedKernel" }, referenciasDoCore);
    }
}
