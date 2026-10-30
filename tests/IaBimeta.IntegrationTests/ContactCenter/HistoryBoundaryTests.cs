using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A fronteira do histórico que a fila antiga pode ler é a transição EFETIVA no Conversations (a sequência da última mensagem naquele
/// instante), nunca o relógio de quem processou o fim: o que chegou depois do encerramento não aparece, mesmo que o ContactCenter só
/// reconcilie muito depois, e a paginação (na ordem de exibição, que não é a de chegada) não omite nem inclui mensagem errada.
/// PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoryBoundaryTests(PostgresFixture banco)
{
    private static async Task<List<string?>> LerTudoAsync(CenarioDeConversas c, Guid item, RequestContext contexto, HistoryOrder ordem, int tamanho)
    {
        var textos = new List<string?>();
        string? cursor = null;
        for (var pagina = 0; pagina < 30; pagina++)
        {
            var lida = await c.ComoAsync(contexto, sp => sp.GetRequiredService<QueueItemService>().GetMessagesAsync(item, tamanho, cursor, ordem));
            Assert.NotNull(lida);
            Assert.True(lida.Items.Count <= tamanho);
            textos.AddRange(lida.Items.Select(m => m.Text));
            if (lida.NextCursor is null)
            {
                return textos;
            }

            cursor = lida.NextCursor;
        }

        Assert.Fail("A paginação não terminou.");
        return textos;
    }

    private static readonly (HistoryOrder Ordem, int Tamanho)[] Leituras =
    {
        (HistoryOrder.OldestFirst, 50), (HistoryOrder.OldestFirst, 1), (HistoryOrder.NewestFirst, 50), (HistoryOrder.NewestFirst, 1), (HistoryOrder.NewestFirst, 2),
    };

    [Fact]
    public async Task Encerramento_remoto_em_t1_mensagens_em_t2_e_reconciliacao_em_t3_a_fila_anterior_nao_ve_o_que_veio_em_t2()
    {
        banco.RelogioDaMensageria.Sincronizar();
        var c = await CenarioDeConversas.CriarAsync(banco, "hpremoto", mensageria: true);
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        await c.EntrarAsync(contato, texto: "durante o atendimento");
        await c.RodarRelayAsync(); // as operações do próprio ContactCenter já foram entregues: só o que vem agora é novo

        // t1: o atendimento termina POR FORA (alguém devolve a conversa à IA direto no Conversations). O evento fica no outbox.
        var t1 = await c.TransicionarAsync(sessao, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(sessao));
        Assert.Equal(TransitionOutcome.Applied, t1.Outcome);

        // t2: a conversa segue com a IA e chegam mensagens do cliente.
        banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(5));
        await c.EntrarAsync(contato, texto: "t2 primeira");
        await c.EntrarAsync(contato, texto: "t2 segunda");

        // t3: só agora o evento é entregue e o ContactCenter reconcilia (o item é encerrado neste instante).
        banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(5));
        await c.RodarRelayAsync();

        var encerrado = (await c.ItemAsync(item.Id))!;
        Assert.Equal(QueueItemStatus.Closed, encerrado.Status);
        Assert.True(encerrado.ClosedAt > (await c.HistoricoAsync(sessao, ordem: HistoryOrder.NewestFirst))!.Items[0].ReceivedAt); // o relógio de t3 já passou de tudo
        foreach (var (ordem, tamanho) in Leituras)
        {
            var textos = await LerTudoAsync(c, item.Id, supervisor.Contexto, ordem, tamanho);
            var esperado = ordem == HistoryOrder.OldestFirst ? new[] { "antes da fila", "durante o atendimento" } : new[] { "durante o atendimento", "antes da fila" };
            Assert.Equal(esperado, textos);
        }
    }

    [Fact]
    public async Task A_devolucao_a_IA_feita_pelo_ContactCenter_fecha_o_historico_na_fronteira_da_transicao_e_nao_na_gravacao_do_item()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hpdevolve");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);

        var resultado = await c.ComoAsync(supervisor.Contexto, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());

            // Uma mensagem do cliente chega LOGO DEPOIS de a transição para a IA ser confirmada, antes de o ContactCenter gravar o fim do item.
            controle.Depois(nameof(IConversationGateway.TransitionAsync), async () => await c.EntrarAsync(contato, texto: "chegou entre a transição e a gravação"));
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor).ReturnToAiAsync(item.Id, Guid.NewGuid());
        });

        Assert.Equal((OperationOutcome.Completed, QueueItemStatus.Closed), (resultado.Outcome, resultado.Item.Status));
        foreach (var (ordem, tamanho) in Leituras)
        {
            var textos = await LerTudoAsync(c, item.Id, supervisor.Contexto, ordem, tamanho);
            Assert.Equal(new[] { "antes da fila" }, textos);
        }
    }

    [Fact]
    public async Task Mensagens_atrasadas_e_com_horario_do_provedor_fora_de_ordem_em_varias_paginas_nenhuma_valida_e_omitida_e_nenhuma_de_fora_entra()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hpordem");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var contato = ContactCenterTestSupport.Unico("contato");
        var t = DateTimeOffset.UtcNow;

        // A ordem de exibição é por horário do PROVEDOR (OccurredAt); a de chegada (MessageSeq) é outra.
        var sessao = (await c.EntrarAsync(contato, texto: "m1", quando: t.AddSeconds(10))).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.EntrarAsync(contato, texto: "m2", quando: t.AddSeconds(20));
        await c.EntrarAsync(contato, texto: "m3 atrasada", quando: t.AddSeconds(-100)); // chegou durante o atendimento, mas é de muito antes
        await c.DevolverAIAAsync(item.Id, c.ContextoDeSistema);

        // Depois do encerramento chegam uma mensagem com horário do provedor ANTERIOR a todas (primeira na ordem de exibição) e outra posterior.
        await c.EntrarAsync(contato, texto: "m4 de fora e mais antiga", quando: t.AddSeconds(-200));
        await c.EntrarAsync(contato, texto: "m5 de fora", quando: t.AddSeconds(500));

        foreach (var (ordem, tamanho) in Leituras)
        {
            var textos = await LerTudoAsync(c, item.Id, supervisor.Contexto, ordem, tamanho);
            var esperado = new[] { "m3 atrasada", "m1", "m2" }; // na ordem de exibição, por horário do provedor
            Assert.Equal(ordem == HistoryOrder.OldestFirst ? esperado : esperado.Reverse().ToArray(), textos);
        }
    }

    [Fact]
    public async Task Antes_da_reconciliacao_o_item_ainda_ativo_nao_deixa_ler_o_que_veio_depois_da_devolucao_a_IA_feita_no_Conversations()
    {
        banco.RelogioDaMensageria.Sincronizar();
        var c = await CenarioDeConversas.CriarAsync(banco, "hpantes", mensageria: true);
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        await c.EntrarAsync(contato, texto: "durante o atendimento");
        await c.RodarRelayAsync();

        // O Conversations confirma a devolução à IA e chegam mensagens, mas o evento ainda NÃO foi entregue ao ContactCenter: o item continua ativo.
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        await c.EntrarAsync(contato, texto: "depois da devolucao");
        await c.EntrarAsync(contato, texto: "mais uma");
        Assert.Equal(QueueItemStatus.Assigned, (await c.ItemAsync(item.Id))!.Status);

        foreach (var (ordem, tamanho) in Leituras)
        {
            Assert.Empty(await LerTudoAsync(c, item.Id, supervisor.Contexto, ordem, tamanho)); // período fechado até a fronteira chegar
        }

        Assert.Null(await c.ConversaDoItemAsync(item.Id, supervisor.Contexto)); // nem o estado atual da conversa

        // A projeção chega: o item é encerrado com a fronteira da transição, e o período volta a ser legível, sem o que veio depois.
        await c.RodarRelayAsync();
        Assert.Equal(QueueItemStatus.Closed, (await c.ItemAsync(item.Id))!.Status);
        Assert.Equal(new[] { "antes da fila", "durante o atendimento" }, await LerTudoAsync(c, item.Id, supervisor.Contexto, HistoryOrder.OldestFirst, 1));
    }

    [Fact]
    public async Task Se_a_devolucao_a_IA_e_as_mensagens_chegam_durante_a_propria_consulta_so_se_le_ate_o_que_a_verificacao_viu()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hpconcorre");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);

        var pagina = await c.ComoAsync(supervisor.Contexto, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());

            // Entre a verificação do estado e a leitura das mensagens, a sessão volta para a IA e chegam mensagens.
            var intercalou = false;
            controle.Antes(nameof(IConversationGateway.GetHistoryAsync), async () =>
            {
                Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
                await c.EntrarAsync(contato, texto: "chegou depois da verificacao");
                intercalou = true;
            });
            var lida = await ActivatorUtilities.CreateInstance<QueueItemService>(sp, gateway).GetMessagesAsync(item.Id, 50, null, HistoryOrder.OldestFirst);
            Assert.True(intercalou);
            return lida;
        });

        Assert.Equal(new[] { "antes da fila" }, pagina!.Items.Select(m => m.Text));
    }

    [Fact]
    public async Task O_no_op_com_a_resposta_perdida_e_repetido_sem_transformar_a_fronteira_desconhecida_em_fronteira_efetiva()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hpnoop");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);
        var pedido = Guid.NewGuid();

        async Task<QueueItemOperationResult> DevolverAsync(Action<Interceptador<IConversationGateway>> falha) => await c.ComoAsync(supervisor.Contexto, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            falha(controle);
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor).ReturnToAiAsync(item.Id, pedido);
        });

        // 1) A intenção de devolver à IA fica pendente (o Conversations não responde): nada foi aplicado.
        var primeira = await DevolverAsync(g => g.Substituir(nameof(IConversationGateway.FenceAsync), _ => Task.FromException<FenceHandlingResult>(new InvalidOperationException("fora do ar"))));
        Assert.Equal(OperationOutcome.Pending, primeira.Outcome);

        // 2) OUTRA transição devolve a sessão à IA (instante t1) e chega uma mensagem (m2).
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        await c.EntrarAsync(contato, texto: "m2 depois da devolucao");

        // 3) O executor faz a transição dele, que agora NÃO muda nada (e fica registrada), e o processo perde a resposta depois do commit.
        var segunda = await DevolverAsync(g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")));
        Assert.Equal(OperationOutcome.Pending, segunda.Outcome);

        // 4) A recuperação repete o pedido: a transição já foi tratada, e a fronteira continua DESCONHECIDA (nunca a sequência de m2).
        var terceira = await c.DevolverAIAAsync(item.Id, supervisor.Contexto, pedido);
        Assert.Equal((OperationOutcome.Completed, QueueItemStatus.Closed), (terceira.Outcome, terceira.Item.Status));

        foreach (var (ordem, tamanho) in Leituras)
        {
            Assert.Empty(await LerTudoAsync(c, item.Id, supervisor.Contexto, ordem, tamanho)); // período fechado: m2 não é lido
        }
    }

    [Fact]
    public async Task Sem_fronteira_conhecida_o_item_encerrado_nao_mostra_nenhuma_mensagem_o_periodo_fica_fechado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hpsemfronteira");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.DevolverAIAAsync(item.Id, c.ContextoDeSistema);
        Assert.Equal(new[] { "antes da fila" }, await LerTudoAsync(c, item.Id, supervisor.Contexto, HistoryOrder.OldestFirst, 50));

        // Um item encerrado de antes de a fronteira existir (ou cuja fronteira não se conhece) nunca abre a conversa inteira.
        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ContactCenterDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE contact_center.queue_items SET closed_through_message_seq = NULL WHERE id = {item.Id}");
            return 0;
        });

        Assert.Empty(await LerTudoAsync(c, item.Id, supervisor.Contexto, HistoryOrder.OldestFirst, 50));
        Assert.Empty(await LerTudoAsync(c, item.Id, supervisor.Contexto, HistoryOrder.NewestFirst, 1));
    }
}
