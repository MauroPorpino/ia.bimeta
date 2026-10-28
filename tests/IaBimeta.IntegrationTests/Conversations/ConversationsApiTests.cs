using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Api;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using Microsoft.Extensions.Configuration;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// A leitura de conversas pela API real (HTTP, PostgreSQL real): sem identidade válida é 401, sem leitura na organização o
/// recurso responde 404 (e a lista 403), tenants são isolados, a paginação valida entrada, e o JSON só expõe o contrato.
/// Também o simulador de desenvolvimento: existe só em Development, com o contexto de sistema, e nunca com token de usuário.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationsApiTests(PostgresFixture banco)
{
    private const string Sessoes = "/conversations/sessions";

    private sealed record PaginaDto<T>(List<T> Items, string? NextCursor);

    private sealed record SessaoDto(Guid Id, Guid OrganizationId, string Status, string HandlingMode, long StateVersion, long LastInboundSeq, long AnsweredThroughSeq, bool HasActiveTurn);

    private sealed record MensagemDto(Guid Id, Guid SessionId, string Direction, string Author, long MessageSeq, string Kind, string ContentState, string? Text);

    private sealed record EntradaDto(string Outcome, Guid SessionId, Guid ConversationId, Guid MessageId, long InboundSeq);

    private sealed record ConcessaoDeTurnoDto(long Epoch, long BasedOnInboundSeq, long StateVersion);

    private sealed record MensagemDeSnapshotDto(string? Text);

    private sealed record SnapshotDto(Guid SessionId, List<MensagemDeSnapshotDto> Messages);

    private sealed record InicioDeTurnoDto(string Outcome, ConcessaoDeTurnoDto? Grant, SnapshotDto? Snapshot);

    private sealed record RespostaDto(string Outcome, Guid? MessageId);

    private sealed record Ambiente(
        CenarioDeConversas Cenario,
        Guid Org2,
        IngestInboundMessageResultDto S1,
        IngestInboundMessageResultDto S2,
        UsuarioCriado AdminDoTenant,
        UsuarioCriado AdminDaOrg1,
        UsuarioCriado Gestor,
        UsuarioCriado UsuarioComum,
        UsuarioCriado UsuarioDaOrg2);

    private sealed record IngestInboundMessageResultDto(Guid SessionId, Guid ConversationId);

    private async Task<Ambiente> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, $"Org 2 {prefixo}")).Id;
        var a = await c.EntrarAsync("5511999990001", "oi");
        await c.EntrarAsync("5511999990001", "tudo bem?");
        await c.EntrarAsync("5511999990001", "preciso de ajuda");
        var b = await c.EntrarAsync("5511999990002", "mensagem da organização 2", organizacao: org2);
        return new Ambiente(
            c,
            org2,
            new IngestInboundMessageResultDto(a.SessionId, a.ConversationId),
            new IngestInboundMessageResultDto(b.SessionId, b.ConversationId),
            await banco.CriarAdministradorDoTenantAsync(c.Tenant),
            await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.OrganizationAdmin),
            await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager),
            await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User),
            await banco.CriarUsuarioAsync(c.Tenant, org2, Role.User));
    }

    private static async Task<List<MensagemDto>> TodasAsMensagensAsync(ApiServer api, string token, Guid sessao, int take, string ordem)
    {
        var itens = new List<MensagemDto>();
        string? cursor = null;
        do
        {
            var caminho = $"{Sessoes}/{sessao}/messages?take={take}&order={ordem}" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var pagina = await api.LerAsync<PaginaDto<MensagemDto>>(token, caminho);
            itens.AddRange(pagina.Items);
            cursor = pagina.NextCursor;
        }
        while (cursor is not null);
        return itens;
    }

    // Identidade

    [Fact]
    public async Task Sem_token_ou_com_token_invalido_todas_as_rotas_respondem_401()
    {
        var a = await MontarAsync("semtoken");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var rotas = new[] { Sessoes, $"{Sessoes}/{a.S1.SessionId}", $"{Sessoes}/{a.S1.SessionId}/messages" };

        foreach (var rota in rotas)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(null, HttpMethod.Get, rota));
            Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync("token-que-nao-existe", HttpMethod.Get, rota));
        }
    }

    [Fact]
    public async Task Sessao_expirada_deixa_de_ler_conversas()
    {
        var a = await MontarAsync("expirada");
        var relogio = new RelogioManual();
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao(), relogio);
        var token = await api.LoginAsync(a.Gestor);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, Sessoes));

        relogio.Avancar(TimeSpan.FromHours(8));

        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(token, HttpMethod.Get, Sessoes));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/{a.S1.SessionId}/messages"));
    }

    [Fact]
    public async Task O_tenant_nao_vem_da_requisicao_nem_por_cabecalho_nem_por_query_string()
    {
        var a = await MontarAsync("origem");
        var outro = await MontarAsync("outrotenant");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.Gestor);

        using var cabecalho = api.RequisicaoDe(token, HttpMethod.Get, Sessoes);
        cabecalho.Headers.Add("X-Dev-Tenant", outro.Cenario.Tenant.Slug);
        using var respostaComCabecalho = await api.Client.SendAsync(cabecalho);
        var daQuery = await api.LerAsync<PaginaDto<SessaoDto>>(token, $"{Sessoes}?tenantId={outro.Cenario.Tenant.TenantId.Value}&tenant={outro.Cenario.Tenant.Slug}");

        Assert.Equal(HttpStatusCode.OK, respostaComCabecalho.StatusCode);
        var doCabecalho = (await respostaComCabecalho.Content.ReadFromJsonAsync<PaginaDto<SessaoDto>>())!;
        Assert.Equal(new[] { a.S1.SessionId }, doCabecalho.Items.Select(s => s.Id));
        Assert.Equal(new[] { a.S1.SessionId }, daQuery.Items.Select(s => s.Id));
    }

    // Autorização por papel e por organização

    [Fact]
    public async Task Cada_papel_le_so_as_conversas_das_organizacoes_em_que_tem_leitura()
    {
        var a = await MontarAsync("papeis");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var doTenant = await api.LoginAsync(a.AdminDoTenant);
        var daOrg1 = await api.LoginAsync(a.AdminDaOrg1);
        var gestor = await api.LoginAsync(a.Gestor);

        Assert.Equal(new[] { a.S1.SessionId, a.S2.SessionId }.Order(), (await api.LerAsync<PaginaDto<SessaoDto>>(doTenant, Sessoes)).Items.Select(s => s.Id).Order());
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(doTenant, HttpMethod.Get, $"{Sessoes}/{a.S2.SessionId}"));

        foreach (var token in new[] { daOrg1, gestor })
        {
            Assert.Equal(new[] { a.S1.SessionId }, (await api.LerAsync<PaginaDto<SessaoDto>>(token, Sessoes)).Items.Select(s => s.Id));
            Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/{a.S1.SessionId}"));
            Assert.Equal(3, (await api.LerAsync<PaginaDto<MensagemDto>>(token, $"{Sessoes}/{a.S1.SessionId}/messages")).Items.Count);
            // A organização 2 responde como se a sessão não existisse.
            Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/{a.S2.SessionId}"));
            Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/{a.S2.SessionId}/messages"));
        }
    }

    [Fact]
    public async Task Usuario_comum_sem_permissao_de_conversa_recebe_403_na_lista_e_404_no_recurso()
    {
        var a = await MontarAsync("comum");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var comum = await api.LoginAsync(a.UsuarioComum);
        var daOrg2 = await api.LoginAsync(a.UsuarioDaOrg2);

        foreach (var token in new[] { comum, daOrg2 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Get, Sessoes));
            Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/{a.S1.SessionId}"));
            Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/{a.S1.SessionId}/messages"));
            Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/{Guid.NewGuid()}"));
        }
    }

    [Fact]
    public async Task Usuario_de_outro_tenant_nao_ve_nem_sabe_que_a_conversa_existe()
    {
        var a = await MontarAsync("tenanta");
        var b = await MontarAsync("tenantb");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var gestorA = await api.LoginAsync(a.Gestor);
        var adminB = await api.LoginAsync(b.AdminDoTenant);

        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminB, HttpMethod.Get, $"{Sessoes}/{a.S1.SessionId}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminB, HttpMethod.Get, $"{Sessoes}/{a.S1.SessionId}/messages"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(gestorA, HttpMethod.Get, $"{Sessoes}/{b.S1.SessionId}"));
        var listaDeB = await api.LerAsync<PaginaDto<SessaoDto>>(adminB, Sessoes);
        Assert.DoesNotContain(listaDeB.Items, s => s.Id == a.S1.SessionId || s.Id == a.S2.SessionId);
        Assert.Equal(2, listaDeB.Items.Count);
    }

    [Fact]
    public async Task Nao_ha_rota_para_escrever_mensagem_nem_alterar_sessao_pela_API_de_usuario()
    {
        var a = await MontarAsync("semescrita");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var admin = await api.LoginAsync(a.AdminDoTenant);
        var corpo = new { text = "mensagem forjada", organizationId = a.Cenario.OrganizacaoId };
        var naoEscrevem = new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed };

        foreach (var (metodo, rota) in new[]
                 {
                     (HttpMethod.Post, Sessoes),
                     (HttpMethod.Post, $"{Sessoes}/{a.S1.SessionId}/messages"),
                     (HttpMethod.Put, $"{Sessoes}/{a.S1.SessionId}"),
                     (HttpMethod.Delete, $"{Sessoes}/{a.S1.SessionId}"),
                     (HttpMethod.Post, "/simulator/inbound"),
                 })
        {
            Assert.Contains(await api.StatusAsync(admin, metodo, rota, corpo), naoEscrevem);
        }

        Assert.Equal(3, (await api.LerAsync<PaginaDto<MensagemDto>>(admin, $"{Sessoes}/{a.S1.SessionId}/messages")).Items.Count);
    }

    // Paginação e validação de entrada

    [Fact]
    public async Task A_paginacao_pela_API_percorre_todo_o_historico_nas_duas_ordens_e_o_padrao_e_do_mais_novo_para_o_mais_antigo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "apipag");
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        Guid sessao = default;
        for (var i = 1; i <= 25; i++)
        {
            sessao = (await c.EntrarAsync("5511999990001", $"m{i}", quando: t0.AddSeconds(i))).SessionId;
        }

        var gestor = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(gestor);

        var antigas = await TodasAsMensagensAsync(api, token, sessao, 10, "asc");
        var novas = await TodasAsMensagensAsync(api, token, sessao, 7, "desc");
        var padrao = await api.LerAsync<PaginaDto<MensagemDto>>(token, $"{Sessoes}/{sessao}/messages?take=3");

        Assert.Equal(Enumerable.Range(1, 25).Select(i => $"m{i}"), antigas.Select(m => m.Text));
        Assert.Equal(Enumerable.Range(1, 25).Reverse().Select(i => $"m{i}"), novas.Select(m => m.Text));
        Assert.Equal(new[] { "m25", "m24", "m23" }, padrao.Items.Select(m => m.Text));
        Assert.NotNull(padrao.NextCursor);
    }

    [Theory]
    [InlineData("messages?take=0")]
    [InlineData("messages?take=101")]
    [InlineData("messages?take=-5")]
    [InlineData("messages?cursor=isto-nao-e-um-cursor")]
    [InlineData("messages?order=de-lado")]
    public async Task Parametros_invalidos_do_historico_respondem_400(string consulta)
    {
        var a = await MontarAsync("invalido");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.Gestor);

        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/{a.S1.SessionId}/{consulta}"));
    }

    [Theory]
    [InlineData("?take=0")]
    [InlineData("?take=101")]
    [InlineData("?cursor=lixo")]
    [InlineData("?status=Rascunho")]
    [InlineData("?status=99")]
    public async Task Parametros_invalidos_da_lista_respondem_400(string consulta)
    {
        var a = await MontarAsync("invalidolista");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.Gestor);

        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Get, Sessoes + consulta));
    }

    [Fact]
    public async Task O_id_de_sessao_que_nao_e_guid_nao_casa_com_a_rota()
    {
        var a = await MontarAsync("idruim");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.Gestor);

        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/nao-e-guid"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"{Sessoes}/1' or '1'='1/messages"));
    }

    [Fact]
    public async Task A_lista_pagina_e_filtra_por_status_pela_API()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "apilista");
        var sessoes = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            sessoes.Add((await c.EntrarAsync($"55119999900{i:00}")).SessionId);
            await Task.Delay(5);
        }

        await c.EncerrarSessaoAsync(sessoes[0]);
        var gestor = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(gestor);

        var primeira = await api.LerAsync<PaginaDto<SessaoDto>>(token, $"{Sessoes}?take=2");
        var segunda = await api.LerAsync<PaginaDto<SessaoDto>>(token, $"{Sessoes}?take=2&cursor={Uri.EscapeDataString(primeira.NextCursor!)}");
        var terceira = await api.LerAsync<PaginaDto<SessaoDto>>(token, $"{Sessoes}?take=2&cursor={Uri.EscapeDataString(segunda.NextCursor!)}");
        var fechadas = await api.LerAsync<PaginaDto<SessaoDto>>(token, $"{Sessoes}?status=closed");

        Assert.Equal(sessoes.AsEnumerable().Reverse(), primeira.Items.Concat(segunda.Items).Concat(terceira.Items).Select(s => s.Id));
        Assert.Null(terceira.NextCursor);
        Assert.Equal(sessoes[0], Assert.Single(fechadas.Items).Id);
        Assert.Equal("Closed", fechadas.Items[0].Status);
    }

    // Contrato do JSON

    [Fact]
    public async Task O_JSON_expoe_so_o_contrato_com_enums_em_texto_e_sem_campos_internos()
    {
        var a = await MontarAsync("json");
        await a.Cenario.EntrarAsync("5511999990009", null, tipo: IaBimeta.Conversations.Contracts.MessageKind.Audio);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.Gestor);

        using var sessao = JsonDocument.Parse(await (await api.EnviarAsync(token, HttpMethod.Get, $"{Sessoes}/{a.S1.SessionId}")).Content.ReadAsStringAsync());
        var lista = await api.LerAsync<PaginaDto<SessaoDto>>(token, Sessoes);
        var midia = lista.Items.Single(s => s.Id != a.S1.SessionId);
        using var historico = JsonDocument.Parse(await (await api.EnviarAsync(token, HttpMethod.Get, $"{Sessoes}/{midia.Id}/messages")).Content.ReadAsStringAsync());

        Assert.Equal(
            new[]
            {
                "answeredThroughSeq", "businessUnitId", "channelInstanceId", "contactDisplayName", "contactId", "contactPreferredLocale", "createdAt", "departmentId", "effectiveLocale",
                "handlingMode", "hasActiveTurn", "id", "lastInboundSeq", "lastMessageAt", "localeSource", "organizationId", "replyDeliveryFailed", "responderRef", "stateVersion", "status",
            },
            sessao.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Open", sessao.RootElement.GetProperty("status").GetString());
        Assert.Equal("AiAutonomous", sessao.RootElement.GetProperty("handlingMode").GetString());

        // O idioma sai como texto BCP 47 na forma canônica, e a origem da escolha como enum em texto. Sem dica do canal nem
        // preferência, o idioma da conversa é o do tenant e a origem diz que foi uma suposição.
        Assert.Equal("pt-BR", sessao.RootElement.GetProperty("effectiveLocale").GetString());
        Assert.Equal("TenantDefault", sessao.RootElement.GetProperty("localeSource").GetString());
        Assert.Equal(JsonValueKind.Null, sessao.RootElement.GetProperty("contactPreferredLocale").ValueKind);

        Assert.Equal(new[] { "items", "nextCursor" }, historico.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var mensagem = historico.RootElement.GetProperty("items")[0];
        var campos = mensagem.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.All(new[] { "tenantId", "providerEventId", "aiRequestIds", "requestedBy", "resendRequestId", "turnId", "deliveryFailureReason" }, interno => Assert.DoesNotContain(interno, campos));
        Assert.Equal("Inbound", mensagem.GetProperty("direction").GetString());
        Assert.Equal("Audio", mensagem.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, mensagem.GetProperty("locale").ValueKind); // idioma da mensagem desconhecido: nunca presumido
        Assert.Equal(JsonValueKind.Null, mensagem.GetProperty("localeOrigin").ValueKind);
        Assert.Equal("Pending", mensagem.GetProperty("contentState").GetString());
        Assert.Equal(JsonValueKind.Null, mensagem.GetProperty("text").ValueKind); // conteúdo que ainda não está pronto não sai
    }

    // Simulador de desenvolvimento

    private static Dictionary<string, string?> ConfiguracaoComSimulador()
    {
        var configuracao = ApiServer.ConfiguracaoDeDesenvolvimento();
        configuracao["Tenancy:Development:SimulatorEnabled"] = "true";
        return configuracao;
    }

    private static async Task<T> DoSimuladorAsync<T>(ApiServer api, string slug, string rota, object corpo)
    {
        using var requisicao = api.Requisicao(HttpMethod.Post, rota, slug, JsonContent.Create(corpo));
        using var resposta = await api.Client.SendAsync(requisicao);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("true", Assert.Single(resposta.Headers.GetValues("X-Simulator")));
        return (await resposta.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task O_simulador_em_Development_percorre_entrada_turno_e_resposta_e_a_leitura_de_usuario_enxerga_o_resultado()
    {
        var a = await MontarAsync("simulador");
        var slug = a.Cenario.Tenant.Slug;
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoComSimulador());

        var entrada = await DoSimuladorAsync<EntradaDto>(api, slug, "/simulator/inbound",
            new { contactRef = "simulado-1", organizationId = a.Cenario.OrganizacaoId, text = "Preciso ficar em jejum para o exame A?", providerEventId = "sim-evt-1" });
        var repetida = await DoSimuladorAsync<EntradaDto>(api, slug, "/simulator/inbound",
            new { contactRef = "simulado-1", organizationId = a.Cenario.OrganizacaoId, text = "de novo", providerEventId = "sim-evt-1" });
        var turno = Guid.NewGuid();
        var inicio = await DoSimuladorAsync<InicioDeTurnoDto>(api, slug, "/simulator/turns/begin", new { sessionId = entrada.SessionId, turnId = turno, leaseToken = 1 });
        var resposta = await DoSimuladorAsync<RespostaDto>(api, slug, "/simulator/turns/reply", new
        {
            sessionId = entrada.SessionId,
            turnId = turno,
            epoch = inicio.Grant!.Epoch,
            expectedStateVersion = inicio.Grant.StateVersion,
            basedOnInboundSeq = inicio.Grant.BasedOnInboundSeq,
            replyKind = "Generated",
            targetConversationId = entrada.ConversationId,
            text = "Resposta simulada (não vem de modelo de IA).",
            aiRequestIds = new[] { Guid.NewGuid() },
        });

        Assert.Equal(("Registered", "Duplicate"), (entrada.Outcome, repetida.Outcome));
        Assert.Equal(entrada.MessageId, repetida.MessageId);
        Assert.Equal("Granted", inicio.Outcome);
        Assert.Equal(new[] { "Preciso ficar em jejum para o exame A?" }, inicio.Snapshot!.Messages.Select(m => m.Text));
        Assert.Equal("Accepted", resposta.Outcome);

        // Um usuário real, autenticado, lê o que o simulador gravou, pelo caminho normal e autorizado.
        var token = await api.LoginAsync(a.Gestor);
        var mensagens = await api.LerAsync<PaginaDto<MensagemDto>>(token, $"{Sessoes}/{entrada.SessionId}/messages?order=asc");
        Assert.Equal(new[] { "Inbound", "Outbound" }, mensagens.Items.Select(m => m.Direction));
        Assert.Equal(new[] { "Customer", "Ai" }, mensagens.Items.Select(m => m.Author));
        var sessao = await api.LerAsync<SessaoDto>(token, $"{Sessoes}/{entrada.SessionId}");
        Assert.Equal(1, sessao.AnsweredThroughSeq);

        using var eventos = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/simulator/events", slug));
        Assert.Equal(HttpStatusCode.OK, eventos.StatusCode);
        var texto = await eventos.Content.ReadAsStringAsync();
        Assert.Contains("conversations.outbound-message-requested", texto);
        Assert.DoesNotContain("Preciso ficar em jejum", texto); // os eventos não carregam o conteúdo da conversa
    }

    [Fact]
    public async Task O_simulador_nao_atende_token_de_usuario_nem_requisicao_sem_contexto_e_valida_a_entrada()
    {
        var a = await MontarAsync("simseguro");
        var slug = a.Cenario.Tenant.Slug;
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoComSimulador());
        var token = await api.LoginAsync(a.AdminDoTenant);
        var corpo = new { contactRef = "x", organizationId = a.Cenario.OrganizacaoId, text = "forjada" };

        using var comToken = api.RequisicaoDe(token, HttpMethod.Post, "/simulator/inbound", JsonContent.Create(corpo));
        using var respostaComToken = await api.Client.SendAsync(comToken);
        using var semContexto = api.RequisicaoDe(null, HttpMethod.Post, "/simulator/inbound", JsonContent.Create(corpo));
        using var respostaSemContexto = await api.Client.SendAsync(semContexto);
        using var invalida = api.Requisicao(HttpMethod.Post, "/simulator/inbound", slug, JsonContent.Create(new { contactRef = "", organizationId = a.Cenario.OrganizacaoId, text = "x" }));
        using var respostaInvalida = await api.Client.SendAsync(invalida);
        using var deOutraOrganizacao = api.Requisicao(HttpMethod.Post, "/simulator/inbound", slug, JsonContent.Create(new { contactRef = "x", organizationId = Guid.NewGuid(), text = "x" }));
        using var respostaOrganizacao = await api.Client.SendAsync(deOutraOrganizacao);

        Assert.Equal(HttpStatusCode.Forbidden, respostaComToken.StatusCode); // SystemOperation nunca é de usuário
        Assert.Equal(HttpStatusCode.Unauthorized, respostaSemContexto.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, respostaInvalida.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, respostaOrganizacao.StatusCode);
        Assert.All(new[] { respostaComToken, respostaSemContexto, respostaInvalida, respostaOrganizacao }, r => Assert.Equal("true", Assert.Single(r.Headers.GetValues("X-Simulator"))));
    }

    [Fact]
    public async Task O_simulador_nao_existe_sem_ser_ligado_em_Development_e_a_partida_falha_se_ligado_fora_dele()
    {
        // O appsettings.Development.json do host liga o simulador por padrão (uso local): aqui ele é desligado de forma explícita.
        var desligado = ApiServer.ConfiguracaoDeDesenvolvimento();
        desligado["Tenancy:Development:SimulatorEnabled"] = "false";
        await using (var semSimulador = await ApiServer.StartAsync("Development", desligado))
        {
            Assert.Equal(HttpStatusCode.NotFound, await semSimulador.StatusAsync(null, HttpMethod.Post, "/simulator/inbound", new { }));
        }

        await using (var producao = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao()))
        {
            Assert.Equal(HttpStatusCode.NotFound, await producao.StatusAsync(null, HttpMethod.Post, "/simulator/inbound", new { }));
            Assert.Equal(HttpStatusCode.NotFound, await producao.StatusAsync(null, HttpMethod.Get, "/simulator/events"));
        }

        foreach (var ambiente in new[] { "Production", "Staging" })
        {
            var configuracao = ApiServer.ConfiguracaoDeProducao();
            configuracao["Tenancy:Development:SimulatorEnabled"] = "true";
            var falha = Assert.Throws<InvalidOperationException>(() => ApiHost.Build(
                new[] { "--environment=" + ambiente },
                builder => builder.Configuration.AddInMemoryCollection(configuracao)));
            Assert.Contains("Development", falha.Message);
        }
    }
}
