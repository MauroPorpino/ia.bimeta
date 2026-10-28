using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

internal static class Fabrica
{
    public static readonly TenantId Tenant = new(Guid.NewGuid());
    public static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public static ServiceSession Sessao(TenantId? tenant = null) =>
        ServiceSession.Create(tenant ?? Tenant, Guid.NewGuid(), null, Guid.NewGuid(), ChannelInstanceId.New(), Agora);
}

/// <summary>
/// As cercas do ADR 0007 como métodos puros da sessão: turno logicamente ativo, época, token de lane, <c>StateVersion</c>,
/// cerca de entrada e cobertura. Sem banco: a serialização por linha e os índices únicos são provados com PostgreSQL real.
/// </summary>
public class ServiceSessionTests
{
    // Criação e entrada

    [Fact]
    public void Sessao_nova_comeca_aberta_com_a_IA_no_comando_e_contadores_zerados()
    {
        var sessao = Fabrica.Sessao();

        Assert.Equal(SessionStatus.Open, sessao.Status);
        Assert.Equal(HandlingMode.AiAutonomous, sessao.HandlingMode);
        Assert.True(sessao.IsAiResponder);
        Assert.Null(sessao.ResponderRef);
        Assert.Equal(1, sessao.StateVersion);
        Assert.Equal(0, sessao.LastMessageSeq);
        Assert.Equal(0, sessao.LastInboundSeq);
        Assert.Equal(0, sessao.AnsweredThroughSeq);
        Assert.Null(sessao.ActiveTurnId);
        Assert.Equal(0, sessao.TurnEpoch);
        Assert.False(sessao.ReplyDeliveryFailed);
    }

    [Fact]
    public void Sessao_exige_organizacao_contato_e_canal()
    {
        Assert.Throws<ConversationsValidationException>(() => ServiceSession.Create(Fabrica.Tenant, Guid.Empty, null, Guid.NewGuid(), ChannelInstanceId.New(), Fabrica.Agora));
        Assert.Throws<ConversationsValidationException>(() => ServiceSession.Create(Fabrica.Tenant, Guid.NewGuid(), null, Guid.Empty, ChannelInstanceId.New(), Fabrica.Agora));
        Assert.Throws<ConversationsValidationException>(() => ServiceSession.Create(Fabrica.Tenant, Guid.NewGuid(), null, Guid.NewGuid(), default, Fabrica.Agora));
    }

    [Fact]
    public void Cada_mensagem_do_cliente_avanca_as_duas_sequencias_em_ordem_total()
    {
        var sessao = Fabrica.Sessao();

        var primeira = sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        var segunda = sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        var saida = sessao.NextOutboundSeq(Fabrica.Agora);
        var terceira = sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);

        Assert.Equal((1L, 1L), (primeira.MessageSeq, primeira.InboundSeq));
        Assert.Equal((2L, 2L), (segunda.MessageSeq, segunda.InboundSeq));
        Assert.Equal(3, saida);
        Assert.Equal((4L, 3L), (terceira.MessageSeq, terceira.InboundSeq));
        Assert.Equal(3, sessao.LastInboundSeq);
        Assert.Equal(4, sessao.LastMessageSeq);
    }

    [Fact]
    public void Mensagem_de_antes_de_uma_resposta_ja_aceita_chega_marcada_como_tardia_mas_entra_como_pendente()
    {
        var sessao = Fabrica.Sessao();
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        Begin(sessao, Guid.NewGuid(), out var grant);
        sessao.AcceptAiReply(grant!.BasedOnInboundSeq, Fabrica.Agora.AddMinutes(10));

        var tardia = sessao.RegisterInbound(Fabrica.Agora.AddMinutes(5), Fabrica.Agora.AddMinutes(11));
        var normal = sessao.RegisterInbound(Fabrica.Agora.AddMinutes(12), Fabrica.Agora.AddMinutes(12));

        Assert.True(tardia.LateArrival);
        Assert.False(normal.LateArrival);
        Assert.Equal(1, sessao.AnsweredThroughSeq);
        Assert.Equal(3, sessao.LastInboundSeq); // a tardia é pendente (acima da cobertura), como qualquer outra
    }

    [Fact]
    public void Sessao_encerrada_nao_recebe_mensagem()
    {
        var sessao = Fabrica.Sessao();
        sessao.Close(Fabrica.Agora);

        Assert.Throws<ConversationsValidationException>(() => sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora));
    }

    // BeginTurn

    private static BeginTurnOutcome Begin(ServiceSession sessao, Guid turno, out TurnGrant? grant, long lease = 1) =>
        sessao.BeginTurn(turno, lease, out grant);

    [Fact]
    public void BeginTurn_assume_o_turno_avanca_a_epoca_e_devolve_a_cerca_de_entrada_e_a_versao()
    {
        var sessao = Fabrica.Sessao();
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        var turno = Guid.NewGuid();

        var resultado = Begin(sessao, turno, out var grant, lease: 7);

        Assert.Equal(BeginTurnOutcome.Granted, resultado);
        Assert.Equal(turno, sessao.ActiveTurnId);
        Assert.Equal(1, grant!.Epoch);
        Assert.Equal(2, grant.BasedOnInboundSeq);
        Assert.Equal(1, grant.StateVersion);
        Assert.Equal(7, sessao.LastLeaseToken);
    }

    [Fact]
    public void Um_novo_BeginTurn_substitui_o_turno_ativo_no_mesmo_instante_e_a_epoca_cresce()
    {
        var sessao = Fabrica.Sessao();
        var primeiro = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        Begin(sessao, primeiro, out var grantDoPrimeiro, lease: 1);

        Begin(sessao, segundo, out var grantDoSegundo, lease: 2);

        Assert.Equal(segundo, sessao.ActiveTurnId);
        Assert.Equal(1, grantDoPrimeiro!.Epoch);
        Assert.Equal(2, grantDoSegundo!.Epoch);
        // O primeiro deixou de ser ativo: a resposta dele é recusada.
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, sessao.CheckReplyFences(primeiro, grantDoPrimeiro.Epoch, 1, 0));
    }

    [Fact]
    public void BeginTurn_recusa_sessao_encerrada_responsavel_humano_e_token_de_lane_antigo()
    {
        var encerrada = Fabrica.Sessao();
        encerrada.Close(Fabrica.Agora);
        Assert.Equal(BeginTurnOutcome.SessionClosed, Begin(encerrada, Guid.NewGuid(), out _));

        var humana = Fabrica.Sessao();
        humana.Transition(HandlingMode.HumanAssisted, null, 1);
        Assert.Equal(BeginTurnOutcome.NotAiResponder, Begin(humana, Guid.NewGuid(), out _));

        var comLane = Fabrica.Sessao();
        Begin(comLane, Guid.NewGuid(), out _, lease: 5);
        Assert.Equal(BeginTurnOutcome.LeaseTokenStale, Begin(comLane, Guid.NewGuid(), out var grant, lease: 4));
        Assert.Null(grant);
        Assert.Equal(BeginTurnOutcome.Granted, Begin(comLane, Guid.NewGuid(), out _, lease: 5));
    }

    [Fact]
    public void Turno_recusado_nao_muda_nada_na_sessao()
    {
        var sessao = Fabrica.Sessao();
        var ativo = Guid.NewGuid();
        Begin(sessao, ativo, out _, lease: 5);

        Begin(sessao, Guid.NewGuid(), out _, lease: 1);

        Assert.Equal(ativo, sessao.ActiveTurnId);
        Assert.Equal(1, sessao.TurnEpoch);
        Assert.Equal(5, sessao.LastLeaseToken);
    }

    // EndTurn e CancelTurn

    [Fact]
    public void EndTurn_libera_so_o_turno_e_a_epoca_certos_e_e_idempotente()
    {
        var sessao = Fabrica.Sessao();
        var turno = Guid.NewGuid();
        Begin(sessao, turno, out var grant);

        Assert.False(sessao.EndTurn(Guid.NewGuid(), grant!.Epoch));
        Assert.False(sessao.EndTurn(turno, grant.Epoch + 1));
        Assert.Equal(turno, sessao.ActiveTurnId);

        Assert.True(sessao.EndTurn(turno, grant.Epoch));
        Assert.Null(sessao.ActiveTurnId);
        Assert.False(sessao.EndTurn(turno, grant.Epoch));
    }

    [Fact]
    public void CancelTurn_limpa_o_turno_avanca_a_epoca_e_a_resposta_do_turno_cancelado_e_recusada()
    {
        var sessao = Fabrica.Sessao();
        var turno = Guid.NewGuid();
        Begin(sessao, turno, out var grant);

        var havia = sessao.CancelTurn();

        Assert.True(havia);
        Assert.Null(sessao.ActiveTurnId);
        Assert.Equal(grant!.Epoch + 1, sessao.TurnEpoch);
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, sessao.CheckReplyFences(turno, grant.Epoch, 1, 0));
        Assert.False(sessao.CancelTurn());
    }

    // Cercas de resposta

    private static (ServiceSession Sessao, Guid Turno, TurnGrant Grant) SessaoComTurno(int mensagens = 1)
    {
        var sessao = Fabrica.Sessao();
        for (var i = 0; i < mensagens; i++)
        {
            sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        }

        var turno = Guid.NewGuid();
        Begin(sessao, turno, out var grant);
        return (sessao, turno, grant!);
    }

    [Fact]
    public void Resposta_que_passa_em_todas_as_cercas_e_aceita()
    {
        var (sessao, turno, grant) = SessaoComTurno(3);

        Assert.Null(sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
    }

    [Fact]
    public void Mensagem_nova_depois_do_snapshot_torna_a_resposta_defasada_e_ela_e_sempre_recusada()
    {
        var (sessao, turno, grant) = SessaoComTurno(2);

        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);

        Assert.Equal(
            RegisterReplyOutcome.StaleInput,
            sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
    }

    [Fact]
    public void A_cerca_de_entrada_exige_igualdade_e_nao_aceita_nem_valor_maior_nem_menor()
    {
        var (sessao, turno, grant) = SessaoComTurno(3);

        Assert.Equal(RegisterReplyOutcome.StaleInput, sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq - 1));
        Assert.Equal(RegisterReplyOutcome.StaleInput, sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq + 1));
        Assert.Equal(RegisterReplyOutcome.StaleInput, sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, 0));
    }

    [Fact]
    public void Turno_diferente_ou_epoca_diferente_e_TurnNotActive()
    {
        var (sessao, turno, grant) = SessaoComTurno();

        Assert.Equal(RegisterReplyOutcome.TurnNotActive, sessao.CheckReplyFences(Guid.NewGuid(), grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, sessao.CheckReplyFences(turno, grant.Epoch + 1, grant.StateVersion, grant.BasedOnInboundSeq));
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, sessao.CheckReplyFences(turno, grant.Epoch - 1, grant.StateVersion, grant.BasedOnInboundSeq));
    }

    [Fact]
    public void A_ordem_das_cercas_e_a_do_ADR_turno_depois_responsavel_depois_versao_depois_entrada()
    {
        var (sessao, turno, grant) = SessaoComTurno(2);
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        sessao.Close(Fabrica.Agora);

        // Tudo errado ao mesmo tempo: a primeira cerca vence.
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, sessao.CheckReplyFences(Guid.NewGuid(), 99, 99, 99));
        // Turno certo, mas a sessão não é mais da IA: NotAiResponder vem antes de StaleState e de StaleInput.
        Assert.Equal(RegisterReplyOutcome.NotAiResponder, sessao.CheckReplyFences(turno, grant.Epoch, 99, 99));
    }

    [Fact]
    public void Resposta_aceita_cobre_logicamente_limpa_o_turno_e_nao_pode_ser_repetida_pelo_mesmo_turno()
    {
        var (sessao, turno, grant) = SessaoComTurno(2);

        sessao.AcceptAiReply(grant.BasedOnInboundSeq, Fabrica.Agora.AddSeconds(5));

        Assert.Equal(2, sessao.AnsweredThroughSeq);
        Assert.Null(sessao.ActiveTurnId);
        Assert.Equal(Fabrica.Agora.AddSeconds(5), sessao.LastReplyAcceptedAt);
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
    }

    [Fact]
    public void AnsweredThroughSeq_nunca_decresce()
    {
        var sessao = Fabrica.Sessao();
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);

        sessao.AcceptAiReply(2, Fabrica.Agora);
        sessao.AcceptAiReply(1, Fabrica.Agora);

        Assert.Equal(2, sessao.AnsweredThroughSeq);
    }

    [Fact]
    public void A_cobertura_depende_so_da_resposta_aceita_e_nao_do_envio()
    {
        var (sessao, _, grant) = SessaoComTurno(2);
        sessao.AcceptAiReply(grant.BasedOnInboundSeq, Fabrica.Agora);

        sessao.MarkReplyDeliveryFailed();

        Assert.Equal(2, sessao.AnsweredThroughSeq);
        Assert.True(sessao.ReplyDeliveryFailed);
    }

    // Estado de atendimento (StateVersion)

    [Fact]
    public void Transicao_aplicada_avanca_a_versao_e_guarda_o_responsavel()
    {
        var sessao = Fabrica.Sessao();
        var atendente = Guid.NewGuid();

        var resultado = sessao.Transition(HandlingMode.HumanAssisted, atendente, 1);

        Assert.Equal(TransitionOutcome.Applied, resultado);
        Assert.Equal(HandlingMode.HumanAssisted, sessao.HandlingMode);
        Assert.Equal(atendente, sessao.ResponderRef);
        Assert.Equal(2, sessao.StateVersion);
        Assert.False(sessao.IsAiResponder);
    }

    [Fact]
    public void Atendimento_humano_sem_responsavel_e_atendimento_pendente_na_fila()
    {
        var sessao = Fabrica.Sessao();

        Assert.Equal(TransitionOutcome.Applied, sessao.Transition(HandlingMode.HumanAssisted, null, 1));

        Assert.Null(sessao.ResponderRef);
        Assert.Equal(HandlingMode.HumanAssisted, sessao.HandlingMode);
    }

    [Fact]
    public void Versao_esperada_diferente_da_atual_e_StaleState_e_nada_muda()
    {
        var sessao = Fabrica.Sessao();

        Assert.Equal(TransitionOutcome.StaleState, sessao.Transition(HandlingMode.HumanAssisted, null, 7));

        Assert.Equal(HandlingMode.AiAutonomous, sessao.HandlingMode);
        Assert.Equal(1, sessao.StateVersion);
    }

    [Fact]
    public void Duas_transicoes_com_a_mesma_versao_esperada_so_uma_vence()
    {
        var sessao = Fabrica.Sessao();

        var primeira = sessao.Transition(HandlingMode.HumanAssisted, Guid.NewGuid(), 1);
        var segunda = sessao.Transition(HandlingMode.HumanAssisted, Guid.NewGuid(), 1);

        Assert.Equal(TransitionOutcome.Applied, primeira);
        Assert.Equal(TransitionOutcome.StaleState, segunda);
    }

    [Fact]
    public void Modo_nao_especificado_combinacao_invalida_e_transicao_sem_efeito_sao_recusados()
    {
        var sessao = Fabrica.Sessao();

        Assert.Equal(TransitionOutcome.ModeNotSupported, sessao.Transition(HandlingMode.AiSupervised, null, 1));
        Assert.Equal(TransitionOutcome.InvalidTransition, sessao.Transition(HandlingMode.AiAutonomous, Guid.NewGuid(), 1));
        Assert.Equal(TransitionOutcome.InvalidTransition, sessao.Transition(HandlingMode.HumanAssisted, Guid.Empty, 1));
        Assert.Equal(TransitionOutcome.InvalidTransition, sessao.Transition((HandlingMode)99, null, 1));
        Assert.Equal(TransitionOutcome.InvalidTransition, sessao.Transition(HandlingMode.AiAutonomous, null, 1)); // já é isso
        Assert.Equal(1, sessao.StateVersion);
    }

    [Fact]
    public void Transicao_em_sessao_encerrada_e_recusada()
    {
        var sessao = Fabrica.Sessao();
        sessao.Close(Fabrica.Agora);

        Assert.Equal(TransitionOutcome.SessionClosed, sessao.Transition(HandlingMode.HumanAssisted, null, sessao.StateVersion));
    }

    [Fact]
    public void Transicao_nao_toca_o_turno_mas_a_resposta_dele_e_recusada_por_responsavel_e_depois_por_versao()
    {
        var (sessao, turno, grant) = SessaoComTurno();

        sessao.Transition(HandlingMode.HumanAssisted, Guid.NewGuid(), 1);
        Assert.Equal(turno, sessao.ActiveTurnId);
        // O humano está no comando: a IA não responde (CON-10).
        Assert.Equal(RegisterReplyOutcome.NotAiResponder, sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));

        // De volta à IA: o turno antigo ainda tem a versão antiga, e é recusado por ela (CON-3).
        sessao.Transition(HandlingMode.AiAutonomous, null, 2);
        Assert.Equal(RegisterReplyOutcome.StaleState, sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
    }

    [Fact]
    public void Encerrar_a_sessao_avanca_a_versao_e_recusa_resposta_e_novos_turnos()
    {
        var (sessao, turno, grant) = SessaoComTurno();

        Assert.True(sessao.Close(Fabrica.Agora));
        Assert.False(sessao.Close(Fabrica.Agora));

        Assert.Equal(SessionStatus.Closed, sessao.Status);
        Assert.Equal(2, sessao.StateVersion);
        Assert.Equal(RegisterReplyOutcome.NotAiResponder, sessao.CheckReplyFences(turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq));
        Assert.Equal(BeginTurnOutcome.SessionClosed, Begin(sessao, Guid.NewGuid(), out _, lease: 9));
    }

    // Sinal de atenção

    [Fact]
    public void Um_sinal_de_atencao_por_rajada_e_novo_sinal_so_depois_de_uma_resposta_aceita()
    {
        var sessao = Fabrica.Sessao();
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);

        Assert.Equal(RaiseAttentionOutcome.Raised, sessao.RaiseAttention());
        Assert.Equal(RaiseAttentionOutcome.AlreadyRaised, sessao.RaiseAttention());

        Begin(sessao, Guid.NewGuid(), out var grant);
        sessao.AcceptAiReply(grant!.BasedOnInboundSeq, Fabrica.Agora);
        sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);

        Assert.Equal(RaiseAttentionOutcome.Raised, sessao.RaiseAttention());
    }

    [Fact]
    public void O_sinal_de_atencao_nao_altera_responsavel_nem_modo_nem_versao()
    {
        var sessao = Fabrica.Sessao();

        sessao.RaiseAttention();

        Assert.Equal(HandlingMode.AiAutonomous, sessao.HandlingMode);
        Assert.Equal(1, sessao.StateVersion);
        Assert.Null(sessao.ResponderRef);
    }

    [Fact]
    public void Sinal_de_atencao_em_sessao_encerrada_e_recusado()
    {
        var sessao = Fabrica.Sessao();
        sessao.Close(Fabrica.Agora);

        Assert.Equal(RaiseAttentionOutcome.SessionClosed, sessao.RaiseAttention());
    }
}
