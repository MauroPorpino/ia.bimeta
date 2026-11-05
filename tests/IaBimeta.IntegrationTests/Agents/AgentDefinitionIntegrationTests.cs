using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// A definição do agente do tenant (instruções e FAQ publicados, ADR 0017 F) e as restrições do esquema <c>agents</c> com PostgreSQL real: só quem gere as configurações lê e altera; o turno usa
/// SÓ a versão publicada; publicar aposenta a anterior; o banco recusa dois rascunhos, duas publicadas e dois turnos abertos da mesma sessão; nada do conteúdo vai para a auditoria.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentDefinitionIntegrationTests(PostgresFixture banco)
{
    private static AgentDefinitionContent Conteudo(string rotulo) => new(
        $"Instruções {rotulo}",
        new[] { new FaqEntry($"Pergunta {rotulo}?", $"Resposta {rotulo}.") },
        $"Áudio {rotulo}",
        $"Transferência {rotulo}");

    private static Task<T> Servico<T>(CenarioDeAgentes a, RequestContext contexto, Func<AgentDefinitionService, Task<T>> acao) =>
        a.C.ComoAsync(contexto, sp => acao(sp.GetRequiredService<AgentDefinitionService>()));

    private static Task<AgentDefinitionContent?> Publicada(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<IAgentDefinitionReader>().GetPublishedAsync(default));

    private static Task<List<AgentDefinitionVersion>> Versoes(CenarioDeAgentes a) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().DefinitionVersions.AsNoTracking().OrderBy(v => v.Number).ToListAsync());

    [Fact]
    public async Task So_quem_gere_as_configuracoes_le_ou_altera_a_definicao_do_agente()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdefperm", publicarAgente: false);
        var semPermissao = PostgresFixture.ContextoDeUsuario(a.Tenant, Guid.NewGuid(), new PermissionGrant(Permission.ConversationRead, null));

        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, semPermissao, s => s.GetDraftAsync()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, semPermissao, s => s.GetPublishedAsync()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, semPermissao, s => s.SaveDraftAsync(Conteudo("x"))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Servico(a, semPermissao, s => s.PublishAsync()));
        Assert.Empty(await Versoes(a));
    }

    [Fact]
    public async Task O_agente_usa_so_a_versao_publicada_o_rascunho_nunca_e_lido_e_publicar_aposenta_a_anterior()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdefver", publicarAgente: false);
        Assert.Null(await Publicada(a)); // sem publicação, o agente não tem instruções

        var rascunho1 = await Servico(a, a.Admin, s => s.SaveDraftAsync(Conteudo("um")));
        Assert.Equal((1, AgentDefinitionStatus.Draft), (rascunho1.Number, rascunho1.Status));
        Assert.Null(await Publicada(a)); // o rascunho NUNCA chega ao turno

        await Servico(a, a.Admin, s => s.PublishAsync());
        Assert.Equal("Instruções um", (await Publicada(a))!.Instructions);
        Assert.Null(await Servico(a, a.Admin, s => s.GetDraftAsync())); // o rascunho virou a publicada

        // Um rascunho novo (versão 2) não muda o que o agente usa até ser publicado.
        var rascunho2 = await Servico(a, a.Admin, s => s.SaveDraftAsync(Conteudo("dois")));
        Assert.Equal(2, rascunho2.Number);
        Assert.Equal("Instruções um", (await Publicada(a))!.Instructions);
        await Servico(a, a.Admin, s => s.SaveDraftAsync(Conteudo("dois-b"))); // trocar o rascunho mantém o número
        Assert.Equal(2, (await Servico(a, a.Admin, s => s.GetDraftAsync()))!.Number);

        await Servico(a, a.Admin, s => s.PublishAsync());

        var conteudo = (await Publicada(a))!;
        Assert.Equal(("Instruções dois-b", "Transferência dois-b"), (conteudo.Instructions, conteudo.HandoffNoticeText));
        var versoes = await Versoes(a);
        Assert.Equal(new[] { AgentDefinitionStatus.Retired, AgentDefinitionStatus.Published }, versoes.Select(v => v.Status).ToArray());
        Assert.Equal(new[] { 1, 2 }, versoes.Select(v => v.Number).ToArray());
    }

    [Fact]
    public async Task Publicar_sem_rascunho_e_conteudo_invalido_sao_recusados_e_nada_e_gravado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdefinv", publicarAgente: false);

        await Assert.ThrowsAsync<AgentsValidationException>(() => Servico(a, a.Admin, s => s.PublishAsync()));
        await Assert.ThrowsAsync<AgentsValidationException>(() => Servico(a, a.Admin, s => s.SaveDraftAsync(new AgentDefinitionContent("   ", Array.Empty<FaqEntry>()))));
        await Assert.ThrowsAsync<AgentsValidationException>(() => Servico(a, a.Admin, s => s.SaveDraftAsync(Conteudo("x") with { Instructions = new string('x', AgentDefinitionContent.InstructionsMaxLength + 1) })));
        Assert.Empty(await Versoes(a));
    }

    [Fact]
    public async Task A_auditoria_registra_quem_publicou_sem_nenhum_conteudo_das_instrucoes_ou_do_FAQ()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdefaud", publicarAgente: false);
        await Servico(a, a.Admin, s => s.SaveDraftAsync(Conteudo("canario-de-conteudo")));
        await Servico(a, a.Admin, s => s.PublishAsync());

        var operacoes = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().Database
            .SqlQueryRaw<string>("select operation as \"Value\" from audit.audit_events where tenant_id = {0} and operation like 'agents.definition.%' order by operation", a.Tenant.TenantId.Value)
            .ToListAsync());
        var vazou = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from audit.audit_events x where tenant_id = {0} and to_jsonb(x)::text like {1}", a.Tenant.TenantId.Value, "%canario-de-conteudo%")
            .SingleAsync());

        Assert.Equal(new[] { "agents.definition.draft-saved", "agents.definition.published" }, operacoes);
        Assert.Equal(0, vazou);
    }

    [Fact]
    public async Task Criar_o_primeiro_rascunho_ao_mesmo_tempo_nunca_deixa_dois_e_a_perdedora_recebe_conflito_explicito()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdefconc", publicarAgente: false);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Servico(a, a.Admin, s => s.SaveDraftAsync(Conteudo($"c{i}"))).ContinueWith(t => t.Exception?.GetBaseException())));

        Assert.All(resultados, e => Assert.True(e is null or AgentDefinitionConflictException, $"Falha inesperada: {e}"));
        var versoes = await Versoes(a);
        Assert.Single(versoes);
        Assert.Equal(AgentDefinitionStatus.Draft, versoes[0].Status);
    }

    [Fact]
    public async Task Cada_tenant_tem_a_propria_definicao_e_a_numeracao_comeca_em_um()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdefisoa");
        var b = await CenarioDeAgentes.CriarAsync(banco, "agdefisob", publicarAgente: false);

        Assert.Contains(a.CanarioDasInstrucoes, (await Publicada(a))!.Instructions);
        Assert.Null(await Publicada(b)); // a publicação de A não vale para B
        var rascunho = await Servico(b, b.Admin, s => s.SaveDraftAsync(Conteudo("de-b")));
        Assert.Equal(1, rascunho.Number);
        Assert.DoesNotContain(b.CanarioDasInstrucoes, (await Publicada(a))!.Instructions);
        Assert.All(await Versoes(a), v => Assert.Equal(a.Tenant.TenantId, v.TenantId));
    }

    // As restrições do banco

    private static Task<int> Sql(CenarioDeAgentes a, string comando, params object[] parametros) =>
        a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AgentsDbContext>().Database.ExecuteSqlRawAsync(comando, parametros));

    private static Task<int> InserirTurno(CenarioDeAgentes a, Guid sessao, int estado, string colunasExtras = "", string valoresExtras = "") =>
        Sql(
            a,
            "INSERT INTO agents.ai_turns (tenant_id, id, session_id, state, first_received_at, last_received_at, fire_at, attempts, supersede_count, input_pending, notice_done, created_at, updated_at, version"
            + colunasExtras + ") VALUES ({0}, {1}, {2}, {3}, now(), now(), now(), 0, 0, false, false, now(), now(), 1" + valoresExtras + ")",
            a.Tenant.TenantId.Value, Guid.NewGuid(), sessao, (short)estado);

    [Fact]
    public async Task O_banco_recusa_dois_turnos_abertos_na_mesma_sessao_mas_aceita_varios_terminais()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdbuniq", publicarAgente: false, configurarIA: false);
        var sessao = Guid.NewGuid();
        const string terminal = ", ended_at, end_reason";

        await InserirTurno(a, sessao, 1);
        var duplicado = await Assert.ThrowsAsync<PostgresException>(() => InserirTurno(a, sessao, 2, ", next_attempt_at", ", now()"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicado.SqlState);

        // Terminais não disputam o índice: o histórico da sessão pode ter quantos turnos terminou.
        await InserirTurno(a, sessao, 4, terminal, ", now(), 'replied'");
        await InserirTurno(a, sessao, 5, terminal, ", now(), 'stale-input'");

        // Outra sessão do mesmo tenant tem o próprio turno aberto.
        await InserirTurno(a, Guid.NewGuid(), 1);
    }

    [Theory]
    [InlineData(3, "", "")] // em andamento sem token de lane, dono e lease
    [InlineData(4, "", "")] // terminal sem fim nem motivo
    [InlineData(4, ", ended_at", ", now()")] // terminal sem motivo
    [InlineData(1, ", next_attempt_at", ", now()")] // recuo fora do estado pronto
    [InlineData(1, ", ended_at, end_reason", ", now(), 'x'")] // aberto com fim
    [InlineData(9, "", "")] // estado inexistente
    public async Task O_banco_recusa_turno_incoerente(int estado, string colunas, string valores)
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdbck", publicarAgente: false, configurarIA: false);

        var falha = await Assert.ThrowsAsync<PostgresException>(() => InserirTurno(a, Guid.NewGuid(), estado, colunas, valores));

        Assert.Equal(PostgresErrorCodes.CheckViolation, falha.SqlState);
    }

    [Fact]
    public async Task O_banco_recusa_dois_rascunhos_duas_publicadas_e_definicao_incoerente_do_mesmo_tenant()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdbdef", publicarAgente: false, configurarIA: false);
        Task<int> Inserir(int numero, int estado, string instrucoes = "texto", bool publicada = false) => Sql(
            a,
            "INSERT INTO agents.agent_definition_versions (tenant_id, id, number, status, instructions, faq, created_at, updated_at, revision, published_at) VALUES ({0}, {1}, {2}, {3}, {4}, '[]'::jsonb, now(), now(), 1, "
            + (publicada ? "now()" : "NULL") + ")",
            a.Tenant.TenantId.Value, Guid.NewGuid(), numero, (short)estado, instrucoes);

        await Inserir(1, 1);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => Inserir(2, 1))).SqlState); // dois rascunhos
        await Inserir(3, 2, publicada: true);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => Inserir(4, 2, publicada: true))).SqlState); // duas publicadas
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => Inserir(3, 3, publicada: true))).SqlState); // número repetido
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => Inserir(5, 3, instrucoes: ""))).SqlState); // instruções vazias
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => Inserir(6, 2, publicada: false))).SqlState); // publicada sem data
    }
}
