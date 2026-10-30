using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>Um atendente de verdade (usuário no Identity) com o contexto que o host criaria para ele: só as permissões do papel dele.</summary>
internal sealed record Agente(Guid Id, RequestContext Contexto);

/// <summary>
/// Atalhos para a entrada de conversas nas filas, a atribuição, a transferência e o handoff. Cada chamada roda em um escopo próprio
/// (uma "requisição") com o contexto informado e PostgreSQL real. Sem contexto informado, vale o de sistema do tenant.
/// </summary>
internal static class HandoffTestSupport
{
    public static async Task<Agente> NovoAgenteAsync(this CenarioDeConversas c, PostgresFixture banco, Guid? organizacao = null, Role papel = Role.User)
    {
        var org = organizacao ?? c.OrganizacaoId;
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, org, papel);
        return new Agente(usuario.Id, PostgresFixture.ContextoDeUsuario(c.Tenant, usuario.Id, RolePermissions.Expand(papel, org).ToArray()));
    }

    /// <summary>Um agente já cadastrado na fila com o papel informado.</summary>
    public static async Task<Agente> AgenteNaFilaAsync(
        this CenarioDeConversas c, PostgresFixture banco, Guid fila, QueueMemberRole papel = QueueMemberRole.Agent, Guid? organizacaoDoUsuario = null)
    {
        var agente = await c.NovoAgenteAsync(banco, organizacaoDoUsuario);
        await c.AdicionarAtendenteAsync(fila, agente.Id, papel);
        return agente;
    }

    public static async Task<Guid> NovaConversaAsync(
        this CenarioDeConversas c, Guid? unidade = null, Guid? setor = null, ChannelInstanceId? canal = null, HandlingMode modoInicial = HandlingMode.AiAutonomous) =>
        (await c.EntrarAsync(ContactCenterTestSupport.Unico("contato"), unidade: unidade, setor: setor, canal: canal, modoInicial: modoInicial)).SessionId;

    private static Task<T> NoItem<T>(this CenarioDeConversas c, RequestContext? contexto, Func<QueueItemService, Task<T>> acao) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => acao(sp.GetRequiredService<QueueItemService>()));

    public static Task<QueueItemOperationResult> PedirAtendimentoAsync(
        this CenarioDeConversas c, Guid sessao, Guid fila, Guid? pedido = null, string motivo = "ai-requested", RequestContext? contexto = null) =>
        c.NoItem(contexto, s => s.RequestHandoffAsync(sessao, fila, pedido ?? Guid.NewGuid(), motivo));

    /// <summary>O pedido de atendimento SEM fila: o roteamento decide pelas regras da organização.</summary>
    public static Task<QueueItemOperationResult> PedirPorRotaAsync(
        this CenarioDeConversas c, Guid sessao, Guid? pedido = null, string motivo = "ai-requested", RequestContext? contexto = null) =>
        c.NoItem(contexto, s => s.RequestHandoffAsync(sessao, null, pedido ?? Guid.NewGuid(), motivo));

    // Roteamento, horário, política e presença

    private static Task<T> Em<T>(this CenarioDeConversas c, RequestContext? contexto, Func<IServiceProvider, Task<T>> acao) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, acao);

    public static Task<RoutingRuleView> CriarRegraAsync(
        this CenarioDeConversas c, Guid fila, string? nome = null, Guid? canal = null, Guid? unidade = null, Guid? setor = null, int prioridade = 0, int ordem = 0,
        RequestContext? contexto = null, Guid? organizacao = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<RoutingRuleService>().CreateAsync(
            organizacao ?? c.OrganizacaoId, nome ?? ContactCenterTestSupport.Unico("regra"), canal, unidade, setor, fila, prioridade, ordem));

    public static Task<RoutingDecisionView> SimularAsync(this CenarioDeConversas c, Guid sessao, RequestContext? contexto = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<RoutingRuleService>().PreviewAsync(sessao));

    public static Task<ServiceQueueView> ConfigurarFilaAsync(
        this CenarioDeConversas c, Guid fila, Guid? contingencia = null, OutOfHoursBehavior foraDoHorario = OutOfHoursBehavior.Accept,
        NoAgentBehavior semAtendente = NoAgentBehavior.Wait, int prioridade = 0, int? limite = null, RequestContext? contexto = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<QueuePolicyService>().ConfigureAsync(fila, contingencia, foraDoHorario, semAtendente, prioridade, limite));

    public static Task<QueueScheduleView> DefinirHorarioAsync(
        this CenarioDeConversas c, Guid fila, string fuso, IReadOnlyList<ScheduleWindowInput> janelas, IReadOnlyList<DateOnly>? fechados = null, RequestContext? contexto = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<QueuePolicyService>().SetScheduleAsync(fila, fuso, janelas, fechados ?? Array.Empty<DateOnly>()));

    /// <summary>Faixas que cobrem os sete dias, o dia inteiro: a fila está aberta em qualquer instante.</summary>
    public static IReadOnlyList<ScheduleWindowInput> SempreAberto { get; } =
        Enum.GetValues<DayOfWeek>().Select(d => new ScheduleWindowInput(d, "00:00", "24:00")).ToList();

    /// <summary>Faixas que só cobrem um dia da semana que NÃO é hoje (em UTC): a fila está fechada em qualquer instante de hoje.</summary>
    public static IReadOnlyList<ScheduleWindowInput> FechadoHoje() =>
        new[] { new ScheduleWindowInput((DayOfWeek)(((int)DateTime.UtcNow.DayOfWeek + 1) % 7), "00:00", "24:00") };

    public static Task<QueueScheduleView> LimparHorarioAsync(this CenarioDeConversas c, Guid fila, RequestContext? contexto = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<QueuePolicyService>().ClearScheduleAsync(fila));

    public static Task<QueueScheduleView?> HorarioAsync(this CenarioDeConversas c, Guid fila, RequestContext? contexto = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<QueuePolicyService>().GetScheduleAsync(fila));

    public static Task<QueueAvailabilityView?> DisponibilidadeAsync(this CenarioDeConversas c, Guid fila, RequestContext? contexto = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<QueuePolicyService>().GetAvailabilityAsync(fila));

    public static Task<AgentPresenceView> PresencaAsync(this CenarioDeConversas c, RequestContext contexto, AgentStatus situacao) =>
        c.Em(contexto, sp => sp.GetRequiredService<AgentPresenceService>().SetMineAsync(situacao));

    /// <summary>Um atendente na fila que já informou presença "disponível".</summary>
    public static async Task<Agente> AgenteDisponivelAsync(this CenarioDeConversas c, PostgresFixture banco, Guid fila, QueueMemberRole papel = QueueMemberRole.Agent)
    {
        var agente = await c.AgenteNaFilaAsync(banco, fila, papel);
        await c.PresencaAsync(agente.Contexto, AgentStatus.Available);
        return agente;
    }

    public static Task<QueueItemOperationResult?> AssumirProximaAsync(this CenarioDeConversas c, Guid fila, RequestContext contexto, Guid? pedido = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<QueueItemService>().ClaimNextAsync(fila, pedido ?? Guid.NewGuid()));

    public static Task<QueueItemOperationResult> PrioridadeAsync(this CenarioDeConversas c, Guid item, int prioridade, RequestContext contexto, Guid? pedido = null) =>
        c.Em(contexto, sp => sp.GetRequiredService<QueueItemService>().SetPriorityAsync(item, prioridade, pedido ?? Guid.NewGuid()));

    /// <summary>Coloca a conversa na fila e devolve o item em espera (a entrada concluída).</summary>
    public static async Task<QueueItemView> NaFilaAsync(this CenarioDeConversas c, Guid sessao, Guid fila)
    {
        var resultado = await c.PedirAtendimentoAsync(sessao, fila);
        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        return resultado.Item;
    }

    public static Task<QueueItemOperationResult> AssumirAsync(this CenarioDeConversas c, Guid item, RequestContext contexto, Guid? pedido = null) =>
        c.NoItem(contexto, s => s.ClaimAsync(item, pedido ?? Guid.NewGuid()));

    public static Task<QueueItemOperationResult> AtribuirAsync(this CenarioDeConversas c, Guid item, Guid usuario, RequestContext contexto, Guid? pedido = null) =>
        c.NoItem(contexto, s => s.AssignAsync(item, usuario, pedido ?? Guid.NewGuid()));

    public static Task<QueueItemOperationResult> DevolverAFilaAsync(this CenarioDeConversas c, Guid item, RequestContext contexto, Guid? pedido = null) =>
        c.NoItem(contexto, s => s.ReleaseAsync(item, pedido ?? Guid.NewGuid()));

    public static Task<QueueItemOperationResult> TransferirParaFilaAsync(
        this CenarioDeConversas c, Guid item, Guid filaDestino, Guid? usuarioDestino, RequestContext contexto, Guid? pedido = null) =>
        c.NoItem(contexto, s => s.TransferAsync(item, filaDestino, usuarioDestino, pedido ?? Guid.NewGuid()));

    public static Task<QueueItemOperationResult> DevolverAIAAsync(this CenarioDeConversas c, Guid item, RequestContext contexto, Guid? pedido = null) =>
        c.NoItem(contexto, s => s.ReturnToAiAsync(item, pedido ?? Guid.NewGuid()));

    public static Task<QueueItemView?> ItemAsync(this CenarioDeConversas c, Guid item, RequestContext? contexto = null) =>
        c.NoItem(contexto, s => s.GetAsync(item));

    public static Task<IReadOnlyList<QueueItemView>> ItensAsync(
        this CenarioDeConversas c, RequestContext? contexto = null, Guid? fila = null, QueueItemStatus? situacao = null, bool minhas = false, int take = 50) =>
        c.NoItem(contexto, s => s.ListAsync(fila, situacao, minhas, take));

    public static Task<IReadOnlyList<QueueItemHistoryView>?> HistoricoDoItemAsync(this CenarioDeConversas c, Guid item, RequestContext? contexto = null) =>
        c.NoItem(contexto, s => s.GetHistoryAsync(item));

    public static Task<SessionView?> ConversaDoItemAsync(this CenarioDeConversas c, Guid item, RequestContext? contexto = null) =>
        c.NoItem(contexto, s => s.GetConversationAsync(item));

    public static Task<Page<MessageView>?> MensagensDoItemAsync(this CenarioDeConversas c, Guid item, RequestContext? contexto = null) =>
        c.NoItem(contexto, s => s.GetMessagesAsync(item, 50, null, HistoryOrder.OldestFirst));

    public static Task<int> ReconciliarAsync(this CenarioDeConversas c, RequestContext? contexto = null, TimeSpan? idadeMinima = null, int lote = 50) =>
        c.NoItem(contexto, s => s.ReconcilePendingAsync(idadeMinima ?? TimeSpan.Zero, lote));

    /// <summary>Grava só a INTENÇÃO de assumir (como uma requisição que caiu antes de chamar o Conversations): o item fica pendente.</summary>
    public static Task<Guid> GravarIntencaoDeAssumirAsync(this CenarioDeConversas c, Guid item, Guid usuario) =>
        c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var rastreado = await itens.FindItemAsync(item, default) ?? throw new InvalidOperationException("O item não existe.");
            var operacao = Guid.NewGuid();
            var agora = DateTimeOffset.UtcNow;

            // A intenção nasce com a base da autorização, como no QueueItemService: o estado do atendimento no Conversations naquele instante.
            var sessao = await sp.GetRequiredService<IConversationGateway>().GetSessionAsync(rastreado.SessionId, default);
            rastreado.BeginAssign(
                usuario, operacao, usuario, "claim", agora,
                basis: sessao is { Status: SessionStatus.Open } ? new HandlingBasis(sessao.HandlingPeriod, sessao.ResponderRef, sessao.HandlingMode != HandlingMode.HumanAssisted, sessao.PlacementEpoch) : null);

            // O recibo da operação nasce com a intenção, como no QueueItemService.
            itens.AddOperation(QueueOperation.Begin(rastreado.TenantId, operacao, OperationKind.Claim, rastreado.Id, rastreado.SessionId, usuario, rastreado.QueueId, usuario, agora));
            await itens.SaveChangesAsync(default);
            return operacao;
        });

    /// <summary>
    /// A base da autorização (o período e o responsável efetivos da sessão agora) para os testes que gravam a intenção direto no item, como o
    /// <see cref="QueueItemService"/> faria: sem ela a operação não é executada.
    /// </summary>
    public static async Task<HandlingBasis?> BaseDaSessaoAsync(this CenarioDeConversas c, Guid sessao) =>
        await c.SessaoAsync(sessao) is { Status: SessionStatus.Open } s ? new HandlingBasis(s.HandlingPeriod, s.ResponderRef, s.HandlingMode != HandlingMode.HumanAssisted, s.PlacementEpoch) : null;

    /// <summary>A versão atual do estado da sessão, para os testes que mexem no Conversations por fora do ContactCenter.</summary>
    public static async Task<long> VersaoDaSessaoAsync(this CenarioDeConversas c, Guid sessao) => (await c.SessaoAsync(sessao))!.StateVersion;

    /// <summary>Roda a operação com uma porta do Conversations (a do SERVIÇO, que autoriza) em que o teste intercala outra operação.</summary>
    public static Task<T> NaAutorizacaoAsync<T>(
        this CenarioDeConversas c, RequestContext ator, Action<Interceptador<IConversationGateway>> ponto, Func<QueueItemService, Task<T>> operacao) =>
        c.ComoAsync(ator, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            ponto(controle);
            return await operacao(ActivatorUtilities.CreateInstance<QueueItemService>(sp, gateway));
        });

    /// <summary>Roda a operação com uma porta do Conversations (a do EXECUTOR, que escreve) em que o teste intercala outra operação.</summary>
    public static Task<T> NaExecucaoAsync<T>(
        this CenarioDeConversas c, RequestContext ator, Action<Interceptador<IConversationGateway>> ponto, Func<QueueItemService, Task<T>> operacao) =>
        c.ComoAsync(ator, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            ponto(controle);
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await operacao(ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor));
        });

    /// <summary>
    /// Resolve, SÓ no tenant do cenário, o que o teste deixou pendente de propósito (a intenção inerte e a obrigação de ressincronização aberta não terminam
    /// sozinhas). O banco de teste é compartilhado e persiste entre execuções: sem isto o trabalho elegível se acumula e disputa o lote de tenants do Worker.
    /// </summary>
    public static Task LimparPendenciasAsync(this CenarioDeConversas c)
    {
        var tenant = c.Tenant.TenantId.Value;
        return c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>();
            foreach (var item in await db.Items.Where(i => i.PendingKindValue != null).ToListAsync())
            {
                item.CancelPending("operation-rejected", DateTimeOffset.UtcNow);
            }

            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE contact_center.queue_items SET resync_state_version = NULL WHERE tenant_id = {tenant} AND resync_state_version IS NOT NULL");
            return 0;
        });
    }

    /// <summary>Tira o recuo da operação pendente do item (o instante em que o job a retomaria), para o teste reconciliar na hora.</summary>
    public static Task LiberarRecuoAsync(this CenarioDeConversas c, Guid item) =>
        c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>().Database
                .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET pending_next_attempt_at = NULL WHERE id = {item}");
            return 0;
        });
}
