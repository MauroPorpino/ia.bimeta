using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// A NORMALIZAÇÃO do webhook <c>whatsapp_business_account</c> (ADR 0011, A.2 e B.2): texto, mídias (só metadados), tipos sem tratamento, lote com vários números, status, e a regra de que entrada inválida nunca lança
/// (corpo inválido: nulo; item com campo faltando: descartado e contado, sem perder o resto do lote). Os payloads seguem a forma documentada pela Meta (consulta de 05/11/2026).
/// </summary>
public sealed class ParseTests
{
    private static ParsedWebhook? Parse(JsonNode webhook) => Sends.Plain().Parse(Webhooks.Bytes(webhook));

    private static ParsedWebhook ParseOk(JsonNode webhook) => Assert.IsType<ParsedWebhook>(Parse(webhook));

    // Texto

    [Fact]
    public void Texto_vira_Text_com_o_numero_o_contato_o_nome_o_instante_e_o_corpo_exatamente_como_veio()
    {
        const string Corpo = "  Olá! Preciso de jejum para o exame de coração? 😀\nSegunda linha — “aspas”  ";
        var webhook = Webhooks.Envelope(Webhooks.Value(
            contacts: new JsonArray(Webhooks.Contact("5511999990001", "Mauro Fictício")),
            messages: new JsonArray(Webhooks.TextMessage("wamid.AAA", "5511999990001", Corpo))));

        var resultado = ParseOk(webhook);

        var mensagem = Assert.Single(resultado.Messages);
        Assert.Empty(resultado.Statuses);
        Assert.Equal(0, resultado.IgnoredEntries);
        Assert.Equal(Webhooks.Phone, mensagem.ExternalAccountId);
        Assert.Equal("wamid.AAA", mensagem.ProviderMessageId);
        Assert.Equal("5511999990001", mensagem.ContactExternalRef);
        Assert.Equal("Mauro Fictício", mensagem.ContactDisplayName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1749416383), mensagem.ProviderTimestamp);
        Assert.Equal(ProviderMessageKind.Text, mensagem.Kind);
        Assert.Equal(Corpo, mensagem.Text);
        Assert.Null(mensagem.ChannelData);
    }

    [Fact]
    public void O_payload_acentuado_em_bytes_utf8_e_lido_sem_corromper_o_texto()
    {
        // O corpo BRUTO (como chega do HTTP), com os caracteres literais em UTF-8 e com escapes \u: os dois resultam no mesmo texto.
        var literal = "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"field\":\"messages\",\"value\":{\"metadata\":{\"phone_number_id\":\"1\"},\"messages\":[{\"from\":\"55\",\"id\":\"m1\",\"timestamp\":\"1749416383\",\"type\":\"text\",\"text\":{\"body\":\"Olá, ação — çãõ 😀\"}}]}}]}]}";
        var escapado = "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"field\":\"messages\",\"value\":{\"metadata\":{\"phone_number_id\":\"1\"},\"messages\":[{\"from\":\"55\",\"id\":\"m1\",\"timestamp\":\"1749416383\",\"type\":\"text\",\"text\":{\"body\":\"Ol\\u00e1, a\\u00e7\\u00e3o \\u2014 \\u00e7\\u00e3\\u00f5 \\ud83d\\ude00\"}}]}}]}]}";

        var a = Assert.Single(Assert.IsType<ParsedWebhook>(Sends.Plain().Parse(Encoding.UTF8.GetBytes(literal))).Messages);
        var b = Assert.Single(Assert.IsType<ParsedWebhook>(Sends.Plain().Parse(Encoding.UTF8.GetBytes(escapado))).Messages);

        Assert.Equal("Olá, ação — çãõ 😀", a.Text);
        Assert.Equal(a.Text, b.Text);
    }

    [Fact]
    public void Contato_sem_nome_ou_de_outro_wa_id_deixa_o_nome_nulo()
    {
        var webhook = Webhooks.Envelope(Webhooks.Value(
            contacts: new JsonArray(Webhooks.Contact("5511000000000", "Outra Pessoa")),
            messages: new JsonArray(Webhooks.TextMessage("m1", "5511999990001", "oi"))));

        Assert.Null(Assert.Single(ParseOk(webhook).Messages).ContactDisplayName);

        var semContatos = Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(Webhooks.TextMessage("m1", "5511999990001", "oi"))));
        Assert.Null(Assert.Single(ParseOk(semContatos).Messages).ContactDisplayName);
    }

    [Fact]
    public void Timestamp_numerico_tambem_e_aceito()
    {
        var mensagem = Webhooks.TextMessage("m1", "55", "oi");
        mensagem["timestamp"] = 1749416383;

        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(mensagem))));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1749416383), Assert.Single(resultado.Messages).ProviderTimestamp);
    }

    // Mídias: só metadados

    [Fact]
    public void Audio_de_voz_vira_Audio_so_com_metadados_e_sem_texto()
    {
        var midia = new JsonObject
        {
            ["mime_type"] = "audio/ogg; codecs=opus",
            ["sha256"] = "wvqXMe6n7n1W0zphvLPoLj+s/NtKqmr3zZ7YzTP7xFI=",
            ["id"] = "1908647269898587",
            ["voice"] = true,
            ["url"] = "https://lookaside.fbsbx.com/whatsapp_business/attachments/?mid=SEGREDO",
        };

        var mensagem = Assert.Single(ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(Webhooks.MediaMessage("audio", "m1", "55", midia))))).Messages);

        Assert.Equal(ProviderMessageKind.Audio, mensagem.Kind);
        Assert.Null(mensagem.Text);
        Assert.Equal(
            new Dictionary<string, string> { ["media_id"] = "1908647269898587", ["mime_type"] = "audio/ogg; codecs=opus", ["voice"] = "true" },
            mensagem.ChannelData!.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public void O_tipo_voice_tambem_vira_Audio()
    {
        var midia = new JsonObject { ["id"] = "42", ["mime_type"] = "audio/ogg" };

        var mensagem = Assert.Single(ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(Webhooks.MediaMessage("voice", "m1", "55", midia))))).Messages);

        Assert.Equal(ProviderMessageKind.Audio, mensagem.Kind);
        Assert.Equal("42", mensagem.ChannelData!["media_id"]);
    }

    [Fact]
    public void Imagem_com_legenda_vira_Image_com_a_legenda_no_texto_e_nos_metadados()
    {
        var midia = new JsonObject { ["caption"] = "Minha requisição médica", ["mime_type"] = "image/jpeg", ["sha256"] = "SfInY0gG", ["id"] = "1003383421387256", ["url"] = "https://lookaside.fbsbx.com/x" };

        var mensagem = Assert.Single(ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(Webhooks.MediaMessage("image", "m1", "55", midia))))).Messages);

        Assert.Equal(ProviderMessageKind.Image, mensagem.Kind);
        Assert.Equal("Minha requisição médica", mensagem.Text);
        Assert.Equal("1003383421387256", mensagem.ChannelData!["media_id"]);
        Assert.Equal("image/jpeg", mensagem.ChannelData["mime_type"]);
        Assert.Equal("Minha requisição médica", mensagem.ChannelData["caption"]);
    }

    [Fact]
    public void Imagem_sem_legenda_nao_tem_texto_nem_chave_de_legenda()
    {
        var midia = new JsonObject { ["mime_type"] = "image/png", ["id"] = "7" };

        var mensagem = Assert.Single(ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(Webhooks.MediaMessage("image", "m1", "55", midia))))).Messages);

        Assert.Null(mensagem.Text);
        Assert.False(mensagem.ChannelData!.ContainsKey("caption"));
    }

    [Fact]
    public void Documento_e_video_viram_Document_e_Video_com_nome_do_arquivo_so_no_documento()
    {
        var documento = new JsonObject { ["caption"] = "my receipt", ["filename"] = "receipt.pdf", ["mime_type"] = "application/pdf", ["sha256"] = "V5OP", ["id"] = "622684793477189" };
        var video = new JsonObject { ["caption"] = "Timelapse", ["filename"] = "ignorado.mp4", ["mime_type"] = "video/mp4", ["sha256"] = "vdGU", ["id"] = "731675419373506" };

        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(
            Webhooks.MediaMessage("document", "m1", "55", documento),
            Webhooks.MediaMessage("video", "m2", "55", video)))));

        Assert.Equal(2, resultado.Messages.Count);
        var d = resultado.Messages[0];
        var v = resultado.Messages[1];
        Assert.Equal(ProviderMessageKind.Document, d.Kind);
        Assert.Equal("receipt.pdf", d.ChannelData!["filename"]);
        Assert.Equal("application/pdf", d.ChannelData["mime_type"]);
        Assert.Equal(ProviderMessageKind.Video, v.Kind);
        Assert.Equal("video/mp4", v.ChannelData!["mime_type"]);
        Assert.False(v.ChannelData.ContainsKey("filename"));
    }

    [Fact]
    public void A_midia_nunca_leva_url_nem_hash_e_nada_e_baixado()
    {
        var midia = new JsonObject { ["mime_type"] = "image/jpeg", ["sha256"] = "HASH-SECRETO", ["id"] = "9", ["url"] = "https://lookaside.fbsbx.com/whatsapp_business/attachments/?mid=URL-SECRETA" };

        var mensagem = Assert.Single(ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(Webhooks.MediaMessage("image", "m1", "55", midia))))).Messages);

        Assert.All(mensagem.ChannelData!, par =>
        {
            Assert.DoesNotContain("URL-SECRETA", par.Value);
            Assert.DoesNotContain("HASH-SECRETO", par.Value);
        });
        Assert.DoesNotContain(mensagem.ChannelData!.Keys, k => k is "url" or "sha256");
    }

    [Fact]
    public void Midia_sem_o_id_da_midia_e_descartada_e_contada()
    {
        var semId = new JsonObject { ["mime_type"] = "image/jpeg" };

        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(
            Webhooks.MediaMessage("image", "m1", "55", semId),
            Webhooks.TextMessage("m2", "55", "depois")))));

        Assert.Equal("m2", Assert.Single(resultado.Messages).ProviderMessageId);
        Assert.Equal(1, resultado.IgnoredEntries);
    }

    // Tipos sem tratamento

    [Theory]
    [InlineData("sticker", "sticker")]
    [InlineData("location", "location")]
    [InlineData("contacts", "contacts")]
    [InlineData("reaction", "reaction")]
    [InlineData("interactive", "interactive")]
    [InlineData("button", "button")]
    [InlineData("order", "order")]
    [InlineData("unsupported", "unsupported")]
    [InlineData("tipo_futuro_2027", "tipo_futuro_2027")]
    [InlineData("TIPO COM ESPAÇO <b>", "unknown")]
    public void Tipo_desconhecido_vira_Other_so_com_o_nome_do_tipo_e_sem_conteudo(string tipo, string esperado)
    {
        var mensagem = new JsonObject
        {
            ["from"] = "55",
            ["id"] = "m1",
            ["timestamp"] = "1749416383",
            ["type"] = tipo,
            [tipo] = new JsonObject { ["latitude"] = -23.5, ["name"] = "CONTEUDO-DO-CLIENTE" },
        };

        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(mensagem))));

        var normalizada = Assert.Single(resultado.Messages);
        Assert.Equal(ProviderMessageKind.Other, normalizada.Kind);
        Assert.Null(normalizada.Text);
        Assert.Equal(esperado, Assert.Single(normalizada.ChannelData!).Value);
        Assert.Equal(0, resultado.IgnoredEntries);
    }

    // Lote

    [Fact]
    public void Lote_com_varias_mensagens_varios_contatos_e_varios_numeros_e_normalizado_por_numero()
    {
        var webhook = Webhooks.Envelope(
            Webhooks.Value(
                Webhooks.Phone,
                contacts: new JsonArray(Webhooks.Contact("5511111111111", "Ana"), Webhooks.Contact("5522222222222", "Beto")),
                messages: new JsonArray(
                    Webhooks.TextMessage("m1", "5511111111111", "oi da Ana"),
                    Webhooks.TextMessage("m2", "5522222222222", "oi do Beto"),
                    Webhooks.MediaMessage("image", "m3", "5511111111111", new JsonObject { ["id"] = "img1", ["mime_type"] = "image/png" }))),
            Webhooks.Value(
                Webhooks.OtherPhone,
                contacts: new JsonArray(Webhooks.Contact("5533333333333", "Caio")),
                messages: new JsonArray(Webhooks.TextMessage("m4", "5533333333333", "oi do Caio")),
                statuses: new JsonArray(Webhooks.Status("out1", "delivered"))));

        var resultado = ParseOk(webhook);

        Assert.Equal(new[] { "m1", "m2", "m3", "m4" }, resultado.Messages.Select(m => m.ProviderMessageId).ToArray());
        Assert.Equal(new[] { Webhooks.Phone, Webhooks.Phone, Webhooks.Phone, Webhooks.OtherPhone }, resultado.Messages.Select(m => m.ExternalAccountId).ToArray());
        Assert.Equal(new[] { "Ana", "Beto", "Ana", "Caio" }, resultado.Messages.Select(m => m.ContactDisplayName).ToArray());
        var status = Assert.Single(resultado.Statuses);
        Assert.Equal(Webhooks.OtherPhone, status.ExternalAccountId);
        Assert.Equal(0, resultado.IgnoredEntries);
    }

    [Fact]
    public void Varias_mudancas_na_mesma_entrada_sao_todas_lidas()
    {
        var webhook = new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = "waba",
                ["changes"] = new JsonArray(
                    new JsonObject { ["field"] = "messages", ["value"] = Webhooks.Value(messages: new JsonArray(Webhooks.TextMessage("m1", "55", "a"))) },
                    new JsonObject { ["field"] = "messages", ["value"] = Webhooks.Value(Webhooks.OtherPhone, messages: new JsonArray(Webhooks.TextMessage("m2", "66", "b"))) }),
            }),
        };

        var resultado = ParseOk(webhook);

        Assert.Equal(new[] { "m1", "m2" }, resultado.Messages.Select(m => m.ProviderMessageId).ToArray());
    }

    // Status

    [Theory]
    [InlineData("sent", ProviderDeliveryStatus.Sent)]
    [InlineData("delivered", ProviderDeliveryStatus.Delivered)]
    [InlineData("read", ProviderDeliveryStatus.Read)]
    public void Status_de_envio_entrega_e_leitura_levam_o_dado_opaco_de_volta(string estado, ProviderDeliveryStatus esperado)
    {
        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(statuses: new JsonArray(Webhooks.Status("wamid.OUT", estado, "1750030073", "msg:6f1c2d3e-0001")))));

        var status = Assert.Single(resultado.Statuses);
        Assert.Empty(resultado.Messages);
        Assert.Equal(Webhooks.Phone, status.ExternalAccountId);
        Assert.Equal("wamid.OUT", status.ProviderMessageId);
        Assert.Equal(esperado, status.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750030073), status.ProviderTimestamp);
        Assert.Equal("msg:6f1c2d3e-0001", status.OpaqueCallbackData);
        Assert.Null(status.ErrorCode);
    }

    [Fact]
    public void Status_sem_dado_opaco_devolve_nulo()
    {
        var status = Assert.Single(ParseOk(Webhooks.Envelope(Webhooks.Value(statuses: new JsonArray(Webhooks.Status("w", "sent"))))).Statuses);

        Assert.Null(status.OpaqueCallbackData);
    }

    [Fact]
    public void Status_failed_leva_so_o_codigo_do_primeiro_erro_nunca_o_texto_do_provedor()
    {
        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(statuses: new JsonArray(Webhooks.Status("w", "failed", "1751142888", "msg:1", errorCode: 131049)))));

        var status = Assert.Single(resultado.Statuses);
        Assert.Equal(ProviderDeliveryStatus.Failed, status.Status);
        Assert.Equal("131049", status.ErrorCode);
        Assert.DoesNotContain("LIVRE", status.ToString());
    }

    [Fact]
    public void O_codigo_do_erro_como_texto_de_digitos_tambem_vale_e_lixo_nao_vira_codigo()
    {
        var comoTexto = Webhooks.Status("w1", "failed", errorCode: 1);
        comoTexto["errors"]![0]!["code"] = "131047";
        var lixo = Webhooks.Status("w2", "failed", errorCode: 1);
        lixo["errors"]![0]!["code"] = "131047 OR 1=1; <x>";
        var semErros = Webhooks.Status("w3", "failed");

        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(statuses: new JsonArray(comoTexto, lixo, semErros))));

        Assert.Equal(new string?[] { "131047", null, null }, resultado.Statuses.Select(s => s.ErrorCode).ToArray());
    }

    [Fact]
    public void Status_played_ou_desconhecido_e_reconhecido_descartado_e_contado()
    {
        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(statuses: new JsonArray(
            Webhooks.Status("w1", "played"),
            Webhooks.Status("w2", "deleted"),
            Webhooks.Status("w3", "read")))));

        Assert.Equal("w3", Assert.Single(resultado.Statuses).ProviderMessageId);
        Assert.Equal(2, resultado.IgnoredEntries);
    }

    // Entrada inválida

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("não é JSON")]
    [InlineData("{")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":[")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"whatsapp_business_account\"")]
    [InlineData("{}")]
    [InlineData("{\"object\":\"instagram\",\"entry\":[]}")]
    [InlineData("{\"object\":\"page\",\"entry\":[{\"changes\":[]}]}")]
    [InlineData("{\"object\":\"whatsapp_business_account\"}")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":null}")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":{}}")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":\"x\"}")]
    [InlineData("{\"object\":42,\"entry\":[]}")]
    [InlineData("{\"entry\":[]}")]
    public void Corpo_invalido_ou_de_forma_desconhecida_devolve_nulo_e_nao_lanca(string corpo)
    {
        Assert.Null(Sends.Plain().Parse(Encoding.UTF8.GetBytes(corpo)));
    }

    [Fact]
    public void Bytes_que_nao_sao_utf8_valido_ou_vazios_devolvem_nulo()
    {
        Assert.Null(Sends.Plain().Parse(ReadOnlySpan<byte>.Empty));
        Assert.Null(Sends.Plain().Parse(new byte[] { 0xFF, 0xFE, 0xFD }));
        Assert.Null(Sends.Plain().Parse(new byte[] { (byte)'{', 0xC3, 0x28, (byte)'}' }));
    }

    [Fact]
    public void Entrada_vazia_e_um_webhook_valido_sem_itens()
    {
        var resultado = ParseOk(new JsonObject { ["object"] = "whatsapp_business_account", ["entry"] = new JsonArray() });

        Assert.Empty(resultado.Messages);
        Assert.Empty(resultado.Statuses);
        Assert.Equal(0, resultado.IgnoredEntries);
    }

    [Fact]
    public void Eventos_de_outros_campos_do_aplicativo_sao_reconhecidos_descartados_e_contados()
    {
        var webhook = new JsonObject
        {
            ["object"] = "whatsapp_business_account",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = "waba",
                ["changes"] = new JsonArray(
                    new JsonObject { ["field"] = "message_template_status_update", ["value"] = new JsonObject { ["event"] = "APPROVED" } },
                    new JsonObject { ["field"] = "account_update", ["value"] = new JsonObject { ["event"] = "X" } },
                    new JsonObject { ["field"] = "messages", ["value"] = Webhooks.Value(messages: new JsonArray(Webhooks.TextMessage("m1", "55", "oi"))) }),
            }),
        };

        var resultado = ParseOk(webhook);

        Assert.Equal("m1", Assert.Single(resultado.Messages).ProviderMessageId);
        Assert.Equal(2, resultado.IgnoredEntries);
    }

    [Fact]
    public void Valor_sem_mensagens_nem_status_e_contado_como_ignorado()
    {
        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value()));

        Assert.Empty(resultado.Messages);
        Assert.Equal(1, resultado.IgnoredEntries);
    }

    [Fact]
    public void Sem_o_numero_de_origem_nada_do_lote_e_roteavel_e_tudo_e_contado()
    {
        var valor = Webhooks.Value(
            messages: new JsonArray(Webhooks.TextMessage("m1", "55", "a"), Webhooks.TextMessage("m2", "55", "b")),
            statuses: new JsonArray(Webhooks.Status("w1", "sent")));
        valor["metadata"] = new JsonObject { ["display_phone_number"] = "551130000000" };

        var resultado = ParseOk(Webhooks.Envelope(valor));

        Assert.Empty(resultado.Messages);
        Assert.Empty(resultado.Statuses);
        Assert.Equal(3, resultado.IgnoredEntries);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("from")]
    [InlineData("timestamp")]
    [InlineData("type")]
    public void Mensagem_sem_um_campo_obrigatorio_e_descartada_e_contada_sem_perder_as_demais(string faltando)
    {
        var defeituosa = Webhooks.TextMessage("m-ruim", "55", "ruim");
        defeituosa.Remove(faltando);

        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(
            Webhooks.TextMessage("m1", "55", "antes"),
            defeituosa,
            Webhooks.TextMessage("m2", "55", "depois")))));

        Assert.Equal(new[] { "m1", "m2" }, resultado.Messages.Select(m => m.ProviderMessageId).ToArray());
        Assert.Equal(1, resultado.IgnoredEntries);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("12.5")]
    [InlineData("1e9")]
    [InlineData(" 1749416383")]
    [InlineData("99999999999999999999")]
    [InlineData("253402300800")]
    public void Timestamp_invalido_descarta_so_aquela_mensagem(string timestamp)
    {
        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(
            Webhooks.TextMessage("m-ruim", "55", "ruim", timestamp),
            Webhooks.TextMessage("m-bom", "55", "bom")))));

        Assert.Equal("m-bom", Assert.Single(resultado.Messages).ProviderMessageId);
        Assert.Equal(1, resultado.IgnoredEntries);
    }

    [Fact]
    public void Texto_sem_o_corpo_ou_com_corpo_que_nao_e_texto_e_descartado_e_contado()
    {
        var semCorpo = Webhooks.TextMessage("m1", "55", "x");
        semCorpo["text"] = new JsonObject();
        var corpoNumerico = Webhooks.TextMessage("m2", "55", "x");
        corpoNumerico["text"] = new JsonObject { ["body"] = 42 };
        var semObjeto = Webhooks.TextMessage("m3", "55", "x");
        semObjeto.Remove("text");

        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(semCorpo, corpoNumerico, semObjeto, Webhooks.TextMessage("m4", "55", "ok")))));

        Assert.Equal("m4", Assert.Single(resultado.Messages).ProviderMessageId);
        Assert.Equal(3, resultado.IgnoredEntries);
    }

    [Fact]
    public void Identificador_gigante_ou_com_controle_descarta_a_mensagem()
    {
        var resultado = ParseOk(Webhooks.Envelope(Webhooks.Value(messages: new JsonArray(
            Webhooks.TextMessage(new string('x', 513), "55", "gigante"),
            Webhooks.TextMessage("com espaco", "55", "espaço"),
            Webhooks.TextMessage("m-ok", "55", "ok")))));

        Assert.Equal("m-ok", Assert.Single(resultado.Messages).ProviderMessageId);
        Assert.Equal(2, resultado.IgnoredEntries);
    }

    [Fact]
    public void Escape_de_substituto_sem_par_descarta_so_aquele_item_e_nao_lanca()
    {
        // O texto \ud800 sozinho não é UTF-16 válido: o leitor de JSON lança ao materializá-lo. Só aquela mensagem cai; o lote segue.
        var bruto = "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"field\":\"messages\",\"value\":{\"metadata\":{\"phone_number_id\":\"1\"},\"messages\":["
            + "{\"from\":\"55\",\"id\":\"m1\",\"timestamp\":\"1749416383\",\"type\":\"text\",\"text\":{\"body\":\"ruim \\ud800\"}},"
            + "{\"from\":\"55\",\"id\":\"m2\",\"timestamp\":\"1749416383\",\"type\":\"text\",\"text\":{\"body\":\"bom\"}}]}}]}]}";

        var resultado = Assert.IsType<ParsedWebhook>(Sends.Plain().Parse(Encoding.UTF8.GetBytes(bruto)));

        Assert.Equal("m2", Assert.Single(resultado.Messages).ProviderMessageId);
        Assert.Equal(1, resultado.IgnoredEntries);
    }

    [Theory]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":[null,1,\"x\",[],{\"changes\":null},{\"changes\":{}},{\"changes\":[null,1,\"x\",[],{\"field\":\"messages\",\"value\":null},{\"field\":\"messages\",\"value\":[]},{\"field\":\"messages\",\"value\":{\"messages\":null,\"statuses\":null}},{\"field\":1,\"value\":{}}]}]}")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"field\":\"messages\",\"value\":{\"metadata\":{\"phone_number_id\":\"1\"},\"messages\":[null,1,\"x\",[],{\"id\":1,\"from\":2,\"timestamp\":{},\"type\":[]},{\"id\":\"a\",\"from\":\"b\",\"timestamp\":\"1\",\"type\":\"image\",\"image\":[]},{\"id\":\"a\",\"from\":\"b\",\"timestamp\":\"1\",\"type\":\"text\",\"text\":null}],\"statuses\":[null,1,{},{\"id\":\"s\",\"status\":5,\"timestamp\":\"1\"},{\"id\":\"s\",\"status\":\"failed\",\"timestamp\":\"1\",\"errors\":[null]},{\"id\":\"s\",\"status\":\"failed\",\"timestamp\":\"1\",\"errors\":[{\"code\":{}}]},{\"id\":\"s\",\"status\":\"failed\",\"timestamp\":\"1\",\"errors\":\"x\"}],\"contacts\":[null,1,{\"wa_id\":1},{\"wa_id\":\"b\",\"profile\":null},{\"wa_id\":\"b\",\"profile\":{\"name\":5}}]}}]}]}")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"field\":\"messages\",\"value\":{\"metadata\":null,\"messages\":[{}]}}]}]}")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"field\":\"messages\",\"value\":{\"metadata\":{\"phone_number_id\":123},\"messages\":[{}]}}]}]}")]
    public void Formas_quebradas_dentro_de_um_webhook_valido_nunca_lancam(string corpo)
    {
        var resultado = Sends.Plain().Parse(Encoding.UTF8.GetBytes(corpo));

        Assert.NotNull(resultado);
        Assert.True(resultado!.IgnoredEntries > 0);
    }
}
