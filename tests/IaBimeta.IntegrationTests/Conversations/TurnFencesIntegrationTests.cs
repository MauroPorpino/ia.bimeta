using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// As cercas do ADR 0007 com PostgreSQL real: turno logicamente ativo, época, StateVersion e basedOnInboundSeq. A resposta
/// defasada é sempre recusada, e a decisão é só do Conversations, sob trava da linha da sessão, mesmo com chamadas simultâneas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TurnFencesIntegrationTests(PostgresFixture banco)
{
    private const string Contato = "5511999990001";

    private async Task<(CenarioDeConversas Cenario, IngestInboundMessageResult Entrada)> NovaSessaoAsync(string prefixo, string texto = "Pergunta 1")
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        return (c, await c.EntrarAsync(Contato, texto));
    }

    private async Task<long> OutboundCountAsync(CenarioDeConversas c, Guid sessao)
    {
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>().Messages
            .LongCountAsync(m => m.SessionId == sessao && m.Direction == MessageDirection.Outbound);
    }

    // BeginTurn e snapshot

    [Fact]
    public async Task O_turno_recebe_o_grant_e_o_snapshot_da_mesma_leitura_consistente()
    {
        var (c, r) = await NovaSessaoAsync("begin");
        await c.EntrarAsync(Contato, "Pergunta 2");

        var inicio = await c.IniciarTurnoAsync(r.SessionId, Guid.NewGuid());

        Assert.Equal(BeginTurnOutcome.Granted, inicio.Outcome);
        Assert.Equal((1L, 2L, 1L), (inicio.Grant!.Epoch, inicio.Grant.BasedOnInboundSeq, inicio.Grant.StateVersion));
        var snapshot = inicio.Snapshot!;
        Assert.Equal((r.SessionId, HandlingMode.AiAutonomous, 2L, 0L, false), (snapshot.SessionId, snapshot.HandlingMode, snapshot.LastInboundSeq, snapshot.AnsweredThroughSeq, snapshot.ReplyDeliveryFailed));
        Assert.Equal(inicio.Grant.BasedOnInboundSeq, snapshot.LastInboundSeq); // a cerca de entrada descreve exatamente o que o modelo vê
        Assert.Equal(new[] { "Pergunta 1", "Pergunta 2" }, snapshot.Messages.Select(m => m.Text));
        Assert.All(snapshot.Messages, m => Assert.True(m.IsPending));
        Assert.True((await c.SessaoAsync(r.SessionId))!.HasActiveTurn);
    }

    [Fact]
    public async Task O_snapshot_esconde_conteudo_pendente_ou_com_falha_e_marca_o_que_ja_foi_respondido()
    {
        var (c, r) = await NovaSessaoAsync("snap", "Pergunta respondida");
        var turno = Guid.NewGuid();
        var primeiro = await c.IniciarTurnoAsync(r.SessionId, turno);
        await c.ResponderAsync(r.SessionId, turno, primeiro, r.ConversationId, "Resposta 1");
        var audio = await c.EntrarAsync(Contato, null, tipo: MessageKind.Audio);
        var imagem = await c.EntrarAsync(Contato, "legenda", tipo: MessageKind.Image);
        await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IConversationIngestion>().FailMessageContentAsync(new FailMessageContentCommand(imagem.MessageId, "ocr-timeout")));

        var inicio = await c.IniciarTurnoAsync(r.SessionId, Guid.NewGuid(), lease: 2);

        var mensagens = inicio.Snapshot!.Messages;
        Assert.Equal(new[] { "Pergunta respondida", "Resposta 1", null, null }, mensagens.Select(m => m.Text));
        Assert.Equal(new[] { false, false, true, true }, mensagens.Select(m => m.IsPending));
        Assert.Equal((ContentState.Pending, ContentState.Failed), (mensagens[2].ContentState, mensagens[3].ContentState));
        Assert.Equal(audio.InboundSeq, mensagens[2].InboundSeq);
    }

    [Fact]
    public async Task O_snapshot_respeita_o_limite_de_mensagens_e_traz_as_mais_recentes_em_ordem()
    {
        var (c, r) = await NovaSessaoAsync("limite", "m1");
        for (var i = 2; i <= 12; i++)
        {
            await c.EntrarAsync(Contato, $"m{i}");
        }

        var inicio = await c.IniciarTurnoAsync(r.SessionId, Guid.NewGuid(), mensagens: 5);

        Assert.Equal(new[] { "m8", "m9", "m10", "m11", "m12" }, inicio.Snapshot!.Messages.Select(m => m.Text));
        Assert.Equal(12, inicio.Grant!.BasedOnInboundSeq);
    }

    [Fact]
    public async Task Sessao_inexistente_encerrada_ou_com_atendente_humano_nao_concede_turno()
    {
        var (c, r) = await NovaSessaoAsync("negado");
        var humano = await c.EntrarAsync("5511999990002", "outra sessão");
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(humano.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), 1)).Outcome);
        var fechada = await c.EntrarAsync("5511999990003", "outra ainda");
        Assert.True((await c.EncerrarSessaoAsync(fechada.SessionId)).Closed);

        Assert.Equal(BeginTurnOutcome.SessionNotFound, (await c.IniciarTurnoAsync(Guid.NewGuid(), Guid.NewGuid())).Outcome);
        Assert.Equal(BeginTurnOutcome.NotAiResponder, (await c.IniciarTurnoAsync(humano.SessionId, Guid.NewGuid())).Outcome);
        Assert.Equal(BeginTurnOutcome.SessionClosed, (await c.IniciarTurnoAsync(fechada.SessionId, Guid.NewGuid())).Outcome);
        Assert.False((await c.SessaoAsync(r.SessionId))!.HasActiveTurn);
    }

    [Fact]
    public async Task Worker_com_lane_antiga_nao_toma_o_turno()
    {
        var (c, r) = await NovaSessaoAsync("lease");
        var vigente = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, vigente, lease: 5);

        var zumbi = await c.IniciarTurnoAsync(r.SessionId, Guid.NewGuid(), lease: 3);

        Assert.Equal(BeginTurnOutcome.LeaseTokenStale, zumbi.Outcome);
        Assert.Null(zumbi.Grant);
        // O turno vigente continua sendo o ativo e responde normalmente.
        Assert.Equal(RegisterReplyOutcome.Accepted, (await c.ResponderAsync(r.SessionId, vigente, inicio, r.ConversationId)).Outcome);
    }

    // A resposta defasada é sempre rejeitada

    [Fact]
    public async Task Resposta_baseada_em_versao_antiga_da_conversa_e_rejeitada_e_nada_e_gravado()
    {
        var (c, r) = await NovaSessaoAsync("defasada");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        await c.EntrarAsync(Contato, "Pergunta 2, chegou depois do snapshot");

        var resposta = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId, "Resposta que não viu a pergunta 2");

        Assert.Equal(RegisterReplyOutcome.StaleInput, resposta.Outcome);
        Assert.Null(resposta.MessageId);
        Assert.Equal((0L, 2L), (resposta.AnsweredThroughSeq, resposta.LastInboundSeq));
        Assert.Equal(0, await OutboundCountAsync(c, r.SessionId));
        Assert.Equal(0, (await c.SessaoAsync(r.SessionId))!.AnsweredThroughSeq);
        Assert.Empty(c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, r.SessionId));
        Assert.Empty(c.PayloadsDe(ConversationEventTypes.AiReplyAccepted, r.SessionId));
    }

    [Fact]
    public async Task Depois_da_defasada_um_turno_novo_com_a_cerca_atual_responde_cobrindo_tudo()
    {
        var (c, r) = await NovaSessaoAsync("novoturno");
        var velho = Guid.NewGuid();
        var inicioVelho = await c.IniciarTurnoAsync(r.SessionId, velho);
        await c.EntrarAsync(Contato, "Pergunta 2");
        Assert.Equal(RegisterReplyOutcome.StaleInput, (await c.ResponderAsync(r.SessionId, velho, inicioVelho, r.ConversationId)).Outcome);

        var novo = Guid.NewGuid();
        var inicioNovo = await c.IniciarTurnoAsync(r.SessionId, novo);
        var resposta = await c.ResponderAsync(r.SessionId, novo, inicioNovo, r.ConversationId, "Resposta às duas perguntas");

        Assert.Equal(RegisterReplyOutcome.Accepted, resposta.Outcome);
        Assert.Equal(2, resposta.AnsweredThroughSeq);
        // E a resposta do turno velho, mesmo tardia, continua recusada: ele foi substituído.
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, (await c.ResponderAsync(r.SessionId, velho, inicioVelho, r.ConversationId)).Outcome);
        Assert.Equal(1, await OutboundCountAsync(c, r.SessionId));
    }

    [Fact]
    public async Task Mudanca_de_modo_invalida_a_resposta_do_turno_em_andamento()
    {
        var (c, r) = await NovaSessaoAsync("modo");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);

        var atendente = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, atendente, 1)).Outcome);
        var comHumano = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(r.SessionId, HandlingMode.AiAutonomous, null, 2)).Outcome);
        var devolvida = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);

        Assert.Equal(RegisterReplyOutcome.NotAiResponder, comHumano.Outcome); // o atendente humano assumiu
        Assert.Equal(RegisterReplyOutcome.StaleState, devolvida.Outcome); // voltou à IA, mas o estado de quando o turno começou já passou
        Assert.Equal(0, await OutboundCountAsync(c, r.SessionId));
    }

    [Fact]
    public async Task Sessao_encerrada_com_turno_em_andamento_nao_aceita_a_resposta()
    {
        var (c, r) = await NovaSessaoAsync("fechou");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        Assert.True((await c.EncerrarSessaoAsync(r.SessionId)).Closed);

        var resposta = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);

        Assert.Equal(RegisterReplyOutcome.NotAiResponder, resposta.Outcome);
        Assert.Equal(0, await OutboundCountAsync(c, r.SessionId));
    }

    // Turno logicamente ativo e época

    [Fact]
    public async Task Turno_substituido_nao_registra_resposta_mesmo_que_continue_executando()
    {
        var (c, r) = await NovaSessaoAsync("substituido");
        var primeiro = Guid.NewGuid();
        var inicioPrimeiro = await c.IniciarTurnoAsync(r.SessionId, primeiro);
        var segundo = Guid.NewGuid();
        var inicioSegundo = await c.IniciarTurnoAsync(r.SessionId, segundo);

        var doSubstituido = await c.ResponderAsync(r.SessionId, primeiro, inicioPrimeiro, r.ConversationId, "do turno substituído");
        var doAtivo = await c.ResponderAsync(r.SessionId, segundo, inicioSegundo, r.ConversationId, "do turno ativo");

        Assert.Equal((1L, 2L), (inicioPrimeiro.Grant!.Epoch, inicioSegundo.Grant!.Epoch));
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, doSubstituido.Outcome);
        Assert.Equal(RegisterReplyOutcome.Accepted, doAtivo.Outcome);
        Assert.Equal("do turno ativo", (await c.HistoricoAsync(r.SessionId))!.Items.Last().Text);
    }

    [Fact]
    public async Task Epoca_ou_turno_trocados_na_resposta_sao_recusados()
    {
        var (c, r) = await NovaSessaoAsync("epoca");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        var g = inicio.Grant!;
        RegisterAiReplyCommand Com(Guid t, long epoca) => new(r.SessionId, t, epoca, g.StateVersion, g.BasedOnInboundSeq, ReplyKind.Generated, r.ConversationId, "x", new[] { Guid.NewGuid() });

        Assert.Equal(RegisterReplyOutcome.TurnNotActive, (await c.ResponderComAsync(Com(turno, g.Epoch + 1))).Outcome);
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, (await c.ResponderComAsync(Com(turno, g.Epoch - 1))).Outcome);
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, (await c.ResponderComAsync(Com(Guid.NewGuid(), g.Epoch))).Outcome);
        Assert.Equal(RegisterReplyOutcome.Accepted, (await c.ResponderComAsync(Com(turno, g.Epoch))).Outcome); // o legítimo segue valendo
    }

    [Fact]
    public async Task Cancelar_o_turno_avanca_a_epoca_e_recusa_a_resposta_em_andamento()
    {
        var (c, r) = await NovaSessaoAsync("cancela");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);

        var cancelado = await c.CancelarTurnoAsync(r.SessionId);
        var resposta = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);
        var denovo = await c.CancelarTurnoAsync(r.SessionId);

        Assert.True(cancelado.SessionFound);
        Assert.True(cancelado.TurnWasActive);
        Assert.Equal(2, cancelado.TurnEpoch);
        Assert.False(denovo.TurnWasActive);
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, resposta.Outcome);
        Assert.False((await c.SessaoAsync(r.SessionId))!.HasActiveTurn);
        Assert.False((await c.CancelarTurnoAsync(Guid.NewGuid())).SessionFound);
    }

    [Fact]
    public async Task Encerrar_o_turno_e_idempotente_e_a_resposta_depois_dele_e_recusada()
    {
        var (c, r) = await NovaSessaoAsync("fim");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);

        Assert.Equal(EndTurnOutcome.NotActive, (await c.EncerrarTurnoAsync(r.SessionId, turno, inicio.Grant!.Epoch + 1)).Outcome);
        Assert.Equal(EndTurnOutcome.Ended, (await c.EncerrarTurnoAsync(r.SessionId, turno, inicio.Grant.Epoch)).Outcome);
        Assert.Equal(EndTurnOutcome.NotActive, (await c.EncerrarTurnoAsync(r.SessionId, turno, inicio.Grant.Epoch)).Outcome);
        Assert.Equal(EndTurnOutcome.SessionNotFound, (await c.EncerrarTurnoAsync(Guid.NewGuid(), turno, 1)).Outcome);
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, (await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId)).Outcome);
    }

    // Idempotência e concorrência

    [Fact]
    public async Task A_mesma_resposta_do_mesmo_turno_devolve_a_mesma_mensagem_sem_gravar_outra()
    {
        var (c, r) = await NovaSessaoAsync("idemresp");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);

        var primeira = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);
        var repetida = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId, "texto diferente");

        Assert.Equal(RegisterReplyOutcome.Accepted, primeira.Outcome);
        Assert.Equal(RegisterReplyOutcome.AlreadyRegistered, repetida.Outcome);
        Assert.Equal(primeira.MessageId, repetida.MessageId);
        Assert.Equal(1, await OutboundCountAsync(c, r.SessionId));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.AiReplyAccepted, r.SessionId));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.OutboundMessageRequested, r.SessionId));
    }

    [Fact]
    public async Task Vinte_registros_simultaneos_da_mesma_resposta_aceitam_uma_so_vez()
    {
        var (c, r) = await NovaSessaoAsync("corridaresp");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId)));

        Assert.Equal(1, resultados.Count(x => x.Outcome == RegisterReplyOutcome.Accepted));
        Assert.Equal(19, resultados.Count(x => x.Outcome == RegisterReplyOutcome.AlreadyRegistered));
        Assert.Single(resultados.Select(x => x.MessageId).Distinct());
        Assert.Equal(1, await OutboundCountAsync(c, r.SessionId));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.OutboundMessageRequested, r.SessionId));
    }

    [Fact]
    public async Task Dez_turnos_iniciados_ao_mesmo_tempo_deixam_um_unico_ativo_e_so_ele_responde()
    {
        var (c, r) = await NovaSessaoAsync("corridaturno");
        var turnos = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();

        var inicios = await Task.WhenAll(turnos.Select(async t => (Turno: t, Inicio: await c.IniciarTurnoAsync(r.SessionId, t))));

        Assert.All(inicios, x => Assert.Equal(BeginTurnOutcome.Granted, x.Inicio.Outcome));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), inicios.Select(x => x.Inicio.Grant!.Epoch).OrderBy(e => e)); // épocas distintas, sem repetição
        var vencedor = inicios.MaxBy(x => x.Inicio.Grant!.Epoch);

        var respostas = await Task.WhenAll(inicios.Select(async x => (x.Turno, Resultado: await c.ResponderAsync(r.SessionId, x.Turno, x.Inicio, r.ConversationId, $"resposta de {x.Turno}"))));

        Assert.Equal(vencedor.Turno, Assert.Single(respostas, x => x.Resultado.Outcome == RegisterReplyOutcome.Accepted).Turno);
        Assert.All(respostas.Where(x => x.Turno != vencedor.Turno), x => Assert.Equal(RegisterReplyOutcome.TurnNotActive, x.Resultado.Outcome));
        Assert.Equal(1, await OutboundCountAsync(c, r.SessionId));
    }

    [Fact]
    public async Task Resposta_e_mensagem_nova_simultaneas_nunca_cobrem_uma_mensagem_que_o_modelo_nao_viu()
    {
        for (var rodada = 0; rodada < 15; rodada++)
        {
            var (c, r) = await NovaSessaoAsync($"corrida{rodada}");
            var turno = Guid.NewGuid();
            var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);

            var respostaEmCurso = c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);
            var nova = c.EntrarAsync(Contato, "chegou durante a resposta");
            await Task.WhenAll(respostaEmCurso, nova);
            var resposta = await respostaEmCurso;

            var sessao = (await c.SessaoAsync(r.SessionId))!;
            Assert.Equal(2, sessao.LastInboundSeq); // a mensagem nova entrou, em qualquer ordem
            if (resposta.Outcome == RegisterReplyOutcome.Accepted)
            {
                // A resposta entrou primeiro: cobre só o que o modelo viu, e a mensagem nova segue pendente.
                Assert.Equal(1, sessao.AnsweredThroughSeq);
                Assert.Equal(1, await OutboundCountAsync(c, r.SessionId));
            }
            else
            {
                // A mensagem entrou primeiro: a resposta ficou defasada e foi recusada.
                Assert.Equal(RegisterReplyOutcome.StaleInput, resposta.Outcome);
                Assert.Equal(0, sessao.AnsweredThroughSeq);
                Assert.Equal(0, await OutboundCountAsync(c, r.SessionId));
            }
        }
    }

    // Forma da resposta

    [Fact]
    public async Task Resposta_com_forma_invalida_e_recusada_sem_consumir_o_turno()
    {
        var (c, r) = await NovaSessaoAsync("forma");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        var g = inicio.Grant!;
        RegisterAiReplyCommand Com(string texto, ReplyKind tipo = ReplyKind.Generated, Guid? conversa = null, IReadOnlyList<Guid>? requisicoes = null) =>
            new(r.SessionId, turno, g.Epoch, g.StateVersion, g.BasedOnInboundSeq, tipo, conversa ?? r.ConversationId, texto, requisicoes ?? new[] { Guid.NewGuid() });

        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, (await c.ResponderComAsync(Com(""))).Outcome);
        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, (await c.ResponderComAsync(Com("   "))).Outcome);
        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, (await c.ResponderComAsync(Com(new string('x', ConversationLimits.TextMaxLength + 1)))).Outcome);
        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, (await c.ResponderComAsync(Com("ok", (ReplyKind)99))).Outcome);
        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, (await c.ResponderComAsync(Com("ok", conversa: Guid.NewGuid()))).Outcome);
        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, (await c.ResponderComAsync(Com("ok", requisicoes: Enumerable.Range(0, TurnServiceLimites.MaxAiRequestIds + 1).Select(_ => Guid.NewGuid()).ToList()))).Outcome);
        Assert.Equal(0, await OutboundCountAsync(c, r.SessionId));
        Assert.True((await c.SessaoAsync(r.SessionId))!.HasActiveTurn); // o turno segue ativo para uma resposta válida

        Assert.Equal(RegisterReplyOutcome.Accepted, (await c.ResponderComAsync(Com("agora sim", ReplyKind.Informative))).Outcome);
        var saida = (await c.HistoricoAsync(r.SessionId))!.Items.Last();
        Assert.Equal((MessageAuthor.Ai, ReplyKind.Informative, "agora sim"), (saida.Author, saida.ReplyKind!.Value, saida.Text));
    }

    [Fact]
    public async Task A_cobertura_nunca_diminui_e_cada_resposta_cobre_ate_a_cerca_que_citou()
    {
        var (c, r) = await NovaSessaoAsync("cobertura");
        var coberturas = new List<long>();
        for (var i = 1; i <= 4; i++)
        {
            if (i > 1)
            {
                await c.EntrarAsync(Contato, $"Pergunta {i}");
            }

            var turno = Guid.NewGuid();
            var inicio = await c.IniciarTurnoAsync(r.SessionId, turno, lease: i);
            var resposta = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId, $"Resposta {i}");
            Assert.Equal(RegisterReplyOutcome.Accepted, resposta.Outcome);
            coberturas.Add((await c.SessaoAsync(r.SessionId))!.AnsweredThroughSeq);
        }

        Assert.Equal(new long[] { 1, 2, 3, 4 }, coberturas);
        var saidas = (await c.HistoricoAsync(r.SessionId))!.Items.Where(m => m.Direction == MessageDirection.Outbound).ToList();
        Assert.Equal(new long?[] { 1, 2, 3, 4 }, saidas.Select(m => m.CoveredThroughSeq));
    }

    // Sinal de atenção

    [Fact]
    public async Task O_sinal_de_atencao_sai_uma_vez_por_rajada_e_volta_a_valer_depois_de_uma_resposta_aceita()
    {
        var (c, r) = await NovaSessaoAsync("atencao");
        Task<RaiseSessionAttentionResult> Sinalizar(Guid sessao) =>
            c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<ITurnCommands>().RaiseSessionAttentionAsync(new RaiseSessionAttentionCommand(sessao, SessionAttentionReason.ChattyBurst, 12, 3)));

        var primeiro = await Sinalizar(r.SessionId);
        var repetido = await Sinalizar(r.SessionId);
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);
        await c.EntrarAsync(Contato, "outra rajada");
        var outraRajada = await Sinalizar(r.SessionId);

        Assert.Equal(RaiseAttentionOutcome.Raised, primeiro.Outcome);
        Assert.Equal(RaiseAttentionOutcome.AlreadyRaised, repetido.Outcome);
        Assert.Equal(RaiseAttentionOutcome.Raised, outraRajada.Outcome);
        Assert.Equal(RaiseAttentionOutcome.SessionNotFound, (await Sinalizar(Guid.NewGuid())).Outcome);
        Assert.Equal(2, c.EventosDe(ConversationEventTypes.SessionAttentionRaised, r.SessionId));
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((HandlingMode.AiAutonomous, null), (sessao.HandlingMode, sessao.ResponderRef)); // o sinal não transfere nada
    }
}

/// <summary>O limite do contrato de resposta (<c>TurnService.MaxAiRequestIds</c>), sem expor o serviço aos testes.</summary>
internal static class TurnServiceLimites
{
    public const int MaxAiRequestIds = IaBimeta.Conversations.Application.TurnService.MaxAiRequestIds;
}
