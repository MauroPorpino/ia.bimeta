using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

/// <summary>
/// Testes de unidade da regra do caso de uso (o tenant vem do contexto e a falta de contexto falha fechada).
/// O isolamento persistente é provado com PostgreSQL real em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class OrganizationServiceTests
{
    private sealed class RepositorioEmMemoria : IOrganizationRepository
    {
        public List<Organization> Adicionadas { get; } = new();

        public int Chamadas { get; private set; }

        public Task AddAsync(Organization organization, CancellationToken cancellationToken)
        {
            Chamadas++;
            Adicionadas.Add(organization);
            return Task.CompletedTask;
        }

        public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult(Adicionadas.FirstOrDefault(o => o.Id == id));
        }

        public Task<IReadOnlyList<Organization>> ListAsync(CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult<IReadOnlyList<Organization>>(Adicionadas.ToList());
        }
    }

    private static (OrganizationService Servico, RepositorioEmMemoria Repositorio, RequestContextHolder Holder) Criar()
    {
        var repositorio = new RepositorioEmMemoria();
        var holder = new RequestContextHolder();
        return (new OrganizationService(repositorio, holder, TimeProvider.System, new AuditoriaEmMemoria()), repositorio, holder);
    }

    [Fact]
    public async Task Sem_contexto_criar_falha_fechada_sem_tocar_o_repositorio()
    {
        var (servico, repositorio, _) = Criar();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.CreateAsync("Matriz"));

        Assert.Equal(0, repositorio.Chamadas);
    }

    [Fact]
    public async Task Sem_contexto_obter_e_listar_falham_fechados_sem_tocar_o_repositorio()
    {
        var (servico, repositorio, _) = Criar();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.ListAsync());

        Assert.Equal(0, repositorio.Chamadas);
    }

    [Fact]
    public async Task Criar_usa_o_tenant_do_contexto()
    {
        var (servico, repositorio, holder) = Criar();
        var tenant = new TenantId(Guid.NewGuid());
        holder.Set(new RequestContext(tenant, "teste"));

        var visao = await servico.CreateAsync("Matriz");

        var guardada = Assert.Single(repositorio.Adicionadas);
        Assert.Equal(tenant, guardada.TenantId);
        Assert.Equal(visao.Id, guardada.Id);
        Assert.Equal("Matriz", visao.Name);
    }

    [Fact]
    public async Task Criar_com_nome_invalido_nao_chega_ao_repositorio()
    {
        var (servico, repositorio, holder) = Criar();
        holder.Set(new RequestContext(new TenantId(Guid.NewGuid()), "teste"));

        await Assert.ThrowsAsync<TenancyValidationException>(() => servico.CreateAsync("   "));

        Assert.Equal(0, repositorio.Chamadas);
    }

    [Fact]
    public async Task Obter_inexistente_devolve_nulo()
    {
        var (servico, _, holder) = Criar();
        holder.Set(new RequestContext(new TenantId(Guid.NewGuid()), "teste"));

        Assert.Null(await servico.GetAsync(Guid.NewGuid()));
    }
}
