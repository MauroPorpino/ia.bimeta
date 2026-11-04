using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Tests;

/// <summary>
/// Os limites básicos por tenant (ADR 0017, seção I.2 e critério 9): simultaneidade nunca excedida, chamadas e tokens por período, e a vaga devolvida em todo
/// desfecho. Os valores são configuração do tenant (aqui, valores de teste): nenhum vem do código.
/// </summary>
public class GatewayLimitsTests
{
    private static readonly TenantId A = GatewayHarness.TenantA;
    private static readonly TenantId B = GatewayHarness.TenantB;

    private static void Limita(GatewayHarness h, TenantId tenant, string provedor, string modelo, AiTenantLimits limites) =>
        h.Configurations.Set(new AiTenantConfiguration(tenant, provedor, modelo, AiCredentialSource.Byok, limites));

    private static async Task EsperarAteAsync(Func<bool> condicao, string descricao)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condicao())
        {
            Assert.True(DateTime.UtcNow < limite, $"Esgotou o tempo esperando: {descricao}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_simultaneidade_do_tenant_nunca_e_excedida_e_as_chamadas_excedentes_sao_recusadas_na_hora()
    {
        var h = new GatewayHarness();
        Limita(h, A, GatewayHarness.ProviderAId, "model-a1", GatewayHarness.GenerousLimits with { MaxConcurrentCalls = 2 });
        var portao = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.ProviderA.BlocksUntil(portao);
        h.AsSystem(A);

        // Cinco chamadas ao mesmo tempo: duas ocupam as vagas e ficam esperando; as outras três são recusadas sem chamar o provedor.
        var chamadas = Enumerable.Range(0, 5).Select(_ => Task.Run(() => h.Gateway.GenerateAsync(GatewayHarness.Request()))).ToList();
        await EsperarAteAsync(() => h.ProviderA.CallCount == 2, "as duas primeiras chamadas chegarem ao provedor");
        await EsperarAteAsync(() => chamadas.Count(c => c.IsCompleted) == 3, "as três chamadas excedentes serem recusadas");

        Assert.Equal(2, h.Ledger.InFlight(A));
        portao.SetResult();
        var resultados = await Task.WhenAll(chamadas);

        Assert.Equal(2, resultados.Count(r => r.IsSuccess));
        var recusadas = resultados.Where(r => !r.IsSuccess).ToList();
        Assert.Equal(3, recusadas.Count);
        Assert.All(recusadas, r =>
        {
            Assert.Equal(AiErrorKind.TenantLimitExceeded, r.Failure!.Kind);
            Assert.False(r.Failure.Retryable);
            Assert.Equal(AiFailureCodes.ConcurrencyLimit, r.Failure.Code);
        });
        Assert.Equal(2, h.Ledger.MaxInFlightObserved(A));
        Assert.True(h.ProviderA.MaxObservedConcurrency <= 2);
        Assert.Equal(2, h.ProviderA.CallCount);
        Assert.Equal(0, h.Ledger.InFlight(A));
    }

    [Fact]
    public async Task Ao_liberar_a_vaga_a_proxima_chamada_passa()
    {
        var h = new GatewayHarness();
        Limita(h, A, GatewayHarness.ProviderAId, "model-a1", GatewayHarness.GenerousLimits with { MaxConcurrentCalls = 1 });
        h.AsSystem(A);

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await h.Gateway.GenerateAsync(GatewayHarness.Request())).IsSuccess);
        }

        Assert.Equal(5, h.ProviderA.CallCount);
        Assert.Equal(1, h.Ledger.MaxInFlightObserved(A));
    }

    [Fact]
    public async Task A_vaga_volta_tambem_quando_o_provedor_falha_para_nao_travar_o_tenant()
    {
        var h = new GatewayHarness();
        Limita(h, A, GatewayHarness.ProviderAId, "model-a1", GatewayHarness.GenerousLimits with { MaxConcurrentCalls = 1 });
        h.ProviderA.Returns(h.ProviderA.Fail(AiErrorKind.Transient));
        h.AsSystem(A);

        for (var i = 0; i < 5; i++)
        {
            var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());
            Assert.Equal(AiErrorKind.Transient, resultado.Failure!.Kind); // nunca "limite": a vaga foi devolvida a cada falha
        }

        Assert.Equal(5, h.ProviderA.CallCount);
        Assert.Equal(0, h.Ledger.InFlight(A));
    }

    [Fact]
    public async Task O_limite_de_chamadas_do_periodo_recusa_a_excedente_e_libera_no_periodo_seguinte()
    {
        var h = new GatewayHarness();
        var limites = GatewayHarness.GenerousLimits with { MaxCallsPerPeriod = 2, Period = TimeSpan.FromHours(1) };
        Limita(h, A, GatewayHarness.ProviderAId, "model-a1", limites);
        h.AsSystem(A);

        var primeira = await h.Gateway.GenerateAsync(GatewayHarness.Request());
        var segunda = await h.Gateway.GenerateAsync(GatewayHarness.Request());
        var terceira = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.True(primeira.IsSuccess && segunda.IsSuccess);
        Assert.Equal(AiErrorKind.TenantLimitExceeded, terceira.Failure!.Kind);
        Assert.Equal(AiFailureCodes.PeriodCallLimit, terceira.Failure.Code);
        Assert.Equal(2, h.ProviderA.CallCount);

        h.Clock.Advance(TimeSpan.FromHours(1)); // outra janela
        Assert.True((await h.Gateway.GenerateAsync(GatewayHarness.Request())).IsSuccess);
        Assert.Equal(3, h.ProviderA.CallCount);
    }

    [Fact]
    public async Task O_limite_de_tokens_do_periodo_recusa_a_chamada_seguinte_e_libera_no_periodo_seguinte()
    {
        var h = new GatewayHarness();
        Limita(h, A, GatewayHarness.ProviderAId, "model-a1", GatewayHarness.GenerousLimits with { MaxTokensPerPeriod = 100 });
        h.ProviderA.Returns(h.ProviderA.Succeed("ok", new AiUsage(100, 0, 0, 20))); // 120 tokens por chamada
        h.AsSystem(A);

        var primeira = await h.Gateway.GenerateAsync(GatewayHarness.Request()); // 0 < 100: passa, e a última chamada pode ultrapassar o limite
        var segunda = await h.Gateway.GenerateAsync(GatewayHarness.Request()); // 120 >= 100: recusada

        Assert.True(primeira.IsSuccess);
        Assert.Equal(AiErrorKind.TenantLimitExceeded, segunda.Failure!.Kind);
        Assert.Equal(AiFailureCodes.PeriodTokenLimit, segunda.Failure.Code);
        Assert.Equal(1, h.ProviderA.CallCount);
        Assert.Equal((1, 120L), h.Ledger.UsageInWindowOf(A, h.Clock.GetUtcNow(), TimeSpan.FromHours(1)));

        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await h.Gateway.GenerateAsync(GatewayHarness.Request())).IsSuccess);
    }

    [Fact]
    public async Task O_limite_de_um_tenant_nao_afeta_o_outro()
    {
        var h = new GatewayHarness();
        Limita(h, A, GatewayHarness.ProviderAId, "model-a1", GatewayHarness.GenerousLimits with { MaxCallsPerPeriod = 1 });
        h.AsSystem(A);
        Assert.True((await h.Gateway.GenerateAsync(GatewayHarness.Request())).IsSuccess);
        Assert.False((await h.Gateway.GenerateAsync(GatewayHarness.Request())).IsSuccess); // o tenant A esgotou o período

        h.AsSystem(B);
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await h.Gateway.GenerateAsync(GatewayHarness.Request())).IsSuccess);
        }

        Assert.Equal((1, 120L), h.Ledger.UsageInWindowOf(A, h.Clock.GetUtcNow(), TimeSpan.FromHours(1)));
        Assert.Equal((3, 360L), h.Ledger.UsageInWindowOf(B, h.Clock.GetUtcNow(), TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task A_simultaneidade_de_um_tenant_no_limite_nao_bloqueia_o_outro_tenant()
    {
        var h = new GatewayHarness();
        Limita(h, A, GatewayHarness.ProviderAId, "model-a1", GatewayHarness.GenerousLimits with { MaxConcurrentCalls = 1 });
        var portao = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.ProviderA.BlocksUntil(portao);
        h.AsSystem(A);
        var presa = Task.Run(() => h.Gateway.GenerateAsync(GatewayHarness.Request()));
        await EsperarAteAsync(() => h.ProviderA.CallCount == 1, "a chamada do tenant A ocupar a única vaga");

        var excedida = await h.Gateway.GenerateAsync(GatewayHarness.Request());
        h.AsSystem(B);
        var doOutro = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiFailureCodes.ConcurrencyLimit, excedida.Failure!.Code);
        Assert.True(doOutro.IsSuccess);
        portao.SetResult();
        Assert.True((await presa).IsSuccess);
    }
}

/// <summary>
/// O isolamento entre tenants (ADR 0017, critério 10): o tenant do contexto decide tudo; nenhum dado, credencial, configuração, registro ou contagem de um tenant
/// chega a outro, e uma leitura que devolva a configuração de OUTRO tenant falha fechada.
/// </summary>
public class GatewayIsolationTests
{
    private static readonly TenantId A = GatewayHarness.TenantA;
    private static readonly TenantId B = GatewayHarness.TenantB;

    [Fact]
    public async Task Cada_tenant_chama_so_o_proprio_provedor_com_a_propria_credencial()
    {
        var h = new GatewayHarness();

        h.AsSystem(A);
        var deA = await h.Gateway.GenerateAsync(GatewayHarness.Request(text: "CANARIO-DO-TENANT-A"));
        h.AsSystem(B);
        var deB = await h.Gateway.GenerateAsync(GatewayHarness.Request(text: "CANARIO-DO-TENANT-B"));

        Assert.Equal("provider-a", deA.ProviderId);
        Assert.Equal("provider-b", deB.ProviderId);

        var pedidoA = Assert.Single(h.ProviderA.Requests);
        var pedidoB = Assert.Single(h.ProviderB.Requests);
        Assert.Equal(GatewayHarness.CredentialOfTenantA, pedidoA.Credential.Secret);
        Assert.Equal(GatewayHarness.CredentialOfTenantB, pedidoB.Credential.Secret);
        Assert.Equal("model-a1", pedidoA.ModelId);
        Assert.Equal("model-b1", pedidoB.ModelId);

        // Nenhum canário de um tenant aparece no pedido do outro.
        Assert.Equal("CANARIO-DO-TENANT-A", pedidoA.Messages[0].Text);
        Assert.Equal("CANARIO-DO-TENANT-B", pedidoB.Messages[0].Text);
        Assert.DoesNotContain(pedidoB.Messages, m => m.Text.Contains("TENANT-A"));
        Assert.DoesNotContain(pedidoA.Messages, m => m.Text.Contains("TENANT-B"));
    }

    [Fact]
    public async Task A_configuracao_e_a_credencial_so_sao_pedidas_para_o_tenant_do_contexto()
    {
        var h = new GatewayHarness();

        h.AsSystem(A);
        await h.Gateway.GenerateAsync(GatewayHarness.Request());
        h.AsSystem(B);
        await h.Gateway.GenerateAsync(GatewayHarness.Request());
        h.AsSystem(A);
        await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(new[] { A, B, A }, h.Configurations.Reads.ToArray());
        Assert.Equal(new[] { (A, "provider-a"), (B, "provider-b"), (A, "provider-a") }, h.Credentials.Resolutions.ToArray());
    }

    [Fact]
    public async Task Uma_leitura_que_vaza_a_configuracao_de_outro_tenant_falha_fechada_sem_chamar_provedor_nem_resolver_credencial()
    {
        var h = new GatewayHarness();
        h.Configurations.LeakedConfiguration = new AiTenantConfiguration(B, GatewayHarness.ProviderBId, "model-b1", AiCredentialSource.Byok, GatewayHarness.GenerousLimits);
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.NotConfigured, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.ConfigurationTenantMismatch, resultado.Failure.Code);
        Assert.Null(resultado.ProviderId);
        Assert.Null(resultado.ModelId);
        Assert.Equal(0, h.ProviderA.CallCount + h.ProviderB.CallCount);
        Assert.Empty(h.Credentials.Resolutions);
        Assert.Empty(h.Ledger.Records);
    }

    [Fact]
    public async Task O_registro_de_consumo_leva_o_tenant_do_contexto_e_a_contagem_de_um_nao_soma_no_outro()
    {
        var h = new GatewayHarness();

        h.AsSystem(A);
        await h.Gateway.GenerateAsync(GatewayHarness.Request());
        await h.Gateway.GenerateAsync(GatewayHarness.Request());
        h.AsSystem(B);
        await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(new[] { A, A, B }, h.Ledger.Records.Select(r => r.TenantId).ToArray());
        Assert.Equal(new[] { "provider-a", "provider-a", "provider-b" }, h.Ledger.Records.Select(r => r.ProviderId).ToArray());
        Assert.Equal((2, 240L), h.Ledger.UsageInWindowOf(A, h.Clock.GetUtcNow(), TimeSpan.FromHours(1)));
        Assert.Equal((1, 120L), h.Ledger.UsageInWindowOf(B, h.Clock.GetUtcNow(), TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task O_mesmo_gateway_nao_guarda_estado_de_um_tenant_para_o_proximo()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Fail(AiErrorKind.QuotaOrBilling));

        h.AsSystem(A);
        var primeira = await h.Gateway.GenerateAsync(GatewayHarness.Request());
        h.AsSystem(B);
        var segunda = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.QuotaOrBilling, primeira.Failure!.Kind);
        Assert.True(segunda.IsSuccess); // a falha de cobrança do tenant A não contamina o tenant B
    }

    [Fact]
    public async Task A_credencial_de_um_tenant_nunca_e_usada_com_o_provedor_de_outro()
    {
        var h = new GatewayHarness();
        // O tenant B só tem credencial para o provider-b. Configurado (por engano) para o provider-a, não encontra chave: falha fechada, sem emprestar a do tenant A.
        h.Configurations.Set(new AiTenantConfiguration(B, GatewayHarness.ProviderAId, "model-a1", AiCredentialSource.Byok, GatewayHarness.GenerousLimits));
        h.AsSystem(B);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiFailureCodes.CredentialMissing, resultado.Failure!.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
    }
}
