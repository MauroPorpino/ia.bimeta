using System.Net;
using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.ContactCenter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// O primeiro fluxo funcional (C4): mensagem do cliente, turno do Agents, AiGateway REAL, adaptador REAL da OpenAI (o pedido HTTP, o uso, os erros) e o servidor da OpenAI SIMULADO por HTTP, ledger e
/// atendimento humano, tudo com PostgreSQL. Nenhuma rede e nenhuma chave real: a credencial BYOK de cada tenant é um canário sintético, e o consumo registrado é o que a "OpenAI" informou.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentsOpenAiEndToEndTests(PostgresFixture banco)
{
    private static Task<List<AiRequest>> Ledger(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync());

    private static Task<AiUsageWindow> Janela(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AiGatewayDbContext>().Windows.AsNoTracking().SingleAsync());

    [Fact]
    public async Task Fluxo_completo_o_cliente_pergunta_a_OpenAI_responde_e_o_consumo_real_vai_para_o_ledger()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai1", usarOpenAi: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("Em geral não é necessário jejum para o hemograma."), entrada: 150, cache: 40, saida: 25, requestId: "req_abc"));
        var entrada = await a.EntrarAsync("mauro", "Preciso de jejum para o hemograma?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);
        Assert.Equal("Em geral não é necessário jejum para o hemograma.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);

        // O pedido que foi à OpenAI: Responses API, a credencial BYOK DESTE tenant só no cabeçalho, estado não guardado, esforço do perfil, saída estruturada estrita e o teto de saída imposto na requisição.
        var http = Assert.Single(a.RequisicoesHttp);
        Assert.Equal("https://api.openai.com/v1/responses", http.Url);
        Assert.NotNull(http.ClientRequestId);
        var corpo = http.Json;
        Assert.Equal(("gpt-6-luna", false, "none"), ((string)corpo["model"]!, (bool)corpo["store"]!, (string)corpo["reasoning"]!["effort"]!));
        Assert.Equal(("json_schema", "agent_decision", true), ((string)corpo["text"]!["format"]!["type"]!, (string)corpo["text"]!["format"]!["name"]!, (bool)corpo["text"]!["format"]!["strict"]!));
        Assert.Contains(a.CanarioDasInstrucoes, (string)corpo["instructions"]!);
        var mensagens = corpo["input"]!.AsArray();
        Assert.Contains("Preciso de jejum para o hemograma?", (string)mensagens[mensagens.Count - 1]!["content"]!);
        Assert.DoesNotContain(a.Canario, http.Corpo);

        // O Agents pediu 600 tokens de saída; o limite do tenant no AiGateway é 500: é o 500 que vai na requisição (o teto é imposto, não sugerido).
        Assert.Equal(500, (int)corpo["max_output_tokens"]!);

        // O ledger registra o consumo REAL informado pela OpenAI: entrada total (com o cache), saída e a certeza Reported. Nada de prompt nem de resposta.
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Confirmed, "openai", "gpt-6-luna", 175L), (registro.State, registro.ProviderId, registro.ModelId, registro.AppliedTokens));
        Assert.Equal((150, 40, 25), (registro.InputTokens, registro.CachedInputTokens, registro.OutputTokens));
        Assert.Equal(("req_abc", AiUsageCertainty.Reported), (registro.ProviderRequestId, registro.UsageCertainty));
        Assert.True(registro.EstimatedCost > 0m);
        var janela = await Janela(a);
        Assert.Equal((1, 175L, 0L), (janela.Calls, janela.ConfirmedTokens, janela.IndeterminateTokens));
    }

    [Fact]
    public async Task O_modelo_decide_transferir_e_o_atendimento_humano_assume_com_o_cliente_avisado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai2", usarOpenAi: true);
        var fila = await a.FilaDeAtendimentoAsync();
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir("price")));
        var entrada = await a.EntrarAsync("mauro", "Quanto custa o hemograma?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        // WhatsApp (simulado) para OpenAI para o atendimento humano: o item entrou na fila, o cliente foi avisado (a fila aceita) e a IA deixou de responder.
        Assert.Equal(TurnRunOutcome.HandedOff, resultado.Outcome);
        var item = Assert.Single(await a.ItensDaFilaAsync());
        Assert.Equal((fila, entrada.SessionId), (item.QueueId, item.SessionId));
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
        Assert.Equal("Vou transferir você para um atendente. Um momento, por favor.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
        Assert.Single(a.RequisicoesHttp);
    }

    [Fact]
    public async Task Erro_5xx_da_OpenAI_deixa_o_consumo_indeterminado_cobra_a_reserva_tenta_de_novo_e_o_sucesso_confirma()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai3", usarOpenAi: true);
        var chamadas = 0;
        a.RoteiroHttp = (_, _) => Task.FromResult(Interlocked.Increment(ref chamadas) == 1
            ? ServidorOpenAiSimulado.Json(HttpStatusCode.ServiceUnavailable, ServidorOpenAiSimulado.Erro("service_unavailable"))
            : ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("Agora foi."), entrada: 100, saida: 20));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var primeira = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Retrying, "ai-service_unavailable"), (primeira.Outcome, primeira.Code));
        var indeterminado = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Indeterminate, AiUsageCertainty.Unknown), (indeterminado.State, indeterminado.UsageCertainty));
        Assert.Equal(indeterminado.ReservedTokens, indeterminado.AppliedTokens); // 5xx nunca presume "nada cobrado": cobra a reserva (o limite superior)

        a.Avancar(TimeSpan.FromSeconds(6));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal("Agora foi.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
        var ledger = await Ledger(a);
        Assert.Equal(new[] { AiRequestState.Indeterminate, AiRequestState.Confirmed }, ledger.Select(r => r.State).ToArray());
        Assert.Equal(2, a.RequisicoesHttp.Count); // a MESMA OpenAI, sem fallback para outro provedor
    }

    [Fact]
    public async Task Cota_esgotada_da_OpenAI_e_permanente_o_cliente_vai_para_o_atendente_e_nada_mais_e_chamado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai4", usarOpenAi: true);
        await a.FilaDeAtendimentoAsync();
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOpenAiSimulado.Json(HttpStatusCode.TooManyRequests, ServidorOpenAiSimulado.Erro("insufficient_quota"), ("Retry-After", "30")));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Dead, "ai-quota-or-billing"), (resultado.Outcome, resultado.Code));
        Assert.Single(a.RequisicoesHttp); // sem retentativa, mesmo com a espera sugerida
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
        Assert.Equal(AiRequestState.Indeterminate, Assert.Single(await Ledger(a)).State); // 429 não é evidência de que nada foi cobrado
    }

    [Fact]
    public async Task Credencial_recusada_pela_OpenAI_e_rejeicao_de_admissao_nao_cobra_nada_e_o_tenant_vai_para_o_atendente()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai5", usarOpenAi: true);
        await a.FilaDeAtendimentoAsync();
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOpenAiSimulado.Json(HttpStatusCode.Unauthorized, ServidorOpenAiSimulado.Erro("invalid_api_key", "invalid_request_error")));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Dead, "ai-invalid-credentials"), (resultado.Outcome, resultado.Code));
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Confirmed, 0L, AiUsageCertainty.NoneIncurred), (registro.State, registro.AppliedTokens, registro.UsageCertainty));
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
    }

    [Fact]
    public async Task Resposta_sem_consumo_informado_e_descartada_o_consumo_fica_Unknown_e_a_tentativa_falha()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai6", usarOpenAi: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOpenAiSimulado.SemConsumo(CenarioDeAgentes.Responder("Sem usage.")));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        // Uma resposta sem usage nunca vira sucesso: a contabilidade do tenant não aceita "gastou não sei quanto". Nenhuma resposta sai, e o ledger cobra a reserva (indeterminado).
        Assert.Equal((TurnRunOutcome.Retrying, "ai-missing-usage"), (resultado.Outcome, resultado.Code));
        Assert.Empty(await a.RespostasDaIAAsync(entrada.SessionId));
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Indeterminate, AiUsageCertainty.Unknown), (registro.State, registro.UsageCertainty));
    }

    [Fact]
    public async Task Saida_do_modelo_que_nao_e_a_decisao_esperada_nao_executa_nada_e_conta_como_falha_da_tentativa()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai7", usarOpenAi: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso("Claro! Posso ajudar com isso."));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Retrying, resultado.Outcome);
        Assert.StartsWith("invalid-output-", resultado.Code);
        Assert.Empty(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(AiRequestState.Confirmed, Assert.Single(await Ledger(a)).State); // o consumo foi real e informado: é cobrado
    }

    [Fact]
    public async Task Dois_tenants_com_a_OpenAI_cada_um_com_a_propria_credencial_e_o_proprio_agente_nunca_se_misturam()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai8a", usarOpenAi: true);
        var b = await CenarioDeAgentes.CriarAsync(banco, "agoai8b", usarOpenAi: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta do tenant A")));
        b.RoteiroHttp = (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta do tenant B")));
        var deA = await a.EntrarAsync("mauro", "Pergunta do tenant A");
        var deB = await b.EntrarAsync("mauro", "Pergunta do tenant B");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var executor = a.ExecutorDoWorker();
        for (var i = 0; i < 5 && (await a.RespostasDaIAAsync(deA.SessionId)).Count + (await b.RespostasDaIAAsync(deB.SessionId)).Count < 2; i++)
        {
            await executor.RunOnceAsync(default);
        }

        Assert.Equal("Resposta do tenant A", Assert.Single(await a.RespostasDaIAAsync(deA.SessionId)).Text);
        Assert.Equal("Resposta do tenant B", Assert.Single(await b.RespostasDaIAAsync(deB.SessionId)).Text);

        // Cada requisição à OpenAI carregou a credencial e o agente do PRÓPRIO tenant, e nada do outro (nem o canário da chave, nem o das instruções).
        var httpA = Assert.Single(a.RequisicoesHttp);
        var httpB = Assert.Single(b.RequisicoesHttp);
        Assert.Contains(a.CanarioDasInstrucoes, httpA.Corpo);
        Assert.DoesNotContain(b.CanarioDasInstrucoes, httpA.Corpo);
        Assert.DoesNotContain(a.Canario, httpB.Corpo);
        Assert.DoesNotContain(b.Canario, httpA.Corpo);
        Assert.DoesNotContain("tenant B", httpA.Corpo);
        Assert.NotEqual(httpA.Credencial, httpB.Credencial);
    }

    [Fact]
    public async Task Tenant_com_a_OpenAI_sem_credencial_nunca_chega_a_chamar_o_servidor()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoai9", usarOpenAi: true);
        await a.FilaDeAtendimentoAsync();
        await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<IaBimeta.AiGateway.Application.AiTenantConfigurationService>().RevokeCredentialAsync(PostgresFixture.ProvedorOpenAiDeTeste));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Dead, resultado.Outcome);
        Assert.Empty(a.RequisicoesHttp);
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
    }
}
