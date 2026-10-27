using IaBimeta.Audit.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

/// <summary>
/// Autorização por recurso nos casos de uso de organização, unidade e setor (TEN-2): nega por padrão, o escopo vem do
/// recurso carregado, sem leitura o recurso "não existe" e a negação é auditada. O isolamento persistente é provado
/// com PostgreSQL real em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class StructureAuthorizationTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());

    private sealed class Cenario
    {
        public OrganizacoesEmMemoria Organizacoes { get; } = new();
        public UnidadesEmMemoria Unidades { get; } = new();
        public SetoresEmMemoria Setores { get; } = new();
        public AuditoriaEmMemoria Auditoria { get; } = new();
        public RequestContextHolder Holder { get; } = new();

        public OrganizationService Organizacao => new(Organizacoes, Holder, TimeProvider.System, Auditoria);
        public BusinessUnitService Unidade => new(Unidades, Organizacoes, Holder, TimeProvider.System, Auditoria);
        public DepartmentService Setor => new(Setores, Unidades, Holder, TimeProvider.System, Auditoria);

        public Organization NovaOrganizacao(string nome)
        {
            var organizacao = Organization.Create(Tenant, nome, DateTimeOffset.UtcNow);
            Organizacoes.Itens.Add(organizacao);
            return organizacao;
        }

        public BusinessUnit NovaUnidade(Organization organizacao, string nome, BusinessUnitKind tipo = BusinessUnitKind.Branch)
        {
            var unidade = BusinessUnit.Create(Tenant, organizacao.Id, nome, tipo, DateTimeOffset.UtcNow);
            Unidades.Itens.Add(unidade);
            return unidade;
        }

        public void Como(RequestContext contexto) => Holder.Set(contexto);
    }

    private static PermissionGrant[] Papel(Guid? organizacao, params Permission[] permissoes) =>
        permissoes.Select(p => new PermissionGrant(p, organizacao)).ToArray();

    private static readonly Permission[] LeituraEstrutura = { Permission.OrganizationRead, Permission.StructureRead };
    private static readonly Permission[] GestaoEstrutura = { Permission.OrganizationRead, Permission.StructureRead, Permission.StructureManage };

    // Organizações

    [Fact]
    public async Task Usuario_sem_concessao_nao_cria_organizacao_e_a_negacao_e_auditada()
    {
        var c = new Cenario();
        c.Como(Contextos.DeUsuario(Tenant));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Organizacao.CreateAsync("Nova"));

        Assert.Empty(c.Organizacoes.Itens);
        var negacao = Assert.Single(c.Auditoria.Eventos);
        Assert.Equal("tenancy.organization.create", negacao.Operation);
        Assert.Equal(AuditOutcome.Denied, negacao.Outcome);
    }

    [Fact]
    public async Task Administrador_de_uma_organizacao_nao_cria_organizacao_pois_falta_a_concessao_de_tenant()
    {
        var c = new Cenario();
        var organizacao = c.NovaOrganizacao("Matriz");
        c.Como(Contextos.DeUsuario(Tenant, Papel(organizacao.Id, Permission.OrganizationCreate)));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Organizacao.CreateAsync("Outra"));
    }

    [Fact]
    public async Task Concessao_de_tenant_cria_organizacao_e_audita_o_sucesso()
    {
        var c = new Cenario();
        c.Como(Contextos.DeUsuario(Tenant, Papel(null, Permission.OrganizationCreate)));

        var criada = await c.Organizacao.CreateAsync("Nova");

        Assert.Single(c.Organizacoes.Itens);
        var sucesso = Assert.Single(c.Auditoria.Eventos);
        Assert.Equal(AuditOutcome.Success, sucesso.Outcome);
        Assert.Equal(criada.Id.ToString(), sucesso.ResourceId);
    }

    [Fact]
    public async Task Usuario_so_lista_e_obtem_as_organizacoes_com_concessao()
    {
        var c = new Cenario();
        var minha = c.NovaOrganizacao("Minha");
        var alheia = c.NovaOrganizacao("Alheia");
        c.Como(Contextos.DeUsuario(Tenant, Papel(minha.Id, Permission.OrganizationRead)));

        var lista = await c.Organizacao.ListAsync();

        Assert.Equal(new[] { "Minha" }, lista.Select(o => o.Name));
        Assert.NotNull(await c.Organizacao.GetAsync(minha.Id));
        Assert.Null(await c.Organizacao.GetAsync(alheia.Id));
    }

    [Fact]
    public async Task Usuario_sem_nenhuma_concessao_nao_enxerga_organizacoes()
    {
        var c = new Cenario();
        var organizacao = c.NovaOrganizacao("Matriz");
        c.Como(Contextos.DeUsuario(Tenant));

        Assert.Empty(await c.Organizacao.ListAsync());
        Assert.Null(await c.Organizacao.GetAsync(organizacao.Id));
    }

    [Fact]
    public async Task Concessao_de_tenant_enxerga_todas_as_organizacoes()
    {
        var c = new Cenario();
        c.NovaOrganizacao("A");
        c.NovaOrganizacao("B");
        c.Como(Contextos.DeUsuario(Tenant, Papel(null, Permission.OrganizationRead)));

        Assert.Equal(2, (await c.Organizacao.ListAsync()).Count);
    }

    [Fact]
    public async Task Contexto_de_sistema_nao_passa_por_permissao_de_usuario()
    {
        var c = new Cenario();
        c.Como(Contextos.DeSistema(Tenant));

        var criada = await c.Organizacao.CreateAsync("Via sistema");

        Assert.NotNull(await c.Organizacao.GetAsync(criada.Id));
        Assert.Single(await c.Organizacao.ListAsync());
    }

    // Unidades

    [Fact]
    public async Task Gestor_cria_unidade_na_sua_organizacao()
    {
        var c = new Cenario();
        var organizacao = c.NovaOrganizacao("Matriz");
        c.Como(Contextos.DeUsuario(Tenant, Papel(organizacao.Id, GestaoEstrutura)));

        var criada = await c.Unidade.CreateAsync(organizacao.Id, "Centro", BusinessUnitKind.Branch);

        Assert.Equal(organizacao.Id, criada.OrganizationId);
        Assert.Equal("tenancy.business-unit.create", Assert.Single(c.Auditoria.Eventos).Operation);
    }

    [Fact]
    public async Task Usuario_com_so_leitura_e_negado_ao_criar_unidade_e_a_negacao_e_auditada()
    {
        var c = new Cenario();
        var organizacao = c.NovaOrganizacao("Matriz");
        c.Como(Contextos.DeUsuario(Tenant, Papel(organizacao.Id, LeituraEstrutura)));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Unidade.CreateAsync(organizacao.Id, "Centro", BusinessUnitKind.Branch));

        Assert.Empty(c.Unidades.Itens);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(c.Auditoria.Eventos).Outcome);
    }

    [Fact]
    public async Task Organizacao_sem_concessao_responde_como_inexistente_ao_criar_e_listar_unidades()
    {
        var c = new Cenario();
        var minha = c.NovaOrganizacao("Minha");
        var alheia = c.NovaOrganizacao("Alheia");
        c.Como(Contextos.DeUsuario(Tenant, Papel(minha.Id, GestaoEstrutura)));

        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => c.Unidade.CreateAsync(alheia.Id, "Intrusa", BusinessUnitKind.Branch));
        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => c.Unidade.ListByOrganizationAsync(alheia.Id));
        Assert.Empty(c.Unidades.Itens);
    }

    [Fact]
    public async Task Organizacao_inexistente_responde_igual_a_organizacao_sem_concessao()
    {
        var c = new Cenario();
        c.Como(Contextos.DeUsuario(Tenant, Papel(null, GestaoEstrutura)));

        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => c.Unidade.CreateAsync(Guid.NewGuid(), "X", BusinessUnitKind.Branch));
    }

    [Fact]
    public async Task Unidade_de_organizacao_sem_concessao_nao_e_obtida()
    {
        var c = new Cenario();
        var minha = c.NovaOrganizacao("Minha");
        var alheia = c.NovaOrganizacao("Alheia");
        var daMinha = c.NovaUnidade(minha, "Minha unidade");
        var daAlheia = c.NovaUnidade(alheia, "Unidade alheia");
        c.Como(Contextos.DeUsuario(Tenant, Papel(minha.Id, LeituraEstrutura)));

        Assert.NotNull(await c.Unidade.GetAsync(daMinha.Id));
        Assert.Null(await c.Unidade.GetAsync(daAlheia.Id));
        Assert.Null(await c.Unidade.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task O_escopo_da_unidade_vem_do_recurso_e_nao_de_parametro_da_requisicao()
    {
        // Tenta criar uma unidade na organização alheia informando o id dela: a negação vale mesmo com nome válido.
        var c = new Cenario();
        var minha = c.NovaOrganizacao("Minha");
        var alheia = c.NovaOrganizacao("Alheia");
        c.Como(Contextos.DeUsuario(Tenant, Papel(minha.Id, GestaoEstrutura)));

        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => c.Unidade.CreateAsync(alheia.Id, "Matriz", BusinessUnitKind.Headquarters));
    }

    // Setores

    [Fact]
    public async Task Gestor_cria_setor_na_unidade_da_sua_organizacao()
    {
        var c = new Cenario();
        var organizacao = c.NovaOrganizacao("Matriz");
        var unidade = c.NovaUnidade(organizacao, "Centro");
        c.Como(Contextos.DeUsuario(Tenant, Papel(organizacao.Id, GestaoEstrutura)));

        var criado = await c.Setor.CreateAsync(unidade.Id, "Coleta");

        Assert.Equal(unidade.Id, criado.BusinessUnitId);
        Assert.Equal("tenancy.department.create", Assert.Single(c.Auditoria.Eventos).Operation);
    }

    [Fact]
    public async Task Usuario_com_so_leitura_e_negado_ao_criar_setor()
    {
        var c = new Cenario();
        var organizacao = c.NovaOrganizacao("Matriz");
        var unidade = c.NovaUnidade(organizacao, "Centro");
        c.Como(Contextos.DeUsuario(Tenant, Papel(organizacao.Id, LeituraEstrutura)));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Setor.CreateAsync(unidade.Id, "Coleta"));

        Assert.Empty(c.Setores.Itens);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(c.Auditoria.Eventos).Outcome);
    }

    [Fact]
    public async Task Setor_e_unidade_de_organizacao_sem_concessao_respondem_como_inexistentes()
    {
        var c = new Cenario();
        var minha = c.NovaOrganizacao("Minha");
        var alheia = c.NovaOrganizacao("Alheia");
        var unidadeAlheia = c.NovaUnidade(alheia, "Unidade alheia");
        var setorAlheio = Department.Create(Tenant, unidadeAlheia.Id, "Setor alheio", DateTimeOffset.UtcNow);
        c.Setores.Itens.Add(setorAlheio);
        c.Como(Contextos.DeUsuario(Tenant, Papel(minha.Id, GestaoEstrutura)));

        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => c.Setor.CreateAsync(unidadeAlheia.Id, "Intruso"));
        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => c.Setor.ListByBusinessUnitAsync(unidadeAlheia.Id));
        Assert.Null(await c.Setor.GetAsync(setorAlheio.Id));
        Assert.Single(c.Setores.Itens);
    }

    [Fact]
    public async Task Sem_contexto_todos_os_casos_de_uso_de_estrutura_falham_fechados()
    {
        var c = new Cenario();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Unidade.CreateAsync(Guid.NewGuid(), "X", BusinessUnitKind.Branch));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Unidade.ListByOrganizationAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Unidade.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Setor.CreateAsync(Guid.NewGuid(), "X"));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Setor.ListByBusinessUnitAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Setor.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Nome_invalido_de_unidade_nao_chega_ao_repositorio()
    {
        var c = new Cenario();
        var organizacao = c.NovaOrganizacao("Matriz");
        c.Como(Contextos.DeUsuario(Tenant, Papel(organizacao.Id, GestaoEstrutura)));

        await Assert.ThrowsAsync<TenancyValidationException>(() => c.Unidade.CreateAsync(organizacao.Id, "  ", BusinessUnitKind.Branch));

        Assert.Empty(c.Unidades.Itens);
    }
}
