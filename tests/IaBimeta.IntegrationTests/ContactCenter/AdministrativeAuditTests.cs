using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A auditoria das mudanças administrativas (filas, membros, regras, políticas e horários) e das negações que o contrato manda auditar é
/// guardada na MESMA transação da mudança, e entregue depois com o ator, a origem e o instante ORIGINAIS: uma queda depois do commit e
/// antes da entrega, ou a trilha fora do ar, não a perde (e repetir a operação, que já não muda nada, não a refaz). PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdministrativeAuditTests(PostgresFixture banco)
{
    private const string OrigemDoGestor = "session";
    private const string OrigemDoJob = "worker-reconciler";

    private static IDeferredAudit SemEntrega(IDeferredAudit real)
    {
        var (porta, controle) = Interceptador<IDeferredAudit>.Envolver(real);
        controle.Substituir(nameof(IDeferredAudit.FlushAsync), _ => Task.FromResult(0)); // o processo "morre" depois de confirmar e antes de entregar
        return porta;
    }

    private static DeferredAudit ComTrilhaForaDoAr(IServiceProvider sp)
    {
        var (escritor, controle) = Interceptador<IAuditRecoveryWriter>.Envolver(sp.GetRequiredService<IAuditRecoveryWriter>());
        controle.Substituir(nameof(IAuditRecoveryWriter.WriteRecoveredAsync), _ => Task.FromException(new InvalidOperationException("trilha fora do ar")));
        return new DeferredAudit(sp.GetRequiredService<ContactCenterDbContext>(), sp.GetRequiredService<IRequestContextAccessor>(), escritor, sp.GetRequiredService<TimeProvider>());
    }

    private static Task<int> PendentesAsync(CenarioDeConversas c) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IDeferredAudit>().PendingCountAsync(default));

    private static Task<int> EntregarComoJobAsync(CenarioDeConversas c) =>
        c.ComoAsync(new RequestContext(c.Tenant.TenantId, OrigemDoJob), sp => sp.GetRequiredService<IDeferredAudit>().FlushAsync(100, default));

    /// <summary>Quem gere as filas da organização, com a origem de uma requisição de usuário (diferente da do job que entrega depois).</summary>
    private static (Guid Id, RequestContext Contexto) Gestor(CenarioDeConversas c, params Permission[] permissoes)
    {
        var usuario = Guid.NewGuid();
        var concedidas = (permissoes.Length == 0 ? new[] { Permission.OrganizationRead, Permission.QueueRead, Permission.QueueManage } : permissoes)
            .Select(p => new PermissionGrant(p, c.OrganizacaoId)).ToArray();
        return (usuario, new RequestContext(c.Tenant.TenantId, OrigemDoGestor, new ActorPrincipal(usuario, Guid.NewGuid(), concedidas)));
    }

    private static async Task<T> ComAuditoriaSemEntregaAsync<T>(CenarioDeConversas c, RequestContext contexto, Func<IServiceProvider, IDeferredAudit, Task<T>> acao) =>
        await c.ComoAsync(contexto, sp => acao(sp, SemEntrega(sp.GetRequiredService<IDeferredAudit>())));

    [Fact]
    public async Task A_mudanca_administrativa_confirmada_e_a_auditoria_guardada_juntas_a_queda_antes_da_entrega_nao_perde_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "admaud");
        var (gestorId, gestor) = Gestor(c);
        var inicio = DateTimeOffset.UtcNow;

        // Uma sequência que cobre as filas, os membros, as regras, a política e o horário; o processo "morre" depois de cada confirmação.
        var fila = await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, a).CreateAsync(c.OrganizacaoId, null, null, ContactCenterTestSupport.Unico("fila")));
        var outra = await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, a).CreateAsync(c.OrganizacaoId, null, null, ContactCenterTestSupport.Unico("fila")));
        var atendente = await c.NovoAgenteAsync(banco);
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, a).PauseAsync(fila.Id));
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, a).AddMemberAsync(fila.Id, atendente.Id, QueueMemberRole.Agent));
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, a).AddMemberAsync(fila.Id, atendente.Id, QueueMemberRole.Supervisor));
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, a).RemoveMemberAsync(fila.Id, atendente.Id));
        var regra = await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<RoutingRuleService>(sp, a).CreateAsync(c.OrganizacaoId, ContactCenterTestSupport.Unico("regra"), null, null, null, fila.Id, 0, 0));
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<RoutingRuleService>(sp, a).DisableAsync(regra.Id));
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<RoutingRuleService>(sp, a).DeleteAsync(regra.Id));
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<QueuePolicyService>(sp, a).ConfigureAsync(fila.Id, outra.Id, OutOfHoursBehavior.Accept, NoAgentBehavior.Wait, 3, 5));
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<QueuePolicyService>(sp, a).SetScheduleAsync(fila.Id, "America/Sao_Paulo", HandoffTestSupport.SempreAberto, Array.Empty<DateOnly>()));
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<QueuePolicyService>(sp, a).ClearScheduleAsync(fila.Id));
        var fim = DateTimeOffset.UtcNow;

        string[] esperadas =
        {
            "contact-center.queue.create", "contact-center.queue.create", "contact-center.queue.pause", "contact-center.queue.member.add", "contact-center.queue.member.role",
            "contact-center.queue.member.remove", "contact-center.routing-rule.create", "contact-center.routing-rule.disable", "contact-center.routing-rule.delete",
            "contact-center.queue.configure", "contact-center.queue.schedule.set", "contact-center.queue.schedule.clear",
        };

        // As mudanças estão confirmadas; a trilha ainda não recebeu NADA; e cada intenção de auditoria está guardada.
        Assert.Equal(3, (await c.LerFilaAsync(fila.Id))!.DefaultPriority); // a política e as demais mudanças confirmadas valem
        Assert.Equal(esperadas.Length, await PendentesAsync(c));
        foreach (var operacao in esperadas.Distinct())
        {
            Assert.Empty(await c.AuditoriaAsync(operacao));
        }

        // O job de reconciliação entrega tudo; nada se repete.
        Assert.Equal(esperadas.Length, await EntregarComoJobAsync(c));
        Assert.Equal(0, await EntregarComoJobAsync(c));
        Assert.Equal(0, await PendentesAsync(c));

        // Cada evento diz o que o GESTOR fez: o ator, a origem e a hora da operação, não os do job que entregou depois.
        var eventos = new List<AuditEventView>();
        foreach (var operacao in esperadas.Distinct())
        {
            eventos.AddRange(await c.AuditoriaAsync(operacao));
        }

        Assert.Equal(esperadas.Length, eventos.Count(e => esperadas.Contains(e.Operation)));
        Assert.All(eventos, e =>
        {
            Assert.Equal((gestorId, OrigemDoGestor, AuditOutcome.Success), (e.ActorUserId, e.ActorOrigin, e.Outcome));
            Assert.InRange(e.OccurredAt, inicio, fim);
        });
    }

    [Fact]
    public async Task Repetir_a_operacao_que_ja_nao_muda_nada_nao_produz_auditoria_e_a_que_estava_guardada_continua_valendo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "admaudrep");
        var (gestorId, gestor) = Gestor(c);
        var fila = await c.CriarFilaAsync();

        // Pausar, com o processo caindo depois de confirmar e antes de entregar; reiniciar e repetir encontra a fila já pausada (nada a mudar).
        await ComAuditoriaSemEntregaAsync(c, gestor, (sp, a) => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, a).PauseAsync(fila.Id));
        await c.ComoAsync(gestor, sp => sp.GetRequiredService<ServiceQueueService>().PauseAsync(fila.Id));

        Assert.Empty(await c.AuditoriaAsync("contact-center.queue.pause", fila.Id.ToString()));
        Assert.True(await PendentesAsync(c) >= 1);

        await EntregarComoJobAsync(c);

        // Existe UMA auditoria da pausa, com o autor dela: a repetição não a refez, e a queda não a perdeu.
        var evento = Assert.Single(await c.AuditoriaAsync("contact-center.queue.pause", fila.Id.ToString()));
        Assert.Equal((gestorId, OrigemDoGestor), (evento.ActorUserId, evento.ActorOrigin));
    }

    [Fact]
    public async Task A_trilha_fora_do_ar_nao_faz_a_mudanca_administrativa_ja_confirmada_falhar_e_a_auditoria_chega_depois()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "admaudfora");
        var (gestorId, gestor) = Gestor(c);

        var fila = await c.ComoAsync(gestor, sp => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, ComTrilhaForaDoAr(sp)).CreateAsync(c.OrganizacaoId, null, null, ContactCenterTestSupport.Unico("fila")));

        Assert.NotNull(await c.LerFilaAsync(fila.Id)); // a mudança confirmada vale
        Assert.Empty(await c.AuditoriaAsync("contact-center.queue.create", fila.Id.ToString()));
        Assert.True(await PendentesAsync(c) >= 1);

        Assert.True(await EntregarComoJobAsync(c) >= 1); // a trilha voltou
        var evento = Assert.Single(await c.AuditoriaAsync("contact-center.queue.create", fila.Id.ToString()));
        Assert.Equal((gestorId, OrigemDoGestor, AuditOutcome.Success), (evento.ActorUserId, evento.ActorOrigin, evento.Outcome));
    }

    [Fact]
    public async Task A_mudanca_recusada_por_nome_repetido_nao_deixa_auditoria_de_sucesso_guardada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "admaudnome");
        var (_, gestor) = Gestor(c);
        var nome = ContactCenterTestSupport.Unico("fila");
        await c.CriarFilaAsync(nome);
        var antes = await PendentesAsync(c);

        // O mesmo escopo (a mesma "requisição") segue depois da recusa e confirma outra coisa: o que ficou rastreado não pode ser gravado junto.
        await c.ComoAsync(gestor, async sp =>
        {
            var filas = sp.GetRequiredService<ServiceQueueService>();
            await Assert.ThrowsAsync<QueueNameInUseException>(() => filas.CreateAsync(c.OrganizacaoId, null, null, nome));
            await filas.CreateAsync(c.OrganizacaoId, null, null, ContactCenterTestSupport.Unico("outra"));
            return 0;
        });

        Assert.Equal(antes, await PendentesAsync(c)); // só a criação que valeu deixou (e já entregou) a auditoria
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.create"), e => e.ActorUserId == gestor.Principal!.UserId);
    }

    // As negações que o contrato manda auditar

    [Fact]
    public async Task A_negacao_por_permissao_com_a_trilha_fora_do_ar_continua_recusada_e_a_auditoria_da_negacao_e_recuperada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "admaudnega");
        var fila = await c.CriarFilaAsync();
        var (leitorId, leitor) = Gestor(c, Permission.OrganizationRead, Permission.QueueRead); // vê a fila, mas não a gere

        var recusa = await Assert.ThrowsAsync<AccessDeniedException>(() =>
            c.ComoAsync(leitor, sp => ActivatorUtilities.CreateInstance<ServiceQueueService>(sp, ComTrilhaForaDoAr(sp)).PauseAsync(fila.Id)));

        Assert.Equal(Permission.QueueManage, recusa.Permission); // a recusa é a mesma, e a fila não mudou
        Assert.Equal(QueueStatus.Active, (await c.LerFilaAsync(fila.Id))!.Status);
        Assert.Empty(await c.AuditoriaAsync("contact-center.queue.pause", fila.Id.ToString()));
        Assert.True(await PendentesAsync(c) >= 1); // a intenção da negação ficou guardada

        await EntregarComoJobAsync(c);

        var negacao = Assert.Single(await c.AuditoriaAsync("contact-center.queue.pause", fila.Id.ToString()));
        Assert.Equal((AuditOutcome.Denied, leitorId, OrigemDoGestor), (negacao.Outcome, negacao.ActorUserId, negacao.ActorOrigin));
    }

    [Fact]
    public async Task A_negacao_por_papel_na_fila_e_a_recusa_por_regra_tambem_sao_guardadas_com_a_trilha_fora_do_ar()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "admaudpapel");
        var fila = await c.CriarFilaAsync();
        await c.ConfigurarFilaAsync(fila.Id, limite: 1);
        var ana = await c.AgenteDisponivelAsync(banco, fila.Id);
        var forasteiro = await c.NovoAgenteAsync(banco); // não é atendente da fila
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.AssumirAsync(primeiro.Id, ana.Contexto);

        // Papel: quem não é atendente da fila não a assume (negação 403). Regra: a Ana já está no limite (recusa por regra).
        await Assert.ThrowsAnyAsync<Exception>(() => c.ComoAsync(forasteiro.Contexto, sp =>
            ActivatorUtilities.CreateInstance<QueueItemService>(sp, ComTrilhaForaDoAr(sp)).ClaimAsync(segundo.Id, Guid.NewGuid())));
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.ComoAsync(ana.Contexto, sp =>
            ActivatorUtilities.CreateInstance<QueueItemService>(sp, ComTrilhaForaDoAr(sp)).ClaimAsync(segundo.Id, Guid.NewGuid())));
        Assert.Equal(ContactCenterRule.AgentAtCapacity, recusa.Rule);
        Assert.Empty(await c.AuditoriaAsync("contact-center.item.assign", segundo.Id.ToString()));
        Assert.True(await PendentesAsync(c) >= 1);

        await EntregarComoJobAsync(c);

        var negacoes = await c.AuditoriaAsync("contact-center.item.assign", segundo.Id.ToString());
        Assert.All(negacoes, e => Assert.Equal(AuditOutcome.Denied, e.Outcome));
        Assert.Contains(negacoes, e => e.ActorUserId == ana.Id); // a recusa por regra, com o autor dela
    }
}
