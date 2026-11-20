using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IaBimeta.Api.RequestContexts;
using IaBimeta.IntegrationTests.Api;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// E1-R: regressão de cada achado da auditoria independente da E1 que toca a federação (revogação, tokens, ticket de contexto, concorrência, taxa, corpo, timestamp, rotas públicas). Cada teste descreve a falha que existia e o
/// comportamento correto agora. API real, PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedHardeningApiTests(PostgresFixture banco)
{
    private static async Task<long> EscalarAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }

    private static async Task<int> StatusDaTrocaAsync(ApiServer api, string ticket, string verifier) =>
        (int)await EmbeddedTestSupport.TentarTrocarAsync(api, ticket, verifier);

    // Item 3: revogação (logout com ticket pendente, corrida troca x revogação, chave, origens)

    [Fact]
    public async Task Logout_do_ERP_invalida_o_ticket_pendente_do_mesmo_usuario_e_nao_o_de_outro()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "logout-pendente");
        await using var servidor = c.Api;
        _ = await c.EntrarAsync("u-ana"); // provisiona o vínculo
        _ = await c.EntrarAsync("u-beto");
        var verifierAna = EmbeddedTestSupport.NovoVerifier();
        var verifierBeto = EmbeddedTestSupport.NovoVerifier();
        var pendenteDaAna = await c.Erp.EmitirAsync("u-ana", EmbeddedTestSupport.Challenge(verifierAna));
        var pendenteDoBeto = await c.Erp.EmitirAsync("u-beto", EmbeddedTestSupport.Challenge(verifierBeto));

        using var logout = await c.Erp.EnviarAsync("/embed/v1/sessions/revoke", new { externalUserId = "u-ana" });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        Assert.Equal(401, await StatusDaTrocaAsync(c.Api, pendenteDaAna.Ticket, verifierAna)); // antes: o ticket pendente ainda abria uma sessão NOVA depois do logout
        Assert.Equal(200, await StatusDaTrocaAsync(c.Api, pendenteDoBeto.Ticket, verifierBeto)); // o de outro usuário segue valendo

        // Um ticket emitido DEPOIS do logout vale (é um novo login legítimo).
        var verifierNovo = EmbeddedTestSupport.NovoVerifier();
        var novo = await c.Erp.EmitirAsync("u-ana", EmbeddedTestSupport.Challenge(verifierNovo));
        Assert.Equal(200, await StatusDaTrocaAsync(c.Api, novo.Ticket, verifierNovo));
    }

    [Fact]
    public async Task Troca_e_revogacao_concorrentes_nunca_deixam_uma_sessao_viva_depois_do_logout()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "corrida-logout");
        await using var servidor = c.Api;
        _ = await c.EntrarAsync("u-corrida"); // provisiona

        for (var rodada = 0; rodada < 25; rodada++)
        {
            var verifier = EmbeddedTestSupport.NovoVerifier();
            var ticket = await c.Erp.EmitirAsync("u-corrida", EmbeddedTestSupport.Challenge(verifier));

            var troca = Task.Run(async () =>
            {
                using var resposta = await c.Api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket = ticket.Ticket, verifier, parentOrigin = EmbeddedTestSupport.Origem });
                return resposta.StatusCode == HttpStatusCode.OK ? (await resposta.Content.ReadFromJsonAsync<TrocaDto>())!.Token : null;
            });
            var revogacao = Task.Run(async () =>
            {
                using var resposta = await c.Erp.EnviarAsync("/embed/v1/sessions/revoke", new { externalUserId = "u-corrida" });
                Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            });
            await Task.WhenAll(troca, revogacao);

            // Qualquer que tenha sido a ordem, terminada a revogação nenhuma sessão desse usuário pode funcionar.
            if (await troca is { } token)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(token, HttpMethod.Get, "/embed/v1/session"));
            }
        }
    }

    [Fact]
    public async Task Revogar_a_chave_derruba_a_sessao_e_o_ticket_pendente_dela_mas_nao_os_de_outra_chave()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "revoga-chave");
        await using var servidor = c.Api;
        var segunda = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys", new { });
        var erpB = new ErpAssinante(c.Api, segunda.KeyId, segunda.Secret);

        // Sessão aberta com a chave A; sessão e ticket pendente da chave B; ticket pendente da chave A.
        var (sessaoA, _) = await c.EntrarAsync("u-chave-a");
        var verifierB = EmbeddedTestSupport.NovoVerifier();
        var ticketB = await erpB.EmitirAsync("u-chave-b", EmbeddedTestSupport.Challenge(verifierB));
        var sessaoB = await EmbeddedTestSupport.TrocarAsync(c.Api, ticketB.Ticket, verifierB);
        var verifierA = EmbeddedTestSupport.NovoVerifier();
        var pendenteA = await c.Erp.EmitirAsync("u-chave-a", EmbeddedTestSupport.Challenge(verifierA));
        var verifierB2 = EmbeddedTestSupport.NovoVerifier();
        var pendenteB = await erpB.EmitirAsync("u-chave-b", EmbeddedTestSupport.Challenge(verifierB2));

        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/{c.Chave.KeyId}/revoke"));

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(sessaoA.Token, HttpMethod.Get, "/embed/v1/session")); // antes: a sessão aberta com a chave revogada seguia viva
        Assert.Equal(401, await StatusDaTrocaAsync(c.Api, pendenteA.Ticket, verifierA)); // antes: o ticket pendente da chave revogada ainda trocava
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(sessaoB.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(200, await StatusDaTrocaAsync(c.Api, pendenteB.Ticket, verifierB2));

        // A chave revogada também não emite mais.
        using var emissao = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-chave-a", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem });
        Assert.Equal(HttpStatusCode.Unauthorized, emissao.StatusCode);
    }

    [Fact]
    public async Task A_rotacao_de_chave_nao_derruba_as_sessoes_abertas_com_a_chave_que_so_expirou()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "rotacao");
        await using var servidor = c.Api;
        var (sessao, _) = await c.EntrarAsync("u-rot");

        var rotacionada = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys/rotate", new { });

        Assert.NotEqual(c.Chave.KeyId, rotacionada.KeyId);
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(sessao.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    [Fact]
    public async Task Tirar_a_origem_da_conexao_derruba_a_sessao_aberta_e_o_ticket_pendente_dessa_origem()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "origem");
        await using var servidor = c.Api;
        const string outraOrigem = "https://outro-erp.exemplo.com.br";
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}", new
        {
            name = "ERP", mode = "FullAttendant", provisioning = "JitAttendant", allowedOrigins = new[] { EmbeddedTestSupport.Origem, outraOrigem }, allowStartConversation = false,
        }));
        var (sessao, _) = await c.EntrarAsync("u-origem");
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var pendente = await c.Erp.EmitirAsync("u-origem", EmbeddedTestSupport.Challenge(verifier));
        var verifierOutra = EmbeddedTestSupport.NovoVerifier();
        var pendenteOutra = await c.Erp.EmitirAsync("u-origem", EmbeddedTestSupport.Challenge(verifierOutra), parentOrigin: outraOrigem);
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(sessao.Token, HttpMethod.Get, "/embed/v1/session"));

        // O administrador retira a origem do ERP original, mantendo só a outra.
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}", new
        {
            name = "ERP", mode = "FullAttendant", provisioning = "JitAttendant", allowedOrigins = new[] { outraOrigem }, allowStartConversation = false,
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(sessao.Token, HttpMethod.Get, "/embed/v1/session")); // antes: a sessão seguia viva sob uma origem que já não era permitida
        Assert.Equal(401, await StatusDaTrocaAsync(c.Api, pendente.Ticket, verifier));
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)await ((Func<Task<int>>)(async () =>
        {
            using var resposta = await c.Api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket = pendenteOutra.Ticket, verifier = verifierOutra, parentOrigin = outraOrigem });
            return (int)resposta.StatusCode;
        }))());
    }

    [Fact]
    public async Task A_revogacao_de_emergencia_da_conexao_derruba_sessoes_e_tickets_pendentes_de_todos_os_usuarios()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "emergencia");
        await using var servidor = c.Api;
        var (ana, _) = await c.EntrarAsync("u-ana");
        var (beto, _) = await c.EntrarAsync("u-beto");
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var pendente = await c.Erp.EmitirAsync("u-ana", EmbeddedTestSupport.Challenge(verifier));

        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/sessions/revoke", new { }));

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(ana.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(beto.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(401, await StatusDaTrocaAsync(c.Api, pendente.Ticket, verifier));

        // Depois da emergência, um login novo e legítimo volta a funcionar.
        var (nova, _) = await c.EntrarAsync("u-ana");
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(nova.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    // Item 4: tokens embutidos nunca se passam por interativos (versões mistas e retrocesso)

    [Fact]
    public async Task O_token_embutido_tem_formato_e_hash_que_uma_versao_antiga_da_API_nao_acha_nem_aceita()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "versoes");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-versao");

        // A versão ANTIGA só conhecia o prefixo ibs_ e achava a sessão por SHA-256(token). O novo token não tem o prefixo antigo...
        Assert.StartsWith("ibe_", troca.Token);
        Assert.DoesNotMatch("^ibs_", troca.Token);

        // ...e o hash que fica no banco NÃO é o SHA-256 simples do token: nenhuma busca da versão antiga acha a linha.
        var hashAntigo = SHA256.HashData(Encoding.UTF8.GetBytes(troca.Token));
        var hashAntigoDoPrefixoAntigo = SHA256.HashData(Encoding.UTF8.GetBytes("ibs_" + troca.Token[4..]));
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.sessions WHERE token_hash = @h", ("h", hashAntigo)));
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.sessions WHERE token_hash = @h", ("h", hashAntigoDoPrefixoAntigo)));
        Assert.Equal(1, await EscalarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND kind = 'Embedded'", ("t", c.Tenant.TenantId.Value)));

        // Na versão NOVA, trocar o prefixo não transforma uma sessão na outra, nos dois sentidos.
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync("ibs_" + troca.Token[4..], HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync("ibs_" + troca.Token[4..], HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));

        var interativo = await c.Api.LoginAsync(c.Admin);
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync("ibe_" + interativo[4..], HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(interativo, HttpMethod.Get, "/auth/me"));
    }

    [Fact]
    public async Task O_usuario_federado_nao_tem_senha_utilizavel_em_nenhuma_versao_e_o_papel_novo_e_so_dele()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "federado-senha");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-fed");

        // A senha guardada é um marcador que nenhum verificador de hash (nem o da versão antiga) valida; não há e-mail real a que ela pudesse servir.
        Assert.Equal("!federated", await TextoAsync("SELECT password_hash FROM identity.users WHERE tenant_id = @t AND id = @u", ("t", c.Tenant.TenantId.Value), ("u", troca.User.Id)));
        Assert.EndsWith("@federated.invalid", await TextoAsync("SELECT email FROM identity.users WHERE tenant_id = @t AND id = @u", ("t", c.Tenant.TenantId.Value), ("u", troca.User.Id)));
    }

    private static async Task<string> TextoAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        return (string)(await comando.ExecuteScalarAsync())!;
    }

    // Item 5: ticket de contexto preso à sessão exata

    [Fact]
    public async Task Ticket_de_contexto_so_vale_na_sessao_para_a_qual_foi_emitido_mesmo_entre_duas_sessoes_do_mesmo_usuario()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "contexto-sessao");
        await using var servidor = c.Api;
        var (aba1, _) = await c.EntrarAsync("u-abas");
        var (aba2, _) = await c.EntrarAsync("u-abas");
        Assert.NotEqual(aba1.SessionId, aba2.SessionId);

        var paraAba1 = await c.Erp.EmitirContextoAsync("u-abas", "CLI-1", aba1.SessionId);

        // A aba 2 (mesmo tenant, mesma conexão, mesmo usuário externo) NÃO resgata o ticket da aba 1 (antes resgatava) e a tentativa o queima.
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(aba2.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = paraAba1.Ticket }));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(aba1.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = paraAba1.Ticket }));
        Assert.Empty((await c.Api.LerAsync<SessaoEmbutidaDto>(aba1.Token, "/embed/v1/session")).CustomerRefs);
        Assert.Empty((await c.Api.LerAsync<SessaoEmbutidaDto>(aba2.Token, "/embed/v1/session")).CustomerRefs);

        // Emitido para a aba 1 e resgatado nela, vale.
        var outro = await c.Erp.EmitirContextoAsync("u-abas", "CLI-2", aba1.SessionId);
        using var resgate = await c.Api.EnviarAsync(aba1.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = outro.Ticket });
        Assert.Equal(HttpStatusCode.OK, resgate.StatusCode);
        Assert.Equal(new[] { "CLI-2" }, (await c.Api.LerAsync<SessaoEmbutidaDto>(aba1.Token, "/embed/v1/session")).CustomerRefs.ToArray());
    }

    [Fact]
    public async Task Ticket_de_contexto_exige_a_sessao_e_recusa_sessao_de_outro_usuario_revogada_ou_inexistente()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "contexto-alvo");
        await using var servidor = c.Api;
        var (ana, _) = await c.EntrarAsync("u-ana");
        var (beto, _) = await c.EntrarAsync("u-beto");

        async Task<HttpStatusCode> EmitirAsync(string usuario, object? sessionId)
        {
            using var resposta = await c.Erp.EnviarAsync("/embed/v1/context-tickets", new { externalUserId = usuario, sessionId, customer = new { @ref = "CLI-1" } });
            return resposta.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await EmitirAsync("u-ana", null)); // sem a sessão, sem ticket
        Assert.Equal(HttpStatusCode.Forbidden, await EmitirAsync("u-ana", Guid.NewGuid())); // sessão que não existe
        Assert.Equal(HttpStatusCode.Forbidden, await EmitirAsync("u-ana", beto.SessionId)); // sessão de OUTRO usuário externo
        Assert.Equal(HttpStatusCode.Created, await EmitirAsync("u-ana", ana.SessionId));

        using var logout = await c.Erp.EnviarAsync("/embed/v1/sessions/revoke", new { externalUserId = "u-ana" });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await EmitirAsync("u-ana", ana.SessionId)); // sessão revogada
    }

    [Fact]
    public async Task Ticket_de_contexto_de_um_tenant_nunca_aponta_a_sessao_de_outro()
    {
        var a = await EmbeddedCenario.CriarAsync(banco, "ctx-tenant-a");
        await using var servidor = a.Api;
        var b = await EmbeddedCenario.CriarAsync(banco, "ctx-tenant-b", api: a.Api);
        var (sessaoDeB, _) = await b.EntrarAsync("mesmo-id");
        _ = await a.EntrarAsync("mesmo-id");

        using var resposta = await a.Erp.EnviarAsync("/embed/v1/context-tickets", new { externalUserId = "mesmo-id", sessionId = sessaoDeB.SessionId, customer = new { @ref = "CLI-1" } });

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    // Item 6: tetos sob concorrência

    [Fact]
    public async Task Trocas_simultaneas_do_mesmo_usuario_externo_respeitam_o_teto_de_sessoes_ativas()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "teto-sessoes");
        await using var servidor = c.Api;
        _ = await c.EntrarAsync("u-teto");
        const int Simultaneas = 14;
        var preparados = new List<(string Ticket, string Verifier)>();
        for (var i = 0; i < Simultaneas; i++)
        {
            var verifier = EmbeddedTestSupport.NovoVerifier();
            preparados.Add(((await c.Erp.EmitirAsync("u-teto", EmbeddedTestSupport.Challenge(verifier))).Ticket, verifier));
        }

        var estados = await Task.WhenAll(preparados.Select(p => StatusDaTrocaAsync(c.Api, p.Ticket, p.Verifier)));

        Assert.All(estados, e => Assert.Equal(200, e));
        // O teto é 5 (padrão): antes, cada troca contava ANTES de a outra gravar e o total passava do teto.
        var ativas = await EscalarAsync(
            "SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND kind = 'Embedded' AND external_user_id = 'u-teto' AND revoked_at IS NULL AND expires_at > now()",
            ("t", c.Tenant.TenantId.Value));
        Assert.Equal(5, ativas);
    }

    [Fact]
    public async Task Resgates_simultaneos_de_contexto_na_mesma_sessao_respeitam_o_teto_de_clientes_em_foco()
    {
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        var apiComLog = await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao(), null, s => s.AddLogging(b => b.AddProvider(new CapturaDeErros(bag))));
        var c = await EmbeddedCenario.CriarAsync(banco, "teto-clientes", api: apiComLog);
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-clientes");
        const int Simultaneos = 24; // o teto é 20 por sessão (24 bastam para provar o teto sem esgotar as conexões do PostgreSQL de teste, que tem 100)
        var tickets = new List<string>();
        for (var i = 0; i < Simultaneos; i++)
        {
            tickets.Add((await c.Erp.EmitirContextoAsync("u-clientes", $"CLI-{i:00}", troca.SessionId)).Ticket);
        }

        var estados = await Task.WhenAll(tickets.Select(t => c.Api.StatusAsync(troca.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = t })));

        Assert.True(estados.All(e => e == HttpStatusCode.OK), "Erros do servidor: " + string.Join(" ---- ", bag.Where(x => !x.TrimEnd().EndsWith("::")).Take(3)));
        Assert.Equal(20, await EscalarAsync("SELECT count(*) FROM identity.session_customer_grants WHERE tenant_id = @t AND session_id = @s", ("t", c.Tenant.TenantId.Value), ("s", troca.SessionId)));
    }

    // Item 7: limites de taxa

    [Fact]
    public async Task Inventar_identificadores_de_chave_no_cabecalho_nao_ganha_cota_nova()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "taxa-ip", configurar: k => k["Embedded:RateLimit:IntegrationPermitLimit"] = "8");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-x", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        var estados = new List<HttpStatusCode>();
        for (var i = 0; i < 30; i++)
        {
            using var resposta = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, keyIdUsado: $"ibk_{Guid.NewGuid():N}"); // um identificador novo a cada requisição
            estados.Add(resposta.StatusCode);
        }

        // Antes: cada identificador inventado ganhava a PRÓPRIA cota e nada era limitado. Agora a cota anterior à autenticação é só da origem de rede.
        Assert.Equal(8, estados.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(22, estados.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Falhas_de_autenticacao_de_uma_chave_conhecida_esgotam_a_cota_dela_e_depois_ate_a_chamada_boa_espera()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "taxa-chave", configurar: k => k["Identity:IntegrationPermitsPerKey"] = "3");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-x", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        for (var i = 0; i < 3; i++)
        {
            using var ruim = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, segredoUsado: "ibsk_errado");
            Assert.Equal(HttpStatusCode.Unauthorized, ruim.StatusCode);
        }

        using var bloqueada = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, segredoUsado: "ibsk_errado");
        using var boaMasBloqueada = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo);
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, boaMasBloqueada.StatusCode);
        Assert.True(boaMasBloqueada.Headers.Contains("Retry-After"));
        Assert.Equal("embedded-rate-limited", await EmbeddedTestSupport.CodigoDoProblemaAsync(boaMasBloqueada));

        // Só a chave atacada espera: outra conexão (outro tenant) não é afetada.
        var outro = await EmbeddedCenario.CriarAsync(banco, "taxa-chave-b", api: c.Api);
        _ = await outro.EntrarAsync("u-outro");
    }

    [Fact]
    public async Task Chamadas_autenticadas_de_uma_conexao_tem_cota_propria_somando_todas_as_chaves()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "taxa-conexao", configurar: k => k["Identity:IntegrationPermitsPerConnection"] = "4");
        await using var servidor = c.Api;
        var segunda = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys", new { });
        var erpB = new ErpAssinante(c.Api, segunda.KeyId, segunda.Secret);
        var corpo = new { externalUserId = "u-ana", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        var estados = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var resposta = await (i % 2 == 0 ? c.Erp : erpB).EnviarAsync("/embed/v1/launch-tickets", corpo);
            estados.Add(resposta.StatusCode);
        }

        Assert.Equal(4, estados.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(2, estados.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    // Item 8: teto de corpo antes do JSON, inclusive sem Content-Length

    private sealed class CorpoEmPedacos(byte[] bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? contexto)
        {
            for (var i = 0; i < bytes.Length; i += 1024)
            {
                await stream.WriteAsync(bytes.AsMemory(i, Math.Min(1024, bytes.Length - i)));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false; // sem Content-Length: Transfer-Encoding chunked
        }
    }

    [Theory]
    [InlineData("/embed/v1/exchange", false)]
    [InlineData("/embed/v1/exchange", true)]
    public async Task O_exchange_recusa_corpo_acima_do_teto_com_ou_sem_content_length_sem_virar_500(string caminho, bool semContentLength)
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "corpo");
        await using var servidor = c.Api;
        var enorme = Encoding.UTF8.GetBytes("{\"ticket\":\"" + new string('a', 200_000) + "\"}");

        using var requisicao = new HttpRequestMessage(HttpMethod.Post, caminho) { Content = semContentLength ? new CorpoEmPedacos(enorme) : new ByteArrayContent(enorme) };
        requisicao.Content.Headers.ContentType = new("application/json");
        using var resposta = await c.Api.Client.SendAsync(requisicao);

        Assert.Contains((int)resposta.StatusCode, new[] { 400, 413 });
        if (semContentLength)
        {
            Assert.Null(requisicao.Content.Headers.ContentLength);
        }
    }

    [Fact]
    public async Task O_resgate_do_contexto_tambem_tem_teto_de_corpo_e_o_json_quebrado_e_400()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "corpo-ctx");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-corpo");
        var enorme = Encoding.UTF8.GetBytes("{\"ticket\":\"" + new string('a', 200_000) + "\"}");

        using var grande = new HttpRequestMessage(HttpMethod.Post, "/embed/v1/context") { Content = new CorpoEmPedacos(enorme) };
        grande.Headers.Authorization = new("Bearer", troca.Token);
        grande.Content.Headers.ContentType = new("application/json");
        using var respostaGrande = await c.Api.Client.SendAsync(grande);
        Assert.Contains((int)respostaGrande.StatusCode, new[] { 400, 413 });

        using var quebrado = new HttpRequestMessage(HttpMethod.Post, "/embed/v1/exchange") { Content = new StringContent("{isto não é json", Encoding.UTF8, "application/json") };
        using var respostaQuebrada = await c.Api.Client.SendAsync(quebrado);
        Assert.Equal(HttpStatusCode.BadRequest, respostaQuebrada.StatusCode);

        // O caminho feliz continua igual.
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await c.Erp.EmitirAsync("u-corpo", EmbeddedTestSupport.Challenge(verifier));
        Assert.Equal(200, await StatusDaTrocaAsync(c.Api, ticket.Ticket, verifier));
    }

    // Item 10: timestamp inválido é erro controlado

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("253402300800")]
    [InlineData("99999999999999999999")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0")]
    public async Task Timestamp_invalido_ou_absurdo_responde_401_controlado_e_nunca_500(string timestamp)
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "timestamp");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-ts", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        // A chave é VÁLIDA e a assinatura é calculada sobre o próprio texto do timestamp: o único defeito é o instante.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(corpo, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var nonce = ErpAssinante.NonceNovo();
        using var requisicao = new HttpRequestMessage(HttpMethod.Post, "/embed/v1/launch-tickets") { Content = new ByteArrayContent(bytes) };
        requisicao.Content.Headers.ContentType = new("application/json");
        requisicao.Headers.Add("X-IaB-Key-Id", c.Chave.KeyId);
        requisicao.Headers.TryAddWithoutValidation("X-IaB-Timestamp", timestamp);
        requisicao.Headers.Add("X-IaB-Nonce", nonce);
        requisicao.Headers.Add("X-IaB-Signature", ErpAssinante.Assinar(c.Chave.Secret, timestamp, nonce, "POST", "/embed/v1/launch-tickets", bytes));
        using var resposta = await c.Api.Client.SendAsync(requisicao);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal("embedded-authentication-failed", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
    }

    // Item 11: rotas públicas exatas, métodos e canonicalização

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/HEALTH", true)]
    [InlineData("/health/", true)]
    [InlineData("/health/x", false)]
    [InlineData("/healthz", false)]
    [InlineData("/auth/login", true)]
    [InlineData("/auth/login/x", false)]
    [InlineData("/auth/me", false)]
    [InlineData("/embed/v1/exchange", true)]
    [InlineData("/embed/v1/exchange/x", false)]
    [InlineData("/embed/v1/launch-tickets", true)]
    [InlineData("/embed/v1/launch-tickets/x", false)]
    [InlineData("/embed/v1/context", false)]
    [InlineData("/embed/v1/session", false)]
    [InlineData("/embed/v1/customers/CLI-1/consents", true)] // E2.4: o cliente no caminho (um segmento)
    [InlineData("/EMBED/V1/CUSTOMERS/CLI-1/CONSENTS", true)]
    [InlineData("/embed/v1/customers/CLI-1/consents/", true)]
    [InlineData("/embed/v1/customers//consents", false)]
    [InlineData("/embed/v1/customers/a/b/consents", false)]
    [InlineData("/embed/v1/customers/consents", false)]
    [InlineData("/embed/v1/customers/CLI-1/consent", false)]
    [InlineData("/embed/v1/customers/CLI-1/consents/x", false)]
    [InlineData("/embed/v1/customers/CLI-1", false)]
    [InlineData("/embed/v1", false)]
    [InlineData("/webhooks/whatsapp/abc", true)]
    [InlineData("/webhooks", false)]
    [InlineData("/webhooksx/abc", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    public void A_dispensa_de_contexto_vale_so_para_o_caminho_exato_e_o_prefixo_do_webhook(string caminho, bool esperado)
    {
        Assert.Equal(esperado, RequestContextMiddleware.SemContexto(new PathString(caminho)));
    }

    [Fact]
    public async Task Rotas_assinadas_recusam_consulta_metodo_errado_e_qualquer_forma_alternativa_do_caminho()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "canonico");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-can", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        // Consulta: dado NÃO assinado, recusado (400).
        using var comConsulta = await c.Erp.EnviarAsync("/embed/v1/launch-tickets?admin=1", corpo);
        Assert.Equal(HttpStatusCode.BadRequest, comConsulta.StatusCode);

        // Forma alternativa do caminho: assinada com o caminho canônico mas enviada em outra forma, ou assinada na forma alternativa: nunca vale a mesma assinatura.
        using var barraFinal = await c.Erp.EnviarAsync("/embed/v1/launch-tickets/", corpo, caminhoAssinado: "/embed/v1/launch-tickets");
        using var caixa = await c.Erp.EnviarAsync("/EMBED/v1/launch-tickets", corpo, caminhoAssinado: "/embed/v1/launch-tickets");
        using var comPrefixoDoProxy = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, caminhoAssinado: "/api/embed/v1/launch-tickets");
        Assert.Equal(HttpStatusCode.Unauthorized, barraFinal.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, caixa.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, comPrefixoDoProxy.StatusCode);

        // O caminho com /api não existe na API (o Caddy o remove): nem chega a autenticar.
        using var diretoComApi = await c.Erp.EnviarAsync("/api/embed/v1/launch-tickets", corpo);
        Assert.Equal(HttpStatusCode.NotFound, diretoComApi.StatusCode);

        // Método errado nas rotas públicas.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await c.Api.StatusAsync(null, HttpMethod.Get, "/embed/v1/launch-tickets"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await c.Api.StatusAsync(null, HttpMethod.Get, "/embed/v1/exchange"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await c.Api.StatusAsync(null, HttpMethod.Put, "/embed/v1/sessions/revoke"));

        // A forma canônica, sem nenhum desses defeitos, funciona.
        using var correta = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo);
        Assert.Equal(HttpStatusCode.Created, correta.StatusCode);
    }

    [Fact]
    public async Task Sessao_embutida_nao_alcanca_nada_por_metodo_alternativo_nem_por_forma_alternativa_do_caminho()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "metodos");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-met");
        var id = Guid.NewGuid();

        var tentativas = new (HttpMethod Metodo, string Caminho)[]
        {
            (HttpMethod.Delete, $"/contact-center/items/{id}"),
            (HttpMethod.Put, $"/contact-center/items/{id}/claim"),
            (HttpMethod.Patch, $"/contact-center/items/{id}"),
            (HttpMethod.Head, "/contact-center/items"),
            (HttpMethod.Options, "/contact-center/items"),
            (HttpMethod.Get, $"/contact-center/items/{id}/assign"),
            (HttpMethod.Get, "/contact-center/items/"),
            (HttpMethod.Get, "/Contact-Center/../organizations"),
            (HttpMethod.Get, "/contact-center//items"),
            (HttpMethod.Get, "/contact-center/items/%2e%2e/%2e%2e/organizations"),
            (HttpMethod.Get, "/embedded/connections"),
            (HttpMethod.Get, "/health/../organizations"),
            (HttpMethod.Get, "/auth/login/../me/password"),
        };

        foreach (var (metodo, caminho) in tentativas)
        {
            using var resposta = await c.Api.EnviarAsync(troca.Token, metodo, caminho);
            var codigo = (int)resposta.StatusCode;

            // O que a lista não admite é 403; o que o roteamento nem admite (barras, ..) pode ser 400/404/405, mas NUNCA 2xx com dado. A única forma alternativa que a lista admite é a barra final da própria rota permitida.
            var barraFinalDaRotaPermitida = caminho == "/contact-center/items/" && metodo == HttpMethod.Get;
            Assert.True(codigo is 403 or 400 or 404 or 405 || (barraFinalDaRotaPermitida && codigo == 200), $"{metodo} {caminho} respondeu {codigo}");
        }
    }
}
