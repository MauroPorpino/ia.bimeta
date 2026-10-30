using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O lado do Conversations da autoridade operacional: o comando leva o estado de atendimento (responsável e período) sobre o qual quem
/// comanda decidiu que a operação é autorizada, e o Conversations o confere DENTRO da trava da sessão, junto da gravação. Renovar a versão
/// esperada não renova a autorização; a repetição de um comando já aplicado é reconhecida antes da condição; quem não altera nada
/// dispensa a condição. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HandlingPreconditionTests(PostgresFixture banco)
{
    private static Task<T> Comandos<T>(CenarioDeConversas c, Func<IHandlingCommands, Task<T>> acao) =>
        c.ComoAsync(c.ContextoDeSistema, sp => acao(sp.GetRequiredService<IHandlingCommands>()));

    private static Task<TransitionResult> Transitar(
        CenarioDeConversas c, Guid sessao, HandlingMode para, Guid? responsavel, long versao, HandlingPrecondition? condicao, Guid? id = null) =>
        Comandos(c, h => h.TransitionHandlingModeAsync(new TransitionHandlingModeCommand(sessao, para, responsavel, versao, id ?? Guid.NewGuid(), "manual", null, condicao)));

    /// <summary>Uma sessão em atendimento humano com a Ana como responsável (período 1).</summary>
    private static async Task<(Guid Sessao, Guid Ana, Guid Carla)> ComAnaAsync(CenarioDeConversas c, string contato)
    {
        var sessao = (await c.EntrarAsync(contato)).SessionId;
        var ana = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, ana, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var visao = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.HumanAssisted, ana, 1L), (visao.HandlingMode, visao.ResponderRef, visao.HandlingPeriod));
        return (sessao, ana, Guid.NewGuid());
    }

    [Fact]
    public async Task Renovar_a_versao_esperada_nao_renova_a_autorizacao_se_o_responsavel_mudou_nada_e_gravado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precresp");
        var (sessao, ana, carla) = await ComAnaAsync(c, "5511999990401");

        // A autorização foi dada sobre a Ana. O responsável muda por fora, para a Carla.
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, carla, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(sessao);
        var eventos = c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao);

        // Quem relê a sessão e usa a versão ATUAL (a renovação automática) continua sem a autorização sobre a Carla.
        var recusada = await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao, new HandlingPrecondition(ana, 1));

        Assert.Equal((TransitionOutcome.HandlingChanged, versao), (recusada.Outcome, recusada.StateVersion));
        var depois = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.HumanAssisted, carla, versao), (depois.HandlingMode, depois.ResponderRef, depois.StateVersion));
        Assert.Equal(eventos, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));

        // Autorizada sobre o responsável de agora, a mesma transição vale.
        Assert.Equal(TransitionOutcome.Applied, (await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao, new HandlingPrecondition(carla, 1))).Outcome);
        Assert.Equal(HandlingMode.AiAutonomous, (await c.SessaoAsync(sessao))!.HandlingMode);
    }

    [Fact]
    public async Task Outro_periodo_de_atendimento_nao_satisfaz_a_condicao_mesmo_com_o_mesmo_responsavel()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precperiodo");
        var (sessao, ana, _) = await ComAnaAsync(c, "5511999990402");

        // Humano → IA → humano com a MESMA Ana: o período avança de 1 para 2.
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, ana, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(sessao);
        Assert.Equal(2L, (await c.SessaoAsync(sessao))!.HandlingPeriod);

        Assert.Equal(TransitionOutcome.HandlingChanged, (await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao, new HandlingPrecondition(ana, 1))).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao, new HandlingPrecondition(ana, 2))).Outcome);
    }

    [Fact]
    public async Task A_sessao_fora_do_atendimento_humano_nao_satisfaz_a_condicao_e_a_encerrada_continua_encerrada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precmodo");
        var sessaoIa = (await c.EntrarAsync("5511999990403")).SessionId; // em IA, período 0
        var versao = await c.VersaoDaSessaoAsync(sessaoIa);

        // Autorizada sobre "esperando atendente" (humano, sem responsável, período 1), a sessão em IA não satisfaz a condição.
        Assert.Equal(
            TransitionOutcome.HandlingChanged,
            (await Transitar(c, sessaoIa, HandlingMode.HumanAssisted, Guid.NewGuid(), versao, new HandlingPrecondition(null, 1))).Outcome);
        Assert.Equal(HandlingMode.AiAutonomous, (await c.SessaoAsync(sessaoIa))!.HandlingMode);

        // Sessão encerrada responde encerrada (o executor encerra o item), e não "o estado mudou".
        var (sessao, _, _) = await ComAnaAsync(c, "5511999990404");
        await c.EncerrarSessaoAsync(sessao);
        Assert.Equal(
            TransitionOutcome.SessionClosed,
            (await Transitar(c, sessao, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(sessao), new HandlingPrecondition(Guid.NewGuid(), 7))).Outcome);
    }

    [Fact]
    public async Task O_comando_que_ja_encontra_a_sessao_no_estado_pedido_nao_altera_nada_e_dispensa_a_condicao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precnoop");
        var (sessao, _, carla) = await ComAnaAsync(c, "5511999990405");
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, carla, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(sessao);

        // Pede o estado em que a sessão JÁ está, com uma condição que não vale: nada a proteger (não há o que alterar), então não é "mudou".
        var igual = await Transitar(c, sessao, HandlingMode.HumanAssisted, carla, versao, new HandlingPrecondition(Guid.NewGuid(), 1));
        Assert.Equal(TransitionOutcome.InvalidTransition, igual.Outcome);
        Assert.Equal(versao, await c.VersaoDaSessaoAsync(sessao));
    }

    [Fact]
    public async Task A_repeticao_de_um_comando_ja_aplicado_e_reconhecida_antes_da_condicao_mesmo_depois_do_proprio_efeito()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precrepete");
        var (sessao, ana, _) = await ComAnaAsync(c, "5511999990406");
        var versao = await c.VersaoDaSessaoAsync(sessao);
        var id = Guid.NewGuid();
        var condicao = new HandlingPrecondition(ana, 1);

        // Devolve à IA sobre a Ana: aplicado.
        var primeira = await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao, condicao, id);
        Assert.Equal(TransitionOutcome.Applied, primeira.Outcome);

        // A resposta se perdeu e quem comanda repete, com o mesmo identificador e a mesma condição. A sessão já está em IA (a condição não
        // vale mais, por causa do próprio efeito): a repetição é reconhecida e devolve o mesmo resultado, com a fronteira e o período.
        var repetida = await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao, condicao, id);
        Assert.Equal(TransitionOutcome.AlreadyApplied, repetida.Outcome);
        Assert.Equal((primeira.StateVersion, primeira.MessageSeq, primeira.HandlingPeriod), (repetida.StateVersion, repetida.MessageSeq, repetida.HandlingPeriod));
    }

    [Fact]
    public async Task O_encaminhamento_tambem_so_altera_o_estado_sobre_o_qual_foi_autorizado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precencam");
        var a = await c.CriarUnidadeAsync(c.OrganizacaoId, "A", matriz: true);
        var b = await c.CriarUnidadeAsync(c.OrganizacaoId, "B");
        var sessao = (await c.EntrarAsync("5511999990407", unidade: a)).SessionId;
        var ana = Guid.NewGuid();
        var carla = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, ana, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, carla, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(sessao);

        // Autorizado sobre a Ana, o encaminhamento não move a conversa da Carla: nada é gravado, nem rastro.
        var recusado = await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versao, Guid.NewGuid(), "queue-transfer", null, new HandlingPrecondition(ana, 1))));
        Assert.Equal((TransferOutcome.HandlingChanged, versao), (recusado.Outcome, recusado.StateVersion));
        Assert.Equal(a, (await c.SessaoAsync(sessao))!.BusinessUnitId);
        Assert.Empty((await c.RastroAsync(sessao))!);

        // Sobre a Carla, vale. A repetição (mesmo identificador) é reconhecida antes da condição, mesmo com uma condição que não vale mais.
        var id = Guid.NewGuid();
        Assert.Equal(TransferOutcome.Applied, (await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versao, id, "queue-transfer", null, new HandlingPrecondition(carla, 1))))).Outcome);
        Assert.Equal(TransferOutcome.AlreadyApplied, (await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versao, id, "queue-transfer", null, new HandlingPrecondition(ana, 1))))).Outcome);

        // Já na unidade pedida não há o que alterar: a condição que não vale não o transforma em "mudou".
        var versaoDepois = await c.VersaoDaSessaoAsync(sessao);
        var ja = await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versaoDepois, Guid.NewGuid(), "queue-transfer", null, new HandlingPrecondition(ana, 1))));
        Assert.Equal(TransferOutcome.Unchanged, ja.Outcome);
    }

    // A identidade durável da colocação (a geração que avança a cada mudança de unidade ou setor)

    [Fact]
    public async Task A_geracao_da_colocacao_comeca_em_1_avanca_a_cada_mudanca_de_unidade_ou_setor_e_nunca_volta_mesmo_em_A_B_A()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "geracao");
        var a = await c.CriarUnidadeAsync(c.OrganizacaoId, "A", matriz: true);
        var b = await c.CriarUnidadeAsync(c.OrganizacaoId, "B");
        var setorUm = await c.CriarSetorAsync(a, "Um");
        var setorDois = await c.CriarSetorAsync(a, "Dois");
        var sessao = (await c.EntrarAsync("5511999990420", unidade: a, setor: setorUm)).SessionId;

        async Task<(Guid? Unidade, Guid? Setor, long Geracao)> EstadoAsync()
        {
            var atual = (await c.SessaoAsync(sessao))!;
            return (atual.BusinessUnitId, atual.DepartmentId, atual.PlacementEpoch);
        }

        async Task<TransferOutcome> IrAsync(Guid unidade, Guid? setor, Guid? id = null) =>
            (await c.EncaminharAsync(sessao, unidade, setor, await c.VersaoDaSessaoAsync(sessao), id)).Outcome;

        Assert.Equal((a, (Guid?)setorUm, 1L), await EstadoAsync());

        // A → B → A (o mesmo setor de antes): a unidade e o setor são de novo os de antes, a geração não.
        Assert.Equal(TransferOutcome.Applied, await IrAsync(b, null));
        Assert.Equal((b, (Guid?)null, 2L), await EstadoAsync());
        Assert.Equal(TransferOutcome.Applied, await IrAsync(a, setorUm));
        Assert.Equal((a, (Guid?)setorUm, 3L), await EstadoAsync());

        // Outro setor da mesma unidade também é outra colocação; ficar onde está não é.
        Assert.Equal(TransferOutcome.Applied, await IrAsync(a, setorDois));
        Assert.Equal(4L, (await EstadoAsync()).Geracao);
        Assert.Equal(TransferOutcome.Unchanged, await IrAsync(a, setorDois));
        Assert.Equal(4L, (await EstadoAsync()).Geracao);

        // A repetição de um encaminhamento já aplicado (mesmo identificador) é reconhecida e não avança a geração.
        var id = Guid.NewGuid();
        Assert.Equal(TransferOutcome.Applied, await IrAsync(b, null, id));
        Assert.Equal(5L, (await EstadoAsync()).Geracao);
        Assert.Equal(TransferOutcome.AlreadyApplied, (await c.EncaminharAsync(sessao, b, null, await c.VersaoDaSessaoAsync(sessao), id)).Outcome);
        Assert.Equal(5L, (await EstadoAsync()).Geracao);

        // Mudanças que NÃO são de colocação (a transição de modo e responsável) não a avançam, nem o período de atendimento a substitui.
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, Guid.NewGuid(), await c.VersaoDaSessaoAsync(sessao))).Outcome);
        Assert.Equal(5L, (await EstadoAsync()).Geracao);
    }

    [Fact]
    public async Task A_pre_condicao_confere_a_geracao_da_colocacao_nos_dois_comandos_e_so_quando_ela_e_informada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "geracao2");
        var a = await c.CriarUnidadeAsync(c.OrganizacaoId, "A", matriz: true);
        var b = await c.CriarUnidadeAsync(c.OrganizacaoId, "B");
        var sessao = (await c.EntrarAsync("5511999990421", unidade: a)).SessionId;
        var carla = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, carla, await c.VersaoDaSessaoAsync(sessao))).Outcome);

        // A → B → A por fora: tudo é de novo como era (unidade, setor, período, responsável), e a geração é a 3.
        Assert.Equal(TransferOutcome.Applied, (await c.EncaminharAsync(sessao, b, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        Assert.Equal(TransferOutcome.Applied, (await c.EncaminharAsync(sessao, a, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(sessao);
        Assert.Equal(3L, (await c.SessaoAsync(sessao))!.PlacementEpoch);

        // Autorizado sobre a geração 1, o encaminhamento e a transição são recusados (HandlingChanged); sobre a 3, valem; sem a geração, não é conferida.
        var antiga = new HandlingPrecondition(carla, 1, HandlingMode.HumanAssisted, 1);
        Assert.Equal(
            TransferOutcome.HandlingChanged,
            (await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versao, Guid.NewGuid(), "queue-transfer", null, antiga)))).Outcome);
        var recusada = await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao, antiga);
        Assert.Equal((TransitionOutcome.HandlingChanged, (long?)versao), (recusada.Outcome, recusada.StateVersion));
        Assert.Equal(a, (await c.SessaoAsync(sessao))!.BusinessUnitId);

        var certa = new HandlingPrecondition(carla, 1, HandlingMode.HumanAssisted, 3);
        Assert.Equal(
            TransferOutcome.Applied,
            (await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versao, Guid.NewGuid(), "queue-transfer", null, certa)))).Outcome);

        // Uma operação composta espera a geração SEGUINTE depois da unidade (a 4), e a mesma condição de antes já não vale para o comando seguinte.
        var depois = await c.VersaoDaSessaoAsync(sessao);
        Assert.Equal(TransitionOutcome.HandlingChanged, (await Transitar(c, sessao, HandlingMode.AiAutonomous, null, depois, certa)).Outcome);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await Transitar(c, sessao, HandlingMode.AiAutonomous, null, depois, new HandlingPrecondition(carla, 1, HandlingMode.HumanAssisted, 4))).Outcome);
    }

    [Fact]
    public async Task A_pre_condicao_sem_a_geracao_continua_como_antes_e_o_encaminhamento_que_nao_muda_nada_dispensa_a_geracao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "geracao3");
        var a = await c.CriarUnidadeAsync(c.OrganizacaoId, "A", matriz: true);
        var b = await c.CriarUnidadeAsync(c.OrganizacaoId, "B");
        var sessao = (await c.EntrarAsync("5511999990422", unidade: a)).SessionId;
        var ana = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, ana, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        Assert.Equal(TransferOutcome.Applied, (await c.EncaminharAsync(sessao, b, null, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(sessao);

        // A geração não informada não é conferida (o chamador que não a conhece não muda).
        Assert.Equal(TransitionOutcome.Applied, (await Transitar(c, sessao, HandlingMode.AiAutonomous, null, versao, new HandlingPrecondition(ana, 1))).Outcome);

        // Já na unidade pedida, o encaminhamento não altera nada e dispensa a condição, inclusive a geração que não vale.
        var versaoDeAgora = await c.VersaoDaSessaoAsync(sessao);
        var jaNaB = await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versaoDeAgora, Guid.NewGuid(), "queue-transfer", null,
            new HandlingPrecondition(Guid.NewGuid(), 99, HandlingMode.HumanAssisted, 99))));
        Assert.Equal(TransferOutcome.Unchanged, jaNaB.Outcome);
    }

    // A entrada na fila parte da IA: a condição dela é o modo de IA, sem responsável, no período lido

    private static readonly HandlingPrecondition EntradaDaIa = new(null, 0, HandlingMode.AiAutonomous);

    [Fact]
    public async Task A_condicao_da_entrada_nao_retira_o_atendente_que_apareceu_depois_mesmo_com_a_versao_renovada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precentra");
        var sessao = (await c.EntrarAsync("5511999990410")).SessionId; // em IA, período 0
        var carla = Guid.NewGuid();

        // A entrada foi autorizada sobre a conversa com a IA. A Carla assume por fora.
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, carla, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(sessao);
        var eventos = c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao);

        // A entrada (humano, sem responsável) com a versão ATUAL, como quem relê a sessão a usaria: recusada, a Carla continua.
        var recusada = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, versao, EntradaDaIa);

        Assert.Equal((TransitionOutcome.HandlingChanged, versao), (recusada.Outcome, recusada.StateVersion));
        var depois = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.HumanAssisted, carla, versao), (depois.HandlingMode, depois.ResponderRef, depois.StateVersion));
        Assert.Equal(eventos, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
    }

    [Fact]
    public async Task A_condicao_da_entrada_vale_com_a_sessao_em_IA_no_periodo_lido_e_outro_periodo_ou_outro_modo_a_invalida()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precentra2");

        // Em IA, no período lido: a entrada se aplica e abre o período 1.
        var livre = (await c.EntrarAsync("5511999990411")).SessionId;
        var entrada = await Transitar(c, livre, HandlingMode.HumanAssisted, null, await c.VersaoDaSessaoAsync(livre), EntradaDaIa);
        Assert.Equal((TransitionOutcome.Applied, 1L), (entrada.Outcome, entrada.HandlingPeriod));

        // IA → humano → IA: a sessão está de novo em IA, mas já no período 1. A entrada autorizada sobre o período 0 não vale (é outro
        // atendimento), e a condição com o período lido, sim.
        var voltou = (await c.EntrarAsync("5511999990412")).SessionId;
        var carla = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(voltou, HandlingMode.HumanAssisted, carla, await c.VersaoDaSessaoAsync(voltou))).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(voltou, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(voltou))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(voltou);
        Assert.Equal(TransitionOutcome.HandlingChanged, (await Transitar(c, voltou, HandlingMode.HumanAssisted, null, versao, EntradaDaIa)).Outcome);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await Transitar(c, voltou, HandlingMode.HumanAssisted, null, versao, new HandlingPrecondition(null, 1, HandlingMode.AiAutonomous))).Outcome);

        // A condição de IA não vale para a sessão que já espera na fila (humano, sem responsável): é outro modo, e uma condição de IA não
        // autoriza ninguém a assumi-la.
        var recusada = await Transitar(
            c, voltou, HandlingMode.HumanAssisted, Guid.NewGuid(), await c.VersaoDaSessaoAsync(voltou), new HandlingPrecondition(null, 1, HandlingMode.AiAutonomous));
        Assert.Equal(TransitionOutcome.HandlingChanged, recusada.Outcome);
    }

    [Fact]
    public async Task A_repeticao_da_entrada_ja_aplicada_e_reconhecida_antes_da_condicao_e_o_estado_em_que_ela_resulta_dispensa_a_condicao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precentra3");
        var sessao = (await c.EntrarAsync("5511999990413")).SessionId;
        var id = Guid.NewGuid();
        var primeira = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, await c.VersaoDaSessaoAsync(sessao), EntradaDaIa, id);
        Assert.Equal(TransitionOutcome.Applied, primeira.Outcome);

        // A resposta se perdeu e a mesma entrada é repetida, depois de a Carla assumir por fora: o identificador já foi aplicado, e a repetição
        // devolve o resultado de antes, sem reavaliar a condição contra o estado de agora.
        var carla = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, carla, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var repetida = await Transitar(c, sessao, HandlingMode.HumanAssisted, null, primeira.StateVersion!.Value, EntradaDaIa, id);
        Assert.Equal(TransitionOutcome.AlreadyApplied, repetida.Outcome);
        Assert.Equal((primeira.StateVersion, primeira.HandlingPeriod), (repetida.StateVersion, repetida.HandlingPeriod));
        Assert.Equal(carla, (await c.SessaoAsync(sessao))!.ResponderRef);

        // A entrada de uma sessão que JÁ espera na fila (humano, sem responsável) não altera nada e dispensa a condição.
        var esperando = (await c.EntrarAsync("5511999990414")).SessionId;
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(esperando, HandlingMode.HumanAssisted, null, await c.VersaoDaSessaoAsync(esperando))).Outcome);
        var igual = await Transitar(c, esperando, HandlingMode.HumanAssisted, null, await c.VersaoDaSessaoAsync(esperando), EntradaDaIa);
        Assert.Equal(TransitionOutcome.InvalidTransition, igual.Outcome);
    }

    [Fact]
    public async Task O_encaminhamento_da_entrada_respeita_a_mesma_condicao_de_IA()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "precentra4");
        var a = await c.CriarUnidadeAsync(c.OrganizacaoId, "A", matriz: true);
        var b = await c.CriarUnidadeAsync(c.OrganizacaoId, "B");
        var sessao = (await c.EntrarAsync("5511999990415", unidade: a)).SessionId;
        var carla = Guid.NewGuid();
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, carla, await c.VersaoDaSessaoAsync(sessao))).Outcome);
        var versao = await c.VersaoDaSessaoAsync(sessao);

        // A entrada foi autorizada com a conversa na IA: a unidade de destino dela não é aplicada sobre a conversa da Carla.
        var recusado = await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(sessao, b, null, versao, Guid.NewGuid(), "queue-transfer", null, EntradaDaIa)));
        Assert.Equal((TransferOutcome.HandlingChanged, versao), (recusado.Outcome, recusado.StateVersion));
        Assert.Equal(a, (await c.SessaoAsync(sessao))!.BusinessUnitId);
        Assert.Empty((await c.RastroAsync(sessao))!);

        // Com a sessão ainda em IA, no período lido, o encaminhamento vale.
        var emIa = (await c.EntrarAsync("5511999990416", unidade: a)).SessionId;
        var versaoEmIa = await c.VersaoDaSessaoAsync(emIa);
        Assert.Equal(
            TransferOutcome.Applied,
            (await Comandos(c, h => h.TransferSessionAsync(new TransferSessionCommand(emIa, b, null, versaoEmIa, Guid.NewGuid(), "queue-transfer", null, EntradaDaIa)))).Outcome);
        Assert.Equal(b, (await c.SessaoAsync(emIa))!.BusinessUnitId);
    }
}
