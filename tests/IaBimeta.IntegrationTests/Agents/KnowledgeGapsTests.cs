using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// As lacunas do FAQ (ADR 0030) com PostgreSQL real: o registro atômico e idempotente (hash com segredo do tenant, só referências, nenhum texto de cliente, o teto por tenant, a concorrência), o isolamento
/// entre empresas, a leitura da pergunta e da resposta humana com a autorização do Conversations, a mensagem removida, o envio ao rascunho (concorrência, nada publicado) e o filtro de sequência do histórico
/// que sustenta a busca da resposta humana.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class KnowledgeGapsTests(PostgresFixture banco)
{
    private const string Pergunta = "Preciso levar documento para a coleta?";

    private static string Normalizada(string texto) => FaqMatcher.Normalize(texto);

    private static KnowledgeGapCandidate Candidata(
        CenarioDeAgentes a, string texto = Pergunta, Guid? turno = null, Guid? sessao = null, Guid? mensagem = null, KnowledgeGapKind tipo = KnowledgeGapKind.NoMatch, DateTimeOffset? quando = null) =>
        new(a.Tenant.TenantId, turno ?? Guid.NewGuid(), sessao ?? Guid.NewGuid(), Guid.NewGuid(), mensagem ?? Guid.NewGuid(), Normalizada(texto), tipo, quando ?? DateTimeOffset.UtcNow);

    private static Task RegistrarAsync(CenarioDeAgentes a, KnowledgeGapCandidate candidata) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IKnowledgeGapRecorder>().RecordAsync(candidata, default);
            return 0;
        });

    private static Task<List<KnowledgeGap>> LacunasAsync(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().KnowledgeGaps.AsNoTracking().OrderBy(g => g.FirstSeenAt).ToListAsync());

    private static Task<List<KnowledgeGapOccurrence>> OcorrenciasAsync(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().KnowledgeGapOccurrences.AsNoTracking().OrderBy(o => o.OccurredAt).ToListAsync());

    /// <summary>Um administrador do tenant que também lê conversas (o administrador do tenant tem as duas permissões): a leitura da fonte usa as mesmas regras do histórico.</summary>
    private static RequestContext AdminQueLe(CenarioDeAgentes a) =>
        PostgresFixture.ContextoDeUsuario(a.Tenant, Guid.NewGuid(), new PermissionGrant(Permission.TenantSettingsManage, null), new PermissionGrant(Permission.ConversationRead, null));

    private static Task<T> ComoAsync<T>(CenarioDeAgentes a, RequestContext contexto, Func<KnowledgeGapService, Task<T>> acao) =>
        a.C.ComoAsync(contexto, sp => acao(sp.GetRequiredService<KnowledgeGapService>()));

    private static async Task<string> TudoQueFoiGravadoAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT coalesce(string_agg(row_to_json(g)::text, ' '), '') || ' ' || coalesce((SELECT string_agg(row_to_json(o)::text, ' ') FROM agents.knowledge_gap_occurrences o WHERE o.tenant_id = @t), '') " +
            "FROM agents.knowledge_gaps g WHERE g.tenant_id = @t",
            conexao);
        comando.Parameters.AddWithValue("t", tenant);
        return (string)(await comando.ExecuteScalarAsync())!;
    }

    private static KnowledgeGapRecorder RegistradorComOpcoes(IServiceProvider sp, Action<AgentsOptions> ajustar, bool semChave = false)
    {
        var opcoes = new AgentsOptions { KnowledgeGapHashKey = PostgresFixture.ChaveDeLacunasDeTeste };
        ajustar(opcoes);
        return new KnowledgeGapRecorder(sp.GetRequiredService<AgentsDbContext>(), opcoes, new KnowledgeGapHasherHolder(semChave ? null : KnowledgeGapHasher.FromBase64(opcoes.KnowledgeGapHashKey)));
    }

    // O registro

    [Fact]
    public async Task Registrar_cria_a_lacuna_e_a_ocorrencia_so_com_hash_e_referencias_e_nenhum_texto_de_cliente_e_gravado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacreg");
        var candidata = Candidata(a);

        await RegistrarAsync(a, candidata);

        var lacuna = Assert.Single(await LacunasAsync(a));
        Assert.Equal((KnowledgeGapStatus.Open, KnowledgeGapKind.NoMatch, 1), (lacuna.Status, lacuna.Kind, lacuna.Revision));
        Assert.Equal(KnowledgeGapHasher.HashLength, lacuna.QuestionHash.Length);
        var ocorrencia = Assert.Single(await OcorrenciasAsync(a));
        Assert.Equal((lacuna.Id, candidata.TurnId, candidata.SessionId, candidata.ConversationId, candidata.MessageId), (ocorrencia.GapId, ocorrencia.TurnId, ocorrencia.SessionId, ocorrencia.ConversationId, ocorrencia.MessageId));

        // O banco inteiro dessas duas tabelas, em JSON: nenhuma palavra da pergunta, nem do texto normalizado.
        var gravado = (await TudoQueFoiGravadoAsync(a.Tenant.TenantId.Value)).ToLowerInvariant();
        Assert.DoesNotContain("documento", gravado);
        Assert.DoesNotContain("coleta", gravado);
        Assert.DoesNotContain("preciso", gravado);
    }

    [Fact]
    public async Task O_mesmo_turno_repetido_depois_de_uma_queda_conta_uma_vez()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacidem");
        var candidata = Candidata(a);

        await RegistrarAsync(a, candidata);
        await RegistrarAsync(a, candidata);
        await RegistrarAsync(a, candidata with { At = DateTimeOffset.UtcNow.AddMinutes(1) });

        Assert.Single(await LacunasAsync(a));
        Assert.Single(await OcorrenciasAsync(a)); // a chave é (tenant, turno)
    }

    [Fact]
    public async Task A_mesma_pergunta_em_turnos_diferentes_cai_na_mesma_lacuna_e_o_tipo_e_o_da_ultima_ocorrencia()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacagr");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        await RegistrarAsync(a, Candidata(a, "Preciso levar DOCUMENTO para a coleta?!", quando: t0));
        await RegistrarAsync(a, Candidata(a, "preciso levar documento para a coleta", quando: t0.AddMinutes(1)));
        await RegistrarAsync(a, Candidata(a, "  Preciso  levar documento para a coleta??? ", tipo: KnowledgeGapKind.Ambiguous, quando: t0.AddMinutes(2)));

        var lacuna = Assert.Single(await LacunasAsync(a));
        Assert.Equal(3, (await OcorrenciasAsync(a)).Count);
        Assert.Equal(KnowledgeGapKind.Ambiguous, lacuna.Kind);
        Assert.Equal(t0.AddMinutes(2).ToUnixTimeMilliseconds(), lacuna.LastSeenAt.ToUnixTimeMilliseconds());
        Assert.Equal(t0.ToUnixTimeMilliseconds(), lacuna.FirstSeenAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Perguntas_diferentes_abrem_lacunas_diferentes()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacdif");

        await RegistrarAsync(a, Candidata(a, "exame 2"));
        await RegistrarAsync(a, Candidata(a, "exame 3"));

        var lacunas = await LacunasAsync(a);
        Assert.Equal(2, lacunas.Count);
        Assert.NotEqual(lacunas[0].QuestionHash, lacunas[1].QuestionHash);
    }

    [Fact]
    public async Task A_mesma_pergunta_em_dois_tenants_abre_lacunas_separadas_com_hashes_diferentes_e_cada_um_so_ve_a_sua()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lactena");
        var b = await CenarioDeAgentes.CriarAsync(banco, "lactenb");

        await RegistrarAsync(a, Candidata(a));
        await RegistrarAsync(b, Candidata(b));
        await RegistrarAsync(b, Candidata(b));

        var deA = Assert.Single(await LacunasAsync(a));
        var deB = Assert.Single(await LacunasAsync(b));
        Assert.NotEqual(deA.QuestionHash, deB.QuestionHash); // o segredo é do tenant: nem por comparação de hashes se cruza
        Assert.Single(await OcorrenciasAsync(a));
        Assert.Equal(2, (await OcorrenciasAsync(b)).Count);
        Assert.All(await LacunasAsync(a), g => Assert.Equal(a.Tenant.TenantId, g.TenantId));
    }

    [Fact]
    public async Task Muitos_turnos_da_mesma_pergunta_ao_mesmo_tempo_caem_numa_unica_lacuna_sem_perder_nem_duplicar_ocorrencias()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "laccon");

        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => RegistrarAsync(a, Candidata(a))));

        Assert.Single(await LacunasAsync(a));
        Assert.Equal(24, (await OcorrenciasAsync(a)).Count);
    }

    [Fact]
    public async Task Perguntas_diferentes_ao_mesmo_tempo_cada_uma_abre_a_sua_lacuna_e_o_mesmo_turno_em_paralelo_conta_uma_vez()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "laccon2");
        var turnoRepetido = Candidata(a, "pergunta repetida");

        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => RegistrarAsync(a, Candidata(a, $"pergunta distinta {i}")))
                .Concat(Enumerable.Range(0, 8).Select(_ => RegistrarAsync(a, turnoRepetido))));

        Assert.Equal(9, (await LacunasAsync(a)).Count);
        Assert.Equal(9, (await OcorrenciasAsync(a)).Count); // 8 distintas + o turno repetido contado uma vez
    }

    [Fact]
    public async Task O_teto_por_tenant_impede_lacuna_nova_mas_as_que_existem_continuam_contando()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacteto");

        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var registrador = RegistradorComOpcoes(sp, o => o.KnowledgeGapMaxPerTenant = 2);
            await registrador.RecordAsync(Candidata(a, "primeira"), default);
            await registrador.RecordAsync(Candidata(a, "segunda"), default);
            await registrador.RecordAsync(Candidata(a, "terceira"), default); // teto atingido: nova pergunta não abre lacuna
            await registrador.RecordAsync(Candidata(a, "primeira"), default); // a que já existe continua contando
            return 0;
        });

        Assert.Equal(2, (await LacunasAsync(a)).Count);
        Assert.Equal(3, (await OcorrenciasAsync(a)).Count);
    }

    [Fact]
    public async Task Sem_chave_ou_com_o_registro_desligado_nada_e_gravado_e_nao_ha_erro()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacoff");

        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            await RegistradorComOpcoes(sp, _ => { }, semChave: true).RecordAsync(Candidata(a), default);
            await RegistradorComOpcoes(sp, o => o.KnowledgeGapsEnabled = false).RecordAsync(Candidata(a), default);
            await RegistradorComOpcoes(sp, _ => { }).RecordAsync(Candidata(a) with { NormalizedQuestion = string.Empty }, default);
            return 0;
        });

        Assert.Empty(await LacunasAsync(a));
        Assert.Empty(await OcorrenciasAsync(a));
    }

    [Fact]
    public async Task Candidata_de_outro_tenant_e_bloqueada_antes_de_chegar_ao_banco()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "laccross");
        var b = await CenarioDeAgentes.CriarAsync(banco, "laccrossb");

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => RegistrarAsync(a, Candidata(b)));

        Assert.Empty(await LacunasAsync(a));
        Assert.Empty(await LacunasAsync(b));
    }

    [Fact]
    public async Task O_registro_nao_conflita_com_a_decisao_do_administrador_e_uma_lacuna_descartada_continua_contando()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacdec");
        await RegistrarAsync(a, Candidata(a));
        var lacuna = Assert.Single(await LacunasAsync(a));
        await ComoAsync(a, a.Admin, s => s.DismissAsync(lacuna.Id));

        await RegistrarAsync(a, Candidata(a)); // a mesma pergunta de novo

        var depois = Assert.Single(await LacunasAsync(a));
        Assert.Equal(KnowledgeGapStatus.Dismissed, depois.Status); // a decisão é do hash: a lacuna não volta a abrir sozinha
        Assert.Equal(2, (await OcorrenciasAsync(a)).Count);
    }

    // Revisão: isolamento, lista e permissão

    [Fact]
    public async Task Um_administrador_nao_lista_nem_le_nem_decide_a_lacuna_de_outro_tenant()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacisoa");
        var b = await CenarioDeAgentes.CriarAsync(banco, "lacisob");
        await RegistrarAsync(a, Candidata(a));
        await RegistrarAsync(b, Candidata(b));
        var deA = Assert.Single(await LacunasAsync(a));
        var deB = Assert.Single(await LacunasAsync(b));

        Assert.Equal(deA.Id, Assert.Single((await ComoAsync(a, a.Admin, s => s.ListAsync(null, 50, 0))).Items).Id);
        Assert.Equal(deB.Id, Assert.Single((await ComoAsync(b, b.Admin, s => s.ListAsync(null, 50, 0))).Items).Id);

        // O administrador de B usa o identificador da lacuna de A: é inexistente (404), em toda operação, e nada muda.
        Assert.Null(await ComoAsync(b, b.Admin, s => s.GetAsync(deA.Id)));
        Assert.Null(await ComoAsync(b, b.Admin, s => s.DismissAsync(deA.Id)));
        Assert.Null(await ComoAsync(b, b.Admin, s => s.PromoteAsync(deA.Id, new PromoteKnowledgeGapRequest("P?", null, "R"))));
        Assert.Equal(KnowledgeGapStatus.Open, Assert.Single(await LacunasAsync(a)).Status);
        Assert.Null(await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetDraftAsync()));
    }

    [Fact]
    public async Task Sem_a_permissao_de_configurar_o_tenant_nenhuma_operacao_passa_nem_com_permissao_de_leitura_de_conversas()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacperm");
        await RegistrarAsync(a, Candidata(a));
        var lacuna = Assert.Single(await LacunasAsync(a));
        var leitor = PostgresFixture.ContextoDeUsuario(a.Tenant, Guid.NewGuid(), new PermissionGrant(Permission.ConversationRead, null), new PermissionGrant(Permission.ConversationManage, null));

        await Assert.ThrowsAsync<AccessDeniedException>(() => ComoAsync(a, leitor, s => s.ListAsync(null, 10, 0)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ComoAsync(a, leitor, s => s.GetAsync(lacuna.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ComoAsync(a, leitor, s => s.DismissAsync(lacuna.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ComoAsync(a, leitor, s => s.PromoteAsync(lacuna.Id, new PromoteKnowledgeGapRequest("P?", null, "R"))));
        Assert.Equal(KnowledgeGapStatus.Open, Assert.Single(await LacunasAsync(a)).Status);
    }

    [Fact]
    public async Task A_lista_vem_das_mais_frequentes_com_a_contagem_exata_e_filtra_por_situacao()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "laclista");
        await RegistrarAsync(a, Candidata(a, "descartada"));
        var descartada = Assert.Single(await LacunasAsync(a)).Id;
        await ComoAsync(a, a.Admin, s => s.DismissAsync(descartada));
        await RegistrarAsync(a, Candidata(a, "rara"));
        for (var i = 0; i < 4; i++)
        {
            await RegistrarAsync(a, Candidata(a, "comum"));
        }

        var abertas = await ComoAsync(a, a.Admin, s => s.ListAsync(KnowledgeGapStatus.Open, 50, 0));
        var todas = await ComoAsync(a, a.Admin, s => s.ListAsync(null, 50, 0));

        Assert.Equal(new[] { 4, 1 }, abertas.Items.Select(i => i.Occurrences)); // das mais frequentes para as menos, a descartada fora
        Assert.Equal((2, 3), (abertas.Total, todas.Total));
        var primeira = await ComoAsync(a, a.Admin, s => s.ListAsync(KnowledgeGapStatus.Open, 1, 0));
        Assert.Equal((1, 2, abertas.Items[0].Id), (primeira.Items.Count, primeira.Total, primeira.Items[0].Id));
        Assert.Equal(descartada, Assert.Single((await ComoAsync(a, a.Admin, s => s.ListAsync(KnowledgeGapStatus.Dismissed, 50, 0))).Items).Id);
        Assert.Empty((await ComoAsync(a, a.Admin, s => s.ListAsync(KnowledgeGapStatus.Promoted, 50, 0))).Items);
    }

    // Revisão: a pergunta e a resposta humana, com a autorização do Conversations

    [Fact]
    public async Task O_detalhe_le_a_pergunta_do_cliente_e_a_resposta_do_atendente_pelo_Conversations_e_a_resposta_da_IA_nao_e_sugestao()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacfonte");
        var entrada = await a.EntrarAsync("mauro", Pergunta);
        var atendente = Guid.NewGuid();

        // A IA responde primeiro (não é sugestão); depois o atendente assume e responde.
        var sessao = (await a.C.SessaoAsync(entrada.SessionId))!;
        var turno = Guid.NewGuid();
        var inicio = await a.C.IniciarTurnoAsync(entrada.SessionId, turno, 1);
        Assert.Equal(RegisterReplyOutcome.Accepted, (await a.C.ResponderAsync(entrada.SessionId, turno, inicio, entrada.ConversationId, "Resposta da IA que não vale como sugestão.")).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await a.C.TransicionarAsync(entrada.SessionId, HandlingMode.HumanAssisted, atendente, (await a.C.SessaoAsync(entrada.SessionId))!.StateVersion)).Outcome);
        var s = (await a.C.SessaoAsync(entrada.SessionId))!;
        var resposta = await a.C.ComoAsync(
            a.C.ContextoDeSistema,
            sp => sp.GetRequiredService<IHandlingCommands>().RegisterHumanReplyAsync(new RegisterHumanReplyCommand(
                entrada.SessionId, Guid.NewGuid(), atendente, new ReplyPrecondition(s.HandlingPeriod, s.PlacementEpoch, ResponderMatch.Exact, atendente), s.StateVersion, 0,
                new ReplyContent("Sim, leve um documento oficial com foto.", null, null))));
        Assert.Equal(RegisterHumanReplyOutcome.Applied, resposta.Outcome);
        _ = sessao;

        var mensagemDoCliente = (await a.C.HistoricoAsync(entrada.SessionId))!.Items.Single(m => m.Direction == MessageDirection.Inbound);
        await RegistrarAsync(a, Candidata(a, sessao: entrada.SessionId, mensagem: mensagemDoCliente.Id));
        var lacuna = Assert.Single(await LacunasAsync(a));

        var detalhe = await ComoAsync(a, AdminQueLe(a), svc => svc.GetAsync(lacuna.Id));

        Assert.Equal(KnowledgeGapSourceState.Available, detalhe!.Source.State);
        Assert.Equal(Pergunta, detalhe.Source.Question);
        Assert.Equal("Sim, leve um documento oficial com foto.", detalhe.Source.HumanReply!.Text);
        Assert.Equal(0, detalhe.Source.HumanReply.CustomerMessagesBetween);
    }

    [Fact]
    public async Task Sem_permissao_de_leitura_de_conversas_a_fonte_fica_indisponivel_mas_a_lacuna_continua_revisavel()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacsemleit");
        var entrada = await a.EntrarAsync("mauro", Pergunta);
        var mensagem = (await a.C.HistoricoAsync(entrada.SessionId))!.Items.Single();
        await RegistrarAsync(a, Candidata(a, sessao: entrada.SessionId, mensagem: mensagem.Id));
        var lacuna = Assert.Single(await LacunasAsync(a));

        // a.Admin só tem TenantSettingsManage: não lê conversas.
        var detalhe = await ComoAsync(a, a.Admin, svc => svc.GetAsync(lacuna.Id));

        Assert.Equal(KnowledgeGapSourceState.Unavailable, detalhe!.Source.State);
        Assert.Null(detalhe.Source.Question);
        Assert.Equal(lacuna.Id, detalhe.Gap.Id);
    }

    [Fact]
    public async Task Administrador_de_outro_tenant_nao_le_a_mensagem_pela_referencia_e_a_referencia_a_mensagem_removida_nao_derruba_a_revisao()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacrem");
        var entrada = await a.EntrarAsync("mauro", Pergunta);

        // A referência aponta para uma mensagem que não existe mais (removida) numa sessão que existe, e para uma sessão que nunca existiu.
        await RegistrarAsync(a, Candidata(a, "mensagem removida", sessao: entrada.SessionId, mensagem: Guid.NewGuid()));
        await RegistrarAsync(a, Candidata(a, "sessao removida", sessao: Guid.NewGuid(), mensagem: Guid.NewGuid()));

        foreach (var lacuna in await LacunasAsync(a))
        {
            var detalhe = await ComoAsync(a, AdminQueLe(a), svc => svc.GetAsync(lacuna.Id));
            Assert.Equal(KnowledgeGapSourceState.Unavailable, detalhe!.Source.State);
            Assert.Single(detalhe.Occurrences);
        }

        // E a lacuna segue revisável: o administrador escreve a pergunta.
        var primeira = (await LacunasAsync(a)).First();
        var promovida = await ComoAsync(a, a.Admin, svc => svc.PromoteAsync(primeira.Id, new PromoteKnowledgeGapRequest("Pergunta escrita pelo administrador?", null, "Resposta do administrador.")));
        Assert.Equal(KnowledgeGapStatus.Promoted, promovida!.Gap.Status);
    }

    [Fact]
    public async Task O_historico_aceita_o_corte_de_baixo_so_o_que_veio_depois_de_uma_mensagem_e_rejeita_valor_negativo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacseq");
        var entrada = await a.EntrarAsync("mauro", "Primeira");
        var segunda = await a.EntrarAsync("mauro", "Segunda");
        var terceira = await a.EntrarAsync("mauro", "Terceira");
        Assert.Equal(entrada.SessionId, terceira.SessionId);
        var todas = (await a.C.HistoricoAsync(entrada.SessionId, ordem: HistoryOrder.OldestFirst))!.Items;
        var primeiraMsg = todas.Single(m => m.Text == "Primeira");

        var depois = await a.C.ComoAsync(
            AdminQueLe(a),
            sp => sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(entrada.SessionId, 10, null, HistoryOrder.OldestFirst, AfterMessageSeq: primeiraMsg.MessageSeq)));
        var depoisDaUltima = await a.C.ComoAsync(
            AdminQueLe(a),
            sp => sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(entrada.SessionId, 10, null, HistoryOrder.OldestFirst, AfterMessageSeq: todas.Max(m => m.MessageSeq))));

        Assert.Equal(new[] { "Segunda", "Terceira" }, depois!.Items.Select(m => m.Text));
        Assert.Empty(depoisDaUltima!.Items);
        _ = segunda;
        await Assert.ThrowsAsync<IaBimeta.Conversations.Domain.ConversationsValidationException>(() => a.C.ComoAsync(
            AdminQueLe(a),
            sp => sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(entrada.SessionId, 10, null, HistoryOrder.OldestFirst, AfterMessageSeq: -1))));

        // Quem não lê conversas na organização da sessão não recebe nada (404 lógico), com o mesmo filtro.
        var semLeitura = await a.C.ComoAsync(
            a.Admin,
            sp => sp.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(entrada.SessionId, 10, null, HistoryOrder.OldestFirst, AfterMessageSeq: 0)));
        Assert.Null(semLeitura);
    }

    // Enviar ao rascunho: decisão, concorrência e nada publicado

    [Fact]
    public async Task Enviar_ao_rascunho_grava_a_entrada_no_rascunho_com_a_resposta_direta_desligada_e_nao_publica_nada()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacpromo");
        await RegistrarAsync(a, Candidata(a));
        var lacuna = Assert.Single(await LacunasAsync(a));
        var publicadaAntes = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetPublishedAsync());

        var resultado = await ComoAsync(a, a.Admin, s => s.PromoteAsync(lacuna.Id, new PromoteKnowledgeGapRequest(Pergunta, new[] { "preciso de documento para coletar" }, "Sim, leve um documento com foto.")));

        Assert.NotNull(resultado);
        Assert.Equal(KnowledgeGapStatus.Promoted, resultado.Gap.Status);
        Assert.Equal(publicadaAntes!.Number + 1, resultado.DraftVersion);
        var (rascunho, publicadaDepois) = await a.C.ComoAsync(a.Admin, async sp =>
        {
            var servico = sp.GetRequiredService<AgentDefinitionService>();
            return (await servico.GetDraftAsync(), await servico.GetPublishedAsync());
        });
        Assert.Equal(AgentDefinitionStatus.Draft, rascunho!.Status);
        var nova = rascunho.Content.Faq[^1];
        Assert.Equal((Pergunta, false), (nova.Question, nova.AutoReply)); // AutoReply desligado por padrão
        Assert.Equal(new[] { "preciso de documento para coletar" }, nova.Aliases);
        // A versão publicada é a mesma de antes: mesmo número, mesmo conteúdo, nada novo.
        Assert.Equal((publicadaAntes.Number, AgentDefinitionStatus.Published), (publicadaDepois!.Number, publicadaDepois.Status));
        Assert.Equal(publicadaAntes.Content.Faq.Count, publicadaDepois.Content.Faq.Count);
        var gravada = Assert.Single(await LacunasAsync(a));
        Assert.Equal((KnowledgeGapStatus.Promoted, resultado.DraftVersion), (gravada.Status, gravada.PromotedVersion));
    }

    [Fact]
    public async Task Dois_administradores_enviando_a_mesma_lacuna_ao_mesmo_tempo_so_um_vence_e_o_rascunho_ganha_uma_unica_entrada()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacrace1");
        await RegistrarAsync(a, Candidata(a));
        var lacuna = Assert.Single(await LacunasAsync(a));
        var antes = (await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetPublishedAsync()))!.Content.Faq.Count;

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            try
            {
                var r = await ComoAsync(a, a.Admin, s => s.PromoteAsync(lacuna.Id, new PromoteKnowledgeGapRequest($"Pergunta do administrador {i}?", null, "Resposta.")));
                return (Ok: r is not null, Erro: (string?)null);
            }
            catch (KnowledgeGapConflictException ex)
            {
                return (Ok: false, Erro: ex.Code);
            }
            catch (AgentDefinitionConflictException)
            {
                return (Ok: false, Erro: "definition-changed");
            }
        }));

        Assert.Equal(1, resultados.Count(r => r.Ok)); // exatamente um
        Assert.All(resultados.Where(r => !r.Ok), r => Assert.Contains(r.Erro, new[] { "gap-changed", "gap-not-open", "definition-changed" }));
        var rascunho = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetDraftAsync());
        Assert.Equal(antes + 1, rascunho!.Content.Faq.Count); // uma só entrada nova: nenhuma duplicata do mesmo pedido
        Assert.Equal(KnowledgeGapStatus.Promoted, Assert.Single(await LacunasAsync(a)).Status);
    }

    [Fact]
    public async Task Lacunas_diferentes_enviadas_ao_mesmo_tempo_nunca_perdem_entrada_em_silencio()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacrace2");
        for (var i = 0; i < 4; i++)
        {
            await RegistrarAsync(a, Candidata(a, $"pergunta {i}"));
        }

        var lacunas = await LacunasAsync(a);
        var antes = (await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetPublishedAsync()))!.Content.Faq.Count;

        var vencedoras = await Task.WhenAll(lacunas.Select(async (g, i) =>
        {
            try
            {
                var r = await ComoAsync(a, a.Admin, s => s.PromoteAsync(g.Id, new PromoteKnowledgeGapRequest($"Entrada {i}?", null, $"Resposta {i}.")));
                return r is not null;
            }
            catch (Exception ex) when (ex is AgentDefinitionConflictException or KnowledgeGapConflictException)
            {
                return false; // perdeu a corrida do rascunho: conflito explícito, nada gravado por esta
            }
        }));

        var rascunho = await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetDraftAsync());
        var promovidas = (await LacunasAsync(a)).Count(g => g.Status == KnowledgeGapStatus.Promoted);
        Assert.Contains(true, vencedoras);
        Assert.Equal(vencedoras.Count(v => v), promovidas); // cada sucesso marcou uma lacuna, e cada lacuna marcada tem a entrada no rascunho
        Assert.Equal(antes + promovidas, rascunho!.Content.Faq.Count);
    }

    [Fact]
    public async Task Pergunta_ja_existente_no_FAQ_e_conflito_e_a_lacuna_descartada_nao_vai_ao_rascunho()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacconf");
        await RegistrarAsync(a, Candidata(a, "x"));
        await RegistrarAsync(a, Candidata(a, "y"));
        var lacunas = await LacunasAsync(a);

        var conflito = await Assert.ThrowsAsync<KnowledgeGapConflictException>(
            () => ComoAsync(a, a.Admin, s => s.PromoteAsync(lacunas[0].Id, new PromoteKnowledgeGapRequest("preciso de jejum para o hemograma", null, "Resposta."))));
        await ComoAsync(a, a.Admin, s => s.DismissAsync(lacunas[1].Id));
        var naoAberta = await Assert.ThrowsAsync<KnowledgeGapConflictException>(
            () => ComoAsync(a, a.Admin, s => s.PromoteAsync(lacunas[1].Id, new PromoteKnowledgeGapRequest("Outra?", null, "Resposta."))));

        Assert.Equal("faq-conflict", conflito.Code); // "Preciso de jejum para o hemograma?" é a entrada do cenário
        Assert.Equal("gap-not-open", naoAberta.Code);
        Assert.Null(await a.C.ComoAsync(a.Admin, sp => sp.GetRequiredService<AgentDefinitionService>().GetDraftAsync()));
        Assert.All(await LacunasAsync(a), g => Assert.NotEqual(KnowledgeGapStatus.Promoted, g.Status));
    }

    [Fact]
    public async Task Descartar_e_enviar_ao_rascunho_sao_auditados_sem_conteudo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "lacaud");
        await RegistrarAsync(a, Candidata(a, "a"));
        await RegistrarAsync(a, Candidata(a, "b"));
        var lacunas = await LacunasAsync(a);

        await ComoAsync(a, a.Admin, s => s.DismissAsync(lacunas[0].Id));
        await ComoAsync(a, a.Admin, s => s.PromoteAsync(lacunas[1].Id, new PromoteKnowledgeGapRequest("Pergunta segredo-do-cliente?", null, "Resposta segredo-da-empresa.")));

        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT string_agg(row_to_json(e)::text, ' ') FROM audit.audit_events e WHERE e.tenant_id = @t AND e.operation LIKE 'agents.%'", conexao);
        comando.Parameters.AddWithValue("t", a.Tenant.TenantId.Value);
        var auditoria = (await comando.ExecuteScalarAsync()) as string ?? string.Empty;

        Assert.Contains("agents.knowledge-gap.dismissed", auditoria);
        Assert.Contains("agents.knowledge-gap.promoted", auditoria);
        Assert.Contains("agents.definition.draft-saved", auditoria);
        Assert.DoesNotContain("segredo-do-cliente", auditoria);
        Assert.DoesNotContain("segredo-da-empresa", auditoria);
    }
}
