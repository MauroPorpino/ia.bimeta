using IaBimeta.Agents.Domain;
using IaBimeta.Api.Operations;
using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Infrastructure;
using IaBimeta.Capabilities.Infrastructure.Outbound;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.SharedKernel;
using IaBimeta.Worker;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using IaBimeta.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Workflows;

/// <summary>
/// O runtime do chatbot visual PONTA A PONTA (ADR 0037 e 0038): PostgreSQL, Conversations, ContactCenter, Agents, AiGateway e Motor de Integração REAIS; só o provedor de IA e o ERP (transporte de saída) são
/// simulados. Prova: um único dono por sessão (nunca Workflows e Agents juntos), uma mensagem de Bot por passo, nenhuma duplicidade em repetição, recuperação de queda, transferência, tomada por atendente,
/// entrega ao agente, nós de integração pelo Capabilities, variáveis cifradas, isolamento entre tenants, concorrência e limites.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FluxoPontaAPontaTests(PostgresFixture banco)
{
    // ------------------------------------------------------------ desenho dos fluxos

    private static FlowNode Msg(string id, string text) => new(id, NodeType.Message, new NodeConfig(Text: text));

    private static FlowEdge E(string de, string porta, string para) => new(de, porta, para);

    private static readonly FlowChoice[] Opcoes =
    [
        new("preco", "Preço"), new("horario", "Horário"), new("humano", "Falar com atendente"), new("ia", "Outra dúvida"),
    ];

    /// <summary>Olá → menu (escolha) → horário | atendente | dúvida (conhecimento e, sem resposta, o agente de IA).</summary>
    private static FlowGraph Menu() => new(
        1,
        "ola",
        [
            Msg("ola", "Olá! Sou o assistente."),
            new("menu", NodeType.Question, new NodeConfig(Prompt: "Escolha uma opção:", AnswerType: AnswerType.Choice, Variable: "opcao", Choices: Opcoes, MaxAttempts: 2, InvalidText: "Não entendi. Escolha uma opção:")),
            new("rota", NodeType.Condition, new NodeConfig(Branches:
            [
                new ConditionBranch("horario", [new FlowCondition("opcao", ConditionOperator.Equals, "horario")]),
                new ConditionBranch("humano", [new FlowCondition("opcao", ConditionOperator.Equals, "humano")]),
            ])),
            Msg("horario", "Atendemos de segunda a sexta."),
            new("fim", NodeType.End, new NodeConfig(Text: "Até logo!")),
            new("atendente", NodeType.Handoff, new NodeConfig(Text: "Vou chamar um atendente.", ReasonCode: "pedido_do_cliente")),
            new("faq", NodeType.Knowledge, new NodeConfig()),
            Msg("resposta", "{{knowledge.answer}}"),
            new("ia", NodeType.Ai),
        ],
        [
            E("ola", "next", "menu"), E("menu", "answered", "rota"), E("menu", "invalid", "ia"),
            E("rota", "horario", "horario"), E("rota", "humano", "atendente"), E("rota", "else", "faq"),
            E("horario", "next", "fim"), E("faq", "found", "resposta"), E("faq", "not_found", "ia"), E("resposta", "next", "fim"),
        ],
        [new FlowVariable("opcao", VariableClass.Public)]);

    /// <summary>Pergunta aberta → conhecimento; sem resposta aprovada, entrega ao agente.</summary>
    private static FlowGraph Duvida() => new(
        1,
        "pergunta",
        [
            new("pergunta", NodeType.Question, new NodeConfig(Prompt: "Qual é a sua dúvida?", Variable: "duvida", MaxAttempts: 2)),
            new("faq", NodeType.Knowledge, new NodeConfig(QueryVariable: "duvida")),
            Msg("resposta", "{{knowledge.answer}}"),
            new("fim", NodeType.End),
            new("ia", NodeType.Ai),
        ],
        [E("pergunta", "answered", "faq"), E("pergunta", "invalid", "ia"), E("faq", "found", "resposta"), E("faq", "not_found", "ia"), E("resposta", "next", "fim")],
        [new FlowVariable("duvida", VariableClass.Personal)]);

    /// <summary>Código do exame → consulta pública de preço (Capabilities) → mostra o resultado ou avisa a falha.</summary>
    private static FlowGraph Preco() => new(
        1,
        "codigo",
        [
            new("codigo", NodeType.Question, new NodeConfig(Prompt: "Qual o exame?", Variable: "exame", MaxAttempts: 2)),
            new("consulta", NodeType.Integration, new NodeConfig(OperationKey: "preco.consultar", ResultPrefix: "preco", Parameters: new Dictionary<string, ParamBinding> { ["exame"] = new(Variable: "exame") })),
            Msg("mostra", "O exame {{exame}} custa R$ {{preco.preco}}, pronto em {{preco.prazo}}."),
            Msg("falha", "Não consegui consultar o preço agora."),
            new("fim", NodeType.End),
        ],
        [E("codigo", "answered", "consulta"), E("codigo", "invalid", "falha"), E("consulta", "success", "mostra"), E("consulta", "failure", "falha"), E("mostra", "next", "fim"), E("falha", "next", "fim")],
        [new FlowVariable("exame", VariableClass.Personal)]);

    // ------------------------------------------------------------ cenário

    /// <summary>
    /// O banco de testes é compartilhado e guarda instâncias de execuções anteriores (de outros tenants) que ficaram devidas: o executor do Worker olha um lote de tenants por ciclo e as deixaria à frente do tenant
    /// deste teste. Antes de cada cenário elas são "estacionadas" (sem próximo olhar); as classes de teste da coleção rodam em série, então nada concorre com isto.
    /// </summary>
    private async Task EstacionarSobrasAsync()
    {
        await using var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new Npgsql.NpgsqlCommand("UPDATE workflows.flow_instances SET next_run_at = NULL WHERE next_run_at IS NOT NULL", conexao);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<CenarioDeAgentes> CenarioAsync(string prefixo, FlowGraph? grafo = null, string chave = "atendimento", OnFailurePolicy politica = OnFailurePolicy.Agent, Guid? canal = null)
    {
        await EstacionarSobrasAsync();
        banco.Erp.Roteiro = ErpSimulado.RoteiroPadrao;
        var a = await CenarioDeAgentes.CriarAsync(banco, prefixo, fluxos: true);
        await a.FilaDeAtendimentoAsync();
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Resposta do agente de IA."));
        if (grafo is not null)
        {
            await PublicarAsync(a, chave, grafo);
            await AtivarAsync(a, chave, politica, canal);
        }

        return a;
    }

    private static RequestContext Admin(CenarioDeAgentes a) => PostgresFixture.ContextoDeUsuario(
        a.Tenant,
        Guid.NewGuid(),
        new PermissionGrant(Permission.WorkflowRead, a.C.OrganizacaoId),
        new PermissionGrant(Permission.WorkflowManage, a.C.OrganizacaoId),
        new PermissionGrant(Permission.WorkflowPublish, a.C.OrganizacaoId),
        new PermissionGrant(Permission.IntegrationExecute, a.C.OrganizacaoId));

    private static Task PublicarAsync(CenarioDeAgentes a, string chave, FlowGraph grafo) => a.C.ComoAsync(Admin(a), async sp =>
    {
        var servico = sp.GetRequiredService<WorkflowDefinitionService>();
        var salvo = await servico.SaveDraftAsync(new SaveWorkflowRequest(a.C.OrganizacaoId, chave, "Fluxo de teste", null, FlowGraphSerializer.Serialize(grafo), null));
        Assert.Empty(salvo.Problems);
        await servico.PublishAsync(salvo.Workflow.Id);
        return 0;
    });

    private static Task AtivarAsync(CenarioDeAgentes a, string chave, OnFailurePolicy politica, Guid? canal = null) => a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
    {
        var db = sp.GetRequiredService<WorkflowsDbContext>();
        var agora = DateTimeOffset.UtcNow;
        var ativacao = FlowActivation.Create(a.Tenant.TenantId, a.C.OrganizacaoId, null, canal, chave, 0, 100, politica, null, agora);
        ativacao.SetEnabled(true, agora);
        db.Activations.Add(ativacao);
        await db.SaveChangesAsync();
        return 0;
    });

    private static FlowExecutor NovoExecutor(CenarioDeAgentes a) => ActivatorUtilities.CreateInstance<FlowExecutor>(a.Servicos);

    /// <summary>O ciclo do Worker: o relay entrega os eventos (roteador, consumidores) e o executor roda as instâncias devidas.</summary>
    private static async Task ProcessarAsync(CenarioDeAgentes a, int ciclos = 4)
    {
        var executor = NovoExecutor(a);
        for (var i = 0; i < ciclos; i++)
        {
            await a.RelayAsync();
            await executor.RunOnceAsync(default);
        }
    }

    private static async Task<List<MessageView>> BotAsync(CenarioDeAgentes a, Guid sessao) =>
        (await a.C.HistoricoAsync(sessao))!.Items.Where(m => m.Author == MessageAuthor.Bot).OrderBy(m => m.MessageSeq).ToList();

    private static Task<List<FlowInstance>> InstanciasAsync(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<WorkflowsDbContext>().Instances.AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync());

    private static Task<RoutingDecision?> DecisaoAsync(CenarioDeAgentes a, Guid sessao) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<WorkflowsDbContext>().Decisions.AsNoTracking().FirstOrDefaultAsync(d => d.SessionId == sessao));

    private static Task<List<FlowEffectRecord>> EfeitosAsync(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<WorkflowsDbContext>().Effects.AsNoTracking().OrderBy(e => e.CreatedAt).ThenBy(e => e.Index).ToListAsync());

    /// <summary>Um resumo (só identificadores, estados e códigos) do que o runtime gravou: a mensagem de uma asserção que falha diz onde o fluxo parou.</summary>
    private static async Task<string> DiagnosticoAsync(CenarioDeAgentes a)
    {
        var instancias = await InstanciasAsync(a);
        var efeitos = await EfeitosAsync(a);
        return string.Join(" | ", instancias.Select(i => $"instancia {i.Status} no={i.NodeId} falha={i.FailureCode} cancel={i.CancelReason} next={i.NextRunAt:HH:mm:ss} visto={i.SeenMessageSeq}"))
            + " || " + string.Join(" | ", efeitos.Select(e => $"{e.Key}:{e.Kind}:{e.State}:{e.ResultCode}"));
    }

    // ------------------------------------------------------------ caminho feliz

    [Fact]
    public async Task O_fluxo_responde_uma_mensagem_de_Bot_por_passo_sem_turno_de_IA_e_repetir_o_ciclo_nao_duplica_nada()
    {
        var a = await CenarioAsync("fluxo-feliz", Menu());
        var r = await a.EntrarAsync("mauro", "oi");
        await ProcessarAsync(a);

        var primeira = Assert.Single(await BotAsync(a, r.SessionId));
        Assert.Equal("Olá! Sou o assistente.\n\nEscolha uma opção:\n1) Preço\n2) Horário\n3) Falar com atendente\n4) Outra dúvida", primeira.Text);
        Assert.Equal(ReplyKind.Informative, primeira.ReplyKind);

        await a.EntrarAsync("mauro", "2");
        await ProcessarAsync(a);
        var bot = await BotAsync(a, r.SessionId);
        Assert.True(bot.Count == 2, await DiagnosticoAsync(a));
        Assert.Equal("Atendemos de segunda a sexta.\n\nAté logo!", bot[1].Text);

        // Repetir o relay e o executor não envia nada de novo (idempotência de efeito e de entrada).
        await ProcessarAsync(a, 3);
        Assert.Equal(2, (await BotAsync(a, r.SessionId)).Count);

        // Nenhum turno de IA e nenhuma chamada ao modelo: Workflows e Agents nunca respondem juntos.
        Assert.Empty(await a.TurnosAsync(r.SessionId));
        Assert.Empty(a.PedidosAoModelo);

        var instancia = Assert.Single(await InstanciasAsync(a));
        Assert.Equal(FlowStatus.Completed, instancia.Status);
        Assert.Equal(AutomationOwner.Agent, (await DecisaoAsync(a, r.SessionId))!.Owner);
        Assert.All(await EfeitosAsync(a), e => Assert.NotEqual(EffectState.Pending, e.State));
    }

    [Fact]
    public async Task Resposta_invalida_repete_a_pergunta_e_a_segunda_invalida_segue_a_porta_de_invalida()
    {
        var a = await CenarioAsync("fluxo-invalida", Menu());
        var r = await a.EntrarAsync("ana", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("ana", "talvez");
        await ProcessarAsync(a);
        Assert.Equal("Não entendi. Escolha uma opção:\n1) Preço\n2) Horário\n3) Falar com atendente\n4) Outra dúvida", (await BotAsync(a, r.SessionId))[1].Text);

        await a.EntrarAsync("ana", "sei lá");
        await ProcessarAsync(a);
        // A porta "invalid" leva ao nó de IA: o agente assume e responde (um só turno).
        a.Avancar(TimeSpan.FromSeconds(4));
        await a.RelayAsync();
        await a.ExecutarAsync();
        Assert.Single(await a.RespostasDaIAAsync(r.SessionId));
        Assert.Equal(FlowStatus.Delegated, Assert.Single(await InstanciasAsync(a)).Status);
    }

    // ------------------------------------------------------------ um dono por sessão

    [Fact]
    public async Task Sessao_que_nenhuma_ativacao_casa_fica_com_o_agente_e_nada_do_fluxo_e_gravado()
    {
        var a = await CenarioAsync("fluxo-sem-ativacao", Menu(), canal: Guid.NewGuid());
        var r = await a.EntrarAsync("bia", "oi");
        await ProcessarAsync(a);
        a.Avancar(TimeSpan.FromSeconds(4));
        await a.RelayAsync();
        await a.ExecutarAsync();

        Assert.Single(await a.RespostasDaIAAsync(r.SessionId));
        Assert.Empty(await BotAsync(a, r.SessionId));
        Assert.Empty(await InstanciasAsync(a));
        Assert.Null(await DecisaoAsync(a, r.SessionId));
    }

    [Fact]
    public async Task Com_o_chatbot_desligado_o_roteador_e_o_dubl_do_agente_e_nenhuma_tabela_do_fluxo_e_tocada()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "fluxo-desligado"); // a composição dos Agents (sem fluxos)
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Resposta normal."));
        var r = await a.EntrarAsync("caio", "oi");
        a.Avancar(TimeSpan.FromSeconds(4));
        await a.RelayAsync();
        await a.ExecutarAsync();

        Assert.Single(await a.RespostasDaIAAsync(r.SessionId));
        var router = await a.C.ComoAsync(a.C.ContextoDeSistema, async sp => await sp.GetRequiredService<IAutomationRouter>().ResolveOnInboundAsync(r.SessionId, 1));
        Assert.Equal(AutomationOwner.Agent, router);
        Assert.Empty(await InstanciasAsync(a));
        Assert.Null(await DecisaoAsync(a, r.SessionId));
        Assert.Empty(await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<WorkflowsDbContext>().Set<IaBimeta.Messaging.InboxMessage>().AsNoTracking().Where(i => i.TenantId == a.Tenant.TenantId.Value).ToListAsync()));
    }

    [Fact]
    public async Task A_ativacao_de_um_tenant_nunca_roteia_a_sessao_de_outro()
    {
        var a = await CenarioAsync("fluxo-iso-a", Menu());
        var b = await CenarioAsync("fluxo-iso-b"); // sem ativação
        var ra = await a.EntrarAsync("mauro", "oi");
        var rb = await b.EntrarAsync("mauro", "oi");
        await ProcessarAsync(a);
        b.Avancar(TimeSpan.FromSeconds(4));
        await b.RelayAsync();
        await b.ExecutarAsync();

        Assert.Single(await BotAsync(a, ra.SessionId));
        Assert.Empty(await BotAsync(b, rb.SessionId));
        Assert.Empty(await InstanciasAsync(b));
        Assert.Single(await b.RespostasDaIAAsync(rb.SessionId));
        Assert.Empty(await a.RespostasDaIAAsync(ra.SessionId));
    }

    // ------------------------------------------------------------ transferências, retomadas e mudança de modo

    [Fact]
    public async Task Transferir_para_o_atendente_avisa_o_cliente_uma_vez_cria_o_item_da_fila_e_encerra_o_fluxo()
    {
        var a = await CenarioAsync("fluxo-transferir", Menu());
        var r = await a.EntrarAsync("duda", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("duda", "3");
        await ProcessarAsync(a);

        var bot = await BotAsync(a, r.SessionId);
        Assert.True(bot.Count == 2, await DiagnosticoAsync(a));
        Assert.Equal("Vou chamar um atendente.", bot[1].Text);
        var sessao = (await a.C.SessaoAsync(r.SessionId))!;
        Assert.Equal(HandlingMode.HumanAssisted, sessao.HandlingMode);
        Assert.Single(await a.ItensDaFilaAsync());
        Assert.Equal(FlowStatus.HandedOff, Assert.Single(await InstanciasAsync(a)).Status);

        // Repetir o ciclo não cria outro item nem outro aviso.
        await ProcessarAsync(a, 3);
        Assert.Single(await a.ItensDaFilaAsync());
        Assert.Equal(2, (await BotAsync(a, r.SessionId)).Count);
        Assert.Empty(await a.TurnosAsync(r.SessionId));
    }

    [Fact]
    public async Task Se_nenhuma_fila_aceita_a_transferencia_o_cliente_nao_recebe_o_aviso_e_a_politica_de_falha_devolve_ao_agente()
    {
        await EstacionarSobrasAsync();
        var a = await CenarioDeAgentes.CriarAsync(banco, "fluxo-sem-fila", fluxos: true); // SEM fila de atendimento
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Resposta do agente."));
        await PublicarAsync(a, "atendimento", Menu());
        await AtivarAsync(a, "atendimento", OnFailurePolicy.Agent);
        var r = await a.EntrarAsync("eva", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("eva", "3");
        await ProcessarAsync(a);

        Assert.Single(await BotAsync(a, r.SessionId)); // só o menu: o aviso "vou chamar um atendente" NÃO saiu
        Assert.Equal(FlowStatus.Failed, Assert.Single(await InstanciasAsync(a)).Status);
        Assert.Equal(AutomationOwner.Agent, (await DecisaoAsync(a, r.SessionId))!.Owner);
        Assert.Equal(HandlingMode.AiAutonomous, (await a.C.SessaoAsync(r.SessionId))!.HandlingMode);
    }

    [Fact]
    public async Task Quando_o_atendente_assume_no_meio_do_fluxo_nada_mais_e_enviado_e_a_instancia_e_cancelada()
    {
        var a = await CenarioAsync("fluxo-assumir", Menu());
        var r = await a.EntrarAsync("fabio", "oi");
        await ProcessarAsync(a);
        var sessao = (await a.C.SessaoAsync(r.SessionId))!;
        await a.C.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), sessao.StateVersion);

        await a.EntrarAsync("fabio", "2");
        await ProcessarAsync(a);

        Assert.Single(await BotAsync(a, r.SessionId));
        var instancia = Assert.Single(await InstanciasAsync(a));
        Assert.Equal(FlowStatus.Cancelled, instancia.Status);
        Assert.Equal("human-takeover", instancia.FailureCode);
        Assert.Equal(AutomationOwner.Agent, (await DecisaoAsync(a, r.SessionId))!.Owner);
        Assert.Empty(await a.TurnosAsync(r.SessionId));
    }

    [Fact]
    public async Task Entregar_ao_agente_faz_o_Agents_responder_a_ultima_pergunta_uma_vez_depois_do_fluxo()
    {
        var a = await CenarioAsync("fluxo-delegar", Duvida());
        var r = await a.EntrarAsync("gabi", "oi");
        await ProcessarAsync(a);
        Assert.Equal("Qual é a sua dúvida?", Assert.Single(await BotAsync(a, r.SessionId)).Text);

        await a.EntrarAsync("gabi", "Como faço para agendar um exame?");
        await ProcessarAsync(a);
        Assert.Single(await a.TurnosAsync(r.SessionId)); // o turno de IA nasceu só DEPOIS da entrega (o evento de liberação o criou)
        Assert.Empty(await a.RespostasDaIAAsync(r.SessionId)); // e ninguém respondeu ainda

        a.Avancar(TimeSpan.FromSeconds(4));
        await a.RelayAsync();
        await a.ExecutarAsync();
        var ia = Assert.Single(await a.RespostasDaIAAsync(r.SessionId));
        Assert.Equal("Resposta do agente de IA.", ia.Text);
        Assert.Single(await BotAsync(a, r.SessionId));
        Assert.Equal(FlowStatus.Delegated, Assert.Single(await InstanciasAsync(a)).Status);
        Assert.Equal(AutomationOwner.Agent, (await DecisaoAsync(a, r.SessionId))!.Owner);

        // A próxima mensagem do cliente já é do agente: nenhum fluxo a toca.
        await a.EntrarAsync("gabi", "obrigada");
        a.Avancar(TimeSpan.FromSeconds(4));
        await ProcessarAsync(a);
        await a.ExecutarAsync();
        Assert.Equal(2, (await a.RespostasDaIAAsync(r.SessionId)).Count);
        Assert.Single(await BotAsync(a, r.SessionId));
    }

    [Fact]
    public async Task O_no_de_conhecimento_responde_com_o_faq_aprovado_sem_chamar_o_modelo()
    {
        var a = await CenarioAsync("fluxo-faq", Duvida());
        await a.PublicarAgenteAsync(new AgentDefinitionContent(
            "Atenda com cortesia.",
            [new FaqEntry("Preciso de jejum para o hemograma?", "Em geral não; confirme com o atendimento.", AutoReply: true)],
            "Só lemos texto.",
            "Vou transferir você."));
        var r = await a.EntrarAsync("hugo", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("hugo", "Preciso de jejum para o hemograma?");
        await ProcessarAsync(a);

        var bot = await BotAsync(a, r.SessionId);
        Assert.Equal("Em geral não; confirme com o atendimento.", bot[^1].Text);
        Assert.Empty(a.PedidosAoModelo);
        Assert.Equal(FlowStatus.Completed, Assert.Single(await InstanciasAsync(a)).Status);
    }

    // ------------------------------------------------------------ concorrência e recuperação

    [Fact]
    public async Task Dois_executores_simultaneos_nunca_rodam_a_mesma_instancia_duas_vezes()
    {
        var a = await CenarioAsync("fluxo-paralelo", Menu());
        var r = await a.EntrarAsync("iris", "oi");
        await a.RelayAsync();

        var um = NovoExecutor(a);
        var outro = NovoExecutor(a);
        await Task.WhenAll(um.RunOnceAsync(default), outro.RunOnceAsync(default));
        await Task.WhenAll(um.RunOnceAsync(default), outro.RunOnceAsync(default));

        Assert.Single(await BotAsync(a, r.SessionId));
        Assert.Single(await InstanciasAsync(a));
    }

    [Fact]
    public async Task Queda_depois_do_registro_da_mensagem_e_recuperada_sem_duplicar_a_mensagem()
    {
        var a = await CenarioAsync("fluxo-queda", Menu());
        var r = await a.EntrarAsync("joao", "oi");
        await ProcessarAsync(a);
        Assert.Single(await BotAsync(a, r.SessionId));

        // Simula o processo que caiu DEPOIS de o Conversations aceitar a mensagem e ANTES de marcar o efeito como feito: o efeito volta a "pendente" e a instância a ficar devida.
        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<WorkflowsDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE workflows.flow_effects SET state = 1, completed_at = NULL, result_code = NULL WHERE tenant_id = {0}", a.Tenant.TenantId.Value);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflows.flow_instances SET next_run_at = {a.Relogio.GetUtcNow()}, lease_owner = NULL, lease_expires_at = NULL WHERE tenant_id = {a.Tenant.TenantId.Value}");
            return 0;
        });
        await ProcessarAsync(a, 3);

        Assert.Single(await BotAsync(a, r.SessionId)); // a mesma intenção, a mesma chave: o Conversations devolve a mensagem que já existe
        Assert.All(await EfeitosAsync(a), e => Assert.Equal(EffectState.Done, e.State));
        Assert.Contains(await EfeitosAsync(a), e => e.ResultCode == "already");
    }

    [Fact]
    public async Task O_limite_de_instancias_ativas_por_tenant_deixa_a_sessao_nova_com_o_agente()
    {
        var a = await CenarioAsync("fluxo-limite", Menu());
        var opcoes = a.Servicos.GetRequiredService<WorkflowsOptions>();
        var original = opcoes.MaxActiveInstancesPerTenant;
        try
        {
            opcoes.MaxActiveInstancesPerTenant = 1;
            var primeira = await a.EntrarAsync("k1", "oi");
            var segunda = await a.EntrarAsync("k2", "oi");
            await ProcessarAsync(a);

            Assert.Single(await InstanciasAsync(a));
            Assert.Equal(AutomationOwner.Flow, (await DecisaoAsync(a, primeira.SessionId))!.Owner);
            Assert.Null(await DecisaoAsync(a, segunda.SessionId)); // sem decisão: do agente
            Assert.Empty(await BotAsync(a, segunda.SessionId));
        }
        finally
        {
            opcoes.MaxActiveInstancesPerTenant = original;
        }
    }

    // ------------------------------------------------------------ variáveis pessoais

    [Fact]
    public async Task As_variaveis_e_o_conteudo_dos_efeitos_ficam_cifrados_no_banco_e_o_historico_so_tem_identificadores()
    {
        var a = await CenarioAsync("fluxo-cifra", Duvida());
        var r = await a.EntrarAsync("lia", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("lia", "Minha senha secreta SEGREDO-DA-LIA para o exame");
        await ProcessarAsync(a);

        var dump = await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<WorkflowsDbContext>();
            var textos = new List<string>();
            textos.AddRange(await db.Instances.AsNoTracking().Select(i => i.VariablesCipher + "|" + i.NodeId + "|" + i.FailureCode).ToListAsync());
            textos.AddRange(await db.Effects.AsNoTracking().Select(e => e.PayloadCipher + "|" + e.ResultCode).ToListAsync());
            textos.AddRange(await db.Steps.AsNoTracking().Select(s => s.NodeId + "|" + s.Outcome).ToListAsync());
            return string.Join('\n', textos);
        });
        Assert.DoesNotContain("SEGREDO-DA-LIA", dump);
        Assert.DoesNotContain("senha", dump, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v1:", dump); // há texto cifrado
        Assert.NotEmpty(await BotAsync(a, r.SessionId));
    }

    // ------------------------------------------------------------ integração (Capabilities)

    private static async Task<string> OperacaoDeLeituraAsync(CenarioDeAgentes a, CapabilityDataClass classe = CapabilityDataClass.Public, CapabilityCaller[]? origens = null)
    {
        var host = $"erp-{Guid.NewGuid().ToString("N")[..10]}.example.com";
        var admin = PostgresFixture.ContextoDeUsuario(a.Tenant, Guid.NewGuid(), new PermissionGrant(Permission.IntegrationManage, null), new PermissionGrant(Permission.IntegrationRead, null));
        await a.C.ComoAsync(admin, async sp =>
        {
            var conexoes = sp.GetRequiredService<ConnectionService>();
            var conexao = await conexoes.CreateAsync(new ConnectionRequest(a.C.OrganizacaoId, "erp-" + Guid.NewGuid().ToString("N")[..8], "ERP de testes", $"https://{host}/api", IaBimeta.Capabilities.Contracts.Ports.ConnectorAuthType.None, null, null, null, null, null, null));
            await conexoes.SetEnabledAsync(conexao.Id, true);
            var definicoes = sp.GetRequiredService<DefinitionService>();
            var molde = new CapabilityOperationSpec(
                "GET",
                "/precos/{exame}",
                [new CapabilityParameterSpec("exame", CapabilityParameterType.String, CapabilityParameterLocation.Path, true, "Nome do exame", MaxLength: 40, Pattern: "^[a-z0-9-]+$")],
                [new CapabilityResponseField("preco", "/preco", CapabilityFieldType.Number), new CapabilityResponseField("prazo", "/prazo", CapabilityFieldType.String)],
                IdempotencySupport.Unspecified,
                MaxAttempts: 1);
            var rascunho = await definicoes.SaveDraftAsync(new DefinitionRequest(conexao.Id, "preco.consultar", "Consultar preço", "Preço e prazo de um exame.", CapabilityEffect.Read, null, origens ?? [CapabilityCaller.Workflow], molde, null, classe));
            await definicoes.PublishAsync(rascunho.Id);
            return 0;
        });
        return host;
    }

    [Fact]
    public async Task O_no_de_integracao_consulta_a_operacao_publica_pelo_Capabilities_e_mostra_o_resultado()
    {
        var a = await CenarioAsync("fluxo-integracao");
        var host = await OperacaoDeLeituraAsync(a);
        banco.Erp.Roteiro = req => ErpSimulado.Json(200, req.Uri.Host == host ? """{"preco":42.5,"prazo":"2 dias","segredo_interno":"NAO-MAPEADO"}""" : "{}");
        await PublicarAsync(a, "preco", Preco());
        await AtivarAsync(a, "preco", OnFailurePolicy.Agent);
        var r = await a.EntrarAsync("mia", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("mia", "glicose");
        await ProcessarAsync(a);

        var bot = await BotAsync(a, r.SessionId);
        Assert.Equal("O exame glicose custa R$ 42.5, pronto em 2 dias.", bot[^1].Text);
        Assert.DoesNotContain("NAO-MAPEADO", string.Join('|', bot.Select(m => m.Text)));
        var chamada = Assert.Single(banco.Erp.De(host));
        Assert.EndsWith("/precos/glicose", chamada.Uri.AbsolutePath);

        var execucao = Assert.Single(await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<CapabilitiesDbContext>().Executions.AsNoTracking().Where(e => e.ServiceSessionId == r.SessionId).ToListAsync()));
        Assert.Equal(CapabilityCaller.Workflow, execucao.Caller);
        Assert.Equal(CapabilityExecutionStatus.Succeeded, execucao.Status);
        Assert.Empty(a.PedidosAoModelo);
    }

    [Fact]
    public async Task Drenagem_recusa_execucao_ativa_e_nao_reenvia_mensagem_ou_consulta_ja_realizada()
    {
        var a = await CenarioAsync("fluxo-drena-em-voo");
        var host = await OperacaoDeLeituraAsync(a);
        var preco = Preco();
        var continua = preco with
        {
            Nodes = [.. preco.Nodes.Where(n => n.Id != "fim"),
                new FlowNode("fim", NodeType.Question, new NodeConfig(Prompt: "Quer consultar outro exame?", Variable: "exame")),
                new FlowNode("encerra", NodeType.End)],
            Edges = [.. preco.Edges, E("fim", "answered", "encerra"), E("fim", "invalid", "encerra")],
        };
        await PublicarAsync(a, "preco", continua);
        await AtivarAsync(a, "preco", OnFailurePolicy.Agent);
        var r = await a.EntrarAsync("cliente", "oi");
        await ProcessarAsync(a);
        var instancia = Assert.Single(await InstanciasAsync(a));
        await a.EntrarAsync("cliente", "glicose");

        var iniciou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concluir = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        banco.Erp.Roteiro = req =>
        {
            iniciou.TrySetResult();
            concluir.Task.GetAwaiter().GetResult();
            return ErpSimulado.RoteiroPadrao(req);
        };

        Task<FlowRunOutcome> RodarAsync() => a.C.ComoAsync(a.C.ContextoDeSistema,
            sp => sp.GetRequiredService<FlowRuntime>().RunAsync(instancia.Id));

        var execucao = Task.Run(RodarAsync);
        try
        {
            await iniciou.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(await EfeitosAsync(a), e => e.Kind == EffectKind.CallCapability && e.State == EffectState.InFlight);
            var saida = new StringWriter();
            Assert.Equal(1, await WorkflowsCommand.ExecuteAsync(a.Servicos, true, a.Tenant.Slug, saida));
            Assert.Contains("execução ativa", saida.ToString());
            Assert.Equal(AutomationOwner.Flow, (await DecisaoAsync(a, r.SessionId))!.Owner);
        }
        finally
        {
            concluir.TrySetResult();
        }

        Assert.Equal(FlowRunOutcome.Waiting, await execucao);
        var realizados = (await EfeitosAsync(a)).Where(e => e.State == EffectState.Done).ToArray();
        Assert.Contains(realizados, e => e.Kind == EffectKind.CallCapability);
        Assert.Contains(realizados, e => e.Kind == EffectKind.SendMessage);
        var antes = await BotAsync(a, r.SessionId);

        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(a.Servicos, true, a.Tenant.Slug, new StringWriter()));
        Assert.Equal(FlowRunOutcome.Finished, await RodarAsync());
        Assert.Equal(AutomationOwner.Agent, (await DecisaoAsync(a, r.SessionId))!.Owner);
        Assert.Single(banco.Erp.De(host));
        Assert.Equal(antes.Select(m => m.Id), (await BotAsync(a, r.SessionId)).Select(m => m.Id));
        foreach (var realizado in realizados)
        {
            var preservado = Assert.Single(await EfeitosAsync(a), e => e.Id == realizado.Id);
            Assert.Equal((EffectState.Done, realizado.ResultCode, realizado.CompletedAt, ""), (preservado.State, preservado.ResultCode, preservado.CompletedAt, preservado.PayloadCipher));
        }
    }

    [Fact]
    public async Task Falha_do_ERP_segue_a_porta_de_falha_do_fluxo_sem_transferir_nem_chamar_o_modelo()
    {
        var a = await CenarioAsync("fluxo-erp-falha");
        var host = await OperacaoDeLeituraAsync(a);
        banco.Erp.Roteiro = req => req.Uri.Host == host ? ErpSimulado.Json(503, "{}") : ErpSimulado.Json(200, "{}");
        await PublicarAsync(a, "preco", Preco());
        await AtivarAsync(a, "preco", OnFailurePolicy.Handoff);
        var r = await a.EntrarAsync("nina", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("nina", "glicose");
        await ProcessarAsync(a);

        Assert.Equal("Não consegui consultar o preço agora.", (await BotAsync(a, r.SessionId))[^1].Text);
        Assert.Equal(FlowStatus.Completed, Assert.Single(await InstanciasAsync(a)).Status);
        Assert.Equal(HandlingMode.AiAutonomous, (await a.C.SessaoAsync(r.SessionId))!.HandlingMode);
        Assert.Empty(a.PedidosAoModelo);
    }

    [Fact]
    public async Task Timeout_depois_da_chamada_ao_ERP_preserva_resultado_incerto()
    {
        var a = await CenarioAsync("fluxo-erp-timeout");
        var host = await OperacaoDeLeituraAsync(a);
        banco.Erp.Roteiro = _ => throw new OutboundTimeoutException();
        await PublicarAsync(a, "preco", Preco());
        await AtivarAsync(a, "preco", OnFailurePolicy.Agent);
        var r = await a.EntrarAsync("cliente", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("cliente", "glicose");
        await ProcessarAsync(a);

        var efeito = Assert.Single(await EfeitosAsync(a), e => e.Kind == EffectKind.CallCapability);
        Assert.Equal((EffectState.Unknown, "outcome-unknown"), (efeito.State, efeito.ResultCode));
        Assert.Single(banco.Erp.De(host));
        Assert.Contains("Não consegui consultar o preço agora.", (await BotAsync(a, r.SessionId)).Select(m => m.Text));
    }

    [Fact]
    public async Task Consulta_em_andamento_quando_o_processo_cai_vira_resultado_ambiguo_e_nunca_e_reenviada()
    {
        var a = await CenarioAsync("fluxo-ambiguo");
        var host = await OperacaoDeLeituraAsync(a);
        await PublicarAsync(a, "preco", Preco());
        await AtivarAsync(a, "preco", OnFailurePolicy.Agent);
        var r = await a.EntrarAsync("otto", "oi");
        await ProcessarAsync(a);
        await a.EntrarAsync("otto", "glicose");
        await ProcessarAsync(a);
        var chamadasNormais = banco.Erp.De(host).Count;
        Assert.Equal(1, chamadasNormais); // a consulta saiu UMA vez, na execução normal

        // O processo "morre no meio da chamada": o efeito da consulta volta a "em andamento", a instância a "aguardando o efeito" e devida, e o ERP agora FALHARIA se fosse chamado de novo.
        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<WorkflowsDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE workflows.flow_effects SET state = 2, completed_at = NULL, result_code = NULL WHERE kind = 2 AND tenant_id = {0}", a.Tenant.TenantId.Value);
            await db.Database.ExecuteSqlRawAsync("UPDATE workflows.flow_effects SET state = 6 WHERE kind <> 2 AND tenant_id = {0} AND step > (SELECT step FROM workflows.flow_effects WHERE kind = 2 AND tenant_id = {0} LIMIT 1)", a.Tenant.TenantId.Value);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE workflows.flow_instances SET status = 3, node_id = 'consulta',
                    pending_effect_key = (SELECT key FROM workflows.flow_effects WHERE kind = 2 AND tenant_id = {a.Tenant.TenantId.Value} LIMIT 1),
                    ended_at = NULL, next_run_at = {a.Relogio.GetUtcNow()}, lease_owner = NULL, lease_expires_at = NULL, failure_code = NULL
                WHERE tenant_id = {a.Tenant.TenantId.Value}
                """);
            await db.Database.ExecuteSqlRawAsync("UPDATE workflows.routing_decisions SET owner = 2 WHERE tenant_id = {0}", a.Tenant.TenantId.Value);
            return 0;
        });
        banco.Erp.Roteiro = _ => throw new InvalidOperationException("a consulta NÃO deve ser reenviada");
        await ProcessarAsync(a, 3);

        Assert.Equal(chamadasNormais, banco.Erp.De(host).Count); // nenhuma chamada nova: o resultado ambíguo nunca é reenviado
        Assert.Contains(await EfeitosAsync(a), e => e.Kind == EffectKind.CallCapability && e.State == EffectState.Unknown);
        Assert.Contains("Não consegui consultar o preço agora.", (await BotAsync(a, r.SessionId)).Select(m => m.Text));
    }
}
