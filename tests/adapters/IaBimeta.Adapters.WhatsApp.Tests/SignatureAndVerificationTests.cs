using System.Security.Cryptography;
using System.Text;
using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// A AUTENTICIDADE do webhook (ADR 0011, A.2, item 3): <c>X-Hub-Signature-256</c> sobre os BYTES brutos, em tempo constante, e o GET de verificação com o desafio. Inclui o payload ACENTUADO que a página oficial
/// não detalha. Nenhuma rede, nenhum segredo real.
/// </summary>
public sealed class SignatureAndVerificationTests
{
    private static readonly byte[] AsciiBody = Encoding.UTF8.GetBytes("{\"object\":\"whatsapp_business_account\",\"entry\":[]}");

    // Texto de cliente real: acentos, cedilha, emoji (fora do plano básico) e escapes.
    private static readonly byte[] AccentedBody = Encoding.UTF8.GetBytes("{\"object\":\"whatsapp_business_account\",\"entry\":[{\"text\":\"Olá, preciso do exame de coração — ação rápida? 😀 \\u00e7\"}]}");

    [Fact]
    public void O_adaptador_declara_o_provedor_e_a_estrategia_de_efeito_nenhuma()
    {
        var adaptador = Sends.Plain();

        Assert.Equal(ChannelProvider.WhatsAppCloud, adaptador.Provider);

        // Sem chave de idempotência nem consulta documentadas (ADR 0011, A.2, item 11): a estratégia (c).
        Assert.Equal(ProviderEffectStrategy.None, adaptador.EffectStrategy);
    }

    [Fact]
    public void Assinatura_correta_sobre_corpo_ascii_confere()
    {
        Assert.True(Sends.Plain().VerifySignature(AsciiBody, Signatures.Header(AsciiBody), Signatures.AppSecret));
    }

    [Fact]
    public void Assinatura_correta_sobre_payload_acentuado_em_utf8_confere()
    {
        Assert.True(Sends.Plain().VerifySignature(AccentedBody, Signatures.Header(AccentedBody), Signatures.AppSecret));
    }

    [Fact]
    public void A_assinatura_e_sobre_os_bytes_brutos_e_nao_sobre_o_texto_reinterpretado()
    {
        // O MAC sobre o texto em Latin-1 (outra codificação dos mesmos caracteres) NÃO vale para os bytes UTF-8 que chegaram.
        var latin1 = Encoding.Latin1.GetBytes(Encoding.UTF8.GetString(AccentedBody));
        Assert.NotEqual(AccentedBody, latin1);

        Assert.False(Sends.Plain().VerifySignature(AccentedBody, Signatures.Header(latin1), Signatures.AppSecret));

        // O corpo com o escape \u00e7 normalizado para o caractere literal é OUTRO corpo: a assinatura do original não vale para ele.
        var normalizado = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(AccentedBody).Replace("\\u00e7", "ç", StringComparison.Ordinal));
        Assert.False(Sends.Plain().VerifySignature(normalizado, Signatures.Header(AccentedBody), Signatures.AppSecret));
    }

    [Fact]
    public void Um_byte_alterado_em_qualquer_posicao_invalida_a_assinatura()
    {
        var assinatura = Signatures.Header(AccentedBody);

        for (var i = 0; i < AccentedBody.Length; i++)
        {
            var alterado = (byte[])AccentedBody.Clone();
            alterado[i] ^= 0x01;
            Assert.False(Sends.Plain().VerifySignature(alterado, assinatura, Signatures.AppSecret), $"A alteração do byte {i} não foi detectada.");
        }

        // Corpo mais curto ou mais longo também.
        Assert.False(Sends.Plain().VerifySignature(AccentedBody.AsSpan(0, AccentedBody.Length - 1), assinatura, Signatures.AppSecret));
        Assert.False(Sends.Plain().VerifySignature([.. AccentedBody, (byte)' '], assinatura, Signatures.AppSecret));
    }

    [Fact]
    public void Um_caractere_alterado_em_qualquer_posicao_do_cabecalho_invalida_a_assinatura_inclusive_o_ultimo()
    {
        var assinatura = Signatures.Header(AsciiBody);
        var inicio = "sha256=".Length;

        for (var i = inicio; i < assinatura.Length; i++)
        {
            var caracteres = assinatura.ToCharArray();
            caracteres[i] = caracteres[i] == '0' ? '1' : '0';
            Assert.False(Sends.Plain().VerifySignature(AsciiBody, new string(caracteres), Signatures.AppSecret), $"A alteração do caractere {i} do cabeçalho não foi detectada.");
        }
    }

    [Fact]
    public void Segredo_diferente_ou_vazio_nao_confere()
    {
        var assinatura = Signatures.Header(AsciiBody);

        Assert.False(Sends.Plain().VerifySignature(AsciiBody, assinatura, Signatures.AppSecret + "x"));
        Assert.False(Sends.Plain().VerifySignature(AsciiBody, assinatura, "outro-segredo"));
        Assert.False(Sends.Plain().VerifySignature(AsciiBody, assinatura, string.Empty));

        // Nunca confere com chave vazia, mesmo que o MAC com chave vazia tenha sido o enviado.
        var comChaveVazia = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Array.Empty<byte>(), AsciiBody)).ToLowerInvariant();
        Assert.False(Sends.Plain().VerifySignature(AsciiBody, comChaveVazia, string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha256")]
    [InlineData("=")]
    [InlineData("abc")]
    [InlineData("sha1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("SHA256=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256=000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256=00000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256=zz00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData(" sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000 ")]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000\n")]
    [InlineData("sha256=ãããããããããããããããããããããããããããããããããããããããããããããããããããããããã")]
    public void Cabecalho_ausente_ou_mal_formado_e_falso_e_nunca_lanca(string? cabecalho)
    {
        Assert.False(Sends.Plain().VerifySignature(AsciiBody, cabecalho, Signatures.AppSecret));
    }

    [Fact]
    public void Cabecalho_com_hexadecimal_maiusculo_ou_muito_longo_nao_confere_e_nao_lanca()
    {
        var assinatura = Signatures.Header(AsciiBody);
        var maiusculo = "sha256=" + assinatura["sha256=".Length..].ToUpperInvariant();

        Assert.False(Sends.Plain().VerifySignature(AsciiBody, maiusculo, Signatures.AppSecret));
        Assert.False(Sends.Plain().VerifySignature(AsciiBody, "sha256=" + new string('a', 1_000_000), Signatures.AppSecret));
        Assert.False(Sends.Plain().VerifySignature(ReadOnlySpan<byte>.Empty, assinatura, Signatures.AppSecret));
    }

    [Fact]
    public void Corpo_vazio_com_a_assinatura_do_corpo_vazio_confere()
    {
        Assert.True(Sends.Plain().VerifySignature(ReadOnlySpan<byte>.Empty, Signatures.Header([]), Signatures.AppSecret));
    }

    [Fact]
    public void Segredo_com_substituto_sem_par_nao_lanca()
    {
        var segredo = "chave-\ud800-estranha";

        Assert.False(Sends.Plain().VerifySignature(AsciiBody, "sha256=" + new string('0', 64), segredo));
    }

    // GET de verificação

    [Fact]
    public void A_verificacao_com_modo_e_token_corretos_devolve_o_desafio()
    {
        var ok = Sends.Plain().TryAnswerVerification("subscribe", "token-de-verificacao-FICTICIO", "1158201444", "token-de-verificacao-FICTICIO", out var resposta);

        Assert.True(ok);
        Assert.Equal("1158201444", resposta);
    }

    [Theory]
    [InlineData("subscribe", "token-errado", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", "token-de-verificacao-FICTICIO ", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", "token-de-verificacao-FICTICI", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", "token-de-verificacao-FICTICIOO", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", "TOKEN-DE-VERIFICACAO-FICTICIO", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", null, "123", "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", "", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("unsubscribe", "token-de-verificacao-FICTICIO", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("SUBSCRIBE", "token-de-verificacao-FICTICIO", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("", "token-de-verificacao-FICTICIO", "123", "token-de-verificacao-FICTICIO")]
    [InlineData(null, "token-de-verificacao-FICTICIO", "123", "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", "token-de-verificacao-FICTICIO", null, "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", "token-de-verificacao-FICTICIO", "", "token-de-verificacao-FICTICIO")]
    [InlineData("subscribe", "", "123", "")]
    [InlineData("subscribe", null, "123", "")]
    public void A_verificacao_com_modo_ou_token_ou_desafio_invalidos_e_falsa_e_nao_devolve_nada(string? modo, string? token, string? desafio, string esperado)
    {
        var ok = Sends.Plain().TryAnswerVerification(modo, token, desafio, esperado, out var resposta);

        Assert.False(ok);
        Assert.Null(resposta);
    }

    [Fact]
    public void O_desafio_so_e_devolvido_se_parece_um_desafio_curto_sem_controle()
    {
        // O desafio volta num endpoint público: nunca se reflete texto com quebra de linha, espaço, marcação ou tamanho de corpo.
        foreach (var desafio in new[] { "12\n34", "<script>alert(1)</script> x", "a b", "12\r\n", new string('1', 257), "ã1" })
        {
            var ok = Sends.Plain().TryAnswerVerification("subscribe", "t", desafio, "t", out var resposta);
            Assert.False(ok, desafio);
            Assert.Null(resposta);
        }

        Assert.True(Sends.Plain().TryAnswerVerification("subscribe", "t", new string('1', 256), "t", out var longo));
        Assert.Equal(new string('1', 256), longo);
    }
}
