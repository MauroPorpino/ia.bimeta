using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Testing;

namespace IaBimeta.AiGateway.Tests;

/// <summary>A suíte de contrato da porta, executada contra o dublê de provedor (os adaptadores reais herdarão a mesma suíte nas fatias C4 e C6).</summary>
public class FakeAiProviderContractTests : AiProviderContractTests
{
    protected override IAiProvider CreateSucceeding(string text, AiUsage usage)
    {
        var fake = new FakeAiProvider("provider-a");
        return fake.Returns(fake.Succeed(text, usage));
    }

    protected override IAiProvider CreateFailing(AiErrorKind kind)
    {
        var fake = new FakeAiProvider("provider-a");
        return fake.Returns(fake.Fail(kind));
    }

    protected override IAiProvider CreateHanging() => new FakeAiProvider("provider-a").Hangs();
}

/// <summary>O próprio dublê: latência e falha injetáveis, pedidos registrados e concorrência observada (o que os testes do AiGateway e, depois, do Agents usam).</summary>
public class FakeAiProviderTests
{
    private static AiCompletionRequest Pedido(string texto = "Olá") =>
        new(Guid.NewGuid(), "turno", "model-a1", "profile-a", "sistema", new[] { new AiMessage(AiMessageRole.User, texto) }, 100, TimeSpan.FromSeconds(5),
            AiOutputMode.Text, null, new AiCredential("CANARIO"));

    [Fact]
    public async Task O_padrao_responde_com_texto_e_registra_o_pedido_recebido()
    {
        var fake = new FakeAiProvider("provider-a");
        var pedido = Pedido("pergunta do cliente");

        var resultado = await fake.CompleteAsync(pedido, CancellationToken.None);

        Assert.True(resultado.IsSuccess);
        Assert.Equal(1, fake.CallCount);
        Assert.Same(pedido, Assert.Single(fake.Requests));
    }

    [Fact]
    public async Task A_latencia_injetada_atrasa_a_chamada_e_aparece_no_resultado()
    {
        var fake = new FakeAiProvider("provider-a") { Latency = TimeSpan.FromMilliseconds(80) };

        var inicio = DateTime.UtcNow;
        var resultado = await fake.CompleteAsync(Pedido(), CancellationToken.None);

        Assert.True(DateTime.UtcNow - inicio >= TimeSpan.FromMilliseconds(60));
        Assert.Equal(TimeSpan.FromMilliseconds(80), resultado.Latency);
    }

    [Fact]
    public async Task A_falha_injetada_e_devolvida_e_a_excecao_injetada_e_lancada()
    {
        var fake = new FakeAiProvider("provider-a");
        fake.Returns(fake.Fail(AiErrorKind.Transient, TimeSpan.FromSeconds(3), "rate_limit"));
        var falha = await fake.CompleteAsync(Pedido(), CancellationToken.None);

        Assert.Equal(new AiError(AiErrorKind.Transient, TimeSpan.FromSeconds(3), "rate_limit"), falha.Error);

        fake.Throws(new InvalidOperationException("defeito do adaptador"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.CompleteAsync(Pedido(), CancellationToken.None));
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task A_concorrencia_maxima_observada_e_registrada_e_o_portao_controla_o_termino()
    {
        var fake = new FakeAiProvider("provider-a");
        var portao = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.BlocksUntil(portao);

        var chamadas = Enumerable.Range(0, 3).Select(_ => fake.CompleteAsync(Pedido(), CancellationToken.None)).ToList();
        Assert.Equal(3, fake.MaxObservedConcurrency);
        Assert.All(chamadas, c => Assert.False(c.IsCompleted));

        portao.SetResult();
        var resultados = await Task.WhenAll(chamadas);

        Assert.All(resultados, r => Assert.True(r.IsSuccess));
        Assert.Equal(3, fake.MaxObservedConcurrency);
    }

    [Fact]
    public async Task O_cancelamento_encerra_a_chamada_pendurada()
    {
        var fake = new FakeAiProvider("provider-a").Hangs();
        using var cancelamento = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.CompleteAsync(Pedido(), cancelamento.Token));
    }
}
