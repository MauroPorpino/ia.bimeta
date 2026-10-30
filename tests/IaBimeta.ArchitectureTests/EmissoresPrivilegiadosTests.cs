namespace IaBimeta.ArchitectureTests;

/// <summary>
/// A proteção do executor privilegiado é EXPLÍCITA no ContactCenter (a base completa, validada antes da cerca), e o Conversations NÃO rejeita globalmente a
/// pré-condição sem a geração (compatibilidade). Isso só se sustenta se o executor for o ÚNICO emissor de produção dos comandos que alteram o atendimento: este
/// inventário falha se outro ponto do <c>backend/</c> passar a construir esses comandos ou a pré-condição, e não passa em conjunto vazio.
/// </summary>
public class EmissoresPrivilegiadosTests
{
    private static readonly string[] Construcoes =
    {
        "new HandlingPrecondition(",
        "new TransitionHandlingModeCommand(",
        "new TransferSessionCommand(",
    };

    private static IEnumerable<string> ArquivosDeProducao()
    {
        var backend = Path.Combine(RepositorioDeProjetos.Raiz, "backend");
        return Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void So_o_executor_do_ContactCenter_constroi_os_comandos_e_a_pre_condicao_do_atendimento()
    {
        var emissores = ArquivosDeProducao()
            .Where(arquivo => { var texto = File.ReadAllText(arquivo); return Construcoes.Any(c => texto.Contains(c, StringComparison.Ordinal)); })
            .Select(Path.GetFileName)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // A regra negativa tem entrada real: o executor existe e constrói os três.
        Assert.Contains("HandlingExecutor.cs", emissores);
        Assert.Equal(new[] { "HandlingExecutor.cs" }, emissores);
    }
}
