using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

/// <summary>
/// Falha de gravação da auditoria nunca é tratada como sucesso: a exceção sobe até o chamador, que a converte em erro
/// (500 na API), inclusive no caminho de negação, onde o 403 não pode esconder que o evento não foi gravado.
/// Como não há transação entre módulos (COM-5), a operação pode já ter sido gravada quando a auditoria falha: o cliente
/// vê erro, nunca sucesso, e o reprocessamento é por ação consciente (o outbox da etapa 4 fecha essa lacuna).
/// </summary>
public class AuditFailureTests
{
    private sealed class AuditoriaQueFalha : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("auditoria indisponível (simulada)");

        public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("auditoria indisponível (simulada)");
    }

    private static readonly TenantId Tenant = new(Guid.NewGuid());

    private static (RequestContextHolder Holder, OrganizacoesEmMemoria Organizacoes, UnidadesEmMemoria Unidades, SetoresEmMemoria Setores) Montar()
    {
        return (new RequestContextHolder(), new OrganizacoesEmMemoria(), new UnidadesEmMemoria(), new SetoresEmMemoria());
    }

    [Fact]
    public async Task Criar_organizacao_com_auditoria_fora_do_ar_falha_em_vez_de_responder_sucesso()
    {
        var (holder, organizacoes, _, _) = Montar();
        holder.Set(Contextos.DeSistema(Tenant));
        var servico = new OrganizationService(organizacoes, holder, TimeProvider.System, new AuditoriaQueFalha());

        var falha = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.CreateAsync("Matriz"));

        Assert.Contains("auditoria", falha.Message);
    }

    [Fact]
    public async Task Negacao_com_auditoria_fora_do_ar_tambem_falha_e_nao_vira_um_acesso_negado_silencioso()
    {
        var (holder, organizacoes, _, _) = Montar();
        holder.Set(Contextos.DeUsuario(Tenant));
        var servico = new OrganizationService(organizacoes, holder, TimeProvider.System, new AuditoriaQueFalha());

        // A negação não pode esconder a falha de auditoria: o que sobe é a falha, e nada foi criado.
        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.CreateAsync("Matriz"));

        Assert.Empty(organizacoes.Itens);
    }

    [Fact]
    public async Task Criar_unidade_e_setor_com_auditoria_fora_do_ar_falham()
    {
        var (holder, organizacoes, unidades, setores) = Montar();
        holder.Set(Contextos.DeSistema(Tenant));
        var organizacao = Organization.Create(Tenant, "Matriz", DateTimeOffset.UtcNow);
        organizacoes.Itens.Add(organizacao);
        var unidade = BusinessUnit.Create(Tenant, organizacao.Id, "Centro", BusinessUnitKind.Branch, DateTimeOffset.UtcNow);
        unidades.Itens.Add(unidade);
        var auditoria = new AuditoriaQueFalha();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BusinessUnitService(unidades, organizacoes, holder, TimeProvider.System, auditoria).CreateAsync(organizacao.Id, "Outra", BusinessUnitKind.Branch));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DepartmentService(setores, unidades, holder, TimeProvider.System, auditoria).CreateAsync(unidade.Id, "Coleta"));
    }

    [Fact]
    public async Task Leituras_nao_dependem_da_auditoria_e_continuam_funcionando()
    {
        var (holder, organizacoes, _, _) = Montar();
        holder.Set(Contextos.DeSistema(Tenant));
        organizacoes.Itens.Add(Organization.Create(Tenant, "Matriz", DateTimeOffset.UtcNow));
        var servico = new OrganizationService(organizacoes, holder, TimeProvider.System, new AuditoriaQueFalha());

        Assert.Single(await servico.ListAsync());
    }
}
