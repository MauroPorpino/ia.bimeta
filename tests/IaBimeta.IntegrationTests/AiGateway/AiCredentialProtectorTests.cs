using System.Security.Cryptography;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.SharedKernel;

namespace IaBimeta.IntegrationTests.AiGateway;

/// <summary>
/// A cifra das credenciais BYOK (ADR 0017, seção E): AES-256-GCM, nonce aleatório, e o tenant, a credencial e o provedor como dado autenticado. NÃO usa banco (por isso não está na
/// coleção do PostgreSQL). Nenhuma chave aqui é real: são bytes aleatórios desta execução, e o segredo é um canário.
/// </summary>
public sealed class AiCredentialProtectorTests
{
    private const string Canario = "sk-CANARIO-SEGREDO-DO-TENANT-9f3a1c";

    private static readonly TenantId TenantA = new(Guid.Parse("a0000000-0000-0000-0000-0000000000a1"));
    private static readonly TenantId TenantB = new(Guid.Parse("b0000000-0000-0000-0000-0000000000b2"));
    private static readonly Guid Credencial = Guid.Parse("c0000000-0000-0000-0000-0000000000c1");

    private static string NovaChave() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(AiSecretOptions.KeySizeBytes));

    private static AesGcmCredentialProtector Protetor(string? ativa, params (string Id, string Chave)[] chaves)
    {
        var opcoes = new AiSecretOptions { ActiveKeyId = ativa };
        foreach (var (id, chave) in chaves)
        {
            opcoes.Keys[id] = chave;
        }

        return new AesGcmCredentialProtector(opcoes);
    }

    private static AiCredentialBinding Vinculo(TenantId? tenant = null, Guid? credencial = null, string provedor = "provider-a") => new(tenant ?? TenantA, credencial ?? Credencial, provedor);

    // Ida e volta

    [Fact]
    public void Cifrar_e_decifrar_devolvem_o_segredo_e_o_texto_cifrado_nao_o_contem()
    {
        var protetor = Protetor("k1", ("k1", NovaChave()));

        var protegida = protetor.Protect(Canario, Vinculo());

        Assert.Equal("k1", protegida.KeyId);
        Assert.StartsWith("v1:", protegida.Ciphertext);
        Assert.DoesNotContain(Canario, protegida.Ciphertext);
        Assert.DoesNotContain("CANARIO", protegida.Ciphertext);
        Assert.Equal(Canario, protetor.Unprotect(protegida, Vinculo()));
    }

    [Fact]
    public void O_nonce_e_aleatorio_o_mesmo_segredo_cifrado_duas_vezes_gera_textos_diferentes_e_os_dois_abrem()
    {
        var protetor = Protetor("k1", ("k1", NovaChave()));

        var a = protetor.Protect(Canario, Vinculo());
        var b = protetor.Protect(Canario, Vinculo());

        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
        Assert.Equal(Canario, protetor.Unprotect(a, Vinculo()));
        Assert.Equal(Canario, protetor.Unprotect(b, Vinculo()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("acentuação — çãé 😀")]
    public void Segredos_de_formatos_diferentes_voltam_exatamente_iguais(string segredo)
    {
        var protetor = Protetor("k1", ("k1", NovaChave()));

        Assert.Equal(segredo, protetor.Unprotect(protetor.Protect(segredo, Vinculo()), Vinculo()));
    }

    // O dado autenticado: tenant, credencial e provedor

    [Fact]
    public void O_texto_cifrado_nao_abre_sob_outro_tenant_outra_credencial_ou_outro_provedor()
    {
        var protetor = Protetor("k1", ("k1", NovaChave()));
        var protegida = protetor.Protect(Canario, Vinculo());

        Assert.Throws<AuthenticationTagMismatchException>(() => protetor.Unprotect(protegida, Vinculo(tenant: TenantB)));
        Assert.Throws<AuthenticationTagMismatchException>(() => protetor.Unprotect(protegida, Vinculo(credencial: Guid.NewGuid())));
        Assert.Throws<AuthenticationTagMismatchException>(() => protetor.Unprotect(protegida, Vinculo(provedor: "provider-b")));
        Assert.Equal(Canario, protetor.Unprotect(protegida, Vinculo())); // e continua abrindo sob os dados certos
    }

    [Fact]
    public void Qualquer_alteracao_do_texto_cifrado_e_detectada()
    {
        var protetor = Protetor("k1", ("k1", NovaChave()));
        var protegida = protetor.Protect(Canario, Vinculo());
        var bytes = Convert.FromBase64String(protegida.Ciphertext["v1:".Length..]);

        // Altera um bit em cada região: nonce, tag e texto.
        foreach (var posicao in new[] { 0, 12, 28 })
        {
            var adulterado = (byte[])bytes.Clone();
            adulterado[posicao] ^= 0x01;
            var texto = new AiProtectedCredential(protegida.KeyId, "v1:" + Convert.ToBase64String(adulterado));

            Assert.Throws<AuthenticationTagMismatchException>(() => protetor.Unprotect(texto, Vinculo()));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem-prefixo")]
    [InlineData("v2:AAAA")]
    [InlineData("v1:não é base64!!")]
    [InlineData("v1:AAAA")] // curto demais para ter nonce e tag
    public void Formato_desconhecido_ou_invalido_falha_com_erro_de_cifra_sem_repetir_o_conteudo(string ciphertext)
    {
        var protetor = Protetor("k1", ("k1", NovaChave()));

        var falha = Assert.ThrowsAny<CryptographicException>(() => protetor.Unprotect(new AiProtectedCredential("k1", ciphertext), Vinculo()));

        Assert.DoesNotContain(ciphertext.Length > 4 ? ciphertext : "§§§", falha.Message);
    }

    // Chaves: ausência, formato e rotação

    [Fact]
    public void Sem_chave_ativa_nada_e_cifrado_falha_fechada_e_a_mensagem_nao_revela_segredo()
    {
        var semAtiva = Protetor(null, ("k1", NovaChave()));
        var ativaDesconhecida = Protetor("nao-existe", ("k1", NovaChave()));

        var a = Assert.Throws<AiSecretsUnavailableException>(() => semAtiva.Protect(Canario, Vinculo()));
        var b = Assert.Throws<AiSecretsUnavailableException>(() => ativaDesconhecida.Protect(Canario, Vinculo()));

        Assert.DoesNotContain(Canario, a.Message);
        Assert.DoesNotContain(Canario, b.Message);
    }

    [Theory]
    [InlineData("não é base64!")]
    [InlineData("AAAA")] // base64 válido, mas curto
    public void Chave_com_formato_invalido_e_recusada_sem_repetir_o_conteudo_da_chave(string chaveInvalida)
    {
        var protetor = Protetor("k1", ("k1", chaveInvalida));

        var falha = Assert.Throws<AiSecretsUnavailableException>(() => protetor.Protect(Canario, Vinculo()));

        Assert.DoesNotContain(chaveInvalida, falha.Message);
        Assert.Contains("32 bytes", falha.Message);
    }

    [Fact]
    public void A_rotacao_acrescenta_uma_chave_nova_a_antiga_continua_abrindo_e_o_que_se_cifra_depois_usa_a_nova()
    {
        var chave1 = NovaChave();
        var chave2 = NovaChave();
        var antes = Protetor("k1", ("k1", chave1)).Protect(Canario, Vinculo());

        var depois = Protetor("k2", ("k1", chave1), ("k2", chave2));
        var recifrada = depois.Protect(Canario, Vinculo());

        Assert.Equal("k1", antes.KeyId);
        Assert.Equal("k2", recifrada.KeyId);
        Assert.Equal(Canario, depois.Unprotect(antes, Vinculo())); // a antiga segue abrindo
        Assert.Equal(Canario, depois.Unprotect(recifrada, Vinculo()));
    }

    [Fact]
    public void Se_a_chave_que_cifrou_saiu_da_configuracao_a_leitura_falha_fechada_como_indisponivel()
    {
        var antes = Protetor("k1", ("k1", NovaChave())).Protect(Canario, Vinculo());
        var semAChaveAntiga = Protetor("k2", ("k2", NovaChave()));

        var falha = Assert.Throws<AiSecretsUnavailableException>(() => semAChaveAntiga.Unprotect(antes, Vinculo()));

        Assert.DoesNotContain(Canario, falha.Message);
    }

    [Fact]
    public void Uma_chave_diferente_com_o_mesmo_identificador_nao_abre_o_texto()
    {
        var antes = Protetor("k1", ("k1", NovaChave())).Protect(Canario, Vinculo());
        var outraChaveMesmoId = Protetor("k1", ("k1", NovaChave()));

        Assert.Throws<AuthenticationTagMismatchException>(() => outraChaveMesmoId.Unprotect(antes, Vinculo()));
    }

    // Redação

    [Fact]
    public void As_opcoes_e_o_texto_cifrado_nao_imprimem_chave_nem_segredo()
    {
        var chave = NovaChave();
        var opcoes = new AiSecretOptions { ActiveKeyId = "k1" };
        opcoes.Keys["k1"] = chave;
        var protegida = new AesGcmCredentialProtector(opcoes).Protect(Canario, Vinculo());

        Assert.DoesNotContain(chave, opcoes.ToString());
        Assert.DoesNotContain(protegida.Ciphertext, protegida.ToString());
        Assert.DoesNotContain(Canario, protegida.ToString());
        Assert.DoesNotContain(Canario, new IaBimeta.AiGateway.Contracts.AiCredential(Canario).ToString());
    }

    [Fact]
    public void A_impressao_digital_identifica_a_chave_sem_revela_la_e_muda_com_o_tenant_a_credencial_e_o_valor()
    {
        var a = StoredAiCredential.ComputeFingerprint(TenantA, Credencial, Canario);

        Assert.Equal(12, a.Length);
        Assert.Matches("^[0-9a-f]{12}$", a);
        Assert.Equal(a, StoredAiCredential.ComputeFingerprint(TenantA, Credencial, Canario)); // estável
        Assert.NotEqual(a, StoredAiCredential.ComputeFingerprint(TenantB, Credencial, Canario)); // o sal: outro tenant
        Assert.NotEqual(a, StoredAiCredential.ComputeFingerprint(TenantA, Guid.NewGuid(), Canario)); // outra credencial
        Assert.NotEqual(a, StoredAiCredential.ComputeFingerprint(TenantA, Credencial, Canario + "x")); // outro valor
        Assert.DoesNotContain(Canario[^4..], a); // NÃO são os últimos caracteres da chave
    }
}
