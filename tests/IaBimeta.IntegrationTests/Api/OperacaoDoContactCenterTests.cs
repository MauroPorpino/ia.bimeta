using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Agents;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A operação diária da Central de Atendimento por HTTP de verdade (Api e Worker reais, PostgreSQL real, Meta e OpenAI simuladas): dois atendentes ao mesmo tempo sobre várias conversas. É o que a interface React usa por
/// baixo: a corrida para assumir tem um único vencedor, cada resposta sai só para o cliente da conversa certa, o histórico de uma conversa nunca traz texto de outra, quem não é o responsável não responde nem encerra,
/// repetir a mesma ação não duplica nada e a devolução à fila deixa o outro atendente assumir. Mais o aviso de conta pausada que o atendente comum recebe (ADR 0022).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperacaoDoContactCenterTests(PostgresFixture banco)
{
    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere(string motivo) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir(motivo), 150, 0, 15, "req_oper"));

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo);
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere("price"));
        return e;
    }

    private static Task<HttpStatusCode> AcaoAsync(EnsaioDeWhatsApp e, string token, string itemId, string acao, object corpo) =>
        e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/{acao}", corpo);

    private static async Task<string?> StatusDoItemAsync(EnsaioDeWhatsApp e, string token, string itemId) =>
        (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}"))["status"]!.GetValue<string>();

    private static async Task<HashSet<string>> IdsAsync(EnsaioDeWhatsApp e, string token, string consulta) =>
        (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items?{consulta}")).AsArray().Select(i => i!["id"]!.GetValue<string>()).ToHashSet();

    private static async Task<string> ItemDaSessaoAsync(EnsaioDeWhatsApp e, string token, Guid sessao)
    {
        string? itemId = null;
        await e.EsperarAsync(
            async () =>
            {
                var itens = await e.Ia.Api.LerAsync<JsonNode>(token, "/contact-center/items?status=Waiting&take=50");
                itemId = itens.AsArray().FirstOrDefault(i => i!["sessionId"]!.GetValue<Guid>() == sessao)?["id"]?.GetValue<string>();
                return itemId is not null;
            },
            "a conversa deveria estar esperando na fila");
        return itemId!;
    }

    [Fact]
    public async Task Dois_atendentes_e_varias_conversas_a_corrida_tem_um_vencedor_cada_resposta_vai_ao_cliente_certo_e_o_historico_nao_se_mistura()
    {
        await using var e = await IniciarAsync("ccoper");
        var (_, tokenA) = await e.NovoAtendenteAsync();
        var (_, tokenB) = await e.NovoAtendenteAsync();

        var clientes = new (string Nome, string WaId, string Pergunta, string Resposta)[]
        {
            ("Mauro", "5511999990101", "Quanto custa o hemograma?", "Mauro, o hemograma segue a tabela do seu convênio."),
            ("Pedro", "5511999990102", "Quanto custa o PSA?", "Pedro, o PSA segue a tabela particular."),
            ("Joana", "5511999990103", "Quanto custa a glicemia?", "Joana, a glicemia tem o valor de balcão."),
        };
        var sessoes = new Dictionary<string, Guid>();
        for (var i = 0; i < clientes.Length; i++)
        {
            var c = clientes[i];
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto($"wamid.OPER{i:0000}", c.WaId, c.Nome, c.Pergunta)));
            sessoes[c.Nome] = await e.EsperarSessaoAsync(c.Nome);
        }

        // Cada cliente recebe o aviso da transferência, e as três conversas entram na fila de atendimento humano.
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 3, "os três avisos de transferência deveriam sair pelo WhatsApp");
        var itens = new Dictionary<string, string>();
        foreach (var c in clientes)
        {
            itens[c.Nome] = await ItemDaSessaoAsync(e, tokenA, sessoes[c.Nome]);
        }

        // Os dois atendentes veem as mesmas conversas aguardando.
        var vistasPorB = await IdsAsync(e, tokenB, "status=Waiting&take=50");
        Assert.All(itens.Values, id => Assert.Contains(id, vistasPorB));

        // A corrida pela mesma conversa: um único vencedor, o outro recebe conflito (a interface mostra "outro atendente assumiu").
        var corrida = await Task.WhenAll(
            AcaoAsync(e, tokenA, itens["Mauro"], "claim", new { requestId = Guid.NewGuid() }),
            AcaoAsync(e, tokenB, itens["Mauro"], "claim", new { requestId = Guid.NewGuid() }));
        Assert.Single(corrida, s => s == HttpStatusCode.OK);
        Assert.Single(corrida, s => s == HttpStatusCode.Conflict);
        var (vencedor, perdedor) = corrida[0] == HttpStatusCode.OK ? (tokenA, tokenB) : (tokenB, tokenA);

        // O perdedor assume outra conversa e o vencedor, a terceira: o trabalho se divide sem pisar em ninguém.
        Assert.Equal(HttpStatusCode.OK, await AcaoAsync(e, perdedor, itens["Pedro"], "claim", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, await AcaoAsync(e, vencedor, itens["Joana"], "claim", new { requestId = Guid.NewGuid() }));
        Assert.Equal(new[] { itens["Mauro"], itens["Joana"] }.Order(), (await IdsAsync(e, vencedor, "status=Assigned&assignedToMe=true&take=50")).Order());
        Assert.Equal(new[] { itens["Pedro"] }, (await IdsAsync(e, perdedor, "status=Assigned&assignedToMe=true&take=50")).ToArray());
        Assert.Empty((await IdsAsync(e, vencedor, "status=Waiting&take=50")).Intersect(itens.Values));

        // As respostas: cada uma sai pelo WhatsApp só para o cliente da própria conversa.
        var donos = new Dictionary<string, string> { ["Mauro"] = vencedor, ["Pedro"] = perdedor, ["Joana"] = vencedor };
        var pedidoDoMauro = Guid.NewGuid();
        foreach (var c in clientes)
        {
            var pedido = c.Nome == "Mauro" ? pedidoDoMauro : Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK, await AcaoAsync(e, donos[c.Nome], itens[c.Nome], "reply", new { requestId = pedido, text = c.Resposta }));
        }

        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 6, "as três respostas dos atendentes deveriam sair pelo WhatsApp");
        foreach (var c in clientes)
        {
            var envio = Assert.Single(e.Graph.Capturas, x => x.Texto == c.Resposta);
            Assert.Equal(c.WaId, envio.Para);
        }

        // Repetir a MESMA resposta (a interface perdeu a conexão antes de ver o 200) devolve a mesma mensagem e não envia de novo.
        Assert.Equal(HttpStatusCode.OK, await AcaoAsync(e, vencedor, itens["Mauro"], "reply", new { requestId = pedidoDoMauro, text = clientes[0].Resposta }));

        // Quem não é o responsável não responde nem encerra a conversa do colega.
        Assert.Equal(HttpStatusCode.Forbidden, await AcaoAsync(e, perdedor, itens["Mauro"], "reply", new { requestId = Guid.NewGuid(), text = "Intruso" }));
        Assert.Equal(HttpStatusCode.Forbidden, await AcaoAsync(e, perdedor, itens["Mauro"], "close", new { requestId = Guid.NewGuid(), reasonCode = "resolved" }));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(6, e.Graph.Capturas.Count);

        // O histórico: cada conversa traz as próprias mensagens e nenhum texto das outras (nem pelo simulador, nem pela API do atendente).
        foreach (var c in clientes)
        {
            var textos = (await e.Ia.MensagensAsync(sessoes[c.Nome])).Select(m => m["text"]?.GetValue<string>()).ToList();
            Assert.Contains(c.Pergunta, textos);
            Assert.Contains(c.Resposta, textos);
            foreach (var outro in clientes.Where(o => o.Nome != c.Nome))
            {
                Assert.DoesNotContain(outro.Pergunta, textos);
                Assert.DoesNotContain(outro.Resposta, textos);
            }

            var pagina = await e.Ia.Api.LerAsync<JsonNode>(donos[c.Nome], $"/contact-center/items/{itens[c.Nome]}/messages?take=50");
            var textosDaApi = pagina["items"]!.AsArray().Select(m => m!["text"]?.GetValue<string>()).ToList();
            Assert.Contains(c.Pergunta, textosDaApi);
            Assert.Contains(c.Resposta, textosDaApi);
            Assert.DoesNotContain(clientes.First(o => o.Nome != c.Nome).Pergunta, textosDaApi);
        }

        // Devolver à fila: a conversa volta a aguardar, o colega assume e o antigo responsável deixa de poder responder nela.
        Assert.Equal(HttpStatusCode.OK, await AcaoAsync(e, vencedor, itens["Joana"], "release", new { requestId = Guid.NewGuid() }));
        await e.EsperarAsync(async () => await StatusDoItemAsync(e, perdedor, itens["Joana"]) == "Waiting", "a conversa devolvida deveria voltar a aguardar");
        Assert.Equal(HttpStatusCode.OK, await AcaoAsync(e, perdedor, itens["Joana"], "claim", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.Forbidden, await AcaoAsync(e, vencedor, itens["Joana"], "reply", new { requestId = Guid.NewGuid(), text = "Já não é minha" }));

        // Encerrar: a conversa sai das listas, e depois de encerrada nenhuma resposta é aceita.
        Assert.Equal(HttpStatusCode.OK, await AcaoAsync(e, perdedor, itens["Pedro"], "close", new { requestId = Guid.NewGuid(), reasonCode = "resolved" }));
        await e.EsperarAsync(async () => await StatusDoItemAsync(e, perdedor, itens["Pedro"]) == "Closed", "o item encerrado deveria concluir");
        Assert.Equal(HttpStatusCode.Conflict, await AcaoAsync(e, perdedor, itens["Pedro"], "reply", new { requestId = Guid.NewGuid(), text = "Depois de encerrar" }));
        Assert.DoesNotContain(itens["Pedro"], await IdsAsync(e, perdedor, "status=Assigned&assignedToMe=true&take=50"));

        // Nada além do combinado saiu para os clientes, e o modelo foi chamado só nas três primeiras mensagens (depois, quem responde é o humano).
        Assert.Equal(6, e.Graph.Capturas.Count);
        Assert.Equal(3, e.Ia.OpenAi.Roteiros.Capturas.Count);
    }

    [Fact]
    public async Task Historico_longo_pagina_pelo_cursor_sem_repetir_nem_perder_mesmo_com_mensagem_nova_no_meio()
    {
        await using var e = await IniciarAsync("cchist");
        var (_, token) = await e.NovoAtendenteAsync();
        const string cliente = "5511999990301";

        // 130 mensagens do cliente na mesma conversa (a primeira faz a IA transferir; as outras entram com a conversa na fila) e o aviso da transferência: 131 no histórico.
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.HIST0000", cliente, "Historia", "Quanto custa o hemograma?")));
        var sessao = await e.EsperarSessaoAsync("Historia");
        var itemId = await ItemDaSessaoAsync(e, token, sessao);
        for (var i = 1; i < 130; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto($"wamid.HIST{i:0000}", cliente, "Historia", $"mensagem {i}")));
        }

        async Task<int> TotalNoHistoricoAsync()
        {
            // Conta pelas páginas da própria API do atendente (o limite de uma página é 100).
            var total = 0;
            string? proximo = null;
            do
            {
                var pagina = await PaginaAsync(proximo, 100);
                total += pagina["items"]!.AsArray().Count;
                proximo = pagina["nextCursor"]?.GetValue<string>();
            }
            while (proximo is not null);
            return total;
        }

        await EnsaioDeIa.AguardarAsync(async () => await TotalNoHistoricoAsync() == 131, "as 131 mensagens deveriam estar no histórico", segundos: 90);

        async Task<JsonNode> PaginaAsync(string? cursor, int take) =>
            await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}/messages?take={take}&order=NewestFirst" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));

        // A primeira página (como a tela a lê): as 100 mais recentes, do mais novo para o mais antigo, com o cursor das mais antigas.
        var primeira = await PaginaAsync(null, 100);
        var vistas = primeira["items"]!.AsArray().Select(m => (Id: m!["id"]!.GetValue<Guid>(), Seq: m["messageSeq"]!.GetValue<long>())).ToList();
        Assert.Equal(100, vistas.Count);
        var cursor = primeira["nextCursor"]?.GetValue<string>();
        Assert.NotNull(cursor);

        // Chega uma mensagem NOVA entre as páginas: o cursor das mais antigas não muda de lugar (nada é repetido nem pulado).
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.HIST9999", cliente, "Historia", "mensagem nova no meio da leitura")));
        await EnsaioDeIa.AguardarAsync(async () => await TotalNoHistoricoAsync() == 132, "a mensagem nova deveria chegar ao histórico");

        // As páginas antigas (50 por vez, como o botão da tela) seguem o cursor até acabar.
        var paginas = 0;
        while (cursor is not null)
        {
            var pagina = await PaginaAsync(cursor, 50);
            vistas.AddRange(pagina["items"]!.AsArray().Select(m => (Id: m!["id"]!.GetValue<Guid>(), Seq: m["messageSeq"]!.GetValue<long>())));
            cursor = pagina["nextCursor"]?.GetValue<string>();
            paginas++;
            Assert.InRange(paginas, 1, 5);
        }

        Assert.Equal(131, vistas.Count); // as 131 de antes da mensagem nova: nenhuma repetida, nenhuma perdida
        Assert.Equal(131, vistas.Select(v => v.Id).Distinct().Count());
        // A ordem das páginas é a do histórico (instante e sequência); a tela reordena por sequência. O que importa aqui: todas as sequências aparecem, uma vez cada.
        Assert.Equal(Enumerable.Range(1, 131).Select(i => (long)i), vistas.Select(v => v.Seq).Order());
        Assert.Equal(1, paginas); // as 31 que restavam cabem numa página de 50, e a última não traz cursor

        // A leitura nova da primeira página (a releitura periódica da tela) já traz a mensagem nova no topo.
        var atual = await PaginaAsync(null, 100);
        var nova = Assert.Single(atual["items"]!.AsArray(), m => m!["text"]!.GetValue<string>() == "mensagem nova no meio da leitura");
        Assert.Equal(132, nova!["messageSeq"]!.GetValue<long>());
    }

    [Fact]
    public async Task O_atendente_comum_ve_a_conta_pausada_so_com_o_necessario_e_o_aviso_some_na_retomada()
    {
        await using var e = await IniciarAsync("ccalerta");
        var (_, atendente) = await e.NovoAtendenteAsync();
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);

        Assert.Empty((await e.Ia.Api.LerAsync<JsonNode>(atendente, "/channels/sending-alerts")).AsArray());
        Assert.Equal(HttpStatusCode.Unauthorized, await e.Ia.Api.StatusAsync(null, HttpMethod.Get, "/channels/sending-alerts"));

        // O atendente comum não administra nada (para ele a conta nem existe: TEN-2): a pausa é do administrador, e só o aviso chega ao atendente.
        Assert.Equal(HttpStatusCode.NotFound, await e.Ia.Api.StatusAsync(atendente, HttpMethod.Post, $"/channels/accounts/{e.ContaId}/sending/pause"));
        Assert.Equal(HttpStatusCode.OK, (await e.ChamarAsync(admin, HttpMethod.Post, $"/channels/accounts/{e.ContaId}/sending/pause")).Status);

        var alertas = (await e.Ia.Api.LerAsync<JsonNode>(atendente, "/channels/sending-alerts")).AsArray();
        var alerta = Assert.Single(alertas)!;
        Assert.Equal(e.ContaId, alerta["accountId"]!.GetValue<Guid>());
        Assert.True(alerta["paused"]!.GetValue<bool>());
        Assert.Contains(e.InstanciaId, alerta["channelInstanceIds"]!.AsArray().Select(x => x!.GetValue<Guid>()));

        // Só o necessário: nem o motivo técnico da pausa, nem a vazão, nem credencial.
        var texto = alerta.ToJsonString();
        Assert.DoesNotContain("pauseReason", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("throughput", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(EnsaioDeWhatsApp.TokenDeAcesso, texto, StringComparison.Ordinal);

        // A retomada (com a prova do provedor) apaga o aviso.
        e.Graph.RoteiroDeLeitura = _ => ServidorGraphLoopback.NumeroValido(e.NumeroDoCanal);
        Assert.Equal(HttpStatusCode.OK, (await e.ChamarAsync(admin, HttpMethod.Post, $"/channels/accounts/{e.ContaId}/sending/resume")).Status);
        Assert.Empty((await e.Ia.Api.LerAsync<JsonNode>(atendente, "/channels/sending-alerts")).AsArray());
    }
}
