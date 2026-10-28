using System.Security.Cryptography;
using IaBimeta.Api.Channels;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    /// <summary>A chave de cifra das credenciais nos testes: aleatória a cada execução, sintética, nunca gravada em arquivo.</summary>
    public static readonly string ChaveDeCifraDeTeste = Convert.ToBase64String(RandomNumberGenerator.GetBytes(ChannelSecretOptions.KeySizeBytes));

    public const string IdDaChaveDeTeste = "teste-1";

    private ServiceProvider? _servicos;
    private ServiceProvider? _servicosComCanaisReais;
    private ServiceProvider? _servicosComCanaisIndisponiveis;

    public string ConnectionString { get; } = TestDatabase.ConnectionString;

    /// <summary>
    /// Três provedores, a mesma composição do host. No padrão, a autorização de unidade por canal é um dublê que aceita tudo (os
    /// testes de conversas não registram canais). No de "canais reais", é a implementação do host sobre o Channels: aceita só o
    /// que o cadastro autoriza. No de "canais indisponíveis", é a mesma implementação do host, mas o banco do Channels não responde
    /// (um banco que não existe), enquanto o do Conversations e os demais funcionam: a falha da consulta de autorização, de verdade.
    /// </summary>
    private ServiceProvider Compor(bool canaisReais, bool canaisIndisponiveis = false)
    {
        var colecao = new ServiceCollection();
        colecao.AddScoped<RequestContextHolder>();
        colecao.AddScoped<IRequestContextAccessor>(provider => provider.GetRequiredService<RequestContextHolder>());
        colecao.AddAudit(_ => ConnectionString);
        colecao.AddTenancy(_ => ConnectionString);
        colecao.AddIdentityModule(_ => ConnectionString, opcoes => opcoes.PasswordHashIterations = PasswordIterations);
        // Etapa 3: o publicador é o dublê em memória (não durável), como no host.
        colecao.AddSingleton<InMemoryEventPublisher>();
        colecao.AddSingleton<IEventPublisher>(provider => provider.GetRequiredService<InMemoryEventPublisher>());
        var conexaoDosCanais = canaisIndisponiveis
            ? new NpgsqlConnectionStringBuilder(ConnectionString) { Database = $"iabimeta_canais_fora_{Guid.NewGuid():N}", Timeout = 5 }.ConnectionString
            : ConnectionString;
        colecao.AddChannels(_ => conexaoDosCanais, opcoes =>
        {
            opcoes.ActiveKeyId = IdDaChaveDeTeste;
            opcoes.Keys[IdDaChaveDeTeste] = ChaveDeCifraDeTeste;
        });
        if (canaisReais || canaisIndisponiveis)
        {
            colecao.AddScoped<IChannelUnitAuthorization, ChannelsBackedUnitAuthorization>();
        }
        else
        {
            colecao.AddScoped<IChannelUnitAuthorization, AutorizaQualquerUnidade>();
        }

        colecao.AddConversations(_ => ConnectionString);
        return colecao.BuildServiceProvider(validateScopes: true);
    }

    public async Task InitializeAsync()
    {
        _servicos = Compor(canaisReais: false);
        _servicosComCanaisReais = Compor(canaisReais: true);
        _servicosComCanaisIndisponiveis = Compor(canaisReais: true, canaisIndisponiveis: true);

        await using var escopo = _servicos.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
        await escopo.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync();
        await escopo.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        await escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>().Database.MigrateAsync();
        await escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>().Database.MigrateAsync();
    }

    /// <summary>Os eventos publicados pelo Conversations (dublê em memória), para os testes conferirem o que sairia para os consumidores.</summary>
    public InMemoryEventPublisher Eventos => _servicos!.GetRequiredService<InMemoryEventPublisher>();

    /// <summary>Os eventos publicados na composição de "canais reais" (cada composição tem o próprio dublê).</summary>
    public InMemoryEventPublisher EventosComCanaisReais => _servicosComCanaisReais!.GetRequiredService<InMemoryEventPublisher>();

    /// <summary>Os eventos publicados na composição de "canais indisponíveis".</summary>
    public InMemoryEventPublisher EventosComCanaisIndisponiveis => _servicosComCanaisIndisponiveis!.GetRequiredService<InMemoryEventPublisher>();

    public async Task DisposeAsync()
    {
        if (_servicos is not null)
        {
            await _servicos.DisposeAsync();
        }

        if (_servicosComCanaisReais is not null)
        {
            await _servicosComCanaisReais.DisposeAsync();
        }

        if (_servicosComCanaisIndisponiveis is not null)
        {
            await _servicosComCanaisIndisponiveis.DisposeAsync();
        }
    }

    /// <summary>Equivale a uma requisição. Sem <paramref name="contexto"/>, nenhum tenant foi estabelecido.</summary>
    public AsyncServiceScope NovoEscopo(RequestContext? contexto = null) => Abrir(_servicos!, contexto);

    /// <summary>
    /// Como <see cref="NovoEscopo"/>, mas com a autorização de unidade por canal REAL (o host sobre o Channels): só aceita as
    /// unidades que o cadastro do canal autoriza.
    /// </summary>
    public AsyncServiceScope NovoEscopoComCanaisReais(RequestContext? contexto = null) => Abrir(_servicosComCanaisReais!, contexto);

    /// <summary>
    /// Como <see cref="NovoEscopoComCanaisReais"/>, mas com o banco do Channels fora do ar: toda consulta de autorização de unidade
    /// falha. Serve para provar que a falha não vira "sim" nem "não" e que nada é gravado por causa dela.
    /// </summary>
    public AsyncServiceScope NovoEscopoComCanaisIndisponiveis(RequestContext? contexto = null) => Abrir(_servicosComCanaisIndisponiveis!, contexto);

    private static AsyncServiceScope Abrir(ServiceProvider provedor, RequestContext? contexto)
    {
        var escopo = provedor.CreateAsyncScope();
        if (contexto is not null)
        {
            escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(contexto);
        }

        return escopo;
    }

    /// <summary>O dublê dos testes de conversas: aceita qualquer unidade em qualquer canal (os testes de autorização trocam-no pelo real).</summary>
    private sealed class AutorizaQualquerUnidade : IChannelUnitAuthorization
    {
        public Task<bool> IsBusinessUnitAuthorizedAsync(ChannelInstanceId channelInstanceId, Guid businessUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
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
