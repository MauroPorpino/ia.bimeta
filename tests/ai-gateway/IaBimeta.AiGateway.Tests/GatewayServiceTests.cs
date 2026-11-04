using System.Text.Json;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Tests;

/// <summary>
/// O AiGateway de ponta a ponta com dublês (ADR 0017, critérios 1, 2, 7 e 9): a requisição que o provedor recebe, o resultado normalizado, o custo, o registro de
/// consumo sem prompt e sem resposta, as falhas normalizadas e a vaga devolvida em todo desfecho. Nenhuma rede, nenhuma chave real, nenhum banco.
/// </summary>
public class GatewayServiceTests
{
    private static readonly TenantId A = GatewayHarness.TenantA;

    [Fact]
    public async Task A_chamada_vai_so_ao_adaptador_do_tenant_com_o_modelo_o_perfil_a_credencial_e_os_tetos_resolvidos()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 300, correlationId: "turno-77"));

        Assert.True(resultado.IsSuccess);
        Assert.Equal(1, h.ProviderA.CallCount);
        Assert.Equal(0, h.ProviderB.CallCount);

        var pedido = Assert.Single(h.ProviderA.Requests);
        Assert.Equal("model-a1", pedido.ModelId);
        Assert.Equal("profile-a", pedido.Profile);
        Assert.Equal(GatewayHarness.CredentialOfTenantA, pedido.Credential.Secret);
        Assert.Equal("turno-77", pedido.CorrelationId);
        Assert.Equal("Você é a atendente virtual.", pedido.SystemInstruction);
        Assert.Equal(AiMessageRole.User, Assert.Single(pedido.Messages).Role);
        Assert.Equal(300, pedido.MaxOutputTokens);
        Assert.Equal(AiOutputMode.Text, pedido.OutputMode);
        Assert.Null(pedido.StructuredOutput);
        Assert.Equal(resultado.AiRequestId, pedido.AiRequestId);
        Assert.NotEqual(Guid.Empty, resultado.AiRequestId);
    }

    [Fact]
    public async Task O_resultado_traz_a_resposta_o_consumo_o_custo_estimado_a_latencia_e_o_identificador_do_provedor()
    {
        var h = new GatewayHarness();
        h.ProviderA.Latency = TimeSpan.FromMilliseconds(30);
        h.ProviderA.Returns(h.ProviderA.Succeed("Atendemos das 8h às 18h.", new AiUsage(100, 0, 0, 20), providerRequestId: "req-abc"));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.True(resultado.IsSuccess);
        Assert.Null(resultado.Failure);
        Assert.Equal("Atendemos das 8h às 18h.", resultado.Completion!.Text);
        Assert.Equal(AiStopReason.Completed, resultado.Completion.StopReason);
        Assert.Equal("provider-a", resultado.ProviderId);
        Assert.Equal("model-a1", resultado.ModelId);
        Assert.Equal(new AiUsage(100, 0, 0, 20), resultado.Usage);
        Assert.Equal(new AiCostEstimate(0.00002m, "USD"), resultado.EstimatedCost);
        Assert.Equal(TimeSpan.FromMilliseconds(30), resultado.Latency);
        Assert.Equal("req-abc", resultado.ProviderRequestId);
    }

    [Fact]
    public async Task O_teto_de_saida_e_o_menor_entre_o_pedido_o_do_tenant_e_o_do_modelo()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 10_000)); // tenant: 500, modelo: 4.000
        h.Configurations.Set(new AiTenantConfiguration(A, GatewayHarness.ProviderAId, "model-a1", AiCredentialSource.Byok, GatewayHarness.GenerousLimits with { MaxOutputTokens = 9_000 }));
        await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 10_000)); // tenant: 9.000, modelo: 4.000
        await h.Gateway.GenerateAsync(GatewayHarness.Request(maxOutputTokens: 120));

        Assert.Equal(new[] { 500, 4_000, 120 }, h.ProviderA.Requests.Select(r => r.MaxOutputTokens).ToArray());
    }

    [Fact]
    public async Task O_tempo_limite_pedido_e_reduzido_ao_maximo_configurado()
    {
        var h = new GatewayHarness(new AiGatewayOptions { MaxCallTimeout = TimeSpan.FromSeconds(20) });
        h.AsSystem(A);

        await h.Gateway.GenerateAsync(GatewayHarness.Request(timeout: TimeSpan.FromMinutes(10)));
        await h.Gateway.GenerateAsync(GatewayHarness.Request(timeout: TimeSpan.FromSeconds(3)));

        Assert.Equal(new[] { TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3) }, h.ProviderA.Requests.Select(r => r.Timeout).ToArray());
    }

    [Fact]
    public async Task A_saida_estruturada_chega_ao_adaptador_com_o_esquema()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);
        var esquema = new AiStructuredOutput("decisao", """{ "type": "object", "properties": { "action": { "type": "string" } }, "required": ["action"], "additionalProperties": false }""");

        await h.Gateway.GenerateAsync(GatewayHarness.Request() with { OutputMode = AiOutputMode.StructuredJson, StructuredOutput = esquema });

        var pedido = Assert.Single(h.ProviderA.Requests);
        Assert.Equal(AiOutputMode.StructuredJson, pedido.OutputMode);
        Assert.Equal(esquema, pedido.StructuredOutput);
    }

    [Fact]
    public async Task O_historico_do_pedido_e_copiado_e_alterar_a_lista_depois_nao_muda_o_que_o_provedor_recebeu()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);
        var mensagens = new List<AiMessage> { new(AiMessageRole.User, "primeira") };
        var pedido = GatewayHarness.Request() with { Messages = mensagens };

        await h.Gateway.GenerateAsync(pedido);
        mensagens.Add(new AiMessage(AiMessageRole.User, "adicionada depois"));

        Assert.Single(Assert.Single(h.ProviderA.Requests).Messages);
    }

    [Theory]
    [InlineData(AiErrorKind.Transient, true)]
    [InlineData(AiErrorKind.InvalidCredentials, false)]
    [InlineData(AiErrorKind.QuotaOrBilling, false)]
    [InlineData(AiErrorKind.InvalidRequest, false)]
    [InlineData(AiErrorKind.ContextTooLong, false)]
    [InlineData(AiErrorKind.Refused, false)]
    public async Task Cada_erro_normalizado_do_adaptador_volta_como_falha_com_a_politica_aplicada_e_a_vaga_devolvida(AiErrorKind tipo, bool retentavel)
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Fail(tipo, TimeSpan.FromSeconds(7), "codigo-do-provedor"));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.False(resultado.IsSuccess);
        Assert.Null(resultado.Completion);
        Assert.Equal(tipo, resultado.Failure!.Kind);
        Assert.Equal(retentavel, resultado.Failure.Retryable);
        TimeSpan? esperaEsperada = retentavel ? TimeSpan.FromSeconds(7) : null;
        Assert.Equal(esperaEsperada, resultado.Failure.RetryAfter);
        Assert.Equal("codigo-do-provedor", resultado.Failure.Code);
        Assert.Equal(0, h.Ledger.InFlight(A));

        var registro = Assert.Single(h.Ledger.Records);
        Assert.Equal(AiRequestOutcome.Failed, registro.Outcome);
        Assert.Equal(tipo, registro.ErrorKind);
        Assert.Null(registro.StopReason);
    }

    [Fact]
    public async Task Nao_ha_fallback_a_falha_do_provedor_do_tenant_nunca_chama_outro_provedor()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Fail(AiErrorKind.Transient));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.Transient, resultado.Failure!.Kind);
        Assert.Equal(1, h.ProviderA.CallCount); // nem retentativa interna: quem tenta de novo é o consumidor
        Assert.Equal(0, h.ProviderB.CallCount);
    }

    [Fact]
    public async Task A_espera_sugerida_pelo_provedor_e_limitada_ao_teto_configurado()
    {
        var h = new GatewayHarness(new AiGatewayOptions { MaxRetryAfter = TimeSpan.FromSeconds(30) });
        h.ProviderA.Returns(h.ProviderA.Fail(AiErrorKind.Transient, TimeSpan.FromHours(2)));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(TimeSpan.FromSeconds(30), resultado.Failure!.RetryAfter);
    }

    [Fact]
    public async Task Uma_recusa_do_modelo_e_resposta_com_o_consumo_cobrado_e_nao_e_convertida_em_falha()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Succeed("", new AiUsage(1_000, 0, 0, 40), AiStopReason.Refused));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.True(resultado.IsSuccess);
        Assert.Equal(AiStopReason.Refused, resultado.Completion!.StopReason);
        Assert.True(resultado.EstimatedCost!.Amount > 0m); // os tokens da recusa foram cobrados
        Assert.Equal(AiStopReason.Refused, Assert.Single(h.Ledger.Records).StopReason);
    }

    [Fact]
    public async Task Uma_resposta_truncada_e_devolvida_como_truncada_para_o_consumidor_decidir()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Succeed("{\"action\": \"re", new AiUsage(100, 0, 0, 300), AiStopReason.Truncated));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiStopReason.Truncated, resultado.Completion!.StopReason);
    }

    [Fact]
    public async Task Falha_com_consumo_registra_o_consumo_e_o_custo_da_chamada_que_falhou()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Fail(AiErrorKind.Refused, usage: new AiUsage(2_000, 0, 0, 0)));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(new AiUsage(2_000, 0, 0, 0), resultado.Usage);
        Assert.Equal(0.0002m, resultado.EstimatedCost!.Amount);
        Assert.Equal(new AiUsage(2_000, 0, 0, 0), Assert.Single(h.Ledger.Records).Usage);
    }

    [Fact]
    public async Task O_consumo_informado_de_forma_incoerente_e_normalizado_antes_do_custo_e_do_registro()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Succeed("ok", new AiUsage(100, 900, 900, 20)));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(new AiUsage(100, 100, 0, 20), resultado.Usage);
        Assert.Equal(new AiUsage(100, 100, 0, 20), Assert.Single(h.Ledger.Records).Usage);
        Assert.True(resultado.EstimatedCost!.Amount >= 0m);
    }

    [Fact]
    public async Task Um_adaptador_que_lanca_excecao_vira_falha_transitoria_sem_vazar_a_mensagem_da_excecao()
    {
        var h = new GatewayHarness();
        h.ProviderA.Throws(new InvalidOperationException("CANARIO-NA-EXCECAO prompt do cliente e chave"));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.Transient, resultado.Failure!.Kind);
        Assert.True(resultado.Failure.Retryable);
        Assert.Equal(AiFailureCodes.ProviderException, resultado.Failure.Code);
        Assert.DoesNotContain("CANARIO-NA-EXCECAO", JsonSerializer.Serialize(resultado));
        Assert.DoesNotContain("CANARIO-NA-EXCECAO", JsonSerializer.Serialize(h.Ledger.Records));
        Assert.Equal(0, h.Ledger.InFlight(A));
    }

    [Fact]
    public async Task O_tempo_limite_esgotado_e_falha_transitoria_e_a_vaga_volta()
    {
        var h = new GatewayHarness();
        h.ProviderA.Hangs();
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request(timeout: TimeSpan.FromMilliseconds(150)));

        Assert.Equal(AiErrorKind.Transient, resultado.Failure!.Kind);
        Assert.True(resultado.Failure.Retryable);
        Assert.Equal(AiFailureCodes.Timeout, resultado.Failure.Code);
        Assert.Equal(0, h.Ledger.InFlight(A));
        Assert.Equal(AiRequestOutcome.Failed, Assert.Single(h.Ledger.Records).Outcome);
    }

    [Fact]
    public async Task O_chamador_que_desiste_recebe_o_cancelamento_a_vaga_volta_e_o_registro_marca_cancelado()
    {
        var h = new GatewayHarness();
        h.ProviderA.Hangs();
        h.AsSystem(A);
        using var cancelamento = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Gateway.GenerateAsync(GatewayHarness.Request(timeout: TimeSpan.FromSeconds(30)), cancelamento.Token));

        Assert.Equal(0, h.Ledger.InFlight(A));
        var registro = Assert.Single(h.Ledger.Records);
        Assert.Equal(AiRequestOutcome.Cancelled, registro.Outcome);
        Assert.Equal(AiUsage.None, registro.Usage);
    }

    [Fact]
    public async Task Resultado_do_adaptador_sem_resposta_e_sem_erro_ou_com_as_duas_coisas_nunca_vira_sucesso()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        h.ProviderA.Returns(new AiCompletionResult(null, null, AiUsage.None, TimeSpan.Zero));
        var nenhum = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        h.ProviderA.Returns(new AiCompletionResult(new AiCompletion("x", AiStopReason.Completed), new AiError(AiErrorKind.Transient), AiUsage.None, TimeSpan.Zero));
        var ambos = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        foreach (var resultado in new[] { nenhum, ambos })
        {
            Assert.False(resultado.IsSuccess);
            Assert.Equal(AiErrorKind.InvalidRequest, resultado.Failure!.Kind);
            Assert.False(resultado.Failure.Retryable);
            Assert.Equal(AiFailureCodes.ProviderResultInvalid, resultado.Failure.Code);
        }

        Assert.Equal(0, h.Ledger.InFlight(A));
    }

    [Fact]
    public async Task O_registro_de_consumo_nao_guarda_prompt_resposta_nem_credencial()
    {
        var h = new GatewayHarness();
        h.ProviderA.Returns(h.ProviderA.Succeed("CANARIO-RESPOSTA-DO-MODELO"));
        h.AsSystem(A);
        var pedido = GatewayHarness.Request(text: "CANARIO-MENSAGEM-DO-CLIENTE", system: "CANARIO-INSTRUCAO-DE-SISTEMA");

        var resultado = await h.Gateway.GenerateAsync(pedido);

        var registro = Assert.Single(h.Ledger.Records);
        var serializado = JsonSerializer.Serialize(registro) + registro;
        Assert.DoesNotContain("CANARIO-MENSAGEM-DO-CLIENTE", serializado);
        Assert.DoesNotContain("CANARIO-INSTRUCAO-DE-SISTEMA", serializado);
        Assert.DoesNotContain("CANARIO-RESPOSTA-DO-MODELO", serializado);
        Assert.DoesNotContain("CANARIO-CHAVE", serializado);

        // O registro tem os campos do ADR 0017, I.1: tenant, chamada, correlação, provedor, modelo, tokens, custo, latência, resultado e identificador no provedor.
        Assert.Equal(A, registro.TenantId);
        Assert.Equal(resultado.AiRequestId, registro.AiRequestId);
        Assert.Equal("turno-1", registro.CorrelationId);
        Assert.Equal("provider-a", registro.ProviderId);
        Assert.Equal("model-a1", registro.ModelId);
        Assert.Equal(AiRequestOutcome.Succeeded, registro.Outcome);
        Assert.Equal(resultado.EstimatedCost, registro.EstimatedCost);
        Assert.Equal(resultado.ProviderRequestId, registro.ProviderRequestId);
        Assert.Equal(h.Clock.GetUtcNow(), registro.OccurredAt);
    }

    [Fact]
    public async Task A_credencial_nunca_aparece_no_resultado_na_falha_nem_no_registro()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        var sucesso = await h.Gateway.GenerateAsync(GatewayHarness.Request());
        h.ProviderA.Returns(h.ProviderA.Fail(AiErrorKind.InvalidCredentials, code: "invalid_api_key"));
        var falha = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        foreach (var texto in new[] { JsonSerializer.Serialize(sucesso), JsonSerializer.Serialize(falha), JsonSerializer.Serialize(h.Ledger.Records), sucesso.ToString(), falha.ToString() })
        {
            Assert.DoesNotContain("CANARIO-CHAVE", texto);
        }
    }

    [Fact]
    public async Task Sem_credencial_a_chamada_falha_fechada_sem_chamar_o_provedor_nem_ocupar_vaga()
    {
        var h = new GatewayHarness();
        var semChave = new TenantId(Guid.NewGuid());
        h.Configurations.Set(new AiTenantConfiguration(semChave, GatewayHarness.ProviderAId, "model-a1", AiCredentialSource.Byok, GatewayHarness.GenerousLimits));
        h.AsSystem(semChave);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.InvalidCredentials, resultado.Failure!.Kind);
        Assert.False(resultado.Failure.Retryable);
        Assert.Equal(AiFailureCodes.CredentialMissing, resultado.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
        Assert.Empty(h.Ledger.Records);
        Assert.Equal(0, h.Ledger.InFlight(semChave));
        Assert.Equal("provider-a", resultado.ProviderId);
    }

    [Fact]
    public async Task Credencial_vazia_tambem_falha_fechada()
    {
        var h = new GatewayHarness();
        h.Credentials.Set(A, GatewayHarness.ProviderAId, "   ");
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiFailureCodes.CredentialMissing, resultado.Failure!.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
    }

    [Fact]
    public async Task Tenant_sem_configuracao_nao_chama_provedor_nem_resolve_credencial()
    {
        var h = new GatewayHarness();
        var semConfiguracao = new TenantId(Guid.NewGuid());
        h.AsSystem(semConfiguracao);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.NotConfigured, resultado.Failure!.Kind);
        Assert.False(resultado.Failure.Retryable);
        Assert.Equal(AiFailureCodes.NotConfigured, resultado.Failure.Code);
        Assert.Null(resultado.ProviderId);
        Assert.Null(resultado.ModelId);
        Assert.Null(resultado.EstimatedCost);
        Assert.Equal(0, h.ProviderA.CallCount + h.ProviderB.CallCount);
        Assert.Empty(h.Credentials.Resolutions);
        Assert.Empty(h.Ledger.Records);
    }

    [Fact]
    public async Task Configuracao_PlatformManaged_e_recusada_sem_resolver_credencial_nem_chamar_provedor()
    {
        var h = new GatewayHarness();
        h.Configurations.Set(new AiTenantConfiguration(A, GatewayHarness.ProviderAId, "model-a1", AiCredentialSource.PlatformManaged, GatewayHarness.GenerousLimits));
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.NotConfigured, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.CredentialSourceNotSupported, resultado.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
        Assert.Empty(h.Credentials.Resolutions);
    }

    [Fact]
    public async Task Modelo_fora_do_catalogo_ou_desativado_e_recusado_na_chamada_sem_fallback()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        h.Configurations.Set(new AiTenantConfiguration(A, GatewayHarness.ProviderAId, "model-fora", AiCredentialSource.Byok, GatewayHarness.GenerousLimits));
        var foraDoCatalogo = await h.Gateway.GenerateAsync(GatewayHarness.Request());
        h.Configurations.Set(new AiTenantConfiguration(A, GatewayHarness.ProviderAId, "model-a2", AiCredentialSource.Byok, GatewayHarness.GenerousLimits));
        var desativado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiFailureCodes.ModelNotSupported, foraDoCatalogo.Failure!.Code);
        Assert.Equal(AiFailureCodes.ModelDisabled, desativado.Failure!.Code);
        Assert.All(new[] { foraDoCatalogo, desativado }, r => Assert.Equal(AiErrorKind.NotConfigured, r.Failure!.Kind));
        Assert.Equal(0, h.ProviderA.CallCount + h.ProviderB.CallCount);
    }

    [Fact]
    public async Task Pedido_invalido_e_excecao_e_nada_e_lido_nem_chamado()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        await Assert.ThrowsAsync<AiGatewayValidationException>(() => h.Gateway.GenerateAsync(GatewayHarness.Request() with { Messages = Array.Empty<AiMessage>() }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Gateway.GenerateAsync(null!));

        Assert.Empty(h.Configurations.Reads);
        Assert.Empty(h.Credentials.Resolutions);
        Assert.Equal(0, h.ProviderA.CallCount);
        Assert.Empty(h.Ledger.Records);
    }

    [Fact]
    public async Task Mais_mensagens_que_o_limite_do_tenant_e_falha_permanente_de_limite_sem_chamar_o_provedor()
    {
        var h = new GatewayHarness();
        h.Configurations.Set(new AiTenantConfiguration(A, GatewayHarness.ProviderAId, "model-a1", AiCredentialSource.Byok, GatewayHarness.GenerousLimits with { MaxInputMessages = 2 }));
        h.AsSystem(A);
        var tres = new[] { new AiMessage(AiMessageRole.User, "1"), new AiMessage(AiMessageRole.Assistant, "2"), new AiMessage(AiMessageRole.User, "3") };

        var excedido = await h.Gateway.GenerateAsync(GatewayHarness.Request() with { Messages = tres });
        var dentro = await h.Gateway.GenerateAsync(GatewayHarness.Request() with { Messages = tres.Skip(1).ToArray() });

        Assert.Equal(AiErrorKind.TenantLimitExceeded, excedido.Failure!.Kind);
        Assert.False(excedido.Failure.Retryable);
        Assert.Equal(AiFailureCodes.InputMessageLimit, excedido.Failure.Code);
        Assert.True(dentro.IsSuccess);
        Assert.Equal(1, h.ProviderA.CallCount);
    }

    [Fact]
    public async Task Cada_chamada_recebe_um_AiRequestId_proprio()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        var primeira = await h.Gateway.GenerateAsync(GatewayHarness.Request());
        var segunda = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.NotEqual(primeira.AiRequestId, segunda.AiRequestId);
        Assert.Equal(new[] { primeira.AiRequestId, segunda.AiRequestId }, h.Ledger.Records.Select(r => r.AiRequestId).ToArray());
    }

    [Fact]
    public void As_opcoes_invalidas_falham_na_montagem()
    {
        Assert.Throws<AiGatewayConfigurationException>(() => new GatewayHarness(new AiGatewayOptions { MaxCallTimeout = TimeSpan.Zero }));
        Assert.Throws<AiGatewayConfigurationException>(() => new GatewayHarness(new AiGatewayOptions { MaxRetryAfter = TimeSpan.FromSeconds(-1) }));
    }
}

/// <summary>Quem pode chamar: só um contexto de SISTEMA (<see cref="Permission.SystemOperation"/>); um usuário, mesmo administrador do tenant, nunca produz uma chamada de IA.</summary>
public class GatewayAuthorizationTests
{
    private static readonly TenantId A = GatewayHarness.TenantA;

    private static void NadaFoiTocado(GatewayHarness h)
    {
        Assert.Empty(h.Configurations.Reads);
        Assert.Empty(h.Credentials.Resolutions);
        Assert.Equal(0, h.ProviderA.CallCount + h.ProviderB.CallCount);
        Assert.Empty(h.Ledger.Records);
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_a_chamada_falha_fechada()
    {
        var h = new GatewayHarness();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => h.Gateway.GenerateAsync(GatewayHarness.Request()));

        NadaFoiTocado(h);
    }

    [Fact]
    public async Task Um_usuario_comum_e_negado()
    {
        var h = new GatewayHarness();
        h.AsUser(A, new PermissionGrant(Permission.ConversationManage, null));

        var negado = await Assert.ThrowsAsync<AccessDeniedException>(() => h.Gateway.GenerateAsync(GatewayHarness.Request()));

        Assert.Equal(Permission.SystemOperation, negado.Permission);
        NadaFoiTocado(h);
    }

    [Fact]
    public async Task Nem_o_administrador_do_tenant_com_TenantSettingsManage_chama_o_modelo_diretamente()
    {
        var h = new GatewayHarness();
        h.AsUser(A, new PermissionGrant(Permission.TenantSettingsManage, null), new PermissionGrant(Permission.ConversationManage, null));

        await Assert.ThrowsAsync<AccessDeniedException>(() => h.Gateway.GenerateAsync(GatewayHarness.Request()));

        NadaFoiTocado(h);
    }

    [Fact]
    public async Task O_contexto_de_sistema_chama()
    {
        var h = new GatewayHarness();
        h.AsSystem(A);

        var resultado = await h.Gateway.GenerateAsync(GatewayHarness.Request());

        Assert.True(resultado.IsSuccess);
    }
}
