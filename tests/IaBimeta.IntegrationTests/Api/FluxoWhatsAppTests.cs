using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using IaBimeta.Api.Development;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O fluxo operacional do MVP, de ponta a ponta e por HTTP de verdade: a "Meta" fala com o Receiver (assinatura verdadeira), o Worker processa o Inbox e entrega ao Conversations e ao Agents, a IA responde (a
/// OpenAI é um servidor local), o Sender envia pelo adaptador REAL do WhatsApp (a Graph API é um servidor local), os status de entrega voltam por webhook, a IA transfere para a fila e o atendente humano assume, responde
/// e encerra pela API. PostgreSQL, Api, Worker, AiGateway, Agents, ContactCenter e Channels são os de produção; só os dois provedores externos são simulados, e nenhuma credencial é real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FluxoWhatsAppTests(PostgresFixture banco)
{
    private const string Cliente = "5511999990001";

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Responde(string texto) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder(texto), 150, 0, 25, "req_wpp"));

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere(string motivo) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir(motivo), 150, 0, 15, "req_wpp"));

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo);
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    private static async Task<int> LinhasDoInboxAsync(EnsaioDeWhatsApp e) =>
        await e.Ia.ComoSistemaAsync(async sp =>
        {
            var tenant = sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value;
            return await sp.GetRequiredService<ChannelsDbContext>().Set<OutboxMessage>().AsNoTracking().CountAsync(m => m.TenantId == tenant);
        });

    // O Receiver

    [Fact]
    public async Task O_Receiver_so_aceita_webhook_assinado_responde_ao_desafio_e_nao_grava_nada_do_que_recusa()
    {
        await using var e = await IniciarAsync("wppreceiver");
        var corpo = e.PayloadDeTexto("wamid.ENTRADA9001", Cliente, "Recusado", "Esta mensagem não pode entrar");

        // Assinatura ausente, de outro segredo, de outro corpo ou de outro formato: 401, e nada é interpretado nem gravado.
        Assert.Equal(HttpStatusCode.Unauthorized, await e.EnviarWebhookAsync(corpo, semAssinatura: true));
        Assert.Equal(HttpStatusCode.Unauthorized, await e.EnviarWebhookAsync(corpo, EnsaioDeWhatsApp.Assinar(corpo, "outro-segredo")));
        Assert.Equal(HttpStatusCode.Unauthorized, await e.EnviarWebhookAsync(corpo, EnsaioDeWhatsApp.Assinar(e.PayloadDeTexto("wamid.OUTRO", Cliente, "Recusado", "Outro texto"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await e.EnviarWebhookAsync(corpo, EnsaioDeWhatsApp.Assinar(corpo).Replace("sha256=", "sha1=", StringComparison.Ordinal)));

        // Identificador da URL desconhecido: 404, sem detalhe (nem a assinatura é conferida).
        Assert.Equal(HttpStatusCode.NotFound, await e.EnviarWebhookAsync(corpo, webhookId: Guid.NewGuid()));

        // Assinatura válida, mas corpo que não é um webhook do provedor: 400.
        var lixo = System.Text.Encoding.UTF8.GetBytes("{\"nao\":\"e um webhook\"}");
        Assert.Equal(HttpStatusCode.BadRequest, await e.EnviarWebhookAsync(lixo));

        // Corpo acima do limite: 413 (nenhum byte além do limite é lido).
        var grande = new byte[1_048_576 + 10];
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await e.EnviarWebhookAsync(grande));

        Assert.Equal(0, await LinhasDoInboxAsync(e));

        // O pedido de verificação do cadastro do webhook: o desafio volta só com o token certo.
        using (var certo = await e.Ia.Api.Client.GetAsync($"/webhooks/whatsapp/{e.WebhookId}?hub.mode=subscribe&hub.verify_token={EnsaioDeWhatsApp.TokenDeVerificacao}&hub.challenge=desafio-123"))
        {
            Assert.Equal(HttpStatusCode.OK, certo.StatusCode);
            Assert.Equal("desafio-123", await certo.Content.ReadAsStringAsync());
        }

        using (var errado = await e.Ia.Api.Client.GetAsync($"/webhooks/whatsapp/{e.WebhookId}?hub.mode=subscribe&hub.verify_token=token-errado&hub.challenge=desafio-123"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, errado.StatusCode);
        }

        // Nenhum segredo no log, em nenhum dos recusados.
        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(EnsaioDeWhatsApp.SegredoDoApp) || l.Contains(EnsaioDeWhatsApp.TokenDeAcesso) || l.Contains(EnsaioDeWhatsApp.TokenDeVerificacao));
    }

    [Fact]
    public async Task O_mesmo_webhook_repetido_e_gravado_uma_vez_e_numero_de_outra_conta_ou_desconhecido_e_descartado()
    {
        await using var e = await IniciarAsync("wppdedup");
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Olá!"));
        var corpo = e.PayloadDeTexto("wamid.ENTRADA9100", Cliente, "Repetido", "Oi, tudo bem?");

        // A Meta reenvia o mesmo evento: cem repetições são uma linha (a chave é determinística, e o INSERT ... ON CONFLICT DO NOTHING a barra).
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(corpo));
        }

        Assert.Equal(1, await LinhasDoInboxAsync(e));
        var sessao = await e.EsperarSessaoAsync("Repetido");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "uma única resposta deveria sair");
        Assert.Single(await e.Ia.MensagensAsync(sessao), m => m["author"]!.GetValue<string>() == "Customer");

        // Um número que não é de nenhuma conta do tenant (o payload pode trazer qualquer identificador): 200, descartado, nada gravado.
        var antes = await LinhasDoInboxAsync(e);
        var alheio = e.PayloadDeTexto("wamid.ENTRADA9101", "5511999990002", "Alheio", "Mensagem para outro número", numero: "999000999000");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(alheio));
        Assert.Equal(antes, await LinhasDoInboxAsync(e));
    }

    // O fluxo da IA pelo WhatsApp

    [Fact]
    public async Task Mensagem_do_WhatsApp_a_IA_responde_a_resposta_sai_pelo_WhatsApp_e_os_status_de_entrega_voltam()
    {
        await using var e = await IniciarAsync("wppfluxo");
        const string resposta = "Em geral não é necessário jejum para o hemograma.";
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde(resposta));

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.ENTRADA0001", Cliente, "Mauro", "Preciso de jejum para o hemograma?")));
        var sessao = await e.EsperarSessaoAsync("Mauro");

        // A resposta da IA sai pelo adaptador real: o token da conta só no cabeçalho, o número de origem na URL, o destinatário e o texto no corpo.
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "a resposta da IA deveria sair pelo WhatsApp");
        var envio = Assert.Single(e.Graph.Capturas);
        Assert.Equal(EnsaioDeWhatsApp.TokenDeAcesso, envio.Credencial);
        Assert.EndsWith($"/{e.NumeroDoCanal}/messages", envio.Caminho);
        Assert.Equal((Cliente, resposta), (envio.Para, envio.Texto));
        Assert.DoesNotContain(EnsaioDeWhatsApp.TokenDeAcesso, envio.Corpo);
        Assert.Matches("^[0-9a-f]{32}$", envio.DadoOpaco);

        // O Conversations viu o envio aceito; depois, os status da Meta (entregue, lido) chegam por webhook, assinados, e só avançam.
        await e.EsperarAsync(async () => await e.EstadoDeEntregaAsync(sessao, "Ai") == "Sent", "a mensagem deveria ficar Enviada");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeStatus("wamid.SAIDA0001", "delivered", envio.DadoOpaco, Cliente)));
        await e.EsperarAsync(async () => await e.EstadoDeEntregaAsync(sessao, "Ai") == "Delivered", "a mensagem deveria ficar Entregue");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeStatus("wamid.SAIDA0001", "read", envio.DadoOpaco, Cliente)));
        await e.EsperarAsync(async () => await e.EstadoDeEntregaAsync(sessao, "Ai") == "Read", "a mensagem deveria ficar Lida");

        // Um status atrasado (entregue, depois de lido) nunca regride o estado.
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeStatus("wamid.SAIDA0001", "sent", envio.DadoOpaco, Cliente)));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal("Read", await e.EstadoDeEntregaAsync(sessao, "Ai"));

        // O histórico: o texto do cliente exatamente como veio, e o consumo da IA no ledger.
        var mensagens = await e.Ia.MensagensAsync(sessao);
        Assert.Equal("Preciso de jejum para o hemograma?", mensagens.First(m => m["author"]!.GetValue<string>() == "Customer")["text"]!.GetValue<string>());
        var linha = Assert.Single((await e.Ia.LedgerAsync()).AsArray())!;
        Assert.Equal(("Confirmed", 150, 25), (linha["state"]!.GetValue<string>(), linha["inputTokens"]!.GetValue<int>(), linha["outputTokens"]!.GetValue<int>()));

        // Nenhum segredo do canal em nenhum log do Api nem do Worker.
        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(EnsaioDeWhatsApp.SegredoDoApp) || l.Contains(EnsaioDeWhatsApp.TokenDeAcesso) || l.Contains(EnsaioDeWhatsApp.TokenDeVerificacao));
    }

    // A transferência e o atendente humano

    [Fact]
    public async Task A_IA_transfere_o_atendente_assume_responde_pelo_WhatsApp_sem_duplicar_e_encerra_o_atendimento()
    {
        await using var e = await IniciarAsync("wpphumano");
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere("price"));
        var (atendente, token) = await e.NovoAtendenteAsync();

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.ENTRADA0201", Cliente, "Pedro", "Quanto custa o hemograma?")));
        var sessao = await e.EsperarSessaoAsync("Pedro");

        // O cliente recebe o aviso da transferência pelo WhatsApp, e a conversa entra na fila de atendimento humano.
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "o aviso de transferência deveria sair pelo WhatsApp");
        Assert.Equal("Vou transferir você para um atendente. Um momento, por favor.", e.Graph.Capturas[0].Texto);
        string? itemId = null;
        await e.EsperarAsync(
            async () =>
            {
                var itens = await e.Ia.Api.LerAsync<JsonNode>(token, "/contact-center/items?status=Waiting&take=20");
                itemId = itens.AsArray().FirstOrDefault(i => i!["sessionId"]!.GetValue<Guid>() == sessao)?["id"]?.GetValue<string>();
                return itemId is not null;
            },
            "a conversa deveria estar esperando na fila");

        // O atendente assume, e só então pode responder.
        Assert.Equal(HttpStatusCode.Forbidden, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = Guid.NewGuid(), text = "Ainda não assumi" }));
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/claim", new { requestId = Guid.NewGuid() }));

        var pedido = Guid.NewGuid();
        const string textoDoAtendente = "O hemograma custa o valor da tabela do convênio. Posso agendar para você?";
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = pedido, text = textoDoAtendente }));
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 2, "a resposta do atendente deveria sair pelo WhatsApp");
        Assert.Equal((Cliente, textoDoAtendente), (e.Graph.Capturas[1].Para, e.Graph.Capturas[1].Texto));

        // A MESMA ação repetida (o cliente da API não recebeu a resposta e tenta de novo) devolve a mesma mensagem e nunca envia duas vezes.
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = pedido, text = textoDoAtendente }));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, e.Graph.Capturas.Count);
        Assert.Single(await e.Ia.MensagensAsync(sessao), m => m["author"]!.GetValue<string>() == "Human");

        // O mesmo identificador com OUTRO texto é recusado (nunca expõe nem altera o original), e uma resposta vazia é erro do pedido.
        Assert.Equal(HttpStatusCode.Conflict, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = pedido, text = "Outro texto" }));
        Assert.Equal(HttpStatusCode.BadRequest, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = Guid.NewGuid(), text = "   " }));

        // O cliente escreve de novo: quem responde é o humano, a IA não fala nem chama o modelo.
        var chamadasAoModelo = e.Ia.OpenAi.Roteiros.Capturas.Count;
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.ENTRADA0202", Cliente, "Pedro", "Pode ser amanhã de manhã?")));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(chamadasAoModelo, e.Ia.OpenAi.Roteiros.Capturas.Count);
        Assert.Equal(2, e.Graph.Capturas.Count);
        Assert.Contains((await e.Ia.MensagensAsync(sessao)), m => m["text"]?.GetValue<string>() == "Pode ser amanhã de manhã?");

        // Outro atendente da fila que não é o responsável não responde nem encerra a conversa do colega.
        var (_, outroToken) = await e.NovoAtendenteAsync();
        Assert.Equal(HttpStatusCode.Forbidden, await e.Ia.Api.StatusAsync(outroToken, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = Guid.NewGuid(), text = "Intruso" }));
        Assert.Equal(HttpStatusCode.Forbidden, await e.Ia.Api.StatusAsync(outroToken, HttpMethod.Post, $"/contact-center/items/{itemId}/close", new { requestId = Guid.NewGuid(), reasonCode = "resolved" }));

        // O encerramento: a sessão fecha, o item conclui pela projeção do evento, e repetir converge.
        var encerramento = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/close", new { requestId = encerramento, reasonCode = "resolved" }));
        Assert.Equal("Closed", (await e.Ia.SessaoAsync(sessao))["session"]!["status"]!.GetValue<string>());
        await e.EsperarAsync(
            async () => (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}"))["status"]!.GetValue<string>() == "Closed",
            "o item da fila deveria concluir");
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/close", new { requestId = encerramento, reasonCode = "resolved" }));
        Assert.Equal(HttpStatusCode.Conflict, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = Guid.NewGuid(), text = "Depois de encerrar" }));
        Assert.NotEqual(Guid.Empty, atendente.Id);
    }

    // A demonstração local

    [Fact]
    public async Task O_canal_de_demonstracao_do_Api_e_a_simulacao_da_Meta_percorrem_o_fluxo_do_roteiro_de_demonstracao()
    {
        await using var graph = await ServidorGraphLoopback.IniciarAsync();
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var adicionais = new Dictionary<string, string?>
        {
            ["Channels:Providers:WhatsApp:BaseAddress"] = graph.EnderecoBase,
            ["Channels:Secrets:ActiveKeyId"] = PostgresFixture.IdDaChaveDeTeste,
            [$"Channels:Secrets:Keys:{PostgresFixture.IdDaChaveDeTeste}"] = PostgresFixture.ChaveDeCifraDeTeste,
            ["Messaging:BaseBackoff"] = "00:00:00.300",
            ["Messaging:MaxBackoff"] = "00:00:01",

            // Os identificadores do canal de demonstração são únicos na plataforma: o teste usa os próprios, para ser repetível no banco compartilhado.
            ["Tenancy:Development:Ai:DemoChannel"] = "true",
            ["Tenancy:Development:Ai:DemoWabaId"] = $"waba-demo-{sufixo}",
            ["Tenancy:Development:Ai:DemoPhoneNumberId"] = $"9{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}",
        };
        await using var ia = await EnsaioDeIa.IniciarAsync(banco, "wppdemo", adicionais: adicionais);
        ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta do canal de demonstração."));
        await ia.CadastrarChaveAsync();

        // O que o script Send-MensagemWhatsApp.ps1 faz: lê o caminho do webhook e o App Secret sintético, assina o corpo e envia.
        var canal = await ia.LerAsync("/simulator/whatsapp/status");
        var corpo = EnsaioDeWhatsApp.MontarPayloadDeTexto(
            canal["wabaId"]!.GetValue<string>(), canal["phoneNumberId"]!.GetValue<string>(), $"wamid.SIM{sufixo}", Cliente, "Demonstração", "Olá, tudo bem?");
        using var pedido = new HttpRequestMessage(HttpMethod.Post, canal["webhookPath"]!.GetValue<string>()) { Content = new ByteArrayContent(corpo) };
        pedido.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        pedido.Headers.TryAddWithoutValidation("X-Hub-Signature-256", EnsaioDeWhatsApp.Assinar(corpo, canal["appSecret"]!.GetValue<string>()));
        using var resposta = await ia.Api.Client.SendAsync(pedido);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        await EnsaioDeIa.AguardarAsync(async () => graph.Capturas.Count == 1, "a resposta da IA deveria sair pelo WhatsApp simulado");
        var envio = Assert.Single(graph.Capturas);
        Assert.Equal((Cliente, "Resposta do canal de demonstração.", DevelopmentAiSetup.DemoAccessToken), (envio.Para, envio.Texto, envio.Credencial));
    }

    // O Sender: efeito externo

    [Fact]
    public async Task Janela_de_24_horas_fechada_e_falha_definitiva_registrada_e_nunca_reenviada()
    {
        await using var e = await IniciarAsync("wppjanela");
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta que não poderá sair."));
        e.Graph.Roteiro = _ => ServidorGraphLoopback.Erro(400, 131047);

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.ENTRADA0301", Cliente, "Janela", "Oi")));
        var sessao = await e.EsperarSessaoAsync("Janela");

        await e.EsperarAsync(async () => await e.EstadoDeEntregaAsync(sessao, "Ai") == "Failed", "a mensagem deveria ficar com falha definitiva");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Single(e.Graph.Capturas); // falha definitiva: nenhuma nova tentativa
    }

    [Fact]
    public async Task Resultado_ambiguo_do_envio_vira_SendUnknown_e_a_mensagem_nunca_e_reenviada()
    {
        await using var e = await IniciarAsync("wppincerto");
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta de resultado incerto."));
        e.Graph.Roteiro = _ => (500, "{}");

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.ENTRADA0401", Cliente, "Incerto", "Oi")));
        var sessao = await e.EsperarSessaoAsync("Incerto");

        await e.EsperarAsync(async () => await e.EstadoDeEntregaAsync(sessao, "Ai") == "SendUnknown", "o envio deveria ficar de resultado incerto");

        // A mensagem PODE ter saído: nada a reenvia, nem o relay, nem a espera, nem uma nova entrega do mesmo evento.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Single(e.Graph.Capturas);
        Assert.Equal("SendUnknown", await e.EstadoDeEntregaAsync(sessao, "Ai"));
    }

    [Fact]
    public async Task Recusa_transitoria_com_prova_de_que_nada_saiu_e_tentada_de_novo_e_o_envio_conclui()
    {
        await using var e = await IniciarAsync("wpptransitorio");
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta depois do limite de taxa."));
        var chamadas = 0;
        e.Graph.Roteiro = _ => Interlocked.Increment(ref chamadas) == 1 ? ServidorGraphLoopback.Erro(429, 130429) : e.Graph.Aceita();

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.ENTRADA0501", Cliente, "Taxa", "Oi")));
        var sessao = await e.EsperarSessaoAsync("Taxa");

        await e.EsperarAsync(async () => await e.EstadoDeEntregaAsync(sessao, "Ai") == "Sent", "o envio deveria concluir depois da nova tentativa");
        Assert.Equal(2, e.Graph.Capturas.Count);
    }
}
