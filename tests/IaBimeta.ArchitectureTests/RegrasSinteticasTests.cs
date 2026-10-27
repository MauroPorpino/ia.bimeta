namespace IaBimeta.ArchitectureTests;

/// <summary>
/// Prova das próprias regras: grafos montados à mão, com uma violação cada, precisam ser acusados pela regra certa, e
/// um grafo bom não pode ser acusado. Sem isto, uma regra quebrada passaria em silêncio.
/// </summary>
public class RegrasSinteticasTests
{
    private static readonly IReadOnlyDictionary<string, string[]> Matriz = new Dictionary<string, string[]>
    {
        ["Audit"] = Array.Empty<string>(),
        ["Tenancy"] = new[] { "Audit" },
        ["Identity"] = new[] { "Tenancy", "Audit" },
        ["Conversations"] = new[] { "Identity", "Tenancy", "Audit" },
    };

    private static ProjetoDaSolucao P(string nome, string[]? referencias = null, string[]? pacotes = null, string[]? frameworks = null) =>
        new(nome, referencias ?? Array.Empty<string>(), pacotes ?? Array.Empty<string>(), frameworks ?? Array.Empty<string>());

    private static List<ProjetoDaSolucao> GrafoBom() => new()
    {
        P("IaBimeta.SharedKernel"),
        P("IaBimeta.Audit.Contracts", new[] { "IaBimeta.SharedKernel" }),
        P("IaBimeta.Audit.Core", new[] { "IaBimeta.SharedKernel", "IaBimeta.Audit.Contracts" }),
        P("IaBimeta.Tenancy.Contracts", new[] { "IaBimeta.SharedKernel" }),
        P("IaBimeta.Tenancy.Core", new[] { "IaBimeta.SharedKernel", "IaBimeta.Tenancy.Contracts", "IaBimeta.Audit.Contracts" }),
        P("IaBimeta.Tenancy.Infrastructure", new[] { "IaBimeta.Tenancy.Core" }, new[] { "Npgsql.EntityFrameworkCore.PostgreSQL" }),
        P("IaBimeta.Tenancy.Presentation", new[] { "IaBimeta.Tenancy.Core" }, frameworks: new[] { "Microsoft.AspNetCore.App" }),
        P("IaBimeta.Identity.Core", new[] { "IaBimeta.SharedKernel", "IaBimeta.Tenancy.Contracts", "IaBimeta.Audit.Contracts" }),
        P("IaBimeta.Api", new[] { "IaBimeta.Tenancy.Infrastructure", "IaBimeta.Tenancy.Presentation", "IaBimeta.Identity.Core" }),
        P("IaBimeta.IntegrationTests", new[] { "IaBimeta.Api", "IaBimeta.Tenancy.Infrastructure" }),
    };

    private static List<Violacao> Verificar(List<ProjetoDaSolucao> projetos) => RegrasDeArquitetura.Verificar(projetos, Matriz).ToList();

    private static List<ProjetoDaSolucao> Com(ProjetoDaSolucao novo)
    {
        var grafo = GrafoBom().Where(p => p.Nome != novo.Nome).ToList();
        grafo.Add(novo);
        return grafo;
    }

    [Fact]
    public void Um_grafo_correto_nao_tem_violacao()
    {
        Assert.Empty(Verificar(GrafoBom()));
    }

    [Fact]
    public void ARQ_04_acusa_modulo_que_referencia_a_Infrastructure_de_outro_modulo()
    {
        var grafo = Com(P("IaBimeta.Identity.Core", new[] { "IaBimeta.Tenancy.Infrastructure" }));

        Assert.Contains(Verificar(grafo), v => v.Regra == "ARQ-04" && v.Detalhe.Contains("Identity.Core -> IaBimeta.Tenancy.Infrastructure"));
    }

    [Fact]
    public void ARQ_04_acusa_o_Core_que_referencia_a_Presentation_do_proprio_modulo()
    {
        var grafo = Com(P("IaBimeta.Tenancy.Core", new[] { "IaBimeta.Tenancy.Presentation" }));

        Assert.Contains(Verificar(grafo), v => v.Regra == "ARQ-04");
    }

    [Fact]
    public void ARQ_05_acusa_referencia_entre_modulos_que_nao_e_a_Contracts()
    {
        var grafo = Com(P("IaBimeta.Identity.Core", new[] { "IaBimeta.Tenancy.Core" }));

        Assert.Contains(Verificar(grafo), v => v.Regra == "ARQ-05" && v.Detalhe.Contains("Identity.Core -> IaBimeta.Tenancy.Core"));
    }

    [Fact]
    public void ARQ_06_acusa_aresta_entre_modulos_que_nao_esta_na_matriz()
    {
        // O Tenancy só pode usar o Audit: depender do Identity.Contracts é uma aresta nova.
        var grafo = GrafoBom();
        grafo.Add(P("IaBimeta.Identity.Contracts", new[] { "IaBimeta.SharedKernel" }));
        grafo = grafo.Where(p => p.Nome != "IaBimeta.Tenancy.Core").ToList();
        grafo.Add(P("IaBimeta.Tenancy.Core", new[] { "IaBimeta.Identity.Contracts" }));

        var violacoes = Verificar(grafo);

        Assert.Contains(violacoes, v => v.Regra == "ARQ-06" && v.Detalhe.Contains("Tenancy.Core -> IaBimeta.Identity.Contracts"));
        Assert.DoesNotContain(violacoes, v => v.Regra == "ARQ-05");
    }

    [Fact]
    public void Contracts_que_referencia_o_Core_e_acusado()
    {
        var grafo = Com(P("IaBimeta.Tenancy.Contracts", new[] { "IaBimeta.SharedKernel", "IaBimeta.Tenancy.Core" }));

        Assert.Contains(Verificar(grafo), v => v.Regra == "CTR");
    }

    [Fact]
    public void SharedKernel_que_referencia_um_modulo_e_acusado()
    {
        var grafo = Com(P("IaBimeta.SharedKernel", new[] { "IaBimeta.Audit.Contracts" }));

        var violacoes = Verificar(grafo);

        Assert.Contains(violacoes, v => v.Regra == "DEP-4");
        Assert.Contains(violacoes, v => v.Regra == "CICLO");
    }

    [Fact]
    public void Projeto_que_nao_e_teste_nem_host_referenciando_um_host_e_acusado()
    {
        var grafo = Com(P("IaBimeta.Identity.Core", new[] { "IaBimeta.Api" }));

        Assert.Contains(Verificar(grafo), v => v.Regra == "HOST");
    }

    [Fact]
    public void Teste_pode_referenciar_host_e_Infrastructure()
    {
        Assert.DoesNotContain(Verificar(GrafoBom()), v => v.Regra is "HOST" or "ARQ-04");
    }

    [Fact]
    public void Adaptador_que_referencia_o_Core_do_dono_e_acusado()
    {
        var grafo = GrafoBom();
        grafo.Add(P("IaBimeta.Adapters.WhatsApp", new[] { "IaBimeta.Tenancy.Core" }));

        var violacoes = Verificar(grafo);

        Assert.Contains(violacoes, v => v.Regra == "DEP-2");
    }

    [Fact]
    public void Ciclo_entre_projetos_e_acusado_com_o_caminho()
    {
        var grafo = GrafoBom();
        grafo.RemoveAll(p => p.Nome is "IaBimeta.Audit.Core" or "IaBimeta.Audit.Contracts");
        grafo.Add(P("IaBimeta.Audit.Contracts", new[] { "IaBimeta.Audit.Core" }));
        grafo.Add(P("IaBimeta.Audit.Core", new[] { "IaBimeta.Audit.Contracts" }));

        var ciclo = Assert.Single(Verificar(grafo), v => v.Regra == "CICLO");

        Assert.Contains("Audit.Contracts", ciclo.Detalhe);
        Assert.Contains("Audit.Core", ciclo.Detalhe);
    }

    [Fact]
    public void Projeto_sem_classificacao_e_referencia_a_projeto_inexistente_sao_acusados()
    {
        var grafo = GrafoBom();
        grafo.Add(P("IaBimeta.Qualquer"));
        grafo.Add(P("IaBimeta.Api.Extra", new[] { "IaBimeta.Nao.Existe" }));

        var violacoes = Verificar(grafo);

        Assert.Contains(violacoes, v => v.Regra == "CLASSIFICACAO" && v.Detalhe.Contains("IaBimeta.Qualquer"));
        Assert.Contains(violacoes, v => v.Regra == "REFERENCIA" && v.Detalhe.Contains("IaBimeta.Nao.Existe"));
    }

    [Fact]
    public void Modulo_que_nao_esta_na_matriz_fica_sem_classificacao()
    {
        var grafo = GrafoBom();
        grafo.Add(P("IaBimeta.Invasor.Core"));

        Assert.Contains(Verificar(grafo), v => v.Regra == "CLASSIFICACAO" && v.Detalhe.Contains("Invasor.Core"));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Relational")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Npgsql")]
    public void Core_e_Contracts_com_pacote_de_banco_sao_acusados(string pacote)
    {
        foreach (var nome in new[] { "IaBimeta.Tenancy.Core", "IaBimeta.Tenancy.Contracts" })
        {
            var grafo = Com(P(nome, new[] { "IaBimeta.SharedKernel" }, new[] { pacote }));

            Assert.Contains(Verificar(grafo), v => v.Regra == "PKG-1" && v.Detalhe.Contains(nome));
        }
    }

    [Fact]
    public void Core_com_ASP_NET_e_acusado()
    {
        var grafo = Com(P("IaBimeta.Tenancy.Core", new[] { "IaBimeta.SharedKernel" }, frameworks: new[] { "Microsoft.AspNetCore.App" }));

        Assert.Contains(Verificar(grafo), v => v.Regra == "PKG-1");
    }

    [Fact]
    public void Presentation_com_pacote_de_banco_e_acusada_mas_a_Infrastructure_pode()
    {
        var grafo = Com(P("IaBimeta.Tenancy.Presentation", new[] { "IaBimeta.Tenancy.Core" }, new[] { "Npgsql" }, new[] { "Microsoft.AspNetCore.App" }));

        Assert.Contains(Verificar(grafo), v => v.Regra == "PKG-2");
        Assert.DoesNotContain(Verificar(GrafoBom()), v => v.Regra is "PKG-1" or "PKG-2");
    }

    // A própria matriz

    [Fact]
    public void Matriz_com_dependencia_inexistente_e_com_ciclo_e_acusada()
    {
        var inexistente = new Dictionary<string, string[]> { ["A"] = new[] { "B" } };
        var ciclo = new Dictionary<string, string[]> { ["A"] = new[] { "B" }, ["B"] = new[] { "C" }, ["C"] = new[] { "A" } };
        var consigoMesmo = new Dictionary<string, string[]> { ["A"] = new[] { "A" } };

        Assert.Contains(RegrasDeArquitetura.VerificarMatriz(inexistente), v => v.Detalhe.Contains("não é um módulo"));
        Assert.Contains(RegrasDeArquitetura.VerificarMatriz(ciclo), v => v.Detalhe.Contains("ciclo"));
        Assert.Contains(RegrasDeArquitetura.VerificarMatriz(consigoMesmo), v => v.Detalhe.Contains("si mesmo"));
        Assert.Empty(RegrasDeArquitetura.VerificarMatriz(Matriz));
    }
}
