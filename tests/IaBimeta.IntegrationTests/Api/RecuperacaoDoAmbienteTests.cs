using System.Net;
using System.Text.RegularExpressions;
using IaBimeta.Api.Operations;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A RECUPERAÇÃO do ambiente depois da queda do Worker (ADR 0026), com Api e Worker reais, PostgreSQL real, o adaptador real do WhatsApp contra a Graph API simulada e a OpenAI simulada. Provam que, com o Worker
/// derrubado, o webhook continua respondendo 200 e gravando no Inbox, o comando <c>status</c> aponta o Worker e os eventos parados, e que, ao voltar, cada mensagem é processada UMA vez: uma sessão por cliente, uma resposta
/// da IA, um envio ao provedor, nenhum duplicado e nenhum perdido, inclusive quando a Meta repete o webhook e quando o Worker cai no meio da chamada ao modelo.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RecuperacaoDoAmbienteTests(PostgresFixture banco)
{
    private const string TextoDaIa = "Resposta da IA depois da recuperação.";

    private static Dictionary<string, string?> Configuracao() => new()
    {
        // Limites curtos: o ensaio não espera cinco minutos para um evento "parado" (a configuração de produção usa os padrões).
        ["Health:EventStuckAfter"] = "00:00:02",
        ["Health:WorkerSilenceAfter"] = "00:00:02",
        ["Health:TurnStuckAfter"] = "00:00:02",
        ["Health:OutboundStuckAfter"] = "00:00:02",
    };

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, Configuracao());
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde());
        return e;
    }

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Responde() =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder(TextoDaIa), 150, 0, 25, "req_recup"));

    private static async Task<(int Codigo, string Saida)> StatusAsync(EnsaioDeWhatsApp e)
    {
        var saida = new StringWriter();
        var codigo = await StatusCommand.ExecuteAsync(e.Ia.Api.Services, new StatusOptions(e.Ia.Slug, null, false, true), saida);
        return (codigo, saida.ToString());
    }

    private static string SecaoDoWorker(string saida) => Regex.Match(saida, @"WORKER\r?\n(?<linhas>(  .*\r?\n?)+)").Groups["linhas"].Value;

    /// <summary>Quantas mensagens de saída do tenant estão em cada estado do despacho (1 despachando, 2 enviada, 3 falha, 4 ambígua, 5 nova tentativa).</summary>
    private static Task<Dictionary<short, int>> SaidasPorEstadoAsync(EnsaioDeWhatsApp e) =>
        e.Ia.ComoSistemaAsync(async sp =>
        {
            var tenant = new TenantId(sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value);
            var linhas = await sp.GetRequiredService<ChannelsDbContext>().OutboundMessages.AsNoTracking().Where(m => m.TenantId == tenant).Select(m => (short)m.State).ToListAsync();
            return linhas.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        });

    private static async Task<IReadOnlyList<(string Contato, Guid Sessao)>> SessoesAsync(EnsaioDeWhatsApp e) =>
        (await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<IConversationQueries>().ListSessionsAsync(new ListSessionsQuery(100, null, null))))
            .Items.Select(s => (s.ContactDisplayName ?? string.Empty, s.Id)).ToList();

    [Fact]
    public async Task Worker_parado_acumula_a_entrada_o_status_aponta_e_ao_voltar_cada_mensagem_e_processada_uma_vez()
    {
        await using var e = await IniciarAsync("recup");

        // Em ordem: o Worker anda e o status diz que ele está ativo.
        var (codigoInicial, inicial) = await StatusAsync(e);
        Assert.Matches(@"\[OK\]\s+ativo", SecaoDoWorker(inicial));
        Assert.Equal(0, codigoInicial);

        // O Worker cai. A Meta continua entregando: o Receiver responde 200 e grava no Inbox durável (nada depende do Worker no caminho do webhook).
        await e.Ia.PararWorkerAsync();
        var clientes = Enumerable.Range(0, 5).Select(i => (Nome: $"Recup{i}", Numero: $"551199999{i:D4}")).ToList();
        foreach (var (nome, numero) in clientes.Select((c, i) => (c.Nome, c.Numero)))
        {
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto($"wamid.REC-{nome}", numero, nome, "Oi, preciso de ajuda")));
        }

        // A Meta repete o primeiro webhook (não recebeu o 200 a tempo): o mesmo corpo, o mesmo identificador.
        var repetido = e.PayloadDeTexto($"wamid.REC-{clientes[0].Nome}", clientes[0].Numero, clientes[0].Nome, "Oi, preciso de ajuda");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(repetido));
        await Task.Delay(TimeSpan.FromSeconds(3)); // passa dos limites curtos de saúde

        // O status aponta o que parou: o Worker sem atividade e os eventos que ninguém entrega. Nada foi processado.
        var (codigoParado, parado) = await StatusAsync(e);
        Assert.Equal(2, codigoParado);
        Assert.Matches(@"\[CRITICO\]\s+\d+ conexão\(ões\) do Worker, mas sem atividade", SecaoDoWorker(parado));
        Assert.Contains("evento(s) elegível(is) sem ser entregue(s)", parado);
        Assert.Contains("-> Confira se o Worker está no ar", parado);
        Assert.Empty(await SessoesAsync(e));
        Assert.Empty(e.Graph.Capturas);
        Assert.Empty(e.Ia.OpenAi.Roteiros.Capturas);

        // O Worker volta: tudo é processado, uma vez.
        await e.Ia.SubirWorkerAsync();
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 5, "as 5 respostas deveriam sair pelo WhatsApp");
        await e.EsperarAsync(async () => (await SaidasPorEstadoAsync(e)).GetValueOrDefault((short)2) == 5, "as 5 saídas deveriam ficar Enviadas");
        await Task.Delay(TimeSpan.FromSeconds(3)); // tempo para qualquer duplicado aparecer

        var sessoes = await SessoesAsync(e);
        Assert.Equal(clientes.Select(c => c.Nome).Order(), sessoes.Select(s => s.Contato).Order()); // uma sessão por cliente, nenhuma a mais
        foreach (var (_, sessao) in sessoes)
        {
            var mensagens = await e.Ia.MensagensAsync(sessao);
            Assert.Single(mensagens, m => m["author"]!.GetValue<string>() == "Customer"); // a repetição da Meta não virou segunda mensagem
            Assert.Single(mensagens, m => m["author"]!.GetValue<string>() == "Ai");
        }

        Assert.Equal(5, e.Graph.Capturas.Count);
        Assert.Equal(5, e.Graph.Capturas.Select(c => c.Para).Distinct().Count()); // um envio por cliente
        Assert.All(e.Graph.Capturas, c => Assert.Equal(TextoDaIa, c.Texto));
        Assert.Equal(5, e.Ia.OpenAi.Roteiros.Capturas.Count); // uma chamada ao modelo por cliente
        Assert.Equal(new Dictionary<short, int> { [2] = 5 }, await SaidasPorEstadoAsync(e));

        // Recuperado: o status volta a dizer que está tudo em ordem.
        var (codigoFinal, final) = await StatusAsync(e);
        Assert.Matches(@"\[OK\]\s+ativo", SecaoDoWorker(final));
        Assert.DoesNotContain("[CRITICO]", final);
        Assert.Equal(0, codigoFinal);
    }

    [Fact]
    public async Task Worker_que_cai_no_meio_da_chamada_ao_modelo_retoma_ao_voltar_com_uma_resposta_e_um_envio()
    {
        await using var e = await IniciarAsync("recmeio");

        // A OpenAI "demora" para sempre na primeira chamada: o Worker cai com ela em andamento.
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, async (_, cancelamento) =>
        {
            await Task.Delay(Timeout.Infinite, cancelamento);
            return ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("nunca chega"));
        });
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.REC-MEIO", "5511999991234", "Meio", "Oi")));
        await e.EsperarAsync(async () => e.Ia.OpenAi.Roteiros.Capturas.Count == 1, "a chamada ao modelo deveria estar em andamento");

        await e.Ia.PararWorkerAsync();
        Assert.Empty(e.Graph.Capturas); // nada saiu para o cliente

        // O ambiente volta e o modelo responde. O turno interrompido é retomado: nem perdido, nem duplicado.
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Responde());
        await e.Ia.SubirWorkerAsync();
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "a resposta deveria sair pelo WhatsApp depois da recuperação");
        await e.EsperarAsync(async () => (await SaidasPorEstadoAsync(e)).GetValueOrDefault((short)2) == 1, "a saída deveria ficar Enviada");
        await Task.Delay(TimeSpan.FromSeconds(3));

        var sessao = Assert.Single(await SessoesAsync(e)).Sessao;
        var mensagens = await e.Ia.MensagensAsync(sessao);
        Assert.Single(mensagens, m => m["author"]!.GetValue<string>() == "Customer");
        Assert.Single(mensagens, m => m["author"]!.GetValue<string>() == "Ai"); // uma resposta ao cliente, mesmo com duas chamadas ao modelo
        Assert.Single(e.Graph.Capturas);
        Assert.Equal(TextoDaIa, e.Graph.Capturas[0].Texto);
        Assert.Equal(new Dictionary<short, int> { [2] = 1 }, await SaidasPorEstadoAsync(e));
        Assert.True(e.Ia.OpenAi.Roteiros.Capturas.Count >= 2); // a chamada cortada e a refeita (o custo da interrupção, visível no ledger)

        var (_, depois) = await StatusAsync(e);
        Assert.DoesNotContain("[CRITICO]", depois);
    }
}
