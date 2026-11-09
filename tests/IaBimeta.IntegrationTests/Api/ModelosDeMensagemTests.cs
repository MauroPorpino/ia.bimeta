using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O envio de MODELOS de mensagem aprovados pela Meta (ADR 0027), de ponta a ponta: Api e Worker reais, PostgreSQL real, o adaptador REAL do WhatsApp contra a Graph API simulada (HTTP de verdade) e a OpenAI
/// simulada. O administrador associa os modelos à conta como a Meta os devolve; o atendente vê a janela de 24 horas, escolhe o modelo, preenche os parâmetros e o Sender envia pelo mesmo outbox, com a mesma
/// idempotência, vazão, pausa de conta e regra de resultado incerto do texto livre. Nenhuma credencial real, nenhuma chamada à Meta.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ModelosDeMensagemTests(PostgresFixture banco)
{
    private const string Cliente = "5511999990071";

    private const string ModelosDaMeta = """
        {"data":[
          {"name":"resultado_pronto","language":"pt_BR","category":"UTILITY","status":"APPROVED","id":"111","components":[
            {"type":"BODY","text":"Olá {{1}}, o resultado do exame {{2}} já está disponível no portal.","example":{"body_text":[["Mauro","hemograma"]]}},
            {"type":"FOOTER","text":"Laboratório Exemplo"}]},
          {"name":"aviso_simples","language":"pt_BR","category":"UTILITY","status":"APPROVED","id":"222","components":[
            {"type":"BODY","text":"Seu atendimento foi retomado."}]},
          {"name":"em_analise","language":"pt_BR","category":"MARKETING","status":"PENDING","id":"333","components":[
            {"type":"BODY","text":"Novidades do laboratório."}]},
          {"name":"com_imagem","language":"pt_BR","category":"MARKETING","status":"APPROVED","id":"444","components":[
            {"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Oi"}]}
        ]}
        """;

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere(string motivo) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir(motivo), 150, 0, 15, "req_mod"));

    private sealed record Atendimento(Guid Sessao, string ItemId, string Token);

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, IReadOnlyDictionary<string, string?>? configuracao = null)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, configuracao);
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere("price"));
        return e;
    }

    /// <summary>O cliente escreve (no relógio do provedor informado), a IA transfere, e o atendente assume a conversa. Termina com o aviso de transferência já enviado (uma captura na Meta).</summary>
    private static async Task<Atendimento> AtenderAsync(EnsaioDeWhatsApp e, string nome, string waId, DateTimeOffset? momento = null, string? token = null)
    {
        token ??= (await e.NovoAtendenteAsync()).Token;
        var idDaMensagem = "wamid.MOD" + Guid.NewGuid().ToString("N")[..12];
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto(idDaMensagem, waId, nome, "Preciso do resultado do meu exame.", momento: momento)));
        var sessao = await e.EsperarSessaoAsync(nome);

        string? itemId = null;
        await e.EsperarAsync(
            async () =>
            {
                var itens = await e.Ia.Api.LerAsync<JsonNode>(token, "/contact-center/items?status=Waiting&take=50");
                itemId = itens.AsArray().FirstOrDefault(i => i!["sessionId"]!.GetValue<Guid>() == sessao)?["id"]?.GetValue<string>();
                return itemId is not null;
            },
            "a conversa deveria estar esperando na fila");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count >= 1, "o aviso de transferência deveria sair pelo WhatsApp");

        // Assumir logo depois de o item aparecer na fila pode cair antes de a projeção do atendimento terminar (409 de mudança concorrente): repete o MESMO pedido até o atendimento assentar.
        var pedido = Guid.NewGuid();
        await e.EsperarAsync(
            async () => await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/claim", new { requestId = pedido }) == HttpStatusCode.OK,
            "o atendente deveria conseguir assumir a conversa");
        return new Atendimento(sessao, itemId!, token);
    }

    private static async Task<JsonNode> CadastrarModelosAsync(EnsaioDeWhatsApp e, string admin, string json, Guid? conta = null)
    {
        var (status, corpo) = await e.ChamarAsync(admin, HttpMethod.Post, $"/channels/accounts/{conta ?? e.ContaId}/templates", JsonNode.Parse(json));
        Assert.Equal(HttpStatusCode.OK, status);
        return corpo!;
    }

    private static async Task<JsonNode> ModelosDaContaAsync(EnsaioDeWhatsApp e, string admin, Guid? conta = null) =>
        (await e.ChamarAsync(admin, HttpMethod.Get, $"/channels/accounts/{conta ?? e.ContaId}/templates")).Corpo!;

    private static async Task<Guid> IdDoModeloAsync(EnsaioDeWhatsApp e, string admin, string nome, Guid? conta = null) =>
        (await ModelosDaContaAsync(e, admin, conta)).AsArray().Single(m => m!["name"]!.GetValue<string>() == nome)!["id"]!.GetValue<Guid>();

    private static Task<(HttpStatusCode Status, JsonNode? Corpo)> OpcoesAsync(EnsaioDeWhatsApp e, Atendimento a) =>
        e.ChamarAsync(a.Token, HttpMethod.Get, $"/contact-center/items/{a.ItemId}/reply-options");

    private static Task<(HttpStatusCode Status, JsonNode? Corpo)> ResponderComModeloAsync(
        EnsaioDeWhatsApp e, Atendimento a, Guid modelo, IReadOnlyDictionary<string, string>? parametros = null, Guid? pedido = null)
    {
        var corpo = new JsonObject { ["requestId"] = pedido ?? Guid.NewGuid(), ["templateId"] = modelo };
        if (parametros is not null)
        {
            var valores = new JsonObject();
            foreach (var (chave, valor) in parametros)
            {
                valores[chave] = valor;
            }

            corpo["parameters"] = valores;
        }

        return e.ChamarAsync(a.Token, HttpMethod.Post, $"/contact-center/items/{a.ItemId}/reply-template", corpo);
    }

    private static Dictionary<string, string> Parametros(string nome = "Mauro", string exame = "hemograma") => new() { ["body.1"] = nome, ["body.2"] = exame };

    private static int Modelos(EnsaioDeWhatsApp e) => e.Graph.Capturas.Count(c => c.Tipo == "template");

    // Fora da janela de 24 horas

    [Fact]
    public async Task Fora_da_janela_de_24_horas_o_texto_livre_e_bloqueado_e_o_modelo_aprovado_sai_pela_Meta_com_o_payload_de_template()
    {
        await using var e = await IniciarAsync("modforajanela");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);

        // O administrador associa os modelos que JÁ existem na Meta (a página da Graph API): três entram (um deles ainda pendente) e o de cabeçalho de imagem é recusado com o motivo.
        var registro = await CadastrarModelosAsync(e, admin, ModelosDaMeta);
        Assert.Equal(new[] { "aviso_simples", "em_analise", "resultado_pronto" }, registro["created"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).Order().ToArray());
        Assert.Contains("IMAGE", Assert.Single(registro["rejected"]!.AsArray())!.GetValue<string>());
        var resultadoPronto = await IdDoModeloAsync(e, admin, "resultado_pronto");

        // A última mensagem do cliente tem 26 horas: a janela de resposta livre está fechada.
        var a = await AtenderAsync(e, "Fora da Janela", Cliente, momento: DateTimeOffset.UtcNow.AddHours(-26));
        var (statusOpcoes, opcoes) = await OpcoesAsync(e, a);
        Assert.Equal(HttpStatusCode.OK, statusOpcoes);
        Assert.False(opcoes!["windowOpen"]!.GetValue<bool>());
        Assert.False(opcoes["freeTextAllowed"]!.GetValue<bool>());
        Assert.Equal(24, opcoes["windowHours"]!.GetValue<double>());
        Assert.True(opcoes["windowClosesAt"]!.GetValue<DateTimeOffset>() < DateTimeOffset.UtcNow);

        // Só os modelos APROVADOS são oferecidos, com a estrutura de parâmetros e o exemplo da aprovação.
        var oferecidos = opcoes["templates"]!.AsArray();
        Assert.Equal(new[] { "aviso_simples", "resultado_pronto" }, oferecidos.Select(m => m!["name"]!.GetValue<string>()).Order().ToArray());
        var oferecido = oferecidos.Single(m => m!["name"]!.GetValue<string>() == "resultado_pronto")!;
        Assert.Equal(new[] { "body.1", "body.2" }, oferecido["parameters"]!.AsArray().Select(p => p!["key"]!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { "Mauro", "hemograma" }, oferecido["parameters"]!.AsArray().Select(p => p!["example"]!.GetValue<string>()).ToArray());

        // Texto livre fora da janela: recusado ANTES de gravar e de chamar a Meta.
        var (livre, problema) = await e.ChamarAsync(a.Token, HttpMethod.Post, $"/contact-center/items/{a.ItemId}/reply", new JsonObject { ["requestId"] = Guid.NewGuid(), ["text"] = "Olá, posso ajudar?" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, livre);
        Assert.Equal("ReplyWindowClosed", problema!["code"]!.GetValue<string>());
        Assert.Single(e.Graph.Capturas); // só o aviso de transferência da IA
        Assert.DoesNotContain(await e.Ia.MensagensAsync(a.Sessao), m => m["author"]!.GetValue<string>() == "Human");

        // Parâmetros inválidos voltam TODOS de uma vez, por chave, antes de gravar ou enviar; o valor digitado nunca é repetido.
        var (invalido, erros) = await ResponderComModeloAsync(e, a, resultadoPronto, new Dictionary<string, string> { ["body.1"] = "SEGREDO\ncom quebra", ["intruso"] = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalido);
        Assert.Equal("TemplateParametersInvalid", erros!["code"]!.GetValue<string>());
        Assert.Equal(
            new[] { ("intruso", "unknown"), ("body.1", "invalid-characters"), ("body.2", "required") },
            erros["errors"]!.AsArray().Select(x => (x!["key"]!.GetValue<string>(), x["code"]!.GetValue<string>())).ToArray());
        Assert.DoesNotContain("SEGREDO", erros.ToJsonString());
        Assert.Single(e.Graph.Capturas);

        // O modelo com os parâmetros certos sai pelo adaptador real: payload de template, token só no cabeçalho, número de origem na URL.
        var pedido = Guid.NewGuid();
        var (enviado, resposta) = await ResponderComModeloAsync(e, a, resultadoPronto, Parametros(), pedido);
        Assert.Equal(HttpStatusCode.OK, enviado);
        Assert.Equal("Applied", resposta!["outcome"]!.GetValue<string>());
        await e.EsperarAsync(async () => Modelos(e) == 1, "o modelo deveria sair pelo WhatsApp");
        var envio = e.Graph.Capturas.Single(c => c.Tipo == "template");
        Assert.Equal(EnsaioDeWhatsApp.TokenDeAcesso, envio.Credencial);
        Assert.EndsWith($"/{e.NumeroDoCanal}/messages", envio.Caminho);
        Assert.Equal((Cliente, "resultado_pronto", "pt_BR"), (envio.Para, envio.NomeDoModelo, envio.IdiomaDoModelo));
        var corpo = Assert.Single(envio.Componentes!)!;
        Assert.Equal("body", corpo["type"]!.GetValue<string>());
        Assert.Equal(new[] { "Mauro", "hemograma" }, corpo["parameters"]!.AsArray().Select(p => p!["text"]!.GetValue<string>()).ToArray());
        Assert.Matches("^[0-9a-f]{32}$", envio.DadoOpaco);
        Assert.DoesNotContain(EnsaioDeWhatsApp.TokenDeAcesso, envio.Corpo);

        // O histórico guarda o que o cliente leu (modelo com os parâmetros) e o nome do modelo; o estado de entrega avança pelos status da Meta como em qualquer mensagem.
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human");
        var humana = (await e.Ia.MensagensAsync(a.Sessao)).Single(m => m["author"]!.GetValue<string>() == "Human");
        Assert.Equal("Olá Mauro, o resultado do exame hemograma já está disponível no portal.\nLaboratório Exemplo", humana["text"]!.GetValue<string>());

        // O atendente vê, no histórico da fila, que a mensagem saiu por modelo (e qual): as demais não têm rótulo.
        var historico = await e.Ia.Api.LerAsync<JsonNode>(a.Token, $"/contact-center/items/{a.ItemId}/messages?take=50");
        var mensagens = historico["items"]!.AsArray();
        Assert.Equal("resultado_pronto", Assert.Single(mensagens, m => m!["author"]!.GetValue<string>() == "Human")!["templateName"]!.GetValue<string>());
        Assert.All(mensagens.Where(m => m!["author"]!.GetValue<string>() != "Human"), m => Assert.Null(m!["templateName"]));
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeStatus("wamid.SAIDA0002", "delivered", envio.DadoOpaco, Cliente)));
        await e.EsperarEntregaAsync(a.Sessao, "Delivered", autor: "Human");

        // A MESMA ação repetida (a resposta se perdeu) devolve a mesma mensagem e não envia duas vezes; o mesmo pedido com outros parâmetros é outro efeito e é recusado.
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, resultadoPronto, Parametros(), pedido)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await ResponderComModeloAsync(e, a, resultadoPronto, Parametros("Pedro"), pedido)).Status);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(1, Modelos(e));
        Assert.Single(await e.Ia.MensagensAsync(a.Sessao), m => m["author"]!.GetValue<string>() == "Human");

        // O texto livre continua bloqueado depois do modelo: a janela só reabre quando o CLIENTE escreve.
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await e.ChamarAsync(a.Token, HttpMethod.Post, $"/contact-center/items/{a.ItemId}/reply", new JsonObject { ["requestId"] = Guid.NewGuid(), ["text"] = "Ainda aí?" })).Status);

        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(EnsaioDeWhatsApp.SegredoDoApp) || l.Contains(EnsaioDeWhatsApp.TokenDeAcesso) || l.Contains(EnsaioDeWhatsApp.TokenDeVerificacao));
    }

    // Dentro da janela, e quem pode o quê

    [Fact]
    public async Task Dentro_da_janela_o_texto_livre_continua_valendo_e_o_modelo_so_sai_aprovado_da_conta_do_numero_e_pelo_responsavel()
    {
        await using var e = await IniciarAsync("moddentro");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        await CadastrarModelosAsync(e, admin, ModelosDaMeta);
        var aviso = await IdDoModeloAsync(e, admin, "aviso_simples");
        var resultadoPronto = await IdDoModeloAsync(e, admin, "resultado_pronto");
        var emAnalise = await IdDoModeloAsync(e, admin, "em_analise");

        var a = await AtenderAsync(e, "Dentro da Janela", "5511999990072");

        // Janela aberta: fecha 24 horas depois da última mensagem do cliente.
        var (_, opcoes) = await OpcoesAsync(e, a);
        Assert.True(opcoes!["windowOpen"]!.GetValue<bool>());
        Assert.True(opcoes["freeTextAllowed"]!.GetValue<bool>());
        Assert.Equal(TimeSpan.FromHours(24), opcoes["windowClosesAt"]!.GetValue<DateTimeOffset>() - opcoes["lastInboundAt"]!.GetValue<DateTimeOffset>());

        // O texto livre sai como texto; o modelo também vale dentro da janela.
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(a.Token, HttpMethod.Post, $"/contact-center/items/{a.ItemId}/reply", new { requestId = Guid.NewGuid(), text = "Olá! Já estou vendo o seu exame." }));
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 2, "o texto livre deveria sair pelo WhatsApp");
        Assert.Equal("text", e.Graph.Capturas[1].Tipo);
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, aviso)).Status); // modelo sem parâmetros: o campo é opcional
        await e.EsperarAsync(async () => Modelos(e) == 1, "o modelo deveria sair pelo WhatsApp");
        Assert.Null(e.Graph.Capturas.Single(c => c.Tipo == "template").Componentes);

        // Modelo pendente, inexistente ou de OUTRA conta do mesmo tenant: indisponível, nada gravado nem enviado.
        var contaOutra = await e.Ia.ComoSistemaAsync(async sp => (await sp.GetRequiredService<ChannelAccountService>().CreateAsync(e.OrganizacaoId, ChannelProvider.WhatsAppCloud, $"waba-outra-{Guid.NewGuid():N}"[..24], "Outra conta")).Id);
        await CadastrarModelosAsync(e, admin, """{"name":"da_outra_conta","language":"pt_BR","category":"UTILITY","status":"APPROVED","components":[{"type":"BODY","text":"Oi"}]}""", contaOutra);
        var daOutraConta = await IdDoModeloAsync(e, admin, "da_outra_conta", contaOutra);
        foreach (var indisponivel in new[] { emAnalise, daOutraConta, Guid.NewGuid() })
        {
            var (status, problema) = await ResponderComModeloAsync(e, a, indisponivel);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Equal("ReplyTemplateUnavailable", problema!["code"]!.GetValue<string>());
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await ResponderComModeloAsync(e, a, Guid.Empty)).Status);

        // Um modelo que o administrador marca como pausado deixa de ser oferecido e de ser aceito.
        Assert.Equal(
            HttpStatusCode.OK,
            (await e.ChamarAsync(admin, HttpMethod.Put, $"/channels/accounts/{e.ContaId}/templates/{resultadoPronto}/status", new JsonObject { ["status"] = "Paused" })).Status);
        var (_, depois) = await OpcoesAsync(e, a);
        Assert.Equal(new[] { "aviso_simples" }, depois!["templates"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ResponderComModeloAsync(e, a, resultadoPronto, Parametros())).Status);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Modelos(e));
        Assert.Equal(3, e.Graph.Capturas.Count); // o aviso da IA, o texto livre e UM modelo: nada a mais

        // Só quem gere os canais associa modelos. O atendente (usuário comum) nem enxerga a conta (404, como qualquer recurso fora do alcance); o gestor lê mas não gere (403).
        var gestor = await e.TokenDeAsync(Role.Manager);
        foreach (var token in new[] { a.Token })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(token, HttpMethod.Get, $"/channels/accounts/{e.ContaId}/templates")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(token, HttpMethod.Post, $"/channels/accounts/{e.ContaId}/templates", JsonNode.Parse(ModelosDaMeta))).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(token, HttpMethod.Put, $"/channels/accounts/{e.ContaId}/templates/{aviso}/status", new JsonObject { ["status"] = "Disabled" })).Status);
        }

        Assert.Equal(HttpStatusCode.OK, (await e.ChamarAsync(gestor, HttpMethod.Get, $"/channels/accounts/{e.ContaId}/templates")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.ChamarAsync(gestor, HttpMethod.Post, $"/channels/accounts/{e.ContaId}/templates", JsonNode.Parse(ModelosDaMeta))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.ChamarAsync(gestor, HttpMethod.Put, $"/channels/accounts/{e.ContaId}/templates/{aviso}/status", new JsonObject { ["status"] = "Disabled" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await e.ChamarAsync(admin, HttpMethod.Put, $"/channels/accounts/{e.ContaId}/templates/{aviso}/status", new JsonObject { ["status"] = "Inventado" })).Status);
        Assert.Equal("Approved", (await ModelosDaContaAsync(e, admin)).AsArray().Single(m => m!["name"]!.GetValue<string>() == "aviso_simples")!["status"]!.GetValue<string>());

        // Outro atendente da fila que NÃO é o responsável não responde por modelo (nem vê as opções de uma conversa que não é dele para responder), e sem sessão é 401.
        var (_, outroToken) = await e.NovoAtendenteAsync();
        var intruso = a with { Token = outroToken };
        Assert.Equal(HttpStatusCode.Forbidden, (await ResponderComModeloAsync(e, intruso, aviso)).Status);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await e.Ia.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/items/{a.ItemId}/reply-template", new { requestId = Guid.NewGuid(), templateId = aviso }));

        Assert.Equal(1, Modelos(e));
    }

    // O Sender: falhas, vazão, pausa e resultado incerto

    [Fact]
    public async Task O_modelo_usa_o_Sender_atual_recusa_da_Meta_falha_transitoria_resultado_incerto_e_conta_pausada()
    {
        await using var e = await IniciarAsync("modsender");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        await CadastrarModelosAsync(e, admin, ModelosDaMeta);
        var aviso = await IdDoModeloAsync(e, admin, "aviso_simples");
        var resultadoPronto = await IdDoModeloAsync(e, admin, "resultado_pronto");
        var a = await AtenderAsync(e, "Sender", "5511999990073", momento: DateTimeOffset.UtcNow.AddDays(-3));

        // (a) A Meta recusa o MODELO (132015, pausado por ela): a mensagem falha de forma visível, o modelo sai da lista do atendente e a CONTA continua enviando.
        e.Graph.Roteiro = _ => ServidorGraphLoopback.Erro(400, 132015);
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, resultadoPronto, Parametros())).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", ordem: 0);
        await e.EsperarAsync(
            async () => (await ModelosDaContaAsync(e, admin)).AsArray().Single(m => m!["name"]!.GetValue<string>() == "resultado_pronto")!["status"]!.GetValue<string>() == "Paused",
            "o modelo recusado pela Meta deveria ficar pausado no cadastro");
        Assert.Equal(new[] { "aviso_simples" }, (await OpcoesAsync(e, a)).Corpo!["templates"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).ToArray());
        Assert.False((await e.ChamarAsync(admin, HttpMethod.Get, $"/channels/accounts/{e.ContaId}/sending")).Corpo!["paused"]!.GetValue<bool>());
        Assert.Equal(1, Modelos(e));

        // (b) Falha transitória (limite de taxa, com a espera do provedor): a MESMA mensagem é tentada de novo e sai UMA vez.
        var chamadas = 0;
        e.Graph.RoteiroComEspera = _ => Interlocked.Increment(ref chamadas) == 1 ? (429, ServidorGraphLoopback.Erro(429, 130429).Corpo, 1) : e.Graph.Aceita().Let(r => (r.Status, r.Corpo, (int?)null));
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, aviso)).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 1);
        Assert.Equal(3, Modelos(e)); // a recusa de (a), a recusa transitória e o envio aceito
        e.Graph.RoteiroComEspera = null;

        // (c) Resultado incerto (a Meta responde 500 sem corpo de erro): SendUnknown e NUNCA reenviado.
        e.Graph.Roteiro = _ => (500, string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, aviso)).Status);
        await e.EsperarEntregaAsync(a.Sessao, "SendUnknown", autor: "Human", ordem: 2);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(4, Modelos(e));
        Assert.Equal("SendUnknown", await e.EstadoDeEntregaAsync(a.Sessao, "Human", 2));

        // (d) Erro de CONTA (token inválido): a mensagem falha, a conta é pausada, e o modelo seguinte falha sem tocar na Meta.
        e.Graph.Roteiro = _ => ServidorGraphLoopback.Erro(401, 190);
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, aviso)).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", ordem: 3);
        Assert.True((await e.ChamarAsync(admin, HttpMethod.Get, $"/channels/accounts/{e.ContaId}/sending")).Corpo!["paused"]!.GetValue<bool>());
        Assert.Equal(5, Modelos(e));
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, aviso)).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", ordem: 4);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(5, Modelos(e)); // a conta pausada não chamou a Meta

        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(EnsaioDeWhatsApp.SegredoDoApp) || l.Contains(EnsaioDeWhatsApp.TokenDeAcesso) || l.Contains(EnsaioDeWhatsApp.TokenDeVerificacao));
    }

    [Fact]
    public async Task Modelo_que_deixa_de_estar_aprovado_antes_do_Sender_falha_de_forma_visivel_sem_chamar_a_Meta_e_a_ordem_da_conversa_e_mantida()
    {
        await using var e = await IniciarAsync("modantes");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        await CadastrarModelosAsync(e, admin, ModelosDaMeta);
        var aviso = await IdDoModeloAsync(e, admin, "aviso_simples");
        var resultadoPronto = await IdDoModeloAsync(e, admin, "resultado_pronto");
        var a = await AtenderAsync(e, "Antes do Sender", "5511999990075", momento: DateTimeOffset.UtcNow.AddDays(-2));

        // O primeiro modelo fica com a chamada presa na Meta; o segundo, da MESMA conversa, espera a vez dele (a ordem da conversa é mantida) com o pedido já aceito.
        var libera = new TaskCompletionSource();
        e.Graph.Antes = async (captura, cancelamento) =>
        {
            if (captura.Tipo == "template")
            {
                await Task.WhenAny(libera.Task, Task.Delay(Timeout.Infinite, cancelamento));
            }
        };
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, aviso)).Status);
        await e.EsperarAsync(async () => Modelos(e) == 1, "o primeiro modelo deveria chegar à Meta e ficar preso");
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, resultadoPronto, Parametros())).Status);

        // Antes de o Sender chegar ao segundo, o modelo é desativado (a Meta o tirou do ar, ou o administrador o marcou).
        Assert.Equal(
            HttpStatusCode.OK,
            (await e.ChamarAsync(admin, HttpMethod.Put, $"/channels/accounts/{e.ContaId}/templates/{resultadoPronto}/status", new JsonObject { ["status"] = "Disabled" })).Status);
        libera.SetResult();

        // O primeiro sai; o segundo falha de forma visível, SEM chamada à Meta (o Sender confere o modelo de novo na hora de enviar).
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 0);
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", ordem: 1);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Modelos(e));
        Assert.False((await e.ChamarAsync(admin, HttpMethod.Get, $"/channels/accounts/{e.ContaId}/sending")).Corpo!["paused"]!.GetValue<bool>()); // a conta continua enviando
    }

    [Fact]
    public async Task Worker_que_cai_no_meio_do_envio_do_modelo_deixa_o_resultado_incerto_e_a_mensagem_nunca_e_reenviada()
    {
        await using var e = await IniciarAsync("modreinicio", new Dictionary<string, string?>
        {
            ["Messaging:LeaseDuration"] = "00:00:03",
            ["Channels:Providers:WhatsApp:RequestTimeout"] = "00:01:00",
        });
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        await CadastrarModelosAsync(e, admin, ModelosDaMeta);
        var resultadoPronto = await IdDoModeloAsync(e, admin, "resultado_pronto");
        var a = await AtenderAsync(e, "Reinicio", "5511999990074", momento: DateTimeOffset.UtcNow.AddDays(-2));

        // A Meta recebe a chamada do modelo e nunca responde (o processo vai cair com ela em andamento).
        e.Graph.Antes = (captura, cancelamento) => captura.Tipo == "template" ? Task.Delay(Timeout.Infinite, cancelamento) : Task.CompletedTask;
        Assert.Equal(HttpStatusCode.OK, (await ResponderComModeloAsync(e, a, resultadoPronto, Parametros())).Status);
        await e.EsperarAsync(async () => Modelos(e) == 1, "a chamada do modelo deveria chegar à Meta e ficar em andamento");

        e.Graph.Antes = null;
        e.Graph.Roteiro = _ => e.Graph.Aceita(); // se o novo Worker (erradamente) reenviasse, a Meta aceitaria e o modelo sairia DUAS vezes
        await e.Ia.ReiniciarWorkerAsync();

        await e.EsperarEntregaAsync(a.Sessao, "SendUnknown", autor: "Human", segundos: 40);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Modelos(e));
        Assert.Equal("SendUnknown", await e.EstadoDeEntregaAsync(a.Sessao, "Human"));
    }
}

internal static class ModelosDeMensagemExtensoes
{
    public static TResultado Let<T, TResultado>(this T valor, Func<T, TResultado> funcao) => funcao(valor);
}
