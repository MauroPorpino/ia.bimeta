using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A entrada nas filas, a atribuição, a transferência e o handoff pela API real (HTTP, PostgreSQL real): 401, 403 e 404 conforme
/// TEN-2, 409 na disputa por uma conversa, 422 quando uma regra recusa o destino, e o tenant e o usuário que age nunca vindo da
/// requisição.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactCenterHandoffApiTests(PostgresFixture banco)
{
    private sealed record Cena(CenarioDeConversas C, ApiServer Api, string Admin, Guid Fila, Guid Sessao, UsuarioCriado Ana, string TokenDaAna, UsuarioCriado Beto, string TokenDoBeto);

    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var admin = await api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.OrganizationAdmin));
        var fila = (await api.CriarAsync<JsonElement>(admin, "/contact-center/queues", new { organizationId = c.OrganizacaoId, name = "Atendimento" })).GetProperty("id").GetGuid();
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        var beto = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        foreach (var atendente in new[] { ana, beto })
        {
            Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(admin, HttpMethod.Put, $"/contact-center/queues/{fila}/members/{atendente.Id}", new { role = "Agent" }));
        }

        var sessao = (await c.EntrarAsync("5511999990001", texto: "Preciso ficar em jejum?")).SessionId;
        return new Cena(c, api, admin, fila, sessao, ana, await api.LoginAsync(ana), beto, await api.LoginAsync(beto));
    }

    private static async Task<JsonElement> LerCorpoAsync(HttpResponseMessage resposta) => await resposta.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task O_fluxo_completo_pedido_de_atendimento_assumir_ler_transferir_e_devolver_a_IA()
    {
        var s = await MontarAsync("apihandoff");
        await using var _ = s.Api;
        var outraFila = (await s.Api.CriarAsync<JsonElement>(s.Admin, "/contact-center/queues", new { organizationId = s.C.OrganizacaoId, name = "Outra fila" })).GetProperty("id").GetGuid();
        var pedido = Guid.NewGuid();

        // O administrador gere a conversa: pede o atendimento humano. Repetir o mesmo pedido é inofensivo.
        using var entrou = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = s.Sessao, queueId = s.Fila, requestId = pedido, reason = "customer-asked" });
        using var repetido = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = s.Sessao, queueId = s.Fila, requestId = pedido });
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (entrou.StatusCode, repetido.StatusCode));
        var corpoDeEntrada = await LerCorpoAsync(entrou);
        Assert.Equal(("Completed", "Waiting"), (corpoDeEntrada.GetProperty("outcome").GetString(), corpoDeEntrada.GetProperty("item").GetProperty("status").GetString()));
        Assert.Equal("AlreadyApplied", (await LerCorpoAsync(repetido)).GetProperty("outcome").GetString());
        var item = corpoDeEntrada.GetProperty("item").GetProperty("id").GetGuid();

        // A Ana enxerga as próprias filas e a conversa esperando, e a assume. O Beto perde a disputa (409 com o código).
        Assert.Equal(s.Fila, Assert.Single((await s.Api.LerAsync<JsonElement>(s.TokenDaAna, "/contact-center/me/queues")).EnumerateArray().ToList()).GetProperty("id").GetGuid());
        Assert.Equal(item, Assert.Single((await s.Api.LerAsync<JsonElement>(s.TokenDaAna, "/contact-center/items")).EnumerateArray().ToList()).GetProperty("id").GetGuid());
        using var assumiu = await s.Api.EnviarAsync(s.TokenDaAna, HttpMethod.Post, $"/contact-center/items/{item}/claim", new { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, assumiu.StatusCode);
        Assert.Equal(s.Ana.Id, (await LerCorpoAsync(assumiu)).GetProperty("item").GetProperty("assignedUserId").GetGuid());
        using var perdeu = await s.Api.EnviarAsync(s.TokenDoBeto, HttpMethod.Post, $"/contact-center/items/{item}/claim", new { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, perdeu.StatusCode);
        Assert.Equal("AlreadyAssigned", (await LerCorpoAsync(perdeu)).GetProperty("code").GetString());

        // O conteúdo é da Ana: o Beto vê o item, mas a conversa é negada (403); a Ana lê a conversa e as mensagens.
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoBeto, HttpMethod.Get, $"/contact-center/items/{item}"));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.TokenDoBeto, HttpMethod.Get, $"/contact-center/items/{item}/conversation"));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.TokenDoBeto, HttpMethod.Get, $"/contact-center/items/{item}/messages"));
        var conversa = await s.Api.LerAsync<JsonElement>(s.TokenDaAna, $"/contact-center/items/{item}/conversation");
        Assert.Equal((s.Sessao, "HumanAssisted", s.Ana.Id), (conversa.GetProperty("id").GetGuid(), conversa.GetProperty("handlingMode").GetString(), conversa.GetProperty("responderRef").GetGuid()));
        var mensagens = await s.Api.LerAsync<JsonElement>(s.TokenDaAna, $"/contact-center/items/{item}/messages?take=10&order=OldestFirst");
        Assert.Equal("Preciso ficar em jejum?", Assert.Single(mensagens.GetProperty("items").EnumerateArray().ToList()).GetProperty("text").GetString());

        // A Ana passa para o Beto e ele devolve à fila de origem; depois a conversa vai para outra fila e volta para a IA.
        using var passada = await s.Api.EnviarAsync(s.TokenDaAna, HttpMethod.Post, $"/contact-center/items/{item}/assign", new { userId = s.Beto.Id, requestId = Guid.NewGuid() });
        using var devolvida = await s.Api.EnviarAsync(s.TokenDoBeto, HttpMethod.Post, $"/contact-center/items/{item}/release", new { requestId = Guid.NewGuid() });
        using var transferida = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, $"/contact-center/items/{item}/transfer", new { queueId = outraFila, requestId = Guid.NewGuid(), reason = "specialty" });
        using var daIA = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, $"/contact-center/items/{item}/return-to-ai", new { requestId = Guid.NewGuid() });
        Assert.All(new[] { passada, devolvida, transferida, daIA }, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var encerrado = (await LerCorpoAsync(daIA)).GetProperty("item");
        Assert.Equal(("Closed", "returned-to-ai"), (encerrado.GetProperty("status").GetString(), encerrado.GetProperty("closeReason").GetString()));

        // O histórico guarda cada passo, na ordem, e o estado final da conversa volta a ser da IA.
        var tipos = (await s.Api.LerAsync<JsonElement>(s.Admin, $"/contact-center/items/{item}/history")).EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(new[] { "Entered", "Assigned", "Reassigned", "Released", "TransferredQueue", "ReturnedToAi" }, tipos);
        Assert.Equal(IaBimeta.Conversations.Contracts.HandlingMode.AiAutonomous, (await s.C.SessaoAsync(s.Sessao))!.HandlingMode);
    }

    [Fact]
    public async Task Parametros_invalidos_da_consulta_das_mensagens_respondem_400_e_nunca_500()
    {
        var s = await MontarAsync("apimsgs400");
        await using var _ = s.Api;
        var item = (await s.C.NaFilaAsync(s.Sessao, s.Fila)).Id;
        var caminho = $"/contact-center/items/{item}/messages";

        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Get, caminho));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Get, $"{caminho}?take=100&order=newestfirst"));
        foreach (var consulta in new[] { "take=0", "take=-3", "take=101", "take=100000", "take=abc", "cursor=isto-nao-e-um-cursor", "order=lateral", "cursor=%00%FF" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.TokenDaAna, HttpMethod.Get, $"{caminho}?{consulta}"));
        }
    }

    [Fact]
    public async Task Sem_token_toda_rota_de_atendimento_responde_401()
    {
        var s = await MontarAsync("apihandoff401");
        await using var _ = s.Api;
        var id = Guid.NewGuid();
        var corpo = new { requestId = Guid.NewGuid() };

        var status = new[]
        {
            await s.Api.StatusAsync(null, HttpMethod.Get, "/contact-center/me/queues"),
            await s.Api.StatusAsync(null, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = s.Sessao, queueId = s.Fila, requestId = Guid.NewGuid() }),
            await s.Api.StatusAsync(null, HttpMethod.Get, "/contact-center/items"),
            await s.Api.StatusAsync(null, HttpMethod.Get, $"/contact-center/items/{id}"),
            await s.Api.StatusAsync(null, HttpMethod.Get, $"/contact-center/items/{id}/history"),
            await s.Api.StatusAsync(null, HttpMethod.Get, $"/contact-center/items/{id}/conversation"),
            await s.Api.StatusAsync(null, HttpMethod.Get, $"/contact-center/items/{id}/messages"),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/items/{id}/claim", corpo),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/items/{id}/assign", new { userId = id, requestId = Guid.NewGuid() }),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/items/{id}/release", corpo),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/items/{id}/transfer", new { queueId = id, requestId = Guid.NewGuid() }),
            await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/items/{id}/return-to-ai", corpo),
        };

        Assert.All(status, st => Assert.Equal(HttpStatusCode.Unauthorized, st));
    }

    [Fact]
    public async Task Quem_nao_enxerga_o_item_recebe_404_quem_nao_le_filas_recebe_403_na_lista_e_a_conversa_nao_muda()
    {
        var s = await MontarAsync("apihandoffperfis");
        await using var _ = s.Api;
        var item = (await s.C.NaFilaAsync(s.Sessao, s.Fila)).Id;
        var forasteiro = await s.Api.LoginAsync(await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.User));
        var gerente = await s.Api.LoginAsync(await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.Manager));

        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(forasteiro, HttpMethod.Get, "/contact-center/items"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(forasteiro, HttpMethod.Get, $"/contact-center/items/{item}"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(forasteiro, HttpMethod.Get, $"/contact-center/items/{item}/conversation"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(forasteiro, HttpMethod.Post, $"/contact-center/items/{item}/claim", new { requestId = Guid.NewGuid() }));

        // O gerente lê as filas da organização: vê o item, mas não opera nem lê o conteúdo por aqui (403).
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(gerente, HttpMethod.Get, $"/contact-center/items/{item}"));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(gerente, HttpMethod.Post, $"/contact-center/items/{item}/claim", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(gerente, HttpMethod.Get, $"/contact-center/items/{item}/conversation"));
        Assert.Equal(IaBimeta.ContactCenter.Domain.QueueItemStatus.Waiting, (await s.C.ItemAsync(item))!.Status);
    }

    [Fact]
    public async Task Regras_que_recusam_o_destino_respondem_422_com_o_codigo_e_dados_invalidos_400()
    {
        var s = await MontarAsync("apihandoff422");
        await using var _ = s.Api;
        var matriz = await s.C.CriarUnidadeAsync(s.C.OrganizacaoId, "Matriz", matriz: true);
        var filial = await s.C.CriarUnidadeAsync(s.C.OrganizacaoId, "Filial");
        var filaDaFilial = await s.C.CriarFilaAsync("Filial", unidade: filial);
        var daMatriz = await s.C.NovaConversaAsync(unidade: matriz);

        using var naoCobre = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = daMatriz, queueId = filaDaFilial.Id, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, naoCobre.StatusCode);
        Assert.Equal("QueueDoesNotCoverSession", (await LerCorpoAsync(naoCobre)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = s.Sessao, queueId = s.Fila, requestId = Guid.Empty }));
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = s.Sessao, queueId = s.Fila, requestId = Guid.NewGuid(), reason = "Texto livre" }));
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Get, "/contact-center/items?status=inexistente"));
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Get, "/contact-center/items?take=0"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = Guid.NewGuid(), queueId = s.Fila, requestId = Guid.NewGuid() }));

        // A conversa que ficou como estava: nenhuma recusa mexeu no estado.
        Assert.Equal(IaBimeta.Conversations.Contracts.HandlingMode.AiAutonomous, (await s.C.SessaoAsync(daMatriz))!.HandlingMode);
        Assert.Empty(await s.C.ItensAsync());
    }

    [Fact]
    public async Task Outro_tenant_ve_404_e_o_tenant_e_o_usuario_que_age_nunca_vem_do_corpo()
    {
        var s = await MontarAsync("apihandofftenant");
        await using var _ = s.Api;
        var item = (await s.C.NaFilaAsync(s.Sessao, s.Fila)).Id;
        var outro = await CenarioDeConversas.CriarAsync(banco, "apihandofftenantb");
        var adminDoOutro = await s.Api.LoginAsync(await banco.CriarAdministradorDoTenantAsync(outro.Tenant));

        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(adminDoOutro, HttpMethod.Get, $"/contact-center/items/{item}"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(adminDoOutro, HttpMethod.Post, $"/contact-center/items/{item}/return-to-ai", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(adminDoOutro, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = s.Sessao, queueId = s.Fila, requestId = Guid.NewGuid() }));
        Assert.Empty((await s.Api.LerAsync<JsonElement>(adminDoOutro, "/contact-center/items")).EnumerateArray());

        // Campos de tenant e de usuário no corpo são ignorados: quem assume é sempre o usuário da sessão (a Ana), nunca o do corpo.
        using var assumiu = await s.Api.EnviarAsync(s.TokenDaAna, HttpMethod.Post, $"/contact-center/items/{item}/claim",
            new { requestId = Guid.NewGuid(), tenantId = outro.Tenant.TenantId.Value, userId = s.Beto.Id, actorUserId = s.Beto.Id });
        Assert.Equal(HttpStatusCode.OK, assumiu.StatusCode);
        Assert.Equal(s.Ana.Id, (await s.C.ItemAsync(item))!.AssignedUserId);
    }
}
