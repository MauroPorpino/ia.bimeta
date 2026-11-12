using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O comando de mensagem de fluxo do chatbot visual (ADR 0038) contra PostgreSQL real: as MESMAS cercas da resposta de IA (turno ativo, época, versão do estado, modo, entrada defasada), idempotência por turno,
/// autor <c>Bot</c>, cobertura lógica opcional, só contexto de sistema e isolamento entre tenants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MensagemDeFluxoTests(PostgresFixture banco)
{
    private static Task<RegisterFlowMessageResult> Registrar(CenarioDeConversas c, RegisterFlowMessageCommand comando, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, s => s.GetRequiredService<IFlowCommands>().RegisterFlowMessageAsync(comando));

    private static RegisterFlowMessageCommand Mensagem(Guid sessao, Guid turno, TurnGrant g, long baseInbound, Guid conversa, string texto = "Olá! Escolha uma opção.", bool cobre = true) =>
        new(sessao, turno, g.Epoch, g.StateVersion, baseInbound, conversa, texto, cobre);

    [Fact]
    public async Task A_mensagem_de_fluxo_e_aceita_com_autor_Bot_cobre_a_entrada_e_a_repeticao_do_mesmo_turno_nao_duplica()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fluxo-ok");
        var r = await c.EntrarAsync("5511999990001", "oi");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        Assert.Equal(BeginTurnOutcome.Granted, inicio.Outcome);

        var primeira = await Registrar(c, Mensagem(r.SessionId, turno, inicio.Grant!, inicio.Snapshot!.LastInboundSeq, r.ConversationId));
        Assert.Equal(RegisterReplyOutcome.Accepted, primeira.Outcome);
        Assert.Equal(1, primeira.AnsweredThroughSeq);

        var repetida = await Registrar(c, Mensagem(r.SessionId, turno, inicio.Grant!, inicio.Snapshot!.LastInboundSeq, r.ConversationId));
        Assert.Equal(RegisterReplyOutcome.AlreadyRegistered, repetida.Outcome);
        Assert.Equal(primeira.MessageId, repetida.MessageId);

        var historico = (await c.HistoricoAsync(r.SessionId))!.Items;
        var saida = Assert.Single(historico, m => m.Direction == MessageDirection.Outbound);
        Assert.Equal(MessageAuthor.Bot, saida.Author);
        Assert.Equal("Olá! Escolha uma opção.", saida.Text);
        Assert.Equal(1, c.EventosDe("conversations.outbound-message-requested", r.SessionId));
        Assert.Equal(0, c.EventosDe("conversations.ai-reply-accepted", r.SessionId)); // fluxo não é resposta de IA
    }

    [Fact]
    public async Task Entrada_chegada_depois_do_snapshot_deixa_a_mensagem_defasada_e_ela_e_recusada_sem_gravar_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fluxo-defasada");
        var r = await c.EntrarAsync("5511999990001", "oi");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        await c.EntrarAsync("5511999990001", "mais uma", evento: "evt-2");

        var resposta = await Registrar(c, Mensagem(r.SessionId, turno, inicio.Grant!, inicio.Snapshot!.LastInboundSeq, r.ConversationId));

        Assert.Equal(RegisterReplyOutcome.StaleInput, resposta.Outcome);
        Assert.DoesNotContain((await c.HistoricoAsync(r.SessionId))!.Items, m => m.Direction == MessageDirection.Outbound);

        // A recuperação é de quem chama: reabre o turno (nova leitura) e tenta de novo, sem afrouxar a cerca.
        var turno2 = Guid.NewGuid();
        var novo = await c.IniciarTurnoAsync(r.SessionId, turno2);
        var ok = await Registrar(c, Mensagem(r.SessionId, turno2, novo.Grant!, novo.Snapshot!.LastInboundSeq, r.ConversationId));
        Assert.Equal(RegisterReplyOutcome.Accepted, ok.Outcome);
        Assert.Equal(2, ok.AnsweredThroughSeq);
    }

    [Fact]
    public async Task Turno_cancelado_ou_substituido_e_estado_alterado_recusam_a_mensagem_do_fluxo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fluxo-cercas");
        var r = await c.EntrarAsync("5511999990001", "oi");

        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        await c.CancelarTurnoAsync(r.SessionId);
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, (await Registrar(c, Mensagem(r.SessionId, turno, inicio.Grant!, 1, r.ConversationId))).Outcome);

        var turnoA = Guid.NewGuid();
        var a = await c.IniciarTurnoAsync(r.SessionId, turnoA);
        var b = await c.IniciarTurnoAsync(r.SessionId, Guid.NewGuid()); // o turno B substitui o A
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, (await Registrar(c, Mensagem(r.SessionId, turnoA, a.Grant!, 1, r.ConversationId))).Outcome);

        // O atendente assume (muda o estado): o fluxo não escreve mais nada.
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        var turnoC = Guid.NewGuid();
        var comeco = await c.IniciarTurnoAsync(r.SessionId, turnoC);
        await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), sessao.StateVersion);
        var depois = await Registrar(c, Mensagem(r.SessionId, turnoC, comeco.Grant!, 1, r.ConversationId));
        Assert.True(depois.Outcome is RegisterReplyOutcome.NotAiResponder or RegisterReplyOutcome.TurnNotActive or RegisterReplyOutcome.StaleState, depois.Outcome.ToString());
        Assert.NotNull(b.Grant);
        Assert.DoesNotContain((await c.HistoricoAsync(r.SessionId))!.Items, m => m.Direction == MessageDirection.Outbound);
    }

    [Fact]
    public async Task Sem_cobertura_o_agente_ainda_precisa_responder_a_entrada_e_a_liberacao_avisa_o_Agents()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fluxo-delegar", mensageria: true);
        var r = await c.EntrarAsync("5511999990001", "oi");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);

        var resposta = await Registrar(c, Mensagem(r.SessionId, turno, inicio.Grant!, inicio.Snapshot!.LastInboundSeq, r.ConversationId, "Vou verificar.", cobre: false));
        Assert.Equal(RegisterReplyOutcome.Accepted, resposta.Outcome);
        Assert.Equal(0, resposta.AnsweredThroughSeq);
        Assert.Equal(0, (await c.SessaoAsync(r.SessionId))!.AnsweredThroughSeq);

        var liberada = await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IFlowCommands>().ReleaseSessionToAgentAsync(new ReleaseFlowCommand(r.SessionId, "delegated")));
        Assert.True(liberada.SessionFound);
        Assert.Equal(1, c.EventosDe("conversations.flow-released-to-agent", r.SessionId));
    }

    [Fact]
    public async Task So_um_contexto_de_sistema_chama_e_outro_tenant_nao_alcanca_a_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fluxo-acesso");
        var outro = await CenarioDeConversas.CriarAsync(banco, "fluxo-outro");
        var r = await c.EntrarAsync("5511999990001", "oi");
        var comando = new RegisterFlowMessageCommand(r.SessionId, Guid.NewGuid(), 1, 1, 1, r.ConversationId, "forjada");

        var admin = c.Usuario(null, Permission.ConversationRead, Permission.ConversationManage, Permission.WorkflowManage, Permission.WorkflowPublish);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Registrar(c, comando, admin));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(admin, s => s.GetRequiredService<IFlowCommands>().ReleaseSessionToAgentAsync(new ReleaseFlowCommand(r.SessionId, "delegated"))));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, s => s.GetRequiredService<IFlowCommands>().RegisterFlowMessageAsync(comando)));

        var deOutroTenant = await Registrar(outro, comando, outro.ContextoDeSistema);
        Assert.Equal(RegisterReplyOutcome.SessionNotFound, deOutroTenant.Outcome);
        Assert.False((await outro.ComoAsync(outro.ContextoDeSistema, s => s.GetRequiredService<IFlowCommands>().ReleaseSessionToAgentAsync(new ReleaseFlowCommand(r.SessionId, "delegated")))).SessionFound);
        Assert.DoesNotContain((await c.HistoricoAsync(r.SessionId))!.Items, m => m.Direction == MessageDirection.Outbound);
    }

    [Fact]
    public async Task Texto_vazio_ou_conversa_de_outra_sessao_e_forma_nao_permitida()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fluxo-forma");
        var r = await c.EntrarAsync("5511999990001", "oi");
        var outraSessao = await c.EntrarAsync("5511999990002", "oi");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);

        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, (await Registrar(c, Mensagem(r.SessionId, turno, inicio.Grant!, 1, r.ConversationId, texto: "   "))).Outcome);
        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, (await Registrar(c, Mensagem(r.SessionId, turno, inicio.Grant!, 1, outraSessao.ConversationId))).Outcome);
        Assert.Equal(RegisterReplyOutcome.Accepted, (await Registrar(c, Mensagem(r.SessionId, turno, inicio.Grant!, 1, r.ConversationId))).Outcome);
    }
}
