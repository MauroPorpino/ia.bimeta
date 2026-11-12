using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Tests.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Tests;

/// <summary>ADR 0036: chamador não humano (agente de IA, fluxo, chatbot): escopo de organização obrigatório, só dado público, agente nunca escreve, catálogo por escopo, correlação.</summary>
public class AgentScopeTests
{
    private const string Chave = "preco.consultar";

    private static Harness Sistema(out IntegrationConnection conexao, Guid? organizacaoDaConexao = null)
    {
        var h = new Harness(TestContexts.System());
        conexao = h.SeedConnection(organizacaoDaConexao);
        return h;
    }

    [Fact]
    public async Task Agente_com_escopo_dado_publico_e_leitura_executa_e_registra_a_correlacao()
    {
        var h = Sistema(out var conexao);
        h.SeedDefinition(conexao, Chave, Specs.Read(), callers: CapabilityCaller.Agent);
        var turno = Guid.NewGuid().ToString();

        var r = await h.Engine.ExecuteAsync(new CapabilityInvocation(Chave, Harness.P(new { numero = 5 }), CapabilityCaller.Agent, null, Guid.NewGuid(), turno, Harness.Scope));

        Assert.Equal(CapabilityExecutionStatus.Succeeded, r.Status);
        var registro = Assert.Single(h.Rows.Executions);
        Assert.Equal(turno, registro.CorrelationId);
        Assert.Equal(CapabilityCaller.Agent, registro.Caller);
    }

    [Theory]
    [InlineData(CapabilityCaller.Agent)]
    [InlineData(CapabilityCaller.Workflow)]
    [InlineData(CapabilityCaller.Chatbot)]
    public async Task Sem_escopo_de_organizacao_a_recusa_e_segura_e_nada_e_chamado(CapabilityCaller origem)
    {
        var h = Sistema(out var conexao);
        h.SeedDefinition(conexao, Chave, Specs.Read(), callers: origem);

        var r = await h.Engine.ExecuteAsync(new CapabilityInvocation(Chave, Harness.P(new { numero = 5 }), origem));

        Assert.Equal(CapabilityExecutionStatus.Rejected, r.Status);
        Assert.Equal("scope-required", r.Error!.Code);
        Assert.Empty(h.Connector.Calls);
        Assert.Empty(h.Rows.Executions);
    }

    [Fact]
    public async Task Conexao_de_outra_organizacao_e_recusada_a_do_tenant_inteiro_e_a_da_mesma_organizacao_passam()
    {
        var h = Sistema(out var daOutra, TestContexts.OrgB);
        var daMesma = h.SeedConnection(Harness.Scope, key: "mesma");
        var doTenant = h.SeedConnection(null, key: "tenant");
        h.SeedDefinition(daOutra, "outra.ler", Specs.Read(), callers: CapabilityCaller.Agent);
        h.SeedDefinition(daMesma, "mesma.ler", Specs.Read(), callers: CapabilityCaller.Agent);
        h.SeedDefinition(doTenant, "tenant.ler", Specs.Read(), callers: CapabilityCaller.Agent);

        Assert.Equal("scope-denied", (await h.Engine.ExecuteAsync(h.Read("outra.ler", new { numero = 1 }, CapabilityCaller.Agent))).Error!.Code);
        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await h.Engine.ExecuteAsync(h.Read("mesma.ler", new { numero = 1 }, CapabilityCaller.Agent))).Status);
        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await h.Engine.ExecuteAsync(h.Read("tenant.ler", new { numero = 1 }, CapabilityCaller.Agent))).Status);
        Assert.Equal(2, h.Connector.Calls.Count);
    }

    [Fact]
    public async Task Dado_do_cliente_nunca_e_executado_para_origem_nao_humana_mas_a_pessoa_pode()
    {
        var h = Sistema(out var conexao);
        h.SeedDefinition(conexao, "pedido.consultar", Specs.Read(), callers: CapabilityCaller.Agent | CapabilityCaller.Operator, dataClass: CapabilityDataClass.CustomerData);

        var r = await h.Engine.ExecuteAsync(h.Read("pedido.consultar", new { numero = 1 }, CapabilityCaller.Agent));

        Assert.Equal("data-class-denied", r.Error!.Code);
        Assert.Empty(h.Connector.Calls);

        var pessoa = h.As(TestContexts.User(TestContexts.TenantA, TestContexts.AllIntegration));
        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await pessoa.Engine.ExecuteAsync(pessoa.Read("pedido.consultar", new { numero = 1 }))).Status);
    }

    [Fact]
    public async Task O_agente_nunca_executa_escrita_nem_com_a_operacao_liberada_a_ele_no_banco()
    {
        var h = Sistema(out var conexao);
        // Estado que o cadastro não permite (a escrita liberada ao agente): o motor ainda recusa.
        h.SeedDefinition(conexao, "pedido.cancelar", Specs.Write(), callers: CapabilityCaller.Agent, requiresConfirmation: false);

        var r = await h.Engine.ExecuteAsync(h.Write("pedido.cancelar", new { numero = 1, motivo = "x" }, caller: CapabilityCaller.Agent));

        Assert.Equal("write-not-allowed", r.Error!.Code);
        Assert.Empty(h.Connector.Calls);
        Assert.Empty(h.Rows.Executions);
        Assert.Empty(await h.Engine.ListAsync(CapabilityCaller.Agent, default, Harness.Scope));
    }

    [Fact]
    public async Task O_catalogo_do_agente_e_por_escopo_classe_efeito_e_origem()
    {
        var h = Sistema(out var conexao);
        var daOutra = h.SeedConnection(TestContexts.OrgB, key: "outra");
        h.SeedDefinition(conexao, "publica.ok", Specs.Read(), callers: CapabilityCaller.Agent);
        h.SeedDefinition(conexao, "privada", Specs.Read(), callers: CapabilityCaller.Agent, dataClass: CapabilityDataClass.CustomerData);
        h.SeedDefinition(conexao, "so.pessoa", Specs.Read(), callers: CapabilityCaller.Operator);
        h.SeedDefinition(conexao, "escrita", Specs.Write(), callers: CapabilityCaller.Agent | CapabilityCaller.Workflow);
        h.SeedDefinition(daOutra, "outra.org", Specs.Read(), callers: CapabilityCaller.Agent);
        var desligada = h.SeedDefinition(conexao, "desligada", Specs.Read(), callers: CapabilityCaller.Agent);
        desligada.SetEnabled(false, h.Clock.GetUtcNow());

        var comEscopo = await h.Engine.ListAsync(CapabilityCaller.Agent, default, Harness.Scope);
        var semEscopo = await h.Engine.ListAsync(CapabilityCaller.Agent);

        Assert.Equal(["publica.ok"], comEscopo.Select(d => d.Key));
        Assert.All(comEscopo, d => Assert.Equal(CapabilityDataClass.Public, d.DataClass));
        Assert.Empty(semEscopo);
        Assert.Null(await h.Engine.GetAsync("privada", CapabilityCaller.Agent, default, Harness.Scope));
    }

    [Fact]
    public async Task Tenants_diferentes_nunca_veem_nem_executam_a_operacao_um_do_outro_mesmo_com_o_mesmo_escopo()
    {
        var a = Sistema(out var conexaoA);
        a.SeedDefinition(conexaoA, Chave, Specs.Read(), callers: CapabilityCaller.Agent);
        var b = a.As(TestContexts.System(TestContexts.TenantB));

        Assert.Empty(await b.Engine.ListAsync(CapabilityCaller.Agent, default, Harness.Scope));
        Assert.Equal("not-found", (await b.Engine.ExecuteAsync(b.Read(Chave, new { numero = 1 }, CapabilityCaller.Agent))).Error!.Code);
        Assert.Empty(a.Connector.Calls);
    }

    [Fact]
    public async Task Correlacao_invalida_e_recusada_e_parametro_de_sessao_e_reservado_a_fase_2()
    {
        var h = Sistema(out var conexao);
        h.SeedDefinition(conexao, Chave, Specs.Read(), callers: CapabilityCaller.Agent);
        var comSessao = Specs.Read() with { Parameters = [Specs.Param("numero", CapabilityParameterType.Integer, CapabilityParameterLocation.Path) with { Source = CapabilityParameterSource.Session }] };
        h.SeedDefinition(conexao, "fase.dois", comSessao, callers: CapabilityCaller.Agent);

        foreach (var ruim in new[] { "tem espaco", new string('a', 65), "ação" })
        {
            var r = await h.Engine.ExecuteAsync(new CapabilityInvocation(Chave, Harness.P(new { numero = 1 }), CapabilityCaller.Agent, null, null, ruim, Harness.Scope));
            Assert.Equal("validation-failed", r.Error!.Code);
        }

        Assert.Equal("unsupported", (await h.Engine.ExecuteAsync(h.Read("fase.dois", new { numero = 1 }, CapabilityCaller.Agent))).Error!.Code);
        Assert.DoesNotContain(await h.Engine.ListAsync(CapabilityCaller.Agent, default, Harness.Scope), d => d.Key == "fase.dois");
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Cadastro_exige_a_classe_para_origem_nao_humana_proibe_escrita_ao_agente_e_a_fase_2_ainda_nao_existe()
    {
        var h = new Harness();
        var conexao = h.SeedConnection();
        DefinitionRequest Pedido(string chave, CapabilityEffect efeito, CapabilityOperationSpec spec, CapabilityCaller[] origens, CapabilityDataClass? classe) =>
            new(conexao.Id, chave, "Op", null, efeito, null, origens, spec, null, classe);

        var semClasse = await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Pedido("a.b", CapabilityEffect.Read, Specs.Read(), [CapabilityCaller.Agent], null)));
        Assert.Contains(semClasse.Problems, p => p.StartsWith("dataClass", StringComparison.Ordinal));

        var escritaAgente = await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Pedido("a.c", CapabilityEffect.Write, Specs.Write(), [CapabilityCaller.Agent], CapabilityDataClass.Public)));
        Assert.Contains(escritaAgente.Problems, p => p.Contains("agente de IA nunca executa", StringComparison.Ordinal));

        var comSessao = Specs.Read() with { Parameters = [Specs.Param("numero", CapabilityParameterType.Integer, CapabilityParameterLocation.Path) with { Source = CapabilityParameterSource.Session }] };
        var fase2 = await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Pedido("a.d", CapabilityEffect.Read, comSessao, [CapabilityCaller.Operator], null)));
        Assert.Contains(fase2.Problems, p => p.Contains("fase 2", StringComparison.Ordinal));

        // A pessoa pode omitir a classe (padrão seguro CustomerData); o administrador classifica Public explicitamente.
        var padrao = await h.Defs.SaveDraftAsync(Pedido("a.e", CapabilityEffect.Read, Specs.Read(), [CapabilityCaller.Operator], null));
        var publica = await h.Defs.SaveDraftAsync(Pedido("a.f", CapabilityEffect.Read, Specs.Read(), [CapabilityCaller.Agent], CapabilityDataClass.Public));
        Assert.Equal(CapabilityDataClass.CustomerData, padrao.DataClass);
        Assert.Equal(CapabilityDataClass.Public, publica.DataClass);
    }

    [Fact]
    public async Task Execucoes_simultaneas_de_agentes_de_atendimentos_diferentes_nao_misturam_correlacao_nem_resultado()
    {
        var h = Sistema(out var conexao);
        h.SeedDefinition(conexao, Chave, Specs.Read(), callers: CapabilityCaller.Agent);
        h.Connector.Script = c => ScriptedConnector.Ok($$"""{"status":"S{{c.Values["numero"]}}","total":1}""");
        var turnos = Enumerable.Range(1, 20).Select(i => (Turno: $"turno-{i}", Numero: i)).ToList();

        var resultados = await Task.WhenAll(turnos.Select(t => Task.Run(async () =>
            (t, await h.Engine.ExecuteAsync(new CapabilityInvocation(Chave, Harness.P(new { numero = t.Numero }), CapabilityCaller.Agent, null, Guid.NewGuid(), t.Turno, Harness.Scope))))));

        Assert.All(resultados, x =>
        {
            Assert.Equal(CapabilityExecutionStatus.Succeeded, x.Item2.Status);
            Assert.Equal($"S{x.t.Numero}", x.Item2.Output!.Value.GetProperty("situacao").GetString());
        });
        Assert.Equal(20, h.Rows.Executions.Select(e => e.CorrelationId).Distinct().Count());
    }
}
