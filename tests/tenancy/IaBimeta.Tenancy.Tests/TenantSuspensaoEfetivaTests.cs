using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

/// <summary>Suspensão efetiva (Etapa 02, ADR 0043): domínio do Tenant e porta de operação do processamento automático.</summary>
public class TenantSuspensaoEfetivaTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static Tenant Ativo(string slug = "lab-central") =>
        Tenant.Register(new TenantId(Guid.NewGuid()), slug, "Lab", Agora);

    [Fact]
    public void Suspender_com_motivo_registra_historico_e_instante()
    {
        var tenant = Ativo();
        var ator = Guid.NewGuid();

        var mudanca = tenant.SuspendWithReason("  Inadimplência  ", ator, Agora);

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.Equal(Agora, tenant.SuspendedAt);
        Assert.Equal(TenantStatus.Active, mudanca.FromStatus);
        Assert.Equal(TenantStatus.Suspended, mudanca.ToStatus);
        Assert.Equal("Inadimplência", mudanca.Reason);
        Assert.Equal(ator, mudanca.ActorUserId);
        Assert.Equal(tenant.Id, mudanca.TenantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("abcd")]
    public void Suspender_exige_motivo_com_pelo_menos_cinco_caracteres(string? motivo)
    {
        var tenant = Ativo();

        Assert.Throws<TenancyValidationException>(() => tenant.SuspendWithReason(motivo, null, Agora));
        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    [Fact]
    public void Motivo_acima_de_500_caracteres_e_recusado_e_com_500_e_aceito()
    {
        var tenant = Ativo();

        Assert.Throws<TenancyValidationException>(() => tenant.SuspendWithReason(new string('m', Tenant.ReasonMaxLength + 1), null, Agora));
        tenant.SuspendWithReason(new string('m', Tenant.ReasonMaxLength), null, Agora);

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
    }

    [Fact]
    public void So_empresa_ativa_pode_ser_suspensa()
    {
        var suspensa = Ativo();
        suspensa.SuspendWithReason("motivo valido", null, Agora);
        var provisionando = Tenant.RegisterCommercial(new TenantId(Guid.NewGuid()), "nova-empresa", "Nova", Agora);

        Assert.Throws<TenancyValidationException>(() => suspensa.SuspendWithReason("motivo valido", null, Agora));
        Assert.Throws<TenancyValidationException>(() => provisionando.SuspendWithReason("motivo valido", null, Agora));
    }

    [Fact]
    public void Tenant_da_plataforma_nunca_e_suspenso()
    {
        var plataforma = Tenant.RegisterPlatform(new TenantId(Guid.NewGuid()), "Plataforma", Agora);

        Assert.Equal(TenantKind.Platform, plataforma.Kind);
        Assert.Equal(TenantStatus.Active, plataforma.Status);
        Assert.Throws<TenancyValidationException>(() => plataforma.SuspendWithReason("motivo valido", null, Agora));
        Assert.Equal(TenantStatus.Active, plataforma.Status);
    }

    [Fact]
    public void Reativar_registra_ReactivatedAt_limpa_SuspendedAt_e_gera_historico()
    {
        var tenant = Ativo();
        tenant.SuspendWithReason("motivo valido", null, Agora);
        var depois = Agora.AddHours(3);

        var mudanca = tenant.ReactivateWithReason("regularizado", Guid.NewGuid(), depois);

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(depois, tenant.ReactivatedAt);
        Assert.Null(tenant.SuspendedAt);
        Assert.Equal(TenantStatus.Suspended, mudanca.FromStatus);
        Assert.Equal(TenantStatus.Active, mudanca.ToStatus);
    }

    [Fact]
    public void Reativar_so_vale_para_empresa_suspensa()
    {
        var ativa = Ativo();

        Assert.Throws<TenancyValidationException>(() => ativa.ReactivateWithReason("motivo valido", null, Agora));
        Assert.Null(ativa.ReactivatedAt);
    }

    [Fact]
    public void Reativar_exige_motivo()
    {
        var tenant = Ativo();
        tenant.SuspendWithReason("motivo valido", null, Agora);

        Assert.Throws<TenancyValidationException>(() => tenant.ReactivateWithReason("  ab ", null, Agora));
        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.Null(tenant.ReactivatedAt);
    }

    [Fact]
    public void RegisterCommercial_nasce_em_provisionamento()
    {
        var tenant = Tenant.RegisterCommercial(new TenantId(Guid.NewGuid()), "nova-empresa", "Nova", Agora);

        Assert.Equal(TenantStatus.Provisioning, tenant.Status);
        Assert.Equal(TenantKind.Commercial, tenant.Kind);
    }

    [Theory]
    [InlineData("bimeta-platform")]
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("platform")]
    [InlineData("bimeta")]
    [InlineData("webhooks")]
    public void RegisterCommercial_recusa_slug_reservado(string slug)
    {
        Assert.True(Tenant.IsReservedSlug(slug));
        Assert.Throws<TenancyValidationException>(() => Tenant.RegisterCommercial(new TenantId(Guid.NewGuid()), slug, "Nome", Agora));
    }

    [Fact]
    public void Slug_reservado_e_reconhecido_sem_diferenciar_caixa_e_espacos()
    {
        Assert.True(Tenant.IsReservedSlug("  BIMETA-PLATFORM "));
        Assert.False(Tenant.IsReservedSlug("lab-central"));
        Assert.False(Tenant.IsReservedSlug(null));
    }

    [Fact]
    public void RegisterPlatform_usa_o_slug_da_plataforma()
    {
        var plataforma = Tenant.RegisterPlatform(new TenantId(Guid.NewGuid()), "Plataforma", Agora);

        Assert.Equal(Tenant.PlatformSlug, plataforma.Slug);
        Assert.Equal("bimeta-platform", plataforma.Slug);
    }

    [Fact]
    public void CompleteProvisioning_ativa_a_empresa_em_provisionamento()
    {
        var tenant = Tenant.RegisterCommercial(new TenantId(Guid.NewGuid()), "nova-empresa", "Nova", Agora);

        tenant.CompleteProvisioning(Agora.AddMinutes(1));

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(Agora.AddMinutes(1), tenant.UpdatedAt);
    }

    [Fact]
    public void CompleteProvisioning_so_vale_em_provisionamento()
    {
        Assert.Throws<TenancyValidationException>(() => Ativo().CompleteProvisioning(Agora));
    }

    [Fact]
    public void UpdateCadastral_normaliza_cnpj_email_e_nao_muda_o_slug()
    {
        var tenant = Ativo("lab-central");

        tenant.UpdateCadastral("  Novo Nome ", " Razão Ltda ", "12.345.678/0001-90", " Maria ", " Maria@Exemplo.COM ", Agora);

        Assert.Equal("lab-central", tenant.Slug);
        Assert.Equal("Novo Nome", tenant.Name);
        Assert.Equal("Razão Ltda", tenant.LegalName);
        Assert.Equal("12345678000190", tenant.Document);
        Assert.Equal("Maria", tenant.ContactName);
        Assert.Equal("maria@exemplo.com", tenant.ContactEmail);
        Assert.Equal(Agora, tenant.UpdatedAt);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12.345.678/0001-9")]
    [InlineData("123456789012345")]
    public void UpdateCadastral_recusa_cnpj_sem_14_digitos(string cnpj)
    {
        Assert.Throws<TenancyValidationException>(() => Ativo().UpdateCadastral("Nome", null, cnpj, null, null, Agora));
    }

    [Theory]
    [InlineData("sem-arroba")]
    [InlineData("a@b")]
    [InlineData("a b@c.com")]
    public void UpdateCadastral_recusa_email_invalido(string email)
    {
        Assert.Throws<TenancyValidationException>(() => Ativo().UpdateCadastral("Nome", null, null, null, email, Agora));
    }

    [Fact]
    public void UpdateCadastral_recusa_nome_vazio_e_aceita_campos_opcionais_vazios()
    {
        var tenant = Ativo();

        Assert.Throws<TenancyValidationException>(() => tenant.UpdateCadastral("  ", null, null, null, null, Agora));
        tenant.UpdateCadastral("Nome", "", "", "", "", Agora);

        Assert.Null(tenant.LegalName);
        Assert.Null(tenant.Document);
        Assert.Null(tenant.ContactEmail);
    }

    // ---- Porta de operação ----

    private sealed class RepositorioFake : ITenantRepository
    {
        public List<Tenant> Tenants { get; } = new();

        public Task AddAsync(Tenant tenant, CancellationToken cancellationToken) { Tenants.Add(tenant); return Task.CompletedTask; }
        public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken) => Task.FromResult(Tenants.FirstOrDefault(t => t.Slug == slug));
        public Task<Tenant?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult(Tenants.FirstOrDefault(t => t.Id == tenantId));
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<(IReadOnlyList<Tenant> Items, int Total)> ListCommercialAsync(string? search, TenantStatus? status, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult(((IReadOnlyList<Tenant>)new List<Tenant>(), 0));
        public Task<IReadOnlyList<Tenant>> ListAllCommercialAsync(CancellationToken cancellationToken) => Task.FromResult((IReadOnlyList<Tenant>)new List<Tenant>());
        public Task<Tenant?> FindPlatformAsync(CancellationToken cancellationToken) => Task.FromResult<Tenant?>(null);
        public Task SaveStatusChangeAsync(TenantStatusChange change, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<TenantStatusChange>> ListStatusChangesAsync(TenantId tenantId, int take, CancellationToken cancellationToken) =>
            Task.FromResult((IReadOnlyList<TenantStatusChange>)new List<TenantStatusChange>());
    }

    [Fact]
    public async Task Gate_empresa_ativa_nunca_suspensa_e_operacional_sem_corte()
    {
        var repo = new RepositorioFake();
        var tenant = Ativo();
        repo.Tenants.Add(tenant);

        var estado = await new TenantOperationGate(repo).GetAsync(tenant.Id);

        Assert.True(estado.IsOperational);
        Assert.Null(estado.OperationalSince);
    }

    [Fact]
    public async Task Gate_empresa_reativada_e_operacional_com_corte_na_reativacao()
    {
        var repo = new RepositorioFake();
        var tenant = Ativo();
        tenant.SuspendWithReason("motivo valido", null, Agora);
        tenant.ReactivateWithReason("regularizado", null, Agora.AddDays(1));
        repo.Tenants.Add(tenant);

        var estado = await new TenantOperationGate(repo).GetAsync(tenant.Id);

        Assert.True(estado.IsOperational);
        Assert.Equal(Agora.AddDays(1), estado.OperationalSince);
    }

    [Fact]
    public async Task Gate_empresa_suspensa_nao_e_operacional()
    {
        var repo = new RepositorioFake();
        var tenant = Ativo();
        tenant.SuspendWithReason("motivo valido", null, Agora);
        repo.Tenants.Add(tenant);

        var estado = await new TenantOperationGate(repo).GetAsync(tenant.Id);

        Assert.False(estado.IsOperational);
    }

    [Fact]
    public async Task Gate_empresa_em_provisionamento_nao_e_operacional()
    {
        var repo = new RepositorioFake();
        var tenant = Tenant.RegisterCommercial(new TenantId(Guid.NewGuid()), "nova-empresa", "Nova", Agora);
        repo.Tenants.Add(tenant);

        Assert.False((await new TenantOperationGate(repo).GetAsync(tenant.Id)).IsOperational);
    }

    [Fact]
    public async Task Gate_empresa_inexistente_falha_fechada()
    {
        var estado = await new TenantOperationGate(new RepositorioFake()).GetAsync(new TenantId(Guid.NewGuid()));

        Assert.False(estado.IsOperational);
        Assert.Null(estado.OperationalSince);
    }

    [Fact]
    public async Task Gate_le_o_registro_a_cada_consulta_sem_cache()
    {
        var repo = new RepositorioFake();
        var tenant = Ativo();
        repo.Tenants.Add(tenant);
        var gate = new TenantOperationGate(repo);

        Assert.True((await gate.GetAsync(tenant.Id)).IsOperational);
        tenant.SuspendWithReason("motivo valido", null, Agora);
        Assert.False((await gate.GetAsync(tenant.Id)).IsOperational);
    }

    [Fact]
    public async Task Gate_so_enxerga_o_tenant_consultado()
    {
        var repo = new RepositorioFake();
        var suspenso = Ativo("empresa-a");
        suspenso.SuspendWithReason("motivo valido", null, Agora);
        var ativo = Ativo("empresa-b");
        repo.Tenants.Add(suspenso);
        repo.Tenants.Add(ativo);
        var gate = new TenantOperationGate(repo);

        Assert.False((await gate.GetAsync(suspenso.Id)).IsOperational);
        Assert.True((await gate.GetAsync(ativo.Id)).IsOperational);
    }
}
