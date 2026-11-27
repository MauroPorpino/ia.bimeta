using System.Net;
using IaBimeta.Agents.Application;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.ContactCenter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// A Etapa 04 de ponta a ponta: mensagem do cliente, turno do Agents, AiGateway REAL, adaptador conversacional REAL do Ollama (pedido HTTP, uso, erros) e o "servidor Ollama" SIMULADO por HTTP, com PostgreSQL.
/// Nenhuma rede, nenhum modelo real e NENHUMA credencial: o Ollama não tem chave, e o tenant nunca guarda uma.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentsOllamaEndToEndTests(PostgresFixture banco)
{
    private static Task<List<AiRequest>> Ledger(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync());

    private static Task MudarSituacaoAsync(CenarioDeAgentes a, bool suspender, string motivo) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var repo = sp.GetRequiredService<IaBimeta.Tenancy.Application.ITenantRepository>();
            var tenant = (await repo.FindByIdAsync(a.Tenant.TenantId, default))!;
            var agora = a.Relogio.GetUtcNow();
            await repo.SaveStatusChangeAsync(suspender ? tenant.SuspendWithReason(motivo, null, agora) : tenant.ReactivateWithReason(motivo, null, agora), default);
            return 0;
        });

    private void NenhumFallback(CenarioDeAgentes a)
    {
        // Nenhum fallback silencioso: nem a OpenAI nem a Anthropic receberam nada deste tenant.
        Assert.DoesNotContain(banco.ServidorOpenAi.Capturas, c => c.Corpo.Contains(a.CanarioDasInstrucoes));
        Assert.DoesNotContain(banco.ServidorAnthropic.Capturas, c => c.Corpo.Contains(a.CanarioDasInstrucoes));
    }

    [Fact]
    public async Task Fluxo_completo_o_cliente_pergunta_o_Ollama_responde_sem_credencial_e_os_tokens_vao_para_o_ledger()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool1", usarOllama: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Sucesso(CenarioDeAgentes.Responder("Em geral não é necessário jejum para o hemograma."), entrada: 140, saida: 26));
        var entrada = await a.EntrarAsync("mauro", "Preciso de jejum para o hemograma?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);
        Assert.Equal("Em geral não é necessário jejum para o hemograma.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);

        // O pedido: /api/chat, sem streaming, sem cabeçalho de autorização, o modelo escolhido, o teto de saída imposto, o contexto do perfil, a saída estruturada e SÓ o contexto desta sessão.
        var http = Assert.Single(a.RequisicoesHttp);
        Assert.Equal("http://127.0.0.1:11434/api/chat", http.Url);
        Assert.Equal(string.Empty, http.Credencial);
        var corpo = http.Json;
        Assert.Equal("qwen3:4b", (string)corpo["model"]!);
        Assert.False((bool)corpo["stream"]!);
        Assert.Equal(500, (int)corpo["options"]!["num_predict"]!); // o teto do tenant (500) vence o pedido do Agents (600)
        Assert.Equal(8192, (int)corpo["options"]!["num_ctx"]!);
        Assert.Equal("object", (string)corpo["format"]!["type"]!);
        var mensagens = corpo["messages"]!.AsArray();
        Assert.Equal("system", (string)mensagens[0]!["role"]!);
        Assert.Contains(a.CanarioDasInstrucoes, (string)mensagens[0]!["content"]!);
        Assert.Contains("Preciso de jejum para o hemograma?", (string)mensagens[mensagens.Count - 1]!["content"]!);

        // O ledger: provedor ollama, modelo, entrada e saída reportadas, custo estimado ZERO (sem tarifa por token; custo de infraestrutura não é calculado) e a certeza Reported.
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Confirmed, "ollama", "qwen3:4b", 166L), (registro.State, registro.ProviderId, registro.ModelId, registro.AppliedTokens));
        Assert.Equal((140, 0, 0, 26), (registro.InputTokens, registro.CachedInputTokens, registro.CacheWriteTokens, registro.OutputTokens));
        Assert.Equal(AiUsageCertainty.Reported, registro.UsageCertainty);
        Assert.Equal(0m, registro.EstimatedCost);

        // O tenant não tem (nem pode ter) credencial: a situação diz que o provedor não a exige.
        var status = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AiTenantConfigurationService>().GetStatusAsync());
        Assert.True(status.Configured);
        Assert.False(status.CredentialRequired);
        Assert.False(status.CredentialPresent);
        NenhumFallback(a);
    }

    [Fact]
    public async Task O_servico_recusa_cadastrar_credencial_para_o_Ollama_e_a_exigencia_dos_outros_provedores_continua()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool2", usarOllama: true);
        await Assert.ThrowsAsync<AiGatewayValidationException>(() => a.C.ComoAsync(a.Admin, async sp =>
        {
            await sp.GetRequiredService<AiTenantConfigurationService>().SetCredentialAsync("ollama", "chave-ficticia");
            return 0;
        }));

        // OpenAI sem credencial continua falhando fechado (a exigência não foi enfraquecida).
        var b = await CenarioDeAgentes.CriarAsync(banco, "agool2b", configurarIA: false);
        await b.C.ComoAsync(b.Admin, async sp =>
        {
            await sp.GetRequiredService<AiTenantConfigurationService>().SetConfigurationAsync(PostgresFixture.ProvedorOpenAiDeTeste, PostgresFixture.ModeloOpenAiDeTeste);
            return 0;
        });
        var resultado = await b.C.ComoAsync(b.C.ContextoDeSistema, sp => sp.GetRequiredService<IAiGateway>().GenerateAsync(new AiGenerationRequest("c", "sistema", [new AiMessage(AiMessageRole.User, "oi")], 100, TimeSpan.FromSeconds(5))));
        Assert.Equal(AiErrorKind.InvalidCredentials, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.CredentialMissing, resultado.Failure.Code);
    }

    [Fact]
    public async Task Duas_empresas_no_Ollama_compartilhando_o_servidor_nunca_misturam_o_contexto()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool3a", usarOllama: true);
        var b = await CenarioDeAgentes.CriarAsync(banco, "agool3b", usarOllama: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta para A.")));
        b.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta para B.")));
        var entradaA = await a.EntrarAsync("mauro", "Pergunta do exame A");
        var entradaB = await b.EntrarAsync("pedro", "Pergunta do exame B");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        await a.ExecutarAsync();
        await b.RelayAsync();
        b.Avancar(TimeSpan.FromSeconds(4));
        await b.ExecutarAsync();

        var deA = Assert.Single(a.RequisicoesHttp);
        var deB = Assert.Single(b.RequisicoesHttp);
        Assert.Contains("Pergunta do exame A", deA.Corpo);
        Assert.DoesNotContain("exame B", deA.Corpo);
        Assert.Contains("Pergunta do exame B", deB.Corpo);
        Assert.DoesNotContain("exame A", deB.Corpo);
        Assert.DoesNotContain(b.CanarioDasInstrucoes, deA.Corpo);
        Assert.DoesNotContain(a.CanarioDasInstrucoes, deB.Corpo);
        Assert.Equal("Resposta para A.", Assert.Single(await a.RespostasDaIAAsync(entradaA.SessionId)).Text);
        Assert.Equal("Resposta para B.", Assert.Single(await b.RespostasDaIAAsync(entradaB.SessionId)).Text);
    }

    [Fact]
    public async Task Servidor_indisponivel_deixa_o_consumo_indeterminado_tenta_de_novo_e_nunca_cai_para_outro_provedor()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool4", usarOllama: true);
        var chamadas = 0;
        a.RoteiroHttp = (_, _) => Interlocked.Increment(ref chamadas) == 1
            ? throw new HttpRequestException("conexão recusada")
            : Task.FromResult(ServidorOllamaSimulado.Sucesso(CenarioDeAgentes.Responder("Agora foi."), entrada: 100, saida: 20));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var primeira = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Retrying, "ai-server-unavailable"), (primeira.Outcome, primeira.Code));
        var indeterminado = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Indeterminate, AiUsageCertainty.Unknown), (indeterminado.State, indeterminado.UsageCertainty));
        Assert.Equal(indeterminado.ReservedTokens, indeterminado.AppliedTokens); // desconhecido nunca vira zero

        a.Avancar(TimeSpan.FromSeconds(6));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal("Agora foi.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
        Assert.Equal(2, a.RequisicoesHttp.Count);
        NenhumFallback(a);
    }

    [Fact]
    public async Task Modelo_nao_instalado_e_permanente_nao_cobra_nada_e_o_cliente_vai_para_o_atendente_sem_baixar_nem_trocar_de_modelo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool5", usarOllama: true);
        await a.FilaDeAtendimentoAsync();
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Erro(HttpStatusCode.NotFound, "model 'qwen3:4b' not found"));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Dead, resultado.Outcome);
        Assert.Single(a.RequisicoesHttp); // sem retentativa, sem /api/pull, sem outro modelo
        Assert.All(a.RequisicoesHttp, r => Assert.EndsWith("/api/chat", r.Url));
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal(AiUsageCertainty.NoneIncurred, registro.UsageCertainty);
        Assert.Equal(0L, registro.AppliedTokens);
        NenhumFallback(a);
    }

    [Fact]
    public async Task Consumo_nao_informado_pelo_servidor_e_desconhecido_a_resposta_e_descartada_e_a_reserva_e_cobrada()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool6", usarOllama: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Json(HttpStatusCode.OK, "{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"action\\\":\\\"reply\\\"}\"},\"done\":true,\"done_reason\":\"stop\"}"));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Retrying, "ai-missing-usage"), (resultado.Outcome, resultado.Code));
        Assert.Empty(await a.RespostasDaIAAsync(entrada.SessionId));
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Indeterminate, AiUsageCertainty.Unknown), (registro.State, registro.UsageCertainty));
        Assert.Equal(registro.ReservedTokens, registro.AppliedTokens);
    }

    [Fact]
    public async Task Saida_estruturada_invalida_do_modelo_local_nunca_vira_resposta_ao_cliente()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool7", usarOllama: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Sucesso("isto nao e json de decisao"));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.NotEqual(TurnRunOutcome.Replied, resultado.Outcome);
        Assert.Empty(await a.RespostasDaIAAsync(entrada.SessionId));
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal(AiUsageCertainty.Reported, registro.UsageCertainty); // o consumo foi informado, mesmo a saída sendo inutilizável
    }

    [Fact]
    public async Task O_raciocinio_do_modelo_nunca_chega_ao_cliente()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool8", usarOllama: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta limpa.")));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        await a.ExecutarAsync();

        var texto = Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text;
        Assert.Equal("Resposta limpa.", texto);
        Assert.DoesNotContain("raciocinio interno", texto);
    }

    [Fact]
    public async Task Empresa_suspensa_nao_chama_o_Ollama_a_mensagem_fica_preservada_e_so_a_mensagem_nova_depois_da_reativacao_e_atendida()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool9", usarOllama: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Sucesso(CenarioDeAgentes.Responder("Posso ajudar.")));
        await MudarSituacaoAsync(a, true, "suspensao de teste");
        var antiga = await a.EntrarAsync("mauro", "mensagem durante a suspensão");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        await a.ExecutarAsync();

        Assert.Empty(a.RequisicoesHttp);
        Assert.Empty(await Ledger(a));

        a.Avancar(TimeSpan.FromMinutes(10));
        await MudarSituacaoAsync(a, false, "reativacao de teste");
        a.Avancar(TimeSpan.FromMinutes(1));
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(30));
        await a.ExecutarAsync();
        Assert.Empty(a.RequisicoesHttp); // a reativação não reprocessa mensagem antiga
        Assert.Contains((await a.C.HistoricoAsync(antiga.SessionId))!.Items, m => m.Text == "mensagem durante a suspensão");

        var nova = await a.EntrarAsync("mauro", "mensagem nova depois da reativacao");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        var resultado = await a.ExecutarAsync();

        Assert.Contains(resultado, r => r.Outcome == TurnRunOutcome.Replied);
        var http = Assert.Single(a.RequisicoesHttp);
        Assert.Contains("mensagem nova depois da reativacao", http.Corpo);
        Assert.Equal("Posso ajudar.", Assert.Single(await a.RespostasDaIAAsync(nova.SessionId)).Text);
    }

    [Fact]
    public async Task Raciocinio_vazado_no_campo_de_resposta_nunca_chega_ao_cliente_o_consumo_fica_e_o_atendente_assume()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agool10", usarOllama: true);
        await a.FilaDeAtendimentoAsync();
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorOllamaSimulado.Sucesso("{\"action\":\"reply\",\"reply_text\":\"<think>raciocinio interno que vazou\",\"handoff_reason\":\"none\"}", entrada: 130, saida: 44));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Dead, resultado.Outcome); // permanente: sem repetir o processamento
        Assert.Single(a.RequisicoesHttp);
        Assert.All(await a.RespostasDaIAAsync(entrada.SessionId), m => { Assert.Equal(ReplyKind.Informative, m.ReplyKind); Assert.DoesNotContain("raciocinio", m.Text); }); // só o aviso de transferência
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal((AiUsageCertainty.Reported, 130, 44), (registro.UsageCertainty, registro.InputTokens, registro.OutputTokens));
    }
}
