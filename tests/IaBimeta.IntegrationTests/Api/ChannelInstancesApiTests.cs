using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Conversations;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// Vários números por tenant pela API real (HTTP, PostgreSQL real): a leitura devolve a instância de canal de cada sessão, o
/// rastro de encaminhamentos respeita o acesso da sessão e o simulador exercita dois números e o encaminhamento.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelInstancesApiTests(PostgresFixture banco)
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resposta)
    {
        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return documento.RootElement.Clone();
    }

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
        return (resposta.StatusCode, resposta.IsSuccessStatusCode && texto.Length > 0 ? JsonDocument.Parse(texto).RootElement.Clone() : default);
    }

    [Fact]
    public async Task A_mesma_pessoa_em_dois_numeros_aparece_em_duas_sessoes_com_a_instancia_de_canal_de_cada_uma()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "apinumeros");
        var gestor = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager);
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoComSimulador());
        var token = await api.LoginAsync(gestor);
        var numeroA = Guid.NewGuid();
        var numeroB = Guid.NewGuid();

        var (statusA, entradaA) = await SimularAsync(api, c.Tenant.Slug, "/simulator/inbound", new { channelInstanceId = numeroA, contactRef = "5511999990001", organizationId = c.OrganizacaoId, text = "Pelo número A" });
        var (statusB, entradaB) = await SimularAsync(api, c.Tenant.Slug, "/simulator/inbound", new { channelInstanceId = numeroB, contactRef = "5511999990001", organizationId = c.OrganizacaoId, text = "Pelo número B" });

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (statusA, statusB));
        Assert.NotEqual(entradaA.GetProperty("sessionId").GetGuid(), entradaB.GetProperty("sessionId").GetGuid());
        var lista = await JsonAsync(await api.EnviarAsync(token, HttpMethod.Get, "/conversations/sessions"));
        var canais = lista.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("channelInstanceId").GetGuid()).Order().ToList();
        Assert.Equal(new[] { numeroA, numeroB }.Order(), canais);

        var historicoA = await JsonAsync(await api.EnviarAsync(token, HttpMethod.Get, $"/conversations/sessions/{entradaA.GetProperty("sessionId").GetGuid()}/messages"));
        var mensagem = Assert.Single(historicoA.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(("Pelo número A", numeroA), (mensagem.GetProperty("text").GetString(), mensagem.GetProperty("channelInstanceId").GetGuid()));
    }

    [Fact]
    public async Task O_simulador_recusa_instancia_de_canal_vazia_ou_mal_formada_com_400()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "apicanalinv");
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoComSimulador());

        var (vazia, _) = await SimularAsync(api, c.Tenant.Slug, "/simulator/inbound", new { channelInstanceId = Guid.Empty, contactRef = "x", organizationId = c.OrganizacaoId, text = "oi" });
        var (mal, _) = await SimularAsync(api, c.Tenant.Slug, "/simulator/inbound", new { channelInstanceId = "nao-e-guid", contactRef = "x", organizationId = c.OrganizacaoId, text = "oi" });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (vazia, mal));
    }

    [Fact]
    public async Task O_encaminhamento_pelo_simulador_deixa_rastro_legivel_so_a_quem_pode_ler_a_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "apirastro");
        var outro = await CenarioDeConversas.CriarAsync(banco, "apirastrob");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var setor = await c.CriarSetorAsync(filial, "Exames");
        var gestor = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager);
        var comum = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        var gestorDeOutro = await banco.CriarAdministradorDoTenantAsync(outro.Tenant);
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoComSimulador());
        var token = await api.LoginAsync(gestor);

        // O simulador usa a autorização real do Channels: o número precisa existir no tenant e autorizar a unidade.
        var (_, entrada) = await SimularAsync(api, c.Tenant.Slug, "/simulator/inbound", new { channelInstanceId = canal.Value, contactRef = "5511999990001", organizationId = c.OrganizacaoId, businessUnitId = matriz, text = "Preciso de exame" });
        var sessaoId = entrada.GetProperty("sessionId").GetGuid();
        var transferId = Guid.NewGuid();
        var (status, resultado) = await SimularAsync(api, c.Tenant.Slug, "/simulator/handling/transfer", new { sessionId = sessaoId, targetBusinessUnitId = filial, targetDepartmentId = setor, expectedStateVersion = 1, transferId, reason = "specialty-unit" });

        Assert.Equal((HttpStatusCode.OK, "Applied", 2), (status, resultado.GetProperty("outcome").GetString(), resultado.GetProperty("stateVersion").GetInt32()));
        var rastro = await JsonAsync(await api.EnviarAsync(token, HttpMethod.Get, $"/conversations/sessions/{sessaoId}/transfers"));
        var item = Assert.Single(rastro.EnumerateArray().ToList());
        Assert.Equal((transferId, matriz, filial, setor, "specialty-unit"), (item.GetProperty("transferId").GetGuid(), item.GetProperty("fromBusinessUnitId").GetGuid(), item.GetProperty("toBusinessUnitId").GetGuid(), item.GetProperty("toDepartmentId").GetGuid(), item.GetProperty("reason").GetString()));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("requestedBy").ValueKind); // veio do contexto de sistema do simulador
        var sessao = await JsonAsync(await api.EnviarAsync(token, HttpMethod.Get, $"/conversations/sessions/{sessaoId}"));
        Assert.Equal((filial, setor), (sessao.GetProperty("businessUnitId").GetGuid(), sessao.GetProperty("departmentId").GetGuid()));

        // Acesso: sem token 401; sem leitura de conversas 404; outro tenant 404; sessão inexistente 404.
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(null, HttpMethod.Get, $"/conversations/sessions/{sessaoId}/transfers"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(await api.LoginAsync(comum), HttpMethod.Get, $"/conversations/sessions/{sessaoId}/transfers"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(await api.LoginAsync(gestorDeOutro), HttpMethod.Get, $"/conversations/sessions/{sessaoId}/transfers"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"/conversations/sessions/{Guid.NewGuid()}/transfers"));
    }

    [Fact]
    public async Task O_simulador_recusa_encaminhamento_com_dados_invalidos_com_400_e_o_gestor_autenticado_encaminha_com_o_rastro_no_nome_dele()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "apiencinv");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var gestor = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager);
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoComSimulador());
        var (_, entrada) = await SimularAsync(api, c.Tenant.Slug, "/simulator/inbound", new { channelInstanceId = canal.Value, contactRef = "5511999990001", organizationId = c.OrganizacaoId, businessUnitId = matriz, text = "oi" });
        var sessaoId = entrada.GetProperty("sessionId").GetGuid();

        var (motivoLivre, _) = await SimularAsync(api, c.Tenant.Slug, "/simulator/handling/transfer", new { sessionId = sessaoId, targetBusinessUnitId = filial, expectedStateVersion = 1, transferId = Guid.NewGuid(), reason = "Texto livre" });
        var (semId, _) = await SimularAsync(api, c.Tenant.Slug, "/simulator/handling/transfer", new { sessionId = sessaoId, targetBusinessUnitId = filial, expectedStateVersion = 1, transferId = Guid.Empty, reason = "manual" });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (motivoLivre, semId));
        // O encaminhamento, diferente dos comandos de máquina, também pode ser pedido por um usuário com ConversationManage: o
        // rastro registra o usuário do contexto como autor (e não um parâmetro). Não há rota HTTP real de escrita ainda.
        using var comToken = api.RequisicaoDe(await api.LoginAsync(gestor), HttpMethod.Post, "/simulator/handling/transfer", JsonContent.Create(new { sessionId = sessaoId, targetBusinessUnitId = filial, expectedStateVersion = 1, transferId = Guid.NewGuid(), reason = "manual" }));
        using var resposta = await api.Client.SendAsync(comToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var rastro = await JsonAsync(await api.EnviarAsync(await api.LoginAsync(gestor), HttpMethod.Get, $"/conversations/sessions/{sessaoId}/transfers"));
        Assert.Equal(gestor.Id, Assert.Single(rastro.EnumerateArray().ToList()).GetProperty("requestedBy").GetGuid());
    }
}
