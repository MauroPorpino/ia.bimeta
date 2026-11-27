using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Tenancy;

/// <summary>
/// P1-06 (revisão do Codex, Etapa 02): duas retomadas concorrentes do mesmo provisionamento. Uma conclui, a empresa é suspensa e a segunda, atrasada, sobrescrevia a situação com Active (a entidade rastreada ainda dizia
/// "Provisioning"). A conclusão agora é uma transição condicional e atômica no banco: só Provisioning vira Active. PostgreSQL real, sincronização determinística (nenhum atraso arbitrário).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProvisionamentoConcorrenteTests(PostgresFixture banco)
{
    private static readonly PermissionGrant[] Permissoes =
    [
        new(Permission.PlatformTenantCreate, null),
        new(Permission.PlatformTenantSuspend, null),
        new(Permission.PlatformTenantRead, null),
    ];

    private async Task<TenantInfo> PlataformaAsync()
    {
        await using var escopo = banco.NovoEscopo();
        return await escopo.ServiceProvider.GetRequiredService<TenantAdministrationService>().EnsurePlatformTenantAsync("Plataforma BImeta (teste)");
    }

    private AsyncServiceScope Operador(TenantInfo plataforma) =>
        banco.NovoEscopo(PostgresFixture.ContextoDeUsuario(plataforma, Guid.NewGuid(), Permissoes));

    private static string Slug() => $"provconc-{Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task Retomada_atrasada_nao_sobrescreve_a_suspensao_nem_reativa_a_empresa()
    {
        var plataforma = await PlataformaAsync();
        var slug = Slug();

        // 1. Duas retomadas começam: ambas leem a empresa em PROVISIONAMENTO (a segunda guarda a entidade rastreada no seu escopo).
        await using var segunda = Operador(plataforma);
        var servicoDaSegunda = segunda.ServiceProvider.GetRequiredService<TenantAdministrationService>();
        var (empresa, criada) = await servicoDaSegunda.RegisterAsync(slug, "Empresa concorrente", null, null, null, null);
        Assert.True(criada);
        Assert.Equal("Provisioning", empresa.Status);

        await using (var primeira = Operador(plataforma))
        {
            var servicoDaPrimeira = primeira.ServiceProvider.GetRequiredService<TenantAdministrationService>();
            var (retomada, criadaDeNovo) = await servicoDaPrimeira.RegisterAsync(slug, "Empresa concorrente", null, null, null, null);
            Assert.False(criadaDeNovo); // é uma retomada

            // 2. A primeira conclui.
            Assert.Equal("Active", (await servicoDaPrimeira.CompleteProvisioningAsync(slug)).Status);

            // 3. A empresa é suspensa.
            Assert.Equal("Suspended", (await servicoDaPrimeira.SuspendAsync(slug, "suspensao entre as retomadas")).Status);
            Assert.Equal(retomada.TenantId, empresa.TenantId);
        }

        // 4. A conclusão ATRASADA da segunda é liberada (ela ainda tem a entidade lida como Provisioning).
        var resultadoAtrasado = await servicoDaSegunda.CompleteProvisioningAsync(slug);

        // 5. A empresa permanece suspensa (nem a resposta da retomada atrasada nem o banco mostram Active).
        Assert.Equal("Suspended", resultadoAtrasado.Status);
        await using var leitura = Operador(plataforma);
        var atual = await leitura.ServiceProvider.GetRequiredService<TenantAdministrationService>().GetAsync(slug);
        Assert.Equal("Suspended", atual.Status);
        Assert.NotNull(atual.SuspendedAt);

        // 6. Sem reativação indevida: a situação "Operacional" do gate segue negativa e a marca de reativação não existe.
        await using var gate = banco.NovoEscopo();
        var estado = await gate.ServiceProvider.GetRequiredService<ITenantOperationGate>().GetAsync(new TenantId(empresa.TenantId));
        Assert.False(estado.IsOperational);
        Assert.Null(estado.OperationalSince);

        // O histórico tem só a suspensão (nenhuma reativação fantasma).
        var historico = await leitura.ServiceProvider.GetRequiredService<TenantAdministrationService>().GetStatusHistoryAsync(slug);
        var unica = Assert.Single(historico);
        Assert.Equal("Suspended", unica.ToStatus);

        // A retomada atrasada não gerou um segundo evento de conclusão.
        Assert.Equal(1, await EventosDeConclusaoAsync(empresa.TenantId));
    }

    [Fact]
    public async Task Conclusao_repetida_e_idempotente_e_audita_uma_vez()
    {
        var plataforma = await PlataformaAsync();
        var slug = Slug();
        await using var escopo = Operador(plataforma);
        var servico = escopo.ServiceProvider.GetRequiredService<TenantAdministrationService>();
        await servico.RegisterAsync(slug, "Empresa idempotente", null, null, null, null);

        Assert.Equal("Active", (await servico.CompleteProvisioningAsync(slug)).Status);
        Assert.Equal("Active", (await servico.CompleteProvisioningAsync(slug)).Status);

        await using var gate = banco.NovoEscopo();
        var empresa = await servico.GetAsync(slug);
        var estado = await gate.ServiceProvider.GetRequiredService<ITenantOperationGate>().GetAsync(new TenantId(empresa.TenantId));
        Assert.True(estado.IsOperational);

        // "Audita uma vez": a conclusão repetida NÃO gera um segundo evento de auditoria (conta de verdade no banco).
        Assert.Equal(1, await EventosDeConclusaoAsync(empresa.TenantId));
    }

    private async Task<long> EventosDeConclusaoAsync(Guid tenantId)
    {
        await using var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var consulta = new Npgsql.NpgsqlCommand("SELECT count(*) FROM audit.audit_events WHERE operation = 'platform.tenant.provisioning-complete' AND resource_id = @r", conexao);
        consulta.Parameters.AddWithValue("r", tenantId.ToString());
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Conclusao_nunca_reativa_uma_empresa_suspensa_nem_toca_o_tenant_tecnico()
    {
        var plataforma = await PlataformaAsync();
        var slug = Slug();
        await using var escopo = Operador(plataforma);
        var servico = escopo.ServiceProvider.GetRequiredService<TenantAdministrationService>();
        await servico.RegisterAsync(slug, "Empresa suspensa", null, null, null, null);
        await servico.CompleteProvisioningAsync(slug);
        await servico.SuspendAsync(slug, "motivo da suspensao");

        Assert.Equal("Suspended", (await servico.CompleteProvisioningAsync(slug)).Status);

        await using var repo = banco.NovoEscopo();
        var tecnico = await repo.ServiceProvider.GetRequiredService<ITenantRepository>().TryCompleteProvisioningAsync(plataforma.TenantId, DateTimeOffset.UtcNow, default);
        Assert.False(tecnico);
    }
}
