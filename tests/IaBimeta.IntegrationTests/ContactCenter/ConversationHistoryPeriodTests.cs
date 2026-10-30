using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// O acesso ao conteúdo de um atendimento vale para o PERÍODO em que a conversa esteve na fila: depois que o item foi encerrado (a
/// conversa voltou para a IA, ou foi para outra fila ou unidade), quem o enxerga pela fila antiga lê as mensagens até o encerramento e
/// nada do que veio depois, nem o estado atual da sessão. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationHistoryPeriodTests(PostgresFixture banco)
{
    private static readonly string[] Periodo = { "antes do handoff", "durante o atendimento" };

    private async Task<(CenarioDeConversas C, QueueItemView Item, Agente Supervisor)> ItemEncerradoAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var contato = ContactCenterTestSupport.Unico("contato");
        var item = await c.NaFilaAsync((await c.EntrarAsync(contato, texto: Periodo[0])).SessionId, fila.Id);
        await Task.Delay(40);
        await c.EntrarAsync(contato, texto: Periodo[1]);
        await Task.Delay(40);
        await c.DevolverAIAAsync(item.Id, c.ContextoDeSistema);
        await Task.Delay(40);

        // A conversa segue com a IA (e poderia ter ido para outra fila ou unidade): nada disso é do período deste item.
        await c.EntrarAsync(contato, texto: "depois, com a IA");
        await c.EntrarAsync(contato, texto: "mais tarde ainda");
        return (c, item, supervisor);
    }

    private static async Task<List<string?>> LerTudoAsync(CenarioDeConversas c, Guid item, RequestContext contexto, HistoryOrder ordem, int tamanho)
    {
        var textos = new List<string?>();
        string? cursor = null;
        for (var pagina = 0; pagina < 20; pagina++)
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

    [Theory]
    [InlineData(HistoryOrder.OldestFirst, 50)]
    [InlineData(HistoryOrder.NewestFirst, 50)]
    [InlineData(HistoryOrder.OldestFirst, 1)]
    [InlineData(HistoryOrder.NewestFirst, 1)]
    public async Task O_item_encerrado_so_mostra_as_mensagens_do_periodo_dele(HistoryOrder ordem, int tamanho)
    {
        var (c, item, supervisor) = await ItemEncerradoAsync($"hpmsg{(int)ordem}{tamanho}");

        var textos = await LerTudoAsync(c, item.Id, supervisor.Contexto, ordem, tamanho);

        var esperado = ordem == HistoryOrder.OldestFirst ? Periodo : Periodo.Reverse().ToArray();
        Assert.Equal(esperado, textos);
    }

    [Fact]
    public async Task O_item_encerrado_nao_mostra_o_estado_atual_da_sessao_que_ja_nao_e_do_periodo_dele()
    {
        var (c, item, supervisor) = await ItemEncerradoAsync("hpsessao");

        Assert.Null(await c.ConversaDoItemAsync(item.Id, supervisor.Contexto));
    }

    [Fact]
    public async Task O_item_aberto_continua_mostrando_a_conversa_inteira_com_o_contexto_anterior_a_entrada_na_fila()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hpaberto");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes da fila")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.EntrarAsync(contato, texto: "depois da entrada");

        var textos = await LerTudoAsync(c, item.Id, supervisor.Contexto, HistoryOrder.OldestFirst, 50);

        Assert.Equal(new[] { "antes da fila", "depois da entrada" }, textos);
        Assert.Equal(sessao, (await c.ConversaDoItemAsync(item.Id, supervisor.Contexto))!.Id);
    }
}
