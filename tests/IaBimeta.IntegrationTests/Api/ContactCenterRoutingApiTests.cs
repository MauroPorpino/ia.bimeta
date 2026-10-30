using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O roteamento, o horário, a política das filas, a presença, a prioridade e o "assumir a próxima" pela API real (HTTP, PostgreSQL
/// real): 401, 403 e 404 conforme TEN-2, 400 para dados inválidos, 422 com o código da regra, 204 para fila vazia.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactCenterRoutingApiTests(PostgresFixture banco)
{
    private sealed record Cena(CenarioDeConversas C, ApiServer Api, string Admin, string Gerente, UsuarioCriado Ana, string TokenDaAna);

    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.OrganizationAdmin));
        var gerente = await api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager));
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        return new Cena(c, api, admin, gerente, ana, await api.LoginAsync(ana));
    }

    private static async Task<Guid> CriarFilaAsync(Cena s, string nome) =>
        (await s.Api.CriarAsync<JsonElement>(s.Admin, "/contact-center/queues", new { organizationId = s.C.OrganizacaoId, name = nome })).GetProperty("id").GetGuid();

    private static async Task<JsonElement> LerCorpoAsync(HttpResponseMessage resposta) => await resposta.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task O_administrador_configura_politica_e_horario_e_a_fila_mostra_se_atende_agora()
    {
        var s = await MontarAsync("apipolitica");
        await using var _ = s.Api;
        var fila = await CriarFilaAsync(s, "Principal");
        var contingencia = await CriarFilaAsync(s, "Contingência");

        using var politica = await s.Api.EnviarAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/policy",
            new { fallbackQueueId = contingencia, outOfHours = "Fallback", noAgent = "Fallback", defaultPriority = 3, maxItemsPerAgent = 2 });
        Assert.Equal(HttpStatusCode.OK, politica.StatusCode);
        var corpo = await LerCorpoAsync(politica);
        Assert.Equal(("Fallback", "Fallback", 3, 2, contingencia), (corpo.GetProperty("outOfHours").GetString(), corpo.GetProperty("noAgent").GetString(), corpo.GetProperty("defaultPriority").GetInt32(), corpo.GetProperty("maxItemsPerAgent").GetInt32(), corpo.GetProperty("fallbackQueueId").GetGuid()));

        using var horario = await s.Api.EnviarAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/schedule", new
        {
            timeZone = "America/Sao_Paulo",
            windows = new[] { new { dayOfWeek = "Monday", start = "08:00", end = "12:00" }, new { dayOfWeek = "Monday", start = "13:00", end = "18:00" } },
            closedDates = new[] { "2026-12-25" },
        });
        Assert.Equal(HttpStatusCode.OK, horario.StatusCode);
        var lido = await s.Api.LerAsync<JsonElement>(s.Admin, $"/contact-center/queues/{fila}/schedule");
        Assert.Equal(("America/Sao_Paulo", 2, 1), (lido.GetProperty("timeZoneId").GetString(), lido.GetProperty("windows").GetArrayLength(), lido.GetProperty("closedDates").GetArrayLength()));
        Assert.Equal(("Monday", "08:00", "12:00"), (lido.GetProperty("windows")[0].GetProperty("dayOfWeek").GetString(), lido.GetProperty("windows")[0].GetProperty("start").GetString(), lido.GetProperty("windows")[0].GetProperty("end").GetString()));

        // Sem horário, a fila atende sempre.
        using var removido = await s.Api.EnviarAsync(s.Admin, HttpMethod.Delete, $"/contact-center/queues/{fila}/schedule");
        Assert.True((await LerCorpoAsync(removido)).GetProperty("openNow").GetBoolean());
        var disponibilidade = await s.Api.LerAsync<JsonElement>(s.Admin, $"/contact-center/queues/{fila}/availability");
        Assert.Equal((0, 0, 0, true), (disponibilidade.GetProperty("members").GetInt32(), disponibilidade.GetProperty("availableAgents").GetInt32(), disponibilidade.GetProperty("waitingItems").GetInt32(), disponibilidade.GetProperty("openNow").GetBoolean()));
    }

    [Fact]
    public async Task Politica_e_horario_invalidos_respondem_400_e_quem_so_le_responde_403_e_o_usuario_comum_404()
    {
        var s = await MontarAsync("apipoliticaerros");
        await using var _ = s.Api;
        var fila = await CriarFilaAsync(s, "Principal");

        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/schedule", new { timeZone = "Marte/Olympus", windows = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/schedule", new { timeZone = "UTC", windows = new[] { new { dayOfWeek = "Monday", start = "12:00", end = "08:00" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/policy", new { outOfHours = "Fallback", noAgent = "Wait", defaultPriority = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/policy", new { outOfHours = "Accept", noAgent = "Wait", defaultPriority = 12 }));

        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Gerente, HttpMethod.Put, $"/contact-center/queues/{fila}/policy", new { outOfHours = "Accept", noAgent = "Wait", defaultPriority = 1 }));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Gerente, HttpMethod.Put, $"/contact-center/queues/{fila}/schedule", new { timeZone = "UTC" }));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Gerente, HttpMethod.Get, $"/contact-center/queues/{fila}/schedule"));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Gerente, HttpMethod.Get, $"/contact-center/queues/{fila}/availability"));

        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Put, $"/contact-center/queues/{fila}/policy", new { outOfHours = "Accept", noAgent = "Wait", defaultPriority = 1 }));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Get, $"/contact-center/queues/{fila}/schedule"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Get, $"/contact-center/queues/{fila}/availability"));
    }

    [Fact]
    public async Task As_regras_de_roteamento_seguem_a_autorizacao_e_o_pedido_sem_fila_e_roteado_com_a_prioridade_da_regra()
    {
        var s = await MontarAsync("apiregras");
        await using var _ = s.Api;
        var fila = await CriarFilaAsync(s, "Atendimento");
        var sessao = (await s.C.EntrarAsync("5511999990002")).SessionId;

        using var criada = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/routing-rules",
            new { organizationId = s.C.OrganizacaoId, name = "Tudo para o atendimento", queueId = fila, priority = 6, order = 1 });
        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        var regra = (await LerCorpoAsync(criada)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Gerente, HttpMethod.Post, "/contact-center/routing-rules", new { organizationId = s.C.OrganizacaoId, name = "x", queueId = fila, priority = 0, order = 0 }));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Gerente, HttpMethod.Get, $"/contact-center/routing-rules/{regra}"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Get, $"/contact-center/routing-rules/{regra}"));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Get, "/contact-center/routing-rules"));
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, "/contact-center/routing-rules", new { organizationId = s.C.OrganizacaoId, name = "Fila inexistente", queueId = Guid.NewGuid(), priority = 0, order = 0 }));

        // A simulação mostra o destino sem mudar nada; o pedido sem fila usa a regra.
        using var simulada = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/routing/preview", new { sessionId = sessao });
        var candidato = Assert.Single((await LerCorpoAsync(simulada)).GetProperty("candidates").EnumerateArray().ToList());
        Assert.Equal((fila, 6, regra), (candidato.GetProperty("queueId").GetGuid(), candidato.GetProperty("priority").GetInt32(), candidato.GetProperty("routingRuleId").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Gerente, HttpMethod.Post, "/contact-center/routing/preview", new { sessionId = sessao }));
        Assert.Empty(await s.C.ItensAsync());

        using var roteada = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = sessao, requestId = Guid.NewGuid(), reason = "ai-requested" });
        Assert.Equal(HttpStatusCode.OK, roteada.StatusCode);
        var item = (await LerCorpoAsync(roteada)).GetProperty("item");
        Assert.Equal((fila, 6, regra), (item.GetProperty("queueId").GetGuid(), item.GetProperty("priority").GetInt32(), item.GetProperty("routingRuleId").GetGuid()));

        // Desligada, a regra deixa de servir: o próximo pedido sem fila é recusado com o código (422).
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/contact-center/routing-rules/{regra}/disable"));
        var outra = (await s.C.EntrarAsync("5511999990003")).SessionId;
        using var semRota = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = outra, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, semRota.StatusCode);
        Assert.Equal("NoRoute", (await LerCorpoAsync(semRota)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Admin, HttpMethod.Delete, $"/contact-center/routing-rules/{regra}"));
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Admin, HttpMethod.Delete, $"/contact-center/routing-rules/{regra}"));
        Assert.Empty((await s.Api.LerAsync<JsonElement>(s.Admin, "/contact-center/routing-rules")).EnumerateArray());
    }

    [Fact]
    public async Task Presenca_prioridade_e_assumir_a_proxima_pela_API()
    {
        var s = await MontarAsync("apiproxima");
        await using var _ = s.Api;
        var fila = await CriarFilaAsync(s, "Atendimento");
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/members/{s.Ana.Id}", new { role = "Agent" }));
        var supervisor = await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.User);
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/members/{supervisor.Id}", new { role = "Supervisor" }));
        var tokenDoSupervisor = await s.Api.LoginAsync(supervisor);

        // A presença é de cada um: começa Offline, e só o próprio usuário a muda.
        Assert.Equal("Offline", (await s.Api.LerAsync<JsonElement>(s.TokenDaAna, "/contact-center/me/presence")).GetProperty("status").GetString());
        using var disponivel = await s.Api.EnviarAsync(s.TokenDaAna, HttpMethod.Put, "/contact-center/me/presence", new { status = "Available" });
        Assert.Equal("Available", (await LerCorpoAsync(disponivel)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Put, "/contact-center/me/presence", new { status = "Inexistente" }));
        Assert.Equal(1, (await s.Api.LerAsync<JsonElement>(s.Admin, $"/contact-center/queues/{fila}/availability")).GetProperty("availableAgents").GetInt32());

        var primeira = (await s.C.NaFilaAsync((await s.C.EntrarAsync("5511999990004")).SessionId, fila)).Id;
        var segunda = (await s.C.NaFilaAsync((await s.C.EntrarAsync("5511999990005")).SessionId, fila)).Id;

        // A prioridade é de quem supervisiona: a Ana (atendente) recebe 403, o supervisor muda e a segunda passa na frente.
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Post, $"/contact-center/items/{segunda}/priority", new { priority = 9, requestId = Guid.NewGuid() }));
        using var priorizada = await s.Api.EnviarAsync(tokenDoSupervisor, HttpMethod.Post, $"/contact-center/items/{segunda}/priority", new { priority = 9, requestId = Guid.NewGuid(), reason = "urgent-case" });
        Assert.Equal(9, (await LerCorpoAsync(priorizada)).GetProperty("item").GetProperty("priority").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(tokenDoSupervisor, HttpMethod.Post, $"/contact-center/items/{segunda}/priority", new { priority = 10, requestId = Guid.NewGuid() }));

        using var proxima = await s.Api.EnviarAsync(s.TokenDaAna, HttpMethod.Post, $"/contact-center/queues/{fila}/claim-next", new { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, proxima.StatusCode);
        Assert.Equal(segunda, (await LerCorpoAsync(proxima)).GetProperty("item").GetProperty("id").GetGuid());
        using var outra = await s.Api.EnviarAsync(s.TokenDaAna, HttpMethod.Post, $"/contact-center/queues/{fila}/claim-next", new { requestId = Guid.NewGuid() });
        Assert.Equal(primeira, (await LerCorpoAsync(outra)).GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Post, $"/contact-center/queues/{fila}/claim-next", new { requestId = Guid.NewGuid() }));

        // Quem não é atendente da fila não puxa conversa: o usuário comum não a enxerga (404) e o gerente enxerga sem poder (403).
        var comum = await s.Api.LoginAsync(await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.User));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(comum, HttpMethod.Post, $"/contact-center/queues/{fila}/claim-next", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Gerente, HttpMethod.Post, $"/contact-center/queues/{fila}/claim-next", new { requestId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Sem_token_toda_rota_de_roteamento_responde_401()
    {
        var s = await MontarAsync("apiroteamento401");
        await using var _ = s.Api;
        var id = Guid.NewGuid();

        var status = new[]
        {
            await s.Api.StatusAsync(null, HttpMethod.Put, $"/contact-center/queues/{id}/policy", new { outOfHours = "Accept", noAgent = "Wait", defaultPriority = 0 }),
            await s.Api.StatusAsync(null, HttpMethod.Get, $"/contact-center/queues/{id}/schedule"),
            await s.Api.StatusAsync(null, HttpMethod.Put, $"/contact-center/queues/{id}/schedule", new { timeZone = "UTC" }),
            await s.Api.StatusAsync(null, HttpMethod.Delete, $"/contact-center/queues/{id}/schedule"),
            await s.Api.StatusAsync(null, HttpMethod.Get, $"/contact-center/queues/{id}/availability"),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/queues/{id}/claim-next", new { requestId = Guid.NewGuid() }),
            await s.Api.StatusAsync(null, HttpMethod.Post, "/contact-center/routing-rules", new { organizationId = id, name = "x", queueId = id, priority = 0, order = 0 }),
            await s.Api.StatusAsync(null, HttpMethod.Get, "/contact-center/routing-rules"),
            await s.Api.StatusAsync(null, HttpMethod.Get, $"/contact-center/routing-rules/{id}"),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/routing-rules/{id}/enable"),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/routing-rules/{id}/disable"),
            await s.Api.StatusAsync(null, HttpMethod.Delete, $"/contact-center/routing-rules/{id}"),
            await s.Api.StatusAsync(null, HttpMethod.Post, "/contact-center/routing/preview", new { sessionId = id }),
            await s.Api.StatusAsync(null, HttpMethod.Get, "/contact-center/me/presence"),
            await s.Api.StatusAsync(null, HttpMethod.Put, "/contact-center/me/presence", new { status = "Available" }),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/items/{id}/priority", new { priority = 1, requestId = Guid.NewGuid() }),
        };

        Assert.All(status, st => Assert.Equal(HttpStatusCode.Unauthorized, st));
    }
}
