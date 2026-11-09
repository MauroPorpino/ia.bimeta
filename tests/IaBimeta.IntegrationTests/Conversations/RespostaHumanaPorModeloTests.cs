using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O lado do Conversations da resposta por MODELO de mensagem (ADR 0027), com PostgreSQL real: o bloco de canal é gravado com a mensagem de saída e devolvido ao Sender, entra na idempotência (o mesmo
/// <c>OperationId</c> com outro bloco é outro efeito), um bloco fora dos limites é recusado sem gravar nada, e a sessão informa o instante da última mensagem do CLIENTE, que o ContactCenter usa para dizer se a
/// janela de resposta livre do provedor está aberta.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RespostaHumanaPorModeloTests(PostgresFixture banco)
{
    private static ChannelData Bloco(string modelo = "resultado_pronto", string nome = "Mauro") => new(1, new Dictionary<string, string>
    {
        ["tpl.id"] = "0197a1b2-0000-7000-8000-000000000001",
        ["tpl.name"] = modelo,
        ["tpl.lang"] = "pt_BR",
        ["tpl.p.body.1"] = nome,
    });

    private static async Task<(CenarioDeConversas C, Guid Sessao, Guid Autor)> HumanoAsync(PostgresFixture banco, string prefixo, DateTimeOffset? quando = null)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var sessao = (await c.EntrarAsync("5511999990311", quando: quando ?? DateTimeOffset.UtcNow.AddHours(-30))).SessionId;
        var autor = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, autor, (await LerAsync(c, sessao)).StateVersion)).Outcome);
        return (c, sessao, autor);
    }

    private static Task<SessionView> LerAsync(CenarioDeConversas c, Guid sessao) =>
        c.ComoAsync(c.ContextoDeSistema, async sp => (await sp.GetRequiredService<IConversationQueries>().GetSessionAsync(sessao))!);

    private static async Task<RegisterHumanReplyResult> ResponderAsync(CenarioDeConversas c, Guid sessao, Guid autor, Guid operacao, ReplyContent conteudo)
    {
        var s = await LerAsync(c, sessao);
        return await c.ComoAsync(
            c.ContextoDeSistema,
            sp => sp.GetRequiredService<IHandlingCommands>().RegisterHumanReplyAsync(new RegisterHumanReplyCommand(
                sessao, operacao, autor, new ReplyPrecondition(s.HandlingPeriod, s.PlacementEpoch, ResponderMatch.Exact, autor), s.StateVersion, 0, conteudo)));
    }

    private static Task<OutboundDispatchView?> DespachoAsync(CenarioDeConversas c, Guid mensagem, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<IOutboundDispatchQueries>().GetAsync(mensagem));

    [Fact]
    public async Task O_bloco_e_gravado_com_a_mensagem_devolvido_ao_Sender_e_o_historico_mostra_o_nome_do_modelo()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "respmodelo");
        var bloco = Bloco();

        var resultado = await ResponderAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent("Olá Mauro, seu resultado está pronto.", null, bloco));

        Assert.Equal(RegisterHumanReplyOutcome.Applied, resultado.Outcome);
        var despacho = await DespachoAsync(c, resultado.MessageId!.Value);
        Assert.Equal("Olá Mauro, seu resultado está pronto.", despacho!.Text);
        Assert.Equal(bloco.Version, despacho.ChannelData!.Version);
        Assert.Equal(bloco.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), despacho.ChannelData.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());

        var historico = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(sessao, 50, null, HistoryOrder.OldestFirst)));
        var humana = Assert.Single(historico!.Items, m => m.Author == MessageAuthor.Human);
        Assert.Equal("resultado_pronto", humana.TemplateName);
        Assert.All(historico.Items.Where(m => m.Author != MessageAuthor.Human), m => Assert.Null(m.TemplateName));
    }

    [Fact]
    public async Task A_resposta_de_texto_livre_continua_sem_bloco()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "resptexto");

        var resultado = await ResponderAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent("Olá!"));

        Assert.Null((await DespachoAsync(c, resultado.MessageId!.Value))!.ChannelData);
    }

    [Fact]
    public async Task O_bloco_entra_na_idempotencia_o_mesmo_efeito_converge_e_outro_bloco_com_o_mesmo_identificador_e_recusado()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "respidem");
        var operacao = Guid.NewGuid();
        var primeira = await ResponderAsync(c, sessao, autor, operacao, new ReplyContent("Olá Mauro.", null, Bloco()));

        // O mesmo pedido (com a ordem dos pares trocada) devolve a mesma mensagem, sem outra gravação.
        var invertido = new ChannelData(1, Bloco().Values.Reverse().ToDictionary(p => p.Key, p => p.Value));
        var repetida = await ResponderAsync(c, sessao, autor, operacao, new ReplyContent("Olá Mauro.", null, invertido));
        Assert.Equal((RegisterHumanReplyOutcome.AlreadyApplied, primeira.MessageId), (repetida.Outcome, repetida.MessageId));

        // O mesmo identificador com outro parâmetro, sem bloco ou com outro modelo é OUTRO efeito: recusado, sem expor o original.
        foreach (var outro in new[]
        {
            new ReplyContent("Olá Mauro.", null, Bloco(nome: "Pedro")),
            new ReplyContent("Olá Mauro.", null, Bloco(modelo: "outro_modelo")),
            new ReplyContent("Olá Mauro.", null, new ChannelData(2, Bloco().Values)),
            new ReplyContent("Olá Mauro."),
        })
        {
            Assert.Equal(RegisterHumanReplyOutcome.OperationIdReused, (await ResponderAsync(c, sessao, autor, operacao, outro)).Outcome);
        }

        var historico = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(sessao, 50, null, HistoryOrder.OldestFirst)));
        Assert.Single(historico!.Items, m => m.Author == MessageAuthor.Human);
    }

    [Fact]
    public async Task Bloco_fora_dos_limites_e_recusado_e_nada_e_gravado()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "respinvalido");

        foreach (var invalido in new[]
        {
            new ChannelData(0, new Dictionary<string, string> { ["tpl.name"] = "a" }),
            new ChannelData(1, new Dictionary<string, string> { ["Tpl Name"] = "a" }),
            new ChannelData(1, new Dictionary<string, string> { ["tpl.name"] = new string('x', 1025) }),
            new ChannelData(1, Enumerable.Range(0, 33).ToDictionary(i => $"k{i}", _ => "v")),
            new ChannelData(1, Enumerable.Range(0, 9).ToDictionary(i => $"k{i}", _ => new string('x', 1000))),
        })
        {
            Assert.Equal(RegisterHumanReplyOutcome.ContentRejected, (await ResponderAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent("Olá.", null, invalido))).Outcome);
        }

        var historico = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(sessao, 50, null, HistoryOrder.OldestFirst)));
        Assert.DoesNotContain(historico!.Items, m => m.Author == MessageAuthor.Human);
    }

    [Fact]
    public async Task O_despacho_de_um_tenant_nao_e_lido_por_outro()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "respiso");
        var outro = await CenarioDeConversas.CriarAsync(banco, "respiso2");
        var resultado = await ResponderAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent("Olá Mauro.", null, Bloco()));

        Assert.NotNull(await DespachoAsync(c, resultado.MessageId!.Value));
        Assert.Null(await DespachoAsync(outro, resultado.MessageId!.Value));
    }

    // A janela de resposta livre: a última mensagem do cliente

    [Fact]
    public async Task A_sessao_informa_o_instante_da_ultima_mensagem_do_cliente_pelo_relogio_do_provedor_e_a_resposta_humana_nao_o_muda()
    {
        var recente = DateTimeOffset.UtcNow.AddHours(-2);
        var antiga = DateTimeOffset.UtcNow.AddHours(-40);
        var (c, sessao, autor) = await HumanoAsync(banco, "respjanela", recente);

        // Uma mensagem do cliente que CHEGA depois mas é mais antiga no relógio do provedor (atraso do webhook) não adianta o instante.
        await c.EntrarAsync("5511999990311", evento: "evt-antigo", quando: antiga);
        var s = await LerAsync(c, sessao);
        Assert.InRange((s.LastInboundAt!.Value - recente).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(5));

        await ResponderAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent("Olá!"));
        Assert.Equal(s.LastInboundAt, (await LerAsync(c, sessao)).LastInboundAt);

        // O cliente escreve de novo: a janela reabre a partir dessa mensagem.
        var agora = DateTimeOffset.UtcNow;
        await c.EntrarAsync("5511999990311", evento: "evt-novo", quando: agora);
        Assert.InRange(((await LerAsync(c, sessao)).LastInboundAt!.Value - agora).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task O_instante_so_vem_na_leitura_de_uma_sessao_e_nao_na_listagem_nem_no_json_da_API()
    {
        var (c, sessao, _) = await HumanoAsync(banco, "respjanela2");

        var pagina = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationQueries>().ListSessionsAsync(new ListSessionsQuery(50, null, null)));

        Assert.NotNull((await LerAsync(c, sessao)).LastInboundAt);
        Assert.All(pagina.Items, item => Assert.Null(item.LastInboundAt));
        Assert.DoesNotContain("lastInboundAt", System.Text.Json.JsonSerializer.Serialize(await LerAsync(c, sessao), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);
    }
}
