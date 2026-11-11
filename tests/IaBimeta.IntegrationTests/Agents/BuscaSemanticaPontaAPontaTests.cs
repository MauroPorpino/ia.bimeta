using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.Knowledge.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// A resposta por similaridade (ADR 0034) ponta a ponta no turno de IA, com PostgreSQL, Conversations, ContactCenter e AiGateway reais; a UNICA coisa simulada alem do provedor de IA e a busca vetorial (sem
/// pgvector nem Ollama no Windows). Prova o fluxo: acerto sem chamar o modelo, fallback pelo AiGateway, modo de avaliacao, falha da busca, versao defasada, isolamento entre empresas, reinicio do Worker e as metricas.
/// A qualidade da similaridade NAO e provada aqui: e da homologacao.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BuscaSemanticaPontaAPontaTests(PostgresFixture banco)
{
    private const string PerguntaDoFaq = "Preciso de jejum para o exame de glicose?";
    private const string Resposta = "Sim: jejum de 8 horas, apenas agua.";
    private const string Parafrase = "preciso ficar em jejum para fazer o exame de glicose";

    private static AgentDefinitionContent Definicao(CenarioDeAgentes a) =>
        new($"Atenda com cortesia. {a.CanarioDasInstrucoes}", [new FaqEntry(PerguntaDoFaq, Resposta, null, true), new FaqEntry("Qual o horario de coleta?", "Das 7h as 11h.", null, true)], "Audio.", "Vou transferir.");

    private async Task<CenarioDeAgentes> CenarioAsync(string prefixo, SemanticAnswerMode modo)
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, prefixo, publicarAgente: false);
        await a.PublicarAgenteAsync(Definicao(a));
        await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticAnswerSettingsService>().UpdateAsync(modo, 0.9, 0.05));
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Resposta do modelo."));
        return a;
    }

    private static Task<int> NumeroPublicadoAsync(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, async sp => (await sp.GetRequiredService<IAgentDefinitionReader>().GetPublishedVersionAsync(default))!.Number);

    private async Task AcharAsync(CenarioDeAgentes a, params (int Entrada, double Score)[] candidatos)
    {
        var numero = await NumeroPublicadoAsync(a);
        banco.BuscaSemantica.Roteirizar(a.Tenant.TenantId, _ => candidatos.Length == 0
            ? SemanticSearchResult.Of(SemanticSearchOutcome.NoCandidates)
            : new SemanticSearchResult(SemanticSearchOutcome.Found, numero, candidatos.Select(c => new SemanticCandidate(c.Entrada, SemanticTextKind.Question, 0, c.Score)).ToList(), null, 12, 3));
    }

    private static async Task ProcessarAsync(CenarioDeAgentes a)
    {
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
    }

    private static Task<List<SemanticDecision>> DecisoesAsync(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().SemanticDecisions.AsNoTracking().ToListAsync());

    [Fact]
    public async Task Parafrase_com_acerto_seguro_e_respondida_com_o_texto_aprovado_sem_chamar_o_modelo_e_sem_linha_no_ledger()
    {
        var a = await CenarioAsync("semhit", SemanticAnswerMode.Active);
        await AcharAsync(a, (0, 0.95), (1, 0.40));
        var entrada = await a.EntrarAsync("mauro", Parafrase);
        await ProcessarAsync(a);

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.SemanticReply, "semantic-hit"), (resultado.Outcome, resultado.Code));
        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Empty(await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.AsNoTracking().ToListAsync()));
        var resposta = Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal((Resposta, ReplyKind.Informative), (resposta.Text, resposta.ReplyKind));
        Assert.Contains(await a.C.LinhasDoOutboxDaSessaoAsync(entrada.SessionId), o => o.EventType == "conversations.outbound-message-requested");

        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal((AiTurnState.Completed, "semantic-hit"), (turno.State, turno.EndReason));
        var decisao = Assert.Single(await DecisoesAsync(a));
        Assert.Equal((SemanticOutcome.Answered, SemanticAnswerMode.Active, 0, turno.Id), (decisao.Outcome, decisao.Mode, decisao.EntryOrdinal, decisao.TurnId));

        // A pergunta chegou a busca sem dado pessoal e no idioma do FAQ.
        Assert.Equal("pt-BR", Assert.Single(banco.BuscaSemantica.PedidosDe(a.Tenant.TenantId)).Locale);
    }

    [Fact]
    public async Task Sem_candidato_o_fallback_e_o_AiGateway_com_uma_chamada()
    {
        var a = await CenarioAsync("semnone", SemanticAnswerMode.Active);
        await AcharAsync(a);
        var entrada = await a.EntrarAsync("mauro", "voces fazem exame de dna?");
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal(1, a.ChamadasAoModelo);
        Assert.Equal("Resposta do modelo.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
        Assert.Equal(SemanticOutcome.NoCandidates, Assert.Single(await DecisoesAsync(a)).Outcome);
    }

    [Fact]
    public async Task Modo_de_avaliacao_registra_o_que_teria_respondido_mas_o_modelo_responde()
    {
        var a = await CenarioAsync("semeval", SemanticAnswerMode.Evaluate);
        await AcharAsync(a, (0, 0.96));
        await a.EntrarAsync("mauro", Parafrase);
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal(1, a.ChamadasAoModelo);
        Assert.Equal(SemanticOutcome.WouldAnswer, Assert.Single(await DecisoesAsync(a)).Outcome);
    }

    [Fact]
    public async Task Servico_de_vetores_indisponivel_ou_com_erro_nunca_impede_o_atendimento()
    {
        var a = await CenarioAsync("semfail", SemanticAnswerMode.Active);
        banco.BuscaSemantica.Roteirizar(a.Tenant.TenantId, _ => SemanticSearchResult.Of(SemanticSearchOutcome.Unavailable, "unreachable"));
        await a.EntrarAsync("mauro", Parafrase);
        await ProcessarAsync(a);
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        var decisao = Assert.Single(await DecisoesAsync(a));
        Assert.Equal((SemanticOutcome.Unavailable, "unreachable"), (decisao.Outcome, decisao.FailureCode));

        var b = await CenarioAsync("semthrow", SemanticAnswerMode.Active);
        banco.BuscaSemantica.Roteirizar(b.Tenant.TenantId, _ => throw new InvalidOperationException("banco sem pgvector"));
        await b.EntrarAsync("pedro", Parafrase);
        await ProcessarAsync(b);
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await b.ExecutarAsync()).Outcome);
        Assert.Equal(1, b.ChamadasAoModelo);
    }

    [Fact]
    public async Task Indice_de_outra_versao_da_definicao_nunca_responde()
    {
        var a = await CenarioAsync("semstale", SemanticAnswerMode.Active);
        var numero = await NumeroPublicadoAsync(a);
        banco.BuscaSemantica.Roteirizar(a.Tenant.TenantId, _ => new SemanticSearchResult(SemanticSearchOutcome.Found, numero + 1, [new SemanticCandidate(0, SemanticTextKind.Question, 0, 0.99)], null, 1, 1));
        await a.EntrarAsync("mauro", Parafrase);
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal(SemanticOutcome.Stale, Assert.Single(await DecisoesAsync(a)).Outcome);
    }

    [Fact]
    public async Task Negacao_na_mensagem_vai_ao_modelo_mesmo_com_similaridade_altissima()
    {
        var a = await CenarioAsync("semneg", SemanticAnswerMode.Active);
        await AcharAsync(a, (0, 0.99));
        await a.EntrarAsync("mauro", "nao preciso ficar em jejum para o exame de glicose?");
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal(SemanticOutcome.GuardRejected, Assert.Single(await DecisoesAsync(a)).Outcome);
    }

    [Fact]
    public async Task Kill_switch_do_tenant_vale_no_proximo_turno_sem_publicar_nada_e_a_busca_nem_e_consultada()
    {
        var a = await CenarioAsync("semoff", SemanticAnswerMode.Active);
        await AcharAsync(a, (0, 0.95));
        await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticAnswerSettingsService>().UpdateAsync(SemanticAnswerMode.Off, 0.9, 0.05));
        var versaoAntes = await NumeroPublicadoAsync(a);

        await a.EntrarAsync("mauro", Parafrase);
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Empty(banco.BuscaSemantica.PedidosDe(a.Tenant.TenantId));
        Assert.Empty(await DecisoesAsync(a));
        Assert.Equal(versaoAntes, await NumeroPublicadoAsync(a));
    }

    [Fact]
    public async Task Cada_empresa_so_usa_a_propria_configuracao_e_o_proprio_conhecimento()
    {
        var a = await CenarioAsync("semisoa", SemanticAnswerMode.Active);
        var b = await CenarioAsync("semisob", SemanticAnswerMode.Off);
        await AcharAsync(a, (0, 0.95));
        await AcharAsync(b, (0, 0.95));

        var deA = await a.EntrarAsync("mauro", Parafrase);
        var deB = await b.EntrarAsync("pedro", Parafrase);
        await ProcessarAsync(a);
        await ProcessarAsync(b);

        Assert.Equal(TurnRunOutcome.SemanticReply, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await b.ExecutarAsync()).Outcome);
        Assert.Equal(Resposta, Assert.Single(await a.RespostasDaIAAsync(deA.SessionId)).Text);
        Assert.Equal("Resposta do modelo.", Assert.Single(await b.RespostasDaIAAsync(deB.SessionId)).Text);
        Assert.Empty(banco.BuscaSemantica.PedidosDe(b.Tenant.TenantId)); // o tenant desligado nunca consultou a busca
        Assert.Empty(await DecisoesAsync(b));
    }

    [Fact]
    public async Task Execucao_repetida_depois_do_acerto_nao_repete_resposta_nem_decisao_e_as_metricas_contam_exato()
    {
        var a = await CenarioAsync("semidem", SemanticAnswerMode.Active);
        await AcharAsync(a, (0, 0.95));
        var entrada = await a.EntrarAsync("mauro", Parafrase);
        await ProcessarAsync(a);
        Assert.Equal(TurnRunOutcome.SemanticReply, Assert.Single(await a.ExecutarAsync()).Outcome);

        a.Avancar(TimeSpan.FromMinutes(5));
        Assert.Empty(await a.ExecutarAsync()); // nada mais devido
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Single(await DecisoesAsync(a));

        var metricas = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<SemanticMetricsService>().GetAsync(7));
        Assert.Equal((1, 1, 1, 1), (metricas.Decisions, metricas.Answered, metricas.SemanticHits, metricas.CommercialCallsAvoided));
        Assert.Equal(0, metricas.ModelReplies);
    }
}
