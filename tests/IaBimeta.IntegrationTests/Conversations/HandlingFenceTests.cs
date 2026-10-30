using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O lado do Conversations da exclusividade de quem executa operações do ContactCenter: a cerca (<c>fencing token</c>) é validada dentro da
/// trava da sessão, a operação que não muda nada também fica registrada (então a repetição tardia não é reavaliada contra um estado que
/// mudou), e cada transição devolve a sequência da última mensagem naquele instante, que é a fronteira estável do histórico. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HandlingFenceTests(PostgresFixture banco)
{
    private static Task<T> Comandos<T>(CenarioDeConversas c, Func<IHandlingCommands, Task<T>> acao, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => acao(sp.GetRequiredService<IHandlingCommands>()));

    private static Task<FenceHandlingResult> Cercar(CenarioDeConversas c, Guid sessao, long token, RequestContext? contexto = null) =>
        Comandos(c, h => h.FenceHandlingAsync(new FenceHandlingCommand(sessao, token)), contexto);

    private static Task<TransitionResult> Transitar(CenarioDeConversas c, Guid sessao, HandlingMode para, Guid? responsavel, long versao, Guid? id = null, long? cerca = null) =>
        Comandos(c, h => h.TransitionHandlingModeAsync(new TransitionHandlingModeCommand(sessao, para, responsavel, versao, id ?? Guid.NewGuid(), "manual", cerca)));

    // A cerca

    [Fact]
    public async Task A_cerca_so_cresce_o_token_igual_e_o_mesmo_executor_o_menor_foi_substituido()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercamono");
        var sessao = (await c.EntrarAsync("5511999990301")).SessionId;

        var primeira = await Cercar(c, sessao, 10);
        Assert.Equal((FenceOutcome.Fenced, sessao), (primeira.Outcome, primeira.Session!.Id));
        Assert.Equal(FenceOutcome.Fenced, (await Cercar(c, sessao, 10)).Outcome); // o mesmo executor repetindo
        Assert.Equal(FenceOutcome.FencedOut, (await Cercar(c, sessao, 9)).Outcome); // o substituído
        Assert.Null((await Cercar(c, sessao, 9)).Session);
        Assert.Equal(FenceOutcome.Fenced, (await Cercar(c, sessao, 11)).Outcome); // o sucessor
        Assert.Equal(FenceOutcome.FencedOut, (await Cercar(c, sessao, 10)).Outcome); // e o anterior continua fora
    }

    [Fact]
    public async Task O_token_de_cerca_precisa_ser_positivo_e_a_sessao_de_outro_tenant_nao_existe()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercaiso");
        var outro = await CenarioDeConversas.CriarAsync(banco, "cercaiso2");
        var sessao = (await c.EntrarAsync("5511999990302")).SessionId;

        await Assert.ThrowsAsync<ConversationsValidationException>(() => Cercar(c, sessao, 0));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => Cercar(c, sessao, -5));
        Assert.Equal(FenceOutcome.SessionNotFound, (await Cercar(outro, sessao, 10)).Outcome); // TEN-2: a sessão de outro tenant é inexistente
        Assert.Equal(FenceOutcome.SessionNotFound, (await Cercar(c, Guid.NewGuid(), 10)).Outcome);
    }

    [Fact]
    public async Task A_transicao_com_token_menor_que_a_cerca_e_recusada_antes_de_qualquer_regra_e_nada_muda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercatrans");
        var sessao = (await c.EntrarAsync("5511999990303")).SessionId;
        await Cercar(c, sessao, 20);
        var versao = await c.VersaoDaSessaoAsync(sessao);
        var eventos = c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao);

        var antiga = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, versao, cerca: 19);

        Assert.Equal((TransitionOutcome.FencedOut, versao), (antiga.Outcome, antiga.StateVersion));
        Assert.Equal((HandlingMode.AiAutonomous, versao), ((await c.SessaoAsync(sessao))!.HandlingMode, await c.VersaoDaSessaoAsync(sessao)));
        Assert.Equal(eventos, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));

        // O executor vigente (token igual ou maior) escreve, e a cerca dele passa a valer: o de token intermediário já não escreve.
        var vigente = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, versao, cerca: 25);
        Assert.Equal(TransitionOutcome.Applied, vigente.Outcome);
        Assert.Equal(TransitionOutcome.FencedOut, (await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao + 1, cerca: 22)).Outcome);
        Assert.Equal(FenceOutcome.FencedOut, (await Cercar(c, sessao, 24)).Outcome);
    }

    [Fact]
    public async Task O_encaminhamento_com_token_menor_que_a_cerca_e_recusado_e_nao_deixa_rastro()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercatransf");
        var a = await c.CriarUnidadeAsync(c.OrganizacaoId, "A", matriz: true);
        var b = await c.CriarUnidadeAsync(c.OrganizacaoId, "B");
        var sessao = (await c.EntrarAsync("5511999990304", unidade: a)).SessionId;
        await Cercar(c, sessao, 30);
        var versao = await c.VersaoDaSessaoAsync(sessao);

        var antigo = await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versao, Guid.NewGuid(), "queue-transfer", 29)));

        Assert.Equal(TransferOutcome.FencedOut, antigo.Outcome);
        Assert.Equal(a, (await c.SessaoAsync(sessao))!.BusinessUnitId);
        Assert.Empty((await c.RastroAsync(sessao))!);

        var vigente = await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versao, Guid.NewGuid(), "queue-transfer", 30)));
        Assert.Equal(TransferOutcome.Applied, vigente.Outcome);
    }

    [Fact]
    public async Task Sem_token_os_comandos_continuam_funcionando_como_antes()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercasem");
        var sessao = (await c.EntrarAsync("5511999990305")).SessionId;
        await Cercar(c, sessao, 50);

        // Quem não tem executor concorrente não informa token, e a cerca não o atinge (compatibilidade dos outros chamadores).
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
    }

    // A operação que não muda nada também fica registrada

    [Fact]
    public async Task A_transicao_sem_efeito_fica_registrada_e_a_repeticao_tardia_nao_e_reaplicada_contra_o_estado_novo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "semefeito");
        var sessao = (await c.EntrarAsync("5511999990306")).SessionId;
        var antiga = Guid.NewGuid();

        // A sessão já está em IA: a transição não muda nada (e continua devolvendo o mesmo resultado de antes).
        var semEfeito = await Transitar(c, sessao, HandlingMode.AiAutonomous, null, 1, antiga);
        Assert.Equal(TransitionOutcome.InvalidTransition, semEfeito.Outcome);
        Assert.Null(semEfeito.MessageSeq); // a mudança que fez o estado ser este não foi esta: fronteira DESCONHECIDA, nunca a sequência de agora

        // Depois a sessão muda de verdade (humano, sem responsável).
        var humano = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, 1);
        Assert.Equal(TransitionOutcome.Applied, humano.Outcome);

        // Um executor atrasado repete o pedido antigo (agora com a versão atual): NÃO é reavaliado contra o estado de hoje.
        var tardia = await Transitar(c, sessao, HandlingMode.AiAutonomous, null, humano.StateVersion!.Value, antiga);

        Assert.Equal(TransitionOutcome.AlreadyApplied, tardia.Outcome);
        Assert.Null(tardia.MessageSeq); // e a repetição também a devolve desconhecida
        var atual = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.HumanAssisted, humano.StateVersion), (atual.HandlingMode, atual.StateVersion)); // nada foi reaplicado
    }

    [Fact]
    public async Task O_encaminhamento_sem_efeito_fica_registrado_e_a_repeticao_tardia_nao_leva_a_sessao_de_volta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "semefeitotransf");
        var a = await c.CriarUnidadeAsync(c.OrganizacaoId, "A", matriz: true);
        var b = await c.CriarUnidadeAsync(c.OrganizacaoId, "B");
        var sessao = (await c.EntrarAsync("5511999990307", unidade: a)).SessionId;
        var antigo = Guid.NewGuid();

        // A sessão já está em A: o encaminhamento não muda nada e não cria rastro.
        var semEfeito = await c.EncaminharAsync(sessao, a, null, await c.VersaoDaSessaoAsync(sessao), antigo);
        Assert.Equal(TransferOutcome.Unchanged, semEfeito.Outcome);
        Assert.Empty((await c.RastroAsync(sessao))!);

        // A sessão vai para B por outro caminho.
        Assert.Equal(TransferOutcome.Applied, (await c.EncaminharAsync(sessao, b, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);

        // A repetição tardia do pedido antigo (para A) não a traz de volta.
        var tardio = await c.EncaminharAsync(sessao, a, null, await c.VersaoDaSessaoAsync(sessao), antigo);

        Assert.Equal(TransferOutcome.AlreadyApplied, tardio.Outcome);
        Assert.Equal(b, (await c.SessaoAsync(sessao))!.BusinessUnitId);
        Assert.Single((await c.RastroAsync(sessao))!); // só o encaminhamento que aconteceu
    }

    // A fronteira estável

    [Fact]
    public async Task A_transicao_devolve_a_sequencia_da_ultima_mensagem_naquele_instante_e_a_repeticao_devolve_a_mesma()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fronteira");
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "um")).SessionId;
        await c.EntrarAsync(contato, texto: "dois");
        var id = Guid.NewGuid();

        var aplicada = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, 1, id);
        Assert.Equal((TransitionOutcome.Applied, 2L), (aplicada.Outcome, aplicada.MessageSeq));
        Assert.Equal(2L, Assert.IsType<HandlingModeChangedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.HandlingModeChanged, sessao))).MessageSeq);

        // Chegam mais mensagens; a repetição da MESMA transição devolve a fronteira de quando ela aconteceu, não a de agora.
        await c.EntrarAsync(contato, texto: "tres");
        await c.EntrarAsync(contato, texto: "quatro");
        var repetida = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, 1, id);
        Assert.Equal((TransitionOutcome.AlreadyApplied, 2L), (repetida.Outcome, repetida.MessageSeq));
        Assert.Equal(4L, (await c.SessaoAsync(sessao))!.LastMessageSeq);
    }

    // A identidade do período de atendimento humano

    [Fact]
    public async Task O_periodo_de_atendimento_humano_so_avanca_ao_entrar_em_humano_vindo_de_outro_modo_e_a_repeticao_devolve_o_mesmo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "periodo");
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato)).SessionId;
        var humano = Guid.NewGuid();
        var outro = Guid.NewGuid();
        Assert.Equal(0L, (await c.SessaoAsync(sessao))!.HandlingPeriod); // em IA, ainda nenhum período humano

        // IA → humano abre o primeiro período, e a repetição devolve o MESMO, mesmo depois de a sessão ter mudado.
        var primeiro = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, 1, humano);
        Assert.Equal((TransitionOutcome.Applied, 1L), (primeiro.Outcome, primeiro.HandlingPeriod));

        // Trocar o responsável DENTRO do atendimento humano não abre período novo.
        var trocou = await Transitar(c, sessao, HandlingMode.HumanAssisted, outro, primeiro.StateVersion!.Value);
        Assert.Equal((TransitionOutcome.Applied, 1L), (trocou.Outcome, trocou.HandlingPeriod));

        // Humano → IA → humano: o segundo atendimento é OUTRO período, ainda que o modo seja de novo o humano.
        var paraIa = await Transitar(c, sessao, HandlingMode.AiAutonomous, null, trocou.StateVersion!.Value);
        var segundo = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, paraIa.StateVersion!.Value);
        Assert.Equal((TransitionOutcome.Applied, 2L), (segundo.Outcome, segundo.HandlingPeriod));
        Assert.Equal((HandlingMode.HumanAssisted, 2L), ((await c.SessaoAsync(sessao))!.HandlingMode, (await c.SessaoAsync(sessao))!.HandlingPeriod));

        var repetida = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, 1, humano);
        Assert.Equal((TransitionOutcome.AlreadyApplied, 1L), (repetida.Outcome, repetida.HandlingPeriod)); // o período que ELA abriu, não o de agora

        // A transição sem efeito não inventa identidade: desconhecida (nula), na primeira resposta e na repetição.
        var semEfeito = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, segundo.StateVersion!.Value, Guid.NewGuid());
        Assert.Equal((TransitionOutcome.InvalidTransition, (long?)null), (semEfeito.Outcome, semEfeito.HandlingPeriod));
    }

    [Fact]
    public async Task A_sessao_que_nasce_em_atendimento_humano_comeca_no_periodo_1_e_em_IA_no_0()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "periodonasce");

        var humana = (await c.EntrarAsync(ContactCenterTestSupport.Unico("contato"), modoInicial: HandlingMode.HumanAssisted)).SessionId;
        var ia = (await c.EntrarAsync(ContactCenterTestSupport.Unico("contato"))).SessionId;

        Assert.Equal((HandlingMode.HumanAssisted, 1L), ((await c.SessaoAsync(humana))!.HandlingMode, (await c.SessaoAsync(humana))!.HandlingPeriod));
        Assert.Equal((HandlingMode.AiAutonomous, 0L), ((await c.SessaoAsync(ia))!.HandlingMode, (await c.SessaoAsync(ia))!.HandlingPeriod));
    }

    [Fact]
    public async Task O_encerramento_da_sessao_leva_no_evento_a_sequencia_da_ultima_mensagem()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fronteirafim");
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "um")).SessionId;
        await c.EntrarAsync(contato, texto: "dois");
        await c.EntrarAsync(contato, texto: "tres");

        await c.EncerrarSessaoAsync(sessao);

        Assert.Equal(3L, Assert.IsType<SessionClosedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.SessionClosed, sessao))).LastMessageSeq);
    }

    [Fact]
    public async Task O_historico_limitado_por_sequencia_filtra_antes_de_paginar_e_valida_o_limite()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fronteirahist");
        var contato = ContactCenterTestSupport.Unico("contato");
        var t = DateTimeOffset.UtcNow;

        // Horários do provedor fora da ordem de chegada: a ordem de exibição (OccurredAt) não é a de ingestão (MessageSeq).
        var sessao = (await c.EntrarAsync(contato, texto: "s1", quando: t.AddSeconds(30))).SessionId;
        await c.EntrarAsync(contato, texto: "s2", quando: t.AddSeconds(10));
        await c.EntrarAsync(contato, texto: "s3", quando: t.AddSeconds(20));
        await c.EntrarAsync(contato, texto: "s4", quando: t.AddSeconds(-60));

        async Task<List<string?>> Ler(HistoryOrder ordem, int tamanho, long? ate)
        {
            var textos = new List<string?>();
            string? cursor = null;
            do
            {
                var pagina = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(sessao, tamanho, cursor, ordem, ate)));
                textos.AddRange(pagina!.Items.Select(m => m.Text));
                cursor = pagina.NextCursor;
            }
            while (cursor is not null);

            return textos;
        }

        // Até a sequência 3: s1, s2 e s3 (s4, de horário mais antigo, chegou depois e fica de fora), na ordem de exibição.
        Assert.Equal(new[] { "s2", "s3", "s1" }, await Ler(HistoryOrder.OldestFirst, 1, 3));
        Assert.Equal(new[] { "s1", "s3", "s2" }, await Ler(HistoryOrder.NewestFirst, 2, 3));
        Assert.Equal(new[] { "s4", "s2", "s3", "s1" }, await Ler(HistoryOrder.OldestFirst, 50, null)); // sem limite, tudo
        Assert.Empty(await Ler(HistoryOrder.OldestFirst, 10, 0));

        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.ComoAsync(c.ContextoDeSistema, sp =>
            sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(sessao, 10, null, HistoryOrder.OldestFirst, -1))));
    }
}
