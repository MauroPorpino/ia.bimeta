using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Domain;
using IaBimeta.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Workflows;

/// <summary>
/// A operação dos fluxos pela API REAL com PostgreSQL real (ADR 0038 e 0037 §7): ativações (nascem desligadas, ligar exige versão publicada, permissões distintas, concorrência otimista, isolamento de tenant e de
/// organização), histórico de execução e métricas (só identificadores, estados e códigos) e o expurgo por retenção (só instâncias ENCERRADAS, com passos e efeitos, por empresa).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FluxoOperacaoApiTests(PostgresFixture banco)
{
    private const string Simples = """
        {"schemaVersion":1,"start":"oi","nodes":[
          {"id":"oi","type":"message","config":{"text":"Olá!"}},
          {"id":"fim","type":"end"}],
         "edges":[{"from":"oi","port":"next","to":"fim"}],"variables":[]}
        """;

    private sealed class Cenario : IAsyncDisposable
    {
        public required ApiServer Api { get; init; }

        public required string Admin { get; init; }

        public required IaBimeta.Tenancy.Contracts.TenantInfo Tenant { get; init; }

        public required IaBimeta.Tenancy.Application.OrganizationView Org { get; init; }

        public async ValueTask DisposeAsync() => await Api.DisposeAsync();

        public async Task<(HttpStatusCode Status, JsonNode? Corpo)> Chamar(string? token, HttpMethod metodo, string caminho, object? corpo = null)
        {
            using var resposta = await Api.EnviarAsync(token, metodo, caminho, corpo);
            var texto = await resposta.Content.ReadAsStringAsync();
            return (resposta.StatusCode, string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto));
        }

        public Task<(HttpStatusCode Status, JsonNode? Corpo)> ComoAdmin(HttpMethod metodo, string caminho, object? corpo = null) => Chamar(Admin, metodo, caminho, corpo);
    }

    private async Task<Cenario> IniciarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var org = await banco.CriarOrganizacaoAsync(tenant, $"Org {prefixo}");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        return new Cenario { Api = api, Admin = await api.LoginAsync(admin), Tenant = tenant, Org = org };
    }

    private static async Task<Guid> RascunhoAsync(Cenario c, string chave, bool publicar)
    {
        var (_, salvo) = await c.ComoAdmin(HttpMethod.Post, "/workflows/definitions", new { organizationId = c.Org.Id, key = chave, name = "Fluxo " + chave, graph = JsonNode.Parse(Simples) });
        var id = salvo!["workflow"]!["id"]!.GetValue<Guid>();
        if (publicar)
        {
            Assert.Equal(HttpStatusCode.OK, (await c.ComoAdmin(HttpMethod.Post, $"/workflows/definitions/{id}/publish")).Status);
        }

        return id;
    }

    private static object Ativacao(Cenario c, string chave, int parcela = 100, int prioridade = 0, string falha = "Agent", Guid? canal = null, int? revisao = null) =>
        new { organizationId = c.Org.Id, businessUnitId = (Guid?)null, channelInstanceId = canal, workflowKey = chave, priority = prioridade, rolloutPercent = parcela, onFailure = falha, expectedRevision = revisao };

    // ------------------------------------------------------------ ativações

    [Fact]
    public async Task A_ativacao_nasce_desligada_so_liga_com_versao_publicada_e_a_edicao_exige_a_revisao()
    {
        await using var c = await IniciarAsync("op-ativacao");
        await RascunhoAsync(c, "publicado", publicar: true);
        await RascunhoAsync(c, "so-rascunho", publicar: false);
        Guid? canal = null;

        var (st, criada) = await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Ativacao(c, "publicado", 50, 3, "Handoff", canal));
        Assert.Equal(HttpStatusCode.Created, st);
        var id = criada!["id"]!.GetValue<Guid>();
        Assert.False(criada["enabled"]!.GetValue<bool>());
        Assert.Equal(50, criada["rolloutPercent"]!.GetValue<int>());
        Assert.Equal("Handoff", criada["onFailure"]!.GetValue<string>());

        var (_, ligada) = await c.ComoAdmin(HttpMethod.Post, $"/workflows/activations/{id}/enable");
        Assert.True(ligada!["enabled"]!.GetValue<bool>());
        var (_, desligada) = await c.ComoAdmin(HttpMethod.Post, $"/workflows/activations/{id}/disable");
        Assert.False(desligada!["enabled"]!.GetValue<bool>());

        // Um fluxo que só tem rascunho pode ter ativação, mas NÃO liga.
        var (_, semVersao) = await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Ativacao(c, "so-rascunho"));
        var idSem = semVersao!["id"]!.GetValue<Guid>();
        var (stLigar, erro) = await c.ComoAdmin(HttpMethod.Post, $"/workflows/activations/{idSem}/enable");
        Assert.Equal(HttpStatusCode.Conflict, stLigar);
        Assert.Equal("no-published-version", erro!["code"]!.GetValue<string>());

        // Edição: a revisão esperada evita sobrescrever o que não se viu.
        var (_, lida) = (await c.ComoAdmin(HttpMethod.Get, "/workflows/activations")).Corpo!.AsArray().Where(a => a!["id"]!.GetValue<Guid>() == id).Select(a => (HttpStatusCode.OK, a)).Single();
        var revisao = lida!["revision"]!.GetValue<int>();
        var (stVelha, velha) = await c.ComoAdmin(HttpMethod.Put, $"/workflows/activations/{id}", Ativacao(c, "publicado", 10, revisao: revisao + 9));
        Assert.Equal(HttpStatusCode.Conflict, stVelha);
        Assert.Equal("changed", velha!["code"]!.GetValue<string>());
        var (stOk, editada) = await c.ComoAdmin(HttpMethod.Put, $"/workflows/activations/{id}", Ativacao(c, "publicado", 10, revisao: revisao));
        Assert.Equal(HttpStatusCode.OK, stOk);
        Assert.Equal(10, editada!["rolloutPercent"]!.GetValue<int>());
        Assert.Equal(revisao + 1, editada["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Dados_invalidos_da_ativacao_sao_recusados_sem_ecoar_o_conteudo()
    {
        await using var c = await IniciarAsync("op-ativ-invalida");
        await RascunhoAsync(c, "valido", publicar: true);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Ativacao(c, "valido", 101))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Ativacao(c, "valido", -1))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Ativacao(c, "inexistente"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Ativacao(c, "valido", canal: Guid.Empty))).Status);
        var (_, lista) = await c.ComoAdmin(HttpMethod.Get, "/workflows/activations");
        Assert.Empty(lista!.AsArray());
    }

    [Fact]
    public async Task Ativar_e_do_administrador_o_gerente_so_le_e_quem_nao_tem_permissao_recebe_403()
    {
        await using var c = await IniciarAsync("op-ativ-papeis");
        await RascunhoAsync(c, "papeis", publicar: true);
        var (_, criada) = await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Ativacao(c, "papeis"));
        var id = criada!["id"]!.GetValue<Guid>();

        var gerente = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.Manager));
        Assert.Equal(HttpStatusCode.OK, (await c.Chamar(gerente, HttpMethod.Get, "/workflows/activations")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(gerente, HttpMethod.Post, "/workflows/activations", Ativacao(c, "papeis", prioridade: 1))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(gerente, HttpMethod.Post, $"/workflows/activations/{id}/enable")).Status);

        var comum = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.User));
        foreach (var caminho in new[] { "/workflows/activations", "/workflows/instances", "/workflows/metrics", $"/workflows/instances/{Guid.NewGuid()}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(comum, HttpMethod.Get, caminho)).Status);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Chamar(null, HttpMethod.Get, "/workflows/activations")).Status);
    }

    [Fact]
    public async Task Ativacoes_sao_isoladas_por_tenant_e_por_organizacao()
    {
        await using var a = await IniciarAsync("op-ativ-iso-a");
        await RascunhoAsync(a, "isolada", publicar: true);
        var (_, criada) = await a.ComoAdmin(HttpMethod.Post, "/workflows/activations", Ativacao(a, "isolada"));
        var id = criada!["id"]!.GetValue<Guid>();

        await using var b = await IniciarAsync("op-ativ-iso-b");
        Assert.Empty((await b.ComoAdmin(HttpMethod.Get, "/workflows/activations")).Corpo!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await b.ComoAdmin(HttpMethod.Post, $"/workflows/activations/{id}/enable")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await b.ComoAdmin(HttpMethod.Put, $"/workflows/activations/{id}", Ativacao(b, "isolada", revisao: 1))).Status);

        // O administrador de OUTRA organização do mesmo tenant não vê nem liga a ativação.
        var outraOrg = await banco.CriarOrganizacaoAsync(a.Tenant, "Outra org");
        var adminOutra = await a.Api.LoginAsync(await banco.CriarUsuarioAsync(a.Tenant, outraOrg.Id, Role.OrganizationAdmin));
        Assert.Empty((await a.Chamar(adminOutra, HttpMethod.Get, "/workflows/activations")).Corpo!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Chamar(adminOutra, HttpMethod.Post, $"/workflows/activations/{id}/enable")).Status);
    }

    // ------------------------------------------------------------ histórico e métricas

    private static async Task SemearAsync(PostgresFixture banco, Cenario c)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant));
        var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var agora = DateTimeOffset.UtcNow;

        FlowInstance Nova(FlowStatus status, string no, string? falha = null, int versao = 1)
        {
            var i = FlowInstance.Create(c.Tenant.TenantId, Guid.NewGuid(), c.Org.Id, Guid.NewGuid(), "semeado", versao, null, OnFailurePolicy.Agent, "v1:x", "k1", agora.AddMinutes(-5));
            i.Apply(new FlowState(status, no, new Dictionary<string, string>(), 0, 2, null, null, falha), "v1:x", "k1", 1, false, agora);
            return i;
        }

        var concluida = Nova(FlowStatus.Completed, "fim");
        var transferida = Nova(FlowStatus.HandedOff, "atendente");
        var falhou = Nova(FlowStatus.Failed, "consulta", "step-limit");
        var esperando = Nova(FlowStatus.WaitingInput, "menu");
        db.Instances.AddRange(concluida, transferida, falhou, esperando);
        foreach (var (instancia, nos) in new[] { (concluida, new[] { "ola", "menu", "menu", "fim" }), (transferida, new[] { "ola", "menu", "atendente" }), (falhou, new[] { "ola", "consulta" }), (esperando, new[] { "ola", "menu" }) })
        {
            for (var n = 0; n < nos.Length; n++)
            {
                db.Steps.Add(FlowStepRecord.Create(c.Tenant.TenantId, instancia.Id, n + 1, nos[n], n == nos.Length - 1 ? "done" : "pass", agora));
            }
        }

        db.Effects.Add(FlowEffectRecord.Create(c.Tenant.TenantId, Guid.NewGuid(), concluida.Id, "1:0", 1, 0, EffectKind.SendMessage, "ola", "v1:segredo-cifrado", "k1", agora));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task O_historico_filtra_pagina_e_o_detalhe_mostra_so_identificadores_estados_e_codigos()
    {
        await using var c = await IniciarAsync("op-historico");
        await SemearAsync(banco, c);

        var (_, todas) = await c.ComoAdmin(HttpMethod.Get, "/workflows/instances?key=semeado");
        Assert.Equal(4, todas!["total"]!.GetValue<int>());
        var (_, falhas) = await c.ComoAdmin(HttpMethod.Get, "/workflows/instances?status=Failed");
        var falha = Assert.Single(falhas!["items"]!.AsArray());
        Assert.Equal("step-limit", falha!["failureCode"]!.GetValue<string>());
        var (_, pagina) = await c.ComoAdmin(HttpMethod.Get, "/workflows/instances?take=3&skip=3");
        Assert.Single(pagina!["items"]!.AsArray());
        Assert.Equal(4, pagina["total"]!.GetValue<int>());

        var concluida = todas["items"]!.AsArray().Single(i => i!["status"]!.GetValue<string>() == "Completed")!;
        var (st, detalhe) = await c.ComoAdmin(HttpMethod.Get, $"/workflows/instances/{concluida["id"]!.GetValue<Guid>()}");
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal(["ola", "menu", "menu", "fim"], detalhe!["steps"]!.AsArray().Select(s => s!["nodeId"]!.GetValue<string>()));
        var texto = detalhe.ToJsonString();
        Assert.DoesNotContain("segredo-cifrado", texto);
        Assert.DoesNotContain("cipher", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("variables", texto, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SendMessage", detalhe["effects"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task As_metricas_contam_por_fluxo_e_versao_e_o_funil_conta_cada_instancia_uma_vez_por_no()
    {
        await using var c = await IniciarAsync("op-metricas");
        await SemearAsync(banco, c);

        var (_, linhas) = await c.ComoAdmin(HttpMethod.Get, "/workflows/metrics?days=7");
        var linha = Assert.Single(linhas!.AsArray())!;
        Assert.Equal((4, 1, 1, 1, 0, 1, 0), (linha["started"]!.GetValue<int>(), linha["active"]!.GetValue<int>(), linha["completed"]!.GetValue<int>(), linha["handedOff"]!.GetValue<int>(), linha["delegated"]!.GetValue<int>(), linha["failed"]!.GetValue<int>(), linha["cancelled"]!.GetValue<int>()));

        var (_, funil) = await c.ComoAdmin(HttpMethod.Get, "/workflows/metrics/funnel?key=semeado&version=1&days=7");
        var porNo = funil!.AsArray().ToDictionary(n => n!["nodeId"]!.GetValue<string>(), n => n!["visits"]!.GetValue<int>());
        Assert.Equal(4, porNo["ola"]);
        Assert.Equal(3, porNo["menu"]); // a instância concluída passou duas vezes por "menu" e conta uma só
        Assert.Equal(1, porNo["atendente"]);
        Assert.Equal(1, porNo["fim"]);
    }

    [Fact]
    public async Task Historico_e_metricas_sao_isolados_por_tenant_e_por_organizacao()
    {
        await using var a = await IniciarAsync("op-hist-iso-a");
        await SemearAsync(banco, a);
        var (_, doA) = await a.ComoAdmin(HttpMethod.Get, "/workflows/instances");
        var idDoA = doA!["items"]![0]!["id"]!.GetValue<Guid>();

        await using var b = await IniciarAsync("op-hist-iso-b");
        Assert.Equal(0, (await b.ComoAdmin(HttpMethod.Get, "/workflows/instances")).Corpo!["total"]!.GetValue<int>());
        Assert.Empty((await b.ComoAdmin(HttpMethod.Get, "/workflows/metrics")).Corpo!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await b.ComoAdmin(HttpMethod.Get, $"/workflows/instances/{idDoA}")).Status);

        var outraOrg = await banco.CriarOrganizacaoAsync(a.Tenant, "Outra org");
        var adminOutra = await a.Api.LoginAsync(await banco.CriarUsuarioAsync(a.Tenant, outraOrg.Id, Role.OrganizationAdmin));
        Assert.Equal(0, (await a.Chamar(adminOutra, HttpMethod.Get, "/workflows/instances")).Corpo!["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Chamar(adminOutra, HttpMethod.Get, $"/workflows/instances/{idDoA}")).Status);
        Assert.Empty((await a.Chamar(adminOutra, HttpMethod.Get, "/workflows/metrics")).Corpo!.AsArray());
    }

    // ------------------------------------------------------------ retenção

    [Fact]
    public async Task O_expurgo_apaga_so_instancias_encerradas_antigas_com_passos_e_efeitos_e_respeita_a_empresa()
    {
        var tenantA = await banco.RegistrarTenantAsync("op-retencao-a");
        var tenantB = await banco.RegistrarTenantAsync("op-retencao-b");
        var orgA = await banco.CriarOrganizacaoAsync(tenantA, "Org A");
        var orgB = await banco.CriarOrganizacaoAsync(tenantB, "Org B");
        var agora = DateTimeOffset.UtcNow;
        var corte = agora.AddDays(-30);

        async Task<(Guid Velha, Guid Ativa, Guid Recente)> SemearAsync(IaBimeta.Tenancy.Contracts.TenantInfo tenant, Guid org)
        {
            await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
            var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            FlowInstance Nova(FlowStatus status, DateTimeOffset fim)
            {
                var i = FlowInstance.Create(tenant.TenantId, Guid.NewGuid(), org, Guid.NewGuid(), "retencao", 1, null, OnFailurePolicy.Agent, "v1:x", "k1", fim.AddDays(-1));
                i.Apply(new FlowState(status, "fim", new Dictionary<string, string>(), 0, 2), "v1:x", "k1", 1, false, fim);
                return i;
            }

            var velha = Nova(FlowStatus.Completed, agora.AddDays(-45));
            var ativa = Nova(FlowStatus.WaitingInput, agora.AddDays(-45));
            var recente = Nova(FlowStatus.Completed, agora.AddDays(-2));
            db.Instances.AddRange(velha, ativa, recente);
            foreach (var i in new[] { velha, ativa, recente })
            {
                db.Steps.Add(FlowStepRecord.Create(tenant.TenantId, i.Id, 1, "ola", "pass", agora));
                db.Effects.Add(FlowEffectRecord.Create(tenant.TenantId, Guid.NewGuid(), i.Id, "1:0", 1, 0, EffectKind.SendMessage, "ola", string.Empty, string.Empty, agora));
            }

            // Uma linha órfã (a instância dela já foi expurgada) também é recolhida.
            db.Effects.Add(FlowEffectRecord.Create(tenant.TenantId, Guid.NewGuid(), Guid.NewGuid(), "9:0", 9, 0, EffectKind.Finish, "fim", string.Empty, string.Empty, agora));
            await db.SaveChangesAsync();
            return (velha.Id, ativa.Id, recente.Id);
        }

        var a = await SemearAsync(tenantA, orgA.Id);
        var b = await SemearAsync(tenantB, orgB.Id);

        var fabrica = banco.ServicosComFluxos.GetRequiredService<IServiceScopeFactory>();
        var passada = new RetentionPass(tenantA.TenantId.Value, [], 30, corte, "empresa");
        IRetentionTask[] tarefas = [new FlowEffectRetentionTask(fabrica), new FlowStepRetentionTask(fabrica), new FlowInstanceRetentionTask(fabrica)];

        // Dry-run: conta e não apaga.
        var seco = await tarefas[2].RunAsync(passada, dryRun: true);
        Assert.Equal(1, seco.Eligible);
        Assert.Equal(0, seco.Deleted);

        foreach (var tarefa in tarefas)
        {
            await tarefa.RunAsync(passada, dryRun: false);
        }

        async Task<(HashSet<Guid> Instancias, int Passos, int Efeitos)> EstadoAsync(IaBimeta.Tenancy.Contracts.TenantInfo tenant)
        {
            await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
            var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            return ((await db.Instances.AsNoTracking().Select(i => i.Id).ToListAsync()).ToHashSet(), await db.Steps.AsNoTracking().CountAsync(), await db.Effects.AsNoTracking().CountAsync());
        }

        var (instanciasA, passosA, efeitosA) = await EstadoAsync(tenantA);
        Assert.DoesNotContain(a.Velha, instanciasA); // encerrada e antiga: sai
        Assert.Contains(a.Ativa, instanciasA); // ATIVA nunca expira por prazo, por antiga que seja
        Assert.Contains(a.Recente, instanciasA); // encerrada mas recente: fica
        Assert.Equal(2, passosA); // só os passos da velha saíram
        Assert.Equal(2, efeitosA); // os efeitos da velha e o órfão saíram

        var (instanciasB, passosB, efeitosB) = await EstadoAsync(tenantB);
        Assert.Contains(b.Velha, instanciasB); // outra empresa: intacta nesta passada
        Assert.Equal((3, 4), (passosB, efeitosB));
    }

    // ------------------------------------------------------------ escopo da ativação (prontidão da V1)

    [Fact]
    public async Task O_escopo_da_ativacao_exige_organizacao_unidade_e_canal_existentes_no_tenant_e_coerentes_entre_si()
    {
        var conv = await CenarioDeConversas.CriarAsync(banco, "op-escopo", canaisReais: true);
        var admin = await banco.CriarAdministradorDoTenantAsync(conv.Tenant);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(admin);
        var c = new Cenario { Api = api, Admin = token, Tenant = conv.Tenant, Org = new IaBimeta.Tenancy.Application.OrganizationView(conv.OrganizacaoId, "Org", DateTimeOffset.UtcNow) };
        await RascunhoAsync(c, "escopo", publicar: true);

        var unidade = await conv.CriarUnidadeAsync(conv.OrganizacaoId, "Matriz", true);
        var outraUnidade = await conv.CriarUnidadeAsync(conv.OrganizacaoId, "Filial");
        var canal = await conv.CriarCanalAsync([unidade], unidade);
        object Corpo(Guid org, Guid? un, Guid? ca) => new { organizationId = org, businessUnitId = un, channelInstanceId = ca, workflowKey = "escopo", priority = 0, rolloutPercent = 100, onFailure = "Agent" };

        Assert.Equal(HttpStatusCode.Created, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Corpo(conv.OrganizacaoId, unidade, canal.Value))).Status);

        // Inexistentes (ou de outro tenant: respondem igual), unidade que o canal não atende e organização que não é a do canal.
        var (stOrg, _) = await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Corpo(Guid.NewGuid(), null, null));
        Assert.True(stOrg is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Corpo(conv.OrganizacaoId, Guid.NewGuid(), null))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Corpo(conv.OrganizacaoId, null, Guid.NewGuid()))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Corpo(conv.OrganizacaoId, outraUnidade, canal.Value))).Status);

        // Unidade e canal de OUTRO tenant nunca valem.
        var outro = await CenarioDeConversas.CriarAsync(banco, "op-escopo-outro", canaisReais: true);
        var unidadeAlheia = await outro.CriarUnidadeAsync(outro.OrganizacaoId, "Alheia", true);
        var canalAlheio = await outro.CriarCanalAsync([unidadeAlheia], unidadeAlheia);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Corpo(conv.OrganizacaoId, unidadeAlheia, null))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, "/workflows/activations", Corpo(conv.OrganizacaoId, null, canalAlheio.Value))).Status);

        // A edição passa pela mesma validação.
        var (_, lista) = await c.ComoAdmin(HttpMethod.Get, "/workflows/activations");
        var id = lista!.AsArray().Single()!["id"]!.GetValue<Guid>();
        var (stEdita, _) = await c.ComoAdmin(HttpMethod.Put, $"/workflows/activations/{id}", new { organizationId = conv.OrganizacaoId, businessUnitId = (Guid?)null, channelInstanceId = Guid.NewGuid(), workflowKey = "escopo", priority = 0, rolloutPercent = 100, onFailure = "Agent", expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, stEdita);
    }
}
