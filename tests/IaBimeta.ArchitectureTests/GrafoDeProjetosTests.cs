namespace IaBimeta.ArchitectureTests;

/// <summary>
/// As regras de <c>docs/architecture/BOUNDARIES.md</c> sobre os projetos reais da solução (ARQ-04, ARQ-05, ARQ-06,
/// DEP-2, DEP-4/6 e as regras de pacote). Substituem a verificação provisória em PowerShell. Cada teste de regra
/// negativa é acompanhado de uma checagem de que ela tem entradas reais (nenhuma regra passa em conjunto vazio).
/// </summary>
public class GrafoDeProjetosTests
{
    private static readonly IReadOnlyList<ProjetoDaSolucao> Projetos = RepositorioDeProjetos.LerProjetos();
    private static readonly IReadOnlyDictionary<string, string[]> Matriz = RepositorioDeProjetos.LerMatriz();
    private static readonly IReadOnlyList<Violacao> Violacoes = RegrasDeArquitetura.Verificar(Projetos, Matriz);

    private static ClasseDoProjeto Classe(ProjetoDaSolucao projeto) => RegrasDeArquitetura.Classificar(projeto.Nome, Matriz.Keys.ToList());

    private static void SemViolacoesDe(params string[] regras)
    {
        var achadas = Violacoes.Where(v => regras.Contains(v.Regra)).Select(v => v.ToString()).ToList();
        Assert.True(achadas.Count == 0, string.Join(Environment.NewLine, achadas));
    }

    [Fact]
    public void O_repositorio_e_a_matriz_foram_lidos_de_verdade()
    {
        Assert.NotEmpty(Projetos);
        Assert.NotEmpty(Matriz);
        Assert.True(File.Exists(Path.Combine(RepositorioDeProjetos.Raiz, "backend", "IaBimeta.slnx")));
    }

    [Fact]
    public void Existem_projetos_de_modulo_hosts_blocos_e_testes_pois_nenhuma_regra_vale_em_conjunto_vazio()
    {
        var tipos = Projetos.Select(p => Classe(p).Tipo).ToHashSet();

        Assert.Contains(TipoDeProjeto.Modulo, tipos);
        Assert.Contains(TipoDeProjeto.Host, tipos);
        Assert.Contains(TipoDeProjeto.Bloco, tipos);
        Assert.Contains(TipoDeProjeto.Teste, tipos);
    }

    [Fact]
    public void Os_tres_modulos_da_etapa_2_existem_com_as_camadas_esperadas()
    {
        var nomes = Projetos.Select(p => p.Nome).ToHashSet();

        foreach (var esperado in new[]
        {
            "IaBimeta.Tenancy.Contracts", "IaBimeta.Tenancy.Core", "IaBimeta.Tenancy.Infrastructure", "IaBimeta.Tenancy.Presentation",
            "IaBimeta.Identity.Core", "IaBimeta.Identity.Infrastructure", "IaBimeta.Identity.Presentation",
            "IaBimeta.Audit.Contracts", "IaBimeta.Audit.Core", "IaBimeta.Audit.Infrastructure", "IaBimeta.Audit.Presentation",
        })
        {
            Assert.Contains(esperado, nomes);
        }
    }

    [Fact]
    public void Todo_projeto_esta_classificado_e_toda_referencia_aponta_para_projeto_existente()
    {
        SemViolacoesDe("CLASSIFICACAO", "REFERENCIA");
    }

    [Fact]
    public void O_grafo_de_projetos_nao_tem_ciclo()
    {
        SemViolacoesDe("CICLO");
    }

    [Fact]
    public void ARQ_04_so_hosts_e_testes_referenciam_Infrastructure_Presentation_e_adaptadores()
    {
        var restritos = Projetos.Where(p => Classe(p) is { Tipo: TipoDeProjeto.Modulo, Camada: "Infrastructure" or "Presentation" }).Select(p => p.Nome).ToHashSet();
        var referenciados = Projetos.SelectMany(p => p.Referencias).Where(restritos.Contains).ToList();
        Assert.NotEmpty(referenciados); // a regra tem o que verificar: o host referencia Infrastructure e Presentation

        SemViolacoesDe("ARQ-04");
    }

    [Fact]
    public void ARQ_05_e_ARQ_06_entre_modulos_so_por_Contracts_e_so_as_arestas_da_matriz()
    {
        var entreModulos = Projetos
            .SelectMany(p => p.Referencias.Select(r => (De: Classe(p), Para: RegrasDeArquitetura.Classificar(r, Matriz.Keys.ToList()))))
            .Where(a => a.De.Tipo == TipoDeProjeto.Modulo && a.Para.Tipo == TipoDeProjeto.Modulo && a.De.Modulo != a.Para.Modulo)
            .ToList();
        Assert.NotEmpty(entreModulos); // Identity -> Tenancy e Audit, Tenancy -> Audit
        Assert.All(entreModulos, a => Assert.Equal("Contracts", a.Para.Camada));

        SemViolacoesDe("ARQ-05", "ARQ-06");
    }

    [Fact]
    public void As_dependencias_entre_modulos_dos_novos_modulos_sao_exatamente_as_permitidas_pela_matriz()
    {
        var arestas = Projetos
            .SelectMany(p => p.Referencias.Select(r => (De: Classe(p), Para: RegrasDeArquitetura.Classificar(r, Matriz.Keys.ToList()))))
            .Where(a => a.De.Tipo == TipoDeProjeto.Modulo && a.Para.Tipo == TipoDeProjeto.Modulo && a.De.Modulo != a.Para.Modulo)
            .Select(a => (a.De.Modulo, a.Para.Modulo))
            .Distinct()
            .OrderBy(a => a.Item1).ThenBy(a => a.Item2)
            .ToList();

        Assert.Equal(
            new[] { ("Identity", "Audit"), ("Identity", "Tenancy"), ("Tenancy", "Audit") },
            arestas);
    }

    [Fact]
    public void Contracts_so_referenciam_Contracts_e_blocos_de_construcao()
    {
        Assert.Contains(Projetos, p => Classe(p) is { Tipo: TipoDeProjeto.Modulo, Camada: "Contracts" });

        SemViolacoesDe("CTR");
    }

    [Fact]
    public void DEP_4_e_DEP_6_o_SharedKernel_nao_referencia_projeto_algum()
    {
        var sharedKernel = Assert.Single(Projetos, p => p.Nome == "IaBimeta.SharedKernel");
        Assert.Empty(sharedKernel.Referencias);

        SemViolacoesDe("DEP-4");
    }

    [Fact]
    public void Nenhum_projeto_exceto_testes_referencia_um_host()
    {
        Assert.Contains(Projetos, p => Classe(p).Tipo == TipoDeProjeto.Host);

        SemViolacoesDe("HOST");
    }

    [Fact]
    public void Core_e_Contracts_nao_conhecem_EF_Npgsql_nem_ASP_NET_e_a_Presentation_nao_toca_no_banco()
    {
        var core = Projetos.Where(p => Classe(p) is { Tipo: TipoDeProjeto.Modulo, Camada: "Core" or "Contracts" }).ToList();
        var presentation = Projetos.Where(p => Classe(p) is { Tipo: TipoDeProjeto.Modulo, Camada: "Presentation" }).ToList();
        Assert.NotEmpty(core);
        Assert.NotEmpty(presentation);

        // A regra tem o que acusar: a Infrastructure usa EF e Npgsql, e a Presentation usa ASP.NET.
        Assert.Contains(Projetos, p => Classe(p) is { Camada: "Infrastructure" } && p.Pacotes.Any(x => x.StartsWith("Npgsql", StringComparison.Ordinal)));
        Assert.Contains(presentation, p => p.FrameworksEWeb.Contains("Microsoft.AspNetCore.App"));

        SemViolacoesDe("PKG-1", "PKG-2");
    }

    [Fact]
    public void Cada_projeto_de_banco_tem_o_proprio_esquema_e_a_infrastructure_de_um_modulo_nao_referencia_a_de_outro()
    {
        var infraestruturas = Projetos.Where(p => Classe(p) is { Tipo: TipoDeProjeto.Modulo, Camada: "Infrastructure" }).ToList();
        Assert.True(infraestruturas.Count >= 3);

        foreach (var infraestrutura in infraestruturas)
        {
            Assert.DoesNotContain(infraestrutura.Referencias, r => Classe(Projetos.Single(p => p.Nome == r)) is { Camada: "Infrastructure" });
        }
    }

    [Fact]
    public void A_matriz_e_coerente_sem_dependencia_inexistente_e_sem_ciclo_entre_modulos()
    {
        var violacoes = RegrasDeArquitetura.VerificarMatriz(Matriz);

        Assert.True(violacoes.Count == 0, string.Join(Environment.NewLine, violacoes));
        Assert.Contains("Identity", Matriz.Keys);
        Assert.Contains("Audit", Matriz.Keys);
        Assert.Empty(Matriz["Audit"]);
    }

    [Fact]
    public void Nenhuma_violacao_de_qualquer_regra_no_grafo_real()
    {
        Assert.True(Violacoes.Count == 0, string.Join(Environment.NewLine, Violacoes));
    }
}
