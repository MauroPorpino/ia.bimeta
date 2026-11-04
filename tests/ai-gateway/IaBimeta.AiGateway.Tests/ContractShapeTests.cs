using System.Reflection;
using System.Text.Json;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.AiGateway.Tests;

/// <summary>
/// A forma do contrato comum do AiGateway (ADR 0017, seção C; BOUNDARIES ARQ-07): só interfaces, records imutáveis e enums, independente de provedor, sem
/// <c>temperature</c>, sem tenant no pedido e sem vazar a credencial nem o prompt pelo <c>ToString</c>. Cada regra negativa afirma antes que o conjunto
/// avaliado não é vazio.
/// </summary>
public class ContractShapeTests
{
    private static readonly Assembly Contratos = typeof(IAiGateway).Assembly;

    private static readonly string[] NomesDeProvedor = { "openai", "anthropic", "claude", "gpt", "gemini", "mistral", "llama" };
    private static readonly string[] ParametrosProibidos = { "temperature", "top_p", "topp", "top_k", "topk", "effort" };

    private static bool EhRecord(Type tipo) => tipo.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;

    private static IEnumerable<string> TodosOsNomes()
    {
        foreach (var tipo in Contratos.GetExportedTypes())
        {
            yield return tipo.Name;
            foreach (var membro in tipo.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                yield return membro.Name;
            }

            foreach (var construtor in tipo.GetConstructors())
            {
                foreach (var parametro in construtor.GetParameters())
                {
                    yield return parametro.Name!;
                }
            }

            foreach (var metodo in tipo.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var parametro in metodo.GetParameters())
                {
                    yield return parametro.Name!;
                }
            }
        }
    }

    [Fact]
    public void Todo_tipo_publico_e_interface_enum_ou_record_selado()
    {
        var tipos = Contratos.GetExportedTypes();
        Assert.NotEmpty(tipos);

        foreach (var tipo in tipos)
        {
            var permitido = tipo.IsInterface || tipo.IsEnum || (tipo.IsClass && tipo.IsSealed && EhRecord(tipo));
            Assert.True(permitido, $"{tipo.FullName} não é interface, enum nem record selado (ARQ-07).");
        }
    }

    [Fact]
    public void Os_records_do_contrato_nao_tem_propriedade_com_setter_publico_mutavel()
    {
        var records = Contratos.GetExportedTypes().Where(EhRecord).ToList();
        Assert.NotEmpty(records);

        foreach (var tipo in records)
        {
            foreach (var propriedade in tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is not null))
            {
                var somenteInit = propriedade.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                Assert.True(somenteInit, $"{tipo.Name}.{propriedade.Name} tem setter mutável.");
            }
        }
    }

    [Fact]
    public void O_contrato_nao_referencia_projeto_algum_do_produto()
    {
        var referencias = Contratos.GetReferencedAssemblies();
        Assert.NotEmpty(referencias);

        Assert.DoesNotContain(referencias, r => r.Name!.StartsWith("IaBimeta.", StringComparison.Ordinal));
    }

    [Fact]
    public void Nenhum_nome_do_contrato_cita_provedor_de_IA()
    {
        var nomes = TodosOsNomes().ToList();
        Assert.True(nomes.Count > 50, "o conjunto avaliado não pode ser vazio");

        foreach (var nome in nomes)
        {
            foreach (var provedor in NomesDeProvedor)
            {
                Assert.False(nome.Contains(provedor, StringComparison.OrdinalIgnoreCase), $"'{nome}' cita o provedor '{provedor}'.");
            }
        }
    }

    [Fact]
    public void O_contrato_nao_tem_temperature_top_p_nem_controle_de_raciocinio_por_nome_de_provedor()
    {
        var nomes = TodosOsNomes().ToList();
        Assert.NotEmpty(nomes);

        foreach (var nome in nomes)
        {
            foreach (var proibido in ParametrosProibidos)
            {
                Assert.False(nome.Contains(proibido, StringComparison.OrdinalIgnoreCase), $"'{nome}' expõe o parâmetro '{proibido}' (ADR 0017, C.2).");
            }
        }
    }

    [Fact]
    public void O_provedor_e_identificado_por_texto_e_nenhum_enum_do_contrato_lista_provedores()
    {
        Assert.Equal(typeof(string), typeof(IAiProvider).GetProperty(nameof(IAiProvider.ProviderId))!.PropertyType);
        Assert.Equal(typeof(string), typeof(AiModelDescriptor).GetProperty(nameof(AiModelDescriptor.ProviderId))!.PropertyType);
        Assert.Equal(typeof(string), typeof(AiGenerationResult).GetProperty(nameof(AiGenerationResult.ProviderId))!.PropertyType);

        var enums = Contratos.GetExportedTypes().Where(t => t.IsEnum).ToList();
        Assert.NotEmpty(enums);
        Assert.DoesNotContain(enums, e => e.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void O_tenant_nunca_e_campo_do_pedido_nem_dos_resultados()
    {
        // O tenant vem do contexto confiável do host, nunca do pedido (TEN-1).
        foreach (var tipo in new[] { typeof(AiGenerationRequest), typeof(AiCompletionRequest), typeof(AiGenerationResult), typeof(AiCompletionResult) })
        {
            var propriedades = tipo.GetProperties().Select(p => p.Name).ToList();
            Assert.NotEmpty(propriedades);
            Assert.DoesNotContain(propriedades, p => p.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void O_pedido_ao_gateway_nao_nomeia_provedor_modelo_nem_credencial()
    {
        var propriedades = typeof(AiGenerationRequest).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(propriedades, p => p is "ProviderId" or "ModelId" or "Credential");
    }

    [Fact]
    public void O_ToString_da_credencial_nao_mostra_o_segredo()
    {
        var credencial = new AiCredential("CANARIO-SEGREDO-123");

        Assert.DoesNotContain("CANARIO-SEGREDO-123", credencial.ToString());
        Assert.DoesNotContain("CANARIO-SEGREDO-123", $"{credencial}");
    }

    [Fact]
    public void O_ToString_dos_pedidos_nao_mostra_o_prompt_nem_a_credencial()
    {
        var mensagens = new[] { new AiMessage(AiMessageRole.User, "CANARIO-MENSAGEM-DO-CLIENTE") };
        var geracao = new AiGenerationRequest("turno-1", "CANARIO-INSTRUCAO-DE-SISTEMA", mensagens, 100, TimeSpan.FromSeconds(5));
        var completamento = new AiCompletionRequest(
            Guid.NewGuid(), "turno-1", "model-a1", "profile-a", "CANARIO-INSTRUCAO-DE-SISTEMA", mensagens, 100, TimeSpan.FromSeconds(5),
            AiOutputMode.Text, null, new AiCredential("CANARIO-SEGREDO-123"));

        foreach (var texto in new[] { geracao.ToString(), completamento.ToString() })
        {
            Assert.DoesNotContain("CANARIO", texto);
        }
    }

    [Fact]
    public void O_resultado_do_adaptador_e_ou_resposta_ou_falha_pelas_fabricas()
    {
        var sucesso = AiCompletionResult.Success(new AiCompletion("oi", AiStopReason.Completed), new AiUsage(10, 0, 0, 5), TimeSpan.FromMilliseconds(120), "req-1");
        var falha = AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, TimeSpan.FromSeconds(2), "rate_limit"), TimeSpan.FromMilliseconds(40));

        Assert.True(sucesso.IsSuccess);
        Assert.Null(sucesso.Error);
        Assert.False(falha.IsSuccess);
        Assert.Null(falha.Completion);
        Assert.Equal(AiUsage.None, falha.Usage);
        Assert.False(new AiCompletionResult(null, null, AiUsage.None, TimeSpan.Zero).IsSuccess);
        Assert.False(new AiCompletionResult(new AiCompletion("x", AiStopReason.Completed), new AiError(AiErrorKind.Transient), AiUsage.None, TimeSpan.Zero).IsSuccess);
    }

    [Fact]
    public void A_lista_de_erros_normalizados_e_a_do_ADR_0017_mais_NotConfigured_que_so_o_AiGateway_produz()
    {
        var valores = Enum.GetNames<AiErrorKind>().ToHashSet();

        Assert.Equal(
            new HashSet<string> { "Transient", "InvalidCredentials", "QuotaOrBilling", "InvalidRequest", "ContextTooLong", "Refused", "TenantLimitExceeded", "NotConfigured" },
            valores);
        Assert.Equal(new HashSet<string> { "Completed", "Truncated", "Refused" }, Enum.GetNames<AiStopReason>().ToHashSet());
        Assert.Equal(new HashSet<string> { "Byok", "PlatformManaged" }, Enum.GetNames<AiCredentialSource>().ToHashSet());
    }

    [Fact]
    public void Os_resultados_do_gateway_serializam_sem_credencial_prompt_nem_resposta_alem_do_campo_da_resposta()
    {
        // O resultado carrega só a resposta (Completion) pedida pelo consumidor; nenhum campo guarda o prompt nem a credencial.
        var resultado = new AiGenerationResult(
            Guid.NewGuid(), "provider-a", "model-a1", new AiCompletion("resposta", AiStopReason.Completed), null, new AiUsage(10, 0, 0, 5),
            new AiCostEstimate(0.1m, "USD"), TimeSpan.FromMilliseconds(10), "req-1");

        var json = JsonSerializer.Serialize(resultado);

        Assert.DoesNotContain("Credential", json);
        Assert.DoesNotContain("SystemInstruction", json);
        Assert.DoesNotContain("Messages", json);
    }
}
