using System.Reflection;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Tests;

/// <summary>
/// E2.3 (ADR 0041, D5): a leitura do <c>CustomerOnly</c> só existe quando o vínculo foi confirmado (<c>GrantedCustomerSession</c>), só para a organização da conexão e só no modo CustomerOnly. Nenhuma via de ESCRITA
/// (trabalhar, gerir, participar) se abre. O estado do acesso é função pura: aqui se prova o que decide.
/// </summary>
public sealed class CustomerOnlyAccessTests
{
    private static readonly TenantId Tenant = new TenantId(Guid.NewGuid());
    private static readonly DateTimeOffset Agora = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static RequestContext Contexto(bool? customerOnly, Guid organizacao, params PermissionGrant[] concessoes)
    {
        var principal = new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), concessoes, customerOnly is null ? null : new EmbeddedScope(organizacao, customerOnly.Value));
        var ctor = typeof(RequestContext).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(TenantId), typeof(string), typeof(ActorPrincipal) })!;
        return (RequestContext)ctor.Invoke(new object?[] { Tenant, "teste", principal });
    }

    private static ServiceQueue Fila(Guid organizacao) => ServiceQueue.Create(Tenant, organizacao, null, null, "Fila", Agora);

    private static QueueItem Item(ServiceQueue fila, Guid organizacao) =>
        QueueItem.Enter(Tenant, Guid.NewGuid(), organizacao, null, null, fila.Id, Guid.NewGuid(), null, "teste", Agora);

    [Fact]
    public void CustomerOnly_sem_a_confirmacao_do_vinculo_nao_ve_nada_e_com_ela_ve_e_le_so_na_organizacao_da_conexao()
    {
        var fila = Fila(OrgA);
        var item = Item(fila, OrgA);
        var contexto = Contexto(true, OrgA);

        var sem = new QueueAccess(contexto, fila, QueueMemberRole.Agent);
        Assert.False(sem.CanSee(item));
        Assert.False(sem.CanReadConversation(item, QueueItemStatus.Waiting, null));

        var com = sem with { GrantedCustomerSession = true };
        Assert.True(com.CanSee(item));
        Assert.True(com.CanReadConversation(item, QueueItemStatus.Assigned, Guid.NewGuid())); // em qualquer estado: é leitura do cliente autorizado

        // Outra organização: nunca, mesmo com a confirmação (o limite duro da conexão).
        var outraFila = Fila(OrgB);
        var outroItem = Item(outraFila, OrgB);
        var fora = new QueueAccess(contexto, outraFila, QueueMemberRole.Agent, GrantedCustomerSession: true);
        Assert.False(fora.CanSee(outroItem));
        Assert.False(fora.CanReadConversation(outroItem, QueueItemStatus.Waiting, null));

        // Item de OUTRA organização numa fila da organização da conexão (registro incoerente): também fechado.
        Assert.False(com.CanSee(Item(fila, OrgB)));
    }

    [Fact]
    public void A_confirmacao_nunca_abre_escrita_nem_gestao_nem_participacao()
    {
        var fila = Fila(OrgA);
        var item = Item(fila, OrgA);
        var acesso = new QueueAccess(Contexto(true, OrgA, new PermissionGrant(Permission.QueueManage, OrgA)), fila, QueueMemberRole.Supervisor, GrantedCustomerSession: true);

        Assert.False(acesso.CanWork(item));
        Assert.False(acesso.CanWork(Guid.NewGuid()));
        Assert.False(acesso.IsManager);
        Assert.False(acesso.IsSupervisor && acesso.IsManager);
        Assert.False(acesso.IsMember);
        Assert.False(acesso.IsOrganizationReader);
        Assert.True(acesso.Outside);
    }

    [Fact]
    public void A_confirmacao_e_ignorada_fora_do_modo_CustomerOnly()
    {
        var fila = Fila(OrgA);
        var item = Item(fila, OrgA);

        // FullAttendant com a bandeira ligada por engano: vale a regra de sempre (sem papel nem concessão, nada).
        Assert.False(new QueueAccess(Contexto(false, OrgA), fila, null, GrantedCustomerSession: true).CanSee(item));
        // Sessão interativa (sem escopo embutido) idem.
        Assert.False(new QueueAccess(Contexto(null, OrgA), fila, null, GrantedCustomerSession: true).CanSee(item));
        // E quem tem papel na fila continua vendo pelo papel, com ou sem a bandeira.
        Assert.True(new QueueAccess(Contexto(false, OrgA), fila, QueueMemberRole.Agent).CanSee(item));
    }
}
