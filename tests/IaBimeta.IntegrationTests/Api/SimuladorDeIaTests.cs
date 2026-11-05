using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.ContactCenter.Application;
using IaBimeta.IntegrationTests.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O ensaio geral do primeiro teste de IA pelo simulador: o Api e o Worker REAIS, PostgreSQL, AiGateway com a chave BYOK cifrada, o adaptador REAL da OpenAI e um "servidor da OpenAI" local (laço de
/// rede 127.0.0.1) no lugar da OpenAI. Prova, sem nenhuma chamada paga nem chave real, tudo o que o desenvolvedor vai ver quando trocar o servidor local pela OpenAI: a preparação do tenant, o cadastro
/// da chave sem exposição, mensagem para o Agents e para o modelo, resposta ou handoff humano, tokens e custo no ledger, e o comportamento nas falhas e depois que o humano assume.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SimuladorDeIaTests(PostgresFixture banco)
{
    private static readonly TimeSpan JanelaSemResposta = TimeSpan.FromSeconds(2.5);

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Responde(string texto, int entrada = 200, int cache = 50, int saida = 20, string requestId = "req_ensaio") =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder(texto), entrada, cache, saida, requestId));

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere(string motivo) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir(motivo), 150, 0, 15, "req_handoff"));

    private static Task AguardarSessaoAsync(EnsaioDeIa e, Guid sessao, string modo) =>
        EnsaioDeIa.AguardarAsync(async () => (await e.SessaoAsync(sessao))["session"]!["handlingMode"]!.GetValue<string>() == modo, $"a sessão deveria ficar em {modo}");

    [Fact]
    public async Task A_IA_de_desenvolvimento_so_pode_ser_ligada_no_ambiente_Development()
    {
        // Como os demais recursos de desenvolvimento: ligada fora de Development, a partida falha (nenhum mecanismo de teste local vaza para produção).
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Tenancy:Development:Ai:Enabled"] = "true";

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiServer.StartAsync("Production", configuracao));

        Assert.Contains("Development", erro.Message);
    }

    // Preparação do tenant

    [Fact]
    public async Task A_preparacao_deixa_organizacao_fila_agente_e_IA_do_tenant_prontos_e_e_idempotente()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiopre");

        var status = await e.StatusAsync();

        Assert.True(status["ai"]!["configured"]!.GetValue<bool>());
        Assert.Equal(("openai", EnsaioDeIa.ModeloDoEnsaio, "Enabled"), (status["ai"]!["providerId"]!.GetValue<string>(), status["ai"]!["modelId"]!.GetValue<string>(), status["ai"]!["status"]!.GetValue<string>()));
        Assert.False(status["ai"]!["credentialPresent"]!.GetValue<bool>()); // a chave nunca vem da configuração: é cadastrada à parte
        Assert.NotNull(status["organizationId"]);
        Assert.NotNull(status["queueId"]);
        Assert.Equal(1, status["agent"]!["publishedVersion"]!.GetValue<int>());
        Assert.Contains(status["catalog"]!.AsArray(), m => m!["modelId"]!.GetValue<string>() == EnsaioDeIa.ModeloDoEnsaio);

        // O handoff precisa de uma fila que a regra de roteamento escolha: a preparação criou as duas.
        var regras = await e.ComoSistemaAsync(sp => sp.GetRequiredService<RoutingRuleService>().ListAsync());
        var regra = Assert.Single(regras);
        Assert.Equal(status["queueId"]!.GetValue<Guid>(), regra.QueueId);

        // Reiniciar o Api (o desenvolvedor sobe de novo) não duplica organização, fila, regra nem definição do agente.
        await using var segundo = await e.SegundoApiAsync();
        using var resposta = await segundo.Client.SendAsync(segundo.Requisicao(HttpMethod.Get, "/simulator/ai/status", e.Slug));
        var depois = JsonNode.Parse(await resposta.Content.ReadAsStringAsync())!;
        Assert.Equal(status["organizationId"]!.GetValue<Guid>(), depois["organizationId"]!.GetValue<Guid>());
        Assert.Equal(status["queueId"]!.GetValue<Guid>(), depois["queueId"]!.GetValue<Guid>());
        Assert.Equal(1, depois["agent"]!["publishedVersion"]!.GetValue<int>());
        Assert.Single(await e.ComoSistemaAsync(sp => sp.GetRequiredService<RoutingRuleService>().ListAsync()));
        Assert.Single(await e.ComoSistemaAsync(sp => sp.GetRequiredService<ServiceQueueService>().ListAsync()));
    }

    [Fact]
    public async Task Sem_modelo_configurado_o_tenant_fica_sem_IA_e_o_log_diz_o_que_fazer_sem_inventar_um_modelo()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiosem", modelo: null);

        var status = await e.StatusAsync();

        Assert.False(status["ai"]!["configured"]!.GetValue<bool>());
        Assert.NotNull(status["organizationId"]); // o resto da preparação seguiu
        Assert.Contains(e.Logs.Linhas, l => l.Contains("Tenancy:Development:Ai:ModelId") && l.Contains("NÃO foi configurada"));
    }

    // A chave BYOK

    [Fact]
    public async Task A_chave_e_cadastrada_cifrada_e_nunca_aparece_em_resposta_log_nem_banco()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiochave");

        var (status, corpo) = await e.CadastrarChaveAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        var visao = JsonNode.Parse(corpo)!;
        Assert.True(visao["credentialPresent"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(visao["credentialFingerprint"]?.GetValue<string>()));
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, corpo);
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, (await e.StatusAsync()).ToJsonString());

        // No banco só há o texto cifrado (AES-256-GCM) e a impressão digital: a chave em claro não existe.
        var credenciais = await e.ComoSistemaAsync(sp => sp.GetRequiredService<AiGatewayDbContext>().Credentials.AsNoTracking().ToListAsync());
        var guardada = Assert.Single(credenciais);
        Assert.False(string.IsNullOrEmpty(guardada.Ciphertext));
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, guardada.Ciphertext);
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, guardada.Fingerprint);

        // Trocar a chave mantém uma só credencial ativa; revogar apaga o texto cifrado.
        Assert.Equal(HttpStatusCode.OK, (await e.CadastrarChaveAsync(EnsaioDeIa.ChaveCanario + "-nova")).Status);
        using (var revogacao = await e.Api.Client.SendAsync(e.Api.Requisicao(HttpMethod.Delete, "/simulator/ai/credential", e.Slug)))
        {
            Assert.True(JsonNode.Parse(await revogacao.Content.ReadAsStringAsync())!["revoked"]!.GetValue<bool>());
        }

        Assert.False((await e.StatusAsync())["ai"]!["credentialPresent"]!.GetValue<bool>());
        Assert.DoesNotContain(e.Logs.Linhas, l => l.Contains(EnsaioDeIa.ChaveCanario));
    }

    [Fact]
    public async Task Sem_a_chave_mestra_o_cadastro_responde_503_e_nada_e_guardado()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiomestra", comChaveMestra: false);

        var (status, corpo) = await e.CadastrarChaveAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, corpo);
        Assert.False((await e.StatusAsync())["ai"]!["credentialPresent"]!.GetValue<bool>());
        Assert.Empty(await e.ComoSistemaAsync(sp => sp.GetRequiredService<AiGatewayDbContext>().Credentials.AsNoTracking().ToListAsync()));
    }

    // O fluxo: mensagem recebida, Agents, OpenAI, resposta

    [Fact]
    public async Task Fluxo_completo_a_mensagem_chega_ao_modelo_a_resposta_volta_e_tokens_e_custo_vao_para_o_ledger()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiofluxo");
        e.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Olá! Em que posso ajudar?"));
        await e.CadastrarChaveAsync();

        var sessao = await e.EntrarAsync("mauro", "Olá, tudo bem?");
        await EnsaioDeIa.AguardarAsync(async () => (await e.RespostasDaIAAsync(sessao)).Count == 1, "a resposta da IA deveria aparecer no histórico");

        Assert.Equal("Olá! Em que posso ajudar?", Assert.Single(await e.RespostasDaIAAsync(sessao)));

        // O pedido à "OpenAI": a chave BYOK só no cabeçalho, o modelo escolhido, estado não guardado, teto de saída imposto e a mensagem do cliente.
        var captura = Assert.Single(e.OpenAi.Roteiros.Capturas);
        Assert.Equal(EnsaioDeIa.ChaveCanario, captura.Credencial);
        Assert.EndsWith("/v1/responses", captura.Url);
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, captura.Corpo);
        var corpoDoPedido = captura.Json;
        Assert.Equal((EnsaioDeIa.ModeloDoEnsaio, false, 600), ((string)corpoDoPedido["model"]!, (bool)corpoDoPedido["store"]!, (int)corpoDoPedido["max_output_tokens"]!));
        Assert.Contains("ia.bimeta", (string)corpoDoPedido["instructions"]!);
        var mensagens = corpoDoPedido["input"]!.AsArray();
        Assert.Contains("Olá, tudo bem?", (string)mensagens[mensagens.Count - 1]!["content"]!);

        // O ledger: o consumo REAL informado, o custo ESTIMADO pela tabela do catálogo e o resultado. Entrada comum 150 x 1,00 + cache 50 x 0,10 + saída 20 x 4,00 (por milhão).
        var ledger = await e.LedgerAsync();
        var linha = Assert.Single(ledger.AsArray())!;
        Assert.Equal(("Confirmed", "Succeeded", "Reported", "openai", EnsaioDeIa.ModeloDoEnsaio), (
            linha["state"]!.GetValue<string>(), linha["outcome"]!.GetValue<string>(), linha["certainty"]!.GetValue<string>(), linha["providerId"]!.GetValue<string>(), linha["modelId"]!.GetValue<string>()));
        Assert.Equal((200, 50, 20), (linha["inputTokens"]!.GetValue<int>(), linha["cachedInputTokens"]!.GetValue<int>(), linha["outputTokens"]!.GetValue<int>()));
        Assert.Equal("req_ensaio", linha["providerRequestId"]!.GetValue<string>());
        Assert.Equal(0.000235m, linha["estimatedCost"]!.GetValue<decimal>());
        Assert.Equal("USD", linha["currency"]!.GetValue<string>());

        // O turno terminou limpo, e nenhuma resposta do simulador carrega a chave.
        var turno = Assert.Single((await e.TurnosAsync()).AsArray())!;
        Assert.Equal("Completed", turno["state"]!.GetValue<string>());
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, ledger.ToJsonString());
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, (await e.SessaoAsync(sessao)).ToJsonString());
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, (await e.StatusAsync()).ToJsonString());
        Assert.DoesNotContain(e.Logs.Linhas, l => l.Contains(EnsaioDeIa.ChaveCanario));
    }

    [Fact]
    public async Task O_modelo_decide_transferir_o_cliente_e_avisado_o_item_entra_na_fila_e_a_IA_para()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiohandoff");
        e.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere("price"));
        await e.CadastrarChaveAsync();

        var sessao = await e.EntrarAsync("mauro", "Quanto custa o hemograma?");
        await AguardarSessaoAsync(e, sessao, "HumanAssisted");

        Assert.Equal("Vou transferir você para um atendente. Um momento, por favor.", Assert.Single(await e.RespostasDaIAAsync(sessao)));
        var fila = (await e.StatusAsync())["queueId"]!.GetValue<Guid>();
        var itens = await e.ComoSistemaAsync(sp => sp.GetRequiredService<QueueItemService>().ListAsync(null, null, false, 50));
        var item = Assert.Single(itens);
        Assert.Equal((fila, sessao), (item.QueueId, item.SessionId));
        var linha = Assert.Single((await e.LedgerAsync()).AsArray())!;
        Assert.Equal(("Confirmed", 150, 15), (linha["state"]!.GetValue<string>(), linha["inputTokens"]!.GetValue<int>(), linha["outputTokens"]!.GetValue<int>()));
        Assert.Single(e.OpenAi.Roteiros.Capturas);
    }

    // Depois que o humano assume

    [Fact]
    public async Task Depois_que_o_humano_assume_a_IA_nao_responde_nem_chama_o_modelo()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiohumano");
        e.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde("Primeira resposta da IA."));
        await e.CadastrarChaveAsync();
        var sessao = await e.EntrarAsync("mauro", "Oi");
        await EnsaioDeIa.AguardarAsync(async () => (await e.RespostasDaIAAsync(sessao)).Count == 1, "a primeira resposta da IA");
        Assert.Single(e.OpenAi.Roteiros.Capturas);

        await e.AssumirComoHumanoAsync(sessao);
        await e.EntrarAsync("mauro", "Alguém aí?");
        await Task.Delay(JanelaSemResposta);

        // O cliente falou de novo, mas quem atende é o humano: a IA não respondeu, o modelo não foi chamado e nenhum turno ficou aberto.
        Assert.Equal(new[] { "Primeira resposta da IA." }, await e.RespostasDaIAAsync(sessao));
        Assert.Single(e.OpenAi.Roteiros.Capturas);
        Assert.Single((await e.LedgerAsync()).AsArray());
        Assert.All((await e.TurnosAsync()).AsArray(), t => Assert.DoesNotContain(t!["state"]!.GetValue<string>(), new[] { "Scheduled", "Ready", "Running" }));
    }

    [Fact]
    public async Task Se_o_humano_assume_durante_a_chamada_ao_modelo_a_resposta_tardia_e_descartada()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiotarde");
        var chamou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        e.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, async (_, ct) =>
        {
            chamou.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            return ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta tardia que ninguém deve ver."), 120, 0, 30, "req_tarde");
        });
        await e.CadastrarChaveAsync();
        var sessao = await e.EntrarAsync("mauro", "Preciso de ajuda");
        await chamou.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await e.AssumirComoHumanoAsync(sessao);

        // A chamada termina (o consumo é registrado: o provedor cobrou), mas a resposta é recusada: o responsável já não é a IA.
        await EnsaioDeIa.AguardarAsync(async () => (await e.LedgerAsync()).AsArray().Any(l => l!["state"]!.GetValue<string>() == "Confirmed"), "o ledger deveria registrar a chamada concluída");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(await e.RespostasDaIAAsync(sessao));
        Assert.Single(e.OpenAi.Roteiros.Capturas);
    }

    // Falhas

    [Fact]
    public async Task Sem_chave_cadastrada_o_modelo_nao_e_chamado_e_o_atendimento_vai_para_o_humano()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiosemchave");

        var sessao = await e.EntrarAsync("mauro", "Oi");
        await AguardarSessaoAsync(e, sessao, "HumanAssisted");

        Assert.Empty(e.OpenAi.Roteiros.Capturas); // nenhuma chamada: nada a cobrar
        Assert.Empty((await e.LedgerAsync()).AsArray());
        var turno = Assert.Single((await e.TurnosAsync()).AsArray())!;
        Assert.Equal("ai-processing-failed", turno["handoffReason"]!.GetValue<string>());
        Assert.Single(await e.ComoSistemaAsync(sp => sp.GetRequiredService<QueueItemService>().ListAsync(null, null, false, 50)));
    }

    [Fact]
    public async Task Chave_recusada_pela_OpenAI_nao_gera_custo_nem_nova_tentativa_e_o_atendimento_vai_para_o_humano()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaiorecusa");
        e.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, (_, _) => Task.FromResult(ServidorOpenAiSimulado.Json(HttpStatusCode.Unauthorized, ServidorOpenAiSimulado.Erro("invalid_api_key"))));
        await e.CadastrarChaveAsync();

        var sessao = await e.EntrarAsync("mauro", "Oi");
        await AguardarSessaoAsync(e, sessao, "HumanAssisted");

        Assert.Single(e.OpenAi.Roteiros.Capturas); // falha permanente: não se tenta de novo
        var linha = Assert.Single((await e.LedgerAsync()).AsArray())!;
        Assert.Equal(("Confirmed", "Failed", "InvalidCredentials", "NoneIncurred", 0, 0), (
            linha["state"]!.GetValue<string>(), linha["outcome"]!.GetValue<string>(), linha["errorKind"]!.GetValue<string>(), linha["certainty"]!.GetValue<string>(),
            linha["inputTokens"]!.GetValue<int>(), linha["outputTokens"]!.GetValue<int>()));
        Assert.Equal(0m, linha["estimatedCost"]!.GetValue<decimal>());
        Assert.Equal("ai-processing-failed", Assert.Single((await e.TurnosAsync()).AsArray())!["handoffReason"]!.GetValue<string>());
        Assert.DoesNotContain(e.Logs.Linhas, l => l.Contains(EnsaioDeIa.ChaveCanario));
    }

    [Fact]
    public async Task Erro_5xx_da_OpenAI_deixa_o_consumo_indeterminado_tenta_de_novo_e_a_resposta_confirma()
    {
        await using var e = await EnsaioDeIa.IniciarAsync(banco, "ensaio5xx");
        var chamadas = 0;
        e.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, (captura, ct) => Interlocked.Increment(ref chamadas) == 1
            ? Task.FromResult(ServidorOpenAiSimulado.Json(HttpStatusCode.ServiceUnavailable, ServidorOpenAiSimulado.Erro("service_unavailable")))
            : Responde("Agora foi.")(captura, ct));
        await e.CadastrarChaveAsync();

        var sessao = await e.EntrarAsync("mauro", "Oi");
        await EnsaioDeIa.AguardarAsync(async () => (await e.RespostasDaIAAsync(sessao)).Count == 1, "a resposta depois da nova tentativa");

        Assert.Equal("Agora foi.", Assert.Single(await e.RespostasDaIAAsync(sessao)));
        Assert.Equal(2, e.OpenAi.Roteiros.Capturas.Count);
        var estados = (await e.LedgerAsync()).AsArray().Select(l => l!["state"]!.GetValue<string>()).Order().ToArray();
        Assert.Equal(new[] { "Confirmed", "Indeterminate" }, estados); // 5xx nunca presume "nada cobrado": a primeira fica indeterminada
    }
}
