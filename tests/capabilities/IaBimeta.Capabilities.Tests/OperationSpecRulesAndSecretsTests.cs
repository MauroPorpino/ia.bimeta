using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Infrastructure;
using IaBimeta.Capabilities.Tests.Testing;

namespace IaBimeta.Capabilities.Tests;

/// <summary>As regras do molde de uma operação: o que a administração NÃO consegue cadastrar.</summary>
public class OperationSpecRulesTests
{
    private static List<string> Check(CapabilityOperationSpec spec, CapabilityEffect effect = CapabilityEffect.Read, bool confirm = true, string[]? headers = null) =>
        OperationSpecRules.Validate(spec, effect, confirm, headers ?? []);

    [Fact]
    public void Leitura_e_escrita_tipicas_sao_validas()
    {
        Assert.Empty(Check(Specs.Read()));
        Assert.Empty(Check(Specs.Write(), CapabilityEffect.Write));
    }

    [Theory]
    [InlineData("TRACE")]
    [InlineData("get")]
    [InlineData("CONNECT")]
    [InlineData("")]
    public void Metodo_fora_da_lista_e_recusado(string metodo) => Assert.NotEmpty(Check(Specs.Read() with { Method = metodo }));

    [Fact]
    public void Efeito_precisa_combinar_com_o_metodo()
    {
        Assert.NotEmpty(Check(Specs.Read(), CapabilityEffect.Write));
        Assert.NotEmpty(Check(Specs.Read() with { Method = "DELETE" }, CapabilityEffect.Read));
        Assert.NotEmpty(Check(Specs.Read() with { Method = "PUT" }, CapabilityEffect.Read));
    }

    [Theory]
    [InlineData("/pedidos/{numero}/../x")]
    [InlineData("pedidos/{numero}")]
    [InlineData("//evil.com/{numero}")]
    [InlineData("/pedidos/{numero}?x=1")]
    [InlineData("/pedidos/{numero}#f")]
    [InlineData("/pedidos\\{numero}")]
    [InlineData("/pedidos/{outro}")]
    [InlineData("/pedidos/{numero}/{numero}")]
    [InlineData("/pedidos/")]
    [InlineData("/pedidos/{numero")]
    public void Caminhos_perigosos_ou_inconsistentes_com_os_parametros_sao_recusados(string caminho)
    {
        var spec = Specs.Read() with { PathTemplate = caminho };
        if (caminho == "/pedidos/")
        {
            // Sem marcador, o parâmetro de caminho "numero" fica sem uso.
            Assert.NotEmpty(Check(spec));
            return;
        }

        Assert.NotEmpty(Check(spec));
    }

    [Fact]
    public void Cabecalho_so_se_for_permitido_pela_conexao_e_nunca_os_reservados()
    {
        CapabilityOperationSpec ComHeader(string nome) => Specs.Read() with { Parameters = [.. Specs.Read().Parameters, Specs.Param("h", where: CapabilityParameterLocation.Header, wire: nome)] };

        Assert.Empty(Check(ComHeader("X-Filial"), headers: ["X-Filial"]));
        Assert.NotEmpty(Check(ComHeader("X-Filial"), headers: []));

        foreach (var reservado in new[] { "Authorization", "Host", "Cookie", "Content-Length", "Transfer-Encoding", "X-Forwarded-For", "Proxy-Authorization", "Sec-Fetch-Site", "Content-Type" })
        {
            Assert.NotEmpty(Check(ComHeader(reservado), headers: [reservado]));
        }
    }

    [Fact]
    public void GET_e_DELETE_nao_tem_corpo()
    {
        var comCorpo = Specs.Read() with { Parameters = [.. Specs.Read().Parameters, Specs.Param("x", where: CapabilityParameterLocation.Body)] };

        Assert.NotEmpty(Check(comCorpo));
        Assert.NotEmpty(Check(comCorpo with { Method = "DELETE", Idempotency = IdempotencySupport.Proven }, CapabilityEffect.Write));
    }

    [Fact]
    public void Escrita_precisa_declarar_a_idempotencia()
    {
        Assert.Contains(Check(Specs.Write(IdempotencySupport.Unspecified), CapabilityEffect.Write), p => p.Contains("idempotency", StringComparison.Ordinal));
        Assert.Empty(Check(Specs.Write(IdempotencySupport.None), CapabilityEffect.Write));
        Assert.Empty(Check(Specs.Write(IdempotencySupport.Reconcilable), CapabilityEffect.Write));
    }

    [Fact]
    public void Proven_em_POST_exige_o_cabecalho_do_sistema_externo_mas_PUT_e_DELETE_sao_naturais()
    {
        Assert.NotEmpty(Check(Specs.Write() with { IdempotencyHeader = null }, CapabilityEffect.Write));
        Assert.Empty(Check(Specs.Write() with { Method = "PUT", IdempotencyHeader = null }, CapabilityEffect.Write));
        Assert.Empty(Check(Specs.Write() with { Method = "DELETE", IdempotencyHeader = null, Parameters = [Specs.Param("numero", CapabilityParameterType.Integer, CapabilityParameterLocation.Path)] }, CapabilityEffect.Write));
    }

    [Fact]
    public void Retentativa_de_escrita_so_com_idempotencia_comprovada()
    {
        Assert.Empty(Check(Specs.Write(IdempotencySupport.Proven, attempts: 3), CapabilityEffect.Write));
        Assert.NotEmpty(Check(Specs.Write(IdempotencySupport.None, attempts: 2), CapabilityEffect.Write));
        Assert.NotEmpty(Check(Specs.Write(IdempotencySupport.Reconcilable, attempts: 2), CapabilityEffect.Write));
        Assert.Empty(Check(Specs.Read() with { MaxAttempts = 3 }));
        Assert.NotEmpty(Check(Specs.Read() with { MaxAttempts = 4 }));
        Assert.NotEmpty(Check(Specs.Read() with { MaxAttempts = 0 }));
    }

    [Fact]
    public void Dispensar_a_confirmacao_so_para_escrita_comprovada()
    {
        Assert.Empty(Check(Specs.Write(), CapabilityEffect.Write, confirm: false));
        Assert.NotEmpty(Check(Specs.Write(IdempotencySupport.None), CapabilityEffect.Write, confirm: false));
        Assert.NotEmpty(Check(Specs.Write(IdempotencySupport.Reconcilable), CapabilityEffect.Write, confirm: false));
    }

    [Fact]
    public void Nomes_repetidos_ponteiros_e_padroes_invalidos_sao_recusados()
    {
        Assert.NotEmpty(Check(Specs.Read() with { Parameters = [Specs.Param("a"), Specs.Param("a")], PathTemplate = "/x" }));
        Assert.NotEmpty(Check(Specs.Read() with { Response = [Specs.Field("a", "/x"), Specs.Field("a", "/y")] }));
        Assert.NotEmpty(Check(Specs.Read() with { Response = [Specs.Field("a", "sem-barra")] }));
        Assert.NotEmpty(Check(Specs.Read() with { Response = [Specs.Field("a", "/x~2")] }));
        Assert.NotEmpty(Check(Specs.Read() with { Parameters = [Specs.Param("1invalido")], PathTemplate = "/x" }));
        Assert.NotEmpty(Check(Specs.Read() with { Parameters = [Specs.Param("s", pattern: "([")], PathTemplate = "/x" }));
        Assert.NotEmpty(Check(Specs.Read() with { Parameters = [Specs.Param("s", CapabilityParameterType.Enum)], PathTemplate = "/x" }));
        Assert.NotEmpty(Check(Specs.Read() with { Parameters = [Specs.Param("n", CapabilityParameterType.Integer, min: 5, max: 1)], PathTemplate = "/x" }));
        Assert.NotEmpty(Check(Specs.Read() with { Parameters = [.. Enumerable.Range(0, 21).Select(i => Specs.Param($"p{i}"))], PathTemplate = "/x" }));
    }

    [Fact]
    public void Padrao_valor_padrao_precisa_respeitar_as_regras_do_proprio_parametro()
    {
        Assert.NotEmpty(Check(Specs.Read() with { Parameters = [Specs.Param("n", CapabilityParameterType.Integer, max: 5, def: "9")], PathTemplate = "/x" }));
        Assert.Empty(Check(Specs.Read() with { Parameters = [Specs.Param("n", CapabilityParameterType.Integer, max: 5, def: "3")], PathTemplate = "/x" }));
    }

    [Fact]
    public void O_molde_ida_e_volta_em_json_nao_perde_nada()
    {
        var spec = Specs.Write();
        var volta = CapabilitySpecJson.Deserialize(CapabilitySpecJson.Serialize(spec));

        Assert.Equal(spec.Method, volta.Method);
        Assert.Equal(spec.PathTemplate, volta.PathTemplate);
        Assert.Equal(spec.Idempotency, volta.Idempotency);
        Assert.Equal(spec.IdempotencyHeader, volta.IdempotencyHeader);
        Assert.Equal(spec.Parameters.Count, volta.Parameters.Count);
        Assert.Equal(spec.Response.Count, volta.Response.Count);
    }
}

/// <summary>A cifra dos segredos: isolamento por tenant, recurso, finalidade e versão; nada vaza em texto.</summary>
public class SecretProtectorTests
{
    private static readonly Application.SecretBinding Binding = new("connection-secret", TestContexts.TenantA, Guid.Parse("11111111-1111-1111-1111-111111111111"), "1:1");

    [Fact]
    public void Cifra_e_decifra_e_o_texto_cifrado_nao_contem_o_segredo()
    {
        var p = TestSecrets.Protector();
        var cifrado = p.Protect("minha-chave-super-secreta", Binding);

        Assert.DoesNotContain("minha-chave", cifrado.Ciphertext, StringComparison.Ordinal);
        Assert.Equal("k1", cifrado.KeyId);
        Assert.Equal("minha-chave-super-secreta", p.Unprotect(cifrado, Binding));
        Assert.NotEqual(cifrado.Ciphertext, p.Protect("minha-chave-super-secreta", Binding).Ciphertext);
    }

    [Fact]
    public void Nao_abre_sob_outro_tenant_recurso_finalidade_ou_versao()
    {
        var p = TestSecrets.Protector();
        var cifrado = p.Protect("segredo", Binding);

        foreach (var outro in new[]
        {
            Binding with { TenantId = TestContexts.TenantB },
            Binding with { ResourceId = Guid.NewGuid() },
            Binding with { Purpose = "pending-params" },
            Binding with { Discriminator = "1:2" },
        })
        {
            var ex = Assert.Throws<CapabilitiesUnavailableException>(() => p.Unprotect(cifrado, outro));
            Assert.DoesNotContain("segredo", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Texto_alterado_ou_formato_desconhecido_falham_fechado()
    {
        var p = TestSecrets.Protector();
        var cifrado = p.Protect("segredo", Binding);
        var bytes = Convert.FromBase64String(cifrado.Ciphertext["v1:".Length..]);
        bytes[^1] ^= 0x01;

        Assert.Throws<CapabilitiesUnavailableException>(() => p.Unprotect(cifrado with { Ciphertext = "v1:" + Convert.ToBase64String(bytes) }, Binding));
        Assert.Throws<CapabilitiesUnavailableException>(() => p.Unprotect(cifrado with { Ciphertext = "v9:abc" }, Binding));
        Assert.Throws<CapabilitiesUnavailableException>(() => p.Unprotect(cifrado with { Ciphertext = "v1:@@@" }, Binding));
        Assert.Throws<CapabilitiesUnavailableException>(() => p.Unprotect(cifrado with { KeyId = "nao-existe" }, Binding));
    }

    [Fact]
    public void Rotacao_de_chave_a_antiga_ainda_decifra_e_a_nova_cifra()
    {
        var antiga = new AesGcmSecretProtector(TestSecrets.Options("k1"));
        var cifrado = antiga.Protect("segredo", Binding);
        var opcoesNovas = TestSecrets.Options("k1");
        opcoesNovas.ActiveKeyId = "k0";
        var nova = new AesGcmSecretProtector(opcoesNovas);

        Assert.Equal("segredo", nova.Unprotect(cifrado, Binding));
        Assert.Equal("k0", nova.Protect("x", Binding).KeyId);
    }

    [Fact]
    public void Sem_chave_ativa_ou_com_chave_invalida_nada_e_cifrado()
    {
        var semChave = new AesGcmSecretProtector(new CapabilitiesSecretOptions());
        Assert.False(semChave.IsConfigured);
        Assert.Throws<CapabilitiesUnavailableException>(() => semChave.Protect("x", Binding));

        var invalida = new AesGcmSecretProtector(new CapabilitiesSecretOptions { ActiveKeyId = "k", Keys = { ["k"] = Convert.ToBase64String(new byte[10]) } });
        Assert.False(invalida.IsConfigured);
        Assert.Throws<CapabilitiesUnavailableException>(() => invalida.Protect("x", Binding));
    }

    [Fact]
    public void As_opcoes_e_o_segredo_nunca_se_imprimem()
    {
        var opcoes = TestSecrets.Options();
        Assert.DoesNotContain(opcoes.Keys["k1"], opcoes.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("valor-secreto", new ConnectorSecret(new Dictionary<string, string> { ["apiKey"] = "valor-secreto" }).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_impressao_digital_e_estavel_distingue_parametros_e_nao_os_revela()
    {
        var p = TestSecrets.Protector();
        var a = p.Fingerprint("op|1|{\"cpf\":\"12345678901\"}");

        Assert.Equal(a, p.Fingerprint("op|1|{\"cpf\":\"12345678901\"}"));
        Assert.NotEqual(a, p.Fingerprint("op|1|{\"cpf\":\"12345678902\"}"));
        Assert.Equal(64, a.Length);
        Assert.DoesNotContain("12345678901", a, StringComparison.Ordinal);
        Assert.NotEqual(a, new AesGcmSecretProtector(new CapabilitiesSecretOptions()).Fingerprint("op|1|{\"cpf\":\"12345678901\"}"));
    }
}
