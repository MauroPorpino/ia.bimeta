using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Agents;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O Sender de ponta a ponta contra a Graph API simulada (HTTP de verdade, Api e Worker reais, PostgreSQL real): a vazão por conta (SND-2, balde de fichas no banco), o limite de taxa do provedor (HTTP 429 com
/// <c>Retry-After</c>: a conta TODA espera, nenhuma mensagem é perdida) e a pausa da conta por erro de conta (SND-6: as próximas mensagens falham sem tocar no provedor, e a retomada exige a prova de que o provedor aceita
/// a credencial). Nenhuma mensagem real, nenhuma credencial real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PausaEVazaoWhatsAppTests(PostgresFixture banco)
{
    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Responde(string texto) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder(texto), 150, 0, 25, "req_snd"));

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, IReadOnlyDictionary<string, string?>? configuracao = null)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, configuracao);
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Resposta do ensaio de envio."));
        return e;
    }

    private static async Task<Guid> ClienteAsync(EnsaioDeWhatsApp e, string nome, string waId, string idDaMensagem)
    {
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto(idDaMensagem, waId, nome, "Olá, preciso de ajuda.")));
        return await e.EsperarSessaoAsync(nome);
    }

    private static string Caminho(EnsaioDeWhatsApp e, string final) => $"/channels/accounts/{e.ContaId}/{final}";

    // SND-6: a pausa da conta

    [Fact]
    public async Task Erro_de_conta_pausa_a_conta_as_proximas_mensagens_falham_sem_tocar_na_Graph_e_a_retomada_exige_a_prova_do_provedor()
    {
        await using var e = await IniciarAsync("sndpausa");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        e.Graph.Roteiro = _ => ServidorGraphLoopback.Erro(401, 190); // token expirado

        // O primeiro envio recebe o erro da CONTA: a mensagem falha e a conta é pausada.
        var a = await ClienteAsync(e, "Pausa A", "5511999990011", "wamid.PAUSA0001");
        await e.EsperarEntregaAsync(a, "Failed");
        var (status, estado) = await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "sending"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(estado!["paused"]!.GetValue<bool>());
        Assert.Equal("account-error.190", estado["pauseReason"]!.GetValue<string>());
        Assert.Single(e.Graph.Capturas);

        // Pausada, a próxima mensagem falha na hora (account-paused) e NÃO chega à Graph API.
        var b = await ClienteAsync(e, "Pausa B", "5511999990012", "wamid.PAUSA0002");
        await e.EsperarEntregaAsync(b, "Failed");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Single(e.Graph.Capturas);

        // Retomada sem prova: o provedor ainda recusa o token, então a conta continua pausada (409) e nenhuma mensagem sai.
        e.Graph.RoteiroDeLeitura = _ => ServidorGraphLoopback.Erro(401, 190);
        var (recusada, _) = await e.ChamarAsync(admin, HttpMethod.Post, Caminho(e, "sending/resume"));
        Assert.Equal(HttpStatusCode.Conflict, recusada);
        Assert.True((await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "sending"))).Corpo!["paused"]!.GetValue<bool>());
        Assert.Single(e.Graph.Leituras);
        Assert.Single(e.Graph.Capturas);

        // Retomada com a prova: o provedor aceita o token e confirma o número. Só LEITURA: nenhuma mensagem sai.
        e.Graph.RoteiroDeLeitura = _ => ServidorGraphLoopback.NumeroValido(e.NumeroDoCanal);
        var (retomada, corpo) = await e.ChamarAsync(admin, HttpMethod.Post, Caminho(e, "sending/resume"));
        Assert.Equal(HttpStatusCode.OK, retomada);
        Assert.False(corpo!["paused"]!.GetValue<bool>());
        Assert.Equal(2, e.Graph.Leituras.Count);
        Assert.All(e.Graph.Leituras, l =>
        {
            Assert.Equal("GET", l.Metodo);
            Assert.Equal(EnsaioDeWhatsApp.TokenDeAcesso, l.Credencial);
            Assert.EndsWith("/" + e.NumeroDoCanal, l.Caminho, StringComparison.Ordinal);
        });
        Assert.Single(e.Graph.Capturas);

        // Retomada concluída: a conta volta a enviar.
        e.Graph.Roteiro = _ => e.Graph.Aceita();
        var c = await ClienteAsync(e, "Pausa C", "5511999990013", "wamid.PAUSA0003");
        await e.EsperarEntregaAsync(c, "Sent");
        Assert.Equal(2, e.Graph.Capturas.Count);
    }

    [Fact]
    public async Task O_administrador_pausa_a_conta_por_decisao_propria_e_nada_sai_ate_retomar()
    {
        await using var e = await IniciarAsync("sndmanual");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);

        var (status, pausada) = await e.ChamarAsync(admin, HttpMethod.Post, Caminho(e, "sending/pause"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("manual", pausada!["pauseReason"]!.GetValue<string>());

        var a = await ClienteAsync(e, "Manual A", "5511999990021", "wamid.MANUAL0001");
        await e.EsperarEntregaAsync(a, "Failed");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(e.Graph.Capturas);

        var (retomada, _) = await e.ChamarAsync(admin, HttpMethod.Post, Caminho(e, "sending/resume"));
        Assert.Equal(HttpStatusCode.OK, retomada);

        var b = await ClienteAsync(e, "Manual B", "5511999990022", "wamid.MANUAL0002");
        await e.EsperarEntregaAsync(b, "Sent");
        Assert.Single(e.Graph.Capturas);
    }

    // SND-2: o limite de taxa do provedor e a vazão por conta

    [Fact]
    public async Task Limite_de_taxa_do_provedor_com_Retry_After_faz_a_conta_toda_esperar_e_nenhuma_mensagem_e_perdida()
    {
        await using var e = await IniciarAsync("sndtaxa");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        var chamadas = 0;
        e.Graph.RoteiroComEspera = _ => Interlocked.Increment(ref chamadas) == 1
            ? (429, ServidorGraphLoopback.Erro(429, 130429).Corpo, 3)
            : (200, e.Graph.Aceita().Corpo, null);

        var a = await ClienteAsync(e, "Taxa A", "5511999990031", "wamid.TAXA0001");
        await EnsaioDeIa.AguardarAsync(() => Task.FromResult(e.Graph.Capturas.Count >= 1), "a primeira chamada deveria chegar à Graph");

        // A espera imposta pelo provedor está gravada na conta (e vale para todas as conversas).
        await e.EsperarAsync(async () => (await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "sending"))).Corpo!["cooldownUntil"] is not null, "a espera do provedor deveria estar gravada na conta");

        // Outra conversa, durante a espera: a mensagem NÃO é enviada antes do fim dela.
        var b = await ClienteAsync(e, "Taxa B", "5511999990032", "wamid.TAXA0002");

        await e.EsperarEntregaAsync(a, "Sent");
        await e.EsperarEntregaAsync(b, "Sent");

        var envios = e.Graph.Capturas.OrderBy(c => c.Quando).ToList();
        Assert.Equal(3, envios.Count); // 429 de A, depois A de novo e B: nenhuma perdida, nenhuma duplicada
        var recusa = envios[0].Quando!.Value;
        Assert.True(
            (envios[1].Quando!.Value - recusa).TotalSeconds >= 2.5 && (envios[2].Quando!.Value - recusa).TotalSeconds >= 2.5,
            $"nenhum envio da conta deveria sair antes do fim da espera (Retry-After de 3 s): {(envios[1].Quando!.Value - recusa).TotalSeconds:F2} s e {(envios[2].Quando!.Value - recusa).TotalSeconds:F2} s");
    }

    [Fact]
    public async Task O_balde_da_conta_espaca_os_envios_pela_vazao_configurada_e_nenhuma_mensagem_e_perdida()
    {
        await using var e = await IniciarAsync("sndvazao");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        var (status, visao) = await e.ChamarAsync(admin, HttpMethod.Put, Caminho(e, "sending/throughput"), new JsonObject { ["messagesPerSecond"] = 1 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, visao!["throughputPerSecond"]!.GetValue<int>());
        Assert.False(visao["throughputIsDefault"]!.GetValue<bool>());

        // Cinco conversas ao mesmo tempo, vazão de 1 mensagem por segundo.
        var sessoes = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => ClienteAsync(e, $"Vazao {i}", $"551199999004{i}", $"wamid.VAZAO000{i}")));
        await Task.WhenAll(sessoes.Select(s => e.EsperarEntregaAsync(s, "Sent", segundos: 60)));

        var momentos = e.Graph.Capturas.Select(c => c.Quando!.Value).Order().ToList();
        Assert.Equal(5, momentos.Count);
        for (var i = 1; i < momentos.Count; i++)
        {
            // O balde libera uma ficha por segundo; a folga cobre a variação do tempo entre a ficha e a chegada da chamada ao servidor simulado.
            Assert.True((momentos[i] - momentos[i - 1]).TotalSeconds >= 0.6, $"o envio {i + 1} saiu {(momentos[i] - momentos[i - 1]).TotalSeconds:F2} s depois do anterior (vazão de 1 por segundo)");
        }

        Assert.True((momentos[^1] - momentos[0]).TotalSeconds >= 3.0);
    }

    [Fact]
    public async Task Limite_de_taxa_do_par_remetente_destinatario_tenta_de_novo_sem_pausar_nem_impor_espera_a_conta()
    {
        await using var e = await IniciarAsync("sndpar");
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        var chamadas = 0;
        e.Graph.Roteiro = _ => Interlocked.Increment(ref chamadas) == 1 ? ServidorGraphLoopback.Erro(400, 131056) : e.Graph.Aceita();

        var a = await ClienteAsync(e, "Par A", "5511999990051", "wamid.PAR0001");
        await e.EsperarEntregaAsync(a, "Sent");

        // O limite do par vale só para o par: a conta não ganha espera, e nada foi pausado.
        var (_, estado) = await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "sending"));
        Assert.False(estado!["paused"]!.GetValue<bool>());
        Assert.Null(estado["cooldownUntil"]);
        Assert.Equal(2, e.Graph.Capturas.Count);
    }

    [Fact]
    public async Task Quem_so_le_canais_nao_pausa_nem_retoma_nem_ajusta_a_vazao_e_outro_tenant_nao_enxerga_a_conta()
    {
        await using var e = await IniciarAsync("sndacesso");
        var leitor = await e.TokenDeAsync(Role.Manager);

        // O gestor LÊ o estado, mas não gere: as ações são negadas (403).
        Assert.Equal(HttpStatusCode.OK, (await e.ChamarAsync(leitor, HttpMethod.Get, Caminho(e, "sending"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.ChamarAsync(leitor, HttpMethod.Post, Caminho(e, "sending/pause"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.ChamarAsync(leitor, HttpMethod.Post, Caminho(e, "sending/resume"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.ChamarAsync(leitor, HttpMethod.Put, Caminho(e, "sending/throughput"), new JsonObject { ["messagesPerSecond"] = 5 })).Status);

        // Sem sessão: 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.ChamarAsync(string.Empty, HttpMethod.Get, Caminho(e, "sending"))).Status);

        // Vazão fora de 1 a 1000: dado inválido (400), nada muda.
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await e.ChamarAsync(admin, HttpMethod.Put, Caminho(e, "sending/throughput"), new JsonObject { ["messagesPerSecond"] = 0 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await e.ChamarAsync(admin, HttpMethod.Put, Caminho(e, "sending/throughput"), new JsonObject { ["messagesPerSecond"] = 1001 })).Status);
        Assert.True((await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "sending"))).Corpo!["throughputIsDefault"]!.GetValue<bool>());

        // Outro tenant: a conta responde como inexistente (404), em todas as rotas.
        var outro = await banco.RegistrarTenantAsync("sndoutro");
        var organizacaoDoOutro = await banco.CriarOrganizacaoAsync(outro, "Outra organização");
        var usuarioDoOutro = await banco.CriarUsuarioAsync(outro, organizacaoDoOutro.Id, Role.OrganizationAdmin);
        var tokenDoOutro = await e.Ia.Api.LoginAsync(usuarioDoOutro);
        Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(tokenDoOutro, HttpMethod.Get, Caminho(e, "sending"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(tokenDoOutro, HttpMethod.Post, Caminho(e, "sending/pause"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(tokenDoOutro, HttpMethod.Post, Caminho(e, "sending/resume"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await e.ChamarAsync(tokenDoOutro, HttpMethod.Get, Caminho(e, "readiness?live=true"))).Status);
        Assert.Empty(e.Graph.Leituras);
    }
}
