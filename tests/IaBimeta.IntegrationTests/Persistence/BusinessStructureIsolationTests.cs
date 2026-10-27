using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// Isolamento de unidades (BusinessUnit) e setores (Department) entre tenants, com PostgreSQL real: dados persistidos,
/// leitura e busca por tenant, falha fechada sem contexto, bloqueio de escrita cruzada e chaves compostas que fazem o
/// próprio banco recusar referência entre tenants. Cada teste cria os próprios tenants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BusinessStructureIsolationTests(PostgresFixture banco)
{
    private async Task<BusinessUnitView> CriarUnidadeAsync(TenantInfo tenant, Guid organizacao, string nome, BusinessUnitKind tipo = BusinessUnitKind.Branch)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        return await escopo.ServiceProvider.GetRequiredService<BusinessUnitService>().CreateAsync(organizacao, nome, tipo);
    }

    private async Task<DepartmentView> CriarSetorAsync(TenantInfo tenant, Guid unidade, string nome)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        return await escopo.ServiceProvider.GetRequiredService<DepartmentService>().CreateAsync(unidade, nome);
    }

    private sealed record Estrutura(TenantInfo Tenant, OrganizationView Organizacao, BusinessUnitView Unidade, DepartmentView Setor);

    private async Task<Estrutura> MontarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, $"Organização {prefixo}");
        var unidade = await CriarUnidadeAsync(tenant, organizacao.Id, $"Matriz {prefixo}", BusinessUnitKind.Headquarters);
        var setor = await CriarSetorAsync(tenant, unidade.Id, $"Coleta {prefixo}");
        return new Estrutura(tenant, organizacao, unidade, setor);
    }

    [Fact]
    public async Task Dados_de_dois_tenants_ficam_persistidos_cada_um_no_seu_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        // Leitura administrativa, ignorando o filtro, só para conferir o que o banco guardou.
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var unidadesDeA = await db.BusinessUnits.IgnoreQueryFilters().Where(u => u.TenantId == a.Tenant.TenantId).Select(u => u.Name).ToListAsync();
        var setoresDeB = await db.Departments.IgnoreQueryFilters().Where(d => d.TenantId == b.Tenant.TenantId).Select(d => d.Name).ToListAsync();

        Assert.Equal(new[] { "Matriz a" }, unidadesDeA);
        Assert.Equal(new[] { "Coleta b" }, setoresDeB);
        Assert.Equal(a.Unidade.Id, a.Setor.BusinessUnitId);
    }

    [Fact]
    public async Task Cada_tenant_so_lista_e_obtem_a_propria_estrutura()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var unidadesA = escopoA.ServiceProvider.GetRequiredService<BusinessUnitService>();
        var unidadesB = escopoB.ServiceProvider.GetRequiredService<BusinessUnitService>();
        var setoresA = escopoA.ServiceProvider.GetRequiredService<DepartmentService>();
        var setoresB = escopoB.ServiceProvider.GetRequiredService<DepartmentService>();

        Assert.Equal(new[] { a.Unidade.Id }, (await unidadesA.ListByOrganizationAsync(a.Organizacao.Id)).Select(u => u.Id));
        Assert.Equal(new[] { a.Setor.Id }, (await setoresA.ListByBusinessUnitAsync(a.Unidade.Id)).Select(s => s.Id));

        // Um tenant não alcança a estrutura do outro nem pelo id: tudo responde como "não encontrado".
        Assert.Null(await unidadesB.GetAsync(a.Unidade.Id));
        Assert.Null(await setoresB.GetAsync(a.Setor.Id));
        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => unidadesB.ListByOrganizationAsync(a.Organizacao.Id));
        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => setoresB.ListByBusinessUnitAsync(a.Unidade.Id));
        Assert.Equal(new[] { b.Unidade.Id }, (await unidadesB.ListByOrganizationAsync(b.Organizacao.Id)).Select(u => u.Id));
    }

    [Fact]
    public async Task Tenant_nao_cria_unidade_nem_setor_dentro_da_estrutura_de_outro_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => CriarUnidadeAsync(b.Tenant, a.Organizacao.Id, "Intrusa"));
        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => CriarSetorAsync(b.Tenant, a.Unidade.Id, "Intruso"));

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        Assert.Single(await escopoA.ServiceProvider.GetRequiredService<BusinessUnitService>().ListByOrganizationAsync(a.Organizacao.Id));
        Assert.Single(await escopoA.ServiceProvider.GetRequiredService<DepartmentService>().ListByBusinessUnitAsync(a.Unidade.Id));
    }

    [Fact]
    public async Task Banco_recusa_unidade_ligada_a_organizacao_de_outro_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        // O tenant do registro é o do contexto (B), então o aplicativo deixa passar; quem recusa é a chave composta.
        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var dbB = escopoB.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbB.BusinessUnits.Add(BusinessUnit.Create(b.Tenant.TenantId, a.Organizacao.Id, "Pendurada", BusinessUnitKind.Branch, DateTimeOffset.UtcNow));

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => dbB.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }

    [Fact]
    public async Task Banco_recusa_setor_ligado_a_unidade_de_outro_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var dbB = escopoB.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbB.Departments.Add(Department.Create(b.Tenant.TenantId, a.Unidade.Id, "Pendurado", DateTimeOffset.UtcNow));

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => dbB.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }

    [Fact]
    public async Task Banco_recusa_unidade_de_organizacao_inexistente()
    {
        var a = await MontarAsync("a");

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.BusinessUnits.Add(BusinessUnit.Create(a.Tenant.TenantId, Guid.NewGuid(), "Sem organizacao", BusinessUnitKind.Branch, DateTimeOffset.UtcNow));

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }

    [Fact]
    public async Task Nome_de_unidade_repete_em_outra_organizacao_e_em_outro_tenant_mas_nao_na_mesma_organizacao()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        var outraOrganizacaoDeA = await banco.CriarOrganizacaoAsync(a.Tenant, "Segunda organização de A");

        await CriarUnidadeAsync(a.Tenant, a.Organizacao.Id, "Centro");
        await CriarUnidadeAsync(a.Tenant, outraOrganizacaoDeA.Id, "Centro");
        await CriarUnidadeAsync(b.Tenant, b.Organizacao.Id, "Centro");

        await Assert.ThrowsAsync<DuplicateBusinessUnitNameException>(() => CriarUnidadeAsync(a.Tenant, a.Organizacao.Id, "Centro"));
    }

    [Fact]
    public async Task Organizacao_tem_no_maximo_uma_matriz_mas_varias_filiais()
    {
        var tenant = await banco.RegistrarTenantAsync("matriz");
        var primeira = await banco.CriarOrganizacaoAsync(tenant, "Primeira");
        var segunda = await banco.CriarOrganizacaoAsync(tenant, "Segunda");

        await CriarUnidadeAsync(tenant, primeira.Id, "Matriz", BusinessUnitKind.Headquarters);
        await CriarUnidadeAsync(tenant, primeira.Id, "Filial 1", BusinessUnitKind.Branch);
        await CriarUnidadeAsync(tenant, primeira.Id, "Filial 2", BusinessUnitKind.Branch);
        await CriarUnidadeAsync(tenant, segunda.Id, "Matriz", BusinessUnitKind.Headquarters);

        await Assert.ThrowsAsync<HeadquartersAlreadyExistsException>(() =>
            CriarUnidadeAsync(tenant, primeira.Id, "Outra matriz", BusinessUnitKind.Headquarters));
    }

    [Fact]
    public async Task Nome_de_setor_repete_em_outra_unidade_mas_nao_na_mesma_unidade()
    {
        var a = await MontarAsync("a");
        var outraUnidade = await CriarUnidadeAsync(a.Tenant, a.Organizacao.Id, "Filial");

        await CriarSetorAsync(a.Tenant, outraUnidade.Id, a.Setor.Name);

        await Assert.ThrowsAsync<DuplicateDepartmentNameException>(() => CriarSetorAsync(a.Tenant, a.Unidade.Id, a.Setor.Name));
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_os_casos_de_uso_de_estrutura_falham_fechados()
    {
        await using var escopo = banco.NovoEscopo();
        var unidades = escopo.ServiceProvider.GetRequiredService<BusinessUnitService>();
        var setores = escopo.ServiceProvider.GetRequiredService<DepartmentService>();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => unidades.CreateAsync(Guid.NewGuid(), "X", BusinessUnitKind.Branch));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => unidades.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => setores.CreateAsync(Guid.NewGuid(), "X"));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => setores.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_a_consulta_direta_ao_DbContext_falha_fechada()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.BusinessUnits.ToListAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Departments.CountAsync());
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_a_gravacao_direta_falha_fechada()
    {
        var a = await MontarAsync("a");
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.BusinessUnits.Add(BusinessUnit.Create(a.Tenant.TenantId, a.Organizacao.Id, "Sem contexto", BusinessUnitKind.Branch, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Gravar_unidade_ou_setor_em_nome_de_outro_tenant_e_bloqueado_antes_do_banco()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var dbB = escopoB.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbB.BusinessUnits.Add(BusinessUnit.Create(a.Tenant.TenantId, a.Organizacao.Id, "Intrusa", BusinessUnitKind.Branch, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        dbB.ChangeTracker.Clear();

        dbB.Departments.Add(Department.Create(a.Tenant.TenantId, a.Unidade.Id, "Intruso", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Excluir_ou_alterar_unidade_e_setor_de_outro_tenant_e_bloqueado()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var dbB = escopoB.ServiceProvider.GetRequiredService<TenancyDbContext>();

        // O invasor contorna o filtro de leitura de propósito e tenta excluir e alterar.
        var unidade = await dbB.BusinessUnits.IgnoreQueryFilters().SingleAsync(u => u.Id == a.Unidade.Id);
        dbB.BusinessUnits.Remove(unidade);
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        dbB.ChangeTracker.Clear();

        var setor = await dbB.Departments.IgnoreQueryFilters().SingleAsync(d => d.Id == a.Setor.Id);
        dbB.Entry(setor).State = EntityState.Modified;
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        Assert.NotNull(await escopoA.ServiceProvider.GetRequiredService<BusinessUnitService>().GetAsync(a.Unidade.Id));
        Assert.NotNull(await escopoA.ServiceProvider.GetRequiredService<DepartmentService>().GetAsync(a.Setor.Id));
    }

    [Fact]
    public async Task Banco_nao_deixa_excluir_organizacao_que_tem_unidades()
    {
        var a = await MontarAsync("a");

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var organizacao = await db.Organizations.SingleAsync(o => o.Id == a.Organizacao.Id);
        db.Organizations.Remove(organizacao);

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }

    // Autorização por recurso com persistência real

    [Fact]
    public async Task Usuario_so_enxerga_e_gere_a_estrutura_das_organizacoes_com_concessao()
    {
        var tenant = await banco.RegistrarTenantAsync("authz");
        var minha = await banco.CriarOrganizacaoAsync(tenant, "Minha");
        var alheia = await banco.CriarOrganizacaoAsync(tenant, "Alheia");
        var unidadeAlheia = await CriarUnidadeAsync(tenant, alheia.Id, "Unidade alheia");
        var gestor = PostgresFixture.ContextoDeUsuario(
            tenant,
            Guid.NewGuid(),
            new PermissionGrant(Permission.OrganizationRead, minha.Id),
            new PermissionGrant(Permission.StructureRead, minha.Id),
            new PermissionGrant(Permission.StructureManage, minha.Id));

        await using var escopo = banco.NovoEscopo(gestor);
        var unidades = escopo.ServiceProvider.GetRequiredService<BusinessUnitService>();
        var setores = escopo.ServiceProvider.GetRequiredService<DepartmentService>();
        var organizacoes = escopo.ServiceProvider.GetRequiredService<OrganizationService>();

        var propria = await unidades.CreateAsync(minha.Id, "Minha unidade", BusinessUnitKind.Headquarters);
        await setores.CreateAsync(propria.Id, "Meu setor");

        Assert.Equal(new[] { "Minha" }, (await organizacoes.ListAsync()).Select(o => o.Name));
        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => unidades.CreateAsync(alheia.Id, "Invasora", BusinessUnitKind.Branch));
        await Assert.ThrowsAsync<TenancyResourceNotFoundException>(() => setores.CreateAsync(unidadeAlheia.Id, "Invasor"));
        Assert.Null(await unidades.GetAsync(unidadeAlheia.Id));
        Assert.Null(await organizacoes.GetAsync(alheia.Id));
    }

    [Fact]
    public async Task Usuario_com_so_leitura_e_negado_ao_criar_e_a_negacao_fica_na_trilha_de_auditoria()
    {
        var tenant = await banco.RegistrarTenantAsync("negado");
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, "Matriz");
        var leitor = PostgresFixture.ContextoDeUsuario(
            tenant,
            Guid.NewGuid(),
            new PermissionGrant(Permission.OrganizationRead, organizacao.Id),
            new PermissionGrant(Permission.StructureRead, organizacao.Id));

        await using (var escopo = banco.NovoEscopo(leitor))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                escopo.ServiceProvider.GetRequiredService<BusinessUnitService>().CreateAsync(organizacao.Id, "Centro", BusinessUnitKind.Branch));
        }

        await using var consulta = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var trilha = await consulta.ServiceProvider.GetRequiredService<AuditQueryService>().ListAsync(operation: "tenancy.business-unit.create");
        var negacao = Assert.Single(trilha);
        Assert.Equal(AuditOutcome.Denied, negacao.Outcome);
        Assert.Equal(leitor.Principal!.UserId, negacao.ActorUserId);
    }
}
