using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// A federação do Embedded (ADR 0039) pela API real com PostgreSQL real: ticket de uso único com verifier e origem do pai, assinatura HMAC com janela e nonce, sessão embutida curta e revogável, JIT que nunca vincula
/// por e-mail, usuário federado sem senha, isolamento entre tenants e a lista fechada de rotas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedFederationApiTests(PostgresFixture banco)
{
    private static async Task<int> ContarAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    // Caminho feliz e JIT

    [Fact]
    public async Task Caminho_feliz_ticket_troca_e_sessao_embutida_usam_o_tenant_da_conexao_e_so_o_atendimento()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "feliz");
        await using var servidor = c.Api;

        var (troca, _) = await c.EntrarAsync("u-100", cliente: "CLI-1");

        Assert.StartsWith("ibs_", troca.Token);
        Assert.Equal("FullAttendant", troca.Mode);
        Assert.Equal(c.Organizacao.Id, troca.OrganizationId);
        Assert.Equal("CLI-1", troca.CustomerRef);
        Assert.InRange(troca.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));

        var sessao = await c.Api.LerAsync<SessaoEmbutidaDto>(troca.Token, "/embed/v1/session");
        Assert.Equal(new[] { "CLI-1" }, sessao.CustomerRefs);
        Assert.Equal(c.Organizacao.Id, sessao.OrganizationId);

        // O atendimento passa (200); a identidade interna é a do usuário federado criado, não a de nenhum administrador.
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/contact-center/me/queues"));
        var eu = await c.Api.LerAsync<EuDto>(troca.Token, "/auth/me");
        Assert.Equal(troca.User.Id, eu.Id);
        Assert.NotEqual(c.Admin.Id, eu.Id);
    }

    [Fact]
    public async Task JIT_cria_usuario_federado_com_email_sintetico_e_papel_de_atendente_so_na_organizacao_da_conexao()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "jit");
        await using var servidor = c.Api;

        var (troca, _) = await c.EntrarAsync("u-jit", nome: "Maria Externa");

        var eu = await c.Api.LerAsync<EuDto>(troca.Token, "/auth/me");
        Assert.EndsWith("@federated.invalid", eu.Email);
        Assert.Equal("Maria Externa", eu.DisplayName);
        var concessao = Assert.Single(eu.Grants);
        Assert.Equal(c.Organizacao.Id, concessao.OrganizationId);
        Assert.Equal("Attendant", concessao.Role);

        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.external_identities WHERE tenant_id = @t AND source = 'Jit'", ("t", c.Tenant.TenantId.Value)));
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @t AND auth_mode = 'Federated'", ("t", c.Tenant.TenantId.Value)));
    }

    [Fact]
    public async Task Email_coincidente_nunca_vincula_conta_existente_nem_administrativa()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "email");
        await using var servidor = c.Api;
        var verifier = EmbeddedTestSupport.NovoVerifier();

        // O ERP afirma o e-mail do administrador do tenant como "nome" e como identificador externo: nada disso escolhe conta interna.
        var ticket = await c.Erp.EmitirAsync(c.Admin.Email, EmbeddedTestSupport.Challenge(verifier), nome: c.Admin.Email);
        var troca = await EmbeddedTestSupport.TrocarAsync(c.Api, ticket.Ticket, verifier);

        Assert.NotEqual(c.Admin.Id, troca.User.Id);
        var eu = await c.Api.LerAsync<EuDto>(troca.Token, "/auth/me");
        Assert.EndsWith("@federated.invalid", eu.Email);
        Assert.NotEqual(c.Admin.Email, eu.Email);

        // E a sessão embutida não administra nada: o que o administrador pode, ela não pode.
        Assert.Equal(HttpStatusCode.Forbidden, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embedded/connections"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Get, "/embedded/connections"));
    }

    [Fact]
    public async Task Usuario_federado_nunca_faz_login_por_senha()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "semsenha");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-sem-senha");
        var eu = await c.Api.LerAsync<EuDto>(troca.Token, "/auth/me");

        foreach (var senha in new[] { "!federated", "Senha-Qualquer-123!", "", "x" })
        {
            using var resposta = await c.Api.Client.PostAsJsonAsync("/auth/login", new { tenant = c.Tenant.Slug, email = eu.Email, password = senha });
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }
    }

    [Fact]
    public async Task Duas_primeiras_entradas_simultaneas_do_mesmo_usuario_externo_criam_um_unico_usuario()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "corrida");
        await using var servidor = c.Api;

        var verifiers = Enumerable.Range(0, 6).Select(_ => EmbeddedTestSupport.NovoVerifier()).ToArray();
        var respostas = await Task.WhenAll(verifiers.Select(async v =>
        {
            using var r = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-corrida", displayName = "X", challenge = EmbeddedTestSupport.Challenge(v), parentOrigin = EmbeddedTestSupport.Origem });
            return r.StatusCode;
        }));

        Assert.All(respostas, status => Assert.Equal(HttpStatusCode.Created, status));
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.external_identities WHERE tenant_id = @t AND external_user_id = 'u-corrida'", ("t", c.Tenant.TenantId.Value)));
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @t AND auth_mode = 'Federated'", ("t", c.Tenant.TenantId.Value)));
    }

    // Ticket de uso único, verifier, origem e validade

    [Fact]
    public async Task Ticket_e_de_uso_unico_e_a_segunda_troca_e_recusada()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "unico");
        await using var servidor = c.Api;
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await c.Erp.EmitirAsync("u-1", EmbeddedTestSupport.Challenge(verifier));

        _ = await EmbeddedTestSupport.TrocarAsync(c.Api, ticket.Ticket, verifier);

        Assert.Equal(HttpStatusCode.Unauthorized, await EmbeddedTestSupport.TentarTrocarAsync(c.Api, ticket.Ticket, verifier));
    }

    [Fact]
    public async Task Trocas_simultaneas_do_mesmo_ticket_tem_um_unico_vencedor()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "paralelo");
        await using var servidor = c.Api;
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await c.Erp.EmitirAsync("u-2", EmbeddedTestSupport.Challenge(verifier));

        var status = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => EmbeddedTestSupport.TentarTrocarAsync(c.Api, ticket.Ticket, verifier)));

        Assert.Equal(1, status.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(9, status.Count(s => s == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Verifier_errado_recusa_e_queima_o_ticket()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "verifier");
        await using var servidor = c.Api;
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await c.Erp.EmitirAsync("u-3", EmbeddedTestSupport.Challenge(verifier));

        // Quem tem o ticket mas não o verifier (um XSS na página do ERP) não troca, e a tentativa invalida o ticket.
        Assert.Equal(HttpStatusCode.Unauthorized, await EmbeddedTestSupport.TentarTrocarAsync(c.Api, ticket.Ticket, EmbeddedTestSupport.NovoVerifier()));
        Assert.Equal(HttpStatusCode.Unauthorized, await EmbeddedTestSupport.TentarTrocarAsync(c.Api, ticket.Ticket, verifier));
    }

    [Theory]
    [InlineData("https://outro-site.exemplo.com")]
    [InlineData("http://erp.exemplo.com.br")]
    [InlineData("https://erp.exemplo.com.br:8443")]
    [InlineData("not-an-origin")]
    public async Task Origem_do_pai_diferente_da_declarada_pelo_erp_recusa_a_troca(string origemErrada)
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "origem");
        await using var servidor = c.Api;
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await c.Erp.EmitirAsync("u-4", EmbeddedTestSupport.Challenge(verifier));

        Assert.Equal(HttpStatusCode.Unauthorized, await EmbeddedTestSupport.TentarTrocarAsync(c.Api, ticket.Ticket, verifier, origemErrada));
    }

    [Fact]
    public async Task Origem_do_pai_fora_da_lista_da_conexao_nem_emite_ticket_e_a_negacao_e_auditada()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "origemlista");
        await using var servidor = c.Api;

        using var resposta = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new
        {
            externalUserId = "u-5",
            challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()),
            parentOrigin = "https://atacante.exemplo.com",
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(0, await ContarAsync("SELECT count(*) FROM identity.launch_tickets WHERE tenant_id = @t", ("t", c.Tenant.TenantId.Value)));
        Assert.True(await ContarAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND operation = 'identity.embed.ticket.issue' AND outcome = 2", ("t", c.Tenant.TenantId.Value)) >= 1);
    }

    [Fact]
    public async Task Ticket_expirado_e_recusado()
    {
        var relogio = new RelogioManual();
        var c = await EmbeddedCenario.CriarAsync(banco, "expira", relogio: relogio);
        await using var servidor = c.Api;
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await c.Erp.EmitirAsync("u-6", EmbeddedTestSupport.Challenge(verifier));

        relogio.Avancar(TimeSpan.FromSeconds(61));

        Assert.Equal(HttpStatusCode.Unauthorized, await EmbeddedTestSupport.TentarTrocarAsync(c.Api, ticket.Ticket, verifier));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ibt_curto")]
    [InlineData("ibs_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ibt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Ticket_malformado_ou_desconhecido_responde_o_mesmo_erro(string ticket)
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "malformado");
        await using var servidor = c.Api;

        using var resposta = await c.Api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket, verifier = EmbeddedTestSupport.NovoVerifier(), parentOrigin = EmbeddedTestSupport.Origem });

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal("embedded-authentication-failed", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
    }

    // Assinatura HMAC, janela, nonce, chave

    [Fact]
    public async Task Corpo_adulterado_depois_de_assinado_e_recusado()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "adulterado");
        await using var servidor = c.Api;
        var challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier());

        var adulterado = JsonSerializer.SerializeToUtf8Bytes(new { externalUserId = "u-outro", challenge, parentOrigin = EmbeddedTestSupport.Origem });
        using var resposta = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-7", challenge, parentOrigin = EmbeddedTestSupport.Origem }, corpoEnviado: adulterado);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Assinatura_com_outro_segredo_ou_outro_caminho_e_recusada()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "assinatura");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-8", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        using var outroSegredo = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, segredoUsado: "ibsk_outro-segredo");
        using var outroCaminho = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, caminhoAssinado: "/embed/v1/context-tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, outroSegredo.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, outroCaminho.StatusCode);
    }

    [Fact]
    public async Task O_ERP_assina_o_caminho_que_o_api_enxerga_sem_o_prefixo_do_proxy()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "proxy");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-9", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        // O Caddy remove o /api antes de o Api ver o caminho; o contrato é assinar o caminho SEM o prefixo (o que o ERP chama como /api/embed/... assina como /embed/...).
        using var semPrefixo = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, caminhoAssinado: "/embed/v1/launch-tickets");
        using var comPrefixo = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, caminhoAssinado: "/api/embed/v1/launch-tickets");

        Assert.Equal(HttpStatusCode.Created, semPrefixo.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, comPrefixo.StatusCode);
    }

    [Theory]
    [InlineData(-130)]
    [InlineData(130)]
    [InlineData(-3600)]
    public async Task Instante_fora_da_janela_de_assinatura_e_recusado(int segundosDeDiferenca)
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "janela");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-10", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        using var resposta = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, instante: DateTimeOffset.UtcNow.AddSeconds(segundosDeDiferenca));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Repetir_exatamente_a_mesma_chamada_assinada_e_replay_e_recusado()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "replay");
        await using var servidor = c.Api;
        var relogio = DateTimeOffset.UtcNow;
        var corpo = new { externalUserId = "u-11", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };
        var nonce = ErpAssinante.NonceNovo();

        using var primeira = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, nonce: nonce, instante: relogio);
        using var repetida = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, nonce: nonce, instante: relogio);

        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, repetida.StatusCode);
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.launch_tickets WHERE tenant_id = @t", ("t", c.Tenant.TenantId.Value)));
    }

    [Fact]
    public async Task Nonce_repetido_com_outro_corpo_tambem_e_recusado_e_nonce_curto_ou_invalido_nem_chega_ao_banco()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "nonce");
        await using var servidor = c.Api;
        var challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier());
        var nonce = ErpAssinante.NonceNovo();

        using var um = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-12", challenge, parentOrigin = EmbeddedTestSupport.Origem }, nonce: nonce);
        using var dois = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-13", challenge, parentOrigin = EmbeddedTestSupport.Origem }, nonce: nonce);
        using var curto = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-14", challenge, parentOrigin = EmbeddedTestSupport.Origem }, nonce: "abc");
        using var lixo = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-15", challenge, parentOrigin = EmbeddedTestSupport.Origem }, nonce: new string('!', 20));

        Assert.Equal(HttpStatusCode.Created, um.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, dois.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, curto.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, lixo.StatusCode);
    }

    [Fact]
    public async Task Cabecalho_ausente_repetido_ou_chave_desconhecida_responde_o_mesmo_erro()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "cabecalhos");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-16", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        using var semCabecalho = await c.Api.Client.PostAsJsonAsync("/embed/v1/launch-tickets", corpo);
        using var repetido = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, ajustar: r => r.Headers.Add("X-IaB-Nonce", ErpAssinante.NonceNovo()));
        using var desconhecida = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, keyIdUsado: "ibk_" + Guid.NewGuid().ToString("N"));
        using var malformada = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, keyIdUsado: "lixo");

        foreach (var resposta in new[] { semCabecalho, repetido, desconhecida, malformada })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
            Assert.Equal("embedded-authentication-failed", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
        }
    }

    [Fact]
    public async Task Chave_revogada_nao_autentica_mais()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "revogada");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-17", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };
        using var antes = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo);
        Assert.Equal(HttpStatusCode.Created, antes.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/{c.Chave.KeyId}/revoke"));

        using var depois = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo);
        Assert.Equal(HttpStatusCode.Unauthorized, depois.StatusCode);
    }

    [Fact]
    public async Task Rotacao_mantem_as_duas_chaves_na_carencia_e_a_antiga_para_de_valer_depois()
    {
        var relogio = new RelogioManual();
        var c = await EmbeddedCenario.CriarAsync(banco, "rotacao", relogio: relogio);
        await using var servidor = c.Api;
        var nova = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys/rotate", new { graceHours = 2 });
        var erpNovo = new ErpAssinante(c.Api, nova.KeyId, nova.Secret, relogio);
        Assert.NotNull(nova.OthersExpireAt);

        var corpo = new { externalUserId = "u-18", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };
        using var antigaNaCarencia = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo);
        using var novaNaCarencia = await erpNovo.EnviarAsync("/embed/v1/launch-tickets", corpo);
        relogio.Avancar(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(5));
        using var antigaDepois = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo);
        using var novaDepois = await erpNovo.EnviarAsync("/embed/v1/launch-tickets", corpo);

        Assert.Equal(HttpStatusCode.Created, antigaNaCarencia.StatusCode);
        Assert.Equal(HttpStatusCode.Created, novaNaCarencia.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, antigaDepois.StatusCode);
        Assert.Equal(HttpStatusCode.Created, novaDepois.StatusCode);
    }

    [Fact]
    public async Task Conexao_desativada_derruba_as_chamadas_assinadas_e_as_sessoes_embutidas_e_reativar_so_libera_as_chamadas()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "desativa");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-19");
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));

        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/disable"));

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
        using var emitir = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-19", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem });
        Assert.Equal(HttpStatusCode.Unauthorized, emitir.StatusCode);

        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/enable"));
        // A sessão já revogada não volta; um ticket novo abre outra.
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
        var (nova, _) = await c.EntrarAsync("u-19");
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(nova.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    // Provisionamento MappedOnly e vínculos

    [Fact]
    public async Task MappedOnly_recusa_usuario_nao_mapeado_sem_criar_nada()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "mapped", provisionamento: "MappedOnly");
        await using var servidor = c.Api;

        using var resposta = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-novo", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem });

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal("external-identity-not-allowed", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
        Assert.Equal(0, await ContarAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @t AND auth_mode = 'Federated'", ("t", c.Tenant.TenantId.Value)));
        Assert.Equal(0, await ContarAsync("SELECT count(*) FROM identity.external_identities WHERE tenant_id = @t", ("t", c.Tenant.TenantId.Value)));
    }

    [Fact]
    public async Task MappedOnly_aceita_o_usuario_que_o_administrador_mapeou_e_a_sessao_e_dele()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "mapeado", provisionamento: "MappedOnly");
        await using var servidor = c.Api;
        var atendente = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        _ = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}/identities", new { externalUserId = "u-mapeado", userId = atendente.Id });

        var (troca, _) = await c.EntrarAsync("u-mapeado");

        Assert.Equal(atendente.Id, troca.User.Id);
        var eu = await c.Api.LerAsync<EuDto>(troca.Token, "/auth/me");
        Assert.Equal(atendente.Email, eu.Email);
    }

    [Fact]
    public async Task Vinculo_desativado_recusa_novos_tickets_e_derruba_as_sessoes_do_usuario_externo()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "vinculo");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-20");
        var (outra, _) = await c.EntrarAsync("u-21");
        var vinculos = await c.Api.LerAsync<List<VinculoDto>>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/identities");
        var dele = vinculos.Single(v => v.ExternalUserId == "u-20");

        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/identities/{dele.Id}/disable"));

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(outra.Token, HttpMethod.Get, "/embed/v1/session"));
        using var novo = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-20", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem });
        Assert.Equal(HttpStatusCode.Forbidden, novo.StatusCode);
    }

    [Fact]
    public async Task Conta_que_vira_administrativa_perde_a_sessao_embutida_na_hora()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "promovido", provisionamento: "MappedOnly");
        await using var servidor = c.Api;
        var gerente = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Manager);
        _ = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}/identities", new { externalUserId = "u-gerente", userId = gerente.Id });
        var (troca, _) = await c.EntrarAsync("u-gerente");
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));

        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Put, $"/organizations/{c.Organizacao.Id}/users/{gerente.Id}/role", new { role = "OrganizationAdmin" }));

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
        using var novo = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-gerente", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem });
        Assert.Equal(HttpStatusCode.Forbidden, novo.StatusCode);
    }

    // Sessão embutida: validade, limite, revogação

    [Fact]
    public async Task Sessao_embutida_expira_em_30_minutos_e_nao_e_estendida_pelo_uso()
    {
        var relogio = new RelogioManual();
        var c = await EmbeddedCenario.CriarAsync(banco, "curta", relogio: relogio);
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-22");

        relogio.Avancar(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
        relogio.Avancar(TimeSpan.FromMinutes(11));

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    [Fact]
    public async Task Limite_de_sessoes_por_usuario_externo_revoga_a_mais_antiga()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "limite", configurar: cfg => cfg["Identity:MaxEmbeddedSessionsPerUser"] = "2");
        await using var servidor = c.Api;

        var (a, _) = await c.EntrarAsync("u-23");
        await Task.Delay(20);
        var (b, _) = await c.EntrarAsync("u-23");
        await Task.Delay(20);
        var (cc, _) = await c.EntrarAsync("u-23");

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(a.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(b.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(cc.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    [Fact]
    public async Task Logout_do_erp_revoga_so_as_sessoes_do_usuario_externo_da_conexao()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "logout");
        await using var servidor = c.Api;
        var (ana1, _) = await c.EntrarAsync("u-ana");
        var (ana2, _) = await c.EntrarAsync("u-ana");
        var (beto, _) = await c.EntrarAsync("u-beto");

        using var resposta = await c.Erp.EnviarAsync("/embed/v1/sessions/revoke", new { externalUserId = "u-ana" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(2, (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(ana1.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(ana2.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(beto.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    [Fact]
    public async Task Logout_da_propria_sessao_embutida_funciona_pela_rota_de_atendimento()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "sair");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-sair");

        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(troca.Token, HttpMethod.Post, "/auth/logout"));

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    // Cliente em foco: ticket de contexto

    [Fact]
    public async Task Ticket_de_contexto_acrescenta_o_cliente_em_foco_e_so_vale_uma_vez()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "contexto");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-ctx", cliente: "CLI-1");

        var contexto = await c.Erp.EmitirContextoAsync("u-ctx", "CLI-2");
        using var resgate = await c.Api.EnviarAsync(troca.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = contexto.Ticket });
        Assert.Equal(HttpStatusCode.OK, resgate.StatusCode);
        Assert.Equal("CLI-2", (await resgate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("customerRef").GetString());

        var sessao = await c.Api.LerAsync<SessaoEmbutidaDto>(troca.Token, "/embed/v1/session");
        Assert.Equal(new[] { "CLI-1", "CLI-2" }, sessao.CustomerRefs.Order().ToArray());

        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = contexto.Ticket }));
    }

    [Fact]
    public async Task Ticket_de_contexto_de_outro_usuario_externo_nao_vale_na_sessao_e_o_ticket_de_abertura_nao_serve_de_contexto()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "contexto2");
        await using var servidor = c.Api;
        var (ana, _) = await c.EntrarAsync("u-ana");
        _ = await c.EntrarAsync("u-beto");

        var doBeto = await c.Erp.EmitirContextoAsync("u-beto", "CLI-9");
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(ana.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = doBeto.Ticket }));

        var verifier = EmbeddedTestSupport.NovoVerifier();
        var abertura = await c.Erp.EmitirAsync("u-ana", EmbeddedTestSupport.Challenge(verifier), cliente: "CLI-7");
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(ana.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = abertura.Ticket }));

        var sessao = await c.Api.LerAsync<SessaoEmbutidaDto>(ana.Token, "/embed/v1/session");
        Assert.Empty(sessao.CustomerRefs);
    }

    [Fact]
    public async Task Ticket_de_contexto_exige_vinculo_existente_e_nunca_provisiona()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "contexto3");
        await using var servidor = c.Api;

        using var resposta = await c.Erp.EnviarAsync("/embed/v1/context-tickets", new { externalUserId = "u-inexistente", customer = new { @ref = "CLI-1" } });

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal(0, await ContarAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @t AND auth_mode = 'Federated'", ("t", c.Tenant.TenantId.Value)));
    }

    [Fact]
    public async Task O_navegador_nao_cria_cliente_em_foco_por_conta_propria()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "foco");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-foco");

        // Sem ticket assinado pelo servidor do ERP, nenhum corpo muda os clientes autorizados.
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = "ibt_" + new string('a', 43) }));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Post, "/embed/v1/context", new { customerRef = "CLI-INVENTADO" }));
        Assert.Empty((await c.Api.LerAsync<SessaoEmbutidaDto>(troca.Token, "/embed/v1/session")).CustomerRefs);
    }

    // Isolamento entre tenants

    [Fact]
    public async Task Chaves_e_tickets_de_um_tenant_nunca_alcancam_o_outro()
    {
        var a = await EmbeddedCenario.CriarAsync(banco, "isola-a");
        await using var servidor = a.Api;
        var b = await EmbeddedCenario.CriarAsync(banco, "isola-b", api: a.Api);

        // A chave de A assinando com o segredo de B (e vice-versa) não autentica.
        var corpo = new { externalUserId = "u-x", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };
        using var cruzada = await a.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo, segredoUsado: b.Chave.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, cruzada.StatusCode);

        // O mesmo identificador externo em dois tenants são DUAS identidades, cada uma no seu tenant.
        var (trocaA, _) = await a.EntrarAsync("mesmo-id");
        var (trocaB, _) = await b.EntrarAsync("mesmo-id");
        Assert.NotEqual(trocaA.User.Id, trocaB.User.Id);
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.external_identities WHERE tenant_id = @t AND external_user_id = 'mesmo-id'", ("t", a.Tenant.TenantId.Value)));
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.external_identities WHERE tenant_id = @t AND external_user_id = 'mesmo-id'", ("t", b.Tenant.TenantId.Value)));

        // O ticket de contexto de A não vale na sessão de B (e a tentativa o queima).
        var contextoDeA = await a.Erp.EmitirContextoAsync("mesmo-id", "CLI-A");
        Assert.Equal(HttpStatusCode.Unauthorized, await a.Api.StatusAsync(trocaB.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = contextoDeA.Ticket }));
        Assert.Equal(HttpStatusCode.Unauthorized, await a.Api.StatusAsync(trocaA.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = contextoDeA.Ticket }));
        Assert.Empty((await a.Api.LerAsync<SessaoEmbutidaDto>(trocaB.Token, "/embed/v1/session")).CustomerRefs);

        // O logout assinado por B não toca as sessões de A.
        using var revogar = await b.Erp.EnviarAsync("/embed/v1/sessions/revoke", new { externalUserId = "mesmo-id" });
        Assert.Equal(HttpStatusCode.OK, revogar.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await a.Api.StatusAsync(trocaB.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.OK, await a.Api.StatusAsync(trocaA.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    // Limites de taxa, corpo e segredo

    [Fact]
    public async Task A_troca_do_ticket_tem_limite_de_taxa_por_origem()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "taxa", configurar: cfg => cfg["Embedded:RateLimit:ExchangePermitLimit"] = "3");
        await using var servidor = c.Api;

        var status = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            status.Add(await EmbeddedTestSupport.TentarTrocarAsync(c.Api, "ibt_" + new string('a', 43), EmbeddedTestSupport.NovoVerifier()));
        }

        Assert.Equal(3, status.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, status.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task As_chamadas_assinadas_tem_limite_de_taxa_por_chave()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "taxa2", configurar: cfg => cfg["Embedded:RateLimit:IntegrationPermitLimit"] = "2");
        await using var servidor = c.Api;
        var corpo = new { externalUserId = "u-taxa", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem };

        var status = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var resposta = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", corpo);
            status.Add(resposta.StatusCode);
        }

        Assert.Equal(2, status.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(2, status.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Corpo_grande_ou_json_invalido_na_chamada_assinada_e_recusado_sem_efeito()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "corpo");
        await using var servidor = c.Api;

        using var grande = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", null, corpoEnviado: Encoding.UTF8.GetBytes(new string('x', 9000)));
        using var invalido = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", null, corpoEnviado: Encoding.UTF8.GetBytes("{nao-e-json"));

        Assert.Equal(HttpStatusCode.BadRequest, grande.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalido.StatusCode); // a assinatura cobre o corpo que foi assinado (vazio), não o enviado
        Assert.Equal(0, await ContarAsync("SELECT count(*) FROM identity.launch_tickets WHERE tenant_id = @t", ("t", c.Tenant.TenantId.Value)));
    }

    // Segredos, auditoria e retenção

    [Fact]
    public async Task O_segredo_so_existe_cifrado_no_banco_e_nenhum_token_ticket_ou_segredo_vai_para_a_auditoria()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "segredos");
        await using var servidor = c.Api;
        var (troca, verifier) = await c.EntrarAsync("u-seg");
        var ticket = await c.Erp.EmitirAsync("u-seg", EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()));

        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT coalesce(string_agg(secret_ciphertext, ' '), '') || ' ' || (SELECT coalesce(string_agg(row_to_json(e)::text, ' '), '') FROM audit.audit_events e WHERE e.tenant_id = @t) FROM identity.integration_keys WHERE tenant_id = @t", conexao);
        comando.Parameters.AddWithValue("t", c.Tenant.TenantId.Value);
        var tudo = (string)(await comando.ExecuteScalarAsync())!;

        Assert.DoesNotContain(c.Chave.Secret, tudo);
        Assert.DoesNotContain(troca.Token, tudo);
        Assert.DoesNotContain(ticket.Ticket, tudo);
        Assert.DoesNotContain(verifier, tudo);
        Assert.Contains("v1:", tudo);
        Assert.Contains("identity.embed.exchange", tudo);
        Assert.Contains("identity.embed.ticket.issue", tudo);
    }

    [Fact]
    public async Task Tickets_e_nonces_vencidos_sao_expurgados_e_os_validos_ficam()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "retencao");
        await using var servidor = c.Api;
        _ = await c.EntrarAsync("u-ret");
        using var valido = await c.Erp.EnviarAsync("/embed/v1/launch-tickets", new { externalUserId = "u-ret", challenge = EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()), parentOrigin = EmbeddedTestSupport.Origem });
        Assert.Equal(HttpStatusCode.Created, valido.StatusCode);

        await using (var conexao = new NpgsqlConnection(TestDatabase.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var envelhecer = new NpgsqlCommand(
                "UPDATE identity.launch_tickets SET expires_at = now() - interval '10 days' WHERE tenant_id = @t AND used_at IS NOT NULL; UPDATE identity.integration_nonces SET expires_at = now() - interval '10 days' WHERE tenant_id = @t AND nonce IN (SELECT nonce FROM identity.integration_nonces WHERE tenant_id = @t ORDER BY expires_at LIMIT 1)", conexao);
            envelhecer.Parameters.AddWithValue("t", c.Tenant.TenantId.Value);
            await envelhecer.ExecuteNonQueryAsync();
        }

        var tarefas = c.Api.Services.GetServices<IaBimeta.SharedKernel.IRetentionTask>().Where(t => t.Name is "identity.launch_tickets" or "identity.integration_nonces").ToList();
        Assert.Equal(2, tarefas.Count);
        var passada = new IaBimeta.SharedKernel.RetentionPass(c.Tenant.TenantId.Value, Array.Empty<Guid>(), 1, DateTimeOffset.UtcNow.AddDays(-1), "teste");
        foreach (var tarefa in tarefas)
        {
            await tarefa.RunAsync(passada, dryRun: false);
        }

        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.launch_tickets WHERE tenant_id = @t", ("t", c.Tenant.TenantId.Value)));
        Assert.True(await ContarAsync("SELECT count(*) FROM identity.integration_nonces WHERE tenant_id = @t", ("t", c.Tenant.TenantId.Value)) >= 1);
    }
}
