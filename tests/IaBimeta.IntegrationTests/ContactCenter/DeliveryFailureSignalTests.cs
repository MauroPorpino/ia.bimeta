using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// O sinal de FALHA DE ENVIO na fila (SND-7): o ContactCenter consome <c>OutboundMessageFailedV1</c> e o item ativo da conversa passa a mostrar o contador e o instante, para o atendente ver e responder de novo.
/// É sinal de monitoramento: não avança a versão do item (nunca disputa com uma operação), conta uma vez por evento (inbox), nunca cruza tenants e sai da fila quando o atendente responde de novo. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeliveryFailureSignalTests(PostgresFixture banco)
{
    private const string Consumidor = "teste.falha-de-envio";

    private static Task<HandlerResult> ProjetarAsync(IServiceProvider sp, CenarioDeConversas c, Guid sessao, Guid evento) =>
        sp.GetRequiredService<InboxGate<ContactCenterDbContext>>().RunOnceAsync(
            c.Tenant.TenantId, Consumidor, evento,
            async token =>
            {
                await sp.GetRequiredService<ConversationProjectionService>().ApplyOutboundMessageFailedAsync(
                    new OutboundMessageFailedV1(Guid.NewGuid(), sessao, 1, "send-failed.relay-dead"), evento, token);
                return HandlerResult.Success;
            },
            default);

    private static Task Projetar(CenarioDeConversas c, Guid sessao, Guid evento) => c.ComoAsync(c.ContextoDeSistema, sp => ProjetarAsync(sp, c, sessao, evento));

    [Fact]
    public async Task A_falha_de_envio_sinaliza_o_item_ativo_sem_avancar_a_versao_e_cada_evento_conta_uma_vez()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ccfalha");
        var fila = await c.CriarFilaAsync();
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var antes = (await c.ItemAsync(item.Id))!;
        Assert.Equal(0, antes.DeliveryFailures);
        Assert.Null(antes.LastDeliveryFailureAt);

        var evento = Guid.NewGuid();
        await Projetar(c, item.SessionId, evento);
        await Projetar(c, item.SessionId, evento); // a mesma entrega repetida (o relay entrega pelo menos uma vez)

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal(1, depois.DeliveryFailures);
        Assert.NotNull(depois.LastDeliveryFailureAt);
        Assert.Equal(antes.Version, depois.Version); // sinal de monitoramento: não entra na máquina de estados do item
        Assert.Equal(1, await c.RegistrosDoInboxAsync(evento));

        await Projetar(c, item.SessionId, Guid.NewGuid()); // outra mensagem falhou
        Assert.Equal(2, (await c.ItemAsync(item.Id))!.DeliveryFailures);
    }

    [Fact]
    public async Task Sessao_sem_item_ativo_ou_com_o_item_encerrado_nao_e_sinalizada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ccfalhasem");
        var fila = await c.CriarFilaAsync();
        var semItem = await c.NovaConversaAsync();
        await Projetar(c, semItem, Guid.NewGuid()); // nada a sinalizar (a conversa nunca entrou na fila), e nada quebra

        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var encerramento = Guid.NewGuid();
        await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ConversationProjectionService>().ApplySessionClosedAsync(new SessionClosedV1(item.SessionId, "manual"), encerramento));
        Assert.Equal(QueueItemStatus.Closed, (await c.ItemAsync(item.Id))!.Status);

        await Projetar(c, item.SessionId, Guid.NewGuid());

        Assert.Equal(0, (await c.ItemAsync(item.Id))!.DeliveryFailures);
    }

    [Fact]
    public async Task O_sinal_de_um_tenant_nunca_aparece_no_item_de_outro()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "ccfalhaa");
        var b = await CenarioDeConversas.CriarAsync(banco, "ccfalhab");
        var itemA = await a.NaFilaAsync(await a.NovaConversaAsync(), (await a.CriarFilaAsync()).Id);
        var itemB = await b.NaFilaAsync(await b.NovaConversaAsync(), (await b.CriarFilaAsync()).Id);

        // O evento de uma sessão do tenant A chega ao contexto do tenant B (por engano ou ataque): o item de B não é tocado, e o de A também não.
        await Projetar(b, itemA.SessionId, Guid.NewGuid());

        Assert.Equal(0, (await a.ItemAsync(itemA.Id))!.DeliveryFailures);
        Assert.Equal(0, (await b.ItemAsync(itemB.Id))!.DeliveryFailures);

        await Projetar(a, itemA.SessionId, Guid.NewGuid());

        Assert.Equal(1, (await a.ItemAsync(itemA.Id))!.DeliveryFailures);
        Assert.Equal(0, (await b.ItemAsync(itemB.Id))!.DeliveryFailures);
    }

    [Fact]
    public async Task Limpar_o_sinal_zera_contador_e_instante_sem_avancar_a_versao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ccfalhalimpa");
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), (await c.CriarFilaAsync()).Id);
        await Projetar(c, item.SessionId, Guid.NewGuid());
        var sinalizado = (await c.ItemAsync(item.Id))!;
        Assert.Equal(1, sinalizado.DeliveryFailures);

        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IQueueItemRepository>().ClearDeliveryFailuresAsync(item.Id, default);
            return 0;
        });

        var limpo = (await c.ItemAsync(item.Id))!;
        Assert.Equal(0, limpo.DeliveryFailures);
        Assert.Null(limpo.LastDeliveryFailureAt);
        Assert.Equal(sinalizado.Version, limpo.Version);
    }
}
