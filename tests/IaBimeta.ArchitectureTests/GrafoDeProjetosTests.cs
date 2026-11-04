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

        // O Conversations usa Tenancy (organização e unidade) e Audit (reenvio); a matriz também permite Identity, mas ele
        // não precisa: o RequestContext já chega pronto do host. O Channels usa Tenancy (organização, unidade e setor) e Audit;
        // a matriz também permite Conversations (o Receiver, depois do PRV-2) e Identity, que ainda não são necessários. Não há
        // aresta Conversations -> Channels nem ContactCenter -> Channels: a lista de unidades autorizadas chega ao Conversations
        // por uma porta dele, implementada pelo host sobre o Channels.Contracts (ADR 0008). O ContactCenter da etapa 4 usa
        // Tenancy (organização, unidade e setor), Identity (o usuário existe no tenant), Audit e Conversations (a atribuição e o
        // handoff IA/humano chamam o estado de atendimento pelos contratos dele; a aresta já estava na matriz). Aresta nova exige
        // ADR e atualização desta lista. O AiGateway (C1.4, ADR 0017) usa só o Audit (a auditoria da configuração de IA do tenant); a matriz também permite Identity e Tenancy,
        // que ele não precisa: o RequestContext chega pronto do host, e o tenant vem dele.
        Assert.Equal(
            new[]
            {
                ("AiGateway", "Audit"),
                ("Channels", "Audit"), ("Channels", "Tenancy"),
                ("ContactCenter", "Audit"), ("ContactCenter", "Conversations"), ("ContactCenter", "Identity"), ("ContactCenter", "Tenancy"),
                ("Conversations", "Audit"), ("Conversations", "Tenancy"),
                ("Identity", "Audit"), ("Identity", "Tenancy"),
                ("Tenancy", "Audit"),
            },
            arestas);
    }

    [Fact]
    public void O_Worker_e_o_Messaging_da_etapa_4_existem_e_so_Infrastructure_e_hosts_referenciam_a_implementacao_do_Messaging()
    {
        var nomes = Projetos.Select(p => p.Nome).ToHashSet();
        Assert.Contains("IaBimeta.Worker", nomes);
        Assert.Contains("IaBimeta.Messaging", nomes);

        // ARQ-20 tem o que verificar: a Infrastructure do Conversations e a do ContactCenter, e os dois hosts, o referenciam.
        var quemReferencia = Projetos.Where(p => p.Referencias.Contains("IaBimeta.Messaging")).Select(p => p.Nome).ToList();
        Assert.Contains("IaBimeta.Conversations.Infrastructure", quemReferencia);
        Assert.Contains("IaBimeta.ContactCenter.Infrastructure", quemReferencia);
        Assert.Contains("IaBimeta.Api", quemReferencia);
        Assert.Contains("IaBimeta.Worker", quemReferencia);

        // O Messaging é bloco: só referencia o SharedKernel (e nenhum módulo, DEP-4 e DEP-6).
        Assert.Equal(new[] { "IaBimeta.SharedKernel" }, Projetos.Single(p => p.Nome == "IaBimeta.Messaging").Referencias.ToArray());
        SemViolacoesDe("ARQ-20", "DEP-4", "HOST");
    }

    [Fact]
    public void O_modulo_Channels_existe_com_as_camadas_esperadas()
    {
        var nomes = Projetos.Select(p => p.Nome).ToHashSet();

        foreach (var esperado in new[]
        {
            "IaBimeta.Channels.Contracts", "IaBimeta.Channels.Core", "IaBimeta.Channels.Infrastructure", "IaBimeta.Channels.Presentation",
        })
        {
            Assert.Contains(esperado, nomes);
        }
    }

    [Fact]
    public void O_modulo_ContactCenter_ganhou_o_Contracts_so_para_o_RequestHandoff_do_Agents_e_nenhum_outro_modulo_o_referencia()
    {
        var nomes = Projetos.Select(p => p.Nome).ToHashSet();

        foreach (var esperado in new[]
        {
            "IaBimeta.ContactCenter.Contracts", "IaBimeta.ContactCenter.Core", "IaBimeta.ContactCenter.Infrastructure", "IaBimeta.ContactCenter.Presentation",
            "IaBimeta.Identity.Contracts",
        })
        {
            Assert.Contains(esperado, nomes);
        }

        // PRJ-2 e ADR 0017 (seção B): o Contracts do ContactCenter nasceu porque o Agents é o primeiro consumidor, e só tem o RequestHandoff. Fora do próprio
        // módulo (e dos testes), só o Agents o pode referenciar; nenhum host nem outro módulo.
        var consumidores = Projetos.Where(p => p.Referencias.Contains("IaBimeta.ContactCenter.Contracts")).ToList();
        Assert.NotEmpty(consumidores); // a regra tem o que verificar: o Core do próprio módulo o referencia

        foreach (var consumidor in consumidores)
        {
            var classe = Classe(consumidor);
            var permitido = classe.Tipo == TipoDeProjeto.Teste || (classe.Tipo == TipoDeProjeto.Modulo && classe.Modulo is "ContactCenter" or "Agents");
            Assert.True(permitido, $"{consumidor.Nome} não pode referenciar o IaBimeta.ContactCenter.Contracts (só o próprio módulo e o Agents).");
        }

        var contratos = Assert.Single(Projetos, p => p.Nome == "IaBimeta.ContactCenter.Contracts");
        Assert.Empty(contratos.Referencias);
    }

    [Fact]
    public void O_modulo_AiGateway_tem_Contracts_Core_e_Infrastructure_so_a_aresta_para_o_Audit_e_sem_provedor_no_grafo()
    {
        var nomes = Projetos.Select(p => p.Nome).ToHashSet();

        // ADR 0017 (seção B): Contracts e Core (C1.1 a C1.3) e a Infrastructure (C1.4). A Presentation nasce na C1.5; os adaptadores de provedor, na C4 e na C6.
        Assert.Contains("IaBimeta.AiGateway.Contracts", nomes);
        Assert.Contains("IaBimeta.AiGateway.Core", nomes);
        Assert.Contains("IaBimeta.AiGateway.Infrastructure", nomes);
        Assert.DoesNotContain("IaBimeta.AiGateway.Presentation", nomes);

        // Só o Audit entre os outros módulos (a auditoria da configuração do tenant); nenhum outro módulo, nenhum adaptador. A matriz não mudou.
        var doModulo = Projetos.Where(p => p.Nome.StartsWith("IaBimeta.AiGateway.", StringComparison.Ordinal) && !p.Nome.EndsWith("Tests", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, doModulo.Count);
        var permitidas = new Dictionary<string, string[]>
        {
            ["IaBimeta.AiGateway.Contracts"] = Array.Empty<string>(),
            ["IaBimeta.AiGateway.Core"] = new[] { "IaBimeta.SharedKernel", "IaBimeta.AiGateway.Contracts", "IaBimeta.Audit.Contracts" },
            ["IaBimeta.AiGateway.Infrastructure"] = new[] { "IaBimeta.SharedKernel", "IaBimeta.AiGateway.Contracts", "IaBimeta.AiGateway.Core" },
        };
        foreach (var projeto in doModulo)
        {
            Assert.All(projeto.Referencias, r => Assert.True(permitidas[projeto.Nome].Contains(r), $"{projeto.Nome} -> {r}"));
        }

        // A Infrastructure não referencia a Infrastructure de outro módulo nem adaptador; o grafo geral já proíbe, e aqui fica explícito.
        var infra = Assert.Single(doModulo, p => p.Nome == "IaBimeta.AiGateway.Infrastructure");
        Assert.DoesNotContain(infra.Referencias, r => r.EndsWith(".Infrastructure", StringComparison.Ordinal) || r.Contains("Adapters", StringComparison.Ordinal));

        Assert.Equal(new[] { "Audit", "Identity", "Tenancy" }, Matriz["AiGateway"].OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void O_modulo_Conversations_da_etapa_3_existe_com_as_camadas_esperadas()
    {
        var nomes = Projetos.Select(p => p.Nome).ToHashSet();

        foreach (var esperado in new[]
        {
            "IaBimeta.Conversations.Contracts", "IaBimeta.Conversations.Core", "IaBimeta.Conversations.Infrastructure", "IaBimeta.Conversations.Presentation",
        })
        {
            Assert.Contains(esperado, nomes);
        }
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
