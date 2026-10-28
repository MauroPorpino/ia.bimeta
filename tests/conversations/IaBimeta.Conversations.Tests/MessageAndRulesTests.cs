using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

public class MessageTests
{
    private static Message Entrada(MessageKind tipo = MessageKind.Text, string? texto = "Oi", string evento = "wamid.1", DateTimeOffset? quando = null)
    {
        var sessao = Fabrica.Sessao();
        var registro = sessao.RegisterInbound(quando ?? Fabrica.Agora, Fabrica.Agora);
        return Message.CreateInbound(Fabrica.Tenant, sessao.Id, Guid.NewGuid(), ChannelInstanceId.New(), registro, evento, quando ?? Fabrica.Agora, tipo, texto, Fabrica.Agora);
    }

    [Fact]
    public void Texto_do_cliente_nasce_pronto_com_as_sequencias_e_a_chave_de_idempotencia()
    {
        var mensagem = Entrada(texto: "Preciso ficar em jejum?");

        Assert.Equal(MessageDirection.Inbound, mensagem.Direction);
        Assert.Equal(MessageAuthor.Customer, mensagem.Author);
        Assert.Equal(ContentState.Ready, mensagem.ContentState);
        Assert.Equal("Preciso ficar em jejum?", mensagem.Text);
        Assert.Equal(1, mensagem.InboundSeq);
        Assert.Equal(1, mensagem.MessageSeq);
        Assert.Equal("wamid.1", mensagem.ProviderEventId);
        Assert.Null(mensagem.DeliveryState);
    }

    [Theory]
    [InlineData("  Preciso ficar em jejum?  ")] // espaços nas pontas
    [InlineData("linha 1\nlinha 2\r\n\tlinha 3 recuada\n")] // quebras e tabulação, inclusive a final
    [InlineData("Olá, não sei se preciso de jejum — é 8h? 😀👍🏽")] // acentos, travessão e emoji (pares substitutos)
    [InlineData("مرحبا، هل أحتاج إلى الصيام؟")] // escrita da direita para a esquerda
    [InlineData("我需要禁食吗？ Нужно ли голодать? Χρειάζεται νηστεία;")] // escritas diferentes na mesma mensagem
    [InlineData("café e café")] // NFC e NFD: nenhuma normalização Unicode
    [InlineData("com​zero-width e  nbsp")] // caracteres invisíveis
    public void O_texto_original_do_cliente_e_guardado_exatamente_como_veio(string texto)
    {
        var mensagem = Entrada(texto: texto);

        Assert.Equal(texto, mensagem.Text);
        Assert.Equal(texto.Length, mensagem.Text!.Length);
    }

    [Fact]
    public void O_idioma_da_mensagem_e_desconhecido_a_menos_que_o_provedor_o_declare_e_nunca_e_presumido()
    {
        var sessao = Fabrica.Sessao();
        var registro = sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        var semDeclaracao = Message.CreateInbound(Fabrica.Tenant, sessao.Id, Guid.NewGuid(), ChannelInstanceId.New(), registro, "wamid.a", Fabrica.Agora, MessageKind.Text, "Hola", Fabrica.Agora);
        var declarada = Message.CreateInbound(Fabrica.Tenant, sessao.Id, Guid.NewGuid(), ChannelInstanceId.New(), registro, "wamid.b", Fabrica.Agora, MessageKind.Text, "Hola", Fabrica.Agora, Locale.Parse("es-es"));

        Assert.Equal((null, null), (semDeclaracao.Locale, semDeclaracao.LocaleOrigin));
        Assert.Equal((Locale.Parse("es-ES"), (MessageLocaleOrigin?)MessageLocaleOrigin.ProviderDeclared), (declarada.Locale, declarada.LocaleOrigin));
        Assert.Equal(semDeclaracao.Text, declarada.Text); // o idioma é metadado: o texto é o mesmo
    }

    [Theory]
    [InlineData(MessageKind.Audio)]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Document)]
    [InlineData(MessageKind.Video)]
    [InlineData(MessageKind.Other)]
    public void Midia_do_cliente_nasce_pendente_e_sem_texto_ate_alguem_processar(MessageKind tipo)
    {
        var mensagem = Entrada(tipo, texto: "legenda que nao entra");

        Assert.Equal(ContentState.Pending, mensagem.ContentState);
        Assert.Null(mensagem.Text);
    }

    [Fact]
    public void A_posicao_cronologica_do_cliente_e_o_momento_do_provedor()
    {
        var quando = Fabrica.Agora.AddHours(-3);

        var mensagem = Entrada(quando: quando);

        Assert.Equal(quando, mensagem.OccurredAt);
        Assert.Equal(quando, mensagem.ProviderTimestamp);
        Assert.Equal(Fabrica.Agora, mensagem.ReceivedAt);
    }

    [Fact]
    public void Dados_invalidos_da_mensagem_do_cliente_sao_recusados()
    {
        Assert.Throws<ConversationsValidationException>(() => Entrada(texto: null));
        Assert.Throws<ConversationsValidationException>(() => Entrada(texto: "   "));
        Assert.Throws<ConversationsValidationException>(() => Entrada(texto: new string('x', ConversationLimits.TextMaxLength + 1)));
        Assert.Throws<ConversationsValidationException>(() => Entrada(evento: ""));
        Assert.Throws<ConversationsValidationException>(() => Entrada(evento: new string('e', ConversationLimits.ProviderEventIdMaxLength + 1)));
        Assert.Throws<ConversationsValidationException>(() => Entrada(tipo: (MessageKind)99));
        Assert.Throws<ConversationsValidationException>(() => Entrada(quando: default(DateTimeOffset)));
    }

    [Fact]
    public void Texto_no_limite_e_aceito()
    {
        Assert.Equal(ConversationLimits.TextMaxLength, Entrada(texto: new string('x', ConversationLimits.TextMaxLength)).Text!.Length);
    }

    // Estado do conteúdo (MED-5)

    [Fact]
    public void Conteudo_pendente_vira_pronto_com_o_texto_extraido_e_a_repeticao_e_inocua()
    {
        var mensagem = Entrada(MessageKind.Audio, null);

        Assert.Equal(ContentChangeOutcome.Changed, mensagem.SetContent("  transcrição  "));
        Assert.Equal(ContentState.Ready, mensagem.ContentState);
        Assert.Equal("transcrição", mensagem.Text);
        Assert.Equal(ContentChangeOutcome.Unchanged, mensagem.SetContent("outra coisa"));
        Assert.Equal("transcrição", mensagem.Text);
    }

    [Fact]
    public void Conteudo_pendente_pode_falhar_e_o_estado_falho_e_final_inclusive_para_conteudo_pronto_tarde()
    {
        var mensagem = Entrada(MessageKind.Audio, null);

        Assert.Equal(ContentChangeOutcome.Changed, mensagem.FailContent());
        Assert.Equal(ContentChangeOutcome.Unchanged, mensagem.FailContent());

        // MED-5: conteúdo que fica pronto depois de a política ter sido aplicada não reabre nada.
        Assert.Equal(ContentChangeOutcome.NotAllowed, mensagem.SetContent("tarde demais"));
        Assert.Equal(ContentState.Failed, mensagem.ContentState);
        Assert.Null(mensagem.Text);
    }

    [Fact]
    public void Conteudo_ja_pronto_nao_pode_falhar()
    {
        var mensagem = Entrada();

        Assert.Equal(ContentChangeOutcome.NotAllowed, mensagem.FailContent());
        Assert.Equal(ContentState.Ready, mensagem.ContentState);
    }

    [Fact]
    public void Texto_extraido_invalido_e_recusado_e_o_conteudo_continua_pendente()
    {
        var mensagem = Entrada(MessageKind.Image, null);

        Assert.Throws<ConversationsValidationException>(() => mensagem.SetContent("  "));
        Assert.Throws<ConversationsValidationException>(() => mensagem.SetContent(new string('x', ConversationLimits.TextMaxLength + 1)));
        Assert.Equal(ContentState.Pending, mensagem.ContentState);
    }

    // Saída

    private static Message Resposta(ServiceSession? sessao = null)
    {
        sessao ??= Fabrica.Sessao();
        return Message.CreateAiReply(
            Fabrica.Tenant, sessao.Id, Guid.NewGuid(), ChannelInstanceId.New(), 2, Guid.NewGuid(), ReplyKind.Generated, 1, "Sim, 8 horas.",
            new[] { Guid.NewGuid() }, Fabrica.Agora, Locale.Parse("pt-BR"));
    }

    [Fact]
    public void Resposta_da_IA_nasce_liberada_para_envio_com_a_cobertura_e_o_turno()
    {
        var resposta = Resposta();

        Assert.Equal(MessageDirection.Outbound, resposta.Direction);
        Assert.Equal(MessageAuthor.Ai, resposta.Author);
        Assert.Equal(DeliveryState.Requested, resposta.DeliveryState);
        Assert.Equal("Sim, 8 horas.", resposta.Text);
        Assert.Equal(1, resposta.CoveredThroughSeq);
        Assert.NotNull(resposta.TurnId);
        Assert.Null(resposta.InboundSeq);
        Assert.Null(resposta.ProviderEventId);
    }

    [Fact]
    public void Resposta_da_IA_exige_texto()
    {
        Assert.Throws<ConversationsValidationException>(() => Message.CreateAiReply(
            Fabrica.Tenant, Guid.NewGuid(), Guid.NewGuid(), ChannelInstanceId.New(), 2, Guid.NewGuid(), ReplyKind.Informative, 1, " ", Array.Empty<Guid>(), Fabrica.Agora, Locale.Parse("pt-BR")));
    }

    [Fact]
    public void A_resposta_da_IA_guarda_o_texto_como_veio_e_o_idioma_pretendido_da_conversa()
    {
        var resposta = Message.CreateAiReply(
            Fabrica.Tenant, Guid.NewGuid(), Guid.NewGuid(), ChannelInstanceId.New(), 2, Guid.NewGuid(), ReplyKind.Generated, 1, "  Sí, ocho horas.\n",
            new[] { Guid.NewGuid() }, Fabrica.Agora, Locale.Parse("es-ES"));

        Assert.Equal("  Sí, ocho horas.\n", resposta.Text);
        Assert.Equal((Locale.Parse("es-ES"), (MessageLocaleOrigin?)MessageLocaleOrigin.ConversationLocale), (resposta.Locale, resposta.LocaleOrigin));
    }

    [Fact]
    public void Reenvio_copia_conteudo_e_destino_guarda_o_vinculo_e_o_autor_e_nao_e_de_turno_de_IA()
    {
        var original = Resposta();
        original.TryApplyDelivery(DeliveryState.Failed, "window-closed");
        var autor = Guid.NewGuid();
        var pedido = Guid.NewGuid();

        var reenvio = Message.CreateResend(original, 5, pedido, autor, Fabrica.Agora.AddMinutes(1));

        Assert.Equal(original.Id, reenvio.ResendOfMessageId);
        Assert.Equal(pedido, reenvio.ResendRequestId);
        Assert.Equal(autor, reenvio.RequestedBy);
        Assert.Equal(MessageAuthor.Human, reenvio.Author);
        Assert.Equal(original.Text, reenvio.Text);
        Assert.Equal(original.ConversationId, reenvio.ConversationId);
        Assert.Equal(original.ChannelInstanceId, reenvio.ChannelInstanceId);
        Assert.Equal(DeliveryState.Requested, reenvio.DeliveryState);
        Assert.Null(reenvio.TurnId);
        Assert.Equal((original.Locale, original.LocaleOrigin), (reenvio.Locale, reenvio.LocaleOrigin)); // o reenvio não muda o idioma da mensagem
    }

    // Entrega

    [Fact]
    public void Status_de_entrega_so_vale_para_mensagem_de_saida()
    {
        var entrada = Entrada();

        Assert.False(entrada.TryApplyDelivery(DeliveryState.Sent, null));
        Assert.Null(entrada.DeliveryState);
    }

    [Fact]
    public void Status_aplicado_avanca_e_falha_definitiva_guarda_o_motivo()
    {
        var resposta = Resposta();

        Assert.True(resposta.TryApplyDelivery(DeliveryState.Queued, null));
        Assert.True(resposta.TryApplyDelivery(DeliveryState.Sending, null));
        Assert.True(resposta.TryApplyDelivery(DeliveryState.Failed, "window-closed"));

        Assert.Equal(DeliveryState.Failed, resposta.DeliveryState);
        Assert.Equal("window-closed", resposta.DeliveryFailureReason);
        Assert.False(resposta.TryApplyDelivery(DeliveryState.Sent, null)); // falha definitiva é terminal
    }
}

public class DeliveryStateRulesTests
{
    public static TheoryData<DeliveryState, DeliveryState, bool> Matriz => new()
    {
        // O caminho feliz só avança.
        { DeliveryState.Requested, DeliveryState.Queued, true },
        { DeliveryState.Queued, DeliveryState.Sending, true },
        { DeliveryState.Sending, DeliveryState.Sent, true },
        { DeliveryState.Sent, DeliveryState.Delivered, true },
        { DeliveryState.Delivered, DeliveryState.Read, true },
        { DeliveryState.Requested, DeliveryState.Sent, true },     // pode pular etapas
        { DeliveryState.Sent, DeliveryState.Read, true },
        // Nunca regride, nem repete.
        { DeliveryState.Sent, DeliveryState.Sending, false },
        { DeliveryState.Delivered, DeliveryState.Sent, false },
        { DeliveryState.Read, DeliveryState.Delivered, false },
        { DeliveryState.Sent, DeliveryState.Sent, false },
        { DeliveryState.Queued, DeliveryState.Requested, false },
        // Falha definitiva: só enquanto a mensagem ainda está em andamento.
        { DeliveryState.Requested, DeliveryState.Failed, true },
        { DeliveryState.Queued, DeliveryState.Expired, true },
        { DeliveryState.Sending, DeliveryState.Dead, true },
        { DeliveryState.SendUnknown, DeliveryState.Failed, true },
        { DeliveryState.Sent, DeliveryState.Failed, false },
        { DeliveryState.Delivered, DeliveryState.Failed, false },
        { DeliveryState.Read, DeliveryState.Dead, false },
        // Falha definitiva é terminal.
        { DeliveryState.Failed, DeliveryState.Sent, false },
        { DeliveryState.Expired, DeliveryState.Queued, false },
        { DeliveryState.Dead, DeliveryState.Failed, false },
        { DeliveryState.Failed, DeliveryState.Failed, false },
        // SendUnknown é um desvio de Sending e só sai por confirmação.
        { DeliveryState.Sending, DeliveryState.SendUnknown, true },
        { DeliveryState.Queued, DeliveryState.SendUnknown, true },
        { DeliveryState.Requested, DeliveryState.SendUnknown, false },
        { DeliveryState.Sent, DeliveryState.SendUnknown, false },
        { DeliveryState.SendUnknown, DeliveryState.Sent, true },
        { DeliveryState.SendUnknown, DeliveryState.Delivered, true },
        { DeliveryState.SendUnknown, DeliveryState.Sending, false },
        { DeliveryState.SendUnknown, DeliveryState.SendUnknown, false },
    };

    [Theory]
    [MemberData(nameof(Matriz))]
    public void A_maquina_de_entrega_so_avanca(DeliveryState de, DeliveryState para, bool permitido)
    {
        Assert.Equal(permitido, DeliveryStateRules.CanAdvance(de, para));
    }

    [Fact]
    public void Falhas_definitivas_sao_Failed_Expired_e_Dead_e_SendUnknown_nao_e_uma_delas()
    {
        Assert.All(new[] { DeliveryState.Failed, DeliveryState.Expired, DeliveryState.Dead }, e => Assert.True(DeliveryStateRules.IsDefinitiveFailure(e)));
        Assert.All(
            new[] { DeliveryState.Requested, DeliveryState.Queued, DeliveryState.Sending, DeliveryState.Sent, DeliveryState.Delivered, DeliveryState.Read, DeliveryState.SendUnknown },
            e => Assert.False(DeliveryStateRules.IsDefinitiveFailure(e)));
    }

    [Fact]
    public void Em_andamento_sao_Requested_Queued_Sending_e_SendUnknown()
    {
        Assert.All(
            new[] { DeliveryState.Requested, DeliveryState.Queued, DeliveryState.Sending, DeliveryState.SendUnknown },
            e => Assert.True(DeliveryStateRules.IsInFlight(e)));
        Assert.All(
            new[] { DeliveryState.Sent, DeliveryState.Delivered, DeliveryState.Read, DeliveryState.Failed, DeliveryState.Expired, DeliveryState.Dead },
            e => Assert.False(DeliveryStateRules.IsInFlight(e)));
    }
}

public class ConversationSupportTests
{
    [Fact]
    public void Cursor_de_sessao_ida_e_volta()
    {
        var original = new SessionCursor(new DateTimeOffset(2026, 10, 27, 12, 30, 45, TimeSpan.Zero).AddTicks(1234), Guid.NewGuid());

        var lido = CursorCodec.DecodeSession(CursorCodec.Encode(original));

        Assert.Equal(original, lido);
    }

    [Fact]
    public void Cursor_de_mensagem_ida_e_volta()
    {
        var original = new MessageCursor(new DateTimeOffset(2026, 10, 27, 12, 30, 45, TimeSpan.Zero).AddTicks(99), 42);

        Assert.Equal(original, CursorCodec.DecodeMessage(CursorCodec.Encode(original)));
    }

    [Fact]
    public void Sem_cursor_a_pagina_comeca_do_inicio()
    {
        Assert.Null(CursorCodec.DecodeSession(null));
        Assert.Null(CursorCodec.DecodeSession(string.Empty));
        Assert.Null(CursorCodec.DecodeMessage(null));
    }

    [Theory]
    [InlineData("lixo")]
    [InlineData("%%%")]
    [InlineData("bTEuMTIz")]
    [InlineData("Zm9v")]
    public void Cursor_invalido_e_recusado_com_erro_de_validacao_e_nunca_ignorado(string cursor)
    {
        Assert.Throws<ConversationsValidationException>(() => CursorCodec.DecodeSession(cursor));
        Assert.Throws<ConversationsValidationException>(() => CursorCodec.DecodeMessage(cursor));
    }

    [Fact]
    public void Cursor_de_um_tipo_nao_serve_para_o_outro_e_adulterado_e_recusado()
    {
        var deMensagem = CursorCodec.Encode(new MessageCursor(Fabrica.Agora, 5));
        var deSessao = CursorCodec.Encode(new SessionCursor(Fabrica.Agora, Guid.NewGuid()));

        Assert.Throws<ConversationsValidationException>(() => CursorCodec.DecodeSession(deMensagem));
        Assert.Throws<ConversationsValidationException>(() => CursorCodec.DecodeMessage(deSessao));
        Assert.Throws<ConversationsValidationException>(() => CursorCodec.DecodeMessage(deMensagem[..^2]));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("chatty-burst")]
    [InlineData("ai.failure_1")]
    public void Motivos_curtos_sao_aceitos(string motivo)
    {
        Assert.Equal(motivo, ConversationLimits.ValidateReason(motivo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Com Espaco")]
    [InlineData("MAIUSCULA")]
    [InlineData("senha: 123")]
    [InlineData("-comeca-com-hifen")]
    public void Motivo_com_texto_livre_e_recusado(string? motivo)
    {
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateReason(motivo));
    }

    [Fact]
    public void Motivo_acima_do_limite_e_recusado()
    {
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateReason(new string('a', ConversationLimits.ReasonMaxLength + 1)));
        ConversationLimits.ValidateReason(new string('a', ConversationLimits.ReasonMaxLength));
    }
}
