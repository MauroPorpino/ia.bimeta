using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;

namespace IaBimeta.AiGateway.Testing;

/// <summary>
/// A suíte de contrato da porta <see cref="IAiProvider"/> (ADR 0017, critério 1): UMA suíte roda contra o dublê e, quando existirem, contra cada adaptador de
/// provedor (que a herdam e montam o cenário com HTTP simulado, sem rede e sem chave real). O que a porta promete vale para todos: a falha do provedor é
/// resultado e não exceção, o resultado é a resposta OU a falha, o consumo e a latência vêm normalizados, o cancelamento é honrado e o identificador do
/// provedor é estável e válido.
/// </summary>
public abstract class AiProviderContractTests
{
    protected static readonly AiUsage UsoDoCenario = new(1_200, 200, 100, 80, ReasoningTokens: 30);

    /// <summary>Um provedor que responde <paramref name="text"/> com o consumo informado.</summary>
    protected abstract IAiProvider CreateSucceeding(string text, AiUsage usage);

    /// <summary>Um provedor que falha com a categoria informada (o equivalente, no adaptador, ao código de erro do provedor).</summary>
    protected abstract IAiProvider CreateFailing(AiErrorKind kind);

    /// <summary>Um provedor que nunca responde: só termina quando o cancelamento chega.</summary>
    protected abstract IAiProvider CreateHanging();

    protected static AiCompletionRequest Request(AiOutputMode modo = AiOutputMode.Text) =>
        new(
            Guid.NewGuid(),
            "turno-contrato",
            "model-a1",
            "profile-a",
            "Você é a atendente virtual.",
            new[] { new AiMessage(AiMessageRole.User, "Olá") },
            200,
            TimeSpan.FromSeconds(5),
            modo,
            modo == AiOutputMode.StructuredJson
                ? new AiStructuredOutput("decisao", """{ "type": "object", "properties": {}, "required": [], "additionalProperties": false }""")
                : null,
            new AiCredential("CANARIO-CHAVE-DE-CONTRATO"));

    [Fact]
    public void O_identificador_do_provedor_e_valido_e_estavel()
    {
        var provedor = CreateSucceeding("oi", UsoDoCenario);

        Assert.True(AiModelCatalog.IsValidProviderId(provedor.ProviderId));
        Assert.Equal(provedor.ProviderId, provedor.ProviderId);
    }

    [Fact]
    public async Task O_sucesso_traz_a_resposta_o_consumo_normalizado_e_a_latencia_sem_erro()
    {
        var provedor = CreateSucceeding("Atendemos das 8h às 18h.", UsoDoCenario);

        var resultado = await provedor.CompleteAsync(Request(), CancellationToken.None);

        Assert.True(resultado.IsSuccess);
        Assert.Null(resultado.Error);
        Assert.Equal("Atendemos das 8h às 18h.", resultado.Completion!.Text);
        Assert.Equal(AiStopReason.Completed, resultado.Completion.StopReason);
        Assert.Equal(UsoDoCenario, resultado.Usage);
        Assert.True(resultado.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task A_saida_estruturada_tambem_devolve_a_resposta_normalizada()
    {
        var provedor = CreateSucceeding("""{"action":"reply"}""", UsoDoCenario);

        var resultado = await provedor.CompleteAsync(Request(AiOutputMode.StructuredJson), CancellationToken.None);

        Assert.True(resultado.IsSuccess);
        Assert.Equal("""{"action":"reply"}""", resultado.Completion!.Text);
    }

    [Theory]
    [InlineData(AiErrorKind.Transient)]
    [InlineData(AiErrorKind.InvalidCredentials)]
    [InlineData(AiErrorKind.QuotaOrBilling)]
    [InlineData(AiErrorKind.InvalidRequest)]
    [InlineData(AiErrorKind.ContextTooLong)]
    [InlineData(AiErrorKind.Refused)]
    public async Task A_falha_do_provedor_e_resultado_normalizado_e_nunca_excecao(AiErrorKind tipo)
    {
        var provedor = CreateFailing(tipo);

        var resultado = await provedor.CompleteAsync(Request(), CancellationToken.None);

        Assert.False(resultado.IsSuccess);
        Assert.Null(resultado.Completion);
        Assert.Equal(tipo, resultado.Error!.Kind);
        Assert.True(resultado.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task O_adaptador_nunca_devolve_NotConfigured_nem_TenantLimitExceeded_que_so_o_AiGateway_produz()
    {
        foreach (var tipo in new[] { AiErrorKind.Transient, AiErrorKind.InvalidCredentials, AiErrorKind.QuotaOrBilling })
        {
            var resultado = await CreateFailing(tipo).CompleteAsync(Request(), CancellationToken.None);

            Assert.NotEqual(AiErrorKind.NotConfigured, resultado.Error!.Kind);
            Assert.NotEqual(AiErrorKind.TenantLimitExceeded, resultado.Error.Kind);
        }
    }

    [Fact]
    public async Task O_cancelamento_e_honrado()
    {
        var provedor = CreateHanging();
        using var cancelamento = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provedor.CompleteAsync(Request(), cancelamento.Token));
    }

    [Fact]
    public async Task O_resultado_nunca_carrega_a_credencial_nem_o_prompt()
    {
        var provedor = CreateSucceeding("resposta", UsoDoCenario);

        var sucesso = await provedor.CompleteAsync(Request(), CancellationToken.None);
        var falha = await CreateFailing(AiErrorKind.InvalidCredentials).CompleteAsync(Request(), CancellationToken.None);

        foreach (var texto in new[] { sucesso.ToString(), falha.ToString() })
        {
            Assert.DoesNotContain("CANARIO-CHAVE-DE-CONTRATO", texto);
            Assert.DoesNotContain("Você é a atendente virtual.", texto);
        }
    }
}
