using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A rotina de DIAGNÓSTICO operacional do envio, por HTTP de verdade (Api e Worker reais, PostgreSQL real, Graph API simulada): contas pausadas, mensagens paradas, falhas definitivas, envios de resultado incerto e
/// eventos de entrada mortos, só dos números que o usuário gere. Só leitura (a única escrita é reenfileirar uma entrada morta, auditada), nunca traz texto de mensagem nem credencial, e nunca cruza tenants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DiagnosticoDeEnvioTests(PostgresFixture banco)
{
    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Responde(string texto) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder(texto), 150, 0, 25, "req_diag"));

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo);
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta do ensaio de diagnóstico."));
        return e;
    }

    private static async Task<Guid> ClienteAsync(EnsaioDeWhatsApp e, string nome, string waId, string idDaMensagem)
    {
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto(idDaMensagem, waId, nome, "Olá, preciso de ajuda.")));
        return await e.EsperarSessaoAsync(nome);
    }

    private static async Task<JsonNode> DiagnosticoAsync(EnsaioDeWhatsApp e, string token, string consulta = "")
    {
        var (status, corpo) = await e.ChamarAsync(token, HttpMethod.Get, "/channels/diagnostics/outbound" + consulta);
        Assert.Equal(HttpStatusCode.OK, status);
        return corpo!;
    }

    private static JsonNode? Problema(JsonNode diagnostico, string problema, Guid? mensagem = null) =>
        diagnostico["messages"]!.AsArray().FirstOrDefault(m => m!["problem"]!.GetValue<string>() == problema && (mensagem is null || m["messageId"]!.GetValue<Guid>() == mensagem));

    private static async Task<Guid> MensagemDaIAAsync(EnsaioDeWhatsApp e, Guid sessao) =>
        (await e.Ia.MensagensAsync(sessao)).First(m => m["author"]!.GetValue<string>() == "Ai")["id"]!.GetValue<Guid>();

    [Fact]
    public async Task O_diagnostico_aponta_conta_pausada_falha_envio_incerto_mensagem_parada_e_entrada_morta_e_o_reenfileirar_atua_so_no_morto_do_proprio_tenant()
    {
        await using var e = await IniciarAsync("diagenvio");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);

        // Sistema saudável: nada a apontar.
        var limpo = await DiagnosticoAsync(e, admin);
        Assert.True(limpo["healthy"]!.GetValue<bool>());
        Assert.Empty(limpo["accounts"]!.AsArray());
        Assert.Empty(limpo["messages"]!.AsArray());
        Assert.Empty(limpo["deadInboundEvents"]!.AsArray());
        Assert.Equal(0, limpo["counts"]!["failed"]!.GetValue<int>());

        // Conta pausada: a mensagem falha na hora (account-paused), e o diagnóstico aponta a conta e a falha.
        Assert.Equal(HttpStatusCode.OK, (await e.ChamarAsync(admin, HttpMethod.Post, $"/channels/accounts/{e.ContaId}/sending/pause")).Status);
        var pausada = await ClienteAsync(e, "Diag Pausa", "5511999990081", "wamid.DIAG0001");
        await e.EsperarEntregaAsync(pausada, "Failed");
        var idDaPausada = await MensagemDaIAAsync(e, pausada);

        var comPausa = await DiagnosticoAsync(e, admin);
        Assert.False(comPausa["healthy"]!.GetValue<bool>());
        var conta = Assert.Single(comPausa["accounts"]!.AsArray())!;
        Assert.Equal((e.ContaId, true, "manual"), (conta["accountId"]!.GetValue<Guid>(), conta["paused"]!.GetValue<bool>(), conta["pauseReason"]!.GetValue<string>()));
        var falha = Problema(comPausa, "failed", idDaPausada)!;
        Assert.Equal("account-paused", falha["errorCode"]!.GetValue<string>());
        Assert.Equal(1, comPausa["counts"]!["failed"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, (await e.ChamarAsync(admin, HttpMethod.Post, $"/channels/accounts/{e.ContaId}/sending/resume")).Status);

        // Envio de resultado incerto: aparece como send-unknown, mas NÃO torna o sistema doente (é um desfecho que alguém precisa ver, não um defeito).
        e.Graph.Roteiro = _ => (500, "{}");
        var incerta = await ClienteAsync(e, "Diag Incerto", "5511999990082", "wamid.DIAG0002");
        await e.EsperarEntregaAsync(incerta, "SendUnknown");
        var idDaIncerta = await MensagemDaIAAsync(e, incerta);
        var comIncerto = await DiagnosticoAsync(e, admin);
        Assert.True(comIncerto["healthy"]!.GetValue<bool>());
        Assert.NotNull(Problema(comIncerto, "send-unknown", idDaIncerta));
        Assert.Empty(comIncerto["accounts"]!.AsArray());
        Assert.Equal(1, comIncerto["counts"]!["sendUnknown"]!.GetValue<int>());

        // Mensagem PARADA: o Conversations a liberou para envio há mais de 30 s e o Sender nunca a viu (evento pendente ou morto no relay).
        e.Graph.Roteiro = _ => e.Graph.Aceita();
        var parada = await ClienteAsync(e, "Diag Parada", "5511999990083", "wamid.DIAG0003");
        await e.EsperarEntregaAsync(parada, "Sent");
        var idDaParada = await MensagemDaIAAsync(e, parada);
        await e.Ia.ComoSistemaAsync(async sp =>
        {
            var canais = sp.GetRequiredService<ChannelsDbContext>();
            await canais.Database.ExecuteSqlRawAsync("DELETE FROM channels.outbound_messages WHERE id = {0}", idDaParada);
            var conversas = sp.GetRequiredService<IaBimeta.Conversations.Infrastructure.ConversationsDbContext>();
            await conversas.Database.ExecuteSqlRawAsync("UPDATE conversations.messages SET delivery_state = 1, created_at = now() - interval '1 hour' WHERE id = {0}", idDaParada);
            return 0;
        });
        var comParada = await DiagnosticoAsync(e, admin);
        Assert.False(comParada["healthy"]!.GetValue<bool>());
        var apontada = Problema(comParada, "stale-before-sender", idDaParada)!;
        Assert.Equal(parada, apontada["sessionId"]!.GetValue<Guid>());
        Assert.Null(apontada["senderState"]);

        // O diagnóstico nunca traz texto de mensagem nem credencial.
        var texto = comParada.ToJsonString();
        Assert.DoesNotContain("Resposta do ensaio de diagnóstico", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("Olá, preciso de ajuda", texto, StringComparison.Ordinal);
        Assert.DoesNotContain(EnsaioDeWhatsApp.TokenDeAcesso, texto, StringComparison.Ordinal);
        Assert.DoesNotContain(EnsaioDeWhatsApp.SegredoDoApp, texto, StringComparison.Ordinal);

        // Entrada MORTA (a mensagem do cliente que não chegou ao Conversations): um evento de tipo sem consumidor, para o reenfileirar ser determinístico.
        var tenant = await e.Ia.ComoSistemaAsync(sp => Task.FromResult(sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value));
        var morto = Guid.NewGuid();
        await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelsDbContext>().Database.ExecuteSqlRawAsync(
            "INSERT INTO channels.outbox_message (tenant_id, id, ordering_key, event_type, event_version, correlation_id, occurred_at, payload, state, attempts, next_attempt_at, created_at, dead_reason) " +
            "VALUES ({0}, {1}, {2}, 'teste.entrada-morta', 1, {1}, now(), '{{}}'::jsonb, 4, 8, now(), now(), 'max-attempts:teste')",
            tenant, morto, $"{e.InstanciaId:N}:5511999990084"));
        var comMorto = await DiagnosticoAsync(e, admin);
        Assert.False(comMorto["healthy"]!.GetValue<bool>());
        var entrada = Assert.Single(comMorto["deadInboundEvents"]!.AsArray(), d => d!["eventId"]!.GetValue<Guid>() == morto)!;
        Assert.Equal("max-attempts:teste", entrada["deadReason"]!.GetValue<string>());

        // Outro tenant: nada do primeiro aparece e o reenfileirar do evento dele responde como inexistente.
        var outro = await banco.RegistrarTenantAsync("diagoutro");
        var organizacaoDoOutro = await banco.CriarOrganizacaoAsync(outro, "Outra organização");
        var tokenDoOutro = await e.Ia.Api.LoginAsync(await banco.CriarUsuarioAsync(outro, organizacaoDoOutro.Id, Role.OrganizationAdmin));
        var doOutro = await DiagnosticoAsync(e, tokenDoOutro);
        Assert.True(doOutro["healthy"]!.GetValue<bool>());
        Assert.Empty(doOutro["messages"]!.AsArray());
        Assert.Empty(doOutro["deadInboundEvents"]!.AsArray());
        Assert.Empty(doOutro["accounts"]!.AsArray());
        Assert.Equal(0, doOutro["counts"]!["sent"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(tokenDoOutro, HttpMethod.Post, $"/channels/diagnostics/dead-events/{morto}/requeue")).Status);

        // Quem só lê canais não pede o diagnóstico nem reenfileira (403); sem sessão, 401; evento inexistente, 404.
        var gestor = await e.TokenDeAsync(Role.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.ChamarAsync(gestor, HttpMethod.Get, "/channels/diagnostics/outbound")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.ChamarAsync(gestor, HttpMethod.Post, $"/channels/diagnostics/dead-events/{morto}/requeue")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.ChamarAsync(string.Empty, HttpMethod.Get, "/channels/diagnostics/outbound")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(admin, HttpMethod.Post, $"/channels/diagnostics/dead-events/{Guid.NewGuid()}/requeue")).Status);

        // O administrador do tenant reenfileira a entrada morta: volta a Pendente, o relay a conclui (sem consumidor) e ela sai do diagnóstico.
        Assert.Equal(HttpStatusCode.NoContent, (await e.ChamarAsync(admin, HttpMethod.Post, $"/channels/diagnostics/dead-events/{morto}/requeue")).Status);
        await e.EsperarAsync(
            async () => !(await DiagnosticoAsync(e, admin))["deadInboundEvents"]!.AsArray().Any(d => d!["eventId"]!.GetValue<Guid>() == morto),
            "a entrada reenfileirada deveria sair dos mortos");
        Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(admin, HttpMethod.Post, $"/channels/diagnostics/dead-events/{morto}/requeue")).Status); // já não está morta
    }

    [Fact]
    public async Task Os_parametros_do_diagnostico_sao_limitados_e_a_janela_filtra_o_que_e_antigo()
    {
        await using var e = await IniciarAsync("diagjanela");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        e.Graph.Roteiro = _ => ServidorGraphLoopback.Erro(400, 131047); // janela de 24 h fechada: falha definitiva
        var sessao = await ClienteAsync(e, "Diag Janela", "5511999990091", "wamid.DIAG0101");
        await e.EsperarEntregaAsync(sessao, "Failed");

        // Valores fora dos limites são ajustados (nunca erro, nunca consulta sem limite).
        var ajustado = await DiagnosticoAsync(e, admin, "?hours=100000&staleMinutes=0&take=100000");
        Assert.Equal(168, ajustado["windowHours"]!.GetValue<int>());
        Assert.Equal(30, ajustado["staleAfterSeconds"]!.GetValue<int>());
        Assert.Equal(1, ajustado["counts"]!["failed"]!.GetValue<int>());
        Assert.Single((await DiagnosticoAsync(e, admin, "?take=1"))["messages"]!.AsArray());

        // Uma falha de dois dias atrás sai da janela de 24 horas (mas continua na de 168).
        await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelsDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE channels.outbound_messages SET created_at = now() - interval '2 days', updated_at = now() - interval '2 days' WHERE tenant_id = {0}",
            sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value));
        var curta = await DiagnosticoAsync(e, admin, "?hours=24");
        Assert.Equal(0, curta["counts"]!["failed"]!.GetValue<int>());
        Assert.Empty(curta["messages"]!.AsArray());
        Assert.Equal(1, (await DiagnosticoAsync(e, admin, "?hours=168"))["counts"]!["failed"]!.GetValue<int>());
    }
}
