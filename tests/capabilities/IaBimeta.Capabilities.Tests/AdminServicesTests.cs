using System.Text.Json;
using IaBimeta.Audit.Contracts;
using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Tests.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Tests;

/// <summary>A administração das conexões: validação do destino, segredo cifrado que nunca volta, habilitar, testar e permissões.</summary>
public class ConnectionAdminTests
{
    private static ConnectionRequest Request(string url = "https://erp.example.com/api", ConnectorAuthType auth = ConnectorAuthType.None, Guid? org = null, string key = "erp-principal", Dictionary<string, string>? settings = null) =>
        new(org, key, "ERP principal", url, auth, settings, null, null, null, null, null);

    [Fact]
    public async Task Cria_desabilitada_normaliza_o_endereco_audita_e_nao_traz_segredo()
    {
        var h = new Harness();

        var v = await h.Admin.CreateAsync(Request("https://erp.example.com/api/"));

        Assert.False(v.Enabled);
        Assert.False(v.HasSecret);
        Assert.Equal("https://erp.example.com/api", v.BaseUrl);
        Assert.Equal("rest-json", v.Kind);
        Assert.Equal(1, v.Revision);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.connection.create" && e.Outcome == AuditOutcome.Success && e.ResourceId == v.Id.ToString());
    }

    [Theory]
    [InlineData("http://erp.example.com/api")]
    [InlineData("https://localhost/api")]
    [InlineData("https://127.0.0.1/api")]
    [InlineData("https://10.0.0.5/api")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://metadata.google.internal/")]
    [InlineData("https://user:senha@erp.example.com/api")]
    [InlineData("https://erp.example.com:8443/api")]
    [InlineData("https://erp.example.com/api?token=abc")]
    [InlineData("https://erp.example.com/api#frag")]
    [InlineData("ftp://erp.example.com/")]
    [InlineData("erp.example.com/api")]
    [InlineData("")]
    public async Task Endereco_nao_permitido_e_recusado_ja_no_cadastro(string url)
    {
        var h = new Harness();

        var ex = await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.CreateAsync(Request(url)));

        Assert.Contains(ex.Problems, p => p.StartsWith("baseUrl", StringComparison.Ordinal));
        Assert.Empty(h.Rows.Connections);
    }

    [Fact]
    public async Task Chave_nome_cabecalhos_tempo_e_tamanho_sao_validados()
    {
        var h = new Harness();

        foreach (var ruim in new[]
        {
            Request(key: "Maiuscula"),
            Request(key: "x"),
            Request(key: "com espaco"),
            Request() with { Name = "" },
            Request() with { DefaultHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer x" } },
            Request() with { DefaultHeaders = new Dictionary<string, string> { ["X-Ok"] = "a\r\nX-Injetado: 1" } },
            Request() with { AllowedHeaders = ["Host"] },
            Request() with { AllowedHeaders = ["Cookie"] },
            Request() with { TimeoutSeconds = 0 },
            Request() with { TimeoutSeconds = 9999 },
            Request() with { MaxResponseBytes = 10 },
            Request() with { MaxResponseBytes = 999_999_999 },
            Request() with { HealthPath = "sem-barra" },
            Request() with { HealthPath = "/../admin" },
        })
        {
            await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.CreateAsync(ruim));
        }

        Assert.Empty(h.Rows.Connections);
    }

    [Fact]
    public async Task A_chave_e_unica_por_tenant_mas_livre_entre_tenants_e_ha_limite_de_conexoes()
    {
        var h = new Harness(options: new CapabilitiesOptions { MaxConnectionsPerTenant = 2 });
        await h.Admin.CreateAsync(Request());

        await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Admin.CreateAsync(Request()));
        await h.Admin.CreateAsync(Request(key: "segunda"));
        var limite = await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Admin.CreateAsync(Request(key: "terceira")));
        Assert.Equal("limit-reached", limite.Code);

        var b = h.As(TestContexts.User(TestContexts.TenantB, TestContexts.AllIntegration));
        await b.Admin.CreateAsync(Request());
    }

    [Fact]
    public async Task Autenticacao_por_tipo_so_aceita_os_ajustes_do_tipo_e_o_token_oauth_passa_pela_politica_de_destino()
    {
        var h = new Harness();

        await h.Admin.CreateAsync(Request(auth: ConnectorAuthType.ApiKey, settings: new() { ["location"] = "header", ["name"] = "X-API-Key" }));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.CreateAsync(Request(key: "chave-a", auth: ConnectorAuthType.ApiKey, settings: new() { ["name"] = "Host" })));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.CreateAsync(Request(key: "chave-b", auth: ConnectorAuthType.ApiKey, settings: new() { ["name"] = "X-Key", ["tokenUrl"] = "https://x.example.com/t" })));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.CreateAsync(Request(key: "chave-c", auth: ConnectorAuthType.Basic, settings: new() { ["name"] = "X" })));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.CreateAsync(Request(key: "chave-d", auth: ConnectorAuthType.OAuth2ClientCredentials, settings: new() { ["tokenUrl"] = "https://169.254.169.254/token" })));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.CreateAsync(Request(key: "chave-e", auth: ConnectorAuthType.OAuth2ClientCredentials, settings: new() { ["tokenUrl"] = "http://auth.example.com/token" })));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.CreateAsync(Request(key: "chave-f", auth: ConnectorAuthType.OAuth2ClientCredentials, settings: [])));
        await h.Admin.CreateAsync(Request(key: "chave-g", auth: ConnectorAuthType.OAuth2ClientCredentials, settings: new() { ["tokenUrl"] = "https://auth.example.com/token", ["scope"] = "leitura" }));
    }

    [Fact]
    public async Task O_segredo_e_cifrado_nunca_volta_e_a_leitura_so_diz_se_existe()
    {
        var h = new Harness();
        var v = await h.Admin.CreateAsync(Request(auth: ConnectorAuthType.ApiKey, settings: new() { ["name"] = "X-API-Key" }));

        var depois = await h.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string> { ["apiKey"] = "CHAVE-SECRETISSIMA" }, v.Revision));

        Assert.True(depois!.HasSecret);
        Assert.Equal(1, depois.SecretVersion);
        Assert.NotNull(depois.SecretUpdatedAt);
        var texto = JsonSerializer.Serialize(depois) + JsonSerializer.Serialize(await h.Admin.ListAsync()) + JsonSerializer.Serialize(await h.Admin.GetAsync(v.Id)) + h.Everything();
        Assert.DoesNotContain("CHAVE-SECRETISSIMA", texto, StringComparison.Ordinal);
        var guardado = h.Rows.Connections.Single();
        Assert.DoesNotContain("CHAVE-SECRETISSIMA", guardado.SecretCiphertext, StringComparison.Ordinal);
        Assert.Equal("k1", guardado.SecretKeyId);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.connection.set-secret");
        Assert.All(typeof(ConnectionView).GetProperties(), p => Assert.DoesNotContain("Ciphertext", p.Name, StringComparison.Ordinal));

        // O segredo decifrado é o que foi enviado.
        var segredo = CapabilityRuntime.ReadSecret(h.Protector, guardado);
        Assert.Equal("CHAVE-SECRETISSIMA", segredo.Values["apiKey"]);
    }

    [Fact]
    public async Task Trocar_o_segredo_incrementa_a_versao_e_a_antiga_deixa_de_abrir()
    {
        var h = new Harness();
        var v = await h.Admin.CreateAsync(Request(auth: ConnectorAuthType.ApiKey, settings: new() { ["name"] = "X-API-Key" }));
        var v1 = await h.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string> { ["apiKey"] = "primeira" }, v.Revision));
        var antigo = h.Rows.Connections.Single().SecretCiphertext;

        var v2 = await h.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string> { ["apiKey"] = "segunda" }, v1!.Revision));

        Assert.Equal(2, v2!.SecretVersion);
        Assert.Throws<CapabilitiesUnavailableException>(() => h.Protector.Unprotect(new ProtectedSecret("k1", antigo!), new SecretBinding("connection-secret", h.Tenant, v.Id, $"{(int)ConnectorAuthType.ApiKey}:2")));
    }

    [Fact]
    public async Task Segredo_com_campos_errados_ou_sem_chave_de_cifra_nao_grava_nada()
    {
        var h = new Harness();
        var v = await h.Admin.CreateAsync(Request(auth: ConnectorAuthType.Basic));

        foreach (var ruim in new Dictionary<string, string>[]
        {
            new() { ["username"] = "u" },
            new() { ["username"] = "u", ["password"] = "" },
            new() { ["username"] = "u", ["password"] = "p", ["extra"] = "x" },
            new() { ["username"] = "u", ["password"] = "p\nq" },
            new() { ["apiKey"] = "k" },
        })
        {
            await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(ruim, v.Revision)));
        }

        var semChaves = h.As(h.Context, secretsConfigured: false);
        var ex = await Assert.ThrowsAsync<CapabilitiesUnavailableException>(() => semChaves.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string> { ["username"] = "u", ["password"] = "p" }, v.Revision)));
        Assert.Equal("secrets-unavailable", ex.Code);
        Assert.False(h.Rows.Connections.Single().HasSecret);

        var semAuth = await h.Admin.CreateAsync(Request(key: "aberta"));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.SetSecretAsync(semAuth.Id, new ConnectionSecretRequest(new Dictionary<string, string> { ["apiKey"] = "x" }, semAuth.Revision)));
    }

    [Fact]
    public async Task Habilitar_exige_segredo_quando_ha_autenticacao_e_desabilitar_vale_na_hora()
    {
        var h = new Harness();
        var v = await h.Admin.CreateAsync(Request(auth: ConnectorAuthType.ApiKey, settings: new() { ["name"] = "X-Key" }));

        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.SetEnabledAsync(v.Id, true));
        await h.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string> { ["apiKey"] = "k" }, v.Revision));
        Assert.True((await h.Admin.SetEnabledAsync(v.Id, true))!.Enabled);
        Assert.False((await h.Admin.SetEnabledAsync(v.Id, false))!.Enabled);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.connection.enable");
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.connection.disable");

        var aberta = await h.Admin.CreateAsync(Request(key: "sem-auth"));
        Assert.True((await h.Admin.SetEnabledAsync(aberta.Id, true))!.Enabled);
    }

    [Fact]
    public async Task Mudar_o_tipo_de_autenticacao_apaga_o_segredo_e_desabilita_e_a_revisao_evita_sobrescrever()
    {
        var h = new Harness();
        var v = await h.Admin.CreateAsync(Request(auth: ConnectorAuthType.ApiKey, settings: new() { ["name"] = "X-Key" }));
        var comSegredo = await h.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string> { ["apiKey"] = "k" }, v.Revision));
        var habilitada = await h.Admin.SetEnabledAsync(v.Id, true);

        var mudou = await h.Admin.UpdateAsync(v.Id, new ConnectionUpdate("Novo nome", "https://erp.example.com/v2", ConnectorAuthType.Basic, null, null, null, null, null, null, habilitada!.Revision));

        Assert.Equal(ConnectorAuthType.Basic, mudou!.AuthType);
        Assert.False(mudou.HasSecret);
        Assert.False(mudou.Enabled);
        Assert.Equal("https://erp.example.com/v2", mudou.BaseUrl);

        // Quem ainda tem a revisão antiga não sobrescreve.
        await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Admin.UpdateAsync(v.Id, new ConnectionUpdate("Velho", "https://erp.example.com/v1", ConnectorAuthType.Basic, null, null, null, null, null, null, comSegredo!.Revision)));
        await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string> { ["username"] = "u", ["password"] = "p" }, comSegredo!.Revision)));
    }

    [Fact]
    public async Task Atualizar_nao_troca_para_um_destino_nao_permitido()
    {
        var h = new Harness();
        var v = await h.Admin.CreateAsync(Request());

        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Admin.UpdateAsync(v.Id, new ConnectionUpdate("x", "https://10.0.0.1/api", ConnectorAuthType.None, null, null, null, null, null, null, v.Revision)));
        Assert.Equal("https://erp.example.com/api", h.Rows.Connections.Single().BaseUrl);
    }

    [Fact]
    public async Task Permissoes_distintas_por_passo_e_escopo_de_organizacao()
    {
        var admin = new Harness();
        var v = await admin.Admin.CreateAsync(Request(org: TestContexts.OrgA));

        var leitor = admin.As(TestContexts.User(TestContexts.TenantA, Permission.IntegrationRead));
        Assert.Single(await leitor.Admin.ListAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => leitor.Admin.CreateAsync(Request(key: "outra")));
        await Assert.ThrowsAsync<AccessDeniedException>(() => leitor.Admin.SetEnabledAsync(v.Id, true));
        await Assert.ThrowsAsync<AccessDeniedException>(() => leitor.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string>(), v.Revision)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => leitor.Admin.TestAsync(v.Id));

        var testador = admin.As(TestContexts.User(TestContexts.TenantA, Permission.IntegrationTest));
        Assert.NotNull(await testador.Admin.TestAsync(v.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => testador.Admin.SetEnabledAsync(v.Id, true));
        await Assert.ThrowsAsync<AccessDeniedException>(() => testador.Admin.ListAsync());

        var daOutraOrg = admin.As(TestContexts.User(TestContexts.TenantA, new PermissionGrant(Permission.IntegrationRead, TestContexts.OrgB), new PermissionGrant(Permission.IntegrationManage, TestContexts.OrgB)));
        Assert.Empty(await daOutraOrg.Admin.ListAsync());
        Assert.Null(await daOutraOrg.Admin.GetAsync(v.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => daOutraOrg.Admin.SetEnabledAsync(v.Id, true));
        await Assert.ThrowsAsync<AccessDeniedException>(() => daOutraOrg.Admin.CreateAsync(Request(key: "tenant.wide", org: null)));

        Assert.Contains(admin.Audit.Entries, e => e.Outcome == AuditOutcome.Denied && e.Operation == "capabilities.connection.set-enabled" || e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Conexao_de_outro_tenant_responde_como_inexistente()
    {
        var a = new Harness();
        var v = await a.Admin.CreateAsync(Request());
        var b = a.As(TestContexts.User(TestContexts.TenantB, TestContexts.AllIntegration));

        Assert.Null(await b.Admin.GetAsync(v.Id));
        Assert.Null(await b.Admin.UpdateAsync(v.Id, new ConnectionUpdate("x", "https://erp.example.com/api", ConnectorAuthType.None, null, null, null, null, null, null, v.Revision)));
        Assert.Null(await b.Admin.SetEnabledAsync(v.Id, true));
        Assert.Null(await b.Admin.SetSecretAsync(v.Id, new ConnectionSecretRequest(new Dictionary<string, string>(), 1)));
        Assert.Null(await b.Admin.TestAsync(v.Id));
        Assert.Empty(await b.Admin.ListAsync());
    }

    [Fact]
    public async Task Testar_a_conexao_sonda_sem_executar_operacoes_respeita_limite_e_nao_vaza_o_segredo()
    {
        var h = new Harness(options: new CapabilitiesOptions { TestsPerConnectionPerMinute = 2 });
        var conexao = h.SeedConnection(auth: ConnectorAuthType.ApiKey, enabled: false);
        h.Connector.Probe = c => c.Secret.Values["apiKey"] == "CHAVE-SECRETISSIMA" ? ScriptedConnector.Ok("{}") : ScriptedConnector.Fail(ConnectorOutcome.AuthFailed, 401);

        var ok = await h.Admin.TestAsync(conexao.Id);
        var segunda = await h.Admin.TestAsync(conexao.Id);
        var limitada = await h.Admin.TestAsync(conexao.Id);

        Assert.True(ok!.Ok);
        Assert.Equal("ok", ok.Outcome);
        Assert.True(segunda!.Ok);
        Assert.False(limitada!.Ok);
        Assert.Equal("rate-limited", limitada.Outcome);
        Assert.Empty(h.Connector.Calls);
        Assert.DoesNotContain("CHAVE-SECRETISSIMA", JsonSerializer.Serialize(ok) + h.Everything(), StringComparison.Ordinal);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        h.Connector.Probe = _ => ScriptedConnector.Fail(ConnectorOutcome.AuthFailed, 401);
        var recusada = await h.Admin.TestAsync(conexao.Id);
        Assert.False(recusada!.Ok);
        Assert.Equal("upstream-auth", recusada.Outcome);
        Assert.Equal(401, recusada.UpstreamStatus);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.connection.test" && e.Outcome == AuditOutcome.Failed);
    }

    [Fact]
    public async Task Testar_sem_chave_de_cifra_informa_segredo_indisponivel_sem_sondar()
    {
        var h = new Harness();
        var conexao = h.SeedConnection(auth: ConnectorAuthType.ApiKey);
        var semChaves = h.As(h.Context, secretsConfigured: false);

        var r = await semChaves.Admin.TestAsync(conexao.Id);

        Assert.False(r!.Ok);
        Assert.Equal("secret-unavailable", r.Outcome);
    }
}

/// <summary>As operações versionadas: rascunho, publicação atômica, imutabilidade e permissões.</summary>
public class DefinitionAdminTests
{
    private static DefinitionRequest Request(Guid conexao, string key = "pedidos.consultar", CapabilityOperationSpec? spec = null, CapabilityEffect effect = CapabilityEffect.Read, int? revisao = null, bool? confirm = null, CapabilityCaller[]? callers = null) =>
        new(conexao, key, "Consultar pedido", "Consulta um pedido pelo número.", effect, confirm, callers, spec ?? Specs.Read(), revisao);

    [Fact]
    public async Task Salva_o_rascunho_publica_e_so_a_publicada_executa_com_o_padrao_seguro_de_origem()
    {
        var h = new Harness();
        var conexao = h.SeedConnection();

        var rascunho = await h.Defs.SaveDraftAsync(Request(conexao.Id));
        Assert.Equal(DefinitionStatus.Draft, rascunho.Status);
        Assert.Equal(1, rascunho.Version);
        Assert.Equal(["Operator"], rascunho.AllowedCallers);
        Assert.Equal("not-found", (await h.Engine.ExecuteAsync(h.Read("pedidos.consultar", new { numero = 1 }))).Error!.Code);

        var publicada = await h.Defs.PublishAsync(rascunho.Id);
        Assert.Equal(DefinitionStatus.Published, publicada!.Status);
        Assert.NotNull(publicada.PublishedAt);
        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await h.Engine.ExecuteAsync(h.Read("pedidos.consultar", new { numero = 1 }))).Status);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.definition.publish");
    }

    [Fact]
    public async Task Cada_edicao_apos_publicar_e_uma_versao_nova_e_a_publicada_nunca_muda()
    {
        var h = new Harness();
        var conexao = h.SeedConnection();
        var v1 = await h.Defs.PublishAsync((await h.Defs.SaveDraftAsync(Request(conexao.Id))).Id);

        var rascunho2 = await h.Defs.SaveDraftAsync(Request(conexao.Id, spec: Specs.Read() with { MaxAttempts = 2 }));
        Assert.Equal(2, rascunho2.Version);
        Assert.Equal(1, h.Rows.Definitions.Single(d => d.Id == v1!.Id).Spec.MaxAttempts);

        var editado = await h.Defs.SaveDraftAsync(Request(conexao.Id, spec: Specs.Read() with { MaxAttempts = 3 }, revisao: rascunho2.Revision));
        Assert.Equal(rascunho2.Id, editado.Id);
        Assert.Equal(3, editado.Spec.MaxAttempts);

        var v2 = await h.Defs.PublishAsync(rascunho2.Id);
        Assert.Equal(DefinitionStatus.Published, v2!.Status);
        Assert.Equal(DefinitionStatus.Retired, h.Rows.Definitions.Single(d => d.Id == v1!.Id).Status);
        Assert.Single(h.Rows.Definitions, d => d.Status == DefinitionStatus.Published);
        Assert.Equal(["2:Published", "1:Retired"], (await h.Defs.VersionsAsync("pedidos.consultar")).Select(v => $"{v.Version}:{v.Status}"));
        Assert.Equal(CapabilityExecutionStatus.Succeeded, (await h.Engine.ExecuteAsync(h.Read("pedidos.consultar", new { numero = 1 }))).Status);
        Assert.Equal(2, h.Rows.Executions.Single().DefinitionVersion);
    }

    [Fact]
    public async Task Editar_o_rascunho_exige_a_revisao_atual()
    {
        var h = new Harness();
        var conexao = h.SeedConnection();
        var rascunho = await h.Defs.SaveDraftAsync(Request(conexao.Id));

        await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Defs.SaveDraftAsync(Request(conexao.Id)));
        await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Defs.SaveDraftAsync(Request(conexao.Id, revisao: rascunho.Revision + 5)));
        await h.Defs.SaveDraftAsync(Request(conexao.Id, revisao: rascunho.Revision));
    }

    [Fact]
    public async Task Molde_invalido_volta_com_os_problemas_e_nada_e_gravado()
    {
        var h = new Harness();
        var conexao = h.SeedConnection();

        var ex = await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Request(conexao.Id, spec: Specs.Read() with { Method = "TRACE", PathTemplate = "/a/../b" })));
        Assert.True(ex.Problems.Count >= 2);

        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Request(conexao.Id, key: "Chave Ruim")));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Request(Guid.NewGuid())));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Request(conexao.Id, callers: [CapabilityCaller.Agent | CapabilityCaller.Chatbot])));
        Assert.Empty(h.Rows.Definitions);
    }

    [Fact]
    public async Task Escrita_sem_idempotencia_declarada_e_confirmacao_dispensada_sem_prova_sao_recusadas()
    {
        var h = new Harness();
        var conexao = h.SeedConnection();

        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Request(conexao.Id, "pedidos.cancelar", Specs.Write(IdempotencySupport.Unspecified), CapabilityEffect.Write)));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Request(conexao.Id, "pedidos.cancelar", Specs.Write(IdempotencySupport.None), CapabilityEffect.Write, confirm: false)));

        var padrao = await h.Defs.SaveDraftAsync(Request(conexao.Id, "pedidos.cancelar", Specs.Write(IdempotencySupport.None), CapabilityEffect.Write));
        Assert.True(padrao.RequiresConfirmation);

        var comprovada = await h.Defs.SaveDraftAsync(Request(conexao.Id, "pedidos.cancelar2", Specs.Write(), CapabilityEffect.Write, confirm: false));
        Assert.False(comprovada.RequiresConfirmation);
    }

    [Fact]
    public async Task A_conexao_de_uma_chave_nao_muda_e_ha_limite_de_chaves()
    {
        var h = new Harness(options: new CapabilitiesOptions { MaxDefinitionKeysPerTenant = 2 });
        var c1 = h.SeedConnection(key: "c1");
        var c2 = h.SeedConnection(key: "c2");
        var publicada = await h.Defs.PublishAsync((await h.Defs.SaveDraftAsync(Request(c1.Id))).Id);
        Assert.NotNull(publicada);

        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.SaveDraftAsync(Request(c2.Id)));
        await h.Defs.SaveDraftAsync(Request(c1.Id, "outra.chave"));
        var limite = await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Defs.SaveDraftAsync(Request(c1.Id, "terceira.chave")));
        Assert.Equal("limit-reached", limite.Code);
    }

    [Fact]
    public async Task Publicar_confere_o_molde_de_novo_e_so_publica_rascunho()
    {
        var h = new Harness();
        var conexao = h.SeedConnection(allowedHeaders: ["X-Filial"]);
        var spec = Specs.Read() with { Parameters = [.. Specs.Read().Parameters, Specs.Param("f", where: CapabilityParameterLocation.Header, wire: "X-Filial")] };
        var rascunho = await h.Defs.SaveDraftAsync(Request(conexao.Id, spec: spec));

        // A conexão deixou de permitir o cabeçalho depois que o rascunho foi aceito.
        conexao.Update(conexao.Name, conexao.BaseUrl, conexao.AuthType, conexao.AuthSettings, conexao.DefaultHeaders, [], conexao.HealthPath, 5, 4096, h.Clock.GetUtcNow());
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.Defs.PublishAsync(rascunho.Id));

        conexao.Update(conexao.Name, conexao.BaseUrl, conexao.AuthType, conexao.AuthSettings, conexao.DefaultHeaders, ["X-Filial"], conexao.HealthPath, 5, 4096, h.Clock.GetUtcNow());
        Assert.NotNull(await h.Defs.PublishAsync(rascunho.Id));
        await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Defs.PublishAsync(rascunho.Id));
    }

    [Fact]
    public async Task Ligar_desligar_e_aposentar_so_a_versao_publicada_e_com_auditoria()
    {
        var h = new Harness();
        var conexao = h.SeedConnection();
        var rascunho = await h.Defs.SaveDraftAsync(Request(conexao.Id));
        await Assert.ThrowsAsync<CapabilitiesConflictException>(() => h.Defs.SetEnabledAsync(rascunho.Id, false));
        await h.Defs.PublishAsync(rascunho.Id);

        Assert.False((await h.Defs.SetEnabledAsync(rascunho.Id, false))!.Enabled);
        Assert.Equal("disabled", (await h.Engine.ExecuteAsync(h.Read("pedidos.consultar", new { numero = 1 }))).Error!.Code);
        Assert.True((await h.Defs.SetEnabledAsync(rascunho.Id, true))!.Enabled);
        Assert.Equal(DefinitionStatus.Retired, (await h.Defs.RetireAsync(rascunho.Id))!.Status);
        Assert.Equal("not-found", (await h.Engine.ExecuteAsync(h.Read("pedidos.consultar", new { numero = 1 }))).Error!.Code);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.definition.retire");
        Assert.Contains(h.Audit.Entries, e => e.Operation == "capabilities.definition.disable");
    }

    [Fact]
    public async Task Permissoes_e_isolamento_do_catalogo_de_operacoes()
    {
        var a = new Harness();
        var conexao = a.SeedConnection(TestContexts.OrgA);
        var rascunho = await a.Defs.SaveDraftAsync(Request(conexao.Id));

        var leitor = a.As(TestContexts.User(TestContexts.TenantA, Permission.IntegrationRead));
        Assert.Single(await leitor.Defs.ListAsync(null));
        Assert.NotNull(await leitor.Defs.GetAsync(rascunho.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => leitor.Defs.PublishAsync(rascunho.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => leitor.Defs.SaveDraftAsync(Request(conexao.Id, "x.y")));
        await Assert.ThrowsAsync<AccessDeniedException>(() => leitor.Defs.SetEnabledAsync(rascunho.Id, false));

        var outraOrg = a.As(TestContexts.User(TestContexts.TenantA, new PermissionGrant(Permission.IntegrationRead, TestContexts.OrgB)));
        Assert.Empty(await outraOrg.Defs.ListAsync(null));
        Assert.Null(await outraOrg.Defs.GetAsync(rascunho.Id));
        Assert.Empty(await outraOrg.Defs.VersionsAsync("pedidos.consultar"));

        var b = a.As(TestContexts.User(TestContexts.TenantB, TestContexts.AllIntegration));
        Assert.Empty(await b.Defs.ListAsync(null));
        Assert.Null(await b.Defs.GetAsync(rascunho.Id));
        Assert.Null(await b.Defs.PublishAsync(rascunho.Id));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => b.Defs.SaveDraftAsync(Request(conexao.Id)));
    }
}

/// <summary>O histórico e o resumo por conexão: filtros, paginação e escopo por organização.</summary>
public class ExecutionHistoryTests
{
    [Fact]
    public async Task Filtra_pagina_e_resume_por_conexao()
    {
        var h = new Harness();
        h.SeedTypical();
        for (var i = 1; i <= 5; i++)
        {
            await h.Engine.ExecuteAsync(h.Read(Harness.Pedido, new { numero = i }));
            h.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        h.Connector.Script = _ => ScriptedConnector.Fail(ConnectorOutcome.Rejected, 404, false);
        await h.Engine.ExecuteAsync(h.Read(Harness.Pedido, new { numero = 6 }));
        await h.Engine.ExecuteAsync(h.Write(Harness.Cancelamento, new { numero = 1, motivo = "x" }));

        var todas = await h.History.ListAsync(null, null, null, null, 3, 0);
        Assert.Equal(7, todas.Total);
        Assert.Equal(3, todas.Items.Count);
        Assert.Equal(2, (await h.History.ListAsync(null, null, CapabilityExecutionStatus.Failed, null, 50, 0)).Total + 1);
        Assert.Equal(1, (await h.History.ListAsync(null, Harness.Cancelamento, null, null, 50, 0)).Total);
        Assert.Equal(0, (await h.History.ListAsync(null, null, null, true, 50, 0)).Total);

        var resumo = Assert.Single(await h.History.StatsAsync(7));
        Assert.Equal(7, resumo.Total);
        Assert.Equal(1, resumo.Failed);
    }

    [Fact]
    public async Task Parametros_invalidos_da_consulta_sao_recusados()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.History.ListAsync(null, null, null, null, 0, 0));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.History.ListAsync(null, null, null, null, 101, 0));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.History.ListAsync(null, null, null, null, 10, -1));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.History.ListAsync(null, "../x", null, null, 10, 0));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.History.StatsAsync(0));
        await Assert.ThrowsAsync<CapabilitiesValidationException>(() => h.History.StatsAsync(91));
    }

    [Fact]
    public async Task O_historico_respeita_a_organizacao_da_conexao()
    {
        var admin = new Harness();
        var a = admin.SeedConnection(TestContexts.OrgA, key: "a");
        var b = admin.SeedConnection(TestContexts.OrgB, key: "b");
        admin.SeedDefinition(a, "a.ler", Specs.Read());
        admin.SeedDefinition(b, "b.ler", Specs.Read());
        await admin.Engine.ExecuteAsync(admin.Read("a.ler", new { numero = 1 }));
        await admin.Engine.ExecuteAsync(admin.Read("b.ler", new { numero = 1 }));

        var daOrgA = admin.As(TestContexts.User(TestContexts.TenantA, new PermissionGrant(Permission.IntegrationRead, TestContexts.OrgA)));

        var pagina = await daOrgA.History.ListAsync(null, null, null, null, 50, 0);
        Assert.Equal(["a.ler"], pagina.Items.Select(i => i.DefinitionKey));
        Assert.Equal(a.Id, Assert.Single(await daOrgA.History.StatsAsync(7)).ConnectionId);
        Assert.Null(await daOrgA.History.GetAsync(admin.Rows.Executions.Single(e => e.DefinitionKey == "b.ler").Id));
        Assert.Equal(2, (await admin.History.ListAsync(null, null, null, null, 50, 0)).Total);
    }
}
