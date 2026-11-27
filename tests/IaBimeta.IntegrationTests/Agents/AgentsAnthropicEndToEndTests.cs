using System.Net;
using IaBimeta.Agents.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.ContactCenter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// A Etapa 03 de ponta a ponta: mensagem do cliente, turno do Agents, AiGateway REAL, adaptador REAL da Anthropic (pedido HTTP, uso, erros) e o "servidor da Anthropic" SIMULADO por HTTP, com PostgreSQL.
/// Nenhuma rede e nenhuma chave real: a credencial BYOK de cada tenant é um canário sintético, e o consumo registrado é o que a "Anthropic" informou.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentsAnthropicEndToEndTests(PostgresFixture banco)
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

    [Fact]
    public async Task Fluxo_completo_o_cliente_pergunta_a_Anthropic_responde_e_o_consumo_real_com_cache_vai_para_o_ledger()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoan1", usarAnthropic: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorAnthropicSimulado.Sucesso(CenarioDeAgentes.Responder("Em geral não é necessário jejum para o hemograma."), entrada: 100, leituraDoCache: 40, escritaNoCache: 10, saida: 25, requestId: "req_abc"));
        var entrada = await a.EntrarAsync("mauro", "Preciso de jejum para o hemograma?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);
        Assert.Equal("Em geral não é necessário jejum para o hemograma.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);

        // O pedido que foi à Anthropic: Messages API, a versão oficial da API, a credencial BYOK DESTE tenant só no cabeçalho, esforço do perfil, saída estruturada nativa e o teto de saída imposto na requisição.
        var http = Assert.Single(a.RequisicoesHttp);
        Assert.Equal("https://api.anthropic.com/v1/messages", http.Url);
        Assert.Equal("2026-10-21", http.ClientRequestId); // a captura leva a versão da API neste campo
        var corpo = http.Json;
        Assert.Equal(("claude-haiku-5-5", "low", "json_schema"), ((string)corpo["model"]!, (string)corpo["output_config"]!["effort"]!, (string)corpo["output_config"]!["format"]!["type"]!));
        Assert.Contains(a.CanarioDasInstrucoes, (string)corpo["system"]!);
        var mensagens = corpo["messages"]!.AsArray();
        Assert.Equal("user", (string)mensagens[mensagens.Count - 1]!["role"]!);
        Assert.Contains("Preciso de jejum para o hemograma?", (string)mensagens[mensagens.Count - 1]!["content"]!);
        Assert.DoesNotContain(a.Canario, http.Corpo);

        // O Agents pediu 600 tokens de saída; o limite do tenant no AiGateway é 500: é o 500 que vai na requisição.
        Assert.Equal(500, (int)corpo["max_tokens"]!);

        // O ledger registra o consumo REAL informado: entrada TOTAL (100 + 40 lidos do cache + 10 escritos), as parcelas de cache, a saída e a certeza Reported. Nada de prompt nem de resposta.
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Confirmed, "anthropic", "claude-haiku-5-5", 175L), (registro.State, registro.ProviderId, registro.ModelId, registro.AppliedTokens));
        Assert.Equal((150, 40, 10, 25), (registro.InputTokens, registro.CachedInputTokens, registro.CacheWriteTokens, registro.OutputTokens));
        Assert.Equal(("req_abc", AiUsageCertainty.Reported), (registro.ProviderRequestId, registro.UsageCertainty));
        Assert.True(registro.EstimatedCost > 0m);
    }

    [Fact]
    public async Task Duas_empresas_na_Anthropic_nunca_misturam_a_credencial_nem_o_contexto()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoan2a", usarAnthropic: true);
        var b = await CenarioDeAgentes.CriarAsync(banco, "agoan2b", usarAnthropic: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorAnthropicSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta para A.")));
        b.RoteiroHttp = (_, _) => Task.FromResult(ServidorAnthropicSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta para B.")));
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
        Assert.DoesNotContain(b.Canario, deA.Corpo);
        Assert.DoesNotContain(a.Canario, deB.Corpo);
        Assert.Equal("Resposta para A.", Assert.Single(await a.RespostasDaIAAsync(entradaA.SessionId)).Text);
        Assert.Equal("Resposta para B.", Assert.Single(await b.RespostasDaIAAsync(entradaB.SessionId)).Text);
    }

    [Fact]
    public async Task Erro_529_da_Anthropic_deixa_o_consumo_indeterminado_tenta_de_novo_e_nunca_cai_para_outro_provedor()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoan3", usarAnthropic: true);
        var chamadas = 0;
        a.RoteiroHttp = (_, _) => Task.FromResult(Interlocked.Increment(ref chamadas) == 1
            ? ServidorAnthropicSimulado.Json((HttpStatusCode)529, ServidorAnthropicSimulado.Erro("overloaded_error"))
            : ServidorAnthropicSimulado.Sucesso(CenarioDeAgentes.Responder("Agora foi."), entrada: 100, saida: 20));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var primeira = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Retrying, "ai-overloaded_error"), (primeira.Outcome, primeira.Code));
        var indeterminado = Assert.Single(await Ledger(a));
        Assert.Equal((AiRequestState.Indeterminate, AiUsageCertainty.Unknown), (indeterminado.State, indeterminado.UsageCertainty));
        Assert.Equal(indeterminado.ReservedTokens, indeterminado.AppliedTokens); // sobrecarga nunca presume "nada cobrado": cobra a reserva

        a.Avancar(TimeSpan.FromSeconds(6));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal("Agora foi.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
        Assert.Equal(2, a.RequisicoesHttp.Count);

        // Nenhum fallback silencioso: a OpenAI nunca recebeu a credencial deste tenant.
        Assert.Empty(banco.ServidorOpenAi.DaCredencial(a.Canario));
    }

    [Fact]
    public async Task Credencial_recusada_pela_Anthropic_e_rejeicao_de_admissao_nao_cobra_nada_e_o_cliente_vai_para_o_atendente()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoan4", usarAnthropic: true);
        await a.FilaDeAtendimentoAsync();
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorAnthropicSimulado.Json(HttpStatusCode.Unauthorized, ServidorAnthropicSimulado.Erro("authentication_error")));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Dead, resultado.Outcome);
        Assert.Single(a.RequisicoesHttp); // sem retentativa
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
        var registro = Assert.Single(await Ledger(a));
        Assert.Equal(AiUsageCertainty.NoneIncurred, registro.UsageCertainty);
        Assert.Equal(0L, registro.AppliedTokens);
    }

    [Fact]
    public async Task Cota_esgotada_da_Anthropic_e_permanente_e_nada_mais_e_chamado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoan5", usarAnthropic: true);
        await a.FilaDeAtendimentoAsync();
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorAnthropicSimulado.Json(HttpStatusCode.TooManyRequests, ServidorAnthropicSimulado.Erro("rate_limit_error", "enforced_spend_limit_reached"), ("retry-after", "30")));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Dead, "ai-quota-or-billing"), (resultado.Outcome, resultado.Code));
        Assert.Single(a.RequisicoesHttp);
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
    }

    [Fact]
    public async Task Empresa_suspensa_nao_chama_a_Anthropic_a_mensagem_fica_preservada_e_so_a_mensagem_nova_depois_da_reativacao_e_atendida()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agoan6", usarAnthropic: true);
        a.RoteiroHttp = (_, _) => Task.FromResult(ServidorAnthropicSimulado.Sucesso(CenarioDeAgentes.Responder("Posso ajudar.")));
        await MudarSituacaoAsync(a, true, "suspensao de teste");
        var antiga = await a.EntrarAsync("mauro", "mensagem durante a suspensão");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        await a.ExecutarAsync();

        // Suspensa: nenhuma chamada ao provedor e nenhum consumo.
        Assert.Empty(a.RequisicoesHttp);
        Assert.Empty(await Ledger(a));

        // Reativada, sem nenhuma mensagem nova: o evento antigo não aciona a IA, mesmo com ciclos repetidos.
        a.Avancar(TimeSpan.FromMinutes(10));
        await MudarSituacaoAsync(a, false, "reativacao de teste");
        a.Avancar(TimeSpan.FromMinutes(1));
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(30));
        await a.ExecutarAsync();
        Assert.Empty(a.RequisicoesHttp);
        Assert.Contains((await a.C.HistoricoAsync(antiga.SessionId))!.Items, m => m.Text == "mensagem durante a suspensão");

        // A credencial e a configuração foram preservadas: uma mensagem NOVA é atendida normalmente pela Anthropic.
        var nova = await a.EntrarAsync("mauro", "mensagem nova depois da reativacao");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        var resultado = await a.ExecutarAsync();

        Assert.Contains(resultado, r => r.Outcome == TurnRunOutcome.Replied);
        var http = Assert.Single(a.RequisicoesHttp);
        Assert.Contains("mensagem nova depois da reativacao", http.Corpo);
        Assert.Equal(nova.SessionId, antiga.SessionId);
        Assert.Equal("Posso ajudar.", Assert.Single(await a.RespostasDaIAAsync(nova.SessionId)).Text);
    }
}
