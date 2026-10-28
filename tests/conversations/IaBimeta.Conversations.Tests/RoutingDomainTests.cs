using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// Instância de canal, roteamento por unidade e setor e encaminhamento entre unidades no domínio (sem banco).
/// </summary>
public class RoutingDomainTests
{
    private static ServiceSession Sessao(Guid? unidade = null, Guid? setor = null, ChannelInstanceId? canal = null) =>
        ServiceSession.Create(Fabrica.Tenant, Guid.NewGuid(), unidade, Guid.NewGuid(), canal ?? ChannelInstanceId.New(), Fabrica.Agora, setor);

    // Instância de canal

    [Fact]
    public void Contato_sessao_e_conversa_guardam_a_instancia_de_canal_e_o_valor_padrao_e_recusado()
    {
        var canal = ChannelInstanceId.New();

        var contato = Contact.Create(Fabrica.Tenant, canal, "5511999990001", null, Fabrica.Agora);
        var sessao = Sessao(canal: canal);
        var conversa = Conversation.Create(Fabrica.Tenant, sessao.Id, contato.Id, canal, Fabrica.Agora, Locale.Parse("pt-BR"), LocaleSource.TenantDefault);

        Assert.Equal((canal, canal, canal), (contato.ChannelInstanceId, sessao.ChannelInstanceId, conversa.ChannelInstanceId));
        Assert.Throws<ConversationsValidationException>(() => Contact.Create(Fabrica.Tenant, default, "5511999990001", null, Fabrica.Agora));
        Assert.Throws<ConversationsValidationException>(() => ServiceSession.Create(Fabrica.Tenant, Guid.NewGuid(), null, Guid.NewGuid(), default, Fabrica.Agora));
    }

    [Fact]
    public void A_mensagem_guarda_a_instancia_de_canal_da_conversa()
    {
        var canal = ChannelInstanceId.New();
        var sessao = Sessao(canal: canal);
        var registro = sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);

        var entrada = Message.CreateInbound(Fabrica.Tenant, sessao.Id, Guid.NewGuid(), canal, registro, "wamid.1", Fabrica.Agora, MessageKind.Text, "Oi", Fabrica.Agora);
        var resposta = Message.CreateAiReply(Fabrica.Tenant, sessao.Id, entrada.ConversationId, canal, 2, Guid.NewGuid(), ReplyKind.Generated, 1, "Olá", new[] { Guid.NewGuid() }, Fabrica.Agora, Locale.Parse("pt-BR"));

        Assert.Equal((canal, canal), (entrada.ChannelInstanceId, resposta.ChannelInstanceId));
    }

    // Roteamento da sessão

    [Fact]
    public void A_sessao_nasce_com_a_unidade_e_o_setor_informados()
    {
        var unidade = Guid.NewGuid();
        var setor = Guid.NewGuid();

        var sessao = Sessao(unidade, setor);

        Assert.Equal((unidade, setor), (sessao.BusinessUnitId, sessao.DepartmentId));
        Assert.Equal((null, null), (Sessao().BusinessUnitId, Sessao().DepartmentId));
    }

    [Fact]
    public void Setor_sem_unidade_e_recusado_porque_o_setor_so_existe_dentro_de_uma_unidade()
    {
        Assert.Throws<ConversationsValidationException>(() => Sessao(unidade: null, setor: Guid.NewGuid()));
    }

    // Encaminhamento

    [Fact]
    public void Encaminhar_troca_unidade_e_setor_e_avanca_a_StateVersion()
    {
        var sessao = Sessao(Guid.NewGuid(), Guid.NewGuid());
        var destino = Guid.NewGuid();
        var setorDestino = Guid.NewGuid();
        var versao = sessao.StateVersion;

        var mudou = sessao.TransferTo(destino, setorDestino);

        Assert.True(mudou);
        Assert.Equal((destino, setorDestino, versao + 1), (sessao.BusinessUnitId, sessao.DepartmentId, sessao.StateVersion));
    }

    [Fact]
    public void Encaminhar_para_a_mesma_unidade_e_o_mesmo_setor_nao_muda_nada()
    {
        var unidade = Guid.NewGuid();
        var setor = Guid.NewGuid();
        var sessao = Sessao(unidade, setor);
        var versao = sessao.StateVersion;

        Assert.False(sessao.TransferTo(unidade, setor));

        Assert.Equal(versao, sessao.StateVersion);
    }

    [Fact]
    public void Mudar_so_o_setor_dentro_da_mesma_unidade_e_encaminhamento_e_tirar_o_setor_tambem()
    {
        var unidade = Guid.NewGuid();
        var sessao = Sessao(unidade, Guid.NewGuid());

        Assert.True(sessao.TransferTo(unidade, Guid.NewGuid()));
        Assert.True(sessao.TransferTo(unidade, null));

        Assert.Equal((unidade, (Guid?)null, 3L), (sessao.BusinessUnitId, sessao.DepartmentId, sessao.StateVersion));
    }

    [Fact]
    public void Sessao_sem_unidade_pode_ser_encaminhada_pela_primeira_vez()
    {
        var sessao = Sessao();
        var unidade = Guid.NewGuid();

        Assert.True(sessao.TransferTo(unidade, null));

        Assert.Equal(unidade, sessao.BusinessUnitId);
    }

    [Fact]
    public void Sessao_encerrada_nao_e_encaminhada_e_identificadores_vazios_sao_recusados()
    {
        var sessao = Sessao(Guid.NewGuid());

        Assert.Throws<ConversationsValidationException>(() => sessao.TransferTo(Guid.Empty, null));
        Assert.Throws<ConversationsValidationException>(() => sessao.TransferTo(Guid.NewGuid(), Guid.Empty));
        sessao.Close(Fabrica.Agora);
        Assert.Throws<ConversationsValidationException>(() => sessao.TransferTo(Guid.NewGuid(), null));
    }

    [Fact]
    public void O_encaminhamento_recusa_a_resposta_composta_antes_dele_por_StaleState()
    {
        var sessao = Sessao(Guid.NewGuid());
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        var turno = Guid.NewGuid();
        sessao.BeginTurn(turno, 1, out var grant);

        sessao.TransferTo(Guid.NewGuid(), null);

        Assert.Equal(
            Contracts.Commands.RegisterReplyOutcome.StaleState,
            sessao.CheckReplyFences(turno, grant!.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
    }

    // Rastro

    [Fact]
    public void O_rastro_guarda_origem_destino_autor_motivo_e_versao()
    {
        var sessaoId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var (de, deSetor, para, paraSetor, autor) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var rastro = SessionTransfer.Create(Fabrica.Tenant, sessaoId, transferId, de, deSetor, para, paraSetor, autor, "specialty-unit", 2, Fabrica.Agora);

        Assert.Equal((sessaoId, transferId, de, deSetor, para, paraSetor, autor, "specialty-unit", 2L, Fabrica.Agora),
            (rastro.SessionId, rastro.TransferId, rastro.FromBusinessUnitId, rastro.FromDepartmentId, rastro.ToBusinessUnitId, rastro.ToDepartmentId, rastro.RequestedBy, rastro.Reason, rastro.StateVersionAfter, rastro.TransferredAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Texto livre")]
    [InlineData("-comeca-com-hifen")]
    public void O_motivo_do_rastro_e_um_codigo_curto_sem_texto_livre(string motivo)
    {
        Assert.Throws<ConversationsValidationException>(() =>
            SessionTransfer.Create(Fabrica.Tenant, Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid(), null, null, motivo, 2, Fabrica.Agora));
    }

    [Fact]
    public void O_rastro_de_sistema_nao_tem_autor()
    {
        var rastro = SessionTransfer.Create(Fabrica.Tenant, Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid(), null, null, "handoff", 2, Fabrica.Agora);

        Assert.Null(rastro.RequestedBy);
    }
}
