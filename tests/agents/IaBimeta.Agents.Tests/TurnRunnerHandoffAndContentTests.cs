using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.ContactCenter.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;

namespace IaBimeta.Agents;

/// <summary>
/// A política do conteúdo pendente (MED-1 a MED-4: o modelo nunca vê conteúdo indisponível) e o handoff do <see cref="TurnRunner"/>: a retomada de um handoff já decidido e cada resposta do ContactCenter
/// (concluído, pendente, sessão encerrada, recusa transitória e definitiva).
/// </summary>
public sealed class TurnRunnerHandoffAndContentTests
{
    private static SnapshotMessage Audio(long seq, ContentState estado = ContentState.Pending) => Snapshots.Customer(seq, null, MessageKind.Audio, estado);

    private static SnapshotMessage Midia(long seq, MessageKind tipo, ContentState estado = ContentState.Pending) => Snapshots.Customer(seq, null, tipo, estado);

    // 8. Conteúdo pendente: espera, resposta informativa e encaminhamento

    [Fact]
    public async Task Audio_pendente_dentro_da_espera_adia_o_turno_sem_chamar_o_modelo()
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Audio(1)), epoch: 4);
        var turno = h.SeedRunning(attempts: 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.WaitingForContent, "content-pending"), resultado);
        var agora = h.Clock.GetUtcNow();
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Scheduled, agora + h.Options.ContentRecheck, (DateTimeOffset?)agora, 1), (final.State, final.FireAt, final.ContentWaitStartedAt, final.Attempts));
        Assert.Empty(h.Gateway.Requests); // MED-1: o turno não começa enquanto houver conteúdo pendente
        Assert.Empty(h.Turns.Registers);
        Assert.Empty(h.Handoff.Requests);
        var fim = Assert.Single(h.Turns.Ends);
        Assert.Equal((TurnEndReason.Discarded, 4L), (fim.Reason, fim.Epoch));
    }

    [Fact]
    public async Task Audio_pendente_e_reavaliado_depois_do_recuo_e_vencida_a_espera_recebe_a_resposta_informativa()
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Audio(1)));
        var turno = h.SeedRunning();

        var primeira = await h.RunAsync(turno);
        Assert.Equal(TurnRunOutcome.WaitingForContent, primeira.Outcome);

        // O executor o reivindica de novo quando fica devido, e a espera adicional já venceu.
        h.Clock.Advance(h.Options.AudioExtraWait);
        var reivindicado = Assert.Single(await h.Store.ClaimDueAsync("teste", 5, CancellationToken.None));
        var segunda = await h.Runner.RunAsync(reivindicado.Id, CancellationToken.None);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.InformativeReply, "informative-reply"), segunda);
        var registro = Assert.Single(h.Turns.Registers);
        Assert.Equal((ReplyKind.Informative, Definitions.Default().AudioInformativeText), (registro.ReplyKind, registro.Text)); // MED-3: o texto aprovado do tenant
        Assert.Empty(registro.AiRequestIds);
        Assert.Empty(h.Gateway.Requests); // sem modelo
        Assert.Equal(AiTurnState.Completed, h.Fresh(turno).State);
    }

    [Fact]
    public async Task Audio_vencida_a_espera_sem_texto_aprovado_na_definicao_transfere_ao_atendente()
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Audio(1)));
        h.Definitions.Published = Definitions.Default() with { AudioInformativeText = null };
        var turno = h.SeedRunning(waitingForContent: true);
        h.Clock.Advance(h.Options.AudioExtraWait);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.HandedOff, "content-unavailable"), resultado); // a IA não improvisa um texto
        Assert.Equal(HandoffReasons.ContentUnavailable, Assert.Single(h.Handoff.Requests).Reason);
        Assert.Empty(h.Gateway.Requests);
        Assert.DoesNotContain(h.Turns.Registers, r => r.Text == Definitions.Default().AudioInformativeText);
    }

    [Theory]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Document)]
    public async Task Imagem_ou_documento_vencida_a_espera_vai_para_o_atendente_com_o_motivo_content_unavailable(MessageKind tipo)
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Midia(1, tipo)));
        var turno = h.SeedRunning(waitingForContent: true);
        h.Clock.Advance(h.Options.ExtraWaitFor(tipo));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.HandedOff, "content-unavailable"), resultado);
        var pedido = Assert.Single(h.Handoff.Requests);
        Assert.Equal((turno.Id, HandoffReasons.ContentUnavailable), (pedido.RequestId, pedido.Reason)); // MED-4
        Assert.Empty(h.Gateway.Requests);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Completed, "handoff:content-unavailable"), (final.State, final.EndReason));
    }

    [Theory]
    [InlineData(MessageKind.Video)]
    [InlineData(MessageKind.Other)]
    public async Task Video_e_outros_tipos_nao_tem_espera_e_vao_para_o_atendente_na_primeira_avaliacao(MessageKind tipo)
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Midia(1, tipo)));
        var turno = h.SeedRunning(); // nenhuma espera anterior

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.HandedOff, "content-unavailable"), resultado);
        Assert.Equal(HandoffReasons.ContentUnavailable, Assert.Single(h.Handoff.Requests).Reason);
        Assert.Empty(h.Gateway.Requests);
    }

    [Fact]
    public async Task Texto_pronto_junto_de_audio_pendente_o_modelo_nunca_e_chamado_enquanto_espera_e_depois_a_politica_vale_para_o_grupo()
    {
        var grupo = RunnerHarness.Group(Snapshots.Customer(1, "Bom dia, tudo bem?"), Audio(2));
        var h = new RunnerHarness().Grant(grupo);
        var turno = h.SeedRunning();

        var dentro = await h.RunAsync(turno); // MID-6: a política vale para o GRUPO: o texto pronto também espera
        Assert.Equal(TurnRunOutcome.WaitingForContent, dentro.Outcome);
        Assert.Empty(h.Gateway.Requests);

        h.Clock.Advance(h.Options.AudioExtraWait);
        var reivindicado = Assert.Single(await h.Store.ClaimDueAsync("teste", 5, CancellationToken.None));
        var depois = await h.Runner.RunAsync(reivindicado.Id, CancellationToken.None);

        Assert.Equal(TurnRunOutcome.InformativeReply, depois.Outcome);
        Assert.Empty(h.Gateway.Requests); // o modelo nunca viu o texto, nem o áudio
        var registro = Assert.Single(h.Turns.Registers);
        Assert.Equal((ReplyKind.Informative, grupo.LastInboundSeq), (registro.ReplyKind, registro.BasedOnInboundSeq)); // cobre o grupo inteiro
    }

    [Fact]
    public async Task Texto_pronto_junto_de_imagem_pendente_vencida_a_espera_o_grupo_vai_para_o_atendente()
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Snapshots.Customer(1, "Segue o pedido."), Midia(2, MessageKind.Image)));
        var turno = h.SeedRunning(waitingForContent: true);
        h.Clock.Advance(h.Options.ImageExtraWait);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.HandedOff, "content-unavailable"), resultado);
        Assert.Empty(h.Gateway.Requests);
    }

    [Fact]
    public async Task Conteudo_que_falhou_aplica_a_politica_na_hora_sem_esperar_o_prazo()
    {
        var audio = new RunnerHarness().Grant(RunnerHarness.Group(Audio(1, ContentState.Failed)));
        var turnoAudio = audio.SeedRunning(); // nenhuma espera vencida

        var imagem = new RunnerHarness().Grant(RunnerHarness.Group(Midia(1, MessageKind.Image, ContentState.Failed)));
        var turnoImagem = imagem.SeedRunning();

        var resultadoAudio = await audio.RunAsync(turnoAudio);
        var resultadoImagem = await imagem.RunAsync(turnoImagem);

        Assert.Equal(TurnRunOutcome.InformativeReply, resultadoAudio.Outcome); // MID-7: a política por tipo, já
        Assert.Equal(TurnRunOutcome.HandedOff, resultadoImagem.Outcome);
        Assert.Empty(audio.Gateway.Requests);
        Assert.Empty(imagem.Gateway.Requests);
    }

    [Fact]
    public async Task Audio_que_falhou_junto_de_imagem_pendente_ainda_espera_a_imagem()
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Audio(1, ContentState.Failed), Midia(2, MessageKind.Image)));
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.WaitingForContent, resultado.Outcome);
        Assert.Empty(h.Gateway.Requests);
    }

    [Fact]
    public async Task O_prompt_nunca_leva_marcador_nem_texto_de_conteudo_que_nao_esta_pronto()
    {
        // MID-8: um áudio antigo cujo conteúdo falhou (já respondido) continua no histórico, mas sem texto: o modelo só vê o que está pronto.
        var snapshot = Snapshots.Snapshot(
            new[]
            {
                Snapshots.Customer(1, null, MessageKind.Audio, ContentState.Failed, pending: false),
                Snapshots.Outbound(2, "Pode escrever sua dúvida?"),
                Snapshots.Customer(3, "Preciso de jejum para o exame?"),
            },
            lastInbound: 3,
            answered: 1);
        var h = new RunnerHarness().Grant(snapshot);
        var turno = h.SeedRunning();

        await h.RunAsync(turno);

        var pedido = Assert.Single(h.Gateway.Requests);
        var mensagem = Assert.Single(pedido.Messages); // a resposta anterior (sem usuário antes dela) e o áudio sem texto ficam de fora
        Assert.Equal(("Preciso de jejum para o exame?", AiGateway.Contracts.AiMessageRole.User), (mensagem.Text, mensagem.Role));
        Assert.DoesNotContain(pedido.Messages, m => string.IsNullOrWhiteSpace(m.Text));
        Assert.DoesNotContain("áudio", string.Join(" ", pedido.Messages.Select(m => m.Text)), StringComparison.OrdinalIgnoreCase);
    }

    // 10. Retomada do handoff

    private static (RunnerHarness H, AiTurn Turno) Retomar(RequestHandoffResult contactCenter, int attempts = 0, string motivo = "ai-decision.price", bool noticeDone = true)
    {
        var h = new RunnerHarness();
        h.Handoff.Behavior = _ => contactCenter;
        return (h, h.SeedRunning(attempts: attempts, handoffReason: motivo, noticeDone: noticeDone));
    }

    [Fact]
    public async Task Retomada_com_o_aviso_ja_registrado_so_repete_o_pedido_sem_BeginTurn_e_sem_modelo()
    {
        var (h, turno) = Retomar(new RequestHandoffResult(RequestHandoffOutcome.Completed, Guid.NewGuid(), Guid.NewGuid()));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.HandedOff, "handoff-resumed"), resultado);
        Assert.Empty(h.Turns.Begins);
        Assert.Empty(h.Gateway.Requests);
        Assert.Empty(h.Turns.Registers);
        var pedido = Assert.Single(h.Handoff.Requests);
        Assert.Equal((turno.SessionId, turno.Id, "ai-decision.price"), (pedido.SessionId, pedido.RequestId, pedido.Reason)); // o MESMO identificador: idempotente
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Completed, "handoff:handoff-resumed"), (final.State, final.EndReason));
    }

    [Fact]
    public async Task Retomada_de_um_handoff_de_falha_termina_Dead()
    {
        var (h, turno) = Retomar(new RequestHandoffResult(RequestHandoffOutcome.AlreadyApplied, Guid.NewGuid(), Guid.NewGuid()), motivo: HandoffReasons.AiProcessingFailed);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "handoff-resumed"), resultado);
        Assert.Equal(AiTurnState.Dead, h.Fresh(turno).State);
    }

    [Fact]
    public async Task Retomada_sem_o_aviso_registra_o_aviso_depois_do_BeginTurn_e_pede_o_atendente()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText(), epoch: 9);
        var turno = h.SeedRunning(handoffReason: "ai-decision.price", noticeDone: false);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.HandedOff, "handoff-resumed"), resultado);
        Assert.Single(h.Turns.Begins);
        var aviso = Assert.Single(h.Turns.Registers);
        Assert.Equal((ReplyKind.Informative, Definitions.Default().HandoffNoticeText, 9L), (aviso.ReplyKind, aviso.Text, aviso.Epoch));
        Assert.Single(h.Handoff.Requests);
        Assert.True(h.Fresh(turno).NoticeDone);
        Assert.Empty(h.Gateway.Requests);
    }

    [Fact]
    public async Task Retomada_sem_aviso_aprovado_na_definicao_nao_precisa_de_BeginTurn()
    {
        var h = new RunnerHarness();
        h.Definitions.Published = Definitions.Default() with { HandoffNoticeText = null };
        var turno = h.SeedRunning(handoffReason: "ai-decision.price", noticeDone: false);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.HandedOff, resultado.Outcome);
        Assert.Empty(h.Turns.Begins);
        Assert.Empty(h.Turns.Registers);
    }

    [Theory]
    [InlineData(BeginTurnOutcome.NotAiResponder, TurnRunOutcome.Cancelled, "handled-by-human")]
    [InlineData(BeginTurnOutcome.SessionClosed, TurnRunOutcome.Cancelled, "session-closed")]
    [InlineData(BeginTurnOutcome.SessionNotFound, TurnRunOutcome.Cancelled, "session-closed")]
    [InlineData(BeginTurnOutcome.LeaseTokenStale, TurnRunOutcome.Retrying, "lease-token-stale")]
    public async Task Retomada_com_BeginTurn_recusado_nao_repete_o_pedido(BeginTurnOutcome recusa, TurnRunOutcome desfecho, string codigo)
    {
        var h = new RunnerHarness();
        h.Turns.OnBegin = _ => new BeginTurnResult(recusa, null, null);
        var turno = h.SeedRunning(handoffReason: "ai-decision.price", noticeDone: false);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(desfecho, codigo), resultado); // o humano já no comando: o pedido anterior chegou a ser aplicado
        Assert.Empty(h.Handoff.Requests);
        Assert.Empty(h.Gateway.Requests);
    }

    // 10. Cada resposta do ContactCenter ao pedido de handoff

    [Fact]
    public async Task Handoff_pendente_devolve_o_turno_a_Ready_sem_gastar_tentativa()
    {
        var (h, turno) = Retomar(new RequestHandoffResult(RequestHandoffOutcome.Pending, Guid.NewGuid(), Guid.NewGuid()), attempts: 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "handoff-pending"), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, 1, h.Clock.GetUtcNow() + h.Options.CapacityRetry), (final.State, final.Attempts, final.NextAttemptAt));
    }

    [Fact]
    public async Task Handoff_pendente_alem_da_idade_maxima_do_turno_mata_o_turno()
    {
        var (h, turno) = Retomar(new RequestHandoffResult(RequestHandoffOutcome.Pending, Guid.NewGuid(), Guid.NewGuid()));
        h.Clock.Advance(h.Options.MaxTurnAge + TimeSpan.FromSeconds(1));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "handoff-pending-timeout"), resultado);
        Assert.Equal(AiTurnState.Dead, h.Fresh(turno).State);
    }

    [Fact]
    public async Task Sessao_encerrada_no_handoff_cancela_o_turno()
    {
        var (h, turno) = Retomar(new RequestHandoffResult(RequestHandoffOutcome.SessionEnded));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Cancelled, "session-ended"), resultado);
        Assert.Equal(AiTurnState.Cancelled, h.Fresh(turno).State);
    }

    [Theory]
    [InlineData(RequestHandoffOutcome.NotRoutable, "queue-paused")]
    [InlineData(RequestHandoffOutcome.Refused, "concurrent-update")]
    public async Task Recusa_transitoria_do_handoff_repete_com_o_mesmo_identificador_e_gasta_tentativa(RequestHandoffOutcome recusa, string codigo)
    {
        var (h, turno) = Retomar(new RequestHandoffResult(recusa, Retryable: true, Code: codigo), attempts: 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "handoff-" + codigo), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, 2, h.Clock.GetUtcNow() + TimeSpan.FromSeconds(10)), (final.State, final.Attempts, final.NextAttemptAt));
        Assert.Equal(turno.Id, Assert.Single(h.Handoff.Requests).RequestId);
    }

    [Fact]
    public async Task Recusa_transitoria_com_as_tentativas_esgotadas_mata_o_turno()
    {
        var (h, turno) = Retomar(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, Retryable: true, Code: "queue-paused"), attempts: 2);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "handoff-refused-queue-paused"), resultado);
        Assert.Equal(AiTurnState.Dead, h.Fresh(turno).State);
    }

    [Fact]
    public async Task Conversa_que_ja_tem_atendente_humano_cancela_o_turno()
    {
        var (h, turno) = Retomar(new RequestHandoffResult(RequestHandoffOutcome.Refused, Retryable: false, Code: "already-handled-by-human"));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Cancelled, "handled-by-human"), resultado);
        Assert.Equal(AiTurnState.Cancelled, h.Fresh(turno).State);
    }

    [Theory]
    [InlineData(RequestHandoffOutcome.NotRoutable, "no-route", "handoff-refused-no-route")]
    [InlineData(RequestHandoffOutcome.Refused, "request-id-reused", "handoff-refused-request-id-reused")]
    [InlineData(RequestHandoffOutcome.Refused, null, "handoff-refused-definitive")]
    public async Task Recusa_definitiva_do_handoff_mata_o_turno_com_o_codigo(RequestHandoffOutcome recusa, string? codigo, string motivoDoFim)
    {
        var (h, turno) = Retomar(new RequestHandoffResult(recusa, Retryable: false, Code: codigo));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, motivoDoFim), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Dead, motivoDoFim), (final.State, final.EndReason));
        Assert.Single(h.Handoff.Requests); // definitiva: repetir não muda nada
    }
}
