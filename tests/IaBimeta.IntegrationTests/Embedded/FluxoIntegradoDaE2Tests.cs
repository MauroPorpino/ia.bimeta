using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Agents.Domain;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using IaBimeta.Identity.Domain;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// Verificação funcional INTEGRADA da E2 (E2.6): um único percurso, com dados sintéticos e os recursos existentes (API real, ingestão, Agents com o provedor de IA dublê, Audit, sonda de status), que atravessa os nove passos
/// pedidos: autenticação do sistema externo, abertura do atendimento, vínculo, histórico autorizado, Standalone x Embedded, consentimento, comando de revogação, supressão da resposta automática e auditoria com consulta operacional.
/// Não cria interface nem funcionalidade: só encadeia o que as etapas E2.1 a E2.5 já entregaram.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FluxoIntegradoDaE2Tests(PostgresFixture banco)
{
    private const string FoneA = "5511999990909";
    private const string FoneB = "5511999991010";

    private static async Task<long> EscalarAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor);
        }

        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }

    private static async Task<List<string>> TrilhaAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("select operation from audit.audit_events where tenant_id = @t and (operation like 'consent.%' or operation like 'embed.customer.%') order by occurred_at, id", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        var lista = new List<string>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            lista.Add(leitor.GetString(0));
        }

        return lista;
    }

    private static async Task<JsonElement> JsonAsync(ApiServer api, string token, string caminho)
    {
        using var r = await api.EnviarAsync(token, HttpMethod.Get, caminho);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static List<Guid> Ids(JsonElement lista) => lista.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Percurso_integrado_da_E2_do_sistema_externo_ate_a_revogacao_com_supressao_e_auditoria()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "e26-fluxo");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Posso ajudar com mais alguma coisa?"));
        var c = a.C;
        var tenant = a.Tenant.TenantId.Value;
        var orgA = c.OrganizacaoId;
        var orgB = (await banco.CriarOrganizacaoAsync(c.Tenant, "Organização B e26")).Id;
        var admin = await banco.CriarAdministradorDoTenantAsync(c.Tenant);
        await using var api = await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao());
        var tokenAdmin = await api.LoginAsync(admin);

        // ---- Passo 1: autenticação do sistema externo (HMAC): chave errada é 401; a válida emite o ticket e o iframe troca por sessão embutida.
        var conexao = await api.CriarAsync<ConexaoDto>(tokenAdmin, "/embedded/connections", new
        {
            organizationId = orgA, name = "Sistema externo", mode = "FullAttendant", provisioning = "JitAttendant", allowedOrigins = new[] { EmbeddedTestSupport.Origem }, allowStartConversation = false,
        });
        var chave = await api.CriarAsync<ChaveCriadaDto>(tokenAdmin, $"/embedded/connections/{conexao.Id}/keys", new { });
        var erp = new ErpAssinante(api, chave.KeyId, chave.Secret);
        using (var recusada = await erp.EnviarAsync("/embed/v1/customer-links", new { customerRef = "CLI-100", phone = "+" + FoneA }, segredoUsado: "ibsk_errado", metodo: HttpMethod.Put))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, recusada.StatusCode);
        }

        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await erp.EmitirAsync("u-e26", EmbeddedTestSupport.Challenge(verifier), cliente: "CLI-100");
        var embutido = await EmbeddedTestSupport.TrocarAsync(api, ticket.Ticket, verifier);
        Assert.Equal(("FullAttendant", orgA), (embutido.Mode, embutido.OrganizationId));

        // ---- Passo 3: abertura do atendimento (o cliente escreve; a IA responde; o item entra na fila de A). Também um cliente na organização B.
        var filaA = (await c.CriarFilaAsync("Fila A", organizacao: orgA)).Id;
        var filaB = (await c.CriarFilaAsync("Fila B", organizacao: orgB)).Id;
        var unidadeB = await c.CriarUnidadeAsync(orgB, "Matriz B", matriz: true);
        var canalB = await c.CriarCanalAsync(new[] { unidadeB }, unidadeB, organizacao: orgB);
        var entrada = await c.EntrarAsync(FoneA, "Preciso de ajuda com o meu exame");
        // A IA responde ANTES de a conversa ir para a fila humana (na fila, a IA já não é o responsável).
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(5));
        await a.ExecutarAsync();
        await a.RelayAsync();
        var itemA = (await c.NaFilaAsync(entrada.SessionId, filaA)).Id;
        var sessaoB = (await c.EntrarAsync(FoneB, "Pergunta em B", organizacao: orgB, unidade: unidadeB, canal: canalB)).SessionId;
        var itemB = (await c.NaFilaAsync(sessaoB, filaB)).Id;
        await c.AdicionarAtendenteAsync(filaA, embutido.User.Id);
        var turnosAntes = (await a.TurnosAsync(entrada.SessionId)).Count;
        var chamadasAntes = a.ChamadasAoModelo;
        var respostasAntes = (await a.RespostasDaIAAsync(entrada.SessionId)).Count;
        Assert.True(respostasAntes >= 1, "A IA deveria ter respondido à primeira mensagem.");

        // ---- Passo 2: vínculo do cliente do sistema externo com o contato (resposta uniforme: resolved true).
        using (var vinculo = await erp.EnviarAsync("/embed/v1/customer-links", new { customerRef = "CLI-100", phone = "+" + FoneA }, metodo: HttpMethod.Put))
        {
            Assert.Equal(HttpStatusCode.OK, vinculo.StatusCode);
            Assert.True((await vinculo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resolved").GetBoolean());
        }

        // ---- Passo 4: histórico autorizado (só o cliente autorizado na sessão, só o contato vinculado). Cliente não autorizado é 404.
        var historico = await JsonAsync(api, embutido.Token, "/contact-center/customers/CLI-100/history");
        Assert.Equal(new[] { itemA }, historico.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("itemId").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(embutido.Token, HttpMethod.Get, "/contact-center/customers/CLI-999/history"));

        // ---- Passo 5: o MESMO usuário interno, gestor das organizações A e B, no Standalone e no Embedded.
        var gestor = await banco.CriarUsuarioAsync(c.Tenant, orgA, Role.Manager);
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(tokenAdmin, HttpMethod.Put, $"/organizations/{orgB}/users/{gestor.Id}/role", new { role = "Manager" }));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(tokenAdmin, HttpMethod.Put, $"/embedded/connections/{conexao.Id}/identities", new { externalUserId = "gestor-e26", userId = gestor.Id }));
        var standalone = await api.LoginAsync(gestor);
        var vg = EmbeddedTestSupport.NovoVerifier();
        var gestorEmbutido = await EmbeddedTestSupport.TrocarAsync(api, (await erp.EmitirAsync("gestor-e26", EmbeddedTestSupport.Challenge(vg), cliente: "CLI-100")).Ticket, vg);
        Assert.Equal(gestor.Id, gestorEmbutido.User.Id); // uma identidade interna única
        var noStandalone = Ids(await JsonAsync(api, standalone, "/contact-center/items"));
        var noEmbutido = Ids(await JsonAsync(api, gestorEmbutido.Token, "/contact-center/items"));
        Assert.Contains(itemA, noStandalone);
        Assert.Contains(itemB, noStandalone); // concessões amplas no Standalone
        Assert.Contains(itemA, noEmbutido);
        Assert.DoesNotContain(itemB, noEmbutido); // o embutido fica na organização da conexão
        // O mesmo dado nas duas modalidades (o item de A), e o consentimento é rota só do Standalone (403 no embutido).
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(standalone, HttpMethod.Get, $"/contact-center/items/{itemA}"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(gestorEmbutido.Token, HttpMethod.Get, $"/contact-center/items/{itemA}"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestorEmbutido.Token, HttpMethod.Get, $"/contact-center/items/{itemA}/consents"));

        // ---- Passo 6: o sistema externo registra o consentimento; o gestor o consulta (origem distinta da prova do titular).
        using (var registro = await erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", new
        {
            phone = "+" + FoneA, purpose = "Utility", status = "Granted", evidence = "Aceite no cadastro (formulário sintético 1)", collectedAt = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        }, metodo: HttpMethod.Put))
        {
            Assert.Equal(HttpStatusCode.OK, registro.StatusCode);
            Assert.True((await registro.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetBoolean());
        }

        var antes = await JsonAsync(api, standalone, $"/contact-center/items/{itemA}/consents");
        var efetivoAntes = Assert.Single(antes.GetProperty("effective").EnumerateArray());
        Assert.Equal(("Utility", "Granted", "IntegrationAsserted"), (efetivoAntes.GetProperty("purpose").GetString(), efetivoAntes.GetProperty("status").GetString(), efetivoAntes.GetProperty("origin").GetString()));

        // ---- Passo 7: o cliente manda PARAR (depois de uma resposta da IA: PARAR é inequívoco). Revoga todas as finalidades.
        var parar = await c.EntrarAsync(FoneA, "PARAR");
        Assert.Equal(IngestOutcome.Registered, parar.Outcome);
        var depois = await JsonAsync(api, standalone, $"/contact-center/items/{itemA}/consents");
        var efetivas = depois.GetProperty("effective").EnumerateArray().ToDictionary(e => e.GetProperty("purpose").GetString()!, e => (e.GetProperty("status").GetString(), e.GetProperty("origin").GetString()));
        Assert.Equal(("Revoked", "CustomerKeyword"), efetivas["Utility"]); // a revogação do titular prevalece sobre a afirmação do sistema externo
        Assert.Equal(("Revoked", "CustomerKeyword"), efetivas["Marketing"]);

        // ---- Passo 8: supressão da resposta automática: nenhum turno novo, nenhuma chamada ao modelo, nenhuma resposta da IA nova.
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(5));
        await a.ExecutarAsync();
        await a.RelayAsync();
        Assert.Equal((turnosAntes, chamadasAntes, respostasAntes), ((await a.TurnosAsync(entrada.SessionId)).Count, a.ChamadasAoModelo, (await a.RespostasDaIAAsync(entrada.SessionId)).Count));
        Assert.Contains((await c.HistoricoAsync(entrada.SessionId))!.Items, m => m.Id == parar.MessageId && m.Text == "PARAR"); // a mensagem segue visível ao atendimento

        // ---- Passo 9: auditoria entregue ao Audit (o que o relay do Worker faz) e consulta operacional (a sonda do comando status).
        await using (var escopo = api.Services.CreateAsyncScope())
        {
            escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(new TenantId(tenant), "messaging-relay"));
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            var handler = escopo.ServiceProvider.GetServices<IEventHandler>().Single(h => h.EventType == ConversationAuditEvents.AuditRequested);
            var linhas = await db.Set<OutboxMessage>().Where(o => o.TenantId == tenant && o.EventType == ConversationAuditEvents.AuditRequested && o.State == OutboxState.Pending).OrderBy(o => o.IngestSeq).ToListAsync();
            foreach (var linha in linhas)
            {
                var envelope = new EventEnvelope(linha.Id, linha.EventType, linha.EventVersion, new TenantId(tenant), linha.CorrelationId, linha.CausationId, linha.OrderingKey, linha.OccurredAt, MessagingJson.Deserialize(linha.Payload, handler.PayloadType));
                Assert.Equal(HandlerOutcome.Success, (await handler.HandleAsync(envelope, default)).Outcome);
                linha.State = OutboxState.Processed;
            }

            await db.SaveChangesAsync();
        }

        // O conjunto exato (a ordem entre o servidor da API e a ingestão do teste depende de relógios diferentes do ambiente de teste).
        Assert.Equal(new[] { "consent.keyword-revoke", "consent.keyword-revoke", "consent.record", "embed.customer.link" }, (await TrilhaAsync(tenant)).Order().ToArray());

        var fabrica = await c.ComoAsync(c.ContextoDeSistema, sp => Task.FromResult(sp.GetRequiredService<IServiceScopeFactory>()));
        var sonda = (IHealthProbe)Activator.CreateInstance(typeof(ConversationsDbContext).Assembly.GetType("IaBimeta.Conversations.Infrastructure.ConversationsHealthProbe")!, fabrica)!;
        var achado = (await sonda.CheckAsync(new HealthContext(new HealthThresholds(), DateTimeOffset.UtcNow, tenant))).Single();
        Assert.Equal(HealthSeverity.Ok, achado.Severity); // comando confirmado não deixa pendência
        Assert.Equal(0, await EscalarAsync("select count(*) from conversations.optout_reviews where tenant_id = @t", ("t", tenant)));
    }
}
