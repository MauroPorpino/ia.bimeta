using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// O identificador de cada pedido (<c>requestId</c>) é a chave de idempotência de UMA operação: ligado ao tenant, à operação, à fila ou
/// ao item e ao autor. A reutilização para outra coisa nunca devolve nem conclui o que era da primeira, o reenvio do autor original
/// funciona mesmo depois de a operação ter mudado as permissões dele, e uma operação que não mudou nada também deixa recibo, para que
/// um pedido repetido ou atrasado não desfaça o que veio depois. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperationReceiptTests(PostgresFixture banco)
{
    // Achado 1: o identificador preso ao tenant, à operação, à fila e ao autor

    [Fact]
    public async Task O_identificador_de_outra_fila_ou_de_outro_atendente_nao_devolve_nem_conclui_a_operacao_alheia()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcvaza");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var beto = await c.AgenteNaFilaAsync(banco, filaB.Id);
        var itemA = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        var itemB = await c.NaFilaAsync(await c.NovaConversaAsync(), filaB.Id);
        var pedido = Guid.NewGuid();
        await c.AssumirProximaAsync(filaA.Id, ana.Contexto, pedido);

        // O Beto só enxerga a fila B: o mesmo identificador, de outra fila, não lhe entrega o item da fila A.
        var reuso = await Assert.ThrowsAsync<QueueItemConflictException>(() => c.AssumirProximaAsync(filaB.Id, beto.Contexto, pedido));

        Assert.Equal(QueueItemConflict.RequestIdReused, reuso.Conflict);
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(itemB.Id))!.Status);
        Assert.Null((await c.SessaoAsync(itemB.SessionId))!.ResponderRef);
        Assert.Equal(ana.Id, (await c.ItemAsync(itemA.Id))!.AssignedUserId);
    }

    [Fact]
    public async Task O_identificador_com_operacao_pendente_de_outro_atendente_nao_e_concluido_por_quem_nao_o_pediu()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcpendente");
        var fila = await c.CriarFilaAsync("Fila A");
        var outra = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, outra.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.NaFilaAsync(await c.NovaConversaAsync(), outra.Id);
        var pedidoDaAna = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        var reuso = await Assert.ThrowsAsync<QueueItemConflictException>(() => c.AssumirProximaAsync(outra.Id, beto.Contexto, pedidoDaAna));

        Assert.Equal(QueueItemConflict.RequestIdReused, reuso.Conflict);
        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Waiting, PendingKind.Assign, ana.Id), (depois.Status, depois.Pending!.Kind, depois.Pending.UserId)); // continua do jeito que a Ana deixou
        Assert.Null((await c.SessaoAsync(item.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task O_mesmo_atendente_nao_reusa_o_identificador_em_outra_fila_nem_em_outra_operacao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcreuso");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        await c.AdicionarAtendenteAsync(filaB.Id, ana.Id);
        var itemA = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        var itemB = await c.NaFilaAsync(await c.NovaConversaAsync(), filaB.Id);
        var pedido = Guid.NewGuid();
        await c.AssumirProximaAsync(filaA.Id, ana.Contexto, pedido);

        Assert.Equal(QueueItemConflict.RequestIdReused, (await Assert.ThrowsAsync<QueueItemConflictException>(() => c.AssumirProximaAsync(filaB.Id, ana.Contexto, pedido))).Conflict);
        Assert.Equal(QueueItemConflict.RequestIdReused, (await Assert.ThrowsAsync<QueueItemConflictException>(() => c.AssumirAsync(itemB.Id, ana.Contexto, pedido))).Conflict);
        Assert.Equal(QueueItemConflict.RequestIdReused, (await Assert.ThrowsAsync<QueueItemConflictException>(() => c.DevolverAFilaAsync(itemA.Id, ana.Contexto, pedido))).Conflict);
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(itemB.Id))!.Status);
        Assert.Equal(ana.Id, (await c.ItemAsync(itemA.Id))!.AssignedUserId);
    }

    // Achado 2: o mesmo identificador nunca atribui duas sessões, mesmo em paralelo

    [Fact]
    public async Task O_mesmo_identificador_em_paralelo_atribui_no_maximo_uma_conversa()
    {
        for (var rodada = 0; rodada < 6; rodada++)
        {
            var c = await CenarioDeConversas.CriarAsync(banco, $"rcparalelo{rodada}");
            var fila = await c.CriarFilaAsync();
            var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
            var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
            var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
            var pedido = Guid.NewGuid();

            using var largada = new ManualResetEventSlim(false);
            var tentativas = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                largada.Wait();
                try
                {
                    return (Resultado: await c.AssumirProximaAsync(fila.Id, ana.Contexto, pedido), Erro: (Exception?)null);
                }
                catch (Exception ex) when (ex is QueueItemConflictException or ContactCenterConflictException)
                {
                    return (Resultado: (QueueItemOperationResult?)null, Erro: ex);
                }
            })).ToList();
            largada.Set();
            await Task.WhenAll(tentativas);

            var atribuidas = new[] { primeiro, segundo }.Select(i => c.ItemAsync(i.Id).Result!).Where(i => i.Status == QueueItemStatus.Assigned).ToList();
            Assert.Single(atribuidas);
            var sessoesComAna = 0;
            foreach (var item in new[] { primeiro, segundo })
            {
                sessoesComAna += (await c.SessaoAsync(item.SessionId))!.ResponderRef == ana.Id ? 1 : 0;
            }

            Assert.Equal(1, sessoesComAna);
            Assert.Equal(1, new[] { primeiro, segundo }.Sum(i => c.HistoricoDoItemAsync(i.Id).Result!.Count(h => h.OperationId == pedido)));
        }
    }

    // Achado 4: recibo também para o que não mudou nada, e o pedido repetido ou atrasado nunca desfaz o que veio depois

    [Fact]
    public async Task O_pedido_de_devolver_que_nao_mudou_nada_deixa_recibo_e_repeti_lo_depois_nao_desfaz_a_atribuicao_seguinte()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcrecibo");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var pedido = Guid.NewGuid();

        var semEfeito = await c.DevolverAFilaAsync(item.Id, supervisor.Contexto, pedido); // a conversa já esperava: nada a devolver
        await c.AssumirAsync(item.Id, beto.Contexto);
        var repetido = await c.DevolverAFilaAsync(item.Id, supervisor.Contexto, pedido);   // o mesmo pedido, depois da atribuição ao Beto

        Assert.Equal((OperationOutcome.AlreadyApplied, OperationOutcome.AlreadyApplied), (semEfeito.Outcome, repetido.Outcome));
        Assert.Equal((QueueItemStatus.Assigned, beto.Id), ((await c.ItemAsync(item.Id))!.Status, (await c.ItemAsync(item.Id))!.AssignedUserId));
        Assert.Equal(beto.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task O_pedido_de_assumir_que_ja_era_do_atendente_deixa_recibo_e_repeti_lo_depois_nao_reatribui_o_que_foi_devolvido()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcrecibo2");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        var pedido = Guid.NewGuid();

        var semEfeito = await c.AssumirAsync(item.Id, ana.Contexto, pedido);     // já era dela
        await c.DevolverAFilaAsync(item.Id, supervisor.Contexto);                // o supervisor tira a conversa dela
        var repetido = await c.AssumirAsync(item.Id, ana.Contexto, pedido);      // a resposta se perdeu e a Ana repete o pedido antigo

        Assert.Equal((OperationOutcome.AlreadyApplied, OperationOutcome.AlreadyApplied), (semEfeito.Outcome, repetido.Outcome));
        Assert.Equal((QueueItemStatus.Waiting, null), ((await c.ItemAsync(item.Id))!.Status, (await c.ItemAsync(item.Id))!.AssignedUserId));
        Assert.Null((await c.SessaoAsync(item.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task O_executor_atrasado_nao_aplica_no_Conversations_uma_operacao_ja_cancelada_nem_desfaz_a_atribuicao_posterior()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcatrasado");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            // O executor (por exemplo, um pedido lento) já leu o item com a operação da Ana pendente...
            var atrasado = (await sp.GetRequiredService<IQueueItemRepository>().FindItemAsync(item.Id, default))!;

            // ...e, enquanto ele espera, a Ana sai da fila e o job cancela a operação, o Beto assume a conversa e a Ana volta à fila.
            await c.RemoverAtendenteAsync(fila.Id, ana.Id);
            await c.ReconciliarAsync();
            await c.AssumirAsync(item.Id, beto.Contexto);
            await c.AdicionarAtendenteAsync(fila.Id, ana.Id);

            // O executor atrasado acorda: a operação dele já não existe. Não pode chamar o Conversations.
            try
            {
                await sp.GetRequiredService<HandlingExecutor>().DriveAsync(atrasado, default);
            }
            catch (Exception ex) when (ex is ContactCenterConflictException or ContactCenterRuleException or QueueItemConflictException)
            {
                // Recusar é aceitável; o que não é aceitável é o efeito remoto abaixo.
            }

            return 0;
        });

        Assert.Equal(beto.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
        Assert.Equal(beto.Id, (await c.ItemAsync(item.Id))!.AssignedUserId);
    }

    // Achado 5: a retirada do atendente não cancela uma intenção cujo efeito já foi aplicado

    [Fact]
    public async Task Intencao_cujo_efeito_ja_foi_aplicado_no_Conversations_nao_e_cancelada_porque_o_atendente_saiu_da_fila()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcefeito");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        var operacao = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        // O Conversations já aplicou a transição da operação, o processo caiu antes de concluir, e depois a Ana saiu da fila.
        await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, ana.Id, await c.VersaoDaSessaoAsync(sessao), operacao);
        await c.RemoverAtendenteAsync(fila.Id, ana.Id);

        await c.ReconciliarAsync();

        // O efeito remoto existe: o ContactCenter o reconhece e reconcilia, em vez de cancelar e divergir.
        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, ana.Id, null), (depois.Status, depois.AssignedUserId, depois.Pending));
        Assert.Equal(ana.Id, (await c.SessaoAsync(sessao))!.ResponderRef);
        Assert.Equal((HistoryKind.Assigned, operacao), ((await c.HistoricoDoItemAsync(item.Id))!.Last().Kind, (await c.HistoricoDoItemAsync(item.Id))!.Last().OperationId));
        Assert.DoesNotContain(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Sem_efeito_remoto_a_retirada_do_atendente_continua_cancelando_a_intencao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcsemefeito");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);
        await c.RemoverAtendenteAsync(fila.Id, ana.Id);

        await c.ReconciliarAsync();

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Waiting, null, null), (depois.Status, depois.AssignedUserId, depois.Pending));
        Assert.Null((await c.SessaoAsync(sessao))!.ResponderRef);
    }

    // Achado 9: o reenvio do autor original, mesmo depois de a operação mudar as permissões dele

    [Fact]
    public async Task O_autor_original_repete_o_pedido_depois_de_perder_o_acesso_e_recebe_o_resultado_gravado_sem_ganhar_acesso_novo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcreenvio");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var beto = await c.AgenteNaFilaAsync(banco, filaB.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        var pedido = Guid.NewGuid();

        var original = await c.TransferirParaFilaAsync(item.Id, filaB.Id, null, ana.Contexto, pedido); // a Ana sai da conversa: não é da fila B
        Assert.Null(await c.ItemAsync(item.Id, ana.Contexto));                                       // ela já não enxerga o item

        var repetido = await c.TransferirParaFilaAsync(item.Id, filaB.Id, null, ana.Contexto, pedido);

        // A repetição devolve o que o pedido original já tinha dado a ela (a mesma resposta), sem refazer nada...
        Assert.Equal((OperationOutcome.AlreadyApplied, original.Item.Id, filaB.Id), (repetido.Outcome, repetido.Item.Id, repetido.Item.QueueId));
        Assert.Single((await c.HistoricoDoItemAsync(item.Id))!, h => h.OperationId == pedido);

        // ...e não abre nada novo: operação nova dela sobre o item continua sendo de quem não o enxerga, e o pedido de outro autor é recusado.
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.DevolverAFilaAsync(item.Id, ana.Contexto));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.TransferirParaFilaAsync(item.Id, filaA.Id, null, ana.Contexto));
        Assert.Equal(QueueItemConflict.RequestIdReused, (await Assert.ThrowsAsync<QueueItemConflictException>(() => c.TransferirParaFilaAsync(item.Id, filaB.Id, null, beto.Contexto, pedido))).Conflict);
        Assert.Equal(filaB.Id, (await c.ItemAsync(item.Id))!.QueueId);
    }

    [Fact]
    public async Task O_pedido_de_assumir_repetido_pelo_autor_original_depois_de_a_conversa_sair_da_fila_dele_nao_assume_de_novo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcreenvio2");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var supervisor = await c.AgenteNaFilaAsync(banco, filaA.Id, QueueMemberRole.Supervisor);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        var pedido = Guid.NewGuid();
        var primeira = await c.AssumirProximaAsync(filaA.Id, ana.Contexto, pedido);
        await c.TransferirParaFilaAsync(item.Id, filaB.Id, null, supervisor.Contexto);

        var repetida = await c.AssumirProximaAsync(filaA.Id, ana.Contexto, pedido);

        Assert.Equal((OperationOutcome.Completed, OperationOutcome.AlreadyApplied), (primeira!.Outcome, repetida!.Outcome));
        Assert.Equal(item.Id, repetida.Item.Id);
        Assert.Equal((filaB.Id, QueueItemStatus.Waiting), ((await c.ItemAsync(item.Id))!.QueueId, (await c.ItemAsync(item.Id))!.Status)); // a repetição não puxou a conversa de volta
    }
}
