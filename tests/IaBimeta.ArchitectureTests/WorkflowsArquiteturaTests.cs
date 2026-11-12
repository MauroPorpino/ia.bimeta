using System.Text.RegularExpressions;

namespace IaBimeta.ArchitectureTests;

/// <summary>
/// As regras do módulo Workflows (ADR 0037 e 0038; BOUNDARIES.md): arestas só as da matriz (Conversations, Capabilities, Identity, Tenancy, Audit), NENHUMA para Agents, Knowledge, AiGateway nem ContactCenter
/// (o que precisa deles passa por portas), e o interpretador e o simulador sem rede, IA, banco nem relógio. Cada regra negativa tem entrada real: a varredura não pode ser vazia.
/// </summary>
public class WorkflowsArquiteturaTests
{
    private static IEnumerable<string> ArquivosDe(string projeto)
    {
        var pasta = Path.Combine(RepositorioDeProjetos.Raiz, "backend", "modules", "workflows", projeto);
        var arquivos = Directory.EnumerateFiles(pasta, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(arquivos.Count >= 1, "A varredura precisa ter os arquivos do projeto para provar a regra negativa.");
        return arquivos;
    }

    private static string Codigo(string arquivo)
    {
        var texto = File.ReadAllText(arquivo);
        texto = Regex.Replace(texto, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(texto, @"//.*$", string.Empty, RegexOptions.Multiline);
    }

    [Fact]
    public void O_Workflows_so_referencia_o_que_a_matriz_permite_e_nunca_Agents_Knowledge_AiGateway_ou_ContactCenter()
    {
        var projetos = RepositorioDeProjetos.LerProjetos();
        var doModulo = projetos.Where(p => p.Nome.StartsWith("IaBimeta.Workflows.", StringComparison.Ordinal) && !p.Nome.EndsWith("Tests", StringComparison.Ordinal)).ToList();

        Assert.Contains(doModulo, p => p.Nome == "IaBimeta.Workflows.Core");
        var permitidas = new Dictionary<string, string[]>
        {
            ["IaBimeta.Workflows.Contracts"] = [],
            ["IaBimeta.Workflows.Core"] = ["IaBimeta.SharedKernel", "IaBimeta.Workflows.Contracts", "IaBimeta.Capabilities.Contracts", "IaBimeta.Conversations.Contracts", "IaBimeta.Audit.Contracts"],
            ["IaBimeta.Workflows.Infrastructure"] = ["IaBimeta.SharedKernel", "IaBimeta.Messaging", "IaBimeta.Workflows.Core", "IaBimeta.Workflows.Contracts", "IaBimeta.Conversations.Contracts"],
            ["IaBimeta.Workflows.Presentation"] = ["IaBimeta.SharedKernel", "IaBimeta.Workflows.Core"],
        };
        foreach (var projeto in doModulo)
        {
            Assert.True(permitidas.ContainsKey(projeto.Nome), $"Projeto inesperado: {projeto.Nome}");
            Assert.All(projeto.Referencias, r => Assert.True(permitidas[projeto.Nome].Contains(r), $"{projeto.Nome} -> {r}"));
            Assert.DoesNotContain(projeto.Referencias, r => r.Contains("Agents", StringComparison.Ordinal) || r.Contains("Knowledge", StringComparison.Ordinal) || r.Contains("AiGateway", StringComparison.Ordinal) || r.Contains("ContactCenter", StringComparison.Ordinal));
        }

        // Nenhum outro módulo referencia o Core nem a Infrastructure do Workflows: só os hosts e os testes (o Agents usará os Contracts quando existirem).
        Assert.DoesNotContain(
            projetos.Where(p => !p.Nome.StartsWith("IaBimeta.Workflows.", StringComparison.Ordinal) && p.Nome.StartsWith("IaBimeta.", StringComparison.Ordinal) && (p.Nome.EndsWith(".Core", StringComparison.Ordinal) || p.Nome.EndsWith(".Contracts", StringComparison.Ordinal) || p.Nome.EndsWith(".Infrastructure", StringComparison.Ordinal))),
            p => p.Referencias.Contains("IaBimeta.Workflows.Core") || p.Referencias.Contains("IaBimeta.Workflows.Infrastructure"));
    }

    [Fact]
    public void O_interpretador_e_o_simulador_nao_usam_rede_IA_banco_relogio_nem_aleatoriedade()
    {
        var motor = ArquivosDe("IaBimeta.Workflows.Core")
            .Where(a => a.Contains($"{Path.DirectorySeparatorChar}Domain{Path.DirectorySeparatorChar}") || Path.GetFileName(a) is "FlowInterpreter.cs" or "FlowSimulator.cs" or "FlowText.cs" or "FlowValidator.cs" or "FlowGraphSerializer.cs")
            .Where(a => Path.GetFileName(a) is not ("WorkflowVersion.cs" or "WorkflowsSupport.cs" or "RuntimeEntities.cs"))
            .ToList();
        Assert.True(motor.Count >= 6);

        foreach (var arquivo in motor)
        {
            var codigo = Codigo(arquivo);
            foreach (var proibido in new[] { "HttpClient", "System.Net", "EntityFrameworkCore", "DateTime.Now", "DateTimeOffset.Now", "UtcNow", "TimeProvider", "Random", "Guid.NewGuid", "ICapabilityExecutor", "IAiGateway", "Environment." })
            {
                Assert.False(codigo.Contains(proibido, StringComparison.Ordinal), $"{Path.GetFileName(arquivo)} usa {proibido}");
            }
        }
    }

    [Fact]
    public void A_Presentation_e_a_Infrastructure_nao_executam_operacao_de_integracao()
    {
        // O editor e a simulação só LEEM o catálogo; executar operação é do runtime (ADR 0037 §2).
        // A composição (WorkflowsInfrastructureExtensions) entrega o executor ao runtime; só o runtime (Core) executa. Os demais arquivos da Infrastructure e da Presentation nunca o tocam.
        foreach (var arquivo in ArquivosDe("IaBimeta.Workflows.Presentation").Concat(ArquivosDe("IaBimeta.Workflows.Infrastructure")).Where(a => Path.GetFileName(a) != "WorkflowsInfrastructureExtensions.cs"))
        {
            Assert.DoesNotContain("ICapabilityExecutor", Codigo(arquivo));
        }

        foreach (var arquivo in ArquivosDe("IaBimeta.Workflows.Core").Where(a => Path.GetFileName(a) is "WorkflowDefinitionService.cs" or "WorkflowsPorts.cs" or "FlowRouter.cs"))
        {
            Assert.DoesNotContain("ICapabilityExecutor", Codigo(arquivo));
        }
    }
}
