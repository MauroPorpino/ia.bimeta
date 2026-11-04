using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Tests;

/// <summary>
/// A C1.4 no Core (ADR 0017, ajustes A a D do ledger): a estimativa de entrada é uma heurística com margem (e a reserva de orçamento), o teto de saída vai de fato à requisição
/// ao provedor, o consumo tem quatro situações (estimado, reservado, confirmado e indeterminado), a contabilidade fora do ar falha fechada, e a conclusão é idempotente. Tudo com
/// dublês: nenhuma rede, nenhuma chave real.
/// </summary>
public class GatewayReservationAndEstimationTests
{
    private static readonly TenantId A = GatewayHarness.TenantA;
    private static readonly TimeSpan Hora = TimeSpan.FromHours(1);

    private static void Limita(GatewayHarness h, AiTenantLimits limites, AiTenantStatus status = AiTenantStatus.Enabled) =>
        h.Configurations.Set(new AiTenantConfiguration(A, GatewayHarness.ProviderAId, "model-a1", AiCredentialSource.Byok, limites, status));

    private static AiLeaseRequest Vaga(Guid id, DateTimeOffset agora, long entrada = 50, int saida = 100, AiTenantLimits? limites = null, TimeSpan? duracao = null) =>
        new(A, id, "turno", GatewayHarness.ProviderAId, "model-a1", limites ?? GatewayHarness.GenerousLimits, agora, entrada, saida, duracao ?? TimeSpan.FromSeconds(60));

    private static AiRequestRecord Registro(AiLease vaga, AiUsageCertainty certeza, AiUsage? consumo = null) =>
        new(A, vaga.AiRequestId, "turno", GatewayHarness.ProviderAId, "model-a1", consumo ?? AiUsage.None, new AiCostEstimate(0m, "USD"), TimeSpan.FromMilliseconds(5),
            AiRequestOutcome.Succeeded, null, null, null, null, vaga.StartedAt, certeza);

    // A estimativa

    [Fact]
    public void A_estimativa_de_entrada_arredonda_para_cima_aplica_a_margem_e_soma_os_acrescimos()
    {
        var opcoes = new AiGatewayOptions { CharactersPerToken = 4, InputSafetyMargin = 1.5, PerMessageOverheadTokens = 10, RequestOverheadTokens = 20 };
        var pedido = new AiGenerationRequest("t", new string('s', 100), new[] { new AiMessage(AiMessageRole.User, new string('m', 101)) }, 100, TimeSpan.FromSeconds(5));

        // 201 caracteres ÷ 4 × 1,5 = 75,375 -> 76; mais 10 por mensagem e 20 da requisição.
        Assert.Equal(201, AiTokenEstimator.CountInputCharacters(pedido));
        Assert.Equal(76 + 10 + 20, AiTokenEstimator.EstimateInputTokens(pedido, opcoes));
    }

    [Fact]
    public void A_estimativa_cresce_com_o_texto_e_com_as_mensagens_e_nunca_e_menor_que_o_acrescimo_da_requisicao()
    {
        var opcoes = new AiGatewayOptions();
        AiGenerationRequest Pedido(int caracteres, int mensagens) => new("t", "s", Enumerable.Range(0, mensagens).Select(_ => new AiMessage(AiMessageRole.User, new string('x', caracteres))).ToArray(), 100, TimeSpan.FromSeconds(5));

        Assert.True(AiTokenEstimator.EstimateInputTokens(Pedido(10, 1), opcoes) < AiTokenEstimator.EstimateInputTokens(Pedido(1000, 1), opcoes));
        Assert.True(AiTokenEstimator.EstimateInputTokens(Pedido(10, 1), opcoes) < AiTokenEstimator.EstimateInputTokens(Pedido(10, 5), opcoes));
        Assert.True(AiTokenEstimator.EstimateInputTokens(Pedido(0, 1), opcoes) >= opcoes.RequestOverheadTokens);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Caracteres_por_token_invalido_e_recusado_na_partida(double valor)
    {
        Assert.Throws<AiGatewayConfigurationException>(() => new AiGatewayOptions { CharactersPerToken = valor }.EnsureValid());
    }

    [Theory]
    [InlineData(0.99)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void A_margem_menor_que_um_e_recusada_porque_a_estimativa_nunca_subestima_por_configuracao(double valor)
    {
        Assert.Throws<AiGatewayConfigurationException>(() => new AiGatewayOptions { InputSafetyMargin = valor }.EnsureValid());
    }

    [Fact]
    public void Acrescimos_negativos_teto_de_entrada_nao_positivo_e_margem_de_vaga_negativa_sao_recusados()
    {
        Assert.Throws<AiGatewayConfigurationException>(() => new AiGatewayOptions { PerMessageOverheadTokens = -1 }.EnsureValid());
        Assert.Throws<AiGatewayConfigurationException>(() => new AiGatewayOptions { RequestOverheadTokens = -1 }.EnsureValid());
        Assert.Throws<AiGatewayConfigurationException>(() => new AiGatewayOptions { MaxInputCharacters = 0 }.EnsureValid());
        Assert.Throws<AiGatewayConfigurationException>(() => new AiGatewayOptions { LeaseMargin = TimeSpan.FromSeconds(-1) }.EnsureValid());
        new AiGatewayOptions { MaxInputCharacters = 1000 }.EnsureValid();
    }

    // O teto de saída vai à requisição; a reserva e os limites

    [Fact]
    public async Task O_teto_de_saida_aplicado_vai_na_requisicao_ao_provedor_o_menor_entre_o_pedido_o_tenant_e_o_modelo()
    {
        var h = new GatewayHarness();
        Limita(h, GatewayHarness.GenerousLimits with { MaxOutputTokens = 200 });
        h.AsSystem(A);

        await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 300)); // o tenant limita a 200
        await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 150)); // o pedido é menor que o do tenant

        Assert.Equal(new[] { 200, 150 }, h.ProviderA.Requests.Select(r => r.MaxOutputTokens).ToArray());
    }

    [Fact]
    public async Task O_teto_de_saida_do_modelo_tambem_limita_a_requisicao()
    {
        var h = new GatewayHarness();
        Limita(h, GatewayHarness.GenerousLimits with { MaxOutputTokens = 9_000 }); // o modelo do catálogo só permite 4.000
        h.AsSystem(A);

        await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 8_000));

        Assert.Equal(4_000, Assert.Single(h.ProviderA.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task A_vaga_retem_a_entrada_estimada_mais_o_teto_de_saida_e_a_confirmacao_registra_o_real_ao_lado_da_estimativa()
    {
        var h = new GatewayHarness();
        var portao = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.ProviderA.BlocksUntil(portao);
        h.AsSystem(A);

        var chamada = Task.Run(() => h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100)));
        while (h.Ledger.OpenReservedTokens(A) == 0)
        {
            await Task.Delay(5);
        }

        // 57 caracteres ÷ 3 × 1,3 = 24,7 -> 25, mais 8 da mensagem e 16 da requisição = 49 de entrada; mais 100 de saída.
        Assert.Equal(149, h.Ledger.OpenReservedTokens(A));
        Assert.Equal(1, h.Ledger.InFlight(A));
        portao.SetResult();
        var resultado = await chamada;

        Assert.True(resultado.IsSuccess);
        Assert.Equal(0, h.Ledger.OpenReservedTokens(A));
        Assert.Equal((1, 120L), h.Ledger.UsageInWindowOf(A, h.Clock.GetUtcNow(), Hora)); // o REAL (100 + 20), não a reserva
    }

    [Fact]
    public async Task Uma_reserva_que_nao_cabe_no_periodo_recusa_a_chamada_sem_chamar_o_provedor()
    {
        var h = new GatewayHarness();
        Limita(h, GatewayHarness.GenerousLimits with { MaxTokensPerPeriod = 100 }); // a reserva (149) já passa de 100
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100));

        Assert.Equal(AiErrorKind.TenantLimitExceeded, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.PeriodTokenLimit, resultado.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
        Assert.Equal(0, h.Ledger.InFlight(A));
    }

    [Fact]
    public async Task O_teto_de_tokens_por_chamada_do_tenant_recusa_antes_da_vaga_e_do_provedor()
    {
        var h = new GatewayHarness();
        Limita(h, GatewayHarness.GenerousLimits with { MaxTokensPerCall = 120 });
        h.AsSystem(A);

        var acima = await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100)); // reserva 149 > 120

        Assert.Equal(AiErrorKind.TenantLimitExceeded, acima.Failure!.Kind);
        Assert.Equal(AiFailureCodes.PerCallTokenLimit, acima.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
        Assert.Empty(h.Ledger.Records);
        Assert.Equal((0, 0L), h.Ledger.UsageInWindowOf(A, h.Clock.GetUtcNow(), Hora)); // nem a chamada foi contada

        // Com uma saída menor a reserva cabe (49 + 60 = 109).
        Assert.True((await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 60))).IsSuccess);
    }

    [Fact]
    public async Task O_pedido_que_nao_cabe_na_janela_de_contexto_do_modelo_pela_estimativa_e_ContextTooLong_sem_chamar_o_provedor()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);
        var gigante = GatewayHarness.Request(text: new string('x', 700_000)); // 700 mil caracteres: muito mais que os 200 mil tokens do modelo, pela estimativa

        var resultado = await h.Gateway.GenerateAsync(gigante);

        Assert.Equal(AiErrorKind.ContextTooLong, resultado.Failure!.Kind);
        Assert.False(resultado.Failure.Retryable);
        Assert.Equal(AiFailureCodes.ContextWindowExceeded, resultado.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
    }

    [Fact]
    public async Task O_teto_de_caracteres_da_entrada_da_plataforma_recusa_a_chamada()
    {
        var h = new GatewayHarness(new AiGatewayOptions { MaxInputCharacters = 100 });
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request(text: new string('x', 200)));

        Assert.Equal(AiErrorKind.TenantLimitExceeded, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.InputTooLarge, resultado.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
    }

    // Habilitação

    [Theory]
    [InlineData(AiTenantStatus.Disabled)]
    [InlineData(AiTenantStatus.Blocked)]
    [InlineData((AiTenantStatus)99)]
    public async Task A_IA_desabilitada_ou_bloqueada_nunca_chama_o_provedor_nem_usa_a_vaga(AiTenantStatus status)
    {
        var h = new GatewayHarness();
        Limita(h, GatewayHarness.GenerousLimits, status);
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.NotConfigured, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.TenantAiDisabled, resultado.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
        Assert.Empty(h.Credentials.Resolutions); // nem a credencial foi decifrada
        Assert.Empty(h.Ledger.Records);
    }

    // A chave mestra

    private sealed class ChaveIndisponivel : IAiCredentialResolver
    {
        public Task<AiCredential?> ResolveAsync(TenantId tenantId, string providerId, CancellationToken cancellationToken) =>
            throw new AiSecretsUnavailableException("sem chave ativa");
    }

    [Fact]
    public async Task Sem_a_chave_mestra_a_falha_e_da_plataforma_NotConfigured_secrets_unavailable_e_o_provedor_nao_e_chamado()
    {
        var h = new GatewayHarness();
        var gateway = new AiGatewayService(h.Holder, h.Configurations, h.Selector, new ChaveIndisponivel(), h.Ledger, h.Clock);
        h.AsSystem(A);

        var resultado = await gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.NotConfigured, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.SecretsUnavailable, resultado.Failure.Code);
        Assert.False(resultado.Failure.Retryable);
        Assert.Equal(0, h.ProviderA.CallCount);
        Assert.Empty(h.Ledger.Records);
    }

    // A contabilidade fora do ar

    [Fact]
    public async Task Com_a_contabilidade_fora_do_ar_antes_da_chamada_a_falha_e_transitoria_e_o_provedor_nao_e_chamado()
    {
        var h = new GatewayHarness();
        h.Ledger.Unavailable = true;
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.Transient, resultado.Failure!.Kind);
        Assert.True(resultado.Failure.Retryable);
        Assert.Equal(AiFailureCodes.LedgerUnavailable, resultado.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount); // falha FECHADA: sem saber o que o tenant gastou, nada é chamado
    }

    [Fact]
    public async Task Se_a_contabilidade_cair_depois_da_chamada_a_resposta_ja_obtida_nao_se_perde_e_a_vaga_expira_como_indeterminada()
    {
        var h = new GatewayHarness();
        var portao = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.ProviderA.BlocksUntil(portao);
        h.AsSystem(A);
        var chamada = Task.Run(() => h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100)));
        while (h.Ledger.OpenReservedTokens(A) == 0)
        {
            await Task.Delay(5);
        }

        h.Ledger.Unavailable = true; // cai no meio da chamada
        portao.SetResult();
        var resultado = await chamada;
        h.Ledger.Unavailable = false;

        Assert.True(resultado.IsSuccess); // o consumidor recebe a resposta
        Assert.Equal(149, h.Ledger.OpenReservedTokens(A)); // a linha ficou aberta...

        // ...e expira: no próximo pedido de vaga vira indeterminada e cobra a reserva (o lado seguro).
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100))).IsSuccess);
        Assert.Equal(149, h.Ledger.IndeterminateTokensInWindowOf(A, h.Clock.GetUtcNow(), Hora));
    }

    // A certeza do consumo

    public static IEnumerable<object[]> Falhas()
    {
        // (certeza declarada pelo adaptador, consumo informado, estado esperado: tokens confirmados, tokens indeterminados)
        yield return new object[] { AiUsageCertainty.Unknown, null!, 0L, 149L };
        yield return new object[] { AiUsageCertainty.NoneIncurred, null!, 0L, 0L };
        yield return new object[] { AiUsageCertainty.Reported, new AiUsage(40, 0, 0, 10), 50L, 0L };
        yield return new object[] { AiUsageCertainty.NoneIncurred, new AiUsage(40, 0, 0, 10), 50L, 0L }; // incoerente: vale o que ele informou (lado seguro)
        yield return new object[] { (AiUsageCertainty)99, null!, 0L, 149L }; // valor fora do enum: desconhecido
    }

    [Theory]
    [MemberData(nameof(Falhas))]
    public async Task A_falha_do_provedor_e_contabilizada_pela_certeza_do_consumo_e_nunca_assume_ausencia_sem_evidencia(
        AiUsageCertainty certeza, AiUsage? consumo, long confirmados, long indeterminados)
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Fail(AiErrorKind.Transient, code: "http-503", usage: consumo, usageCertainty: certeza));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100));

        Assert.False(resultado.IsSuccess);
        Assert.Equal((1, confirmados + indeterminados), h.Ledger.UsageInWindowOf(A, h.Clock.GetUtcNow(), Hora));
        Assert.Equal(indeterminados, h.Ledger.IndeterminateTokensInWindowOf(A, h.Clock.GetUtcNow(), Hora));
        Assert.Equal(0, h.Ledger.OpenReservedTokens(A));
    }

    [Fact]
    public async Task O_adaptador_que_lanca_excecao_e_o_tempo_esgotado_ficam_indeterminados_e_cobram_a_reserva()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        h.ProviderA.Throws(new InvalidOperationException("quebrou"));
        var comExcecao = await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100));
        h.ProviderA.Hangs();
        var esgotada = await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100, timeout: TimeSpan.FromMilliseconds(50)));

        Assert.Equal(AiFailureCodes.ProviderException, comExcecao.Failure!.Code);
        Assert.Equal(AiFailureCodes.Timeout, esgotada.Failure!.Code);
        Assert.Equal(2 * 149L, h.Ledger.IndeterminateTokensInWindowOf(A, h.Clock.GetUtcNow(), Hora));
    }

    [Fact]
    public async Task A_desistencia_do_chamador_fica_indeterminada_e_cobra_a_reserva_e_a_vaga_nunca_vaza()
    {
        var h = new GatewayHarness();
        h.ProviderA.Hangs();
        h.AsSystem(A);
        using var cancelamento = new CancellationTokenSource();

        var chamada = Task.Run(() => h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100), cancelamento.Token));
        while (h.Ledger.OpenReservedTokens(A) == 0)
        {
            await Task.Delay(5);
        }

        cancelamento.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chamada);

        Assert.Equal(0, h.Ledger.InFlight(A));
        Assert.Equal(149, h.Ledger.IndeterminateTokensInWindowOf(A, h.Clock.GetUtcNow(), Hora));
    }

    [Fact]
    public async Task Resposta_com_sucesso_sempre_e_confirmada_com_o_consumo_informado_mesmo_que_o_adaptador_declare_outra_certeza()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(AiCompletionResult.Success(new AiCompletion("ok", AiStopReason.Completed), new AiUsage(30, 0, 0, 5), TimeSpan.Zero));
        h.AsSystem(A);

        await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100));

        Assert.Equal(AiUsageCertainty.Reported, Assert.Single(h.Ledger.Records).UsageCertainty);
        Assert.Equal((1, 35L), h.Ledger.UsageInWindowOf(A, h.Clock.GetUtcNow(), Hora));
    }

    [Fact]
    public async Task O_consumo_real_acima_da_reserva_e_registrado_como_excedente_e_a_conclusao_nunca_recusa()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Succeed("ok", new AiUsage(400, 0, 0, 100))); // 500 reais contra 149 reservados
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 100));

        Assert.True(resultado.IsSuccess);
        Assert.Equal((1, 500L), h.Ledger.UsageInWindowOf(A, h.Clock.GetUtcNow(), Hora));
        Assert.Equal(500 - 149, h.Ledger.OverageTokensInWindowOf(A, h.Clock.GetUtcNow(), Hora));
    }

    // O ledger: expiração, reconciliação e idempotência

    [Fact]
    public async Task A_vaga_expirada_vira_indeterminada_no_proximo_pedido_e_a_conclusao_tardia_reconcilia_uma_unica_vez()
    {
        var ledger = new InMemoryAiUsageLedger();
        var agora = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var concessao = await ledger.TryBeginAsync(Vaga(id, agora, entrada: 50, saida: 100), CancellationToken.None);
        var vaga = concessao.Lease!;
        Assert.Equal(agora + TimeSpan.FromSeconds(60), vaga.ExpiresAt);
        Assert.Equal(150, ledger.OpenReservedTokens(A));

        // Passou da validade: o próximo pedido converte a vaga em indeterminada (cobra a reserva).
        var depois = agora + TimeSpan.FromMinutes(5);
        Assert.NotNull((await ledger.TryBeginAsync(Vaga(Guid.NewGuid(), depois), CancellationToken.None)).Lease);
        Assert.Equal(150, ledger.IndeterminateTokensInWindowOf(A, agora, Hora));

        // A conclusão tardia, com o consumo real, RECONCILIA: o indeterminado sai e o real entra, e só uma vez.
        await ledger.CompleteAsync(vaga, Registro(vaga, AiUsageCertainty.Reported, new AiUsage(60, 0, 0, 20)), CancellationToken.None);
        Assert.Equal(0, ledger.IndeterminateTokensInWindowOf(A, agora, Hora));
        Assert.Equal((2, 80L), ledger.UsageInWindowOf(A, agora, Hora)); // 80 reais (60 + 20); a reserva aberta da segunda chamada ainda não é consumo confirmado
    }

    [Fact]
    public async Task Concluir_de_novo_nunca_conta_a_mesma_chamada_duas_vezes()
    {
        var ledger = new InMemoryAiUsageLedger();
        var agora = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var vaga = (await ledger.TryBeginAsync(Vaga(Guid.NewGuid(), agora), CancellationToken.None)).Lease!;
        var registro = Registro(vaga, AiUsageCertainty.Reported, new AiUsage(60, 0, 0, 20));

        await ledger.CompleteAsync(vaga, registro, CancellationToken.None);
        await ledger.CompleteAsync(vaga, registro, CancellationToken.None);
        await ledger.CompleteAsync(vaga, registro with { Usage = new AiUsage(9999, 0, 0, 9999) }, CancellationToken.None); // até com outro valor: o confirmado é definitivo

        Assert.Equal((1, 80L), ledger.UsageInWindowOf(A, agora, Hora));
        Assert.Single(ledger.Records);
    }

    [Fact]
    public async Task Concluir_como_desconhecido_uma_vaga_que_ja_expirou_nao_conta_de_novo()
    {
        var ledger = new InMemoryAiUsageLedger();
        var agora = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var vaga = (await ledger.TryBeginAsync(Vaga(Guid.NewGuid(), agora, entrada: 50, saida: 100), CancellationToken.None)).Lease!;
        await ledger.TryBeginAsync(Vaga(Guid.NewGuid(), agora + TimeSpan.FromMinutes(5)), CancellationToken.None); // expira a primeira

        await ledger.CompleteAsync(vaga, Registro(vaga, AiUsageCertainty.Unknown), CancellationToken.None);
        await ledger.CompleteAsync(vaga, Registro(vaga, AiUsageCertainty.Unknown), CancellationToken.None);

        Assert.Equal(150, ledger.IndeterminateTokensInWindowOf(A, agora, Hora)); // 150, não 300
    }

    [Fact]
    public async Task Concluir_uma_vaga_que_nunca_foi_concedida_e_defeito_e_nao_cria_consumo()
    {
        var ledger = new InMemoryAiUsageLedger();
        var estranha = new AiLease(A, Guid.NewGuid(), DateTimeOffset.UtcNow, 10, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.CompleteAsync(estranha, Registro(estranha, AiUsageCertainty.Reported), CancellationToken.None));
    }
}
