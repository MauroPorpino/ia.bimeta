using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// A administração das conexões de integração (ADR 0039): só EmbedManage (escopo do tenant) administra; o segredo aparece uma única vez; no máximo duas chaves ativas; vínculos explícitos que recusam conta
/// administrativa; isolamento entre tenants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedAdminApiTests(PostgresFixture banco)
{
    private static async Task<string> UnicoAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        return Convert.ToString(await comando.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static object NovaConexao(Guid organizacao, string provisionamento = "MappedOnly", string[]? origens = null, string modo = "FullAttendant") => new
    {
        organizationId = organizacao,
        name = "ERP de teste",
        mode = modo,
        provisioning = provisionamento,
        allowedOrigins = origens ?? new[] { EmbeddedTestSupport.Origem },
        allowStartConversation = false,
    };

    [Fact]
    public async Task Criar_conexao_e_chave_mostra_o_segredo_uma_unica_vez_e_a_leitura_nunca_o_devolve()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "segredo");
        await using var servidor = c.Api;

        Assert.StartsWith("ibk_", c.Chave.KeyId);
        Assert.StartsWith("ibsk_", c.Chave.Secret);
        Assert.Equal("Active", c.Conexao.Status);
        Assert.False(c.Conexao.AllowStartConversation);

        using var lista = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Get, $"/embedded/connections/{c.Conexao.Id}/keys");
        var texto = await lista.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        Assert.Contains(c.Chave.KeyId, texto);
        Assert.DoesNotContain(c.Chave.Secret, texto);
        Assert.DoesNotContain("secret", texto, StringComparison.OrdinalIgnoreCase);

        using var criacao = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys", new { });
        Assert.Contains("no-store", criacao.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task So_quem_tem_EmbedManage_com_escopo_do_tenant_administra_e_sem_sessao_responde_401()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "perm");
        await using var servidor = c.Api;
        var atendente = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        var gerente = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Manager);
        var adminDaOrg = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.OrganizationAdmin);

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(null, HttpMethod.Get, "/embedded/connections"));
        foreach (var usuario in new[] { atendente, gerente, adminDaOrg })
        {
            var token = await c.Api.LoginAsync(usuario);
            Assert.Equal(HttpStatusCode.Forbidden, await c.Api.StatusAsync(token, HttpMethod.Get, "/embedded/connections"));
            Assert.Equal(HttpStatusCode.Forbidden, await c.Api.StatusAsync(token, HttpMethod.Post, "/embedded/connections", NovaConexao(c.Organizacao.Id)));
            Assert.Equal(HttpStatusCode.Forbidden, await c.Api.StatusAsync(token, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys", new { }));
        }

        // As negações entram na trilha.
        // Exatamente as negações feitas (três usuários x três tentativas), por operação; nenhuma negação a mais ou a menos.
        Assert.Equal(
            "identity.embed.connection.create=3;identity.embed.connection.list=3;identity.embed.key.create=3",
            await UnicoAsync("SELECT string_agg(operation || '=' || n, ';' ORDER BY operation) FROM (SELECT operation, count(*) n FROM audit.audit_events WHERE tenant_id = @t AND operation LIKE 'identity.embed.%' AND outcome = 2 GROUP BY operation) x", ("t", c.Tenant.TenantId.Value)));
    }

    [Theory]
    [InlineData("http://erp.exemplo.com.br")]
    [InlineData("https://*.exemplo.com.br")]
    [InlineData("https://erp.exemplo.com.br/painel")]
    [InlineData("https://erp.exemplo.com.br?x=1")]
    [InlineData("https://usuario@erp.exemplo.com.br")]
    [InlineData("ftp://erp.exemplo.com.br")]
    [InlineData("")]
    [InlineData("erp.exemplo.com.br")]
    public async Task Origem_invalida_na_conexao_e_recusada(string origem)
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "origens");
        await using var servidor = c.Api;

        Assert.Equal(HttpStatusCode.BadRequest, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, "/embedded/connections", NovaConexao(c.Organizacao.Id, origens: new[] { origem })));
        Assert.Equal(HttpStatusCode.BadRequest, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}", NovaConexao(c.Organizacao.Id, origens: new[] { origem })));
    }

    [Fact]
    public async Task Origens_sao_normalizadas_sem_duplicata_e_localhost_aceita_http()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "origens2");
        await using var servidor = c.Api;

        var conexao = await c.Api.CriarAsync<ConexaoDto>(c.TokenDoAdmin, "/embedded/connections", NovaConexao(
            c.Organizacao.Id, origens: new[] { "HTTPS://ERP.Exemplo.com.br:443", "https://erp.exemplo.com.br", "http://localhost:5100", "http://erp.localhost:8080" }));

        Assert.Equal(new[] { "https://erp.exemplo.com.br", "http://localhost:5100", "http://erp.localhost:8080" }, conexao.AllowedOrigins);
    }

    [Fact]
    public async Task Conexao_exige_modo_e_provisionamento_validos_e_organizacao_do_tenant()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "dados");
        await using var servidor = c.Api;
        var outro = await EmbeddedCenario.CriarAsync(banco, "dados-b", api: c.Api);

        Assert.Equal(HttpStatusCode.BadRequest, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, "/embedded/connections", NovaConexao(c.Organizacao.Id, provisionamento: "Qualquer")));
        Assert.Equal(HttpStatusCode.BadRequest, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, "/embedded/connections", NovaConexao(c.Organizacao.Id, modo: "Total")));
        Assert.Equal(HttpStatusCode.NotFound, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, "/embedded/connections", NovaConexao(Guid.NewGuid())));
        // A organização de OUTRO tenant não existe para este.
        Assert.Equal(HttpStatusCode.NotFound, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, "/embedded/connections", NovaConexao(outro.Organizacao.Id)));
    }

    [Fact]
    public async Task Um_tenant_nao_ve_nem_altera_as_conexoes_chaves_e_vinculos_de_outro()
    {
        var a = await EmbeddedCenario.CriarAsync(banco, "tenant-a");
        await using var servidor = a.Api;
        var b = await EmbeddedCenario.CriarAsync(banco, "tenant-b", api: a.Api);

        var listaDeB = await a.Api.LerAsync<List<ConexaoDto>>(b.TokenDoAdmin, "/embedded/connections");
        Assert.Equal(b.Conexao.Id, Assert.Single(listaDeB).Id);

        var caminhos = new[]
        {
            (HttpMethod.Get, $"/embedded/connections/{a.Conexao.Id}"),
            (HttpMethod.Get, $"/embedded/connections/{a.Conexao.Id}/keys"),
            (HttpMethod.Get, $"/embedded/connections/{a.Conexao.Id}/identities"),
            (HttpMethod.Post, $"/embedded/connections/{a.Conexao.Id}/keys"),
            (HttpMethod.Post, $"/embedded/connections/{a.Conexao.Id}/disable"),
            (HttpMethod.Post, $"/embedded/connections/{a.Conexao.Id}/keys/{a.Chave.KeyId}/revoke"),
            (HttpMethod.Post, $"/embedded/connections/{a.Conexao.Id}/sessions/revoke"),
        };
        foreach (var (metodo, caminho) in caminhos)
        {
            Assert.Equal(HttpStatusCode.NotFound, await a.Api.StatusAsync(b.TokenDoAdmin, metodo, caminho, metodo == HttpMethod.Post ? new { } : null));
        }

        // A chave de A segue valendo: nada do que B tentou a afetou.
        using var emitir = await a.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-1", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem });
        Assert.Equal(HttpStatusCode.Created, emitir.StatusCode);
    }

    [Fact]
    public async Task No_maximo_duas_chaves_ativas_por_conexao_e_a_rotacao_respeita_o_limite()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "limite-chaves");
        await using var servidor = c.Api;
        var caminho = $"/embedded/connections/{c.Conexao.Id}/keys";

        var segunda = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, caminho, new { });
        Assert.Equal(HttpStatusCode.Conflict, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, caminho, new { }));
        Assert.Equal(HttpStatusCode.Conflict, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, caminho + "/rotate", new { graceHours = 1 }));

        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"{caminho}/{segunda.KeyId}/revoke"));
        _ = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, caminho, new { });

        var chaves = await c.Api.LerAsync<List<ChaveDto>>(c.TokenDoAdmin, caminho);
        Assert.Equal(3, chaves.Count);
        Assert.Equal(2, chaves.Count(k => k.Status == "Active"));
    }

    [Fact]
    public async Task Duas_criacoes_simultaneas_de_chave_nao_passam_do_limite()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "limite-par");
        await using var servidor = c.Api;
        var caminho = $"/embedded/connections/{c.Conexao.Id}/keys";

        var status = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, caminho, new { })));

        Assert.Equal(1, status.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(5, status.Count(s => s == HttpStatusCode.Conflict));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(200)]
    public async Task Carencia_da_rotacao_fora_do_limite_e_recusada(double horas)
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "carencia");
        await using var servidor = c.Api;

        Assert.Equal(HttpStatusCode.BadRequest, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/rotate", new { graceHours = horas }));
    }

    [Fact]
    public async Task Sem_chave_mestra_configurada_criar_chave_falha_fechado_e_sem_vazar_nada()
    {
        var tenant = await banco.RegistrarTenantAsync("sem-mestra");
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, "Organização");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        var configuracao = EmbeddedTestSupport.Configuracao(cfg =>
        {
            cfg.Remove("Identity:Secrets:ActiveKeyId");
            cfg.Remove("Identity:Secrets:Keys:t1");
        });
        await using var api = await ApiServer.StartAsync("Production", configuracao);
        var token = await api.LoginAsync(admin);
        var conexao = await api.CriarAsync<ConexaoDto>(token, "/embedded/connections", NovaConexao(organizacao.Id));

        using var resposta = await api.EnviarAsync(token, HttpMethod.Post, $"/embedded/connections/{conexao.Id}/keys", new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.Equal("secrets-unavailable", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
        Assert.Equal("0", await UnicoAsync("SELECT count(*) FROM identity.integration_keys WHERE tenant_id = @t", ("t", tenant.TenantId.Value)));
    }

    // Vínculos explícitos

    [Fact]
    public async Task Mapear_vinculo_exige_usuario_atendente_ou_gerente_da_organizacao_e_recusa_o_resto()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "map");
        await using var servidor = c.Api;
        var outro = await EmbeddedCenario.CriarAsync(banco, "map-b", api: c.Api);
        var outraOrg = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var caminho = $"/embedded/connections/{c.Conexao.Id}/identities";

        var atendente = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        var gerente = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Manager);
        var adminDaOrg = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.OrganizationAdmin);
        var deOutraOrg = await banco.CriarUsuarioAsync(c.Tenant, outraOrg.Id, Role.Attendant);
        var doOutroTenant = await banco.CriarUsuarioAsync(outro.Tenant, outro.Organizacao.Id, Role.Attendant);

        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-1", userId = atendente.Id }));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-2", userId = gerente.Id }));

        // Conta administrativa (papel de administrador da organização ou o administrador do tenant), sem acesso à organização da conexão: recusados.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-3", userId = adminDaOrg.Id }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-4", userId = c.Admin.Id }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-5", userId = deOutraOrg.Id }));
        // Usuário de outro tenant não existe para este.
        Assert.Equal(HttpStatusCode.NotFound, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-6", userId = doOutroTenant.Id }));
        Assert.Equal(HttpStatusCode.NotFound, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-7", userId = Guid.NewGuid() }));

        Assert.Equal(2, (await c.Api.LerAsync<List<VinculoDto>>(c.TokenDoAdmin, caminho)).Count);
    }

    [Fact]
    public async Task Vinculo_duplicado_do_usuario_externo_ou_do_usuario_interno_e_conflito()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "dup");
        await using var servidor = c.Api;
        var caminho = $"/embedded/connections/{c.Conexao.Id}/identities";
        var a = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        var b = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);

        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-1", userId = a.Id }));
        Assert.Equal(HttpStatusCode.Conflict, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-1", userId = b.Id }));
        Assert.Equal(HttpStatusCode.Conflict, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, caminho, new { externalUserId = "e-2", userId = a.Id }));
    }

    [Fact]
    public async Task Usuario_federado_nao_pode_ser_mapeado_de_novo()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "federado");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-jit");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}/identities", new { externalUserId = "outro", userId = troca.User.Id }));
    }

    [Fact]
    public async Task Atualizar_a_conexao_troca_origens_e_a_origem_removida_deixa_de_valer_para_tickets_novos()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "atualiza");
        await using var servidor = c.Api;

        var atualizada = await (await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}", NovaConexao(c.Organizacao.Id, "JitAttendant", new[] { "https://novo-erp.exemplo.com.br" }, "CustomerOnly"))).Content.ReadFromJsonAsync<ConexaoDto>();

        Assert.Equal(new[] { "https://novo-erp.exemplo.com.br" }, atualizada!.AllowedOrigins);
        Assert.Equal("CustomerOnly", atualizada.Mode);
        using var antiga = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-1", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem });
        Assert.Equal(HttpStatusCode.BadRequest, antiga.StatusCode);
    }

    [Fact]
    public async Task Ticket_emitido_antes_de_a_origem_sair_da_lista_nao_troca_depois()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "origem-saiu");
        await using var servidor = c.Api;
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await c.Erp.EmitirAsync("u-1", EmbeddedTestSupport.Challenge(verifier));

        _ = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}", NovaConexao(c.Organizacao.Id, "JitAttendant", new[] { "https://novo-erp.exemplo.com.br" }));

        Assert.Equal(HttpStatusCode.Unauthorized, await EmbeddedTestSupport.TentarTrocarAsync(c.Api, ticket.Ticket, verifier));
    }

    [Fact]
    public async Task Revogacao_administrativa_derruba_as_sessoes_embutidas_da_conexao()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "revoga-adm");
        await using var servidor = c.Api;
        var (ana, _) = await c.EntrarAsync("u-ana");
        var (beto, _) = await c.EntrarAsync("u-beto");

        using var so_ana = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/sessions/revoke", new { externalUserId = "u-ana" });
        Assert.Equal(1, (await so_ana.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(ana.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(beto.Token, HttpMethod.Get, "/embed/v1/session"));

        using var todas = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/sessions/revoke", new { });
        Assert.Equal(1, (await todas.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(beto.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    [Fact]
    public async Task Toda_mudanca_administrativa_e_auditada_sem_segredo()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "auditoria");
        await using var servidor = c.Api;
        _ = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}", NovaConexao(c.Organizacao.Id, "JitAttendant"));
        _ = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/disable");

        _ = await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId); // a auditoria de sucesso chega à trilha pelo Worker (outbox, E1-R quinta rodada)
        var operacoes = await UnicoAsync("SELECT string_agg(DISTINCT operation, ',') FROM audit.audit_events WHERE tenant_id = @t AND operation LIKE 'identity.embed.%'", ("t", c.Tenant.TenantId.Value));
        var tudo = await UnicoAsync("SELECT string_agg(row_to_json(e)::text, ' ') FROM audit.audit_events e WHERE e.tenant_id = @t", ("t", c.Tenant.TenantId.Value));

        Assert.Contains("identity.embed.connection.create", operacoes);
        Assert.Contains("identity.embed.key.create", operacoes);
        Assert.Contains("identity.embed.connection.update", operacoes);
        Assert.Contains("identity.embed.connection.disable", operacoes);
        Assert.DoesNotContain(c.Chave.Secret, tudo);
    }
}
