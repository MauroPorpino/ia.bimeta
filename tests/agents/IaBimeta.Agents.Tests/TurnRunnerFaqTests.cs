using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A resposta direta do FAQ no <see cref="TurnRunner"/> (ADR 0029), sobre os dublês (sem banco e sem provedor): quando responde SEM chamar o modelo, cada condição que a recusa (e então o modelo é chamado, como
/// antes), a mesma proteção contra resposta defasada, o reinício do Worker, e o isolamento entre sessões e entre tenants. A prova central de cada acerto é <c>Gateway.Requests</c> VAZIO.
/// </summary>
public sealed class TurnRunnerFaqTests
{
    private const string RespostaDoHorario = "De segunda a sexta, das 7h às 17h.";

    private static readonly FaqEntry Horario = new("Qual o horário de atendimento?", RespostaDoHorario, new[] { "que horas vocês abrem" }, true);

    private static readonly FaqEntry Endereco = new("Onde fica a unidade?", "Na Rua das Flores, 100.", null, true);

    private static AgentDefinitionContent Definicao(string? idioma = null, params FaqEntry[] faq) =>
        Definitions.Default() with { Faq = faq.Length == 0 ? new[] { Horario, Endereco } : faq, FaqLocale = idioma };

    private static RunnerHarness Montar(string texto, AgentDefinitionContent? definicao = null, string locale = "pt-BR", Action<AgentsOptions>? opcoes = null, LocaleSource origem = LocaleSource.ChannelDefault)
    {
        var h = new RunnerHarness(opcoes).Grant(Snapshots.Snapshot(new[] { Snapshots.Customer(1, texto) }, locale: locale, source: origem));
        h.Definitions.Published = definicao ?? Definicao();
        return h;
    }

    // O acerto: zero chamadas ao modelo

    [Fact]
    public async Task Pergunta_igual_a_do_FAQ_com_resposta_direta_responde_sem_chamar_o_modelo_pelo_mesmo_registro_e_com_as_mesmas_cercas()
    {
        var h = Montar("Qual o horário de atendimento?");
        h.Turns.OnBegin = _ => FakeTurnCommands.Granted(Snapshots.OneText("Qual o horário de atendimento?"), epoch: 5);
        var turno = h.SeedRunning(lane: 7);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.FaqReply, AiTurnEndReasons.FaqHit), resultado);
        Assert.Empty(h.Gateway.Requests); // a prova: nenhuma chamada ao provedor

        var snapshot = Snapshots.OneText();
        var registro = Assert.Single(h.Turns.Registers);
        Assert.Equal(
            (turno.SessionId, turno.Id, 5L, snapshot.StateVersion, snapshot.LastInboundSeq, ReplyKind.Informative, snapshot.ConversationId, RespostaDoHorario),
            (registro.SessionId, registro.TurnId, registro.Epoch, registro.ExpectedStateVersion, registro.BasedOnInboundSeq, registro.ReplyKind, registro.TargetConversationId, registro.Text));
        Assert.Empty(registro.AiRequestIds); // sem chamada ao modelo, sem identificador de chamada

        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Completed, "faq-hit"), (final.State, final.EndReason));
        Assert.Empty(h.Handoff.Requests);
        Assert.Empty(h.Turns.Ends); // a resposta aceita já libera o turno ativo no Conversations
    }

    [Theory]
    [InlineData("qual o horario de atendimento")]
    [InlineData("QUAL O HORÁRIO DE ATENDIMENTO!!!")]
    [InlineData("  Qual   o horário,  de atendimento?  ")]
    [InlineData("que horas vocês abrem")]
    [InlineData("Que horas voces abrem???")]
    public async Task A_pergunta_normalizada_ou_um_alias_cadastrado_tambem_acerta(string mensagem)
    {
        var h = Montar(mensagem);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.FaqReply, resultado.Outcome);
        Assert.Empty(h.Gateway.Requests);
        Assert.Equal(RespostaDoHorario, Assert.Single(h.Turns.Registers).Text);
    }

    [Fact]
    public async Task Outra_entrada_do_FAQ_responde_o_texto_dela_e_so_dela()
    {
        var h = Montar("onde fica a unidade?");

        await h.RunAsync(h.SeedRunning());

        Assert.Equal("Na Rua das Flores, 100.", Assert.Single(h.Turns.Registers).Text);
        Assert.Empty(h.Gateway.Requests);
    }

    // As recusas: o turno segue ao modelo, como antes do ADR 0029

    private static async Task<RunnerHarness> RodarAteOModeloAsync(RunnerHarness h)
    {
        h.Gateway.Behavior = _ => ScriptedGateway.Reply("Resposta do modelo.");
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Replied, "replied"), resultado);
        Assert.Single(h.Gateway.Requests); // o modelo foi chamado uma vez
        var registro = Assert.Single(h.Turns.Registers);
        Assert.Equal((ReplyKind.Generated, "Resposta do modelo."), (registro.ReplyKind, registro.Text));
        return h;
    }

    [Fact]
    public async Task Entrada_sem_resposta_direta_vai_ao_modelo_mesmo_com_a_pergunta_identica()
    {
        var semDireta = new FaqEntry("Qual o horário de atendimento?", RespostaDoHorario, null, false);

        await RodarAteOModeloAsync(Montar("Qual o horário de atendimento?", Definicao(null, semDireta)));
    }

    [Theory]
    [InlineData("Qual o horário de atendimeto?")] // erro de digitação
    [InlineData("Qual o horário de atendimento no sábado?")] // palavras a mais
    [InlineData("horário de atendimento")] // palavras a menos
    [InlineData("Qual o horário de atendimento? E o endereço?")] // duas perguntas
    [InlineData("Preciso de jejum para esse exame?")]
    public async Task Pergunta_que_nao_e_igual_vai_ao_modelo(string mensagem)
    {
        await RodarAteOModeloAsync(Montar(mensagem));
    }

    [Fact]
    public async Task Duas_entradas_para_a_mesma_pergunta_ou_alias_vao_ao_modelo()
    {
        var duplicada = new FaqEntry("qual o horário de atendimento", "Outra resposta.", null, true);
        var comAliasIgual = new FaqEntry("Aceitam convênio?", "Sim.", new[] { "Qual o horário de atendimento?" }, true);

        await RodarAteOModeloAsync(Montar("Qual o horário de atendimento?", Definicao(null, Horario, duplicada)));
        await RodarAteOModeloAsync(Montar("Qual o horário de atendimento?", Definicao(null, Horario, comAliasIgual)));
    }

    [Fact]
    public async Task Duas_mensagens_no_grupo_vao_ao_modelo_mesmo_que_uma_seja_a_pergunta_do_FAQ()
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Snapshots.Customer(1, "Oi"), Snapshots.Customer(2, "Qual o horário de atendimento?")));
        h.Definitions.Published = Definicao();

        await RodarAteOModeloAsync(h);
    }

    [Fact]
    public async Task Mensagem_que_nao_e_texto_vai_ao_modelo_mesmo_com_a_legenda_igual_a_pergunta()
    {
        var h = new RunnerHarness().Grant(Snapshots.Snapshot(new[] { Snapshots.Customer(1, "Qual o horário de atendimento?", MessageKind.Image) }));
        h.Definitions.Published = Definicao();

        await RodarAteOModeloAsync(h);
    }

    [Fact]
    public async Task Com_a_funcao_desligada_pela_configuracao_todo_turno_vai_ao_modelo()
    {
        await RodarAteOModeloAsync(Montar("Qual o horário de atendimento?", opcoes: o => o.FaqDirectAnswerEnabled = false));
    }

    [Fact]
    public async Task Sem_definicao_publicada_a_resposta_direta_nao_existe_e_o_atendente_assume()
    {
        var h = Montar("Qual o horário de atendimento?");
        h.Definitions.Published = null;
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.Dead, resultado.Outcome);
        Assert.Empty(h.Gateway.Requests);
        Assert.Empty(h.Turns.Registers);
    }

    // Idioma

    [Fact]
    public async Task Conversa_em_outro_idioma_nao_recebe_a_resposta_direta()
    {
        await RodarAteOModeloAsync(Montar("Qual o horário de atendimento?", locale: "en-US"));
    }

    [Fact]
    public async Task Idioma_do_FAQ_en_US_responde_na_conversa_en_US_e_nao_na_pt_BR()
    {
        var emIngles = Definicao("en-US", new FaqEntry("What are your opening hours?", "Monday to Friday, 7am to 5pm.", null, true));

        var h = Montar("What are your opening hours?", emIngles, locale: "en-US");
        var acerto = await h.RunAsync(h.SeedRunning());
        Assert.Equal(TurnRunOutcome.FaqReply, acerto.Outcome);
        Assert.Empty(h.Gateway.Requests);
        Assert.Equal("Monday to Friday, 7am to 5pm.", Assert.Single(h.Turns.Registers).Text);

        await RodarAteOModeloAsync(Montar("What are your opening hours?", emIngles, locale: "pt-BR"));
    }

    [Fact]
    public async Task O_idioma_efetivo_e_o_da_conversa_e_so_a_igualdade_exata_vale()
    {
        // pt-PT não é pt-BR: o FAQ foi escrito para um e a conversa é do outro.
        await RodarAteOModeloAsync(Montar("Qual o horário de atendimento?", locale: "pt-PT"));
    }

    [Fact]
    public async Task Idioma_so_suposto_pelo_padrao_do_tenant_e_igual_ao_do_FAQ_responde_porque_o_texto_e_igual_a_pergunta_cadastrada()
    {
        var h = Montar("Qual o horário de atendimento?", locale: "pt-BR", origem: LocaleSource.TenantDefault);

        var resultado = await h.RunAsync(h.SeedRunning());

        Assert.Equal(TurnRunOutcome.FaqReply, resultado.Outcome);
        Assert.Empty(h.Gateway.Requests);
    }

    // A mesma proteção contra resposta defasada

    [Theory]
    [InlineData(RegisterReplyOutcome.StaleInput, "stale-input")]
    [InlineData(RegisterReplyOutcome.StaleState, "stale-state")]
    public async Task Resposta_direta_defasada_e_recusada_o_turno_e_descartado_e_o_sucessor_cobre_sem_chamar_o_modelo(RegisterReplyOutcome recusa, string codigo)
    {
        var h = Montar("Qual o horário de atendimento?");
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(recusa, null, 0, 2);
        var turno = h.SeedRunning(supersedeCount: 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Discarded, codigo), resultado);
        Assert.Empty(h.Gateway.Requests);
        var turnos = h.Store.OfSession(turno.SessionId);
        Assert.Equal(2, turnos.Count);
        Assert.Equal((AiTurnState.Discarded, codigo), (h.Fresh(turno).State, h.Fresh(turno).EndReason));
        Assert.Equal((AiTurnState.Scheduled, 2), (turnos.Single(t => t.Id != turno.Id).State, turnos.Single(t => t.Id != turno.Id).SupersedeCount));
    }

    [Theory]
    [InlineData(RegisterReplyOutcome.TurnNotActive, "turn-not-active")]
    [InlineData(RegisterReplyOutcome.NotAiResponder, "not-ai-responder")]
    public async Task Turno_substituido_ou_humano_no_comando_nao_recebe_a_resposta_direta(RegisterReplyOutcome recusa, string codigo)
    {
        var h = Montar("Qual o horário de atendimento?");
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(recusa, null, 0, 1);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Discarded, codigo), resultado);
        Assert.Empty(h.Gateway.Requests);
        Assert.Single(h.Store.OfSession(turno.SessionId));
    }

    [Fact]
    public async Task Forma_nao_aceita_no_registro_da_resposta_direta_e_defeito_e_transfere_ao_atendente()
    {
        var h = Montar("Qual o horário de atendimento?");
        h.Definitions.Published = Definicao() with { HandoffNoticeText = null };
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.ShapeNotAllowed, null, 0, 1);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "reply-shape-not-allowed"), resultado);
        Assert.Empty(h.Gateway.Requests);
        Assert.Equal(HandoffReasons.AiProcessingFailed, Assert.Single(h.Handoff.Requests).Reason);
    }

    // Reinício do Worker

    [Fact]
    public async Task Worker_que_cai_durante_o_registro_o_turno_retomado_responde_uma_unica_vez_pelo_mesmo_TurnId_e_nunca_chama_o_modelo()
    {
        var h = Montar("Qual o horário de atendimento?");
        var turno = h.SeedRunning(lane: 7);
        var primeira = true;
        h.Turns.DuringRegister = _ =>
        {
            if (primeira)
            {
                primeira = false;
                throw new IOException("o processo caiu no meio do registro");
            }
        };

        var antes = await h.RunAsync(turno);
        Assert.Equal(TurnRunOutcome.Retrying, antes.Outcome);

        // Um Worker novo reivindica o turno (o relógio passou do recuo) e o executa de novo.
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Store.Mutate(turno.Id, t => t.Claim(8, "worker-novo", h.Clock.GetUtcNow(), h.Options.TurnMaxDuration));
        var depois = await h.Runner.RunAsync(turno.Id, default);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.FaqReply, "faq-hit"), depois);
        Assert.Empty(h.Gateway.Requests);
        Assert.Equal(2, h.Turns.Registers.Count);
        Assert.All(h.Turns.Registers, r => Assert.Equal(turno.Id, r.TurnId)); // a chave de idempotência do Conversations é a mesma nas duas tentativas
        Assert.Equal(AiTurnState.Completed, h.Fresh(turno).State);
    }

    [Fact]
    public async Task Worker_que_cai_depois_da_resposta_aceita_o_turno_retomado_completa_como_acerto_do_FAQ_sem_nova_resposta()
    {
        var h = Montar("Qual o horário de atendimento?");
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.AlreadyRegistered, Guid.NewGuid(), 1, 1);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.FaqReply, "faq-hit"), resultado);
        Assert.Empty(h.Gateway.Requests);
        Assert.Single(h.Store.OfSession(turno.SessionId)); // tudo coberto: nenhum sucessor
        Assert.Equal((AiTurnState.Completed, "faq-hit"), (h.Fresh(turno).State, h.Fresh(turno).EndReason));
    }

    [Fact]
    public async Task Resposta_direta_ja_registrada_com_mensagem_posterior_a_cobertura_cria_o_sucessor_sem_chamar_o_modelo()
    {
        var h = Montar("Qual o horário de atendimento?");
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.AlreadyRegistered, Guid.NewGuid(), 1, 2);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.FaqReply, resultado.Outcome);
        var sucessor = h.Store.OfSession(turno.SessionId).Single(t => t.Id != turno.Id);
        Assert.Equal(AiTurnState.Scheduled, sucessor.State);
        Assert.Empty(h.Gateway.Requests);
    }

    // Isolamento entre sessões e entre tenants

    [Fact]
    public async Task Mauro_e_Pedro_cada_resposta_direta_vai_so_para_a_sessao_e_a_conversa_de_quem_perguntou()
    {
        var sessaoMauro = Guid.Parse("11111111-0000-0000-0000-00000000aaaa");
        var sessaoPedro = Guid.Parse("11111111-0000-0000-0000-00000000bbbb");
        var conversaMauro = Guid.Parse("22222222-0000-0000-0000-00000000aaaa");
        var conversaPedro = Guid.Parse("22222222-0000-0000-0000-00000000bbbb");
        var h = new RunnerHarness();
        h.Definitions.Published = Definicao();
        h.Turns.OnBegin = c => FakeTurnCommands.Granted(
            (c.SessionId == sessaoMauro
                ? Snapshots.Snapshot(new[] { Snapshots.Customer(1, "Qual o horário de atendimento?") })
                : Snapshots.Snapshot(new[] { Snapshots.Customer(1, "Onde fica a unidade?") })) with
            {
                SessionId = c.SessionId,
                ConversationId = c.SessionId == sessaoMauro ? conversaMauro : conversaPedro,
            });
        var turnoMauro = h.SeedRunning(sessaoMauro, lane: 7);
        var turnoPedro = h.SeedRunning(sessaoPedro, lane: 8);

        await h.RunAsync(turnoMauro);
        await h.RunAsync(turnoPedro);

        Assert.Empty(h.Gateway.Requests);
        var deMauro = Assert.Single(h.Turns.Registers, r => r.SessionId == sessaoMauro);
        var dePedro = Assert.Single(h.Turns.Registers, r => r.SessionId == sessaoPedro);
        Assert.Equal((RespostaDoHorario, conversaMauro, turnoMauro.Id), (deMauro.Text, deMauro.TargetConversationId, deMauro.TurnId));
        Assert.Equal(("Na Rua das Flores, 100.", conversaPedro, turnoPedro.Id), (dePedro.Text, dePedro.TargetConversationId, dePedro.TurnId));
    }

    [Fact]
    public async Task Dois_tenants_com_a_mesma_pergunta_e_respostas_diferentes_cada_um_recebe_a_sua()
    {
        var a = Montar("Qual o horário de atendimento?", Definicao(null, new FaqEntry("Qual o horário de atendimento?", "Resposta do tenant A.", null, true)));
        var b = Montar("Qual o horário de atendimento?", Definicao(null, new FaqEntry("Qual o horário de atendimento?", "Resposta do tenant B.", null, true)));
        // O tenant B nem liga a resposta direta nessa pergunta: o tenant A não herda nem empresta a dele.
        var c = Montar("Qual o horário de atendimento?", Definicao(null, new FaqEntry("Qual o horário de atendimento?", "Resposta do tenant C.", null, false)));

        await a.RunAsync(a.SeedRunning());
        await b.RunAsync(b.SeedRunning());
        c.Gateway.Behavior = _ => ScriptedGateway.Reply("Do modelo, para o tenant C.");
        await c.RunAsync(c.SeedRunning());

        Assert.Equal("Resposta do tenant A.", Assert.Single(a.Turns.Registers).Text);
        Assert.Equal("Resposta do tenant B.", Assert.Single(b.Turns.Registers).Text);
        Assert.Empty(a.Gateway.Requests);
        Assert.Empty(b.Gateway.Requests);
        Assert.Single(c.Gateway.Requests);
        Assert.Equal("Do modelo, para o tenant C.", Assert.Single(c.Turns.Registers).Text);
    }

    [Fact]
    public async Task Executor_que_perde_o_turno_para_outro_durante_o_registro_nao_grava_mais_nada()
    {
        // O turno é do executor que tem o token de lane: se outro o reivindica no meio, este perde o turno e a transição final não é gravada por ele.
        var h = Montar("Qual o horário de atendimento?");
        var turno = h.SeedRunning(lane: 7);
        h.Turns.DuringRegister = _ => h.Store.Mutate(turno.Id, t => t.Claim(99, "outro-executor", h.Clock.GetUtcNow(), h.Options.TurnMaxDuration));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.Lost, resultado.Outcome);
        Assert.Empty(h.Gateway.Requests);
        Assert.Single(h.Turns.Registers); // o registro foi um só; o executor que perdeu o turno não grava mais nada
    }
}
