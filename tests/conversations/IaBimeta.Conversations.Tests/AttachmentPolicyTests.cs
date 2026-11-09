using System.Security.Cryptography;
using System.Text;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// A política de segurança dos arquivos de mídia recebidos (ADR 0028), sem I/O: tamanho, tipo permitido, assinatura do conteúdo, integridade, nome saneado, e o ciclo de vida do anexo no domínio. O que acontece com
/// o arquivo no armazenamento e no banco está nos testes de integração.
/// </summary>
public class AttachmentPolicyTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n%%EOF\n");
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

    private static AttachmentOptions Opcoes() => new();

    private static string Hex(byte[] conteudo) => Convert.ToHexString(SHA256.HashData(conteudo)).ToLowerInvariant();

    [Fact]
    public void Imagem_valida_e_aceita_com_o_tipo_verificado_o_nome_padrao_e_o_resumo()
    {
        var veredito = AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Image, Png, "image/png", "image/png", null, null);

        Assert.True(veredito.Accepted);
        Assert.Null(veredito.FailureCode);
        Assert.Equal("image/png", veredito.MimeType);
        Assert.Equal("arquivo.png", veredito.FileName);
        Assert.Equal(Hex(Png), veredito.Sha256);
    }

    [Fact]
    public void O_resumo_informado_pelo_provedor_em_hexadecimal_ou_Base64_e_conferido()
    {
        var hex = Hex(Pdf);
        var base64 = Convert.ToBase64String(SHA256.HashData(Pdf));

        Assert.True(AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Document, Pdf, "application/pdf", null, hex.ToUpperInvariant(), "a.pdf").Accepted);
        Assert.True(AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Document, Pdf, "application/pdf", null, base64, "a.pdf").Accepted);

        var errado = Convert.ToHexString(SHA256.HashData(Html)).ToLowerInvariant();
        var recusado = AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Document, Pdf, "application/pdf", null, errado, "a.pdf");
        Assert.False(recusado.Accepted);
        Assert.Equal("hash-mismatch", recusado.FailureCode);
    }

    [Fact]
    public void Resumo_em_formato_desconhecido_nao_bloqueia_pois_nao_ha_o_que_conferir()
    {
        Assert.True(AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Document, Pdf, "application/pdf", null, "isto-nao-e-um-resumo", "a.pdf").Accepted);
    }

    [Fact]
    public void Arquivo_vazio_e_recusado()
    {
        var veredito = AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Image, ReadOnlySpan<byte>.Empty, "image/png", null, null, null);

        Assert.False(veredito.Accepted);
        Assert.Equal("empty", veredito.FailureCode);
    }

    [Fact]
    public void Arquivo_acima_do_maximo_do_tipo_e_recusado_e_o_maximo_e_por_tipo()
    {
        var opcoes = new AttachmentOptions { MaxImageBytes = Png.Length - 1, MaxDocumentBytes = 1024 };

        var imagem = AttachmentPolicy.Evaluate(opcoes, MessageKind.Image, Png, "image/png", null, null, null);
        var documento = AttachmentPolicy.Evaluate(opcoes, MessageKind.Document, Pdf, "application/pdf", null, null, "a.pdf");

        Assert.Equal("too-large", imagem.FailureCode);
        Assert.True(documento.Accepted);
    }

    [Theory]
    [InlineData(MessageKind.Image, "application/pdf")]
    [InlineData(MessageKind.Image, "image/svg+xml")]
    [InlineData(MessageKind.Image, "text/html")]
    [InlineData(MessageKind.Audio, "image/png")]
    [InlineData(MessageKind.Document, "application/x-msdownload")]
    [InlineData(MessageKind.Document, "application/zip")]
    [InlineData(MessageKind.Document, "text/html")]
    [InlineData(MessageKind.Image, "")]
    [InlineData(MessageKind.Image, "nao-e-um-tipo")]
    [InlineData(MessageKind.Image, "image/png/extra")]
    public void Tipo_fora_da_lista_do_tipo_de_midia_ou_malformado_e_recusado(MessageKind tipo, string mime)
    {
        var veredito = AttachmentPolicy.Evaluate(Opcoes(), tipo, Png, mime, null, null, null);

        Assert.False(veredito.Accepted);
        Assert.Equal("type-not-allowed", veredito.FailureCode);
    }

    [Fact]
    public void Sem_tipo_do_provedor_vale_o_declarado_na_mensagem_e_sem_nenhum_dos_dois_e_recusado()
    {
        Assert.True(AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Image, Png, null, "image/png", null, null).Accepted);
        Assert.Equal("type-not-allowed", AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Image, Png, null, null, null, null).FailureCode);
    }

    [Fact]
    public void O_tipo_que_o_provedor_entregou_prevalece_sobre_o_declarado()
    {
        // O declarado diz PDF, o provedor entregou PNG como imagem: vale o do provedor, e a assinatura confere.
        var veredito = AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Image, Png, "image/png", "application/pdf", null, null);

        Assert.True(veredito.Accepted);
        Assert.Equal("image/png", veredito.MimeType);
    }

    [Fact]
    public void Parametros_do_tipo_sao_descartados_audio_ogg_com_codec_vira_audio_ogg()
    {
        var ogg = Encoding.ASCII.GetBytes("OggS\0\u0002\0\0\0\0\0\0\0\0");
        var veredito = AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Audio, ogg, "Audio/OGG; codecs=opus", null, null, null);

        Assert.True(veredito.Accepted);
        Assert.Equal("audio/ogg", veredito.MimeType);
        Assert.Equal("arquivo.ogg", veredito.FileName);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    public void Conteudo_que_nao_e_o_que_o_tipo_diz_e_recusado_mesmo_com_o_tipo_permitido(string mime)
    {
        // HTML e texto com tipo de imagem: o tipo está na lista, mas a assinatura não confere.
        var veredito = AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Image, Html, mime, null, null, null);

        Assert.False(veredito.Accepted);
        Assert.Equal("content-mismatch", veredito.FailureCode);
    }

    [Fact]
    public void Um_executavel_com_tipo_de_documento_e_recusado_pela_assinatura()
    {
        var executavel = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };

        Assert.Equal("content-mismatch", AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Document, executavel, "application/pdf", null, null, "x.pdf").FailureCode);
        Assert.Equal("content-mismatch", AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Document, executavel, "application/msword", null, null, "x.doc").FailureCode);
    }

    [Fact]
    public void Texto_com_byte_nulo_nao_e_texto_e_um_documento_de_texto_comum_passa()
    {
        var texto = Encoding.UTF8.GetBytes("Resultado do exame: ok\n");
        var binario = new byte[] { 0x41, 0x00, 0x42 };

        Assert.True(AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Document, texto, "text/plain", null, null, "nota.txt").Accepted);
        Assert.Equal("content-mismatch", AttachmentPolicy.Evaluate(Opcoes(), MessageKind.Document, binario, "text/plain", null, null, "nota.txt").FailureCode);
    }

    [Fact]
    public void Pacote_OOXML_exige_o_zip_com_a_parte_Content_Types()
    {
        var pacote = new byte[] { 0x50, 0x4B, 0x03, 0x04 }.Concat(Encoding.ASCII.GetBytes("....[Content_Types].xml....")).ToArray();
        var zipQualquer = new byte[] { 0x50, 0x4B, 0x03, 0x04 }.Concat(Encoding.ASCII.GetBytes("arquivo.txt")).ToArray();
        const string docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        Assert.True(ContentSniffer.Matches(docx, pacote));
        Assert.False(ContentSniffer.Matches(docx, zipQualquer));
        Assert.False(ContentSniffer.Matches(docx, Pdf));
    }

    [Fact]
    public void A_assinatura_de_cada_tipo_suportado_confere_e_um_conteudo_aleatorio_nao_passa()
    {
        var amostras = new Dictionary<string, byte[]>
        {
            ["image/jpeg"] = Jpeg,
            ["image/png"] = Png,
            ["image/webp"] = Encoding.ASCII.GetBytes("RIFF....WEBPVP8 "),
            ["audio/ogg"] = Encoding.ASCII.GetBytes("OggS\0\u0002"),
            ["audio/amr"] = Encoding.ASCII.GetBytes("#!AMR\n"),
            ["audio/mp4"] = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20],
            ["audio/mpeg"] = Encoding.ASCII.GetBytes("ID3\u0003\0\0"),
            ["audio/aac"] = [0xFF, 0xF1, 0x50, 0x80],
            ["application/pdf"] = Pdf,
            ["application/msword"] = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00],
            ["text/plain"] = Encoding.UTF8.GetBytes("texto"),
        };

        foreach (var (mime, conteudo) in amostras)
        {
            Assert.True(ContentSniffer.IsSupported(mime), mime);
            Assert.True(ContentSniffer.Matches(mime, conteudo), $"{mime} deveria conferir com a própria assinatura.");
        }

        // Um conteúdo de 16 bytes que não é nenhum destes formatos não confere com nenhum tipo binário.
        var aleatorio = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10 };
        foreach (var mime in amostras.Keys.Where(m => m != "text/plain"))
        {
            Assert.False(ContentSniffer.Matches(mime, aleatorio), $"{mime} não pode aceitar um conteúdo sem a assinatura.");
        }

        Assert.False(ContentSniffer.Matches("application/x-desconhecido", Png));
        Assert.False(ContentSniffer.IsSupported("image/svg+xml"));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\system32\\cmd.exe", "cmd.exe")]
    [InlineData("C:\\Users\\x\\laudo.pdf", "laudo.pdf")]
    [InlineData("  laudo final.pdf  ", "laudo final.pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "abcdefgh.txt")]
    [InlineData("laudo\u0000\u0007.pdf", "laudo.pdf")]
    [InlineData("recibo\u202Efdp.exe", "recibofdp.exe")]
    public void O_nome_do_cliente_e_saneado_sem_caminho_controle_ou_inversao_de_texto(string entrada, string esperado)
    {
        Assert.Equal(esperado, AttachmentPolicy.SanitizeDisplayName(entrada));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("../")]
    [InlineData("\u0000\u0001")]
    public void Nome_sem_conteudo_aproveitavel_vira_nulo(string? entrada)
    {
        Assert.Null(AttachmentPolicy.SanitizeDisplayName(entrada));
    }

    [Fact]
    public void Nome_muito_longo_e_cortado_e_mantem_a_extensao()
    {
        var longo = new string('a', 300) + ".pdf";

        var nome = AttachmentPolicy.SanitizeDisplayName(longo)!;

        Assert.Equal(120, nome.Length);
        Assert.EndsWith(".pdf", nome);
    }

    [Fact]
    public void O_nome_final_ganha_a_extensao_do_tipo_verificado_quando_nao_tem_uma()
    {
        Assert.Equal("laudo.pdf", AttachmentPolicy.FileNameFor("laudo", "application/pdf"));
        Assert.Equal("laudo.pdf", AttachmentPolicy.FileNameFor("laudo.pdf", "application/pdf"));
        Assert.Equal("arquivo.jpg", AttachmentPolicy.FileNameFor(null, "image/jpeg"));
        Assert.Equal("arquivo", AttachmentPolicy.FileNameFor("   ", "application/x-desconhecido"));
    }

    [Fact]
    public void A_legenda_perde_o_controle_menos_a_quebra_de_linha_e_e_limitada()
    {
        Assert.Equal("Linha 1\nLinha 2", AttachmentPolicy.SanitizeCaption("  Linha 1\u0007\nLinha 2\u0000 "));
        Assert.Null(AttachmentPolicy.SanitizeCaption("\u0007\u0000"));
        Assert.Null(AttachmentPolicy.SanitizeCaption(null));
        Assert.Equal(MessageAttachment.CaptionMaxLength, AttachmentPolicy.SanitizeCaption(new string('x', 5000))!.Length);
    }

    [Fact]
    public void O_resumo_aceita_so_hexadecimal_de_64_digitos_ou_Base64_de_32_bytes()
    {
        var bytes = SHA256.HashData(Pdf);

        Assert.Equal(bytes, AttachmentPolicy.ParseSha256(Convert.ToHexString(bytes)));
        Assert.Equal(bytes, AttachmentPolicy.ParseSha256($" {Convert.ToBase64String(bytes)} "));
        Assert.Null(AttachmentPolicy.ParseSha256(Convert.ToBase64String(new byte[16])));
        Assert.Null(AttachmentPolicy.ParseSha256("zzzz"));
        Assert.Null(AttachmentPolicy.ParseSha256(null));
        Assert.Null(AttachmentPolicy.ParseSha256("  "));
    }

    [Fact]
    public void As_opcoes_padrao_seguem_os_limites_do_WhatsApp_e_recusam_tipo_nao_conferivel_na_partida()
    {
        var opcoes = Opcoes();

        Assert.Equal(5L * 1024 * 1024, opcoes.MaxBytesFor(MessageKind.Image));
        Assert.Equal(16L * 1024 * 1024, opcoes.MaxBytesFor(MessageKind.Audio));
        Assert.Equal(0, opcoes.MaxBytesFor(MessageKind.Text));
        Assert.Contains("image/webp", opcoes.AllowedTypesFor(MessageKind.Image));
        Assert.Contains("audio/ogg", opcoes.AllowedTypesFor(MessageKind.Audio));
        Assert.Contains("application/pdf", opcoes.AllowedTypesFor(MessageKind.Document));
        Assert.Empty(opcoes.AllowedTypesFor(MessageKind.Text));
        opcoes.Validate();

        Assert.Throws<InvalidOperationException>(() => new AttachmentOptions { MaxImageBytes = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AttachmentOptions { MaxDocumentBytes = AttachmentOptions.HardMaxBytes + 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AttachmentOptions { AllowedImageTypes = ["image/svg+xml"] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AttachmentOptions { AllowedDocumentTypes = ["text/html"] }.Validate());

        // A lista configurada substitui a padrão e é normalizada.
        var restrita = new AttachmentOptions { AllowedImageTypes = ["IMAGE/PNG; q=1"] };
        restrita.Validate();
        Assert.Equal(["image/png"], restrita.AllowedTypesFor(MessageKind.Image));
        Assert.Equal("type-not-allowed", AttachmentPolicy.Evaluate(restrita, MessageKind.Image, Jpeg, "image/jpeg", null, null, null).FailureCode);
    }

    private static MessageAttachment Pendente(MessageKind tipo = MessageKind.Image, string idMidia = "1234567890", string? nome = null, string? legenda = null) =>
        MessageAttachment.CreatePending(Fabrica.Tenant, Guid.NewGuid(), Guid.NewGuid(), tipo, new InboundAttachment(idMidia, "image/jpeg", nome, legenda), Fabrica.Agora);

    [Fact]
    public void O_anexo_nasce_pendente_com_nome_e_legenda_saneados_e_so_para_midia()
    {
        var anexo = Pendente(nome: "../x/laudo.pdf", legenda: "Segue\u0007 o laudo");

        Assert.Equal(AttachmentStatus.Pending, anexo.Status);
        Assert.Equal("laudo.pdf", anexo.FileName);
        Assert.Equal("Segue o laudo", anexo.Caption);
        Assert.Equal("image/jpeg", anexo.DeclaredMimeType);
        Assert.Null(anexo.StorageKey);
        Assert.Null(anexo.MimeType);

        Assert.Throws<ConversationsValidationException>(() => Pendente(MessageKind.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("com espaco")]
    [InlineData("acentuação")]
    [InlineData("id\nquebra")]
    public void O_identificador_da_midia_no_provedor_precisa_ser_ASCII_visivel(string id)
    {
        Assert.Throws<ConversationsValidationException>(() => Pendente(idMidia: id));
    }

    [Fact]
    public void O_identificador_da_midia_tem_tamanho_maximo()
    {
        Assert.Throws<ConversationsValidationException>(() => Pendente(idMidia: new string('9', MessageAttachment.ProviderMediaIdMaxLength + 1)));
        Assert.NotNull(Pendente(idMidia: new string('9', MessageAttachment.ProviderMediaIdMaxLength)));
    }

    [Fact]
    public void Sai_de_pendente_uma_unica_vez_para_disponivel_indisponivel_ou_recusado()
    {
        var disponivel = Pendente();
        Assert.True(disponivel.MarkAvailable("image/jpeg", "a.jpg", 10, new string('a', 64), "chave", Fabrica.Agora));
        Assert.Equal(AttachmentStatus.Available, disponivel.Status);
        Assert.False(disponivel.MarkAvailable("image/png", "b.png", 20, new string('b', 64), "outra", Fabrica.Agora));
        Assert.False(disponivel.MarkFailed(AttachmentStatus.Unavailable, "expired", Fabrica.Agora));
        Assert.Equal("a.jpg", disponivel.FileName);
        Assert.Null(disponivel.FailureCode);

        var indisponivel = Pendente();
        Assert.True(indisponivel.MarkFailed(AttachmentStatus.Unavailable, "expired", Fabrica.Agora));
        Assert.Equal("expired", indisponivel.FailureCode);
        Assert.False(indisponivel.MarkAvailable("image/jpeg", "a.jpg", 10, new string('a', 64), "chave", Fabrica.Agora));
        Assert.Equal(AttachmentStatus.Unavailable, indisponivel.Status);

        var recusado = Pendente();
        Assert.True(recusado.MarkFailed(AttachmentStatus.Rejected, "content-mismatch", Fabrica.Agora));
        Assert.False(recusado.MarkFailed(AttachmentStatus.Unavailable, "expired", Fabrica.Agora));
        Assert.Equal(AttachmentStatus.Rejected, recusado.Status);
        Assert.Null(recusado.StorageKey);
    }

    [Theory]
    [InlineData(AttachmentStatus.Pending, "expired")]
    [InlineData(AttachmentStatus.Available, "expired")]
    [InlineData(AttachmentStatus.Rejected, "")]
    [InlineData(AttachmentStatus.Rejected, "com espaco")]
    [InlineData(AttachmentStatus.Unavailable, "codigo-com-mais-de-sessenta-e-quatro-caracteres-para-exceder-o-limite-do-campo")]
    public void A_falha_so_aceita_o_estado_de_falha_e_um_codigo_curto_sem_texto_livre(AttachmentStatus status, string codigo)
    {
        Assert.Throws<ConversationsValidationException>(() => Pendente().MarkFailed(status, codigo, Fabrica.Agora));
    }
}
