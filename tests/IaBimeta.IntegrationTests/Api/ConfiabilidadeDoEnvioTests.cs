using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A CONFIABILIDADE do envio: o MVP não pode perder uma mensagem em silêncio. Api e Worker reais, PostgreSQL real, o adaptador real do WhatsApp contra a Graph API simulada (HTTP de verdade) e a OpenAI simulada.
/// Provam: (1) o evento de envio que MORRE no relay (tentativas esgotadas) vira falha visível ao atendente, uma vez, sem nunca chamar a Meta nem duplicar; (2) um Worker que cai NO MEIO da chamada deixa
/// <c>SendUnknown</c> e a mensagem nunca é reenviada; (3) uma espera longa por vazão NÃO gasta tentativa do relay (não leva aos mortos) e a que passa da validade falha de forma visível. Nenhuma credencial é real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConfiabilidadeDoEnvioTests(PostgresFixture banco)
{
    private const string Cliente = "5511999990061";

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Responde(string texto) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder(texto), 150, 0, 25, "req_conf"));

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere(string motivo) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir(motivo), 150, 0, 15, "req_conf"));

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, IReadOnlyDictionary<string, string?>? configuracao = null)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, configuracao);
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    /// <summary>As linhas do outbox do Conversations do tenant do ensaio (os eventos de envio e de falha).</summary>
    private static Task<List<OutboxMessage>> EventosAsync(EnsaioDeWhatsApp e, string? tipo = null) =>
        e.Ia.ComoSistemaAsync(async sp =>
        {
            var tenant = sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value;
            var consulta = sp.GetRequiredService<ConversationsDbContext>().Set<OutboxMessage>().AsNoTracking().Where(o => o.TenantId == tenant);
            if (tipo is not null)
            {
                consulta = consulta.Where(o => o.EventType == tipo);
            }

            return await consulta.OrderBy(o => o.IngestSeq).ToListAsync();
        });

    private static Task<int> ExecutarNoCanaisAsync(EnsaioDeWhatsApp e, string sql) =>
        e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelsDbContext>().Database.ExecuteSqlRawAsync(sql));

    private static string Lance(JsonNode? item, string campo) => item![campo]!.ToJsonString().Trim('"');

    // O evento de envio que morre no relay

    [Fact]
    public async Task Evento_de_envio_que_esgota_as_tentativas_vira_falha_visivel_ao_atendente_uma_vez_sem_chamar_a_Meta_e_a_recuperacao_e_uma_nova_resposta()
    {
        // Duas tentativas e espera curta: a falha INDUZIDA (um gatilho do banco que recusa a gravação do despacho deste tenant) esgota as tentativas em poucos segundos.
        await using var e = await IniciarAsync("confmorto", new Dictionary<string, string?>
        {
            ["Messaging:MaxAttempts"] = "2",
            ["Messaging:BaseBackoff"] = "00:00:00.300",
            ["Messaging:MaxBackoff"] = "00:00:00.600",
            ["Messaging:DeadLetterSweepInterval"] = "00:00:01",
        });
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere("price"));
        var (_, token) = await e.NovoAtendenteAsync();
        var tenant = await e.Ia.ComoSistemaAsync(sp => Task.FromResult(sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value));
        var gatilho = $"trg_conf_{tenant:N}";

        await ExecutarNoCanaisAsync(e, "CREATE OR REPLACE FUNCTION channels.falha_induzida_pelo_teste() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'falha induzida pelo teste'; END; $$ LANGUAGE plpgsql");
        await ExecutarNoCanaisAsync(e, $"CREATE TRIGGER {gatilho} BEFORE INSERT ON channels.outbound_messages FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}') EXECUTE FUNCTION channels.falha_induzida_pelo_teste()");
        try
        {
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.CONF0001", Cliente, "Morto", "Quanto custa o hemograma?")));
            var sessao = await e.EsperarSessaoAsync("Morto");

            // O aviso de transferência (a mensagem de saída) esgota as tentativas e vai para os mortos: o relay NÃO o perde em silêncio, e a Meta nunca foi chamada.
            await e.EsperarAsync(
                async () => (await EventosAsync(e, "conversations.outbound-message-requested")).Any(o => o.State == OutboxState.Dead),
                "o evento de envio deveria esgotar as tentativas e ir para os mortos");
            Assert.Empty(e.Graph.Capturas);
        }
        finally
        {
            await ExecutarNoCanaisAsync(e, $"DROP TRIGGER IF EXISTS {gatilho} ON channels.outbound_messages");
        }

        var sessaoDoMorto = await e.EsperarSessaoAsync("Morto");

        // O tratamento do morto é repetido até concluir (falhou enquanto o gatilho existia): agora a falha final é gravada, informada ao Conversations e mostrada. Varreduras em paralelo (outros relays) não duplicam nada.
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.WhenAll(e.Ia.Worker.Services.GetServices<IOutboxRelay>().OfType<OutboxRelay<ConversationsDbContext>>().Select(r => r.SweepDeadLettersAsync()))));
        await e.EsperarEntregaAsync(sessaoDoMorto, "Failed");

        Assert.Empty(e.Graph.Capturas); // nada foi enviado, nem na recuperação: a falha é afirmada porque nenhuma chamada saiu
        var falhas = await EventosAsync(e, "conversations.outbound-message-failed");
        Assert.Single(falhas); // uma única notificação de falha, mesmo com varreduras simultâneas

        // O ContactCenter sinaliza o atendimento: o item na fila mostra a falha de envio.
        string? itemId = null;
        await e.EsperarAsync(
            async () =>
            {
                var itens = await e.Ia.Api.LerAsync<JsonNode>(token, "/contact-center/items?status=Waiting&take=20");
                var item = itens.AsArray().FirstOrDefault(i => i!["sessionId"]!.GetValue<Guid>() == sessaoDoMorto);
                itemId = item?["id"]?.GetValue<string>();
                return item is not null && item["deliveryFailures"]!.GetValue<int>() == 1 && item["lastDeliveryFailureAt"] is not null;
            },
            "o item da fila deveria sinalizar a falha de envio");

        // A recuperação é uma NOVA resposta do atendente (a falha não é reenviada sozinha): sai uma vez e o sinal some da fila.
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/claim", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = Guid.NewGuid(), text = "Desculpe a falha. O hemograma custa o valor da tabela." }));
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "a resposta do atendente deveria sair pelo WhatsApp");
        Assert.Equal("Desculpe a falha. O hemograma custa o valor da tabela.", e.Graph.Capturas[0].Texto);
        await e.EsperarAsync(
            async () => (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}"))["deliveryFailures"]!.GetValue<int>() == 0,
            "a falha deveria sair da fila depois da nova resposta");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Single(e.Graph.Capturas);
        Assert.Single(await EventosAsync(e, "conversations.outbound-message-failed")); // nenhuma nova notificação
    }

    // O Worker que cai no meio da chamada

    [Fact]
    public async Task Worker_que_cai_no_meio_da_chamada_deixa_o_resultado_incerto_e_a_mensagem_nunca_e_reenviada()
    {
        await using var e = await IniciarAsync("confreinicio", new Dictionary<string, string?>
        {
            ["Messaging:LeaseDuration"] = "00:00:03",
            ["Channels:Providers:WhatsApp:RequestTimeout"] = "00:01:00",
        });
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta que fica no meio da chamada."));

        // A Meta recebe a chamada e nunca responde (o processo vai cair com ela em andamento).
        e.Graph.Antes = (_, cancelamento) => Task.Delay(Timeout.Infinite, cancelamento);
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.CONF0101", Cliente, "Reinicio", "Oi")));
        var sessao = await e.EsperarSessaoAsync("Reinicio");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "a chamada deveria chegar à Meta e ficar em andamento");

        // O despacho foi gravado ANTES da chamada: em andamento, com a tentativa já contada.
        var antes = await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelsDbContext>().OutboundMessages.AsNoTracking().SingleAsync());
        Assert.Equal((IaBimeta.Channels.Domain.OutboundState.Dispatching, 1), (antes.State, antes.Attempts));

        // O processo cai (e sobe de novo): a chamada em andamento é cortada e nada conclui.
        e.Graph.Antes = null;
        e.Graph.Roteiro = _ => e.Graph.Aceita(); // se o novo Worker (erradamente) reenviasse, a Meta aceitaria e a mensagem sairia DUAS vezes
        await e.Ia.ReiniciarWorkerAsync();

        // O novo Worker acha o lease vencido e o despacho em andamento: a Meta PODE ter recebido, então o desfecho é incerto, nunca falha afirmada e nunca reenvio.
        await e.EsperarEntregaAsync(sessao, "SendUnknown", segundos: 40);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Single(e.Graph.Capturas);
        Assert.Equal("SendUnknown", await e.EstadoDeEntregaAsync(sessao, "Ai"));
        var depois = await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelsDbContext>().OutboundMessages.AsNoTracking().SingleAsync());
        Assert.Equal((IaBimeta.Channels.Domain.OutboundState.SendUnknown, "interrupted", true), (depois.State, depois.ErrorCode, depois.Notified));
        Assert.Empty(await EventosAsync(e, "conversations.outbound-message-failed")); // incerto não é falha definitiva
    }

    // A espera longa por vazão

    [Fact]
    public async Task Espera_longa_por_vazao_nao_gasta_tentativa_do_relay_todas_as_mensagens_saem_uma_vez_e_nenhuma_vira_incerta()
    {
        // Duas tentativas do relay e espera local curtíssima: no comportamento anterior (a espera longa devolvia Retry), a fila de espera mataria as mensagens (ou, pior, as marcaria como incertas).
        await using var e = await IniciarAsync("confdefer", new Dictionary<string, string?>
        {
            ["Messaging:MaxAttempts"] = "2",
            ["Channels:Sending:MaxLocalWait"] = "00:00:00.200",
            ["Channels:Sending:DefaultThroughputPerSecond"] = "2",
        });
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta depois da fila de vazão."));

        var sessoes = await Task.WhenAll(Enumerable.Range(1, 6).Select(async i =>
        {
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto($"wamid.CONF020{i}", $"551199999007{i}", $"Fila {i}", "Oi")));
            return await e.EsperarSessaoAsync($"Fila {i}");
        }));
        await Task.WhenAll(sessoes.Select(s => e.EsperarEntregaAsync(s, "Sent", segundos: 60)));

        Assert.Equal(6, e.Graph.Capturas.Count);
        Assert.Equal(6, e.Graph.Capturas.Select(c => c.Para).Distinct().Count()); // uma por cliente: nenhuma duplicada
        var despachos = await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelsDbContext>().OutboundMessages.AsNoTracking().ToListAsync());
        Assert.All(despachos, d => Assert.Equal(IaBimeta.Channels.Domain.OutboundState.Sent, d.State));

        // O relay não gastou tentativa nas esperas: todo evento de envio terminou processado, com uma entrega que contou, e nenhum foi aos mortos.
        var eventos = await EventosAsync(e, "conversations.outbound-message-requested");
        Assert.All(eventos, o => Assert.Equal((OutboxState.Processed, 1), (o.State, o.Attempts)));
        Assert.Empty(await EventosAsync(e, "conversations.outbound-message-failed"));
    }

    [Fact]
    public async Task Mensagem_que_esperou_alem_da_validade_operacional_falha_de_forma_visivel_sem_chamar_a_Meta()
    {
        await using var e = await IniciarAsync("confexpira", new Dictionary<string, string?>
        {
            ["Channels:Sending:MaxLocalWait"] = "00:00:00.200",
            ["Channels:Sending:MaxQueueAge"] = "00:00:30",
        });
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta que vai esperar demais."));

        // A Meta limita a conta e manda esperar um minuto: a próxima mensagem teria de esperar além da validade (30 s) e falha de forma visível, em vez de ficar parada.
        e.Graph.RoteiroComEspera = _ => (429, ServidorGraphLoopback.Erro(429, 130429).Corpo, 60);
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.CONF0301", Cliente, "Espera A", "Oi")));
        await e.EsperarSessaoAsync("Espera A");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "a primeira chamada deveria receber o limite de taxa");

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.CONF0302", "5511999990062", "Espera B", "Oi")));
        var b = await e.EsperarSessaoAsync("Espera B");

        await e.EsperarEntregaAsync(b, "Failed");
        Assert.Single(e.Graph.Capturas); // a mensagem B nunca chegou à Meta
        var despachoDeB = await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelsDbContext>().OutboundMessages.AsNoTracking().SingleAsync(d => d.ErrorCode == "expired"));
        Assert.True(despachoDeB.Notified);
    }
}
