using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A política da fila: horário de atendimento com fuso explícito, o que fazer fora do horário e sem atendente disponível, a
/// contingência, a presença, o limite por atendente e a prioridade (com a ordem em que a fila é atendida). PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueuePolicyTests(PostgresFixture banco)
{
    // Política e autorização

    [Fact]
    public async Task Quem_gere_as_filas_configura_a_politica_repetir_nao_audita_e_quem_so_le_e_negado_e_auditado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpconfig");
        var fila = await c.CriarFilaAsync("Principal");
        var contingencia = await c.CriarFilaAsync("Contingência");
        var gestor = c.GestorDeFilas();
        var leitor = c.LeitorDeFilas();
        var semLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead);

        var configurada = await c.ConfigurarFilaAsync(fila.Id, contingencia.Id, OutOfHoursBehavior.Fallback, NoAgentBehavior.Fallback, 3, 2, gestor);
        await c.ConfigurarFilaAsync(fila.Id, contingencia.Id, OutOfHoursBehavior.Fallback, NoAgentBehavior.Fallback, 3, 2, gestor);

        Assert.Equal((contingencia.Id, OutOfHoursBehavior.Fallback, NoAgentBehavior.Fallback, 3, 2, 2L), (configurada.FallbackQueueId, configurada.OutOfHours, configurada.NoAgent, configurada.DefaultPriority, configurada.MaxItemsPerAgent, configurada.Version));
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.configure", fila.Id.ToString()), e => e.Outcome == AuditOutcome.Success);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ConfigurarFilaAsync(fila.Id, contexto: leitor));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.ConfigurarFilaAsync(fila.Id, contexto: semLeitura));
        var lida = (await c.LerFilaAsync(fila.Id))!;
        Assert.Equal((OutOfHoursBehavior.Fallback, 2L), (lida.OutOfHours, lida.Version));
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.configure", fila.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task A_contingencia_precisa_ser_outra_fila_da_mesma_organizacao_e_os_valores_sao_validados()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpvalida");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var fila = await c.CriarFilaAsync("Principal");
        var daOutra = await c.CriarFilaAsync("Da outra", organizacao: outra.Id);

        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ConfigurarFilaAsync(fila.Id, daOutra.Id, OutOfHoursBehavior.Fallback));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ConfigurarFilaAsync(fila.Id, Guid.NewGuid(), OutOfHoursBehavior.Fallback));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ConfigurarFilaAsync(fila.Id, fila.Id, OutOfHoursBehavior.Fallback));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ConfigurarFilaAsync(fila.Id, null, OutOfHoursBehavior.Fallback));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ConfigurarFilaAsync(fila.Id, prioridade: 10));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ConfigurarFilaAsync(fila.Id, limite: 0));
        Assert.Equal((OutOfHoursBehavior.Accept, 1L), ((await c.LerFilaAsync(fila.Id))!.OutOfHours, (await c.LerFilaAsync(fila.Id))!.Version));
    }

    [Fact]
    public async Task Outro_tenant_nao_configura_a_fila_e_sem_contexto_toda_operacao_de_politica_falha_fechada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qptenant");
        var outro = await CenarioDeConversas.CriarAsync(banco, "qptenantb");
        var fila = await c.CriarFilaAsync();

        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.ConfigurarFilaAsync(fila.Id));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.DefinirHorarioAsync(fila.Id, "UTC", CenarioDeConversasHorarios.SempreAberto));
        Assert.Null(await outro.HorarioAsync(fila.Id));
        Assert.Null(await outro.DisponibilidadeAsync(fila.Id));

        Task<T> Sem<T>(Func<QueuePolicyService, Task<T>> acao) => c.ComoAsync(null, sp => acao(sp.GetRequiredService<QueuePolicyService>()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ConfigureAsync(fila.Id, null, OutOfHoursBehavior.Accept, NoAgentBehavior.Wait, 0, null)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.SetScheduleAsync(fila.Id, "UTC", Array.Empty<ScheduleWindowInput>(), Array.Empty<DateOnly>())));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ClearScheduleAsync(fila.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.GetScheduleAsync(fila.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.GetAvailabilityAsync(fila.Id)));
    }

    // Horário de atendimento

    [Fact]
    public async Task O_horario_com_fuso_e_guardado_por_inteiro_lido_formatado_e_removido_voltando_a_atender_sempre()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qphorario");
        var fila = await c.CriarFilaAsync();
        var dias = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday };
        var janelas = dias.SelectMany(d => new[] { new ScheduleWindowInput(d, "13:30", "18:00"), new ScheduleWindowInput(d, "08:00", "12:00") }).ToList();
        var feriado = new DateOnly(2026, 12, 25);

        var definido = await c.DefinirHorarioAsync(fila.Id, "America/Sao_Paulo", janelas, new[] { feriado, feriado });

        Assert.Equal("America/Sao_Paulo", definido.TimeZoneId);
        Assert.Equal(new[] { feriado }, definido.ClosedDates.ToArray());
        Assert.Equal(
            new[] { "Monday 08:00-12:00", "Monday 13:30-18:00", "Tuesday 08:00-12:00", "Tuesday 13:30-18:00" },
            definido.Windows.Select(w => $"{w.DayOfWeek} {w.Start}-{w.End}").ToArray());
        Assert.Equal(("America/Sao_Paulo", 2L), ((await c.LerFilaAsync(fila.Id))!.TimeZoneId, (await c.LerFilaAsync(fila.Id))!.Version));
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.schedule.set", fila.Id.ToString()));

        // Trocar é por inteiro: o que não está no novo horário some.
        var trocado = await c.DefinirHorarioAsync(fila.Id, "UTC", new[] { new ScheduleWindowInput(DayOfWeek.Sunday, "00:00", "24:00") });
        Assert.Equal(("UTC", 1, 0), (trocado.TimeZoneId, trocado.Windows.Count, trocado.ClosedDates.Count));

        var limpo = await c.LimparHorarioAsync(fila.Id);
        await c.LimparHorarioAsync(fila.Id);
        Assert.Equal((null, 0, true), (limpo.TimeZoneId, limpo.Windows.Count, limpo.OpenNow));
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.schedule.clear", fila.Id.ToString()));
    }

    [Fact]
    public async Task Horario_invalido_e_recusado_por_inteiro_e_o_anterior_continua()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qphorarioinvalido");
        var fila = await c.CriarFilaAsync();
        await c.DefinirHorarioAsync(fila.Id, "UTC", CenarioDeConversasHorarios.SempreAberto);
        var versao = (await c.LerFilaAsync(fila.Id))!.Version;

        ScheduleWindowInput J(string ini, string fim, DayOfWeek dia = DayOfWeek.Monday) => new(dia, ini, fim);

        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "Marte/Olympus", new[] { J("08:00", "12:00") }));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "", new[] { J("08:00", "12:00") }));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", new[] { J("12:00", "08:00") }));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", new[] { J("8:00", "12:00") }));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", new[] { J("24:00", "24:00") }));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", new[] { J("08:00", "24:01") }));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", new[] { J("08:60", "12:00") }));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", new[] { J("08:00", "12:00"), J("11:00", "14:00") }));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", Enumerable.Range(0, QueueSchedule.MaxWindows + 1).Select(_ => J("08:00", "09:00")).ToList()));

        // Faixas de dias diferentes, ou encostadas no mesmo dia, são válidas.
        await c.DefinirHorarioAsync(fila.Id, "UTC", new[] { J("08:00", "12:00"), J("12:00", "14:00"), J("08:00", "12:00", DayOfWeek.Tuesday) });

        var depois = (await c.LerFilaAsync(fila.Id))!;
        Assert.Equal(versao + 1, depois.Version); // só a troca válida avançou a versão
        Assert.Equal(3, (await c.HorarioAsync(fila.Id))!.Windows.Count);
    }

    [Fact]
    public async Task O_horario_so_e_gerido_por_quem_gere_as_filas_e_e_lido_por_quem_le_ou_esta_na_fila()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qphorarioperfis");
        var fila = await c.CriarFilaAsync();
        var atendente = await c.AgenteNaFilaAsync(banco, fila.Id);
        var forasteiro = await c.NovoAgenteAsync(banco);
        var leitor = c.LeitorDeFilas();
        await c.DefinirHorarioAsync(fila.Id, "UTC", CenarioDeConversasHorarios.SempreAberto);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", CenarioDeConversasHorarios.SempreAberto, contexto: leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.LimparHorarioAsync(fila.Id, leitor));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.DefinirHorarioAsync(fila.Id, "UTC", CenarioDeConversasHorarios.SempreAberto, contexto: atendente.Contexto));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.LimparHorarioAsync(fila.Id, forasteiro.Contexto));

        Assert.NotNull(await c.HorarioAsync(fila.Id, leitor));
        Assert.NotNull(await c.HorarioAsync(fila.Id, atendente.Contexto));
        Assert.Null(await c.HorarioAsync(fila.Id, forasteiro.Contexto));
        Assert.Equal(7, (await c.HorarioAsync(fila.Id))!.Windows.Count);
        Assert.Contains(await c.AuditoriaAsync("contact-center.queue.schedule.set", fila.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);
    }

    // Fora do horário

    private async Task<(CenarioDeConversas C, ServiceQueueView Fechada, ServiceQueueView Aberta)> FilaFechadaAsync(string prefixo, OutOfHoursBehavior comportamento)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var aberta = await c.CriarFilaAsync("Contingência");
        var fechada = await c.CriarFilaAsync("Principal");
        await c.ConfigurarFilaAsync(fechada.Id, comportamento == OutOfHoursBehavior.Fallback ? aberta.Id : null, comportamento);
        await c.DefinirHorarioAsync(fechada.Id, "UTC", CenarioDeConversasHorarios.FechadoHoje());
        return (c, (await c.LerFilaAsync(fechada.Id))!, aberta);
    }

    [Fact]
    public async Task Fora_do_horario_a_politica_aceitar_entra_na_fila_e_espera_a_abertura()
    {
        var (c, fechada, _) = await FilaFechadaAsync("qpaceita", OutOfHoursBehavior.Accept);
        var sessao = await c.NovaConversaAsync();

        var resultado = await c.PedirAtendimentoAsync(sessao, fechada.Id);

        Assert.Equal((OperationOutcome.Completed, fechada.Id), (resultado.Outcome, resultado.Item.QueueId));
        Assert.False((await c.HorarioAsync(fechada.Id))!.OpenNow);
    }

    [Fact]
    public async Task Fora_do_horario_a_politica_recusar_nao_aceita_o_pedido_e_nada_muda()
    {
        var (c, fechada, _) = await FilaFechadaAsync("qprecusa", OutOfHoursBehavior.Reject);
        var sessao = await c.NovaConversaAsync();

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(sessao, fechada.Id));

        Assert.Equal(ContactCenterRule.QueueClosed, recusa.Rule);
        Assert.Equal(HandlingMode.AiAutonomous, (await c.SessaoAsync(sessao))!.HandlingMode);
        Assert.Empty(await c.ItensAsync());
        Assert.Single(await c.AuditoriaAsync("contact-center.item.enter", sessao.ToString()), e => e.Outcome == AuditOutcome.Denied);

        // Aberta a fila (horário removido), o mesmo pedido passa.
        await c.LimparHorarioAsync(fechada.Id);
        Assert.Equal(OperationOutcome.Completed, (await c.PedirAtendimentoAsync(sessao, fechada.Id)).Outcome);
    }

    [Fact]
    public async Task Fora_do_horario_a_politica_contingencia_manda_para_a_outra_fila_e_o_mesmo_pedido_repetido_e_repeticao()
    {
        var (c, fechada, contingencia) = await FilaFechadaAsync("qpfallback", OutOfHoursBehavior.Fallback);
        var sessao = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();

        var resultado = await c.PedirAtendimentoAsync(sessao, fechada.Id, pedido);
        var repetido = await c.PedirAtendimentoAsync(sessao, fechada.Id, pedido);

        Assert.Equal((OperationOutcome.Completed, contingencia.Id), (resultado.Outcome, resultado.Item.QueueId));
        Assert.Equal((OperationOutcome.AlreadyApplied, resultado.Item.Id), (repetido.Outcome, repetido.Item.Id));
        Assert.Single(await c.ItensAsync());
    }

    [Fact]
    public async Task A_contingencia_fechada_tambem_ou_em_ciclo_nao_gira_sem_fim_e_o_pedido_e_recusado_como_fechado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpciclo");
        var a = await c.CriarFilaAsync("A");
        var b = await c.CriarFilaAsync("B");
        await c.ConfigurarFilaAsync(a.Id, b.Id, OutOfHoursBehavior.Fallback);
        await c.ConfigurarFilaAsync(b.Id, a.Id, OutOfHoursBehavior.Fallback);
        await c.DefinirHorarioAsync(a.Id, "UTC", CenarioDeConversasHorarios.FechadoHoje());
        await c.DefinirHorarioAsync(b.Id, "UTC", CenarioDeConversasHorarios.FechadoHoje());
        var sessao = await c.NovaConversaAsync();

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(sessao, a.Id));

        Assert.Equal(ContactCenterRule.QueueClosed, recusa.Rule);
        Assert.Empty(await c.ItensAsync());
    }

    [Fact]
    public async Task A_contingencia_do_pedido_explicito_tambem_precisa_atender_a_conversa_como_ela_esta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpfallbackunidade");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var principal = await c.CriarFilaAsync("Principal", unidade: matriz);
        var daFilial = await c.CriarFilaAsync("Contingência da filial", unidade: filial);
        await c.ConfigurarFilaAsync(principal.Id, daFilial.Id, OutOfHoursBehavior.Fallback);
        await c.DefinirHorarioAsync(principal.Id, "UTC", CenarioDeConversasHorarios.FechadoHoje());
        var sessao = await c.NovaConversaAsync(unidade: matriz);

        // A contingência é de outra unidade: um pedido de fila explícita não muda a unidade da conversa, então não serve.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(sessao, principal.Id));

        Assert.Equal(ContactCenterRule.QueueClosed, recusa.Rule);
        Assert.Equal(matriz, (await c.SessaoAsync(sessao))!.BusinessUnitId);
    }

    [Fact]
    public async Task O_roteamento_por_regra_tambem_respeita_o_horario_e_a_simulacao_mostra_o_caminho()
    {
        var (c, fechada, contingencia) = await FilaFechadaAsync("qpsimula", OutOfHoursBehavior.Fallback);
        await c.CriarRegraAsync(fechada.Id, prioridade: 4);
        var sessao = await c.NovaConversaAsync();

        var decisao = await c.SimularAsync(sessao);

        var candidato = Assert.Single(decisao.Candidates);
        Assert.Equal((contingencia.Id, 4), (candidato.QueueId, candidato.Priority));
        Assert.Equal(new[] { "rule", "out-of-hours:fallback", "no-agent-wait" }, candidato.Notes.ToArray());
        var item = (await c.PedirPorRotaAsync(sessao)).Item;
        Assert.Equal((contingencia.Id, 4), (item.QueueId, item.Priority));
    }

    // Sem atendente disponível, presença e limite

    [Fact]
    public async Task A_presenca_de_cada_atendente_e_dele_o_padrao_e_fora_e_so_disponivel_conta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qppresenca");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var servico = (Func<IServiceProvider, AgentPresenceService>)(sp => sp.GetRequiredService<AgentPresenceService>());

        Assert.Equal(AgentStatus.Offline, (await c.ComoAsync(ana.Contexto, sp => servico(sp).GetMineAsync())).Status);
        Assert.Equal(0, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents);

        await c.PresencaAsync(ana.Contexto, AgentStatus.Available);
        await c.PresencaAsync(ana.Contexto, AgentStatus.Available);
        Assert.Equal(1, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents);
        Assert.Equal(AgentStatus.Offline, (await c.ComoAsync(beto.Contexto, sp => servico(sp).GetMineAsync())).Status);

        await c.PresencaAsync(beto.Contexto, AgentStatus.Available);
        Assert.Equal((2, 2), ((await c.DisponibilidadeAsync(fila.Id))!.Members, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents));
        await c.PresencaAsync(ana.Contexto, AgentStatus.Paused);
        Assert.Equal(1, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents);
        await c.PresencaAsync(beto.Contexto, AgentStatus.Offline);
        Assert.Equal(0, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => c.PresencaAsync(c.ContextoDeSistema, AgentStatus.Available));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.PresencaAsync(ana.Contexto, (AgentStatus)9));
    }

    [Fact]
    public async Task A_presenca_vale_so_para_o_tenant_do_atendente_e_nao_conta_em_fila_de_que_ele_nao_faz_parte()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qppresencatenant");
        var outro = await CenarioDeConversas.CriarAsync(banco, "qppresencatenantb");
        var fila = await c.CriarFilaAsync();
        var filaDoOutro = await c.CriarFilaAsync("Outra fila");
        var ana = await c.AgenteDisponivelAsync(banco, fila.Id);

        Assert.Equal(1, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents);
        Assert.Equal(0, (await c.DisponibilidadeAsync(filaDoOutro.Id))!.AvailableAgents);
        // O mesmo identificador de usuário em outro tenant não herda a presença.
        var homonimo = PostgresFixture.ContextoDeUsuario(outro.Tenant, ana.Id, new PermissionGrant(Permission.OrganizationRead, outro.OrganizacaoId));
        Assert.Equal(AgentStatus.Offline, (await outro.ComoAsync(homonimo, sp => sp.GetRequiredService<AgentPresenceService>().GetMineAsync())).Status);
    }

    [Fact]
    public async Task Sem_atendente_disponivel_a_politica_esperar_entra_na_propria_fila()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpsemagenteespera");
        var fila = await c.CriarFilaAsync();
        var contingencia = await c.CriarFilaAsync("Contingência");
        await c.AgenteDisponivelAsync(banco, contingencia.Id);

        var resultado = await c.PedirAtendimentoAsync(await c.NovaConversaAsync(), fila.Id);

        Assert.Equal(fila.Id, resultado.Item.QueueId);
    }

    [Fact]
    public async Task Sem_atendente_disponivel_a_politica_contingencia_so_vale_se_la_houver_atendente_e_do_contrario_espera_na_propria_fila()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpsemagente");
        var fila = await c.CriarFilaAsync("Principal");
        var contingencia = await c.CriarFilaAsync("Contingência");
        await c.ConfigurarFilaAsync(fila.Id, contingencia.Id, semAtendente: NoAgentBehavior.Fallback);
        var naContingencia = await c.AgenteDisponivelAsync(banco, contingencia.Id);

        // Ninguém na principal e alguém disponível na contingência: vai para a contingência.
        var primeira = await c.PedirAtendimentoAsync(await c.NovaConversaAsync(), fila.Id);
        Assert.Equal(contingencia.Id, primeira.Item.QueueId);

        // A contingência também ficou sem atendente disponível (em pausa): a conversa espera na principal.
        await c.PresencaAsync(naContingencia.Contexto, AgentStatus.Paused);
        var segunda = await c.PedirAtendimentoAsync(await c.NovaConversaAsync(), fila.Id);
        Assert.Equal(fila.Id, segunda.Item.QueueId);

        // Com atendente disponível na principal, fica na principal mesmo havendo contingência.
        await c.PresencaAsync(naContingencia.Contexto, AgentStatus.Available);
        await c.AgenteDisponivelAsync(banco, fila.Id);
        var terceira = await c.PedirAtendimentoAsync(await c.NovaConversaAsync(), fila.Id);
        Assert.Equal(fila.Id, terceira.Item.QueueId);

        var decisao = await c.SimularAsync(await c.NovaConversaAsync());
        Assert.Empty(decisao.Candidates); // sem regras de roteamento, a simulação não tem destino
    }

    [Fact]
    public async Task A_simulacao_de_sem_atendente_mostra_o_desvio_para_a_contingencia()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpsemagentesimula");
        var fila = await c.CriarFilaAsync("Principal");
        var contingencia = await c.CriarFilaAsync("Contingência");
        await c.ConfigurarFilaAsync(fila.Id, contingencia.Id, semAtendente: NoAgentBehavior.Fallback);
        await c.AgenteDisponivelAsync(banco, contingencia.Id);
        await c.CriarRegraAsync(fila.Id);

        var decisao = await c.SimularAsync(await c.NovaConversaAsync());

        var candidato = Assert.Single(decisao.Candidates);
        Assert.Equal(contingencia.Id, candidato.QueueId);
        Assert.Equal(new[] { "rule", "no-agent:fallback" }, candidato.Notes.ToArray());
    }

    // Limite por atendente

    [Fact]
    public async Task O_limite_por_atendente_barra_o_assumir_por_conta_propria_mas_nao_a_atribuicao_do_supervisor_e_a_disponibilidade_o_reflete()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qplimite");
        var fila = await c.CriarFilaAsync();
        await c.ConfigurarFilaAsync(fila.Id, limite: 1);
        var ana = await c.AgenteDisponivelAsync(banco, fila.Id);
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var terceiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);

        await c.AssumirAsync(primeiro.Id, ana.Contexto);
        Assert.Equal(0, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.AssumirAsync(segundo.Id, ana.Contexto));
        Assert.Equal(ContactCenterRule.AgentAtCapacity, recusa.Rule);
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(segundo.Id))!.Status);
        Assert.Null((await c.SessaoAsync(segundo.SessionId))!.ResponderRef);

        // O supervisor decide pelo atendente.
        Assert.Equal(ana.Id, (await c.AtribuirAsync(segundo.Id, ana.Id, supervisor.Contexto)).Item.AssignedUserId);

        // Liberando uma, o atendente já pode assumir de novo? Ele ainda tem uma (a do supervisor), então continua no limite.
        await c.DevolverAFilaAsync(primeiro.Id, ana.Contexto);
        await c.DevolverAFilaAsync(segundo.Id, ana.Contexto);
        Assert.Equal(1, (await c.DisponibilidadeAsync(fila.Id))!.AvailableAgents);
        Assert.Equal(OperationOutcome.Completed, (await c.AssumirAsync(terceiro.Id, ana.Contexto)).Outcome);
    }

    // Prioridade e a ordem da fila

    [Fact]
    public async Task A_fila_e_atendida_do_mais_urgente_para_o_menos_e_na_mesma_prioridade_do_mais_antigo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpordem");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var i1 = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var i2 = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var i3 = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var i4 = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.PrioridadeAsync(i2.Id, 5, supervisor.Contexto);
        await c.PrioridadeAsync(i3.Id, 9, supervisor.Contexto);
        await c.PrioridadeAsync(i4.Id, 5, supervisor.Contexto);

        Assert.Equal(new[] { i3.Id, i2.Id, i4.Id, i1.Id }, (await c.ItensAsync(ana.Contexto, fila.Id)).Select(i => i.Id).ToArray());

        var ordem = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ordem.Add((await c.AssumirProximaAsync(fila.Id, ana.Contexto))!.Item.Id);
        }

        Assert.Equal(new[] { i3.Id, i2.Id, i4.Id, i1.Id }, ordem.ToArray());
        Assert.Null(await c.AssumirProximaAsync(fila.Id, ana.Contexto));
        Assert.Equal(4, (await c.ItensAsync(ana.Contexto, fila.Id, QueueItemStatus.Assigned)).Count);
    }

    [Fact]
    public async Task Assumir_a_proxima_repetindo_o_mesmo_pedido_devolve_a_mesma_conversa_e_nao_pega_outra()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpproxidem");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var pedido = Guid.NewGuid();

        var a = await c.AssumirProximaAsync(fila.Id, ana.Contexto, pedido);
        var b = await c.AssumirProximaAsync(fila.Id, ana.Contexto, pedido);

        Assert.Equal((OperationOutcome.Completed, primeiro.Id), (a!.Outcome, a.Item.Id));
        Assert.Equal((OperationOutcome.AlreadyApplied, primeiro.Id), (b!.Outcome, b.Item.Id));
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(segundo.Id))!.Status);
    }

    [Fact]
    public async Task Assumir_a_proxima_conclui_a_operacao_que_ficou_pendente_com_o_mesmo_pedido()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpproxpendente");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var pedido = await c.GravarIntencaoDeAssumirAsync(primeiro.Id, ana.Id);

        var concluida = await c.AssumirProximaAsync(fila.Id, ana.Contexto, pedido);

        Assert.Equal((OperationOutcome.Completed, primeiro.Id), (concluida!.Outcome, concluida.Item.Id));
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(segundo.Id))!.Status);
    }

    [Fact]
    public async Task Assumir_a_proxima_so_vale_para_quem_esta_na_fila_e_pula_a_que_tem_operacao_em_andamento()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpproxacesso");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var forasteiro = await c.NovoAgenteAsync(banco);
        var leitor = c.LeitorDeFilas();
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);

        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.AssumirProximaAsync(fila.Id, forasteiro.Contexto));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.AssumirProximaAsync(fila.Id, leitor));
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => c.AssumirProximaAsync(fila.Id, c.ContextoDeSistema));
        Assert.Contains(await c.AuditoriaAsync("contact-center.item.assign", fila.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);

        // A primeira está com a operação da Ana em andamento: o Beto pega a seguinte, sem esperar nem atropelar.
        await c.GravarIntencaoDeAssumirAsync(primeiro.Id, ana.Id);
        var dele = await c.AssumirProximaAsync(fila.Id, beto.Contexto);

        Assert.Equal(segundo.Id, dele!.Item.Id);
        Assert.Null((await c.SessaoAsync(primeiro.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task Varios_atendentes_assumindo_a_proxima_ao_mesmo_tempo_levam_conversas_diferentes_e_nenhuma_e_levada_duas_vezes()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpproxdisputa");
        var fila = await c.CriarFilaAsync();
        var agentes = new List<Agente>();
        for (var i = 0; i < 8; i++)
        {
            agentes.Add(await c.AgenteNaFilaAsync(banco, fila.Id));
        }

        var itens = new List<QueueItemView>();
        for (var i = 0; i < 3; i++)
        {
            itens.Add(await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id));
        }

        using var largada = new ManualResetEventSlim(false);
        var disputas = agentes.Select(a => Task.Run(async () =>
        {
            largada.Wait();
            return (Agente: a, Resultado: await c.AssumirProximaAsync(fila.Id, a.Contexto));
        })).ToList();
        largada.Set();
        var resultados = await Task.WhenAll(disputas);

        var levadas = resultados.Where(r => r.Resultado is not null).ToList();
        Assert.Equal(3, levadas.Count);
        Assert.Equal(itens.Select(i => i.Id).OrderBy(x => x), levadas.Select(r => r.Resultado!.Item.Id).OrderBy(x => x));
        foreach (var (agente, resultado) in levadas)
        {
            Assert.Equal(agente.Id, (await c.ItemAsync(resultado!.Item.Id))!.AssignedUserId);
            Assert.Equal(agente.Id, (await c.SessaoAsync(resultado.Item.SessionId))!.ResponderRef);
        }

        Assert.Empty(await c.ItensAsync(fila: fila.Id, situacao: QueueItemStatus.Waiting));
    }

    [Fact]
    public async Task Mudar_a_prioridade_e_so_de_quem_supervisiona_fica_no_historico_e_repetir_e_inofensivo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpprioridade");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        var pedido = Guid.NewGuid();

        // O atendente que é o responsável também não escolhe a prioridade: nega, audita e nada muda.
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.PrioridadeAsync(item.Id, 9, ana.Contexto));
        Assert.Equal(0, (await c.ItemAsync(item.Id))!.Priority);

        var mudou = await c.PrioridadeAsync(item.Id, 7, supervisor.Contexto, pedido);
        var repetiu = await c.PrioridadeAsync(item.Id, 7, supervisor.Contexto, pedido);
        var igual = await c.PrioridadeAsync(item.Id, 7, supervisor.Contexto);

        Assert.Equal((OperationOutcome.Completed, 7), (mudou.Outcome, mudou.Item.Priority));
        Assert.Equal((OperationOutcome.AlreadyApplied, OperationOutcome.AlreadyApplied), (repetiu.Outcome, igual.Outcome));
        var rastro = (await c.HistoricoDoItemAsync(item.Id))!.Last();
        Assert.Equal((HistoryKind.PriorityChanged, 0, 7, supervisor.Id, pedido), (rastro.Kind, rastro.FromPriority, rastro.ToPriority, rastro.ActorUserId, rastro.OperationId));
        Assert.Single((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind == HistoryKind.PriorityChanged);
        Assert.Single(await c.AuditoriaAsync("contact-center.item.priority", item.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);
        Assert.Single(await c.AuditoriaAsync("contact-center.item.priority", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);

        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.PrioridadeAsync(item.Id, 10, supervisor.Contexto));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.PrioridadeAsync(item.Id, -1, supervisor.Contexto));
        await c.DevolverAIAAsync(item.Id, ana.Contexto);
        var fechado = await Assert.ThrowsAsync<QueueItemConflictException>(() => c.PrioridadeAsync(item.Id, 1, supervisor.Contexto));
        Assert.Equal(QueueItemConflict.ItemClosed, fechado.Conflict);
    }

    [Fact]
    public async Task A_prioridade_do_item_vem_da_regra_ou_do_padrao_da_fila_e_nunca_de_quem_pede()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qpprioridadeorigem");
        var comPadrao = await c.CriarFilaAsync("Com padrão");
        var urgente = await c.CriarFilaAsync("Urgente");
        await c.ConfigurarFilaAsync(comPadrao.Id, prioridade: 3);
        await c.CriarRegraAsync(urgente.Id, "Urgente", prioridade: 8, ordem: 1);

        var explicita = await c.PedirAtendimentoAsync(await c.NovaConversaAsync(), comPadrao.Id);
        var porRegra = await c.PedirPorRotaAsync(await c.NovaConversaAsync());

        Assert.Equal((3, null), (explicita.Item.Priority, explicita.Item.RoutingRuleId));
        Assert.Equal((urgente.Id, 8), (porRegra.Item.QueueId, porRegra.Item.Priority));
    }
}

/// <summary>Faixas de horário prontas, em UTC, para os testes que precisam de uma fila aberta ou fechada em qualquer instante.</summary>
internal static class CenarioDeConversasHorarios
{
    public static IReadOnlyList<ScheduleWindowInput> SempreAberto => HandoffTestSupport.SempreAberto;

    public static IReadOnlyList<ScheduleWindowInput> FechadoHoje() => HandoffTestSupport.FechadoHoje();
}
