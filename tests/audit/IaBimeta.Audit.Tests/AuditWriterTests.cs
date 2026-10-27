using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Audit.Tests;

public class AuditWriterTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelogioFixo : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    private sealed class RepositorioEmMemoria : IAuditRepository
    {
        public List<AuditEvent> Contexto { get; } = new();
        public List<AuditEvent> Autenticacao { get; } = new();
        public AuditQuery? UltimaConsulta { get; private set; }

        public Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Contexto.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task AddForAuthenticationAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Autenticacao.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
        {
            UltimaConsulta = query;
            return Task.FromResult<IReadOnlyList<AuditEvent>>(Contexto.ToList());
        }
    }

    private static readonly AuditEntry Entrada = new("tenancy.organization.create", "organization", "abc-123", AuditOutcome.Success);

    [Fact]
    public async Task Sem_contexto_o_registro_falha_fechado_sem_tocar_o_repositorio()
    {
        var repositorio = new RepositorioEmMemoria();
        var escritor = new AuditWriter(repositorio, new RequestContextHolder(), new RelogioFixo());

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => escritor.WriteAsync(Entrada));

        Assert.Empty(repositorio.Contexto);
    }

    [Fact]
    public async Task Tenant_ator_origem_e_data_vem_do_contexto_e_do_relogio()
    {
        var repositorio = new RepositorioEmMemoria();
        var holder = new RequestContextHolder();
        var tenant = new TenantId(Guid.NewGuid());
        var usuario = Guid.NewGuid();
        holder.Set(new RequestContext(tenant, "session", new ActorPrincipal(usuario, Guid.NewGuid(), Array.Empty<PermissionGrant>())));
        var escritor = new AuditWriter(repositorio, holder, new RelogioFixo());

        await escritor.WriteAsync(Entrada);

        var evento = Assert.Single(repositorio.Contexto);
        Assert.Equal(tenant, evento.TenantId);
        Assert.Equal(usuario, evento.ActorUserId);
        Assert.Equal("session", evento.ActorOrigin);
        Assert.Equal(Agora, evento.OccurredAt);
    }

    [Fact]
    public async Task Contexto_de_sistema_registra_sem_ator()
    {
        var repositorio = new RepositorioEmMemoria();
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(new TenantId(Guid.NewGuid()), "development-seed"));
        var escritor = new AuditWriter(repositorio, holder, new RelogioFixo());

        await escritor.WriteAsync(Entrada);

        var evento = Assert.Single(repositorio.Contexto);
        Assert.Null(evento.ActorUserId);
        Assert.Equal("development-seed", evento.ActorOrigin);
    }

    [Fact]
    public async Task Evento_de_autenticacao_usa_o_tenant_informado_pelo_identity_e_a_origem_authentication()
    {
        var repositorio = new RepositorioEmMemoria();
        var escritor = new AuditWriter(repositorio, new RequestContextHolder(), new RelogioFixo());
        var tenant = new TenantId(Guid.NewGuid());
        var usuario = Guid.NewGuid();

        await escritor.WriteForAuthenticationAsync(tenant, usuario, new AuditEntry("identity.login", "user", usuario.ToString(), AuditOutcome.Denied));

        Assert.Empty(repositorio.Contexto);
        var evento = Assert.Single(repositorio.Autenticacao);
        Assert.Equal(tenant, evento.TenantId);
        Assert.Equal(usuario, evento.ActorUserId);
        Assert.Equal(AuditWriter.AuthenticationOrigin, evento.ActorOrigin);
    }

    [Fact]
    public async Task Entrada_fora_do_formato_nao_e_gravada()
    {
        var repositorio = new RepositorioEmMemoria();
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(new TenantId(Guid.NewGuid()), "session"));
        var escritor = new AuditWriter(repositorio, holder, new RelogioFixo());

        await Assert.ThrowsAsync<AuditValidationException>(() =>
            escritor.WriteAsync(new AuditEntry("identity.login", "user", "senha com espaco", AuditOutcome.Success)));

        Assert.Empty(repositorio.Contexto);
    }

    // Consulta

    private static (AuditQueryService Servico, RepositorioEmMemoria Repositorio, RequestContextHolder Holder) CriarConsulta()
    {
        var repositorio = new RepositorioEmMemoria();
        var holder = new RequestContextHolder();
        return (new AuditQueryService(repositorio, holder), repositorio, holder);
    }

    private static RequestContext Usuario(params PermissionGrant[] permissoes) =>
        new(new TenantId(Guid.NewGuid()), "session", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), permissoes));

    [Fact]
    public async Task Consulta_sem_contexto_falha_fechada()
    {
        var (servico, repositorio, _) = CriarConsulta();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.ListAsync());

        Assert.Null(repositorio.UltimaConsulta);
    }

    [Fact]
    public async Task Usuario_sem_permissao_de_auditoria_nao_consulta_a_trilha()
    {
        var (servico, repositorio, holder) = CriarConsulta();
        holder.Set(Usuario(new PermissionGrant(Permission.OrganizationRead, null)));

        await Assert.ThrowsAsync<AccessDeniedException>(() => servico.ListAsync());

        Assert.Null(repositorio.UltimaConsulta);
    }

    [Fact]
    public async Task Permissao_de_auditoria_de_uma_organizacao_nao_basta_pois_a_trilha_e_do_tenant()
    {
        var (servico, _, holder) = CriarConsulta();
        holder.Set(Usuario(new PermissionGrant(Permission.AuditRead, Guid.NewGuid())));

        await Assert.ThrowsAsync<AccessDeniedException>(() => servico.ListAsync());
    }

    [Fact]
    public async Task Concessao_de_tenant_consulta_a_trilha_com_limite_padrao()
    {
        var (servico, repositorio, holder) = CriarConsulta();
        holder.Set(Usuario(new PermissionGrant(Permission.AuditRead, null)));

        await servico.ListAsync();

        Assert.Equal(AuditQueryService.DefaultTake, repositorio.UltimaConsulta!.Take);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(25, 25)]
    [InlineData(100000, AuditQueryService.MaxTake)]
    public async Task O_limite_da_consulta_e_ajustado_ao_intervalo_permitido(int pedido, int esperado)
    {
        var (servico, repositorio, holder) = CriarConsulta();
        holder.Set(Usuario(new PermissionGrant(Permission.AuditRead, null)));

        await servico.ListAsync(pedido);

        Assert.Equal(esperado, repositorio.UltimaConsulta!.Take);
    }

    [Fact]
    public async Task Contexto_de_sistema_consulta_a_trilha()
    {
        var (servico, _, holder) = CriarConsulta();
        holder.Set(new RequestContext(new TenantId(Guid.NewGuid()), "development-header"));

        Assert.Empty(await servico.ListAsync());
    }
}
