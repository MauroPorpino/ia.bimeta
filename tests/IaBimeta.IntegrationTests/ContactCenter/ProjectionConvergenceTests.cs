using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// Um evento externo MAIS NOVO que o item já observou não pode ser dado por tratado (o inbox o confirma definitivamente) enquanto o item não
/// pode projetá-lo, ou o item fica atrás do Conversations sem nada a reaplicar. Quando chega com uma operação do item pendente, a projeção é
/// ADIADA de forma durável (a versão fica no item, na mesma gravação do inbox) e o item se alinha à sessão quando a operação termina. A
/// gravação do item avança a versão: a corrida entre a conclusão da operação, a releitura do Conversations e a chegada do evento termina com
/// quem perdeu detectado, nunca com a atualização mais nova sobrescrita em silêncio. PostgreSQL real; as intercalações são determinísticas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectionConvergenceTests(PostgresFixture banco)
{
    /// <summary>A fila A é da matriz e a B, da filial. A Ana atende na A; a Carla e a Dora são da B. O relay do Conversations está ligado.</summary>
    private sealed record Cena(
        CenarioDeConversas C, Guid Matriz, Guid Filial, ServiceQueueView FilaA, ServiceQueueView FilaB,
        Agente SupervisorDeB, Agente Ana, Agente Beto, Agente Carla, Agente Dora, Guid Sessao, QueueItemView Item);

    private async Task<Cena> MontarAsync(string prefixo)
    {
        banco.RelogioDaMensageria.Sincronizar();
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true, mensageria: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        var filaA = await c.CriarFilaAsync("A", unidade: matriz);
        var filaB = await c.CriarFilaAsync("B", unidade: filial);
        var supervisorDeB = await c.AgenteNaFilaAsync(banco, filaB.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var beto = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var carla = await c.AgenteNaFilaAsync(banco, filaB.Id);
        var dora = await c.AgenteNaFilaAsync(banco, filaB.Id);

        var sessao = await c.NovaConversaAsync(unidade: matriz, canal: canal);
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        item = (await c.AssumirAsync(item.Id, ana.Contexto)).Item;
        await c.RodarRelayAsync(); // os eventos das operações do próprio ContactCenter já foram entregues: só o que vem agora é novo
        return new Cena(c, matriz, filial, filaA, filaB, supervisorDeB, ana, beto, carla, dora, sessao, item);
    }

    /// <summary>O relógio de teste do Messaging é parado: avança um pouco para a operação pendente ficar elegível e tira o recuo dela.</summary>
    private async Task LiberarAsync(Cena s)
    {
        banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(1));
        await s.C.LiberarRecuoAsync(s.Item.Id);
    }

    private static async Task MudarPorForaAsync(Cena s, Guid responsavel) =>
        Assert.Equal(
            TransitionOutcome.Applied,
            (await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, responsavel, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);

    private static Task<QueueItem> LinhaAsync(Cena s) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Items.AsNoTracking().SingleAsync(i => i.Id == s.Item.Id));

    private static Task<QueueItemOperationResult> TransferirAsync(Cena s, Action<Interceptador<IConversationGateway>> ponto, Guid pedido) =>
        s.C.NaExecucaoAsync(s.Ana.Contexto, ponto, svc => svc.TransferAsync(s.Item.Id, s.FilaB.Id, null, pedido));

    /// <summary>O Conversations CONFIRMA a unidade e a resposta se perde: a intenção fica pendente, com a unidade já aplicada na sessão.</summary>
    private static async Task UnidadeAplicadaRespostaPerdidaAsync(Cena s, Guid pedido)
    {
        var interrompida = await TransferirAsync(s, g => g.Depois(nameof(IConversationGateway.TransferAsync), () => throw new InvalidOperationException("a resposta se perdeu")), pedido);
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        Assert.Equal(s.Filial, (await s.C.SessaoAsync(s.Sessao))!.BusinessUnitId);
    }

    /// <summary>O item convergiu: na fila de destino, com o responsável do Conversations, sem operação pendente e sem nada por projetar.</summary>
    private static async Task AfirmarConvergidoAsync(Cena s, Guid responsavel)
    {
        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaB.Id, s.Filial, QueueItemStatus.Assigned, responsavel, null), (item.QueueId, item.BusinessUnitId, item.Status, item.AssignedUserId, item.Pending));
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((s.Filial, HandlingMode.HumanAssisted, (Guid?)responsavel), (sessao.BusinessUnitId, sessao.HandlingMode, sessao.ResponderRef));
        var linha = await LinhaAsync(s);
        Assert.False(linha.NeedsResync);
        Assert.True(linha.ObservedStateVersion >= sessao.StateVersion);
    }

    // A reprodução da auditoria: Carla/V → Dora/V+1 com o evento chegando antes da conclusão parcial

    [Fact]
    public async Task O_evento_da_Dora_que_chega_antes_da_conclusao_parcial_nao_e_perdido_e_o_item_converge_para_a_Dora()
    {
        var s = await MontarAsync("conv1");
        var pedido = Guid.NewGuid();
        long versaoDaCarla = 0;

        // 1) A unidade da filial é confirmada. 2) A Carla assume antes da transição de quem responde. 3) Depois de a transição ser recusada, o executor
        // RELÊ a sessão (a Carla, versão V). 4) ANTES da gravação local, a Dora é atribuída (V+1) e os eventos chegam ao ContactCenter, com a
        // transferência ainda pendente. O caminho antigo dava o evento da Dora por tratado sem projetá-lo e gravava a Carla, e a Dora ficava sem operar.
        var conflito = await Assert.ThrowsAsync<ContactCenterConflictException>(() => TransferirAsync(s, g => g
            .Depois(nameof(IConversationGateway.TransferAsync), async () =>
            {
                await MudarPorForaAsync(s, s.Carla.Id);
                versaoDaCarla = await s.C.VersaoDaSessaoAsync(s.Sessao);
            })
            .Depois(nameof(IConversationGateway.GetSessionAsync), async () =>
            {
                await MudarPorForaAsync(s, s.Dora.Id);
                await s.C.RodarRelayAsync();
            }, chamada: 2), pedido));
        Assert.NotNull(conflito);
        Assert.True(versaoDaCarla > 0);

        // O item NÃO foi sobrescrito com a Carla: a gravação que se baseava na leitura antiga perdeu a corrida, a intenção continua pendente e a
        // versão do evento da Dora ficou guardada de forma durável.
        var pendente = await LinhaAsync(s);
        Assert.True(pendente.HasPending);
        Assert.Equal(versaoDaCarla + 1, pendente.ResyncStateVersion);
        Assert.Equal(s.Ana.Id, pendente.AssignedUserId);
        Assert.Equal(1, await s.C.RegistrosDoInboxAsync((await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao)).Last(o => o.EventType == ConversationEventTypes.HandlingModeChanged).Id));

        // A reconciliação reexecuta com a sessão atual: a Dora é a responsável (a Carla nunca foi gravada), e a marca fica atendida.
        await LiberarAsync(s);
        Assert.Equal(1, await s.C.ReconciliarAsync());
        await AfirmarConvergidoAsync(s, s.Dora.Id);
        Assert.Null((await LinhaAsync(s)).ResyncStateVersion);

        // A Dora opera a própria conversa; a Carla, que o Conversations deixou para trás, não.
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.DevolverAFilaAsync(s.Item.Id, s.Carla.Contexto));
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAFilaAsync(s.Item.Id, s.Dora.Contexto)).Outcome);
    }

    [Fact]
    public async Task Com_a_resposta_da_unidade_perdida_os_eventos_adiados_sao_alinhados_pela_reconciliacao_e_repetir_tudo_nao_duplica_nem_regride()
    {
        var s = await MontarAsync("conv2");
        await UnidadeAplicadaRespostaPerdidaAsync(s, Guid.NewGuid());

        // Carla e Dora assumem por fora, UMA depois da outra, com a transferência pendente: os dois eventos chegam e nenhum é projetado agora.
        await MudarPorForaAsync(s, s.Carla.Id);
        await MudarPorForaAsync(s, s.Dora.Id);
        var versaoDaDora = await s.C.VersaoDaSessaoAsync(s.Sessao);
        var eventosDoRelay = await s.C.RodarRelayAsync();
        Assert.True(eventosDoRelay.Processed >= 2);
        var adiado = await LinhaAsync(s);
        Assert.True(adiado.HasPending);
        Assert.Equal(versaoDaDora, adiado.ResyncStateVersion);
        Assert.Equal(s.Ana.Id, adiado.AssignedUserId); // nada foi projetado ainda

        // Entrega repetida do relay: o inbox já tem os dois eventos, nada muda.
        var versaoDoItem = adiado.Version;
        await s.C.RodarRelayAsync();
        Assert.Equal(versaoDoItem, (await LinhaAsync(s)).Version);

        // A reconciliação conclui a transferência (em parte) com a Dora. Repetir a reconciliação, e entregar de novo, não muda mais nada.
        await LiberarAsync(s);
        Assert.Equal(1, await s.C.ReconciliarAsync());
        await AfirmarConvergidoAsync(s, s.Dora.Id);
        var convergido = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal(0, await s.C.ReconciliarAsync());
        await s.C.RodarRelayAsync();
        Assert.Equal(convergido, await s.C.ItemAsync(s.Item.Id));
        Assert.Equal(1, (await s.C.HistoricoDoItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!.Count(h => h.Kind == HistoryKind.TransferredQueue));
        Assert.Equal(1, await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().History.CountAsync(h => h.ItemId == s.Item.Id && h.ToUserId == s.Dora.Id)));
    }

    // O evento adiado e as demais conclusões

    [Fact]
    public async Task Evento_mais_novo_chegando_enquanto_a_operacao_confirma_no_Conversations_e_aplicado_pela_reconciliacao_depois_da_conclusao()
    {
        var s = await MontarAsync("conv3");
        var pedido = Guid.NewGuid();

        // O supervisor passa a conversa da Ana para a Carla. O Conversations CONFIRMA; ANTES de o executor gravar a conclusão, a Dora é atribuída
        // por fora e o evento dela chega com a operação ainda pendente.
        var conflito = await Assert.ThrowsAsync<ContactCenterConflictException>(() => s.C.NaExecucaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.TransitionAsync), async () =>
            {
                await MudarPorForaAsync(s, s.Dora.Id);
                await s.C.RodarRelayAsync();
            }),
            svc => svc.AssignAsync(s.Item.Id, s.Beto.Id, pedido)));
        Assert.NotNull(conflito);
        var pendente = await LinhaAsync(s);
        Assert.True(pendente.HasPending);
        Assert.NotNull(pendente.ResyncStateVersion);

        // A reconciliação conclui a operação (a transição já estava aplicada, e é reconhecida) e, na mesma rodada, alinha o item ao que veio depois.
        await LiberarAsync(s);
        Assert.Equal(2, await s.C.ReconciliarAsync());
        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, s.Dora.Id, null), (item.Status, item.AssignedUserId, item.Pending));
        Assert.Equal(s.Dora.Id, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef);
        Assert.Equal(0, await s.C.ReconciliarAsync());
    }

    [Fact]
    public async Task Evento_adiado_durante_uma_operacao_desfeita_por_mudanca_de_responsavel_alinha_o_item_ao_Conversations()
    {
        var s = await MontarAsync("conv4");

        // A Ana devolve a conversa à fila. Entre a cerca e a transição, a Carla e depois a Dora assumem, e os eventos chegam com a operação pendente.
        var conflito = await Assert.ThrowsAsync<ContactCenterConflictException>(() => s.C.NaExecucaoAsync(
            s.Ana.Contexto,
            g => g.Depois(nameof(IConversationGateway.FenceAsync), async () =>
            {
                await MudarPorForaAsync(s, s.Carla.Id);
                await MudarPorForaAsync(s, s.Dora.Id);
                await s.C.RodarRelayAsync();
            }),
            svc => svc.ReleaseAsync(s.Item.Id, Guid.NewGuid())));
        Assert.NotNull(conflito);

        // A reconciliação desfaz a operação (a condição não vale: a conversa já é da Dora) e o item deixa de mostrar a Ana: espelha a Dora.
        await LiberarAsync(s);
        Assert.Equal(1, await s.C.ReconciliarAsync());
        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, s.Dora.Id, null), (item.Status, item.AssignedUserId, item.Pending));
        Assert.Equal(s.Dora.Id, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef);
    }

    [Fact]
    public async Task O_encerramento_da_sessao_com_a_transferencia_pendente_e_a_unidade_aplicada_passa_pelo_executor_e_deixa_a_auditoria_do_efeito()
    {
        var s = await MontarAsync("conv8");
        await UnidadeAplicadaRespostaPerdidaAsync(s, Guid.NewGuid());

        // O evento de encerramento chega (pelo relay, e não pela reconciliação) com a transferência pendente: quem decide é o executor, que conhece o
        // que o Conversations já confirmou.
        await s.C.EncerrarSessaoAsync(s.Sessao);
        await s.C.RodarRelayAsync();

        var item = (await s.C.ItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!;
        Assert.Equal((s.FilaB.Id, s.Filial, QueueItemStatus.Closed, null), (item.QueueId, item.BusinessUnitId, item.Status, item.Pending));
        var transferencia = Assert.Single(await s.C.AuditoriaAsync("contact-center.item.transfer", s.Item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
        Assert.Equal(s.Ana.Id, transferencia.ActorUserId);
        Assert.Single(await s.C.AuditoriaAsync("contact-center.item.session-closed", s.Item.Id.ToString()));

        // Entregar de novo, ou reconciliar, não duplica o histórico nem a trilha.
        await s.C.RodarRelayAsync();
        Assert.Equal(0, await s.C.ReconciliarAsync());
        Assert.Single(await s.C.AuditoriaAsync("contact-center.item.transfer", s.Item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
        Assert.Single(await s.C.AuditoriaAsync("contact-center.item.session-closed", s.Item.Id.ToString()));
        Assert.Equal(1, (await s.C.HistoricoDoItemAsync(s.Item.Id, s.SupervisorDeB.Contexto))!.Count(h => h.Kind == HistoryKind.TransferredQueue));
    }

    [Fact]
    public async Task O_Worker_enxerga_o_tenant_com_item_atrasado_so_alinha_ao_que_a_sessao_ja_mostra_e_para_de_enxergar_depois()
    {
        var s = await MontarAsync("conv10");
        var tenant = s.C.Tenant.TenantId.Value;
        var depois = DateTimeOffset.UtcNow.AddDays(1);
        Task<IReadOnlyList<Guid>> Tenants() => s.C.ComoAsync(
            s.C.ContextoDeSistema, sp => sp.GetRequiredService<IPendingWorkSource>().ListTenantsWithPendingAsync(depois, depois, 1000, default)); // o banco é compartilhado: o lote não pode ser o limitante
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal(sessao.StateVersion, (await LinhaAsync(s)).ObservedStateVersion);
        Assert.DoesNotContain(tenant, await Tenants()); // nada pendente, nada atrasado

        // Uma marca que a sessão ainda NÃO mostra (a autoridade nunca fica atrás do que ela mesma publicou): o item é visto como atrasado, mas
        // não há a que se alinhar, e nada muda.
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database
            .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET resync_state_version = observed_state_version + 5 WHERE id = {s.Item.Id}"));
        Assert.Contains(tenant, await Tenants());
        var antes = await LinhaAsync(s);
        Assert.Equal(0, await s.C.ReconciliarAsync());
        Assert.Equal((antes.Version, antes.ResyncStateVersion), ((await LinhaAsync(s)).Version, (await LinhaAsync(s)).ResyncStateVersion));

        // O item que ficou uma versão atrás do que a sessão mostra é alinhado: o responsável é o mesmo, então só a versão observada avança.
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database
            .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET resync_state_version = observed_state_version, observed_state_version = observed_state_version - 1 WHERE id = {s.Item.Id}"));
        Assert.Contains(tenant, await Tenants());
        Assert.Equal(1, await s.C.ReconciliarAsync());
        var alinhado = await LinhaAsync(s);
        Assert.Equal((sessao.StateVersion, null, s.Ana.Id), (alinhado.ObservedStateVersion, alinhado.ResyncStateVersion, alinhado.AssignedUserId));
        Assert.DoesNotContain(tenant, await Tenants());
        Assert.Equal(0, await s.C.ReconciliarAsync());
    }

    // Diferenciar os eventos: obsoleto, repetido, da própria operação e externo mais novo

    private static HandlingModeChangedV1 Evento(Cena s, Guid responsavel, long versao) =>
        new(s.Sessao, HandlingMode.HumanAssisted, HandlingMode.HumanAssisted, responsavel, versao, Guid.NewGuid(), 1, HandlingPeriod: 1, PlacementEpoch: 1); // do atendimento (período 1) que o item representa

    private static Task<ProjectionOutcome> ProjetarAsync(Cena s, HandlingModeChangedV1 evento) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ConversationProjectionService>().ApplyHandlingModeChangedAsync(evento, Guid.NewGuid()));

    [Fact]
    public async Task O_evento_e_classificado_obsoleto_repetido_adiado_ou_aplicado_e_o_adiado_so_grava_uma_vez()
    {
        var s = await MontarAsync("conv5");
        var observada = (await LinhaAsync(s)).ObservedStateVersion;

        // Sem operação pendente: o evento obsoleto (versão igual ou menor) é ignorado; o mais novo é aplicado.
        Assert.Equal(ProjectionOutcome.Ignored, await ProjetarAsync(s, Evento(s, s.Ana.Id, observada)));
        Assert.Equal(ProjectionOutcome.Applied, await ProjetarAsync(s, Evento(s, s.Ana.Id, observada + 3)));
        Assert.Equal(s.Ana.Id, (await LinhaAsync(s)).AssignedUserId);

        // Com operação pendente: o obsoleto é ignorado, o externo mais novo é ADIADO, e o repetido (mesma versão) não grava de novo.
        await UnidadeAplicadaRespostaPerdidaAsync(s, Guid.NewGuid());
        var antes = await LinhaAsync(s);
        Assert.Equal(ProjectionOutcome.Ignored, await ProjetarAsync(s, Evento(s, s.Dora.Id, antes.ObservedStateVersion)));
        var novo = Evento(s, s.Dora.Id, antes.ObservedStateVersion + 5);
        Assert.Equal(ProjectionOutcome.Deferred, await ProjetarAsync(s, novo));
        var adiado = await LinhaAsync(s);
        Assert.Equal((antes.ObservedStateVersion + 5, antes.Version + 1, true), (adiado.ResyncStateVersion, adiado.Version, adiado.HasPending));
        Assert.Equal(ProjectionOutcome.Ignored, await ProjetarAsync(s, novo with { TransitionId = Guid.NewGuid() }));
        Assert.Equal(ProjectionOutcome.Ignored, await ProjetarAsync(s, Evento(s, s.Dora.Id, antes.ObservedStateVersion + 4))); // mais velho que a marca: já coberto
        Assert.Equal(adiado.Version, (await LinhaAsync(s)).Version);

        // Uma marca mais nova avança a anterior; nenhuma delas é um estado projetado.
        Assert.Equal(ProjectionOutcome.Deferred, await ProjetarAsync(s, Evento(s, s.Dora.Id, antes.ObservedStateVersion + 7)));
        Assert.Equal(antes.ObservedStateVersion + 7, (await LinhaAsync(s)).ResyncStateVersion);
        Assert.Equal(s.Ana.Id, (await LinhaAsync(s)).AssignedUserId);
    }

    [Fact]
    public async Task O_evento_da_propria_transicao_pendente_continua_concluindo_a_operacao_sem_marca()
    {
        var s = await MontarAsync("conv6");

        // O Conversations CONFIRMA a atribuição à Carla e a resposta se perde; o evento da PRÓPRIA transição chega e conclui a operação.
        var interrompida = await s.C.NaExecucaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.AssignAsync(s.Item.Id, s.Beto.Id, Guid.NewGuid()));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);

        await s.C.RodarRelayAsync();

        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, s.Beto.Id, null), (item.Status, item.AssignedUserId, item.Pending));
        Assert.Null((await LinhaAsync(s)).ResyncStateVersion);
    }

    [Fact]
    public async Task O_evento_de_uma_sessao_de_outro_tenant_nao_toca_o_item_deste()
    {
        var s = await MontarAsync("conv7");
        var outro = await MontarAsync("conv7outro");

        // O evento de uma sessão do OUTRO tenant não toca o item deste (a projeção resolve o item pelo tenant do contexto de sistema).
        var evento = new HandlingModeChangedV1(outro.Sessao, HandlingMode.HumanAssisted, HandlingMode.HumanAssisted, s.Dora.Id, 99, Guid.NewGuid(), 1);
        Assert.Equal(
            ProjectionOutcome.Ignored,
            await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ConversationProjectionService>().ApplyHandlingModeChangedAsync(evento, Guid.NewGuid())));
        Assert.Equal(s.Ana.Id, (await LinhaAsync(s)).AssignedUserId);
        Assert.Null((await LinhaAsync(s)).ResyncStateVersion);
        Assert.Equal(outro.Ana.Id, (await outro.C.ItemAsync(outro.Item.Id))!.AssignedUserId);
    }
}
