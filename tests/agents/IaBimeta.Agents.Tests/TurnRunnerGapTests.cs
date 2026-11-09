using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// O registro de lacunas no <see cref="TurnRunner"/> (ADR 0030): quando uma mensagem elegível NÃO teve resposta direta, o turno entrega ao registrador só referências e o texto NORMALIZADO em memória (o tipo
/// distingue ausência de correspondência, ambiguidade e entrada sem resposta direta); o que não é elegível (várias mensagens, mídia, outro idioma) e o que está no FAQ com resposta direta nunca viram lacuna; e
/// o registro é melhor esforço: nenhuma falha dele derruba, atrasa nem muda a resposta do turno.
/// </summary>
public sealed class TurnRunnerGapTests
{
    private static readonly FaqEntry Horario = new("Qual o horário de atendimento?", "De segunda a sexta, das 7h às 17h.", new[] { "que horas vocês abrem" }, true);

    private static AgentDefinitionContent Definicao(string? idioma = null, params FaqEntry[] faq) =>
        Definitions.Default() with { Faq = faq.Length == 0 ? new[] { Horario } : faq, FaqLocale = idioma };

    private static RunnerHarness Montar(string texto, AgentDefinitionContent? definicao = null, string locale = "pt-BR", Action<AgentsOptions>? opcoes = null)
    {
        var h = new RunnerHarness(opcoes).Grant(Snapshots.Snapshot(new[] { Snapshots.Customer(1, texto) }, locale: locale));
        h.Definitions.Published = definicao ?? Definicao();
        h.Gateway.Behavior = _ => ScriptedGateway.Reply("Resposta do modelo.");
        return h;
    }

    // Os três tipos

    [Fact]
    public async Task Pergunta_sem_correspondencia_vira_candidata_com_so_referencias_e_o_texto_normalizado_e_o_turno_segue_ao_modelo()
    {
        var h = Montar("Preciso levar DOCUMENTO para a coleta?!");
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Replied, "replied"), resultado); // o atendimento não muda
        Assert.Single(h.Gateway.Requests);
        var candidata = Assert.Single(h.Gaps.Candidates);
        Assert.Equal(KnowledgeGapKind.NoMatch, candidata.Kind);
        Assert.Equal("preciso levar documento para a coleta", candidata.NormalizedQuestion);
        Assert.Equal((TestContexts.TenantA, turno.Id, turno.SessionId, Snapshots.ConversationId), (candidata.Tenant, candidata.TurnId, candidata.SessionId, candidata.ConversationId));
        Assert.NotEqual(Guid.Empty, candidata.MessageId);
    }

    [Fact]
    public async Task A_candidata_carrega_o_identificador_da_mensagem_do_cliente_e_a_hora_do_relogio_do_servidor()
    {
        var mensagem = Snapshots.Customer(1, "Aceitam cartão?");
        var h = new RunnerHarness().Grant(Snapshots.Snapshot(new[] { mensagem }));
        h.Definitions.Published = Definicao();

        await h.RunAsync(h.SeedRunning());

        var candidata = Assert.Single(h.Gaps.Candidates);
        Assert.Equal((mensagem.MessageId, h.Clock.GetUtcNow()), (candidata.MessageId, candidata.At));
    }

    [Fact]
    public async Task Duas_entradas_para_a_mesma_pergunta_viram_candidata_ambigua()
    {
        var duplicada = new FaqEntry("qual o horario de atendimento", "Outra.", null, true);
        var h = Montar("Qual o horário de atendimento?", Definicao(null, Horario, duplicada));

        await h.RunAsync(h.SeedRunning());

        Assert.Equal(KnowledgeGapKind.Ambiguous, Assert.Single(h.Gaps.Candidates).Kind);
        Assert.Single(h.Gateway.Requests);
    }

    [Fact]
    public async Task Entrada_sem_resposta_direta_vira_candidata_do_tipo_resposta_direta_desligada()
    {
        var semDireta = new FaqEntry("Qual o horário de atendimento?", "Das 7h às 17h.", null, false);
        var h = Montar("Qual o horário de atendimento?", Definicao(null, semDireta));

        await h.RunAsync(h.SeedRunning());

        Assert.Equal(KnowledgeGapKind.NotAutoReply, Assert.Single(h.Gaps.Candidates).Kind);
    }

    [Fact]
    public async Task O_FAQ_publicado_vazio_tambem_registra_o_que_os_clientes_perguntam()
    {
        var vazio = Definitions.Default() with { Faq = Array.Empty<FaqEntry>() };
        var h = Montar("Qual o horário de atendimento?", vazio);

        await h.RunAsync(h.SeedRunning());

        Assert.Equal(KnowledgeGapKind.NoMatch, Assert.Single(h.Gaps.Candidates).Kind);
    }

    // O que NÃO é lacuna

    [Fact]
    public async Task A_pergunta_respondida_direto_pelo_FAQ_nunca_e_lacuna()
    {
        var h = Montar("Qual o horário de atendimento?");

        var resultado = await h.RunAsync(h.SeedRunning());

        Assert.Equal(TurnRunOutcome.FaqReply, resultado.Outcome);
        Assert.Empty(h.Gaps.Candidates);
        Assert.Empty(h.Gateway.Requests);
    }

    [Fact]
    public async Task Com_a_resposta_direta_desligada_a_pergunta_que_esta_no_FAQ_continua_sem_ser_lacuna()
    {
        var h = Montar("Qual o horário de atendimento?", opcoes: o => o.FaqDirectAnswerEnabled = false);

        await h.RunAsync(h.SeedRunning());

        Assert.Single(h.Gateway.Requests); // vai ao modelo...
        Assert.Empty(h.Gaps.Candidates); // ...mas a pergunta está no FAQ: não é lacuna
    }

    [Fact]
    public async Task Com_a_resposta_direta_desligada_a_pergunta_fora_do_FAQ_continua_registrando_a_lacuna()
    {
        var h = Montar("Preciso de documento?", opcoes: o => o.FaqDirectAnswerEnabled = false);

        await h.RunAsync(h.SeedRunning());

        Assert.Equal(KnowledgeGapKind.NoMatch, Assert.Single(h.Gaps.Candidates).Kind); // aprender com os clientes independe de responder direto
    }

    [Fact]
    public async Task Varias_mensagens_no_grupo_nao_sao_elegiveis_nem_viram_lacuna()
    {
        var h = new RunnerHarness().Grant(RunnerHarness.Group(Snapshots.Customer(1, "Oi"), Snapshots.Customer(2, "Preciso de documento?")));
        h.Definitions.Published = Definicao();

        await h.RunAsync(h.SeedRunning());

        Assert.Empty(h.Gaps.Candidates);
        Assert.Single(h.Gateway.Requests);
    }

    [Fact]
    public async Task Mensagem_que_nao_e_texto_nao_e_elegivel()
    {
        var h = new RunnerHarness().Grant(Snapshots.Snapshot(new[] { Snapshots.Customer(1, "Legenda da foto", MessageKind.Image) }));
        h.Definitions.Published = Definicao();

        await h.RunAsync(h.SeedRunning());

        Assert.Empty(h.Gaps.Candidates);
    }

    [Fact]
    public async Task Conversa_em_outro_idioma_nao_e_elegivel_para_o_FAQ_e_nao_vira_lacuna()
    {
        var h = Montar("Do I need an ID?", locale: "en-US");

        await h.RunAsync(h.SeedRunning());

        Assert.Empty(h.Gaps.Candidates);
        Assert.Single(h.Gateway.Requests);
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("?!?")]
    [InlineData("...")]
    public async Task Mensagem_so_de_simbolos_nao_tem_o_que_revisar(string texto)
    {
        var h = Montar(texto);

        await h.RunAsync(h.SeedRunning());

        Assert.Empty(h.Gaps.Candidates);
        Assert.Single(h.Gateway.Requests);
    }

    [Fact]
    public async Task Sem_definicao_publicada_nao_ha_o_que_registrar()
    {
        var h = Montar("Preciso de documento?");
        h.Definitions.Published = null;

        await h.RunAsync(h.SeedRunning());

        Assert.Empty(h.Gaps.Candidates);
    }

    // Melhor esforço

    [Fact]
    public async Task Falha_do_registrador_nao_derruba_nem_muda_a_resposta_do_turno()
    {
        var h = Montar("Preciso levar documento para a coleta?");
        h.Gaps.Behavior = _ => throw new InvalidOperationException("tabela inexistente: dado sensível Mauro exame A");
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Replied, "replied"), resultado);
        Assert.Equal("Resposta do modelo.", Assert.Single(h.Turns.Registers).Text);
        Assert.Equal((AiTurnState.Completed, "replied"), (h.Fresh(turno).State, h.Fresh(turno).EndReason));
    }

    [Fact]
    public async Task O_cancelamento_do_proprio_turno_passa_pelo_registrador_e_o_turno_volta_a_fila_sem_gastar_tentativa()
    {
        var h = Montar("Preciso levar documento para a coleta?");
        h.Gaps.Behavior = _ => throw new OperationCanceledException();
        using var cancelamento = new CancellationTokenSource();
        cancelamento.Cancel();
        var turno = h.SeedRunning();

        var resultado = await h.Runner.RunAsync(turno.Id, cancelamento.Token);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "shutdown"), resultado);
        Assert.Empty(h.Gateway.Requests);
    }

    [Fact]
    public async Task O_registro_acontece_antes_da_chamada_ao_modelo_e_uma_vez_por_execucao()
    {
        var ordem = new List<string>();
        var h = Montar("Preciso de documento?");
        h.Gaps.Behavior = _ =>
        {
            ordem.Add("lacuna");
            return Task.CompletedTask;
        };
        h.Gateway.DuringCall = () => ordem.Add("modelo");

        await h.RunAsync(h.SeedRunning());

        Assert.Equal(new[] { "lacuna", "modelo" }, ordem);
    }
}
