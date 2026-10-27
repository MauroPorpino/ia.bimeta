using IaBimeta.Audit.Infrastructure;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// Compartilha o PostgreSQL de teste entre as classes de teste: aplica as migrations uma vez e entrega "escopos",
/// cada um equivalente a uma requisição (com ou sem contexto de tenant), usando a mesma composição de DI do host.
/// Cada teste cria os próprios tenants com slug aleatório, então execuções repetidas não interferem entre si e
/// nenhum dado é apagado.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Iterações do PBKDF2 nos testes: o piso aceito, só para a suíte ser rápida (o padrão de produção é 600 mil e tem teste próprio).</summary>
    public const int PasswordIterations = IdentityOptions.MinimumPasswordHashIterations;

    public const string PasswordPadrao = "Senha-De-Teste-2026!";

    private ServiceProvider? _servicos;

    public string ConnectionString { get; } = TestDatabase.ConnectionString;

    public async Task InitializeAsync()
    {
        var colecao = new ServiceCollection();
        colecao.AddScoped<RequestContextHolder>();
        colecao.AddScoped<IRequestContextAccessor>(provider => provider.GetRequiredService<RequestContextHolder>());
        colecao.AddAudit(_ => ConnectionString);
        colecao.AddTenancy(_ => ConnectionString);
        colecao.AddIdentityModule(_ => ConnectionString, opcoes => opcoes.PasswordHashIterations = PasswordIterations);
        _servicos = colecao.BuildServiceProvider(validateScopes: true);

        await using var escopo = _servicos.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
        await escopo.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync();
        await escopo.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => _servicos is null ? Task.CompletedTask : _servicos.DisposeAsync().AsTask();

    /// <summary>Equivale a uma requisição. Sem <paramref name="contexto"/>, nenhum tenant foi estabelecido.</summary>
    public AsyncServiceScope NovoEscopo(RequestContext? contexto = null)
    {
        var escopo = _servicos!.CreateAsyncScope();
        if (contexto is not null)
        {
            escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(contexto);
        }

        return escopo;
    }

    /// <summary>Contexto de sistema (sem usuário), como o do cabeçalho de desenvolvimento, do seed e dos jobs.</summary>
    public static RequestContext Contexto(TenantInfo tenant) => new(tenant.TenantId, "teste-integracao");

    /// <summary>Contexto de um usuário autenticado, com as permissões efetivas informadas, como o host o criaria a partir de uma sessão.</summary>
    public static RequestContext ContextoDeUsuario(TenantInfo tenant, Guid userId, params PermissionGrant[] permissoes) =>
        new(tenant.TenantId, "teste-integracao", new ActorPrincipal(userId, Guid.NewGuid(), permissoes));

    public async Task<TenantInfo> RegistrarTenantAsync(string prefixo)
    {
        await using var escopo = NovoEscopo();
        var slug = $"{prefixo}-{Guid.NewGuid().ToString("N")[..8]}";
        return await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>()
            .RegisterAsync(slug, $"Tenant de teste {slug}");
    }

    public async Task<OrganizationView> CriarOrganizacaoAsync(TenantInfo tenant, string nome)
    {
        await using var escopo = NovoEscopo(Contexto(tenant));
        return await escopo.ServiceProvider.GetRequiredService<OrganizationService>().CreateAsync(nome);
    }

    /// <summary>Cria um usuário com o papel na organização, como um administrador faria (contexto de sistema do teste).</summary>
    public async Task<UsuarioCriado> CriarUsuarioAsync(TenantInfo tenant, Guid organizacaoId, Role papel, string? email = null, string senha = PasswordPadrao)
    {
        email ??= $"u{Guid.NewGuid().ToString("N")[..10]}@teste.local";
        await using var escopo = NovoEscopo(Contexto(tenant));
        var criado = await escopo.ServiceProvider.GetRequiredService<UserService>()
            .CreateAsync(organizacaoId, email, "Usuário de teste", senha, papel);
        return new UsuarioCriado(criado.Id, criado.Email, senha, tenant.Slug);
    }

    /// <summary>Cria o administrador do tenant, com concessão de escopo do tenant, pelo mesmo caminho do seed.</summary>
    public async Task<UsuarioCriado> CriarAdministradorDoTenantAsync(TenantInfo tenant, string? email = null, string senha = PasswordPadrao)
    {
        email ??= $"adm{Guid.NewGuid().ToString("N")[..10]}@teste.local";
        await using var escopo = NovoEscopo(Contexto(tenant));
        var servico = escopo.ServiceProvider.GetRequiredService<UserService>();
        await servico.EnsureTenantAdministratorAsync(email, "Administrador de teste", senha);

        var usuario = await escopo.ServiceProvider.GetRequiredService<IUserRepository>().FindByEmailAsync(email, default);
        return new UsuarioCriado(usuario!.Id, email, senha, tenant.Slug);
    }
}

/// <param name="Id">Identificador do usuário.</param>
/// <param name="Email">E-mail (já em minúsculas).</param>
/// <param name="Senha">A senha em texto, que só existe no teste.</param>
/// <param name="TenantSlug">O slug do tenant, usado no login.</param>
public sealed record UsuarioCriado(Guid Id, string Email, string Senha, string TenantSlug);
