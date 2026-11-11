using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Knowledge.Contracts;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A resposta direta por similaridade no <see cref="TurnRunner"/> (ADR 0034) sobre os dublês (sem banco, sem pgvector e sem modelo): as três chaves, o modo de avaliação, a falha segura e as mesmas cercas do
/// FAQ. A prova central de cada acerto é <c>Gateway.Requests</c> VAZIO e o texto APROVADO registrado. Dublês não equivalem à homologação com o índice e o modelo reais.
/// </summary>
public sealed class TurnRunnerSemanticTests
{
    private const string Pergunta = "preciso ficar em jejum para glicose?";

    private static readonly FaqEntry Jejum = new("Preciso de jejum para o exame de glicose?", "Sim, 8 horas.", null, true);

    private static readonly FaqEntry Horario = new("Qual o horario de coleta?", "Das 7h as 11h.", null, true);

    private static RunnerHarness Montar(string texto = Pergunta, SemanticAnswerMode? modo = SemanticAnswerMode.Active, bool global = true, string locale = "pt-BR", params FaqEntry[] faq)
    {
        var h = new RunnerHarness(o => o.SemanticAnswerEnabled = global).Grant(Snapshots.Snapshot(new[] { Snapshots.Customer(1, texto) }, locale: locale));
        h.Definitions.Published = Definitions.Default() with { Faq = faq.Length == 0 ? new[] { Jejum, Horario } : faq };
        h.SemanticSettings.Current = modo is null ? null : FakeSemanticSettings.With(modo.Value);
        h.Search.Result = FakeSearch.Found(1, (0, 0.95), (1, 0.50));
        h.Gateway.Behavior = _ => ScriptedGateway.Reply("Resposta do modelo.");
        return h;
    }

    [Fact]
    public async Task Modo_ativo_com_acerto_seguro_responde_o_texto_aprovado_sem_chamar_o_modelo()
    {
        var h = Montar();
        h.Turns.OnBegin = _ => FakeTurnCommands.Granted(Snapshots.OneText(Pergunta), epoch: 5);
        var turno = h.SeedRunning(lane: 7);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.SemanticReply, AiTurnEndReasons.SemanticHit), resultado);
        Assert.Empty(h.Gateway.Requests);
        var registro = Assert.Single(h.Turns.Registers);
        Assert.Equal((ReplyKind.Informative, "Sim, 8 horas.", 5L), (registro.ReplyKind, registro.Text, registro.Epoch));
        Assert.Empty(registro.AiRequestIds);
        Assert.Equal(("semantic-hit", AiTurnState.Completed), (h.Fresh(turno).EndReason, h.Fresh(turno).State));

        var decisao = Assert.Single(h.SemanticDecisions.Records);
        Assert.Equal((SemanticOutcome.Answered, SemanticAnswerMode.Active, 0, turno.Id), (decisao.Outcome, decisao.Mode, decisao.EntryOrdinal, decisao.TurnId));
        Assert.Equal(0.95, decisao.Score);
    }

    [Fact]
    public async Task A_busca_roda_no_idioma_da_definicao_sem_filtrar_AutoReply_para_que_as_demais_entradas_concorram()
    {
        var h = Montar();
        await h.RunAsync(h.SeedRunning());
        var pedido = Assert.Single(h.Search.Requests);
        Assert.Equal(("pt-BR", false), (pedido.Locale, pedido.OnlyAutoReplyEligible));
    }

    [Fact]
    public async Task A_pergunta_vai_ao_servico_de_vetores_sem_dados_pessoais_por_padrao()
    {
        var h = Montar("preciso de jejum para glicose? meu email e ana@exemplo.com e meu cpf 123.456.789-09");
        await h.RunAsync(h.SeedRunning());
        var consulta = Assert.Single(h.Search.Requests).Query;
        Assert.DoesNotContain("ana@exemplo.com", consulta);
        Assert.DoesNotContain("123.456.789-09", consulta);
        Assert.Contains("[email]", consulta);
    }

    [Fact]
    public async Task Modo_avaliacao_registra_o_que_teria_respondido_mas_o_modelo_responde()
    {
        var h = Montar(modo: SemanticAnswerMode.Evaluate);
        var resultado = await h.RunAsync(h.SeedRunning());

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);
        Assert.Single(h.Gateway.Requests);
        var decisao = Assert.Single(h.SemanticDecisions.Records);
        Assert.Equal((SemanticOutcome.WouldAnswer, SemanticAnswerMode.Evaluate), (decisao.Outcome, decisao.Mode));
        Assert.Equal((ReplyKind.Generated, "Resposta do modelo."), (h.Turns.Registers.Single().ReplyKind, h.Turns.Registers.Single().Text));
    }

    [Theory]
    [InlineData(false, SemanticAnswerMode.Active)] // chave global desligada
    [InlineData(true, SemanticAnswerMode.Off)] // kill switch do tenant
    [InlineData(true, null)] // tenant sem configuracao = desligado
    public async Task Qualquer_chave_desligada_nao_consulta_a_busca_e_o_turno_segue_ao_modelo(bool global, SemanticAnswerMode? modo)
    {
        var h = Montar(modo: modo, global: global);
        var resultado = await h.RunAsync(h.SeedRunning());

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);
        Assert.Empty(h.Search.Requests);
        Assert.Empty(h.SemanticDecisions.Records);
    }

    [Fact]
    public async Task Indice_desligado_no_Knowledge_devolve_Disabled_e_o_turno_segue_ao_modelo_sem_registro()
    {
        var h = Montar();
        h.Search.Result = SemanticSearchResult.Of(SemanticSearchOutcome.Disabled);
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        Assert.Empty(h.SemanticDecisions.Records);
    }

    [Theory]
    [InlineData(0.80, SemanticOutcome.BelowThreshold)]
    public async Task Abaixo_do_limiar_o_modelo_responde_e_a_decisao_e_registrada(double score, SemanticOutcome esperado)
    {
        var h = Montar();
        h.Search.Result = FakeSearch.Found(1, (0, score));
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        Assert.Equal(esperado, Assert.Single(h.SemanticDecisions.Records).Outcome);
    }

    [Fact]
    public async Task Servico_de_vetores_fora_do_ar_nao_atrapalha_o_atendimento()
    {
        var h = Montar();
        h.Search.Result = SemanticSearchResult.Of(SemanticSearchOutcome.Unavailable, "unreachable");
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        var decisao = Assert.Single(h.SemanticDecisions.Records);
        Assert.Equal((SemanticOutcome.Unavailable, "unreachable"), (decisao.Outcome, decisao.FailureCode));
    }

    [Fact]
    public async Task Excecao_da_busca_ou_da_configuracao_nunca_derruba_o_turno()
    {
        var busca = Montar();
        busca.Search.Failure = new InvalidOperationException("banco sem pgvector");
        Assert.Equal(TurnRunOutcome.Replied, (await busca.RunAsync(busca.SeedRunning())).Outcome);

        var configuracao = Montar();
        configuracao.SemanticSettings.Failure = new InvalidOperationException("tabela ausente");
        Assert.Equal(TurnRunOutcome.Replied, (await configuracao.RunAsync(configuracao.SeedRunning())).Outcome);
    }

    [Fact]
    public async Task Falha_do_registro_da_metrica_nao_derruba_nem_muda_a_resposta()
    {
        var h = Montar();
        h.SemanticDecisions.Failure = new InvalidOperationException("tabela ausente");
        var resultado = await h.RunAsync(h.SeedRunning());
        Assert.Equal(TurnRunOutcome.SemanticReply, resultado.Outcome);
        Assert.Equal("Sim, 8 horas.", Assert.Single(h.Turns.Registers).Text);
    }

    [Fact]
    public async Task Indice_de_outra_versao_nunca_responde()
    {
        var h = Montar();
        h.Definitions.PublishedNumber = 2; // o indice ainda e da versao 1
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        Assert.Equal(SemanticOutcome.Stale, Assert.Single(h.SemanticDecisions.Records).Outcome);
    }

    [Fact]
    public async Task Leitura_sem_numero_da_versao_falha_fechado()
    {
        var h = Montar();
        h.Definitions.PublishedNumber = null;
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        Assert.Empty(h.SemanticDecisions.Records); // sem versao publicada confiavel nem se decide
    }

    [Fact]
    public async Task Negacao_na_mensagem_recusa_a_resposta_direta_mesmo_com_similaridade_alta()
    {
        var h = Montar("nao preciso ficar em jejum para glicose?");
        h.Search.Result = FakeSearch.Found(1, (0, 0.98));
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        Assert.Equal(SemanticOutcome.GuardRejected, Assert.Single(h.SemanticDecisions.Records).Outcome);
    }

    [Fact]
    public async Task O_FAQ_deterministico_tem_prioridade_e_a_busca_semantica_nem_roda()
    {
        var h = Montar("Preciso de jejum para o exame de glicose?");
        var resultado = await h.RunAsync(h.SeedRunning());
        Assert.Equal(new TurnRunResult(TurnRunOutcome.FaqReply, "faq-hit"), resultado);
        Assert.Empty(h.Search.Requests);
    }

    [Fact]
    public async Task Entrada_duplicada_ou_sem_AutoReply_no_FAQ_nunca_vai_a_busca_semantica()
    {
        var semDireta = new FaqEntry("Preciso de jejum para o exame de glicose?", "Sim.", null, false);
        var h = Montar("Preciso de jejum para o exame de glicose?", faq: [semDireta, Horario]);
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        Assert.Empty(h.Search.Requests);
    }

    [Fact]
    public async Task Idioma_da_conversa_diferente_do_do_FAQ_vai_ao_modelo_sem_consultar_a_busca()
    {
        var h = Montar(locale: "en");
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        Assert.Empty(h.Search.Requests);
    }

    [Fact]
    public async Task Duas_mensagens_no_grupo_nunca_vao_a_busca_semantica()
    {
        var h = new RunnerHarness(o => o.SemanticAnswerEnabled = true).Grant(RunnerHarness.Group(Snapshots.Customer(1, "Oi"), Snapshots.Customer(2, Pergunta)));
        h.Definitions.Published = Definitions.Default() with { Faq = new[] { Jejum, Horario } };
        h.SemanticSettings.Current = FakeSemanticSettings.With(SemanticAnswerMode.Active);
        h.Search.Result = FakeSearch.Found(1, (0, 0.99));
        h.Gateway.Behavior = _ => ScriptedGateway.Reply("Resposta do modelo.");
        Assert.Equal(TurnRunOutcome.Replied, (await h.RunAsync(h.SeedRunning())).Outcome);
        Assert.Empty(h.Search.Requests);
    }

    [Fact]
    public async Task A_resposta_defasada_continua_recusada_pelas_cercas_do_Conversations()
    {
        var h = Montar();
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.StaleState, null, 1, 1);
        var turno = h.SeedRunning();
        var resultado = await h.RunAsync(turno);
        Assert.Equal(new TurnRunResult(TurnRunOutcome.Discarded, "stale-state"), resultado);
        Assert.Empty(h.Gateway.Requests);
        Assert.Equal(AiTurnState.Discarded, h.Fresh(turno).State);
    }
}
