using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// Fundação multi-idioma no domínio (sem banco): escolha do idioma de uma conversa nova, mudança durante a conversa,
/// preferência do contato e a tradução como registro à parte do original.
/// </summary>
public class LocaleDomainTests
{
    private static readonly Locale PtBr = Locale.Parse("pt-BR");
    private static readonly Locale EnUs = Locale.Parse("en-US");
    private static readonly Locale EsEs = Locale.Parse("es-ES");
    private static readonly Locale FrFr = Locale.Parse("fr-FR");

    // Escolha do idioma de uma conversa nova

    [Fact]
    public void A_preferencia_do_contato_vence_a_dica_do_canal_e_o_padrao_do_tenant()
    {
        var (idioma, origem) = LocaleResolution.ForNewConversation(contactPreferred: EsEs, channelHint: EnUs, channelDefault: FrFr, tenantDefault: PtBr);

        Assert.Equal((EsEs, LocaleSource.ContactPreference), (idioma, origem));
    }

    [Fact]
    public void Sem_preferencia_vale_a_dica_do_canal_mesmo_que_o_numero_e_o_tenant_usem_outro_idioma()
    {
        var (idioma, origem) = LocaleResolution.ForNewConversation(contactPreferred: null, channelHint: EnUs, channelDefault: FrFr, tenantDefault: PtBr);

        Assert.Equal((EnUs, LocaleSource.ChannelHint), (idioma, origem));
    }

    [Fact]
    public void Sem_informacao_do_cliente_o_idioma_padrao_do_numero_vence_o_do_tenant_e_e_suposicao()
    {
        var (idioma, origem) = LocaleResolution.ForNewConversation(contactPreferred: null, channelHint: null, channelDefault: EsEs, tenantDefault: PtBr);

        Assert.Equal((EsEs, LocaleSource.ChannelDefault), (idioma, origem));
        Assert.True(LocaleResolution.IsAssumed(origem));
    }

    [Fact]
    public void So_sem_nenhuma_informacao_do_cliente_nem_do_numero_o_padrao_do_tenant_entra_e_marcado_como_suposicao()
    {
        var (idioma, origem) = LocaleResolution.ForNewConversation(contactPreferred: null, channelHint: null, channelDefault: null, tenantDefault: FrFr);

        Assert.Equal((FrFr, LocaleSource.TenantDefault), (idioma, origem));
        Assert.True(LocaleResolution.IsAssumed(origem));
        Assert.All(
            new[] { LocaleSource.ChannelHint, LocaleSource.ContactPreference, LocaleSource.CustomerChoice, LocaleSource.OperatorChoice },
            fonte => Assert.False(LocaleResolution.IsAssumed(fonte)));
    }

    [Fact]
    public void O_padrao_do_tenant_e_obrigatorio_na_escolha()
    {
        Assert.Throws<ArgumentNullException>(() => LocaleResolution.ForNewConversation(null, null, null, null!));
    }

    // Idioma da conversa

    private static Conversation NovaConversa(Locale? idioma = null, LocaleSource origem = LocaleSource.TenantDefault) =>
        Conversation.Create(Fabrica.Tenant, Guid.NewGuid(), Guid.NewGuid(), ChannelInstanceId.New(), Fabrica.Agora, idioma ?? PtBr, origem);

    [Fact]
    public void A_conversa_nasce_com_o_idioma_e_a_origem_informados()
    {
        var conversa = NovaConversa(EsEs, LocaleSource.ChannelHint);

        Assert.Equal((EsEs, LocaleSource.ChannelHint), (conversa.EffectiveLocale, conversa.LocaleSource));
    }

    [Fact]
    public void Mudar_o_idioma_troca_idioma_e_origem_e_repetir_a_mesma_escolha_nao_muda_nada()
    {
        var conversa = NovaConversa();

        Assert.True(conversa.ChangeLocale(EnUs, LocaleSource.OperatorChoice));
        Assert.Equal((EnUs, LocaleSource.OperatorChoice), (conversa.EffectiveLocale, conversa.LocaleSource));
        Assert.False(conversa.ChangeLocale(EnUs, LocaleSource.OperatorChoice));
    }

    [Fact]
    public void Confirmar_o_mesmo_idioma_com_outra_origem_e_uma_mudanca_porque_troca_a_suposicao_por_escolha()
    {
        var conversa = NovaConversa(PtBr, LocaleSource.TenantDefault);

        Assert.True(conversa.ChangeLocale(PtBr, LocaleSource.CustomerChoice));

        Assert.Equal((PtBr, LocaleSource.CustomerChoice), (conversa.EffectiveLocale, conversa.LocaleSource));
        Assert.False(LocaleResolution.IsAssumed(conversa.LocaleSource));
    }

    [Fact]
    public void Idioma_ausente_e_origem_invalida_sao_recusados()
    {
        Assert.Throws<ArgumentNullException>(() => Conversation.Create(Fabrica.Tenant, Guid.NewGuid(), Guid.NewGuid(), ChannelInstanceId.New(), Fabrica.Agora, null!, LocaleSource.TenantDefault));
        Assert.Throws<ConversationsValidationException>(() => NovaConversa(PtBr, (LocaleSource)0));
        Assert.Throws<ConversationsValidationException>(() => NovaConversa(PtBr, (LocaleSource)99));
        var conversa = NovaConversa();
        Assert.Throws<ArgumentNullException>(() => conversa.ChangeLocale(null!, LocaleSource.OperatorChoice));
        Assert.Throws<ConversationsValidationException>(() => conversa.ChangeLocale(EnUs, (LocaleSource)99));
        Assert.Equal(PtBr, conversa.EffectiveLocale); // a recusa não alterou nada
    }

    [Fact]
    public void Mudar_o_idioma_avanca_a_StateVersion_da_sessao_e_so_ela()
    {
        var sessao = Fabrica.Sessao();
        var versao = sessao.StateVersion;
        var modo = sessao.HandlingMode;

        sessao.AdvanceStateVersion();

        Assert.Equal(versao + 1, sessao.StateVersion);
        Assert.Equal(modo, sessao.HandlingMode);
    }

    [Fact]
    public void A_resposta_composta_antes_da_mudanca_de_idioma_e_recusada_por_StaleState_na_cerca_de_versao()
    {
        var sessao = Fabrica.Sessao();
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        var turno = Guid.NewGuid();
        sessao.BeginTurn(turno, 1, out var grant);

        sessao.AdvanceStateVersion(); // o idioma da conversa mudou durante o turno

        Assert.Equal(
            Contracts.Commands.RegisterReplyOutcome.StaleState,
            sessao.CheckReplyFences(turno, grant!.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
    }

    // Preferência do contato

    [Fact]
    public void A_preferencia_do_contato_nasce_vazia_pode_ser_definida_e_limpa()
    {
        var contato = Contact.Create(Fabrica.Tenant, ChannelInstanceId.New(), "5511999990001", "Mauro", Fabrica.Agora);
        Assert.Null(contato.PreferredLocale);

        Assert.True(contato.SetPreferredLocale(EsEs));
        Assert.False(contato.SetPreferredLocale(EsEs));
        Assert.Equal(EsEs, contato.PreferredLocale);
        Assert.True(contato.SetPreferredLocale(null));
        Assert.Null(contato.PreferredLocale);
        Assert.False(contato.SetPreferredLocale(null));
    }

    // Tradução: registro à parte do original

    private static Message MensagemOriginal(string texto = "¿Necesito ayuno para este examen?", Locale? idioma = null)
    {
        var sessao = Fabrica.Sessao();
        var registro = sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        return Message.CreateInbound(Fabrica.Tenant, sessao.Id, Guid.NewGuid(), ChannelInstanceId.New(), registro, "wamid.t", Fabrica.Agora, MessageKind.Text, texto, Fabrica.Agora, idioma);
    }

    [Fact]
    public void A_traducao_e_um_registro_separado_e_o_original_continua_intacto()
    {
        var original = MensagemOriginal(idioma: EsEs);
        var antes = (original.Text, original.Locale, original.LocaleOrigin, original.ContentState);

        var traducao = MessageTranslation.Create(Fabrica.Tenant, original, PtBr, "Preciso de jejum para este exame?", "human", Fabrica.Agora);

        Assert.Equal(original.Id, traducao.MessageId);
        Assert.Equal((PtBr, EsEs, "Preciso de jejum para este exame?", "human"), (traducao.TargetLocale, traducao.SourceLocale, traducao.Text, traducao.Provenance));
        Assert.NotEqual(original.Id, traducao.Id);
        Assert.Equal(antes, (original.Text, original.Locale, original.LocaleOrigin, original.ContentState));
        Assert.Equal("¿Necesito ayuno para este examen?", original.Text);
    }

    [Fact]
    public void O_idioma_de_origem_da_traducao_e_desconhecido_quando_o_da_mensagem_e()
    {
        var traducao = MessageTranslation.Create(Fabrica.Tenant, MensagemOriginal(idioma: null), PtBr, "texto", "machine.provider-x", Fabrica.Agora);

        Assert.Null(traducao.SourceLocale);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Traducao_sem_texto_e_recusada(string texto)
    {
        Assert.Throws<ConversationsValidationException>(() => MessageTranslation.Create(Fabrica.Tenant, MensagemOriginal(), PtBr, texto, "human", Fabrica.Agora));
    }

    [Fact]
    public void Traducao_acima_do_limite_e_recusada_e_no_limite_e_aceita_exatamente_como_veio()
    {
        var noLimite = " " + new string('á', ConversationLimits.TextMaxLength - 2) + "\n";

        Assert.Throws<ConversationsValidationException>(() => MessageTranslation.Create(Fabrica.Tenant, MensagemOriginal(), PtBr, new string('x', ConversationLimits.TextMaxLength + 1), "human", Fabrica.Agora));
        Assert.Equal(noLimite, MessageTranslation.Create(Fabrica.Tenant, MensagemOriginal(), PtBr, noLimite, "human", Fabrica.Agora).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Humano")]
    [InlineData("tradutor humano")]
    [InlineData("-comeca-com-hifen")]
    public void A_origem_da_traducao_e_um_codigo_curto_sem_texto_livre(string origem)
    {
        Assert.Throws<ConversationsValidationException>(() => MessageTranslation.Create(Fabrica.Tenant, MensagemOriginal(), PtBr, "texto", origem, Fabrica.Agora));
    }

    [Fact]
    public void Traducao_exige_tenant_mensagem_e_idioma()
    {
        Assert.Throws<ArgumentNullException>(() => MessageTranslation.Create(null!, MensagemOriginal(), PtBr, "t", "human", Fabrica.Agora));
        Assert.Throws<ArgumentNullException>(() => MessageTranslation.Create(Fabrica.Tenant, null!, PtBr, "t", "human", Fabrica.Agora));
        Assert.Throws<ArgumentNullException>(() => MessageTranslation.Create(Fabrica.Tenant, MensagemOriginal(), null!, "t", "human", Fabrica.Agora));
    }
}
