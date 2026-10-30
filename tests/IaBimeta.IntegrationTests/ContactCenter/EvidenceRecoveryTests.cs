using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// O efeito reconhecido por EVIDÊNCIA (a intenção não tem a base completa, e nenhum comando é emitido) conclui a operação localmente, mas NÃO cobre a obrigação de
/// ressincronização: cobrir exige a prova correlacionada do efeito E que ele cobre as identidades relevantes, e sem a geração da colocação na base a colocação não
/// é uma identidade comprovada. A obrigação continua aberta, e a recuperação alinha o item à sessão. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EvidenceRecoveryTests(PostgresFixture banco)
{
    [Fact]
    public async Task O_efeito_reconhecido_por_evidencia_sem_a_geracao_na_base_conclui_a_operacao_mas_nao_cobre_a_obrigacao()
    {
        banco.RelogioDaMensageria.Sincronizar();
        var c = await CenarioDeConversas.CriarAsync(banco, "evid1", canaisReais: true, mensageria: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var canal = await c.CriarCanalAsync(new[] { matriz });
        var fila = await c.CriarFilaAsync("A", unidade: matriz);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var dora = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync(unidade: matriz, canal: canal);
        var item = (await c.AssumirAsync((await c.NaFilaAsync(sessao, fila.Id)).Id, ana.Contexto)).Item;
        await c.RodarRelayAsync();

        // A intenção de atribuir à Dora, gravada sem a geração da colocação na base (anterior ao campo): o período e o responsável da base são conhecidos.
        var pendente = await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var rastreado = (await itens.FindItemAsync(item.Id, default))!;
            var visao = (await sp.GetRequiredService<IConversationGateway>().GetSessionAsync(sessao, default))!;
            rastreado.BeginAssign(dora.Id, Guid.NewGuid(), dora.Id, "claim", DateTimeOffset.UtcNow, basis: new HandlingBasis(visao.HandlingPeriod, visao.ResponderRef, false, null));
            await itens.SaveChangesAsync(default);
            return rastreado.Pending!;
        });

        // Por fora, a Ana passa o atendimento ao Beto e o recebe de volta: os eventos chegam com a operação pendente e a obrigação fica aberta.
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, beto.Id, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, ana.Id, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versaoExterna = await c.VersaoDaSessaoAsync(sessao);
        await c.RodarRelayAsync();
        Func<Task<QueueItem>> linha = () => c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Items.AsNoTracking().SingleAsync(i => i.Id == item.Id));
        Assert.Equal(versaoExterna, (await linha()).ResyncStateVersion);

        // O Conversations JÁ aplicou a transição da intenção (a resposta se perdeu): o registro existe, com o identificador que a intenção guardou, e a versão é maior que a marca.
        var remoto = await c.ComoAsync(c.ContextoDeSistema, async sp => await sp.GetRequiredService<IConversationGateway>().TransitionAsync(
            new TransitionHandlingModeCommand(sessao, HandlingMode.HumanAssisted, dora.Id, (await c.SessaoAsync(sessao))!.StateVersion, pendente.TransitionId, "claim"), default));
        Assert.Equal(TransitionOutcome.Applied, remoto.Outcome);

        // O executor, sem autoridade para escrever, reconhece o efeito pela evidência e conclui o item localmente...
        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var rastreado = await sp.GetRequiredService<IQueueItemRepository>().FindItemAsync(item.Id, default);
            Assert.Equal(OperationOutcome.Completed, (await sp.GetRequiredService<HandlingExecutor>().DriveAsync(rastreado!, default)).Outcome);
            return 0;
        });
        var concluido = await linha();
        Assert.Equal((QueueItemStatus.Assigned, dora.Id), (concluido.Status, concluido.AssignedUserId));

        // ... mas a obrigação NÃO foi coberta: a colocação do efeito não é uma identidade comprovada (a base não trazia a geração). Continua aberta, e a
        // recuperação alinha o item ao que a sessão tem.
        Assert.Equal((versaoExterna, true), (concluido.ResyncStateVersion, concluido.NeedsResync));
        Assert.True(await c.ReconciliarAsync() >= 1);
        var final = await linha();
        Assert.Equal((null, false, dora.Id), (final.ResyncStateVersion, final.NeedsResync, final.AssignedUserId));
    }
}
