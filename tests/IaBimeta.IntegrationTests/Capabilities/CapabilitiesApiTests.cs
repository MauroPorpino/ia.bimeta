using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using Npgsql;

namespace IaBimeta.IntegrationTests.Capabilities;

/// <summary>
/// O Motor de Integração Empresarial pela API REAL (HTTP) com PostgreSQL real e um ERP simulado em loopback (ADR 0035): permissões por papel, o fluxo completo de configuração (conexão, segredo, teste, operação,
/// publicação), execução de leitura e de escrita com confirmação e idempotência, concorrência sobre os índices únicos do banco, isolamento entre tenants e organizações, SSRF e que nenhum segredo, parâmetro ou
/// corpo de resposta fica gravado nem volta pela API.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CapabilitiesApiTests(PostgresFixture banco)
{
    private static readonly string ChaveMestra = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray());

    private sealed class Cenario : IAsyncDisposable
    {
        public required ApiServer Api { get; init; }

        public required FakeErp Erp { get; init; }

        public required string Admin { get; init; }

        public required UsuarioCriado AdminUsuario { get; init; }

        public required IaBimeta.Tenancy.Contracts.TenantInfo Tenant { get; init; }

        public required IaBimeta.Tenancy.Application.OrganizationView Org { get; init; }

        /// <summary>Tudo que a API devolveu neste cenário, para provar que um segredo nunca voltou.</summary>
        public List<string> Respostas { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            await Erp.DisposeAsync();
        }

        public async Task<(HttpStatusCode Status, JsonNode? Corpo)> Chamar(string? token, HttpMethod metodo, string caminho, object? corpo = null)
        {
            using var resposta = await Api.EnviarAsync(token, metodo, caminho, corpo);
            var texto = await resposta.Content.ReadAsStringAsync();
            Respostas.Add(texto);
            return (resposta.StatusCode, string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto));
        }

        public Task<(HttpStatusCode Status, JsonNode? Corpo)> Admin_(HttpMethod metodo, string caminho, object? corpo = null) => Chamar(Admin, metodo, caminho, corpo);
    }

    private async Task<Cenario> IniciarAsync(string prefixo, Action<Dictionary<string, string?>>? ajustar = null)
    {
        var erp = await FakeErp.StartAsync();
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var org = await banco.CriarOrganizacaoAsync(tenant, $"Org {prefixo}");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);

        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Capabilities:Secrets:ActiveKeyId"] = "k1";
        configuracao["Capabilities:Secrets:Keys:k1"] = ChaveMestra;
        configuracao["Capabilities:Options:Outbound:AllowHttp"] = "true";
        configuracao["Capabilities:Options:Outbound:AllowedPorts:0"] = erp.Port.ToString();
        configuracao["Capabilities:Options:Outbound:AllowedPrivateCidrs:0"] = "127.0.0.0/8";
        configuracao["Capabilities:Options:RetryBaseDelayMs"] = "0";
        configuracao["Capabilities:Options:RetryMaxDelayMs"] = "0";
        ajustar?.Invoke(configuracao);

        var api = await ApiServer.StartAsync("Production", configuracao);
        return new Cenario { Api = api, Erp = erp, Admin = await api.LoginAsync(admin), AdminUsuario = admin, Tenant = tenant, Org = org };
    }

    // ------------------------------------------------------------ moldes

    private static object ConexaoComApiKey(FakeErp erp, string chave = "erp-principal", Guid? organizacao = null, int? timeout = null) => new
    {
        organizationId = organizacao,
        key = chave,
        name = "ERP principal",
        baseUrl = erp.BaseUrl,
        authType = "ApiKey",
        authSettings = new Dictionary<string, string> { ["location"] = "header", ["name"] = "X-API-Key" },
        defaultHeaders = new Dictionary<string, string>(),
        allowedHeaders = Array.Empty<string>(),
        healthPath = "/health",
        timeoutSeconds = timeout,
        maxResponseBytes = (int?)null,
    };

    private static object MoldeDeLeitura(string caminho = "/pedidos/{numero}", int tentativas = 1) => new
    {
        method = "GET",
        pathTemplate = caminho,
        parameters = new[] { new { name = "numero", type = "Integer", location = "Path", required = true, min = 1, max = 999999 } },
        response = new[]
        {
            new { name = "situacao", pointer = "/status", type = "String", required = true },
            new { name = "total", pointer = "/total", type = "Number", required = true },
        },
        idempotency = "Unspecified",
        maxAttempts = tentativas,
    };

    private static object MoldeDeEscrita(bool comprovada = true) => new
    {
        method = "POST",
        pathTemplate = "/pedidos/{numero}/cancelar",
        parameters = new object[]
        {
            new { name = "numero", type = "Integer", location = "Path", required = true, min = 1 },
            new { name = "motivo", type = "String", location = "Body", required = true, maxLength = 100 },
        },
        response = new[] { new { name = "protocolo", pointer = "/protocolo", type = "String", required = true } },
        idempotency = comprovada ? "Proven" : "None",
        idempotencyHeader = comprovada ? "Idempotency-Key" : null,
        maxAttempts = 1,
    };

    private static object Operacao(Guid conexao, string chave, string nome, string efeito, object molde, string[]? origens = null, bool? confirmar = null) => new
    {
        connectionId = conexao,
        key = chave,
        name = nome,
        description = "Operação de teste.",
        effect = efeito,
        requiresConfirmation = confirmar,
        allowedCallers = origens ?? ["Operator"],
        spec = molde,
        expectedRevision = (int?)null,
    };

    /// <summary>Cria, define o segredo e habilita uma conexão com o ERP simulado; devolve o id.</summary>
    private static async Task<Guid> ConexaoPronta(Cenario c, string chave = "erp-principal", Guid? organizacao = null, int? timeout = null)
    {
        var (status, criada) = await c.Admin_(HttpMethod.Post, "/capabilities/connections", ConexaoComApiKey(c.Erp, chave, organizacao, timeout));
        Assert.Equal(HttpStatusCode.Created, status);
        var id = criada!["id"]!.GetValue<Guid>();

        var (s2, comSegredo) = await c.Admin_(HttpMethod.Put, $"/capabilities/connections/{id}/secret", new { values = new Dictionary<string, string> { ["apiKey"] = FakeErp.ApiKey }, expectedRevision = criada["revision"]!.GetValue<int>() });
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.True(comSegredo!["hasSecret"]!.GetValue<bool>());

        var (s3, habilitada) = await c.Admin_(HttpMethod.Post, $"/capabilities/connections/{id}/enable");
        Assert.Equal(HttpStatusCode.OK, s3);
        Assert.True(habilitada!["enabled"]!.GetValue<bool>());
        return id;
    }

    private static async Task<Guid> OperacaoPublicada(Cenario c, Guid conexao, string chave, string efeito, object molde, string[]? origens = null, bool? confirmar = null)
    {
        var (s, rascunho) = await c.Admin_(HttpMethod.Post, "/capabilities/definitions", Operacao(conexao, chave, chave, efeito, molde, origens, confirmar));
        Assert.True(s == HttpStatusCode.OK, rascunho?.ToJsonString());
        var id = rascunho!["id"]!.GetValue<Guid>();
        var (sp, publicada) = await c.Admin_(HttpMethod.Post, $"/capabilities/definitions/{id}/publish");
        Assert.True(sp == HttpStatusCode.OK, publicada?.ToJsonString());
        return id;
    }

    // ------------------------------------------------------------ permissões

    private static readonly (HttpMethod Metodo, string Caminho, object? Corpo)[] Rotas =
    [
        (HttpMethod.Get, "/capabilities/connections", null),
        (HttpMethod.Get, "/capabilities/connections/00000000-0000-4000-8000-000000000001", null),
        (HttpMethod.Post, "/capabilities/connections", new { key = "x-y", name = "n", baseUrl = "https://erp.example.com", authType = "None" }),
        (HttpMethod.Put, "/capabilities/connections/00000000-0000-4000-8000-000000000001", new { name = "n", baseUrl = "https://erp.example.com", authType = "None", expectedRevision = 1 }),
        (HttpMethod.Put, "/capabilities/connections/00000000-0000-4000-8000-000000000001/secret", new { values = new Dictionary<string, string>(), expectedRevision = 1 }),
        (HttpMethod.Post, "/capabilities/connections/00000000-0000-4000-8000-000000000001/enable", null),
        (HttpMethod.Post, "/capabilities/connections/00000000-0000-4000-8000-000000000001/disable", null),
        (HttpMethod.Post, "/capabilities/connections/00000000-0000-4000-8000-000000000001/test", null),
        (HttpMethod.Get, "/capabilities/definitions", null),
        (HttpMethod.Get, "/capabilities/definitions/00000000-0000-4000-8000-000000000001", null),
        (HttpMethod.Get, "/capabilities/definitions/by-key/pedidos.consultar/versions", null),
        (HttpMethod.Post, "/capabilities/definitions", new { connectionId = "00000000-0000-4000-8000-000000000001", key = "a.b", name = "n", effect = "Read" }),
        (HttpMethod.Post, "/capabilities/definitions/00000000-0000-4000-8000-000000000001/publish", null),
        (HttpMethod.Post, "/capabilities/definitions/00000000-0000-4000-8000-000000000001/retire", null),
        (HttpMethod.Post, "/capabilities/definitions/00000000-0000-4000-8000-000000000001/enable", null),
        (HttpMethod.Post, "/capabilities/definitions/00000000-0000-4000-8000-000000000001/disable", null),
        (HttpMethod.Post, "/capabilities/definitions/00000000-0000-4000-8000-000000000001/test", new { parameters = new { numero = 1 } }),
        (HttpMethod.Post, "/capabilities/definitions/00000000-0000-4000-8000-000000000001/validate", new { parameters = new { numero = 1 } }),
        (HttpMethod.Get, "/capabilities/executions", null),
        (HttpMethod.Get, "/capabilities/executions/stats", null),
        (HttpMethod.Get, "/capabilities/executions/00000000-0000-4000-8000-000000000001", null),
        (HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 1 } }),
        (HttpMethod.Post, "/capabilities/executions/00000000-0000-4000-8000-000000000001/confirm", null),
        (HttpMethod.Post, "/capabilities/executions/00000000-0000-4000-8000-000000000001/cancel", null),
        (HttpMethod.Get, "/capabilities/catalog", null),
    ];

    [Fact]
    public async Task Toda_rota_recusa_sem_sessao_com_401_e_o_usuario_comum_sem_permissao_de_integracao_com_403()
    {
        await using var c = await IniciarAsync("capperm");
        var comum = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.User));

        foreach (var (metodo, caminho, corpo) in Rotas)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.Chamar(null, metodo, caminho, corpo)).Status);
            Assert.True(HttpStatusCode.Forbidden == (await c.Chamar(comum, metodo, caminho, corpo)).Status, $"{metodo} {caminho}");
        }
    }

    [Fact]
    public async Task O_gerente_so_le_e_nao_administra_nem_executa()
    {
        await using var c = await IniciarAsync("capmgr");
        var conexao = await ConexaoPronta(c, organizacao: c.Org.Id);
        await OperacaoPublicada(c, conexao, "pedidos.consultar", "Read", MoldeDeLeitura(), ["Operator"]);
        var gerente = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.Manager));

        Assert.Equal(HttpStatusCode.OK, (await c.Chamar(gerente, HttpMethod.Get, "/capabilities/connections")).Status);
        Assert.Equal(HttpStatusCode.OK, (await c.Chamar(gerente, HttpMethod.Get, "/capabilities/definitions")).Status);
        Assert.Equal(HttpStatusCode.OK, (await c.Chamar(gerente, HttpMethod.Get, "/capabilities/executions")).Status);

        foreach (var (metodo, caminho, corpo) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/capabilities/connections", ConexaoComApiKey(c.Erp, "outra", c.Org.Id)),
            (HttpMethod.Post, $"/capabilities/connections/{conexao}/test", null),
            (HttpMethod.Post, $"/capabilities/connections/{conexao}/disable", null),
            (HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 1 } }),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(gerente, metodo, caminho, corpo)).Status);
        }

        Assert.DoesNotContain(c.Erp.Received, r => r.Path.StartsWith("/pedidos", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ fluxo completo

    [Fact]
    public async Task Fluxo_completo_configura_testa_publica_executa_le_o_historico_e_nenhum_segredo_aparece()
    {
        await using var c = await IniciarAsync("capflow");
        var (s, criada) = await c.Admin_(HttpMethod.Post, "/capabilities/connections", ConexaoComApiKey(c.Erp));
        Assert.Equal(HttpStatusCode.Created, s);
        var id = criada!["id"]!.GetValue<Guid>();
        Assert.False(criada["enabled"]!.GetValue<bool>());
        Assert.False(criada["hasSecret"]!.GetValue<bool>());

        // Habilitar sem segredo é recusado.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Admin_(HttpMethod.Post, $"/capabilities/connections/{id}/enable")).Status);
        await ConexaoPronta(c, "segunda-conexao");

        var (_, comSegredo) = await c.Admin_(HttpMethod.Put, $"/capabilities/connections/{id}/secret", new { values = new Dictionary<string, string> { ["apiKey"] = FakeErp.ApiKey }, expectedRevision = 1 });
        await c.Admin_(HttpMethod.Post, $"/capabilities/connections/{id}/enable");
        var (st, teste) = await c.Admin_(HttpMethod.Post, $"/capabilities/connections/{id}/test");
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.True(teste!["ok"]!.GetValue<bool>());
        Assert.Equal("ok", teste["outcome"]!.GetValue<string>());
        Assert.NotNull(comSegredo);

        // Rascunho -> teste do rascunho -> publicação.
        var (sd, rascunho) = await c.Admin_(HttpMethod.Post, "/capabilities/definitions", Operacao(id, "pedidos.consultar", "Consultar pedido", "Read", MoldeDeLeitura()));
        Assert.Equal(HttpStatusCode.OK, sd);
        var defId = rascunho!["id"]!.GetValue<Guid>();
        Assert.Equal("Draft", rascunho["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 1001 } })).Status);

        var (sv, validacao) = await c.Admin_(HttpMethod.Post, $"/capabilities/definitions/{defId}/validate", new { parameters = new { numero = "abc" } });
        Assert.Equal(HttpStatusCode.OK, sv);
        Assert.False(validacao!["valid"]!.GetValue<bool>());

        var (sx, tentativa) = await c.Admin_(HttpMethod.Post, $"/capabilities/definitions/{defId}/test", new { parameters = new { numero = 1001 } });
        Assert.Equal(HttpStatusCode.OK, sx);
        Assert.Equal("Succeeded", tentativa!["status"]!.GetValue<string>());
        Assert.Equal("ABERTO", tentativa["output"]!["situacao"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await c.Admin_(HttpMethod.Post, $"/capabilities/definitions/{defId}/publish")).Status);
        var (sc, catalogo) = await c.Admin_(HttpMethod.Get, "/capabilities/catalog");
        Assert.Equal(HttpStatusCode.OK, sc);
        Assert.Equal("pedidos.consultar", catalogo!.AsArray().Single()!["key"]!.GetValue<string>());

        var sessao = Guid.NewGuid();
        var (se, executada) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 1001 }, serviceSessionId = sessao });
        Assert.Equal(HttpStatusCode.OK, se);
        Assert.Equal("Succeeded", executada!["status"]!.GetValue<string>());
        Assert.True(executada["untrustedContent"]!.GetValue<bool>());
        Assert.Equal(123.45m, executada["output"]!["total"]!.GetValue<decimal>());
        Assert.Equal(["situacao", "total"], executada["output"]!.AsObject().Select(p => p.Key).ToArray());

        var (sh, historico) = await c.Admin_(HttpMethod.Get, $"/capabilities/executions?key=pedidos.consultar&take=10");
        Assert.Equal(HttpStatusCode.OK, sh);
        Assert.Equal(2, historico!["total"]!.GetValue<int>());
        var real = historico["items"]!.AsArray().Single(i => !i!["isTest"]!.GetValue<bool>())!;
        Assert.Equal(sessao, real["serviceSessionId"]!.GetValue<Guid>());
        Assert.Equal(c.AdminUsuario.Id, real["requestedBy"]!.GetValue<Guid>());
        var (_, resumo) = await c.Admin_(HttpMethod.Get, "/capabilities/executions/stats?days=7");
        Assert.Equal(id, resumo!.AsArray().Single(x => x!["connectionId"]!.GetValue<Guid>() == id)!["connectionId"]!.GetValue<Guid>());

        // O ERP viu a chave da API (a conexão a envia), e a API NUNCA a devolveu.
        Assert.Contains(c.Erp.Received, r => r.Path == "/pedidos/1001" && r.ApiKey == FakeErp.ApiKey);
        Assert.DoesNotContain(c.Respostas, r => r.Contains(FakeErp.ApiKey, StringComparison.Ordinal));
        Assert.DoesNotContain(c.Respostas, r => r.Contains("12345678901", StringComparison.Ordinal) || r.Contains("observacao_interna", StringComparison.Ordinal));

        // No banco: o segredo está cifrado e nenhuma execução guarda parâmetro nem resposta.
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT secret_ciphertext FROM capabilities.integration_connections WHERE id = @id", conexao);
        cmd.Parameters.AddWithValue("id", id);
        var cifrado = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.StartsWith("v1:", cifrado, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeErp.ApiKey, cifrado, StringComparison.Ordinal);
        await using var colunas = new NpgsqlCommand("SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_schema = 'capabilities' AND table_name = 'capability_executions'", conexao);
        var nomes = (string)(await colunas.ExecuteScalarAsync())!;
        Assert.DoesNotContain("param", nomes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response", nomes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body", nomes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conteudo_do_ERP_e_dado_so_os_campos_mapeados_saem_e_o_texto_de_erro_do_sistema_externo_nao_vaza()
    {
        await using var c = await IniciarAsync("caphostil");
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.consultar", "Read", MoldeDeLeitura());

        var (_, hostil) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 666 } });
        Assert.Equal("Succeeded", hostil!["status"]!.GetValue<string>());
        Assert.True(hostil["untrustedContent"]!.GetValue<bool>());
        Assert.Contains("IGNORE", hostil["output"]!["situacao"]!.GetValue<string>(), StringComparison.Ordinal);

        var (_, inexistente) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 999 } });
        Assert.Equal("Failed", inexistente!["status"]!.GetValue<string>());
        Assert.Equal("upstream-rejected", inexistente["error"]!["code"]!.GetValue<string>());
        Assert.Equal(404, inexistente["error"]!["upstreamStatus"]!.GetValue<int>());
        Assert.DoesNotContain(c.Respostas, r => r.Contains("texto do sistema externo", StringComparison.Ordinal) || r.Contains("pedido nao existe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Texto_livre_que_tenta_virar_url_ou_cabecalho_e_recusado_e_o_ERP_nao_e_chamado()
    {
        await using var c = await IniciarAsync("capinj");
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.consultar", "Read", MoldeDeLeitura());
        var antes = c.Erp.Received.Count;

        foreach (var corpo in new object[]
        {
            new { key = "pedidos.consultar", parameters = new { numero = "../../admin" } },
            new { key = "pedidos.consultar", parameters = new { numero = 1, url = "http://169.254.169.254/latest/meta-data/" } },
            new { key = "pedidos.consultar", parameters = new { numero = 1, baseUrl = "https://evil.example.com" } },
            new { key = "../../pedidos", parameters = new { numero = 1 } },
        })
        {
            var (status, resposta) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", corpo);
            Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, resposta?.ToJsonString());
        }

        Assert.Equal(antes, c.Erp.Received.Count);
    }

    // ------------------------------------------------------------ escrita

    [Fact]
    public async Task Escrita_espera_a_confirmacao_executa_uma_vez_com_a_chave_e_a_repeticao_nao_chama_o_ERP_de_novo()
    {
        await using var c = await IniciarAsync("capwrite");
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.cancelar", "Write", MoldeDeEscrita());
        var corpo = new { key = "pedidos.cancelar", parameters = new { numero = 7, motivo = "Paciente desistiu" }, idempotencyKey = "chave-api-0001" };

        var (s1, pendente) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", corpo);
        Assert.Equal(HttpStatusCode.Accepted, s1);
        Assert.Equal("AwaitingConfirmation", pendente!["status"]!.GetValue<string>());
        Assert.Equal(0, c.Erp.Cancellations);
        var execId = pendente["executionId"]!.GetValue<Guid>();

        var (s2, confirmada) = await c.Admin_(HttpMethod.Post, $"/capabilities/executions/{execId}/confirm");
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal("Succeeded", confirmada!["status"]!.GetValue<string>());
        Assert.StartsWith("PROTO-7-", confirmada["output"]!["protocolo"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, c.Erp.Cancellations);
        Assert.Contains(c.Erp.Received, r => r.Method == "POST" && r.IdempotencyKey == "chave-api-0001" && r.Body.Contains("Paciente desistiu", StringComparison.Ordinal));

        // A mesma intenção (mesma chave e parâmetros): devolve o resultado registrado, o ERP não é chamado de novo.
        var (s3, repetida) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", corpo);
        Assert.Equal(HttpStatusCode.OK, s3);
        Assert.True(repetida!["replayed"]!.GetValue<bool>());
        Assert.Equal(execId, repetida["executionId"]!.GetValue<Guid>());
        Assert.Equal(1, c.Erp.Cancellations);

        // A mesma chave com outros parâmetros é conflito.
        var (s4, conflito) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.cancelar", parameters = new { numero = 8, motivo = "Paciente desistiu" }, idempotencyKey = "chave-api-0001" });
        Assert.Equal(HttpStatusCode.Conflict, s4);
        Assert.Equal("idempotency-conflict", conflito!["error"]!["code"]!.GetValue<string>());

        // Confirmar de novo: não executa.
        Assert.Equal(HttpStatusCode.Conflict, (await c.Admin_(HttpMethod.Post, $"/capabilities/executions/{execId}/confirm")).Status);
        Assert.Equal(1, c.Erp.Cancellations);

        // Nada do que foi pedido ficou gravado: nem o motivo, nem o número, nem o protocolo.
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT row_to_json(e)::text FROM capabilities.capability_executions e WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", execId);
        var linha = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.DoesNotContain("Paciente desistiu", linha, StringComparison.Ordinal);
        Assert.DoesNotContain("PROTO-", linha, StringComparison.Ordinal);
        Assert.Contains("\"pending_ciphertext\":null", linha, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelar_uma_escrita_pendente_impede_a_execucao_e_exige_permissao_de_escrita()
    {
        await using var c = await IniciarAsync("capcancel");
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.cancelar", "Write", MoldeDeEscrita());
        var (_, pendente) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.cancelar", parameters = new { numero = 7, motivo = "x" }, idempotencyKey = "chave-api-0002" });
        var execId = pendente!["executionId"]!.GetValue<Guid>();
        var gerente = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.Manager));

        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(gerente, HttpMethod.Post, $"/capabilities/executions/{execId}/confirm")).Status);
        Assert.Equal(HttpStatusCode.OK, (await c.Admin_(HttpMethod.Post, $"/capabilities/executions/{execId}/cancel")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await c.Admin_(HttpMethod.Post, $"/capabilities/executions/{execId}/confirm")).Status);
        Assert.Equal(0, c.Erp.Cancellations);
    }

    // ------------------------------------------------------------ concorrência (índices únicos do banco)

    [Fact]
    public async Task Chamadas_simultaneas_com_a_mesma_chave_executam_o_cancelamento_exatamente_uma_vez()
    {
        await using var c = await IniciarAsync("capconc");
        c.Erp.CancelDelay = TimeSpan.FromMilliseconds(400);
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.cancelar", "Write", MoldeDeEscrita(), confirmar: false);
        var corpo = new { key = "pedidos.cancelar", parameters = new { numero = 21, motivo = "Duplicidade" }, idempotencyKey = "chave-conc-0001" };

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => c.Chamar(c.Admin, HttpMethod.Post, "/capabilities/executions", corpo)));

        Assert.Equal(1, c.Erp.Cancellations);
        Assert.Equal(1, respostas.Count(r => r.Corpo!["replayed"]!.GetValue<bool>() == false));
        Assert.All(respostas, r => Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.Conflict, r.Corpo!.ToJsonString()));
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM capabilities.capability_executions WHERE tenant_id = @t AND idempotency_key = 'chave-conc-0001'", conn);
        cmd.Parameters.AddWithValue("t", c.Tenant.TenantId.Value);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Confirmacoes_simultaneas_da_mesma_escrita_so_uma_executa()
    {
        await using var c = await IniciarAsync("capconf");
        c.Erp.CancelDelay = TimeSpan.FromMilliseconds(300);
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.cancelar", "Write", MoldeDeEscrita());
        var (_, pendente) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.cancelar", parameters = new { numero = 31, motivo = "x" }, idempotencyKey = "chave-conf-0001" });
        var execId = pendente!["executionId"]!.GetValue<Guid>();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.Chamar(c.Admin, HttpMethod.Post, $"/capabilities/executions/{execId}/confirm")));

        Assert.Equal(1, respostas.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(respostas.Where(r => r.Status != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.Status));
        Assert.Equal(1, c.Erp.Cancellations);
    }

    [Fact]
    public async Task Publicacoes_simultaneas_do_mesmo_rascunho_deixam_exatamente_uma_versao_publicada()
    {
        await using var c = await IniciarAsync("cappub");
        var conexao = await ConexaoPronta(c);
        var (_, rascunho) = await c.Admin_(HttpMethod.Post, "/capabilities/definitions", Operacao(conexao, "pedidos.consultar", "x", "Read", MoldeDeLeitura()));
        var id = rascunho!["id"]!.GetValue<Guid>();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => c.Chamar(c.Admin, HttpMethod.Post, $"/capabilities/definitions/{id}/publish")));

        Assert.True(respostas.Count(r => r.Status == HttpStatusCode.OK) >= 1);
        Assert.All(respostas, r => Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.Conflict, r.Corpo?.ToJsonString()));
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM capabilities.capability_definitions WHERE tenant_id = @t AND key = 'pedidos.consultar' AND status = 2", conn);
        cmd.Parameters.AddWithValue("t", c.Tenant.TenantId.Value);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Versionar_aposenta_a_publicada_na_mesma_transacao()
    {
        await using var c = await IniciarAsync("capver");
        var conexao = await ConexaoPronta(c);
        var v1 = await OperacaoPublicada(c, conexao, "pedidos.consultar", "Read", MoldeDeLeitura());
        var (_, v2) = await c.Admin_(HttpMethod.Post, "/capabilities/definitions", Operacao(conexao, "pedidos.consultar", "x", "Read", MoldeDeLeitura(tentativas: 2)));
        Assert.Equal(2, v2!["version"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.OK, (await c.Admin_(HttpMethod.Post, $"/capabilities/definitions/{v2["id"]!.GetValue<Guid>()}/publish")).Status);

        var (_, versoes) = await c.Admin_(HttpMethod.Get, "/capabilities/definitions/by-key/pedidos.consultar/versions");
        Assert.Equal(["2:Published", "1:Retired"], versoes!.AsArray().Select(v => $"{v!["version"]}:{v["status"]}").ToArray());
        var (_, antiga) = await c.Admin_(HttpMethod.Get, $"/capabilities/definitions/{v1}");
        Assert.Equal("Retired", antiga!["status"]!.GetValue<string>());
    }

    // ------------------------------------------------------------ isolamento

    [Fact]
    public async Task Um_tenant_nunca_alcanca_conexoes_operacoes_e_execucoes_de_outro()
    {
        await using var a = await IniciarAsync("capisoa");
        var conexao = await ConexaoPronta(a);
        var def = await OperacaoPublicada(a, conexao, "pedidos.cancelar", "Write", MoldeDeEscrita());
        var (_, pendente) = await a.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.cancelar", parameters = new { numero = 1, motivo = "x" }, idempotencyKey = "chave-iso-0001" });
        var execId = pendente!["executionId"]!.GetValue<Guid>();

        var tenantB = await banco.RegistrarTenantAsync("capisob");
        var adminB = await a.Api.LoginAsync(await banco.CriarAdministradorDoTenantAsync(tenantB));

        Assert.Empty((await a.Chamar(adminB, HttpMethod.Get, "/capabilities/connections")).Corpo!.AsArray());
        Assert.Empty((await a.Chamar(adminB, HttpMethod.Get, "/capabilities/definitions")).Corpo!.AsArray());
        Assert.Empty((await a.Chamar(adminB, HttpMethod.Get, "/capabilities/catalog")).Corpo!.AsArray());
        Assert.Equal(0, (await a.Chamar(adminB, HttpMethod.Get, "/capabilities/executions")).Corpo!["total"]!.GetValue<int>());

        foreach (var (metodo, caminho, corpo) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/capabilities/connections/{conexao}", null),
            (HttpMethod.Put, $"/capabilities/connections/{conexao}/secret", new { values = new Dictionary<string, string> { ["apiKey"] = "x" }, expectedRevision = 1 }),
            (HttpMethod.Post, $"/capabilities/connections/{conexao}/enable", null),
            (HttpMethod.Post, $"/capabilities/connections/{conexao}/test", null),
            (HttpMethod.Get, $"/capabilities/definitions/{def}", null),
            (HttpMethod.Post, $"/capabilities/definitions/{def}/publish", null),
            (HttpMethod.Post, $"/capabilities/definitions/{def}/test", new { parameters = new { numero = 1 } }),
            (HttpMethod.Get, $"/capabilities/executions/{execId}", null),
            (HttpMethod.Post, $"/capabilities/executions/{execId}/confirm", null),
            (HttpMethod.Post, $"/capabilities/executions/{execId}/cancel", null),
            (HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.cancelar", parameters = new { numero = 1, motivo = "x" }, idempotencyKey = "chave-iso-0002" }),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await a.Chamar(adminB, metodo, caminho, corpo)).Status);
        }

        // O mesmo corpo para criar uma operação em cima da conexão do outro tenant é recusado (a conexão "não existe" para B).
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Chamar(adminB, HttpMethod.Post, "/capabilities/definitions", Operacao(conexao, "pedidos.x", "x", "Read", MoldeDeLeitura()))).Status);
        Assert.Equal(0, a.Erp.Cancellations);

        // A mesma chave de conexão em outro tenant é livre, e a chave de idempotência também.
        Assert.Equal(HttpStatusCode.Created, (await a.Chamar(adminB, HttpMethod.Post, "/capabilities/connections", ConexaoComApiKey(a.Erp, "erp-principal"))).Status);
    }

    [Fact]
    public async Task O_escopo_de_organizacao_vale_para_ver_administrar_e_executar()
    {
        await using var c = await IniciarAsync("caporg");
        var org2 = await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização");
        var conexaoDaOrg1 = await ConexaoPronta(c, "erp-org1", c.Org.Id);
        var conexaoDaOrg2 = await ConexaoPronta(c, "erp-org2", org2.Id);
        await OperacaoPublicada(c, conexaoDaOrg1, "org1.consultar", "Read", MoldeDeLeitura());
        await OperacaoPublicada(c, conexaoDaOrg2, "org2.consultar", "Read", MoldeDeLeitura());
        var adminDaOrg1 = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.OrganizationAdmin));

        var (_, lista) = await c.Chamar(adminDaOrg1, HttpMethod.Get, "/capabilities/connections");
        Assert.Equal(["erp-org1"], lista!.AsArray().Select(x => x!["key"]!.GetValue<string>()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Chamar(adminDaOrg1, HttpMethod.Get, $"/capabilities/connections/{conexaoDaOrg2}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(adminDaOrg1, HttpMethod.Post, $"/capabilities/connections/{conexaoDaOrg2}/disable")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(adminDaOrg1, HttpMethod.Post, "/capabilities/executions", new { key = "org2.consultar", parameters = new { numero = 1 } })).Status);
        Assert.Equal(HttpStatusCode.OK, (await c.Chamar(adminDaOrg1, HttpMethod.Post, "/capabilities/executions", new { key = "org1.consultar", parameters = new { numero = 1 } })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(adminDaOrg1, HttpMethod.Post, "/capabilities/connections", ConexaoComApiKey(c.Erp, "do-tenant", organizacao: null))).Status);

        var (_, catalogo) = await c.Chamar(adminDaOrg1, HttpMethod.Get, "/capabilities/catalog");
        Assert.Equal(["org1.consultar"], catalogo!.AsArray().Select(x => x!["key"]!.GetValue<string>()).ToArray());
        var (_, historico) = await c.Chamar(adminDaOrg1, HttpMethod.Get, "/capabilities/executions");
        Assert.Equal(1, historico!["total"]!.GetValue<int>());
    }

    // ------------------------------------------------------------ SSRF, retentativa, tempo e limites

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://localhost/api")]
    [InlineData("https://10.0.0.5/api")]
    [InlineData("https://[::1]/api")]
    [InlineData("https://metadata.google.internal/")]
    [InlineData("https://user:pass@erp.example.com/api")]
    [InlineData("file:///etc/passwd")]
    public async Task Destinos_internos_e_de_metadados_sao_recusados_no_cadastro_da_conexao(string url)
    {
        await using var c = await IniciarAsync("capssrf");
        var corpo = new { key = "ruim", name = "Ruim", baseUrl = url, authType = "None" };

        var (status, resposta) = await c.Admin_(HttpMethod.Post, "/capabilities/connections", corpo);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("baseUrl", resposta!["problems"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty((await c.Admin_(HttpMethod.Get, "/capabilities/connections")).Corpo!.AsArray());
    }

    [Fact]
    public async Task Redirecionamento_do_ERP_nunca_e_seguido()
    {
        await using var c = await IniciarAsync("capredir");
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "redirecionar", "Read", new
        {
            method = "GET",
            pathTemplate = "/redirecionar",
            parameters = Array.Empty<object>(),
            response = new[] { new { name = "situacao", pointer = "/status", type = "String", required = true } },
            idempotency = "Unspecified",
            maxAttempts = 3,
        });

        var (_, r) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "redirecionar", parameters = new { } });

        Assert.Equal("Failed", r!["status"]!.GetValue<string>());
        Assert.Equal("redirect-blocked", r["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, c.Erp.RedirectTargetHits);
    }

    [Fact]
    public async Task Leitura_repete_o_503_ate_o_limite_do_molde_e_sem_o_molde_nao_repete()
    {
        await using var c = await IniciarAsync("capretry");
        var conexao = await ConexaoPronta(c);
        var molde = (int tentativas) => new
        {
            method = "GET",
            pathTemplate = "/instavel/{numero}",
            parameters = new[] { new { name = "numero", type = "Integer", location = "Path", required = true, min = 1, max = 99 } },
            response = new[] { new { name = "situacao", pointer = "/status", type = "String", required = true } },
            idempotency = "Unspecified",
            maxAttempts = tentativas,
        };
        await OperacaoPublicada(c, conexao, "instavel.com.retry", "Read", molde(3));
        await OperacaoPublicada(c, conexao, "instavel.sem.retry", "Read", molde(1));

        var (_, sem) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "instavel.sem.retry", parameters = new { numero = 1 } });
        Assert.Equal("Failed", sem!["status"]!.GetValue<string>());
        Assert.Equal("unavailable", sem["error"]!["code"]!.GetValue<string>());
        Assert.True(sem["error"]!["retryable"]!.GetValue<bool>());
        Assert.Equal(1, c.Erp.Received.Count(r => r.Path.StartsWith("/instavel", StringComparison.Ordinal)));

        var (_, com) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "instavel.com.retry", parameters = new { numero = 2 } });
        Assert.Equal("Succeeded", com!["status"]!.GetValue<string>());
        Assert.Equal("OK-APOS-RETRY", com["output"]!["situacao"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tempo_esgotado_da_leitura_vira_timeout_estruturado()
    {
        await using var c = await IniciarAsync("captimeout");
        var conexao = await ConexaoPronta(c, timeout: 1);
        await OperacaoPublicada(c, conexao, "lento", "Read", new
        {
            method = "GET",
            pathTemplate = "/lento",
            parameters = Array.Empty<object>(),
            response = new[] { new { name = "situacao", pointer = "/status", type = "String", required = true } },
            idempotency = "Unspecified",
            maxAttempts = 1,
        });

        var (_, r) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "lento", parameters = new { } });

        Assert.Equal("Failed", r!["status"]!.GetValue<string>());
        Assert.Equal("timeout", r["error"]!["code"]!.GetValue<string>());
        Assert.True(r["error"]!["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task O_limite_de_chamadas_por_minuto_por_conexao_responde_429()
    {
        await using var c = await IniciarAsync("caprate", cfg => cfg["Capabilities:Options:PerConnectionPerMinute"] = "3");
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.consultar", "Read", MoldeDeLeitura());

        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = i } })).Status);
        }

        var (status, resposta) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 4 } });
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("rate-limited", resposta!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task O_interruptor_global_desliga_a_execucao_mas_nao_a_administracao()
    {
        await using var c = await IniciarAsync("capoff", cfg => cfg["Capabilities:Options:Enabled"] = "false");
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.consultar", "Read", MoldeDeLeitura());

        var (status, resposta) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 1 } });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("module-disabled", resposta!["error"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain(c.Erp.Received, r => r.Path.StartsWith("/pedidos", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ segredos e auditoria

    [Fact]
    public async Task Sem_chave_de_cifra_configurada_gravar_o_segredo_responde_503_e_nada_e_gravado()
    {
        await using var c = await IniciarAsync("capnokey", cfg => cfg.Remove("Capabilities:Secrets:ActiveKeyId"));
        var (_, criada) = await c.Admin_(HttpMethod.Post, "/capabilities/connections", ConexaoComApiKey(c.Erp));
        var id = criada!["id"]!.GetValue<Guid>();

        var (status, resposta) = await c.Admin_(HttpMethod.Put, $"/capabilities/connections/{id}/secret", new { values = new Dictionary<string, string> { ["apiKey"] = "segredo" }, expectedRevision = 1 });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("secrets-unavailable", resposta!["code"]!.GetValue<string>());
        Assert.False((await c.Admin_(HttpMethod.Get, $"/capabilities/connections/{id}")).Corpo!["hasSecret"]!.GetValue<bool>());
        Assert.DoesNotContain(c.Respostas, r => r.Contains("segredo\"", StringComparison.Ordinal) && r.Contains("apiKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_trilha_de_auditoria_registra_configuracao_execucao_e_negativas_sem_valores()
    {
        await using var c = await IniciarAsync("capaudit");
        var conexao = await ConexaoPronta(c);
        await OperacaoPublicada(c, conexao, "pedidos.consultar", "Read", MoldeDeLeitura());
        await OperacaoPublicada(c, conexao, "pedidos.cancelar", "Write", MoldeDeEscrita());
        await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 4242 } });
        var (_, pendente) = await c.Admin_(HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.cancelar", parameters = new { numero = 5, motivo = "Motivo sigiloso" }, idempotencyKey = "chave-aud-0001" });
        await c.Admin_(HttpMethod.Post, $"/capabilities/executions/{pendente!["executionId"]!.GetValue<Guid>()}/confirm");
        var comum = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.User));
        await c.Chamar(comum, HttpMethod.Post, "/capabilities/executions", new { key = "pedidos.consultar", parameters = new { numero = 1 } });

        var (status, eventos) = await c.Admin_(HttpMethod.Get, "/audit-events?take=200");

        Assert.Equal(HttpStatusCode.OK, status);
        var operacoes = eventos!.AsArray().Select(e => $"{e!["operation"]}:{e["outcome"]}").ToList();
        foreach (var esperada in new[]
        {
            "capabilities.connection.create:Success", "capabilities.connection.set-secret:Success", "capabilities.connection.enable:Success", "capabilities.definition.save:Success",
            "capabilities.definition.publish:Success", "capabilities.execute:Success", "capabilities.execution.request:Success", "capabilities.execution.confirm:Success",
        })
        {
            Assert.Contains(esperada, operacoes);
        }

        var texto = eventos.ToJsonString();
        foreach (var proibido in new[] { FakeErp.ApiKey, "Motivo sigiloso", "4242", "PROTO-" })
        {
            Assert.DoesNotContain(proibido, texto, StringComparison.Ordinal);
        }

        // A negativa do usuário comum também fica na trilha (outcome Denied), sem valores.
        Assert.Contains("capabilities.execute:Denied", operacoes);
    }

    // ------------------------------------------------------------ invariantes do banco

    [Fact]
    public async Task O_banco_recusa_por_conta_propria_duas_publicadas_escrita_sem_chave_e_parametros_fora_de_confirmacao()
    {
        await using var c = await IniciarAsync("capdb");
        var conexao = await ConexaoPronta(c);
        var def = await OperacaoPublicada(c, conexao, "pedidos.consultar", "Read", MoldeDeLeitura());
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync();

        async Task<string?> Tentar(string sql, params (string, object)[] parametros)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (nome, valor) in parametros)
            {
                cmd.Parameters.AddWithValue(nome, valor);
            }

            try
            {
                await cmd.ExecuteNonQueryAsync();
                return null;
            }
            catch (PostgresException ex)
            {
                return ex.SqlState + ":" + ex.ConstraintName;
            }
        }

        var t = c.Tenant.TenantId.Value;

        // Duas publicadas da mesma chave.
        var duplicada = await Tentar(
            "INSERT INTO capabilities.capability_definitions (tenant_id, id, connection_id, key, version, status, name, effect, requires_confirmation, allowed_callers, enabled, spec_json, created_at, updated_at, published_at, revision) " +
            "VALUES (@t, gen_random_uuid(), @c, 'pedidos.consultar', 9, 2, 'x', 1, false, 1, true, '{}', now(), now(), now(), 1)",
            ("t", t), ("c", conexao));
        Assert.Equal("23505:ux_capability_definitions_published", duplicada);

        // Escrita sem chave de idempotência.
        var semChave = await Tentar(
            "INSERT INTO capabilities.capability_executions (tenant_id, id, definition_id, definition_key, definition_version, connection_id, effect, caller, status, attempts, is_test, created_at, revision) " +
            "VALUES (@t, gen_random_uuid(), @d, 'k', 1, @c, 2, 1, 6, 0, false, now(), 1)",
            ("t", t), ("d", def), ("c", conexao));
        Assert.Equal("23514:ck_capability_executions_write_key", semChave);

        // Parâmetros guardados fora do estado "aguardando confirmação".
        var vazamento = await Tentar(
            "INSERT INTO capabilities.capability_executions (tenant_id, id, definition_id, definition_key, definition_version, connection_id, effect, caller, status, attempts, is_test, created_at, revision, pending_ciphertext, pending_key_id) " +
            "VALUES (@t, gen_random_uuid(), @d, 'k', 1, @c, 1, 1, 1, 0, false, now(), 1, 'v1:x', 'k1')",
            ("t", t), ("d", def), ("c", conexao));
        Assert.Equal("23514:ck_capability_executions_pending", vazamento);

        // Chave de idempotência repetida no mesmo tenant.
        var primeira = await Tentar(
            "INSERT INTO capabilities.capability_executions (tenant_id, id, definition_id, definition_key, definition_version, connection_id, effect, caller, status, attempts, is_test, created_at, revision, idempotency_key) " +
            "VALUES (@t, gen_random_uuid(), @d, 'k', 1, @c, 2, 1, 2, 0, false, now(), 1, 'chave-db-0001')",
            ("t", t), ("d", def), ("c", conexao));
        var repetida = await Tentar(
            "INSERT INTO capabilities.capability_executions (tenant_id, id, definition_id, definition_key, definition_version, connection_id, effect, caller, status, attempts, is_test, created_at, revision, idempotency_key) " +
            "VALUES (@t, gen_random_uuid(), @d, 'k', 1, @c, 2, 1, 2, 0, false, now(), 1, 'chave-db-0001')",
            ("t", t), ("d", def), ("c", conexao));
        Assert.Null(primeira);
        Assert.Equal("23505:ux_capability_executions_idempotency", repetida);

        // Segredo sem a chave que o cifrou.
        var semChaveDoSegredo = await Tentar("UPDATE capabilities.integration_connections SET secret_key_id = NULL WHERE id = @c AND secret_ciphertext IS NOT NULL", ("c", conexao));
        Assert.Equal("23514:ck_integration_connections_secret", semChaveDoSegredo);
    }
}
