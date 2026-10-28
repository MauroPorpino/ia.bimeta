using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// A contagem de falhas de login com PostgreSQL real: atômica sob concorrência, isolada por tenant, sem depender de o
/// usuário existir, e o bloqueio temporário de ponta a ponta.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LoginThrottlePersistenceTests(PostgresFixture banco)
{
    private static readonly LoginThrottlePolicy Politica = new(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    private async Task<bool> FalharAsync(TenantInfo tenant, string chave, DateTimeOffset agora, LoginThrottlePolicy? politica = null)
    {
        await using var escopo = banco.NovoEscopo();
        return await escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>()
            .RecordLoginFailureAsync(tenant.TenantId, chave, agora, politica ?? Politica, default);
    }

    private async Task<LoginThrottle?> ContagemAsync(TenantInfo tenant, string chave)
    {
        await using var escopo = banco.NovoEscopo();
        return await escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>().FindLoginThrottleAsync(tenant.TenantId, chave, default);
    }

    [Fact]
    public async Task Falhas_simultaneas_do_mesmo_e_mail_nao_perdem_nenhuma_contagem()
    {
        var tenant = await banco.RegistrarTenantAsync("conc");
        var agora = DateTimeOffset.UtcNow;
        var semBloqueio = new LoginThrottlePolicy(1000, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => FalharAsync(tenant, "ana@teste.local", agora, semBloqueio)));

        Assert.Equal(25, (await ContagemAsync(tenant, "ana@teste.local"))!.FailedCount);
    }

    [Fact]
    public async Task Sob_concorrencia_exatamente_uma_falha_causa_o_bloqueio()
    {
        var tenant = await banco.RegistrarTenantAsync("conc");
        var agora = DateTimeOffset.UtcNow;

        var resultados = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => FalharAsync(tenant, "ana@teste.local", agora)));

        Assert.Equal(1, resultados.Count(bloqueou => bloqueou));
        var contagem = (await ContagemAsync(tenant, "ana@teste.local"))!;
        Assert.True(contagem.IsLockedAt(agora.AddMinutes(1)));
        Assert.Equal(5, contagem.FailedCount);
    }

    [Fact]
    public async Task A_contagem_e_isolada_por_tenant_e_por_e_mail()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        var agora = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            await FalharAsync(a, "ana@teste.local", agora);
        }

        Assert.True((await ContagemAsync(a, "ana@teste.local"))!.IsLockedAt(agora));
        Assert.Null(await ContagemAsync(b, "ana@teste.local"));
        Assert.Null(await ContagemAsync(a, "bia@teste.local"));
    }

    [Fact]
    public async Task E_mail_sem_usuario_tem_contagem_e_bloqueio_do_mesmo_jeito()
    {
        var tenant = await banco.RegistrarTenantAsync("fantasma");
        var agora = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            await FalharAsync(tenant, "ninguem@teste.local", agora);
        }

        Assert.True((await ContagemAsync(tenant, "ninguem@teste.local"))!.IsLockedAt(agora));
    }

    [Fact]
    public async Task Limpar_remove_a_contagem_so_do_e_mail_e_do_tenant_informados()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        var agora = DateTimeOffset.UtcNow;
        await FalharAsync(a, "ana@teste.local", agora);
        await FalharAsync(b, "ana@teste.local", agora);
        await FalharAsync(a, "bia@teste.local", agora);

        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>().ClearLoginThrottleAsync(a.TenantId, "ana@teste.local", default);
        }

        Assert.Null(await ContagemAsync(a, "ana@teste.local"));
        Assert.NotNull(await ContagemAsync(b, "ana@teste.local"));
        Assert.NotNull(await ContagemAsync(a, "bia@teste.local"));
    }

    [Fact]
    public async Task A_contagem_nao_e_gravada_em_tenant_diferente_do_contexto_estabelecido()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(b));
        var loja = escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>();

        await Assert.ThrowsAsync<CrossTenantAccessException>(() =>
            loja.RecordLoginFailureAsync(a.TenantId, "ana@teste.local", DateTimeOffset.UtcNow, Politica, default));
        Assert.Null(await ContagemAsync(a, "ana@teste.local"));
    }

    [Fact]
    public async Task Sem_contexto_a_consulta_direta_a_tabela_de_contagens_falha_fechada()
    {
        await using var escopo = banco.NovoEscopo();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() =>
            escopo.ServiceProvider.GetRequiredService<IdentityDbContext>().LoginThrottles.ToListAsync());
    }

    // Ponta a ponta no serviço, com o relógio controlado

    private static AuthenticationService Servico(AsyncServiceScope escopo, RelogioManual relogio) => new(
        escopo.ServiceProvider.GetRequiredService<ITenantDirectory>(),
        escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>(),
        escopo.ServiceProvider.GetRequiredService<ISessionRepository>(),
        escopo.ServiceProvider.GetRequiredService<PasswordHasher>(),
        escopo.ServiceProvider.GetRequiredService<IdentityOptions>(),
        relogio,
        escopo.ServiceProvider.GetRequiredService<IAuditWriter>(),
        escopo.ServiceProvider.GetRequiredService<IRequestContextAccessor>());

    [Fact]
    public async Task Cinco_senhas_erradas_bloqueiam_ate_a_senha_certa_e_depois_do_tempo_o_login_volta()
    {
        var tenant = await banco.RegistrarTenantAsync("bloqueio");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        var relogio = new RelogioManual();

        for (var i = 0; i < 5; i++)
        {
            await using var escopo = banco.NovoEscopo();
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => Servico(escopo, relogio).LoginAsync(tenant.Slug, admin.Email, "senha-errada-123"));
        }

        await using (var bloqueado = banco.NovoEscopo())
        {
            var bloqueio = await Assert.ThrowsAsync<LoginThrottledException>(() => Servico(bloqueado, relogio).LoginAsync(tenant.Slug, admin.Email, admin.Senha));
            Assert.InRange(bloqueio.RetryAfter, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        }

        relogio.Avancar(TimeSpan.FromMinutes(16));
        await using var depois = banco.NovoEscopo();
        Assert.NotEmpty((await Servico(depois, relogio).LoginAsync(tenant.Slug, admin.Email, admin.Senha)).Token);
        Assert.Null(await ContagemAsync(tenant, admin.Email));
    }

    [Fact]
    public async Task O_bloqueio_de_um_tenant_nao_afeta_o_mesmo_e_mail_em_outro_tenant()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        var adminDeA = await banco.CriarAdministradorDoTenantAsync(a, "mesmo@teste.local");
        var adminDeB = await banco.CriarAdministradorDoTenantAsync(b, "mesmo@teste.local");
        var relogio = new RelogioManual();

        for (var i = 0; i < 5; i++)
        {
            await using var escopo = banco.NovoEscopo();
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => Servico(escopo, relogio).LoginAsync(a.Slug, "mesmo@teste.local", "senha-errada-123"));
        }

        await using var final = banco.NovoEscopo();
        await Assert.ThrowsAsync<LoginThrottledException>(() => Servico(final, relogio).LoginAsync(a.Slug, "mesmo@teste.local", adminDeA.Senha));
        Assert.NotEmpty((await Servico(final, relogio).LoginAsync(b.Slug, "mesmo@teste.local", adminDeB.Senha)).Token);
    }
}
