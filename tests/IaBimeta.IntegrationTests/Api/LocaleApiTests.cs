using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Identity.Domain;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// Idioma pela API real (HTTP, PostgreSQL real): o idioma padrão do tenant, o idioma preferido do usuário e, pelo simulador de
/// desenvolvimento, o idioma da conversa, a dica do canal, a mudança durante a conversa e a tradução guardada à parte.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LocaleApiTests(PostgresFixture banco)
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resposta)
    {
        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return documento.RootElement.Clone();
    }

    private static async Task<string?> IdiomaPadraoAsync(ApiServer api, string token)
    {
        using var resposta = await api.EnviarAsync(token, HttpMethod.Get, "/tenant/settings");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await JsonAsync(resposta)).GetProperty("defaultLocale").GetString();
    }

    private static async Task<string?> PreferidoAsync(ApiServer api, string token)
    {
        using var resposta = await api.EnviarAsync(token, HttpMethod.Get, "/auth/me");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var preferido = (await JsonAsync(resposta)).GetProperty("preferredLocale");
        return preferido.ValueKind == JsonValueKind.Null ? null : preferido.GetString();
    }

    // Idioma padrão do tenant

    [Fact]
    public async Task O_administrador_do_tenant_troca_o_idioma_padrao_e_os_demais_papeis_so_leem()
    {
        var tenant = await banco.RegistrarTenantAsync("apitenant");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Organização")).Id;
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        var gestor = await banco.CriarUsuarioAsync(tenant, org, Role.Manager);
        var adminDaOrg = await banco.CriarUsuarioAsync(tenant, org, Role.OrganizationAdmin);
        var comum = await banco.CriarUsuarioAsync(tenant, org, Role.User);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var tokenAdmin = await api.LoginAsync(admin);

        Assert.Equal("pt-BR", await IdiomaPadraoAsync(api, tokenAdmin));
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(tokenAdmin, HttpMethod.Put, "/tenant/settings/default-locale", new { locale = "EN-us" }));
        Assert.Equal("en-US", await IdiomaPadraoAsync(api, tokenAdmin));

        foreach (var usuario in new[] { gestor, adminDaOrg, comum })
        {
            var token = await api.LoginAsync(usuario);
            Assert.Equal("en-US", await IdiomaPadraoAsync(api, token)); // qualquer usuário do tenant lê
            Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Put, "/tenant/settings/default-locale", new { locale = "es-ES" })); // só a concessão de tenant altera
        }

        Assert.Equal("en-US", await IdiomaPadraoAsync(api, tokenAdmin));
    }

    [Fact]
    public async Task Sem_token_o_idioma_padrao_nao_e_lido_nem_alterado_e_valor_invalido_responde_400()
    {
        var tenant = await banco.RegistrarTenantAsync("apitenantinv");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(admin);

        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(null, HttpMethod.Get, "/tenant/settings"));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync("token-invalido", HttpMethod.Put, "/tenant/settings/default-locale", new { locale = "en-US" }));
        foreach (var corpo in new object[] { new { locale = "pt_BR" }, new { locale = "" }, new { locale = (string?)null }, new { }, new { locale = "não é idioma" } })
        {
            Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Put, "/tenant/settings/default-locale", corpo));
        }

        Assert.Equal("pt-BR", await IdiomaPadraoAsync(api, token));
    }

    [Fact]
    public async Task O_idioma_padrao_de_um_tenant_nao_aparece_nem_muda_pelo_outro()
    {
        var a = await banco.RegistrarTenantAsync("apiidiomaa");
        var b = await banco.RegistrarTenantAsync("apiidiomab");
        var adminA = await banco.CriarAdministradorDoTenantAsync(a);
        var adminB = await banco.CriarAdministradorDoTenantAsync(b);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var tokenA = await api.LoginAsync(adminA);
        var tokenB = await api.LoginAsync(adminB);

        await api.StatusAsync(tokenA, HttpMethod.Put, "/tenant/settings/default-locale", new { locale = "en-US" });
        await api.StatusAsync(tokenB, HttpMethod.Put, "/tenant/settings/default-locale", new { locale = "es-ES" });

        Assert.Equal(("en-US", "es-ES"), (await IdiomaPadraoAsync(api, tokenA), await IdiomaPadraoAsync(api, tokenB)));

        // Nem tenant em corpo, nem em query string, nem em cabeçalho: o do contexto é o único.
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(tokenA, HttpMethod.Put, $"/tenant/settings/default-locale?tenantId={b.TenantId.Value}&tenant={b.Slug}", new { locale = "fr-FR", tenantId = b.TenantId.Value, tenant = b.Slug }));
        Assert.Equal(("fr-FR", "es-ES"), (await IdiomaPadraoAsync(api, tokenA), await IdiomaPadraoAsync(api, tokenB)));
    }

    // Idioma preferido do usuário

    [Fact]
    public async Task O_usuario_define_e_limpa_o_proprio_idioma_preferido_e_o_de_outro_usuario_nao_muda()
    {
        var tenant = await banco.RegistrarTenantAsync("apiusuario");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Organização")).Id;
        var bia = await banco.CriarUsuarioAsync(tenant, org, Role.User);
        var leo = await banco.CriarUsuarioAsync(tenant, org, Role.User);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var tokenBia = await api.LoginAsync(bia);
        var tokenLeo = await api.LoginAsync(leo);

        Assert.Null(await PreferidoAsync(api, tokenBia));
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(tokenBia, HttpMethod.Put, "/auth/me/locale", new { locale = "es-es" }));
        Assert.Equal("es-ES", await PreferidoAsync(api, tokenBia));
        Assert.Null(await PreferidoAsync(api, tokenLeo));

        // Sem campo de usuário: nenhuma forma de a Bia alterar o Leo.
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(tokenBia, HttpMethod.Put, "/auth/me/locale", new { locale = "de-DE", userId = leo.Id }));
        Assert.Null(await PreferidoAsync(api, tokenLeo));

        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(tokenBia, HttpMethod.Put, "/auth/me/locale", new { locale = (string?)null }));
        Assert.Null(await PreferidoAsync(api, tokenBia));
    }

    [Fact]
    public async Task Idioma_preferido_invalido_responde_400_e_sem_token_401()
    {
        var tenant = await banco.RegistrarTenantAsync("apiusuarioinv");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Organização")).Id;
        var bia = await banco.CriarUsuarioAsync(tenant, org, Role.User);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(bia);
        await api.StatusAsync(token, HttpMethod.Put, "/auth/me/locale", new { locale = "it-IT" });

        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(null, HttpMethod.Put, "/auth/me/locale", new { locale = "en-US" }));
        foreach (var invalido in new[] { "pt_BR", "português", "en-US-" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Put, "/auth/me/locale", new { locale = invalido }));
        }

        Assert.Equal("it-IT", await PreferidoAsync(api, token)); // as recusas não mudaram nada
    }

    [Fact]
    public async Task O_administrador_cria_o_usuario_com_idioma_preferido_e_idioma_invalido_recusa_a_criacao()
    {
        var tenant = await banco.RegistrarTenantAsync("apiusuariocria");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Organização")).Id;
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(admin);
        var email = $"novo{Guid.NewGuid().ToString("N")[..8]}@teste.local";

        using var criado = await api.EnviarAsync(token, HttpMethod.Post, $"/organizations/{org}/users",
            new { email, displayName = "Nova pessoa", password = PostgresFixture.PasswordPadrao, role = "User", preferredLocale = "FR-fr" });
        var invalido = await api.StatusAsync(token, HttpMethod.Post, $"/organizations/{org}/users",
            new { email = "outro" + email, displayName = "Outra", password = PostgresFixture.PasswordPadrao, role = "User", preferredLocale = "fr_FR" });

        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        Assert.Equal("fr-FR", (await JsonAsync(criado)).GetProperty("preferredLocale").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalido);
        var tokenDoNovo = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, PostgresFixture.PasswordPadrao, tenant.Slug));
        Assert.Equal("fr-FR", await PreferidoAsync(api, tokenDoNovo));
    }

    // Conversa pelo simulador de desenvolvimento

    private static Dictionary<string, string?> ConfiguracaoComSimulador()
    {
        var configuracao = ApiServer.ConfiguracaoDeDesenvolvimento();
        configuracao["Tenancy:Development:SimulatorEnabled"] = "true";
        return configuracao;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Corpo)> SimularAsync(ApiServer api, string slug, string rota, object corpo)
    {
        using var requisicao = api.Requisicao(HttpMethod.Post, rota, slug, JsonContent.Create(corpo));
        using var resposta = await api.Client.SendAsync(requisicao);
        var texto = await resposta.Content.ReadAsStringAsync();

        // Um 400 de leitura do corpo pode vir em texto simples (página de erro de desenvolvimento): só o status importa nele.
        if (resposta.IsSuccessStatusCode && texto.Length > 0)
        {
            return (resposta.StatusCode, JsonDocument.Parse(texto).RootElement.Clone());
        }

        return (resposta.StatusCode, default);
    }

    [Fact]
    public async Task O_idioma_da_conversa_vem_da_dica_do_canal_muda_durante_a_conversa_e_o_original_continua_intacto()
    {
        var tenant = await banco.RegistrarTenantAsync("apiconversa");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Organização")).Id;
        var gestor = await banco.CriarUsuarioAsync(tenant, org, Role.Manager);
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoComSimulador());
        var token = await api.LoginAsync(gestor);
        const string original = "  ¿Necesito ayuno para este examen? 😀  ";

        var (statusEntrada, entrada) = await SimularAsync(api, tenant.Slug, "/simulator/inbound",
            new { contactRef = "sim-idioma", organizationId = org, text = original, contactLocale = "es-es", messageLocale = "ES-es" });
        var sessaoId = entrada.GetProperty("sessionId").GetGuid();
        var mensagemId = entrada.GetProperty("messageId").GetGuid();
        var conversaId = entrada.GetProperty("conversationId").GetGuid();

        // A dica do canal vence o padrão do tenant (pt-BR): o idioma do tenant não é o do cliente.
        Assert.Equal(HttpStatusCode.OK, statusEntrada);
        var sessao = await JsonAsync(await api.EnviarAsync(token, HttpMethod.Get, $"/conversations/sessions/{sessaoId}"));
        Assert.Equal(("es-ES", "ChannelHint"), (sessao.GetProperty("effectiveLocale").GetString(), sessao.GetProperty("localeSource").GetString()));
        var mensagem = (await JsonAsync(await api.EnviarAsync(token, HttpMethod.Get, $"/conversations/sessions/{sessaoId}/messages"))).GetProperty("items")[0];
        Assert.Equal(original, mensagem.GetProperty("text").GetString()); // exatamente como veio, com os espaços e o emoji
        Assert.Equal(("es-ES", "ProviderDeclared"), (mensagem.GetProperty("locale").GetString(), mensagem.GetProperty("localeOrigin").GetString()));

        // O turno recebe o idioma da conversa; o cliente pede inglês e o idioma muda durante a conversa.
        var turno = Guid.NewGuid();
        var (_, inicio) = await SimularAsync(api, tenant.Slug, "/simulator/turns/begin", new { sessionId = sessaoId, turnId = turno, leaseToken = 1 });
        var snapshot = inicio.GetProperty("snapshot");
        Assert.Equal(("es-ES", "ChannelHint", conversaId), (snapshot.GetProperty("effectiveLocale").GetString(), snapshot.GetProperty("localeSource").GetString(), snapshot.GetProperty("conversationId").GetGuid()));
        var grant = inicio.GetProperty("grant");

        var (statusMudanca, mudanca) = await SimularAsync(api, tenant.Slug, "/simulator/handling/locale", new { sessionId = sessaoId, locale = "en-us", expectedStateVersion = grant.GetProperty("stateVersion").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, statusMudanca);
        Assert.Equal(("Applied", "en-US", "CustomerChoice"), (mudanca.GetProperty("outcome").GetString(), mudanca.GetProperty("effectiveLocale").GetString(), mudanca.GetProperty("source").GetString()));

        // A resposta escrita antes da mudança é recusada.
        var (_, tardia) = await SimularAsync(api, tenant.Slug, "/simulator/turns/reply", new
        {
            sessionId = sessaoId,
            turnId = turno,
            epoch = grant.GetProperty("epoch").GetInt64(),
            expectedStateVersion = grant.GetProperty("stateVersion").GetInt64(),
            basedOnInboundSeq = grant.GetProperty("basedOnInboundSeq").GetInt64(),
            replyKind = "Generated",
            targetConversationId = conversaId,
            text = "Sí, ocho horas.",
            aiRequestIds = new[] { Guid.NewGuid() },
        });
        Assert.Equal("StaleState", tardia.GetProperty("outcome").GetString());

        // A tradução fica à parte, e o original e o idioma dele não mudam.
        var (statusTraducao, traducao) = await SimularAsync(api, tenant.Slug, "/simulator/translations", new { messageId = mensagemId, targetLocale = "pt-BR", text = "Preciso de jejum para este exame?", provenance = "human" });
        Assert.Equal((HttpStatusCode.OK, "Recorded"), (statusTraducao, traducao.GetProperty("outcome").GetString()));
        var depois = (await JsonAsync(await api.EnviarAsync(token, HttpMethod.Get, $"/conversations/sessions/{sessaoId}/messages"))).GetProperty("items")[0];
        Assert.Equal((original, "es-ES"), (depois.GetProperty("text").GetString(), depois.GetProperty("locale").GetString()));
        var sessaoDepois = await JsonAsync(await api.EnviarAsync(token, HttpMethod.Get, $"/conversations/sessions/{sessaoId}"));
        Assert.Equal(("en-US", "CustomerChoice", 2), (sessaoDepois.GetProperty("effectiveLocale").GetString(), sessaoDepois.GetProperty("localeSource").GetString(), sessaoDepois.GetProperty("stateVersion").GetInt32()));
    }

    [Fact]
    public async Task O_simulador_recusa_idioma_mal_formado_com_400()
    {
        var tenant = await banco.RegistrarTenantAsync("apiconversainv");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Organização")).Id;
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoComSimulador());

        var (contato, _) = await SimularAsync(api, tenant.Slug, "/simulator/inbound", new { contactRef = "sim-inv", organizationId = org, text = "oi", contactLocale = "pt_BR" });
        var (mensagem, _) = await SimularAsync(api, tenant.Slug, "/simulator/inbound", new { contactRef = "sim-inv", organizationId = org, text = "oi", messageLocale = "português" });
        var (mudanca, _) = await SimularAsync(api, tenant.Slug, "/simulator/handling/locale", new { sessionId = Guid.NewGuid(), locale = "pt_BR", expectedStateVersion = 1 });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (contato, mensagem, mudanca));
    }
}
