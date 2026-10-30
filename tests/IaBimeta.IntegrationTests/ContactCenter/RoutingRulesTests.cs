using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// As regras de roteamento (canal, unidade e setor para fila e prioridade): cadastro e autorização, a ordem de avaliação, a unidade
/// da fila de destino (que só vale se o número a autoriza, ADR 0008) e a prova de que a IA não escolhe fila, unidade nem prioridade
/// fora delas. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoutingRulesTests(PostgresFixture banco)
{
    private sealed record Cena(
        CenarioDeConversas C,
        CenarioDeConversas Fora,
        Guid Matriz,
        Guid Filial,
        Guid OutraFilial,
        ChannelInstanceId Canal,
        ServiceQueueView FilaDaMatriz,
        ServiceQueueView FilaDaFilial,
        ServiceQueueView FilaDaOutra,
        ServiceQueueView FilaGeral);

    /// <summary>O número autoriza a matriz e a filial; a outra filial existe e tem fila, mas o número NÃO a autoriza.</summary>
    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var outra = await c.CriarUnidadeAsync(c.OrganizacaoId, "Outra filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        return new Cena(
            c, c.ComCanaisIndisponiveis(), matriz, filial, outra, canal,
            await c.CriarFilaAsync("Matriz", unidade: matriz),
            await c.CriarFilaAsync("Filial", unidade: filial),
            await c.CriarFilaAsync("Outra filial", unidade: outra),
            await c.CriarFilaAsync("Geral"));
    }

    // Cadastro e autorização

    [Fact]
    public async Task O_administrador_cria_a_regra_o_gerente_le_e_nao_gere_e_o_usuario_comum_nao_enxerga()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rrcadastro");
        var fila = await c.CriarFilaAsync();
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var setor = await c.CriarSetorAsync(unidade, "Coleta");
        var canal = Guid.NewGuid();
        var gestor = c.GestorDeFilas();
        var leitor = c.LeitorDeFilas();
        var semLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead);

        var regra = await c.CriarRegraAsync(fila.Id, "Coleta da matriz", canal, unidade, setor, prioridade: 4, ordem: 10, contexto: gestor);

        Assert.Equal(("Coleta da matriz", canal, unidade, setor, fila.Id, 4, 10, true), (regra.Name, regra.ChannelInstanceId, regra.BusinessUnitId, regra.DepartmentId, regra.QueueId, regra.Priority, regra.Order, regra.Enabled));
        Assert.Single(await c.AuditoriaAsync("contact-center.routing-rule.create", regra.Id.ToString()), e => e.Outcome == AuditOutcome.Success);

        // Quem só lê as filas lê a regra, mas não gere: negado e auditado.
        Assert.Equal(regra.Id, Assert.Single(await c.ComoAsync(leitor, sp => sp.GetRequiredService<RoutingRuleService>().ListAsync())).Id);
        Assert.Equal(regra.Id, (await c.ComoAsync(leitor, sp => sp.GetRequiredService<RoutingRuleService>().GetAsync(regra.Id)))!.Id);
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.CriarRegraAsync(fila.Id, contexto: leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(leitor, sp => sp.GetRequiredService<RoutingRuleService>().DisableAsync(regra.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(leitor, sp => sp.GetRequiredService<RoutingRuleService>().DeleteAsync(regra.Id)));
        Assert.True((await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().GetAsync(regra.Id)))!.Enabled);
        Assert.Single(await c.AuditoriaAsync("contact-center.routing-rule.create", c.OrganizacaoId.ToString()), e => e.Outcome == AuditOutcome.Denied);

        // Sem leitura de filas, a regra responde como inexistente (e a lista é negada).
        Assert.Null(await c.ComoAsync(semLeitura, sp => sp.GetRequiredService<RoutingRuleService>().GetAsync(regra.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(semLeitura, sp => sp.GetRequiredService<RoutingRuleService>().ListAsync()));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.ComoAsync(semLeitura, sp => sp.GetRequiredService<RoutingRuleService>().EnableAsync(regra.Id)));
        Assert.False(await c.ComoAsync(semLeitura, sp => sp.GetRequiredService<RoutingRuleService>().DeleteAsync(regra.Id)));
    }

    [Fact]
    public async Task Ligar_desligar_e_apagar_sao_idempotentes_e_so_o_que_mudou_e_auditado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rridem");
        var fila = await c.CriarFilaAsync();
        var regra = await c.CriarRegraAsync(fila.Id);
        var servico = (Func<IServiceProvider, RoutingRuleService>)(sp => sp.GetRequiredService<RoutingRuleService>());

        Assert.False((await c.ComoAsync(c.ContextoDeSistema, sp => servico(sp).DisableAsync(regra.Id))).Enabled);
        Assert.False((await c.ComoAsync(c.ContextoDeSistema, sp => servico(sp).DisableAsync(regra.Id))).Enabled);
        Assert.True((await c.ComoAsync(c.ContextoDeSistema, sp => servico(sp).EnableAsync(regra.Id))).Enabled);
        Assert.Single(await c.AuditoriaAsync("contact-center.routing-rule.disable", regra.Id.ToString()));
        Assert.Single(await c.AuditoriaAsync("contact-center.routing-rule.enable", regra.Id.ToString()));

        Assert.True(await c.ComoAsync(c.ContextoDeSistema, sp => servico(sp).DeleteAsync(regra.Id)));
        Assert.False(await c.ComoAsync(c.ContextoDeSistema, sp => servico(sp).DeleteAsync(regra.Id)));
        Assert.Null(await c.ComoAsync(c.ContextoDeSistema, sp => servico(sp).GetAsync(regra.Id)));
        Assert.Single(await c.AuditoriaAsync("contact-center.routing-rule.delete", regra.Id.ToString()));
    }

    [Fact]
    public async Task A_regra_so_aponta_para_fila_unidade_e_setor_da_propria_organizacao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rrvalida");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var fila = await c.CriarFilaAsync();
        var filaDaOutra = await c.CriarFilaAsync("Da outra", organizacao: outra.Id);
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var unidadeDaOutra = await c.CriarUnidadeAsync(outra.Id, "Da outra", matriz: true);
        var setorDaOutra = await c.CriarSetorAsync(unidadeDaOutra, "Setor");

        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarRegraAsync(filaDaOutra.Id));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarRegraAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarRegraAsync(fila.Id, unidade: unidadeDaOutra));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarRegraAsync(fila.Id, unidade: Guid.NewGuid()));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarRegraAsync(fila.Id, unidade: unidade, setor: setorDaOutra));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarRegraAsync(fila.Id, setor: Guid.NewGuid()));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarRegraAsync(fila.Id, prioridade: 10));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.CriarRegraAsync(fila.Id, organizacao: Guid.NewGuid()));
        Assert.Empty(await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().ListAsync()));
    }

    [Fact]
    public async Task A_regra_de_um_tenant_e_inexistente_para_o_outro_e_a_permissao_de_uma_organizacao_nao_vale_em_outra()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rrtenanta");
        var outro = await CenarioDeConversas.CriarAsync(banco, "rrtenantb");
        var outraOrganizacao = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var fila = await c.CriarFilaAsync();
        var regra = await c.CriarRegraAsync(fila.Id);
        var gestorDaOutraOrganizacao = c.GestorDeFilas(outraOrganizacao.Id);

        Assert.Null(await outro.ComoAsync(outro.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().GetAsync(regra.Id)));
        Assert.Empty(await outro.ComoAsync(outro.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().ListAsync()));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.ComoAsync(outro.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().DisableAsync(regra.Id)));
        Assert.False(await outro.ComoAsync(outro.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().DeleteAsync(regra.Id)));
        Assert.Null(await c.ComoAsync(gestorDaOutraOrganizacao, sp => sp.GetRequiredService<RoutingRuleService>().GetAsync(regra.Id)));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.ComoAsync(gestorDaOutraOrganizacao, sp => sp.GetRequiredService<RoutingRuleService>().DisableAsync(regra.Id)));
        Assert.True((await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().GetAsync(regra.Id)))!.Enabled);
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_toda_operacao_de_regras_falha_fechada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rrsemctx");
        var fila = await c.CriarFilaAsync();
        var regra = await c.CriarRegraAsync(fila.Id);

        Task<T> Sem<T>(Func<RoutingRuleService, Task<T>> acao) => c.ComoAsync(null, sp => acao(sp.GetRequiredService<RoutingRuleService>()));

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.CreateAsync(c.OrganizacaoId, "x", null, null, null, fila.Id, 0, 0)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.GetAsync(regra.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ListAsync()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.EnableAsync(regra.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.DisableAsync(regra.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.DeleteAsync(regra.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.PreviewAsync(Guid.NewGuid())));
    }

    // Roteamento

    [Fact]
    public async Task O_roteamento_escolhe_a_fila_e_a_prioridade_pela_regra_e_o_item_guarda_qual_regra_foi()
    {
        var s = await MontarAsync("rrrota");
        var especifica = await s.C.CriarRegraAsync(s.FilaDaMatriz.Id, "Canal na matriz", s.Canal.Value, s.Matriz, prioridade: 5, ordem: 10);
        var padrao = await s.C.CriarRegraAsync(s.FilaGeral.Id, "Padrão", prioridade: 1, ordem: 100);
        var daMatriz = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);
        var semUnidade = await s.C.NovaConversaAsync(canal: s.Canal);

        var primeira = await s.C.PedirPorRotaAsync(daMatriz);
        var segunda = await s.C.PedirPorRotaAsync(semUnidade);

        Assert.Equal((OperationOutcome.Completed, s.FilaDaMatriz.Id, 5, especifica.Id), (primeira.Outcome, primeira.Item.QueueId, primeira.Item.Priority, primeira.Item.RoutingRuleId));
        Assert.Equal((OperationOutcome.Completed, s.FilaGeral.Id, 1, padrao.Id), (segunda.Outcome, segunda.Item.QueueId, segunda.Item.Priority, segunda.Item.RoutingRuleId));
        Assert.Equal(HandlingMode.HumanAssisted, (await s.C.SessaoAsync(daMatriz))!.HandlingMode);
    }

    [Fact]
    public async Task Vale_a_ordem_da_regra_e_no_empate_a_mais_especifica_e_a_regra_desligada_nao_serve()
    {
        var s = await MontarAsync("rrordem");
        var geral = await s.C.CriarRegraAsync(s.FilaGeral.Id, "Geral", ordem: 5);
        var porUnidade = await s.C.CriarRegraAsync(s.FilaDaMatriz.Id, "Unidade", unidade: s.Matriz, ordem: 5);
        var porCanalEUnidade = await s.C.CriarRegraAsync(s.FilaDaFilial.Id, "Canal e unidade", s.Canal.Value, s.Matriz, ordem: 5);
        var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);

        // No empate de ordem, a mais específica vem primeiro.
        var decisao = await s.C.SimularAsync(sessao);
        Assert.Equal(new[] { porCanalEUnidade.QueueId, porUnidade.QueueId, geral.QueueId }, decisao.Candidates.Select(c => c.QueueId).ToArray());

        // A ordem menor vence a especificidade.
        await s.C.CriarRegraAsync(s.FilaGeral.Id, "Mais cedo", ordem: 1);
        Assert.Equal(s.FilaGeral.Id, (await s.C.SimularAsync(sessao)).Candidates[0].QueueId);

        // A regra desligada nunca serve.
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().DisableAsync(porCanalEUnidade.Id));
        Assert.DoesNotContain(porCanalEUnidade.QueueId, (await s.C.SimularAsync(sessao)).Candidates.Select(c => c.QueueId));
    }

    [Fact]
    public async Task Sem_regra_que_sirva_o_pedido_sem_fila_e_recusado_e_nada_muda()
    {
        var s = await MontarAsync("rrsemrota");
        await s.C.CriarRegraAsync(s.FilaDaMatriz.Id, unidade: s.Matriz);
        await s.C.CriarRegraAsync(s.FilaGeral.Id, "Desligada");
        var regraDesligada = (await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().ListAsync())).Single(r => r.Name == "Desligada");
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<RoutingRuleService>().DisableAsync(regraDesligada.Id));
        var daFilial = await s.C.NovaConversaAsync(unidade: s.Filial, canal: s.Canal);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.PedirPorRotaAsync(daFilial));

        Assert.Equal(ContactCenterRule.NoRoute, recusa.Rule);
        Assert.Equal(HandlingMode.AiAutonomous, (await s.C.SessaoAsync(daFilial))!.HandlingMode);
        Assert.Empty(await s.C.ItensAsync());
    }

    [Fact]
    public async Task A_regra_de_outro_canal_ou_de_outra_unidade_nao_serve_e_a_fila_pausada_passa_para_a_proxima_regra()
    {
        var s = await MontarAsync("rrpausada");
        var outroCanal = await s.C.CriarCanalAsync(new[] { s.Matriz });
        await s.C.CriarRegraAsync(s.FilaGeral.Id, "Só do outro canal", outroCanal.Value, ordem: 1);
        await s.C.CriarRegraAsync(s.FilaDaMatriz.Id, "Matriz", unidade: s.Matriz, ordem: 2);
        await s.C.CriarRegraAsync(s.FilaDaFilial.Id, "Reserva", ordem: 3);
        await s.C.PausarAsync(s.FilaDaMatriz.Id);
        var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);

        var decisao = await s.C.SimularAsync(sessao);

        // A regra do outro canal não serve; a da matriz esbarra na fila pausada (recusada) e sobra a reserva.
        Assert.Equal(s.FilaDaFilial.Id, Assert.Single(decisao.Candidates).QueueId);
        Assert.Equal((s.FilaDaMatriz.Id, ContactCenterRule.QueuePaused), (Assert.Single(decisao.Refusals).QueueId, decisao.Refusals[0].Rule));
    }

    // Unidade da fila de destino (ADR 0008)

    [Fact]
    public async Task A_fila_de_outra_unidade_autorizada_pelo_numero_leva_a_sessao_junto_com_o_rastro_nos_dois_modulos()
    {
        var s = await MontarAsync("rrunidade");
        var regra = await s.C.CriarRegraAsync(s.FilaDaFilial.Id, "Tudo para a filial", prioridade: 2);
        var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);

        var resultado = await s.C.PedirPorRotaAsync(sessao);

        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        Assert.Equal((s.FilaDaFilial.Id, s.Filial, 2, regra.Id), (resultado.Item.QueueId, resultado.Item.BusinessUnitId, resultado.Item.Priority, resultado.Item.RoutingRuleId));
        var estado = (await s.C.SessaoAsync(sessao))!;
        Assert.Equal((s.Filial, HandlingMode.HumanAssisted, (Guid?)null), (estado.BusinessUnitId, estado.HandlingMode, estado.ResponderRef));
        var rastro = Assert.Single((await s.C.RastroAsync(sessao))!);
        Assert.Equal((s.Matriz, s.Filial, "queue-transfer"), (rastro.FromBusinessUnitId, rastro.ToBusinessUnitId, rastro.Reason));
        Assert.Single(s.C.PayloadsDe(ConversationEventTypes.SessionTransferred, sessao));
        Assert.Equal(new[] { HistoryKind.Entered }, (await s.C.HistoricoDoItemAsync(resultado.Item.Id))!.Select(h => h.Kind).ToArray());
    }

    [Fact]
    public async Task Unidade_que_o_numero_nao_autoriza_e_recusada_e_o_roteamento_tenta_a_proxima_regra_sem_mexer_na_sessao()
    {
        var s = await MontarAsync("rrnaoautoriza");
        await s.C.CriarRegraAsync(s.FilaDaOutra.Id, "Outra filial", ordem: 1);
        var reserva = await s.C.CriarRegraAsync(s.FilaGeral.Id, "Reserva", ordem: 2);
        var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);

        var resultado = await s.C.PedirPorRotaAsync(sessao);

        // A primeira regra leva para uma unidade que o número não autoriza: recusada pelo Conversations; vale a reserva.
        Assert.Equal((OperationOutcome.Completed, s.FilaGeral.Id, reserva.Id), (resultado.Outcome, resultado.Item.QueueId, resultado.Item.RoutingRuleId));
        Assert.Equal(s.Matriz, (await s.C.SessaoAsync(sessao))!.BusinessUnitId);
        Assert.Empty((await s.C.RastroAsync(sessao))!);
        Assert.Equal(0, s.C.EventosDe(ConversationEventTypes.SessionTransferred, sessao));
        Assert.Single(await s.C.ItensAsync());
        Assert.Contains(await s.C.AuditoriaAsync("contact-center.item.enter"), e => e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Se_nenhum_destino_pode_ter_a_unidade_o_pedido_e_recusado_com_o_motivo_da_unidade_e_a_conversa_continua_com_a_IA()
    {
        var s = await MontarAsync("rrnenhuma");
        await s.C.CriarRegraAsync(s.FilaDaOutra.Id, "Só a outra filial");
        var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.PedirPorRotaAsync(sessao));

        Assert.Equal(ContactCenterRule.UnitNotAuthorizedForChannel, recusa.Rule);
        var estado = (await s.C.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.AiAutonomous, s.Matriz, 1L), (estado.HandlingMode, estado.BusinessUnitId, estado.StateVersion));
        Assert.Empty(await s.C.ItensAsync());
    }

    [Fact]
    public async Task A_IA_nao_escolhe_a_unidade_o_pedido_de_fila_explicita_de_outra_unidade_e_recusado_sem_tentar_mudar_a_sessao()
    {
        var s = await MontarAsync("rria");
        var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);

        // A fila PEDIDA tem de atender a conversa como ela está: nem a autorizada pelo número serve para mudar a unidade por esta via.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.PedirAtendimentoAsync(sessao, s.FilaDaFilial.Id));

        Assert.Equal(ContactCenterRule.QueueDoesNotCoverSession, recusa.Rule);
        Assert.Equal(s.Matriz, (await s.C.SessaoAsync(sessao))!.BusinessUnitId);
        Assert.Empty((await s.C.RastroAsync(sessao))!);
    }

    [Fact]
    public async Task Com_o_Channels_indisponivel_a_entrada_por_rota_com_mudanca_de_unidade_fica_pendente_e_nao_tenta_outro_destino()
    {
        var s = await MontarAsync("rrfora");
        await s.C.CriarRegraAsync(s.FilaDaFilial.Id, "Filial", ordem: 1);
        await s.C.CriarRegraAsync(s.FilaGeral.Id, "Reserva", ordem: 2);
        var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);
        var pedido = Guid.NewGuid();

        var pendente = await s.Fora.PedirPorRotaAsync(sessao, pedido);

        // Sem confirmar a autorização do número a operação espera, e a reserva NÃO é usada no lugar do que não pôde ser confirmado.
        Assert.Equal((OperationOutcome.Pending, PendingKind.Enter, s.FilaDaFilial.Id), (pendente.Outcome, pendente.Item.Pending!.Kind, pendente.Item.QueueId));
        Assert.Equal((HandlingMode.AiAutonomous, s.Matriz), ((await s.C.SessaoAsync(sessao))!.HandlingMode, (await s.C.SessaoAsync(sessao))!.BusinessUnitId));
        Assert.Single(await s.C.ItensAsync());

        // O Channels volta: repetir o mesmo pedido conclui, uma vez só.
        var concluida = await s.C.PedirPorRotaAsync(sessao, pedido);

        Assert.Equal((OperationOutcome.Completed, s.FilaDaFilial.Id, s.Filial), (concluida.Outcome, concluida.Item.QueueId, concluida.Item.BusinessUnitId));
        Assert.Equal(s.Filial, (await s.C.SessaoAsync(sessao))!.BusinessUnitId);
        Assert.Single(await s.C.ItensAsync());
    }

    [Fact]
    public async Task Pedir_por_rota_uma_conversa_que_ja_esta_em_fila_e_repeticao_e_o_mesmo_pedido_nao_duplica()
    {
        var s = await MontarAsync("rrrepete");
        await s.C.CriarRegraAsync(s.FilaGeral.Id);
        var sessao = await s.C.NovaConversaAsync(canal: s.Canal);
        var pedido = Guid.NewGuid();

        var primeiro = await s.C.PedirPorRotaAsync(sessao, pedido);
        var mesmoPedido = await s.C.PedirPorRotaAsync(sessao, pedido);
        var outroPedido = await s.C.PedirPorRotaAsync(sessao);

        Assert.Equal((OperationOutcome.Completed, OperationOutcome.AlreadyApplied, OperationOutcome.AlreadyApplied), (primeiro.Outcome, mesmoPedido.Outcome, outroPedido.Outcome));
        Assert.Single(await s.C.ItensAsync());
        Assert.Equal(1, s.C.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
    }

    [Fact]
    public async Task A_simulacao_so_vale_para_quem_gere_as_filas_nao_muda_nada_e_conversa_alheia_e_inexistente()
    {
        var s = await MontarAsync("rrsimula");
        await s.C.CriarRegraAsync(s.FilaGeral.Id);
        var sessao = await s.C.NovaConversaAsync(canal: s.Canal);
        var leitor = s.C.Usuario(s.C.OrganizacaoId, Permission.OrganizationRead, Permission.QueueRead, Permission.ConversationRead);
        var semLeitura = s.C.Usuario(s.C.OrganizacaoId, Permission.OrganizationRead, Permission.QueueRead);
        var outro = await CenarioDeConversas.CriarAsync(banco, "rrsimulab");

        var decisao = await s.C.SimularAsync(sessao);

        Assert.Equal((s.FilaGeral.Id, 0), (Assert.Single(decisao.Candidates).QueueId, decisao.Candidates[0].Priority));
        Assert.Empty(await s.C.ItensAsync());
        Assert.Equal(HandlingMode.AiAutonomous, (await s.C.SessaoAsync(sessao))!.HandlingMode);
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.C.SimularAsync(sessao, leitor));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => s.C.SimularAsync(sessao, semLeitura));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.SimularAsync(sessao));
        Assert.Single(await s.C.AuditoriaAsync("contact-center.routing.preview", sessao.ToString()), e => e.Outcome == AuditOutcome.Denied);
    }
}
