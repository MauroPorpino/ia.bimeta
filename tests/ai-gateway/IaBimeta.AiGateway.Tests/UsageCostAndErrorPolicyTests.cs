using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Testing;

namespace IaBimeta.AiGateway.Tests;

/// <summary>A normalização do consumo e a estimativa de custo (ADR 0017, seção I.1): estimativa por tipo de token, nunca negativa, nunca incoerente.</summary>
public class UsageAndCostTests
{
    [Fact]
    public void A_entrada_comum_custa_o_preco_de_entrada_por_milhao()
    {
        var custo = AiCostEstimator.Estimate(new AiUsage(1_000_000, 0, 0, 0), GatewayHarness.Prices);

        Assert.Equal(0.10m, custo.Amount);
        Assert.Equal("USD", custo.Currency);
    }

    [Fact]
    public void Cada_tipo_de_token_tem_o_proprio_preco_e_a_entrada_comum_exclui_cache_lido_e_escrito()
    {
        // entrada total 1.000.000 = 500.000 comuns + 400.000 lidas do cache + 100.000 escritas no cache; saída 200.000 (raciocínio incluído).
        var custo = AiCostEstimator.Estimate(new AiUsage(1_000_000, 400_000, 100_000, 200_000, ReasoningTokens: 50_000), GatewayHarness.Prices);

        // 500.000 x 0,10 + 400.000 x 0,01 + 100.000 x 0,125 + 200.000 x 0,50, por milhão
        Assert.Equal(0.05m + 0.004m + 0.0125m + 0.1m, custo.Amount);
    }

    [Fact]
    public void O_raciocinio_ja_esta_na_saida_e_nao_e_cobrado_duas_vezes()
    {
        var semDetalhe = AiCostEstimator.Estimate(new AiUsage(0, 0, 0, 1_000_000), GatewayHarness.Prices);
        var comDetalhe = AiCostEstimator.Estimate(new AiUsage(0, 0, 0, 1_000_000, ReasoningTokens: 900_000), GatewayHarness.Prices);

        Assert.Equal(0.5m, semDetalhe.Amount);
        Assert.Equal(semDetalhe.Amount, comDetalhe.Amount);
    }

    [Fact]
    public void Sem_consumo_o_custo_e_zero_e_a_moeda_acompanha_a_tabela()
    {
        var custo = AiCostEstimator.Estimate(AiUsage.None, new AiModelPrices("BRL", 1, 1, 1, 1));

        Assert.Equal(0m, custo.Amount);
        Assert.Equal("BRL", custo.Currency);
    }

    [Fact]
    public void O_custo_e_exato_sem_arredondamento()
    {
        var custo = AiCostEstimator.Estimate(new AiUsage(100, 0, 0, 20), GatewayHarness.Prices);

        Assert.Equal(0.00002m, custo.Amount); // (100 x 0,10 + 20 x 0,50) / 1.000.000
    }

    [Fact]
    public void Consumo_incoerente_e_corrigido_e_nunca_produz_custo_negativo()
    {
        // cache lido e escrito maiores que a entrada total, e valores negativos: o adaptador errou.
        var corrigido = AiUsageNormalizer.Normalize(new AiUsage(100, 500, 500, 10, ReasoningTokens: 99));

        Assert.Equal(new AiUsage(100, 100, 0, 10, ReasoningTokens: 10), corrigido);
        Assert.True(AiCostEstimator.Estimate(new AiUsage(100, 500, 500, 10), GatewayHarness.Prices).Amount >= 0m);

        var negativo = AiUsageNormalizer.Normalize(new AiUsage(-5, -1, -1, -7, ReasoningTokens: -3));
        Assert.Equal(new AiUsage(0, 0, 0, 0, ReasoningTokens: 0), negativo);
        Assert.Equal(0m, AiCostEstimator.Estimate(new AiUsage(-5, -1, -1, -7), GatewayHarness.Prices).Amount);
    }

    [Fact]
    public void Consumo_coerente_nao_muda_e_o_raciocinio_ausente_continua_ausente()
    {
        var usage = new AiUsage(1_000, 300, 100, 200);

        Assert.Equal(usage, AiUsageNormalizer.Normalize(usage));
        Assert.Null(AiUsageNormalizer.Normalize(usage).ReasoningTokens);
        Assert.Equal(AiUsage.None, AiUsageNormalizer.Normalize(null));
    }

    [Fact]
    public void A_escrita_de_cache_e_limitada_ao_que_sobra_depois_do_cache_lido()
    {
        var corrigido = AiUsageNormalizer.Normalize(new AiUsage(100, 80, 50, 0));

        Assert.Equal(80, corrigido.CachedInputTokens);
        Assert.Equal(20, corrigido.CacheWriteTokens);
        Assert.True(corrigido.CachedInputTokens + corrigido.CacheWriteTokens <= corrigido.InputTokens);
    }
}

/// <summary>A política de erro (ADR 0017, seções C.4 e G.4): só a falha transitória é retentável, e cota ou cobrança nunca é, mesmo com espera sugerida.</summary>
public class ErrorPolicyTests
{
    private static readonly TimeSpan Teto = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(AiErrorKind.Transient, true)]
    [InlineData(AiErrorKind.InvalidCredentials, false)]
    [InlineData(AiErrorKind.QuotaOrBilling, false)]
    [InlineData(AiErrorKind.InvalidRequest, false)]
    [InlineData(AiErrorKind.ContextTooLong, false)]
    [InlineData(AiErrorKind.Refused, false)]
    [InlineData(AiErrorKind.TenantLimitExceeded, false)]
    [InlineData(AiErrorKind.NotConfigured, false)]
    public void Cada_categoria_tem_a_politica_definida(AiErrorKind tipo, bool retentavel)
    {
        var falha = AiErrorPolicy.Apply(new AiError(tipo), Teto);

        Assert.Equal(tipo, falha.Kind);
        Assert.Equal(retentavel, falha.Retryable);
        Assert.Equal(retentavel, AiErrorPolicy.IsRetryable(tipo));
    }

    [Fact]
    public void Toda_categoria_do_contrato_e_coberta_por_uma_politica_e_so_a_transitoria_e_retentavel()
    {
        var todas = Enum.GetValues<AiErrorKind>();
        Assert.Equal(8, todas.Length);

        Assert.Equal(new[] { AiErrorKind.Transient }, todas.Where(AiErrorPolicy.IsRetryable).ToArray());
    }

    [Fact]
    public void A_espera_sugerida_so_vale_para_a_falha_transitoria_e_e_limitada_ao_teto()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient, TimeSpan.FromSeconds(10)), Teto).RetryAfter);
        Assert.Equal(Teto, AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient, TimeSpan.FromHours(1)), Teto).RetryAfter);
        Assert.Null(AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient, TimeSpan.FromSeconds(-1)), Teto).RetryAfter);
        Assert.Null(AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient), Teto).RetryAfter);
    }

    [Theory]
    [InlineData(AiErrorKind.QuotaOrBilling)]
    [InlineData(AiErrorKind.InvalidCredentials)]
    [InlineData(AiErrorKind.Refused)]
    [InlineData(AiErrorKind.TenantLimitExceeded)]
    public void Cota_credencial_recusa_e_limite_do_tenant_nunca_viram_retentativa_mesmo_com_espera_sugerida(AiErrorKind tipo)
    {
        var falha = AiErrorPolicy.Apply(new AiError(tipo, TimeSpan.FromSeconds(30), "retry-after-presente"), Teto);

        Assert.False(falha.Retryable);
        Assert.Null(falha.RetryAfter);
    }

    [Fact]
    public void Categoria_desconhecida_falha_para_o_lado_seguro_pedido_invalido_e_nao_retentavel()
    {
        var falha = AiErrorPolicy.Apply(new AiError((AiErrorKind)99, TimeSpan.FromSeconds(5)), Teto);

        Assert.Equal(AiErrorKind.InvalidRequest, falha.Kind);
        Assert.False(falha.Retryable);
        Assert.Equal(AiFailureCodes.ProviderErrorKindUnknown, falha.Code);
    }

    [Fact]
    public void O_codigo_do_provedor_so_passa_se_for_curto_e_sem_caractere_de_controle()
    {
        Assert.Equal("rate_limit_exceeded", AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient, null, "rate_limit_exceeded"), Teto).Code);
        Assert.Null(AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient, null, new string('x', 101)), Teto).Code);
        Assert.Null(AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient, null, "linha1\nlinha2"), Teto).Code);
        Assert.Null(AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient, null, "  "), Teto).Code);
        Assert.Null(AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient), Teto).Code);
        Assert.Equal(new string('x', 100), AiErrorPolicy.Apply(new AiError(AiErrorKind.Transient, null, new string('x', 100)), Teto).Code);
    }
}
