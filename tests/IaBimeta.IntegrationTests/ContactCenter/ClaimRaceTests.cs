using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Conversations;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// As corridas da reserva de uma conversa pelo atendente, provadas de forma determinística: a outra operação roda INTEIRA entre a leitura
/// e a gravação de quem está sendo testado (<see cref="Interceptador{T}"/>), em outro escopo e com PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaimRaceTests(PostgresFixture banco)
{
    private static QueueItemService ServicoCom(IServiceProvider sp, IQueueItemRepository itens) =>
        ActivatorUtilities.CreateInstance<QueueItemService>(sp, itens);

    // N04

    [Fact]
    public async Task ClaimNext_da_fila_A_ignora_a_candidata_transferida_para_B_entre_a_listagem_e_a_reserva_e_segue_para_a_proxima_de_A()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "claimsaiu");
        var a = await c.CriarFilaAsync("Fila A");
        var b = await c.CriarFilaAsync("Fila B");
        var supervisor = await c.AgenteNaFilaAsync(banco, a.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, a.Id);
        await c.AdicionarAtendenteAsync(b.Id, ana.Id, QueueMemberRole.Agent); // a Ana também atende a B: a conversa de B continuaria visível a ela
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), a.Id); // a mais antiga: a primeira candidata
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), a.Id);

        var resultado = await c.ComoAsync(ana.Contexto, async sp =>
        {
            var (itens, controle) = Interceptador<IQueueItemRepository>.Envolver(sp.GetRequiredService<IQueueItemRepository>());

            // Depois de a Ana LISTAR as candidatas de A, o supervisor transfere a primeira para B (a lista dela ficou velha).
            controle.Depois(nameof(IQueueItemRepository.ListWaitingAsync), async () =>
                Assert.Equal(OperationOutcome.Completed, (await c.TransferirParaFilaAsync(primeiro.Id, b.Id, null, supervisor.Contexto)).Outcome));
            return await ServicoCom(sp, itens).ClaimNextAsync(a.Id, Guid.NewGuid());
        });

        Assert.NotNull(resultado);
        Assert.Equal(segundo.Id, resultado.Item.Id); // a próxima candidata VÁLIDA de A
        Assert.Equal(ana.Id, (await c.ItemAsync(segundo.Id))!.AssignedUserId);

        var transferida = (await c.ItemAsync(primeiro.Id))!;
        Assert.Equal((b.Id, QueueItemStatus.Waiting, (Guid?)null), (transferida.QueueId, transferida.Status, transferida.AssignedUserId));
        Assert.Null((await c.SessaoAsync(primeiro.SessionId))!.ResponderRef); // a conversa de B não foi reservada por quem pediu a de A
    }

    [Fact]
    public async Task ClaimNext_da_fila_A_sem_outra_candidata_devolve_nada_quando_a_unica_saiu_para_B()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "claimsaiu2");
        var a = await c.CriarFilaAsync("Fila A");
        var b = await c.CriarFilaAsync("Fila B");
        var supervisor = await c.AgenteNaFilaAsync(banco, a.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, a.Id);
        await c.AdicionarAtendenteAsync(b.Id, ana.Id, QueueMemberRole.Agent);
        var unica = await c.NaFilaAsync(await c.NovaConversaAsync(), a.Id);

        var resultado = await c.ComoAsync(ana.Contexto, async sp =>
        {
            var (itens, controle) = Interceptador<IQueueItemRepository>.Envolver(sp.GetRequiredService<IQueueItemRepository>());
            controle.Depois(nameof(IQueueItemRepository.ListWaitingAsync), () => c.TransferirParaFilaAsync(unica.Id, b.Id, null, supervisor.Contexto));
            return await ServicoCom(sp, itens).ClaimNextAsync(a.Id, Guid.NewGuid());
        });

        Assert.Null(resultado);
        Assert.Null((await c.SessaoAsync(unica.SessionId))!.ResponderRef);
    }

    // N02

    [Fact]
    public async Task O_mesmo_identificador_repetido_ao_mesmo_tempo_com_uma_vaga_so_converge_para_o_resultado_do_primeiro_em_vez_de_recusar_por_limite()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "claimvaga");
        var fila = await c.CriarFilaAsync();
        await c.ConfigurarFilaAsync(fila.Id, limite: 1);
        var ana = await c.AgenteDisponivelAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var pedido = Guid.NewGuid();

        var segunda = await c.ComoAsync(ana.Contexto, async sp =>
        {
            var (itens, controle) = Interceptador<IQueueItemRepository>.Envolver(sp.GetRequiredService<IQueueItemRepository>());

            // A segunda chamada já leu a conversa em espera quando a PRIMEIRA, com o mesmo identificador, termina inteira e ocupa a única vaga.
            controle.Depois(nameof(IQueueItemRepository.FindItemAsync), async () =>
                Assert.Equal(OperationOutcome.Completed, (await c.AssumirAsync(item.Id, ana.Contexto, pedido)).Outcome));
            return await ServicoCom(sp, itens).ClaimAsync(item.Id, pedido);
        });

        // Converge para o resultado da primeira: não é "sem vaga" (a vaga é dela mesma) e não atribui duas vezes.
        Assert.Equal(item.Id, segunda.Item.Id);
        Assert.Equal((QueueItemStatus.Assigned, ana.Id), (segunda.Item.Status, segunda.Item.AssignedUserId));
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
        Assert.Single((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind == HistoryKind.Assigned);
    }

    [Fact]
    public async Task ClaimNext_com_o_mesmo_identificador_e_uma_vaga_converge_para_a_conversa_do_primeiro_e_nao_assume_outra()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "claimvaga2");
        var fila = await c.CriarFilaAsync();
        await c.ConfigurarFilaAsync(fila.Id, limite: 1);
        var ana = await c.AgenteDisponivelAsync(banco, fila.Id);
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var pedido = Guid.NewGuid();

        var segunda = await c.ComoAsync(ana.Contexto, async sp =>
        {
            var (itens, controle) = Interceptador<IQueueItemRepository>.Envolver(sp.GetRequiredService<IQueueItemRepository>());
            controle.Depois(nameof(IQueueItemRepository.FindItemAsync), async () =>
                Assert.NotNull(await c.AssumirProximaAsync(fila.Id, ana.Contexto, pedido)));
            return await ServicoCom(sp, itens).ClaimNextAsync(fila.Id, pedido);
        });

        Assert.NotNull(segunda);
        Assert.Equal(primeiro.Id, segunda.Item.Id);
        Assert.Equal(ana.Id, (await c.SessaoAsync(primeiro.SessionId))!.ResponderRef);
        Assert.Null((await c.SessaoAsync(segundo.SessionId))!.ResponderRef); // nenhuma capacidade a mais foi concedida
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(segundo.Id))!.Status);
    }

    [Fact]
    public async Task Sem_o_recibo_do_mesmo_pedido_o_limite_continua_valendo_para_outro_identificador()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "claimvaga3");
        var fila = await c.CriarFilaAsync();
        await c.ConfigurarFilaAsync(fila.Id, limite: 1);
        var ana = await c.AgenteDisponivelAsync(banco, fila.Id);
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.AssumirAsync(primeiro.Id, ana.Contexto);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.AssumirAsync(segundo.Id, ana.Contexto)); // outro identificador

        Assert.Equal(ContactCenterRule.AgentAtCapacity, recusa.Rule);
    }
}
