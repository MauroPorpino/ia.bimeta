using IaBimeta.Channels.Application;
using IaBimeta.Channels.Domain;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// O estado final do despacho de uma mensagem de saída NUNCA é sobrescrito por quem perdeu a vez: um handler cujo lease venceu (e continua rodando) ou o tratamento de morto de outro relay gravam com comparação e
/// troca (o estado e a contagem lidos são parte da condição do UPDATE). Quem grava primeiro vale; quem perdeu recebe <see cref="OutboundStateConflictException"/> e nada é alterado. Em particular, um
/// <c>SendUnknown</c> gravado por um Worker não vira <c>Sent</c> nem <c>Failed</c> por causa de uma resposta tardia. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboundStateConcurrencyTests(PostgresFixture banco)
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Um despacho com a chamada ao provedor JÁ INICIADA (a tentativa gravada antes da chamada), como o Sender o deixa.</summary>
    private static async Task<(Guid MensagemId, Guid OperacaoId)> NovoDespachoEmAndamentoAsync(CenarioDeConversas c)
    {
        var mensagem = Guid.NewGuid();
        var operacao = Guid.NewGuid();
        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var loja = sp.GetRequiredService<IOutboundMessageStore>();
            var despacho = OutboundMessage.Intend(mensagem, c.Tenant.TenantId, operacao, ChannelInstanceId.New(), Agora);
            Assert.True(await loja.TryAddAsync(despacho, default));
            despacho.BeginAttempt(Agora);
            await loja.SaveAsync(default);
            return 0;
        });
        return (mensagem, operacao);
    }

    private static Task<OutboundMessage> LerAsync(CenarioDeConversas c, Guid operacao) =>
        c.ComoAsync(c.ContextoDeSistema, async sp => (await sp.GetRequiredService<ChannelsDbContext>().OutboundMessages.AsNoTracking().SingleAsync(m => m.OperationId == operacao)));

    /// <summary>Dois processos (dois escopos, dois DbContexts) leem o MESMO despacho antes de qualquer gravação; depois cada um grava o seu desfecho na ordem pedida.</summary>
    private static async Task<(Exception? Primeiro, Exception? Segundo)> DoisLeemEGravamAsync(
        CenarioDeConversas c, Guid operacao, Action<OutboundMessage> primeiro, Action<OutboundMessage> segundo)
    {
        await using var escopoA = c.Banco.NovoEscopo(c.ContextoDeSistema);
        await using var escopoB = c.Banco.NovoEscopo(c.ContextoDeSistema);
        var lojaA = escopoA.ServiceProvider.GetRequiredService<IOutboundMessageStore>();
        var lojaB = escopoB.ServiceProvider.GetRequiredService<IOutboundMessageStore>();
        var lidoPorA = (await lojaA.FindByOperationAsync(operacao, default))!;
        var lidoPorB = (await lojaB.FindByOperationAsync(operacao, default))!;
        Assert.Equal((OutboundState.Dispatching, 1), (lidoPorA.State, lidoPorA.Attempts));
        Assert.Equal((OutboundState.Dispatching, 1), (lidoPorB.State, lidoPorB.Attempts));

        primeiro(lidoPorA);
        var erroDoPrimeiro = await Record.ExceptionAsync(() => lojaA.SaveAsync(default));
        segundo(lidoPorB);
        var erroDoSegundo = await Record.ExceptionAsync(() => lojaB.SaveAsync(default));
        return (erroDoPrimeiro, erroDoSegundo);
    }

    [Fact]
    public async Task O_resultado_tardio_de_quem_perdeu_o_lease_nao_troca_o_SendUnknown_ja_gravado_por_Sent()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ostzumbi");
        var (_, operacao) = await NovoDespachoEmAndamentoAsync(c);

        // O novo Worker acha o despacho em andamento e grava SendUnknown; o Worker antigo, que ficou com a chamada presa, recebe "aceito" e tenta gravar Sent.
        var (novoDono, antigo) = await DoisLeemEGravamAsync(c, operacao, d => d.MarkSendUnknown("interrupted", Agora.AddSeconds(5)), d => d.MarkSent("wamid.TARDIO", Agora.AddSeconds(9)));

        Assert.Null(novoDono);
        Assert.IsType<OutboundStateConflictException>(antigo);
        var gravado = await LerAsync(c, operacao);
        Assert.Equal((OutboundState.SendUnknown, "interrupted", null), (gravado.State, gravado.ErrorCode, gravado.ProviderMessageId));
    }

    [Fact]
    public async Task O_tratamento_de_morto_nao_troca_o_Sent_que_o_handler_ja_gravou_por_SendUnknown()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ostmorto");
        var (_, operacao) = await NovoDespachoEmAndamentoAsync(c);

        // O handler terminou primeiro (Sent); o tratamento de morto de outro relay leu o despacho ainda em andamento e tenta gravar SendUnknown.
        var (handler, morto) = await DoisLeemEGravamAsync(c, operacao, d => d.MarkSent("wamid.REAL", Agora.AddSeconds(2)), d => d.MarkSendUnknown("interrupted", Agora.AddSeconds(3)));

        Assert.Null(handler);
        Assert.IsType<OutboundStateConflictException>(morto);
        var gravado = await LerAsync(c, operacao);
        Assert.Equal((OutboundState.Sent, "wamid.REAL", null), (gravado.State, gravado.ProviderMessageId, gravado.ErrorCode));
    }

    [Fact]
    public async Task Nem_a_falha_definitiva_de_um_sobrescreve_o_desfecho_do_outro()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ostfalha");
        var (_, operacao) = await NovoDespachoEmAndamentoAsync(c);

        var (primeiro, segundo) = await DoisLeemEGravamAsync(c, operacao, d => d.MarkFailed("window-closed", Agora.AddSeconds(2)), d => d.MarkFailed("relay-dead", Agora.AddSeconds(3)));

        Assert.Null(primeiro);
        Assert.IsType<OutboundStateConflictException>(segundo);
        Assert.Equal("window-closed", (await LerAsync(c, operacao)).ErrorCode);
    }

    [Fact]
    public async Task Varios_processos_ao_mesmo_tempo_so_um_grava_o_desfecho_e_os_demais_perdem_sem_alterar_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ostcorrida");
        var (_, operacao) = await NovoDespachoEmAndamentoAsync(c);

        // Oito processos leem o mesmo despacho em andamento e correm para gravar, cada um, um desfecho diferente.
        var largada = new TaskCompletionSource();
        var lidos = new CountdownEvent(8);
        var tarefas = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await using var escopo = c.Banco.NovoEscopo(c.ContextoDeSistema);
            var loja = escopo.ServiceProvider.GetRequiredService<IOutboundMessageStore>();
            var lido = (await loja.FindByOperationAsync(operacao, default))!;
            lidos.Signal();
            await largada.Task;
            if (i % 2 == 0)
            {
                lido.MarkSendUnknown($"interrupted-{i}", Agora.AddSeconds(i));
            }
            else
            {
                lido.MarkSent($"wamid.{i}", Agora.AddSeconds(i));
            }

            return await Record.ExceptionAsync(() => loja.SaveAsync(default));
        })).ToList();

        // Todos leram o despacho em andamento; só então a largada (todos gravam ao mesmo tempo).
        Assert.True(await Task.Run(() => lidos.Wait(TimeSpan.FromSeconds(30))));
        largada.SetResult();
        var resultados = await Task.WhenAll(tarefas);
        Assert.Single(resultados, e => e is null);
        Assert.Equal(7, resultados.Count(e => e is OutboundStateConflictException));

        // O que ficou gravado é exatamente o desfecho do único vencedor, inteiro (nunca uma mistura).
        var gravado = await LerAsync(c, operacao);
        Assert.True(
            (gravado.State == OutboundState.SendUnknown && gravado.ErrorCode!.StartsWith("interrupted-", StringComparison.Ordinal) && gravado.ProviderMessageId is null)
            || (gravado.State == OutboundState.Sent && gravado.ProviderMessageId!.StartsWith("wamid.", StringComparison.Ordinal) && gravado.ErrorCode is null),
            $"estado misturado: {gravado.State} {gravado.ErrorCode} {gravado.ProviderMessageId}");
    }

    [Fact]
    public async Task As_gravacoes_em_sequencia_do_mesmo_handler_continuam_valendo_sem_conflito()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ostsequencia");
        var mensagem = Guid.NewGuid();
        var operacao = Guid.NewGuid();

        // O caminho normal do Sender num só escopo: intenção, tentativa, desfecho e notificação, cada uma uma gravação sobre o estado anterior.
        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var loja = sp.GetRequiredService<IOutboundMessageStore>();
            var despacho = OutboundMessage.Intend(mensagem, c.Tenant.TenantId, operacao, ChannelInstanceId.New(), Agora);
            Assert.True(await loja.TryAddAsync(despacho, default));
            despacho.BeginAttempt(Agora);
            await loja.SaveAsync(default);
            despacho.MarkRetry("130429", Agora.AddSeconds(1));
            await loja.SaveAsync(default);
            despacho.BeginAttempt(Agora.AddSeconds(2));
            await loja.SaveAsync(default);
            despacho.MarkSent("wamid.OK", Agora.AddSeconds(3));
            await loja.SaveAsync(default);
            despacho.MarkNotified(Agora.AddSeconds(4));
            await loja.SaveAsync(default);
            return 0;
        });

        var gravado = await LerAsync(c, operacao);
        Assert.Equal((OutboundState.Sent, 2, "wamid.OK", true), (gravado.State, gravado.Attempts, gravado.ProviderMessageId, gravado.Notified));
    }
}
