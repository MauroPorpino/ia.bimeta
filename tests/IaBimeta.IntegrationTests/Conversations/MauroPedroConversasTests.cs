using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// Teste de aceitação do CLAUDE.md: Mauro pergunta sobre o exame A no atendimento #1001; Pedro pergunta sobre o exame B no
/// #1002; Mauro pergunta "preciso ficar em jejum para esse exame?". A requisição de Mauro contém o contexto de A, nunca o de
/// B, e a resposta dele só vai para o #1001. Aqui o "contexto da requisição" é o snapshot que o Conversations entrega no
/// <c>BeginTurn</c> (o Agents monta o prompt só com ele), e a "resposta enviada" é o registro da resposta e o evento de saída.
/// Vale para duas sessões do mesmo tenant e para dois tenants diferentes (1001 e 1002).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MauroPedroConversasTests(PostgresFixture banco)
{
    private const string PerguntaA = "Quanto custa o exame A?";
    private const string PerguntaB = "Quais os horários do exame B?";
    private const string PerguntaJejum = "Preciso ficar em jejum para esse exame?";

    private static IEnumerable<string?> Textos(BeginTurnResult inicio) => inicio.Snapshot!.Messages.Select(m => m.Text);

    // Duas sessões do mesmo tenant (#1001 e #1002)

    [Fact]
    public async Task A_requisicao_de_Mauro_leva_o_contexto_do_exame_A_nunca_o_do_B_e_a_resposta_vai_so_para_o_atendimento_1001()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "mauropedro");

        var m1 = await c.EntrarAsync("mauro", PerguntaA);
        var p1 = await c.EntrarAsync("pedro", PerguntaB);
        var m2 = await c.EntrarAsync("mauro", PerguntaJejum);
        Assert.NotEqual(m1.SessionId, p1.SessionId); // #1001 e #1002
        Assert.Equal(m1.SessionId, m2.SessionId);

        var turnoMauro = Guid.NewGuid();
        var turnoPedro = Guid.NewGuid();
        var inicioMauro = await c.IniciarTurnoAsync(m1.SessionId, turnoMauro);
        var inicioPedro = await c.IniciarTurnoAsync(p1.SessionId, turnoPedro);

        // A requisição de Mauro: só a sessão dele, com o exame A e a pergunta do jejum, e nada do exame B.
        Assert.Equal(m1.SessionId, inicioMauro.Snapshot!.SessionId);
        Assert.Equal(new[] { PerguntaA, PerguntaJejum }, Textos(inicioMauro));
        Assert.DoesNotContain(Textos(inicioMauro), t => t!.Contains("exame B", StringComparison.Ordinal));
        Assert.Equal(new[] { PerguntaB }, Textos(inicioPedro));
        var idsDeMauro = new HashSet<Guid> { m1.MessageId, m2.MessageId };
        Assert.All(inicioMauro.Snapshot.Messages, m => Assert.Contains(m.MessageId, idsDeMauro));

        // A resposta de Mauro não pode ser aplicada ao atendimento de Pedro, nem ao destino dele.
        var paraOAtendimentoErrado = await c.ResponderComAsync(new RegisterAiReplyCommand(
            p1.SessionId, turnoMauro, inicioMauro.Grant!.Epoch, inicioMauro.Grant.StateVersion, inicioMauro.Grant.BasedOnInboundSeq,
            ReplyKind.Generated, m1.ConversationId, "Sim, 8 horas de jejum.", new[] { Guid.NewGuid() }));
        var paraAConversaErrada = await c.ResponderComAsync(new RegisterAiReplyCommand(
            m1.SessionId, turnoMauro, inicioMauro.Grant.Epoch, inicioMauro.Grant.StateVersion, inicioMauro.Grant.BasedOnInboundSeq,
            ReplyKind.Generated, p1.ConversationId, "Sim, 8 horas de jejum.", new[] { Guid.NewGuid() }));
        Assert.Equal(RegisterReplyOutcome.TurnNotActive, paraOAtendimentoErrado.Outcome);
        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, paraAConversaErrada.Outcome);
        Assert.DoesNotContain((await c.HistoricoAsync(p1.SessionId))!.Items, m => m.Direction == MessageDirection.Outbound);
        Assert.Equal(0, (await c.SessaoAsync(p1.SessionId))!.AnsweredThroughSeq);

        // A resposta certa é aceita e cobre só o atendimento de Mauro.
        var aceita = await c.ResponderAsync(m1.SessionId, turnoMauro, inicioMauro, m1.ConversationId, "Sim, 8 horas de jejum.");

        Assert.Equal(RegisterReplyOutcome.Accepted, aceita.Outcome);
        var historicoMauro = (await c.HistoricoAsync(m1.SessionId))!.Items;
        Assert.Equal(new[] { PerguntaA, PerguntaJejum, "Sim, 8 horas de jejum." }, historicoMauro.Select(m => m.Text));
        Assert.Equal(new[] { PerguntaB }, (await c.HistoricoAsync(p1.SessionId))!.Items.Select(m => m.Text));
        Assert.Equal(2, (await c.SessaoAsync(m1.SessionId))!.AnsweredThroughSeq);
        Assert.Equal(0, (await c.SessaoAsync(p1.SessionId))!.AnsweredThroughSeq);

        var saida = Assert.IsType<OutboundMessageRequestedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, m1.SessionId)));
        Assert.Equal((m1.SessionId, m1.ConversationId, aceita.MessageId), (saida.SessionId, saida.ConversationId, saida.MessageId));
        Assert.Empty(c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, p1.SessionId)); // nada saiu para o atendimento de Pedro

        // O turno de Pedro continua dele e responde a ele.
        var respostaDePedro = await c.ResponderAsync(p1.SessionId, turnoPedro, inicioPedro, p1.ConversationId, "O exame B é feito das 7h às 11h.");
        Assert.Equal(RegisterReplyOutcome.Accepted, respostaDePedro.Outcome);
        Assert.Equal(new[] { PerguntaA, PerguntaJejum, "Sim, 8 horas de jejum." }, (await c.HistoricoAsync(m1.SessionId))!.Items.Select(m => m.Text));
    }

    [Fact]
    public async Task Doze_clientes_do_mesmo_tenant_conversando_ao_mesmo_tempo_nunca_misturam_contexto_nem_resposta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "misturas");

        async Task<(int N, IngestInboundMessageResult Entrada, BeginTurnResult Inicio, RegisterAiReplyResult Resposta, Guid Turno)> FluxoAsync(int n)
        {
            var contato = $"cliente-{n:00}";
            var primeira = await c.EntrarAsync(contato, $"cliente {n}: exame X{n}");
            await c.EntrarAsync(contato, $"cliente {n}: preciso ficar em jejum para esse exame?");
            var turno = Guid.NewGuid();
            var inicio = await c.IniciarTurnoAsync(primeira.SessionId, turno);
            var resposta = await c.ResponderAsync(primeira.SessionId, turno, inicio, primeira.ConversationId, $"resposta para o cliente {n}");
            return (n, primeira, inicio, resposta, turno);
        }

        var fluxos = await Task.WhenAll(Enumerable.Range(1, 12).Select(FluxoAsync));

        Assert.Equal(12, fluxos.Select(f => f.Entrada.SessionId).Distinct().Count());
        foreach (var f in fluxos)
        {
            // O contexto da requisição é só o do cliente.
            Assert.All(Textos(f.Inicio), t => Assert.StartsWith($"cliente {f.N}:", t));
            Assert.Equal(RegisterReplyOutcome.Accepted, f.Resposta.Outcome);

            // E a resposta ficou só no atendimento dele.
            var itens = (await c.HistoricoAsync(f.Entrada.SessionId))!.Items;
            Assert.Equal(3, itens.Count);
            Assert.Equal($"resposta para o cliente {f.N}", itens[^1].Text);
            Assert.Equal(f.Entrada.ConversationId, itens[^1].ConversationId);
            var saida = Assert.IsType<OutboundMessageRequestedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, f.Entrada.SessionId)));
            Assert.Equal(f.Entrada.ConversationId, saida.ConversationId);
        }
    }

    // Dois tenants (1001 e 1002)

    [Fact]
    public async Task Mauro_no_tenant_1001_nunca_acessa_mensagens_nem_contexto_de_Pedro_no_tenant_1002()
    {
        var t1001 = await CenarioDeConversas.CriarAsync(banco, "t1001");
        var t1002 = await CenarioDeConversas.CriarAsync(banco, "t1002");
        var mauro = await t1001.EntrarAsync("5511999990001", PerguntaA);
        var pedro = await t1002.EntrarAsync("5511999990001", PerguntaB); // até o contato tem a mesma referência
        var leitorMauro = t1001.Usuario(null, Permission.ConversationRead, Permission.ConversationManage);
        var leitorPedro = t1002.Usuario(null, Permission.ConversationRead, Permission.ConversationManage);

        // Mauro lê o dele e só o dele.
        Assert.Equal(PerguntaA, Assert.Single((await t1001.HistoricoAsync(mauro.SessionId, 10, contexto: leitorMauro))!.Items).Text);
        Assert.Null(await t1001.SessaoAsync(pedro.SessionId, leitorMauro));
        Assert.Null(await t1001.HistoricoAsync(pedro.SessionId, 10, contexto: leitorMauro));
        Assert.Equal(mauro.SessionId, Assert.Single((await t1001.ListarSessoesAsync(leitorMauro)).Items).Id);
        Assert.Null(await t1002.SessaoAsync(mauro.SessionId, leitorPedro));
        Assert.Null(await t1002.HistoricoAsync(mauro.SessionId, 10, contexto: leitorPedro));

        // O contexto da IA de cada um: um snapshot por (tenant, sessão), e o do outro tenant é inalcançável.
        var turnoMauro = Guid.NewGuid();
        var inicioMauro = await t1001.IniciarTurnoAsync(mauro.SessionId, turnoMauro);
        var inicioPedro = await t1002.IniciarTurnoAsync(pedro.SessionId, Guid.NewGuid());
        Assert.Equal(new[] { PerguntaA }, Textos(inicioMauro));
        Assert.Equal(new[] { PerguntaB }, Textos(inicioPedro));
        Assert.Equal(BeginTurnOutcome.SessionNotFound, (await t1002.IniciarTurnoAsync(mauro.SessionId, Guid.NewGuid())).Outcome);
        Assert.Equal(BeginTurnOutcome.SessionNotFound, (await t1001.IniciarTurnoAsync(pedro.SessionId, Guid.NewGuid())).Outcome);

        // A resposta de IA do tenant do Mauro aplicada, por engano, no contexto do tenant do Pedro: não existe lá.
        var noTenantErrado = await t1002.ResponderComAsync(new RegisterAiReplyCommand(
            mauro.SessionId, turnoMauro, inicioMauro.Grant!.Epoch, inicioMauro.Grant.StateVersion, inicioMauro.Grant.BasedOnInboundSeq,
            ReplyKind.Generated, mauro.ConversationId, "resposta do tenant 1001", new[] { Guid.NewGuid() }));
        Assert.Equal(RegisterReplyOutcome.SessionNotFound, noTenantErrado.Outcome);

        // E, no tenant certo, com o destino (conversa) de outro tenant: a conversa do 1002 não existe no 1001.
        var comDestinoDeOutroTenant = await t1001.ResponderComAsync(new RegisterAiReplyCommand(
            mauro.SessionId, turnoMauro, inicioMauro.Grant.Epoch, inicioMauro.Grant.StateVersion, inicioMauro.Grant.BasedOnInboundSeq,
            ReplyKind.Generated, pedro.ConversationId, "resposta para a conversa de outro tenant", new[] { Guid.NewGuid() }));
        Assert.Equal(RegisterReplyOutcome.ShapeNotAllowed, comDestinoDeOutroTenant.Outcome);

        // Nada foi gravado em lugar nenhum por essas tentativas.
        Assert.Equal(new[] { PerguntaA }, (await t1001.HistoricoAsync(mauro.SessionId))!.Items.Select(m => m.Text));
        Assert.Equal(new[] { PerguntaB }, (await t1002.HistoricoAsync(pedro.SessionId))!.Items.Select(m => m.Text));
        Assert.Equal(0, (await t1001.SessaoAsync(mauro.SessionId))!.AnsweredThroughSeq);
        Assert.Equal(0, (await t1002.SessaoAsync(pedro.SessionId))!.AnsweredThroughSeq);
        Assert.Empty(t1001.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, mauro.SessionId));
        Assert.Empty(t1002.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, pedro.SessionId));

        // Só a resposta certa é aceita, no tenant certo.
        var certa = await t1001.ResponderAsync(mauro.SessionId, turnoMauro, inicioMauro, mauro.ConversationId, "O exame A custa R$ 50.");
        Assert.Equal(RegisterReplyOutcome.Accepted, certa.Outcome);
        Assert.Equal(2, (await t1001.HistoricoAsync(mauro.SessionId))!.Items.Count);
        Assert.Single((await t1002.HistoricoAsync(pedro.SessionId))!.Items);
    }

    [Fact]
    public async Task Uma_conversa_de_um_tenant_nao_aceita_mensagem_nem_resposta_gravada_com_ids_de_outro_tenant_mesmo_pelo_DbContext()
    {
        var t1001 = await CenarioDeConversas.CriarAsync(banco, "t1001");
        var t1002 = await CenarioDeConversas.CriarAsync(banco, "t1002");
        var mauro = await t1001.EntrarAsync("5511999990001", PerguntaA);
        var pedro = await t1002.EntrarAsync("5511999990001", PerguntaB);

        await using var escopo = banco.NovoEscopo(t1001.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();

        // Do ponto de vista do tenant 1001, o que é do 1002 simplesmente não existe, em nenhuma tabela do módulo.
        Assert.False(await db.Sessions.AnyAsync(s => s.Id == pedro.SessionId));
        Assert.False(await db.Messages.AnyAsync(m => m.SessionId == pedro.SessionId));
        Assert.False(await db.Conversations.AnyAsync(x => x.Id == pedro.ConversationId));
        Assert.Equal(1, await db.Messages.CountAsync(m => m.Text == PerguntaA));
        Assert.Equal(0, await db.Messages.CountAsync(m => m.Text == PerguntaB));
        _ = mauro;
    }
}
