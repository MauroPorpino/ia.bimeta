using System.Text.Json;
using System.Xml.Linq;

namespace IaBimeta.ArchitectureTests;

/// <summary>Lê do disco os .csproj da solução e a matriz de dependências, a partir da raiz do repositório.</summary>
internal static class RepositorioDeProjetos
{
    private static readonly Lazy<string> RaizEncontrada = new(EncontrarRaiz);

    public static string Raiz => RaizEncontrada.Value;

    public static IReadOnlyList<ProjetoDaSolucao> LerProjetos()
    {
        var projetos = new List<ProjetoDaSolucao>();

        foreach (var pasta in new[] { "backend", "tests" })
        {
            var caminho = Path.Combine(Raiz, pasta);
            foreach (var arquivo in Directory.EnumerateFiles(caminho, "*.csproj", SearchOption.AllDirectories))
            {
                var relativo = Path.GetRelativePath(Raiz, arquivo).Replace('\\', '/');
                if (relativo.Contains("/bin/") || relativo.Contains("/obj/"))
                {
                    continue;
                }

                projetos.Add(Ler(arquivo));
            }
        }

        return projetos.OrderBy(p => p.Nome, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyDictionary<string, string[]> LerMatriz()
    {
        var caminho = Path.Combine(Raiz, "docs", "architecture", "module-dependencies.json");
        using var documento = JsonDocument.Parse(File.ReadAllText(caminho));

        return documento.RootElement.GetProperty("modulos").EnumerateObject()
            .ToDictionary(
                modulo => modulo.Name,
                modulo => modulo.Value.EnumerateArray().Select(d => d.GetString()!).ToArray());
    }

    private static ProjetoDaSolucao Ler(string arquivo)
    {
        var xml = XDocument.Load(arquivo);
        var raiz = xml.Root!;

        var referencias = raiz.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => Path.GetFileNameWithoutExtension(i!.Replace('\\', '/')))
            .ToList();
        var pacotes = raiz.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i!)
            .ToList();

        var frameworks = raiz.Descendants("FrameworkReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i!)
            .ToList();
        if (((string?)raiz.Attribute("Sdk"))?.EndsWith(".Web", StringComparison.Ordinal) == true)
        {
            frameworks.Add("Microsoft.NET.Sdk.Web");
        }

        return new ProjetoDaSolucao(Path.GetFileNameWithoutExtension(arquivo), referencias, pacotes, frameworks);
    }

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

        throw new InvalidOperationException(
            "Não achei a raiz do repositório (docs/architecture/module-dependencies.json) acima de " + AppContext.BaseDirectory);
    }
}
