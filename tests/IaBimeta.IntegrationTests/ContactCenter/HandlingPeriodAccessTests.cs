using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
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
/// O acesso de um item à conversa vale para UM período de atendimento, identificado pelo Conversations e guardado no item: a sessão estar de
/// novo em atendimento humano não prova que é o mesmo período (humano → IA → humano), e uma transferência de unidade, concorrente ou feita
/// direto no Conversations, tira a conversa de quem a lê por esta fila. Qualquer divergência fecha o acesso, antes da reconciliação entre os
/// módulos e durante consultas concorrentes. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HandlingPeriodAccessTests(PostgresFixture banco)
{
    private static readonly (HistoryOrder Ordem, int Tamanho)[] Leituras =
    {
        (HistoryOrder.OldestFirst, 50), (HistoryOrder.OldestFirst, 1), (HistoryOrder.NewestFirst, 50), (HistoryOrder.NewestFirst, 2),
    };

    private static async Task<List<string?>> LerTudoAsync(CenarioDeConversas c, Guid item, RequestContext contexto, HistoryOrder ordem, int tamanho)
    {
        var textos = new List<string?>();
        string? cursor = null;
        for (var pagina = 0; pagina < 30; pagina++)
        {
            var lida = await c.ComoAsync(contexto, sp => sp.GetRequiredService<QueueItemService>().GetMessagesAsync(item, tamanho, cursor, ordem));
            Assert.NotNull(lida);
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

    [Fact]
    public async Task Humano_IA_humano_antes_da_reconciliacao_o_item_do_primeiro_periodo_nao_le_nem_o_intervalo_da_IA_nem_o_segundo_atendimento()
    {
        banco.RelogioDaMensageria.Sincronizar();
        var c = await CenarioDeConversas.CriarAsync(banco, "pernovo", mensageria: true);
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        await c.EntrarAsync(contato, texto: "durante o primeiro atendimento");
        await c.RodarRelayAsync(); // as operações do próprio ContactCenter já foram entregues

        // O primeiro atendimento termina POR FORA, a IA conversa, e a conversa volta ao atendimento humano em OUTRO período (com o Beto).
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        await c.EntrarAsync(contato, texto: "durante a IA");
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, beto.Id, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        await c.EntrarAsync(contato, texto: "segundo atendimento");

        // Nada foi reconciliado: o item continua ATIVO, e a sessão está em atendimento humano de novo. Só o modo humano não prova o período.
        Assert.Equal(QueueItemStatus.Assigned, (await c.ItemAsync(item.Id))!.Status);
        Assert.Equal(HandlingMode.HumanAssisted, (await c.SessaoAsync(sessao))!.HandlingMode);
        foreach (var (ordem, tamanho) in Leituras)
        {
            Assert.Empty(await LerTudoAsync(c, item.Id, supervisor.Contexto, ordem, tamanho));
        }

        Assert.Null(await c.ConversaDoItemAsync(item.Id, supervisor.Contexto));

        // A projeção chega, em ordem: o primeiro evento encerra o item na fronteira do primeiro período; o segundo não tem item ativo a atualizar.
        await c.RodarRelayAsync();
        Assert.Equal(QueueItemStatus.Closed, (await c.ItemAsync(item.Id))!.Status);
        Assert.Equal(
            new[] { "antes da fila", "durante o primeiro atendimento" },
            await LerTudoAsync(c, item.Id, supervisor.Contexto, HistoryOrder.OldestFirst, 1));
    }

    [Fact]
    public async Task Trocar_o_responsavel_dentro_do_mesmo_periodo_nao_fecha_o_acesso_do_item()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "permesmo");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        // Por fora, o responsável muda dentro do MESMO atendimento humano: o período é o mesmo e o acesso continua.
        await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, beto.Id, await c.VersaoDaSessaoAsync(sessao));
        await c.EntrarAsync(contato, texto: "continua no mesmo atendimento");

        Assert.Equal(
            new[] { "antes da fila", "continua no mesmo atendimento" },
            await LerTudoAsync(c, item.Id, supervisor.Contexto, HistoryOrder.OldestFirst, 50));
        Assert.NotNull(await c.ConversaDoItemAsync(item.Id, supervisor.Contexto));
    }

    private sealed record Cena(CenarioDeConversas C, Guid Matriz, Guid Filial, ServiceQueueView FilaDaMatriz, ServiceQueueView FilaDaFilial, Agente Supervisor, Agente Ana, Guid Sessao, QueueItemView Item, string Contato, ChannelInstanceId Canal);

    /// <summary>Uma conversa da matriz com a Ana (fila da matriz) e um supervisor SÓ da fila da matriz; o número autoriza as duas unidades.</summary>
    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        var filaDaMatriz = await c.CriarFilaAsync("Matriz", unidade: matriz);
        var filaDaFilial = await c.CriarFilaAsync("Filial", unidade: filial);
        var supervisor = await c.AgenteNaFilaAsync(banco, filaDaMatriz.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, filaDaMatriz.Id);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da transferencia", unidade: matriz, canal: canal)).SessionId;
        var item = await c.NaFilaAsync(sessao, filaDaMatriz.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        return new Cena(c, matriz, filial, filaDaMatriz, filaDaFilial, supervisor, ana, sessao, item, contato, canal);
    }

    [Fact]
    public async Task A_transferencia_concorrente_do_item_para_outra_fila_durante_a_consulta_fecha_o_acesso_de_quem_lia_pela_fila_antiga()
    {
        var s = await MontarAsync("pertransf");

        var pagina = await s.C.ComoAsync(s.Supervisor.Contexto, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            var intercalou = false;

            // O supervisor da matriz já foi autorizado e a sessão já foi verificada; ANTES de o histórico ser lido, o item é transferido para a
            // fila da filial (de que ele não participa), e chega uma mensagem na filial.
            controle.Antes(nameof(IConversationGateway.GetHistoryAsync), async () =>
            {
                Assert.Equal(OperationOutcome.Completed, (await s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaDaFilial.Id, null, s.Ana.Contexto)).Outcome);
                await s.C.EntrarAsync(s.Contato, texto: "ja na filial", canal: s.Canal);
                intercalou = true;
            });
            var lida = await ActivatorUtilities.CreateInstance<QueueItemService>(sp, gateway).GetMessagesAsync(s.Item.Id, 50, null, HistoryOrder.OldestFirst);
            Assert.True(intercalou);
            return lida;
        });

        // O que foi lido não é devolvido: o item mudou de fila durante a consulta, e quem lia pela fila antiga já não tem acesso.
        Assert.NotNull(pagina);
        Assert.Empty(pagina.Items);
        Assert.Null(await s.C.ItemAsync(s.Item.Id, s.Supervisor.Contexto)); // e o item já não aparece para ele
    }

    [Fact]
    public async Task A_transferencia_de_unidade_feita_direto_no_Conversations_fecha_o_acesso_do_item_da_fila_antiga_antes_de_qualquer_reconciliacao()
    {
        var s = await MontarAsync("perexterna");
        Assert.Equal(new[] { "antes da transferencia" }, await LerTudoAsync(s.C, s.Item.Id, s.Supervisor.Contexto, HistoryOrder.OldestFirst, 50));

        // A sessão vai para a filial por outro caminho (o item continua na fila da matriz, sem projeção desta mudança) e a conversa segue lá.
        Assert.Equal(TransferOutcome.Applied, (await s.C.EncaminharAsync(s.Sessao, s.Filial, null, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
        await s.C.EntrarAsync(s.Contato, texto: "ja na filial", canal: s.Canal);

        Assert.Equal(QueueItemStatus.Assigned, (await s.C.ItemAsync(s.Item.Id))!.Status);
        foreach (var (ordem, tamanho) in Leituras)
        {
            Assert.Empty(await LerTudoAsync(s.C, s.Item.Id, s.Supervisor.Contexto, ordem, tamanho));
        }

        Assert.Null(await s.C.ConversaDoItemAsync(s.Item.Id, s.Supervisor.Contexto));
    }

    [Fact]
    public async Task O_item_legado_sem_periodo_conhecido_nao_abre_a_conversa_o_acesso_fica_fechado()
    {
        var s = await MontarAsync("perlegado");
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>().Database
                .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET handling_period = NULL WHERE id = {s.Item.Id}");
            return 0;
        });

        Assert.Empty(await LerTudoAsync(s.C, s.Item.Id, s.Supervisor.Contexto, HistoryOrder.OldestFirst, 50));
        Assert.Null(await s.C.ConversaDoItemAsync(s.Item.Id, s.Supervisor.Contexto));
    }
}
