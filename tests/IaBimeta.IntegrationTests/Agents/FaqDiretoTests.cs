using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// A resposta direta do FAQ (ADR 0029) ponta a ponta, com PostgreSQL real, Conversations, ContactCenter e AiGateway reais; o único dublê é o provedor de IA, que CONTA as chamadas por credencial. A prova central
/// de cada acerto é a contagem de chamadas ao provedor ZERO e nenhuma linha no ledger de consumo; a de cada recusa é UMA chamada, como antes. Também o isolamento entre tenants, o idioma, o FAQ antigo gravado
/// no banco, o reinício do Worker (lease vencido) e dois executores ao mesmo tempo.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FaqDiretoTests(PostgresFixture banco)
{
    private const string Pergunta = "Qual o horário de atendimento?";
    private const string Resposta = "De segunda a sexta, das 7h às 17h.";

    private static AgentDefinitionContent Definicao(CenarioDeAgentes a, string? idioma = null, params FaqEntry[] faq) =>
        new(
            $"Atenda com cortesia. {a.CanarioDasInstrucoes}",
            faq.Length == 0 ? new[] { new FaqEntry(Pergunta, Resposta, new[] { "que horas vocês abrem" }, true) } : faq,
            "Recebemos seu áudio, mas por enquanto só lemos texto.",
            "Vou transferir você para um atendente.",
            idioma);

    private static async Task<CenarioDeAgentes> CenarioAsync(PostgresFixture banco, string prefixo, params FaqEntry[] faq)
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, prefixo, publicarAgente: false);
        await a.PublicarAgenteAsync(Definicao(a, null, faq));
        return a;
    }

    private static Task<List<IaBimeta.AiGateway.Domain.AiRequest>> LedgerAsync(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.AsNoTracking().ToListAsync());

    private static async Task ProcessarAsync(CenarioDeAgentes a)
    {
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
    }

    // O acerto

    [Fact]
    public async Task Pergunta_do_FAQ_e_respondida_sem_nenhuma_chamada_ao_provedor_e_sem_linha_no_ledger()
    {
        var a = await CenarioAsync(banco, "faqhit");
        var entrada = await a.EntrarAsync("mauro", Pergunta);
        await ProcessarAsync(a);

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.FaqReply, "faq-hit"), (resultado.Outcome, resultado.Code));
        Assert.Equal(0, a.ChamadasAoModelo); // nenhuma chamada ao provedor
        Assert.DoesNotContain(a.Provedor.Requests, r => r.Credential.Secret == a.Canario);
        Assert.Empty(await LedgerAsync(a)); // e nenhum token reservado nem consumido

        // A resposta foi aceita pelo Conversations, é determinística (Informative, sem chamada ao modelo) e cobre a mensagem.
        var resposta = Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal((Resposta, ReplyKind.Informative), (resposta.Text, resposta.ReplyKind));
        var sessao = (await a.C.SessaoAsync(entrada.SessionId))!;
        Assert.Equal((HandlingMode.AiAutonomous, entrada.InboundSeq), (sessao.HandlingMode, sessao.AnsweredThroughSeq));
        Assert.Contains(await a.C.LinhasDoOutboxDaSessaoAsync(entrada.SessionId), o => o.EventType == "conversations.outbound-message-requested");

        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal((AiTurnState.Completed, "faq-hit"), (turno.State, turno.EndReason));

        // Nada mais devido: o executor não repete a resposta.
        a.Avancar(TimeSpan.FromMinutes(5));
        Assert.Empty(await a.ExecutarAsync());
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
    }

    [Theory]
    [InlineData("QUAL O HORARIO DE ATENDIMENTO!!!")]
    [InlineData("que horas voces abrem?")]
    public async Task A_pergunta_normalizada_e_o_alias_acertam_do_mesmo_jeito(string mensagem)
    {
        var a = await CenarioAsync(banco, "faqnorm");
        var entrada = await a.EntrarAsync("mauro", mensagem);
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.FaqReply, Assert.Single(await a.ExecutarAsync()).Outcome);

        Assert.Equal(Resposta, Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
        Assert.Equal(0, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Pergunta_fora_do_FAQ_vai_ao_modelo_e_so_ela_consome_tokens()
    {
        var a = await CenarioAsync(banco, "faqmisto");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Em geral não é necessário jejum."));
        var noFaq = await a.EntrarAsync("mauro", Pergunta);
        var foraDoFaq = await a.EntrarAsync("pedro", "Preciso de jejum para o hemograma?");
        await ProcessarAsync(a);

        var resultados = await a.ExecutarAsync();

        Assert.Equal(1, resultados.Count(r => r.Outcome == TurnRunOutcome.FaqReply));
        Assert.Equal(1, resultados.Count(r => r.Outcome == TurnRunOutcome.Replied));
        Assert.Equal(1, a.ChamadasAoModelo); // só a pergunta de Pedro chegou ao provedor
        Assert.Contains("Preciso de jejum", a.PedidosAoModelo.Single().Messages[^1].Text);
        Assert.DoesNotContain(Pergunta, string.Join("\n", a.PedidosAoModelo.Single().Messages.Select(m => m.Text)));
        Assert.Single(await LedgerAsync(a)); // uma única linha no ledger: a do modelo

        Assert.Equal(Resposta, Assert.Single(await a.RespostasDaIAAsync(noFaq.SessionId)).Text);
        Assert.Equal("Em geral não é necessário jejum.", Assert.Single(await a.RespostasDaIAAsync(foraDoFaq.SessionId)).Text);
        Assert.Equal("faq-hit", (await a.UnicoTurnoAsync(noFaq.SessionId)).EndReason);
        Assert.Equal("replied", (await a.UnicoTurnoAsync(foraDoFaq.SessionId)).EndReason);
    }

    [Fact]
    public async Task Entrada_sem_resposta_direta_e_ambiguidade_vao_ao_modelo_com_o_FAQ_no_prompt()
    {
        var semDireta = await CenarioAsync(banco, "faqsem", new FaqEntry(Pergunta, Resposta, null, false));
        var ambigua = await CenarioAsync(banco, "faqamb", new FaqEntry(Pergunta, Resposta, null, true), new FaqEntry("qual o horario de atendimento", "Outra resposta.", null, true));
        foreach (var cenario in new[] { semDireta, ambigua })
        {
            cenario.Roteiro = _ => cenario.Sucesso(CenarioDeAgentes.Responder("Resposta do modelo."));
            var entrada = await cenario.EntrarAsync("mauro", Pergunta);
            await ProcessarAsync(cenario);

            Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await cenario.ExecutarAsync()).Outcome);

            Assert.Equal(1, cenario.ChamadasAoModelo);
            Assert.Contains(Pergunta, cenario.PedidosAoModelo.Single().SystemInstruction); // o FAQ continua no prompt, só a resposta direta é que não vale
            Assert.Equal("Resposta do modelo.", Assert.Single(await cenario.RespostasDaIAAsync(entrada.SessionId)).Text);
        }
    }

    // Isolamento entre tenants

    [Fact]
    public async Task Dois_tenants_com_a_mesma_pergunta_cada_um_recebe_a_resposta_do_proprio_FAQ_e_o_que_nao_liga_a_resposta_direta_chama_o_modelo()
    {
        var a = await CenarioAsync(banco, "faqisoa", new FaqEntry(Pergunta, "Resposta do tenant A.", null, true));
        var b = await CenarioAsync(banco, "faqisob", new FaqEntry(Pergunta, "Resposta do tenant B.", null, true));
        var c = await CenarioAsync(banco, "faqisoc", new FaqEntry(Pergunta, "Resposta do tenant C.", null, false));
        c.Roteiro = _ => c.Sucesso(CenarioDeAgentes.Responder("Resposta do modelo ao tenant C."));

        // O MESMO nome de contato e a MESMA pergunta nos três tenants.
        var deA = await a.EntrarAsync("mauro", Pergunta);
        var deB = await b.EntrarAsync("mauro", Pergunta);
        var deC = await c.EntrarAsync("mauro", Pergunta);
        await ProcessarAsync(a);

        // O executor REAL do Worker atende todos os tenants com turno devido.
        var executor = a.ExecutorDoWorker();
        for (var i = 0; i < 5 && (await a.RespostasDaIAAsync(deA.SessionId)).Count + (await b.RespostasDaIAAsync(deB.SessionId)).Count + (await c.RespostasDaIAAsync(deC.SessionId)).Count < 3; i++)
        {
            await executor.RunOnceAsync(default);
        }

        Assert.Equal("Resposta do tenant A.", Assert.Single(await a.RespostasDaIAAsync(deA.SessionId)).Text);
        Assert.Equal("Resposta do tenant B.", Assert.Single(await b.RespostasDaIAAsync(deB.SessionId)).Text);
        Assert.Equal("Resposta do modelo ao tenant C.", Assert.Single(await c.RespostasDaIAAsync(deC.SessionId)).Text);
        Assert.Equal((0, 0, 1), (a.ChamadasAoModelo, b.ChamadasAoModelo, c.ChamadasAoModelo));
        Assert.Empty(await LedgerAsync(a));
        Assert.Empty(await LedgerAsync(b));
        Assert.Single(await LedgerAsync(c));
    }

    [Fact]
    public async Task O_FAQ_de_um_tenant_nao_responde_a_conversa_de_outro_mesmo_com_o_turno_executado_no_contexto_errado()
    {
        var a = await CenarioAsync(banco, "faqctxa", new FaqEntry(Pergunta, "Resposta do tenant A.", null, true));
        var b = await CenarioAsync(banco, "faqctxb", new FaqEntry("Outra pergunta qualquer?", "Resposta do tenant B.", null, true));
        var deB = await b.EntrarAsync("pedro", Pergunta); // a pergunta do FAQ de A, feita no tenant B
        await ProcessarAsync(b);
        var turnoDeB = await b.UnicoTurnoAsync(deB.SessionId);

        // O contexto do tenant A não encontra o turno de B: nada executa, e o turno de B segue agendado.
        var tentativa = await a.RodarTurnoAsync(turnoDeB.Id);
        Assert.Equal(TurnRunOutcome.Skipped, tentativa.Outcome);
        Assert.Null(await a.C.HistoricoAsync(deB.SessionId)); // a sessão de B nem aparece para o contexto de A (404, nunca uma lista vazia)
        Assert.Empty(await b.RespostasDaIAAsync(deB.SessionId));

        // No tenant B, a pergunta do FAQ de A não é do FAQ dele: vai ao modelo dele, nunca à resposta de A.
        b.Roteiro = _ => b.Sucesso(CenarioDeAgentes.Responder("Do modelo, no tenant B."));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await b.ExecutarAsync()).Outcome);
        Assert.Equal("Do modelo, no tenant B.", Assert.Single(await b.RespostasDaIAAsync(deB.SessionId)).Text);
        Assert.Equal(0, a.ChamadasAoModelo);
    }

    // O teste de aceitação Mauro e Pedro com o FAQ direto ligado

    [Fact]
    public async Task Mauro_e_Pedro_com_o_FAQ_direto_ligado_a_requisicao_de_Mauro_tem_o_contexto_do_exame_A_nunca_o_do_B_e_a_resposta_vai_so_ao_atendimento_dele()
    {
        var a = await CenarioAsync(banco, "faqmp", new FaqEntry("Qual o jejum do exame A?", "Para o exame A, jejum de 8 horas.", null, true));
        a.Roteiro = p => a.Sucesso(CenarioDeAgentes.Responder("Resposta sobre: " + p.Messages[^1].Text));

        var mauro = await a.EntrarAsync("mauro", "Qual o jejum do exame A?"); // direto
        var pedro = await a.EntrarAsync("pedro", "Qual o preço do exame B?"); // modelo
        await ProcessarAsync(a);
        var primeiros = await a.ExecutarAsync();
        Assert.Equal((1, 1), (primeiros.Count(r => r.Outcome == TurnRunOutcome.FaqReply), primeiros.Count(r => r.Outcome == TurnRunOutcome.Replied)));

        // Mauro pergunta de novo, sem dizer qual exame: o modelo vê o exame A, que está na CONVERSA DELE (inclusive a resposta direta que ele recebeu), e nada de Pedro.
        await a.EntrarAsync("mauro", "Preciso ficar em jejum para esse exame?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);

        Assert.Equal(2, a.ChamadasAoModelo); // a de Pedro e a segunda de Mauro; a primeira de Mauro foi direta
        var pedidoDeMauro = a.PedidosAoModelo[1];
        var conversaDeMauro = string.Join("\n", pedidoDeMauro.Messages.Select(m => m.Text));
        Assert.Contains("exame A", conversaDeMauro);
        Assert.Contains("jejum de 8 horas", conversaDeMauro);
        Assert.Contains("esse exame", conversaDeMauro);
        Assert.DoesNotContain("exame B", conversaDeMauro);
        Assert.DoesNotContain("Pedro", conversaDeMauro);

        var deMauro = await a.RespostasDaIAAsync(mauro.SessionId);
        Assert.Equal(new[] { "Para o exame A, jejum de 8 horas.", "Resposta sobre: Preciso ficar em jejum para esse exame?" }, deMauro.Select(m => m.Text));
        Assert.Equal("Resposta sobre: Qual o preço do exame B?", Assert.Single(await a.RespostasDaIAAsync(pedro.SessionId)).Text);
    }

    // Idioma

    [Fact]
    public async Task Conversa_em_ingles_com_o_FAQ_em_portugues_vai_ao_modelo()
    {
        var a = await CenarioAsync(banco, "faqidi");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Our hours are 7am to 5pm."));
        var entrada = await a.C.EntrarAsync("john", Pergunta, quando: a.Relogio.GetUtcNow(), idiomaDoContato: "en-US");
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);

        Assert.Equal(1, a.ChamadasAoModelo);
        Assert.Equal("Our hours are 7am to 5pm.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
    }

    [Fact]
    public async Task FAQ_declarado_em_ingles_responde_direto_na_conversa_em_ingles()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "faqen");
        await a.PublicarAgenteAsync(Definicao(a, "en-US", new FaqEntry("What are your opening hours?", "Monday to Friday, 7am to 5pm.", null, true)));
        var entrada = await a.C.EntrarAsync("john", "What are your opening hours?", quando: a.Relogio.GetUtcNow(), idiomaDoContato: "en-US");
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.FaqReply, Assert.Single(await a.ExecutarAsync()).Outcome);

        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Equal("Monday to Friday, 7am to 5pm.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
    }

    // A definição no banco

    [Fact]
    public async Task A_definicao_publicada_guarda_aliases_resposta_direta_e_idioma_e_a_leitura_devolve_o_mesmo_conteudo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "faqdb", publicarAgente: false);
        await a.PublicarAgenteAsync(Definicao(a, "pt-br", new FaqEntry("  Qual o horário?  ", "Das 7h às 17h.", new[] { "  que horas abrem  ", "QUE HORAS ABREM?", "horário de funcionamento" }, true), new FaqEntry("Onde fica?", "Na matriz.")));

        var lida = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<IAgentDefinitionReader>().GetPublishedAsync(default));

        Assert.NotNull(lida);
        Assert.Equal("pt-BR", lida!.FaqLocale);
        Assert.Equal(new[] { "que horas abrem", "horário de funcionamento" }, lida.Faq[0].Aliases);
        Assert.Equal((true, false), (lida.Faq[0].AutoReply, lida.Faq[1].AutoReply));
        Assert.Equal("Qual o horário?", lida.Faq[0].Question);

        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("SELECT faq_locale, faq::text FROM agents.agent_definition_versions WHERE tenant_id = @t AND status = 2", conexao);
        comando.Parameters.AddWithValue("t", a.Tenant.TenantId.Value);
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync());
        Assert.Equal("pt-BR", leitor.GetString(0));
        Assert.Contains("\"autoReply\": true", leitor.GetString(1)); // jsonb normaliza o espaço depois dos dois pontos
    }

    [Fact]
    public async Task FAQ_antigo_gravado_no_banco_sem_os_campos_novos_nem_idioma_continua_indo_ao_modelo_sem_resposta_direta()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "faqold", publicarAgente: false);
        await a.PublicarAgenteAsync(Definicao(a, null, new FaqEntry(Pergunta, Resposta)));
        // O que o banco tinha antes do ADR 0029: pares pergunta e resposta, nenhum idioma.
        await using (var conexao = new NpgsqlConnection(TestDatabase.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = new NpgsqlCommand(
                "UPDATE agents.agent_definition_versions SET faq = @faq::jsonb, faq_locale = NULL WHERE tenant_id = @t AND status = 2", conexao);
            comando.Parameters.AddWithValue("faq", "[{\"question\":\"" + Pergunta + "\",\"answer\":\"" + Resposta + "\"}]");
            comando.Parameters.AddWithValue("t", a.Tenant.TenantId.Value);
            Assert.Equal(1, await comando.ExecuteNonQueryAsync());
        }

        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Resposta do modelo."));
        var entrada = await a.EntrarAsync("mauro", Pergunta);
        await ProcessarAsync(a);

        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);

        Assert.Equal(1, a.ChamadasAoModelo); // a resposta direta está DESLIGADA nas definições antigas
        Assert.Equal("Resposta do modelo.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
        Assert.Contains(Pergunta, a.PedidosAoModelo.Single().SystemInstruction);
    }

    // Reinício do Worker e concorrência

    [Fact]
    public async Task Worker_que_morre_com_o_turno_reivindicado_o_lease_vence_e_so_uma_resposta_sai_sem_chamar_o_modelo()
    {
        var a = await CenarioAsync(banco, "faqmorto");
        var entrada = await a.EntrarAsync("mauro", Pergunta);
        await ProcessarAsync(a);

        // Um executor reivindica o turno e "morre": nada roda.
        Assert.Single(await a.ReivindicarAsync());
        Assert.Equal(AiTurnState.Running, (await a.UnicoTurnoAsync(entrada.SessionId)).State);
        Assert.Empty(await a.ExecutarAsync());

        a.Avancar(TimeSpan.FromSeconds(91));
        var retomado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.FaqReply, "faq-hit"), (retomado.Outcome, retomado.Code));
        Assert.Equal(1, (await a.UnicoTurnoAsync(entrada.SessionId)).Attempts); // o lease perdido gasta uma tentativa
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Empty(await a.ExecutarAsync());
        Assert.Equal(0, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Worker_que_morre_depois_de_a_resposta_direta_ser_aceita_o_turno_retomado_nao_responde_de_novo()
    {
        var a = await CenarioAsync(banco, "faqmorto2");
        var entrada = await a.EntrarAsync("mauro", Pergunta);
        await ProcessarAsync(a);

        // O executor registrou a resposta direta no Conversations e morreu antes de marcar o turno como concluído.
        var reivindicado = Assert.Single(await a.ReivindicarAsync());
        var linha = await a.UnicoTurnoAsync(entrada.SessionId);
        var inicio = await a.C.IniciarTurnoAsync(entrada.SessionId, reivindicado, linha.LaneToken!.Value);
        var registro = await a.C.ResponderAsync(entrada.SessionId, reivindicado, inicio, entrada.ConversationId, Resposta);
        Assert.Equal(RegisterReplyOutcome.Accepted, registro.Outcome);

        a.Avancar(TimeSpan.FromSeconds(91));
        var retomado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Discarded, "nothing-pending"), (retomado.Outcome, retomado.Code)); // nada pendente: não responde de novo
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(0, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Dois_executores_ao_mesmo_tempo_so_um_leva_o_turno_e_a_resposta_direta_sai_uma_vez()
    {
        var a = await CenarioAsync(banco, "faqpar");
        var entrada = await a.EntrarAsync("mauro", Pergunta);
        await ProcessarAsync(a);

        var resultados = await Task.WhenAll(a.ExecutarAsync(), a.ExecutarAsync(), a.ExecutarAsync());

        Assert.Equal(1, resultados.SelectMany(r => r).Count(r => r.Outcome == TurnRunOutcome.FaqReply));
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(0, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Muitos_atendimentos_com_a_mesma_pergunta_em_paralelo_cada_um_recebe_uma_resposta_so_a_sua()
    {
        var a = await CenarioAsync(banco, "faqmuitos");
        var sessoes = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            sessoes.Add((await a.EntrarAsync($"contato-{i}", Pergunta)).SessionId);
        }

        await ProcessarAsync(a);
        var lotes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => a.ExecutarEmParaleloAsync()));
        await a.ExecutarEmParaleloAsync();

        Assert.Equal(6, await TurnosFaqAsync(a)); // todos os seis terminaram como acerto do FAQ, mesmo com o teto de turnos simultâneos do tenant
        Assert.All(lotes.SelectMany(l => l), r => Assert.Equal(TurnRunOutcome.FaqReply, r.Outcome));
        foreach (var sessao in sessoes)
        {
            Assert.Equal(Resposta, Assert.Single(await a.RespostasDaIAAsync(sessao)).Text); // exatamente uma resposta por atendimento
        }

        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Empty(await LedgerAsync(a));
    }

    private static async Task<int> TurnosFaqAsync(CenarioDeAgentes a) => (await a.TurnosAsync()).Count(t => t.EndReason == "faq-hit");
}
