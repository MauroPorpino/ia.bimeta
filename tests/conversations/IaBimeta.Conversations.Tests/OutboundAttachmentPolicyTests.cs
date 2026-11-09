using System.Text;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// A política do arquivo que o ATENDENTE envia (ADR 0031), sem I/O. O que o navegador informa (tipo e extensão) nunca decide: o tipo é o que o CONTEÚDO é, e a contradição recusa o arquivo.
/// </summary>
public class OutboundAttachmentPolicyTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n%%EOF\n");
    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 0];
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00];
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
    private static readonly byte[] Texto = Encoding.UTF8.GetBytes("Resultado liberado.\nAtenciosamente.");

    private static AttachmentOptions Opcoes() => new();

    private static OutboundAttachmentVerdict Avaliar(byte[] conteudo, string? tipo, string? nome, string? legenda = null, AttachmentOptions? opcoes = null) =>
        OutboundAttachmentPolicy.Evaluate(opcoes ?? Opcoes(), conteudo, tipo, nome, legenda);

    private static byte[] OggOpus(byte canais) => [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 19, .. "OpusHead"u8, 1, canais, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];

    private static byte[] Ogg(string codec) => [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 30, 1, .. Encoding.ASCII.GetBytes(codec), 0, 0, 0, 0];

    private static byte[] Mp4(string marca, bool video)
    {
        var bytes = new List<byte> { 0, 0, 0, 0x20 };
        bytes.AddRange("ftyp"u8.ToArray());
        bytes.AddRange(Encoding.ASCII.GetBytes(marca));
        bytes.AddRange(new byte[20]);
        bytes.AddRange(new byte[] { 0, 0, 0, 0x2C });
        bytes.AddRange("hdlr"u8.ToArray());
        bytes.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });
        bytes.AddRange(video ? "vide"u8.ToArray() : "soun"u8.ToArray());
        bytes.AddRange(new byte[16]);
        return bytes.ToArray();
    }

    private static byte[] Zip(params string[] entradas)
    {
        var bytes = new List<byte> { 0x50, 0x4B, 0x03, 0x04, 0x14, 0, 0, 0, 0, 0 };
        foreach (var entrada in entradas)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(entrada));
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        }

        return bytes.ToArray();
    }

    private static byte[] Ole() => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];

    [Theory]
    [InlineData("image/png", "foto.png", "image/png", MessageKind.Image)]
    [InlineData("image/jpeg", "foto.jpg", "image/jpeg", MessageKind.Image)]
    [InlineData("application/pdf", "laudo.pdf", "application/pdf", MessageKind.Document)]
    [InlineData("text/plain", "aviso.txt", "text/plain", MessageKind.Document)]
    public void O_tipo_e_o_tipo_de_midia_saem_do_conteudo(string declarado, string nome, string esperado, MessageKind kind)
    {
        var conteudo = esperado switch
        {
            "image/png" => Png,
            "image/jpeg" => Jpeg,
            "application/pdf" => Pdf,
            _ => Texto,
        };

        var veredito = Avaliar(conteudo, declarado, nome);

        Assert.True(veredito.Accepted, veredito.FailureCode);
        Assert.Equal(esperado, veredito.MimeType);
        Assert.Equal(kind, veredito.Kind);
        Assert.Equal(nome, veredito.FileName);
        Assert.Equal(64, veredito.Sha256!.Length);
    }

    [Fact]
    public void O_tipo_declarado_pelo_navegador_e_dispensavel_e_um_generico_nao_decide()
    {
        // O navegador pode não informar nada, ou informar o genérico: o conteúdo decide.
        Assert.Equal("image/png", Avaliar(Png, null, "x.png").MimeType);
        Assert.Equal("application/pdf", Avaliar(Pdf, "application/octet-stream", "x.pdf").MimeType);
    }

    [Fact]
    public void O_tipo_de_midia_nunca_e_o_que_o_chamador_diz_porque_sai_so_do_conteudo()
    {
        // Um PDF enviado "como imagem" continua sendo documento.
        var veredito = Avaliar(Pdf, "image/png", "laudo.pdf");

        Assert.False(veredito.Accepted);
        Assert.Equal("content-mismatch", veredito.FailureCode);
        Assert.Equal(MessageKind.Document, Avaliar(Pdf, "application/pdf", "laudo.pdf").Kind);
    }

    [Theory]
    [InlineData("image/jpeg", "foto.png")]
    [InlineData("application/pdf", "foto.png")]
    public void Tipo_ou_extensao_que_contradizem_o_conteudo_recusam_o_arquivo(string declarado, string nome)
    {
        var veredito = Avaliar(Png, declarado, nome);

        Assert.False(veredito.Accepted);
        Assert.Equal("content-mismatch", veredito.FailureCode);
    }

    [Fact]
    public void Extensao_de_outro_tipo_conhecido_recusa_mesmo_sem_tipo_declarado()
    {
        var veredito = Avaliar(Png, null, "relatorio.pdf");

        Assert.False(veredito.Accepted);
        Assert.Equal("content-mismatch", veredito.FailureCode);
    }

    [Theory]
    [InlineData("programa.png")]
    [InlineData("pagina.html")]
    public void Executavel_e_pagina_nao_sao_tipos_enviaveis_nem_disfarcados(string nome)
    {
        Assert.Equal("type-not-allowed", Avaliar(Exe, "image/png", nome).FailureCode);
        Assert.False(Avaliar(Html, "text/html", nome).Accepted);
    }

    [Fact]
    public void Texto_so_vale_como_documento_de_texto_e_um_script_com_outra_extensao_e_recusado()
    {
        Assert.True(Avaliar(Texto, "text/plain", "aviso.txt").Accepted);
        Assert.Equal("content-mismatch", Avaliar(Html, "text/plain", "pagina.html").FailureCode);
        Assert.Equal("content-mismatch", Avaliar(Texto, "text/plain", "instalar.bat").FailureCode);
    }

    [Fact]
    public void WebP_e_um_tipo_que_so_a_recepcao_conhece_e_nao_e_enviado()
    {
        Assert.Equal("type-not-allowed", Avaliar(Webp, "image/webp", "foto.webp").FailureCode);
    }

    [Fact]
    public void Vazio_e_recusado_e_o_maximo_e_por_tipo_de_midia()
    {
        Assert.Equal("empty", Avaliar([], "image/png", "x.png").FailureCode);

        var opcoes = new AttachmentOptions { MaxImageBytes = Png.Length - 1, MaxDocumentBytes = 1024 * 1024 };
        Assert.Equal("too-large", Avaliar(Png, "image/png", "x.png", opcoes: opcoes).FailureCode);
        Assert.True(Avaliar(Pdf, "application/pdf", "x.pdf", opcoes: opcoes).Accepted);
    }

    [Fact]
    public void Acima_do_maior_limite_nada_e_examinado()
    {
        var opcoes = new AttachmentOptions { MaxImageBytes = 4, MaxAudioBytes = 4, MaxDocumentBytes = 4 };

        Assert.Equal("too-large", Avaliar(Pdf, "application/pdf", "x.pdf", opcoes: opcoes).FailureCode);
    }

    [Fact]
    public void A_lista_de_tipos_configurada_tambem_vale_para_o_envio()
    {
        var opcoes = new AttachmentOptions { AllowedDocumentTypes = ["text/plain"] };

        Assert.Equal("type-not-allowed", Avaliar(Pdf, "application/pdf", "x.pdf", opcoes: opcoes).FailureCode);
        Assert.True(Avaliar(Texto, "text/plain", "x.txt", opcoes: opcoes).Accepted);
    }

    // Áudio

    [Fact]
    public void Ogg_so_vale_com_Opus_em_mono()
    {
        Assert.True(Avaliar(OggOpus(1), "audio/ogg", "nota.ogg").Accepted);
        Assert.Equal("audio-codec-unsupported", Avaliar(OggOpus(2), "audio/ogg", "nota.ogg").FailureCode);
        Assert.Equal("audio-codec-unsupported", Avaliar(Ogg("vorbis"), "audio/ogg", "musica.ogg").FailureCode);
    }

    [Fact]
    public void Audio_em_MP4_vale_mas_video_e_imagem_HEIC_com_o_mesmo_cabecalho_sao_recusados()
    {
        Assert.True(Avaliar(Mp4("M4A ", video: false), "audio/mp4", "nota.m4a").Accepted);
        Assert.Equal("content-mismatch", Avaliar(Mp4("isom", video: true), "audio/mp4", "filme.mp4").FailureCode);
        Assert.Equal("content-mismatch", Avaliar(Mp4("heic", video: false), "audio/mp4", "foto.m4a").FailureCode);
    }

    [Fact]
    public void Audio_tem_o_tipo_de_midia_audio_e_nao_aceita_legenda()
    {
        Assert.Equal(MessageKind.Audio, Avaliar(OggOpus(1), null, "nota.ogg").Kind);
        Assert.Equal("caption-not-supported", Avaliar(OggOpus(1), "audio/ogg", "nota.ogg", "Segue o áudio").FailureCode);
        Assert.True(Avaliar(OggOpus(1), "audio/ogg", "nota.ogg", "   ").Accepted); // legenda em branco é como nenhuma
    }

    [Fact]
    public void Mp3_e_AAC_sao_distintos_pela_camada_do_quadro()
    {
        var mp3 = new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0, 0, 0, 0 };
        var aac = new byte[] { 0xFF, 0xF1, 0x50, 0x80, 0, 0, 0, 0 };

        Assert.Equal("audio/mpeg", Avaliar(mp3, null, "a.mp3").MimeType);
        Assert.Equal("audio/aac", Avaliar(aac, null, "a.aac").MimeType);
        Assert.Equal("audio/mpeg", Avaliar([.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, 0], "audio/mpeg", "a.mp3").MimeType);
    }

    // Documentos do Office

    [Fact]
    public void O_pacote_do_Office_e_identificado_pela_estrutura_e_nao_pelo_que_o_navegador_diz()
    {
        var docx = Zip("[Content_Types].xml", "word/document.xml");
        var xlsx = Zip("[Content_Types].xml", "xl/workbook.xml");
        var pptx = Zip("[Content_Types].xml", "ppt/presentation.xml");

        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", Avaliar(docx, "application/octet-stream", "a.docx").MimeType);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Avaliar(xlsx, null, "a.xlsx").MimeType);
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", Avaliar(pptx, null, "a.pptx").MimeType);

        // Um pacote do Word declarado como planilha, ou com extensão de planilha, é recusado.
        Assert.Equal("content-mismatch", Avaliar(docx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "a.docx").FailureCode);
        Assert.Equal("content-mismatch", Avaliar(docx, null, "a.xlsx").FailureCode);
    }

    [Fact]
    public void Um_ZIP_qualquer_e_um_pacote_sem_pasta_conhecida_nao_sao_documentos()
    {
        Assert.Equal("type-not-allowed", Avaliar(Zip("dados.csv"), "application/zip", "a.zip").FailureCode);
        Assert.Equal("type-not-allowed", Avaliar(Zip("[Content_Types].xml"), null, "a.docx").FailureCode);
        Assert.Equal("type-not-allowed", Avaliar(Zip("[Content_Types].xml", "word/document.xml", "xl/workbook.xml"), null, "a.docx").FailureCode);
    }

    [Fact]
    public void Os_formatos_antigos_do_Office_so_se_distinguem_pelo_tipo_declarado_ou_pela_extensao()
    {
        Assert.Equal("application/msword", Avaliar(Ole(), "application/msword", "a.doc").MimeType);
        Assert.Equal("application/vnd.ms-excel", Avaliar(Ole(), null, "planilha.xls").MimeType);
        Assert.Equal("application/vnd.ms-powerpoint", Avaliar(Ole(), "application/vnd.ms-powerpoint", "x").MimeType);

        // Sem nada que desempate, não há como saber qual é.
        Assert.Equal("type-not-allowed", Avaliar(Ole(), null, "sem-extensao").FailureCode);
    }

    // Nome e legenda

    [Fact]
    public void O_nome_e_saneado_e_ganha_a_extensao_do_tipo_real_quando_nao_tem_uma_que_o_confirme()
    {
        Assert.Equal("passwd.png", Avaliar(Png, null, "../../etc/passwd").FileName);
        Assert.Equal("arquivo.png", Avaliar(Png, null, null).FileName);
        Assert.Equal("Contrato 2024.01.pdf", Avaliar(Pdf, null, "Contrato 2024.01").FileName);
        Assert.Equal("foto.jpeg", Avaliar(Jpeg, null, "foto.jpeg").FileName);
        Assert.Equal("laudo.pdf", Avaliar(Pdf, null, "laudo‮.pdf").FileName);
    }

    [Fact]
    public void O_nome_final_nunca_passa_de_120_caracteres()
    {
        var longo = new string('a', 300) + ".pdf";

        Assert.True(Avaliar(Pdf, null, longo).FileName!.Length <= 120);
        Assert.EndsWith(".pdf", Avaliar(Pdf, null, new string('b', 300)).FileName);
    }

    [Fact]
    public void A_legenda_e_saneada_e_acima_de_1024_caracteres_e_recusada_em_vez_de_cortada()
    {
        var veredito = Avaliar(Png, "image/png", "x.png", "Linha 1\nLinha 2\u0007");
        Assert.Equal("Linha 1\nLinha 2", veredito.Caption);

        Assert.Equal(new string('a', 1024), Avaliar(Png, "image/png", "x.png", new string('a', 1024)).Caption);
        Assert.Equal("caption-too-long", Avaliar(Png, "image/png", "x.png", new string('a', 1025)).FailureCode);
        Assert.Null(Avaliar(Png, "image/png", "x.png", null).Caption);
    }

    [Fact]
    public void O_resumo_e_o_SHA_256_do_conteudo()
    {
        var esperado = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Pdf)).ToLowerInvariant();

        Assert.Equal(esperado, Avaliar(Pdf, null, "x.pdf").Sha256);
    }
}
