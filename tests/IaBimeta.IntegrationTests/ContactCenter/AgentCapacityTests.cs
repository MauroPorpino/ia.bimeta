using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// O limite de conversas simultâneas por atendente conta também as atribuições ainda pendentes (a intenção já gravada, que o
/// Conversations ainda não confirmou) e vale mesmo quando o mesmo atendente assume várias conversas ao mesmo tempo: a conferência do
/// limite e a gravação da intenção acontecem sob a mesma trava do banco. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentCapacityTests(PostgresFixture banco)
{
    [Fact]
    public async Task A_atribuicao_pendente_conta_no_limite_do_atendente_e_na_disponibilidade_dele()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "capacpendente");
        var fila = await c.CriarFilaAsync();
        await c.ConfigurarFilaAsync(fila.Id, limite: 1);
        var ana = await c.AgenteDisponivelAsync(banco, fila.Id);
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.GravarIntencaoDeAssumirAsync(primeiro.Id, ana.Id); // o Conversations ainda não confirmou: a Ana já ocupa a vaga dela

        Assert.Equal(0, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents);
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.AssumirAsync(segundo.Id, ana.Contexto));

        Assert.Equal(ContactCenterRule.AgentAtCapacity, recusa.Rule);
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(segundo.Id))!.Status);
        Assert.Null((await c.SessaoAsync(segundo.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task A_transferencia_pendente_para_o_atendente_conta_no_limite_da_fila_de_destino()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "capactransfer");
        var origem = await c.CriarFilaAsync("Origem");
        var destino = await c.CriarFilaAsync("Destino");
        await c.ConfigurarFilaAsync(destino.Id, limite: 1);
        var ana = await c.AgenteDisponivelAsync(banco, destino.Id);
        var viajante = await c.NaFilaAsync(await c.NovaConversaAsync(), origem.Id);
        var local = await c.NaFilaAsync(await c.NovaConversaAsync(), destino.Id);
        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            (await itens.FindItemAsync(viajante.Id, default))!.BeginTransferToQueue(destino.Id, ana.Id, null, null, Guid.NewGuid(), null, "transfer", DateTimeOffset.UtcNow);
            await itens.SaveChangesAsync(default);
            return 0;
        });

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.AssumirAsync(local.Id, ana.Contexto));

        Assert.Equal(ContactCenterRule.AgentAtCapacity, recusa.Rule);
    }

    [Fact]
    public async Task O_mesmo_atendente_assumindo_varias_conversas_ao_mesmo_tempo_nunca_passa_do_limite()
    {
        for (var rodada = 0; rodada < 5; rodada++)
        {
            var c = await CenarioDeConversas.CriarAsync(banco, $"capacparalelo{rodada}");
            var fila = await c.CriarFilaAsync();
            await c.ConfigurarFilaAsync(fila.Id, limite: 2);
            var ana = await c.AgenteDisponivelAsync(banco, fila.Id);
            var itens = new List<QueueItemView>();
            for (var i = 0; i < 6; i++)
            {
                itens.Add(await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id));
            }

            using var largada = new ManualResetEventSlim(false);
            var tentativas = itens.Select(item => Task.Run(async () =>
            {
                largada.Wait();
                try
                {
                    await c.AssumirAsync(item.Id, ana.Contexto);
                }
                catch (Exception ex) when (ex is ContactCenterRuleException or QueueItemConflictException or ContactCenterConflictException)
                {
                    // Recusar é o esperado para o que passa do limite.
                }
            })).ToList();
            largada.Set();
            await Task.WhenAll(tentativas);

            var atribuidas = new List<QueueItemView>();
            foreach (var item in itens)
            {
                var atual = (await c.ItemAsync(item.Id))!;
                if (atual.Status == QueueItemStatus.Assigned || atual.Pending is not null)
                {
                    atribuidas.Add(atual);
                }
            }

            Assert.Equal(2, atribuidas.Count);
            var sessoesComAna = 0;
            foreach (var item in itens)
            {
                sessoesComAna += (await c.SessaoAsync(item.SessionId))!.ResponderRef == ana.Id ? 1 : 0;
            }

            Assert.Equal(2, sessoesComAna);
        }
    }
}
