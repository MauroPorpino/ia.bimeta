using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Audit.Domain;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// Trilha de auditoria com PostgreSQL real: isolamento por tenant, falha fechada sem contexto e trilha append-only
/// garantida pelo banco (gatilhos). As tentativas de alterar ou excluir rodam dentro de transação sempre desfeita,
/// para que nada seja perdido mesmo que o gatilho falhasse.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuditPersistenceTests(PostgresFixture banco)
{
    private async Task RegistrarAsync(TenantInfo tenant, string operacao, string? recurso = null, AuditOutcome resultado = AuditOutcome.Success)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        await escopo.ServiceProvider.GetRequiredService<IAuditWriter>()
            .WriteAsync(new AuditEntry(operacao, "teste", recurso, resultado));
    }

    private async Task<IReadOnlyList<AuditEventView>> ListarAsync(TenantInfo tenant, int? take = null)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        return await escopo.ServiceProvider.GetRequiredService<AuditQueryService>().ListAsync(take);
    }

    [Fact]
    public async Task Evento_persistido_guarda_tenant_ator_operacao_recurso_resultado_e_data()
    {
        var tenant = await banco.RegistrarTenantAsync("audit");
        var usuario = Guid.NewGuid();
        var contexto = PostgresFixture.ContextoDeUsuario(tenant, usuario, new PermissionGrant(Permission.AuditRead, null));
        var antes = DateTimeOffset.UtcNow.AddSeconds(-5);

        await using (var escopo = banco.NovoEscopo(contexto))
        {
            await escopo.ServiceProvider.GetRequiredService<IAuditWriter>()
                .WriteAsync(new AuditEntry("tenancy.organization.create", "organization", "abc-123", AuditOutcome.Success));
        }

        await using var consulta = banco.NovoEscopo(contexto);
        var evento = Assert.Single(await consulta.ServiceProvider.GetRequiredService<AuditQueryService>().ListAsync());
        Assert.Equal(usuario, evento.ActorUserId);
        Assert.Equal("teste-integracao", evento.ActorOrigin);
        Assert.Equal("tenancy.organization.create", evento.Operation);
        Assert.Equal("organization", evento.ResourceType);
        Assert.Equal("abc-123", evento.ResourceId);
        Assert.Equal(AuditOutcome.Success, evento.Outcome);
        Assert.InRange(evento.OccurredAt, antes, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Cada_tenant_so_ve_a_propria_trilha()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        await RegistrarAsync(a, "op.de.a");
        await RegistrarAsync(a, "op.de.a.de.novo");
        await RegistrarAsync(b, "op.de.b");

        Assert.Equal(new[] { "op.de.a", "op.de.a.de.novo" }, (await ListarAsync(a)).Select(e => e.Operation).OrderBy(o => o));
        Assert.Equal(new[] { "op.de.b" }, (await ListarAsync(b)).Select(e => e.Operation));
    }

    [Fact]
    public async Task A_trilha_sai_da_mais_recente_para_a_mais_antiga_e_respeita_o_limite()
    {
        var tenant = await banco.RegistrarTenantAsync("ordem");
        foreach (var operacao in new[] { "op.um", "op.dois", "op.tres" })
        {
            await RegistrarAsync(tenant, operacao);
            await Task.Delay(5);
        }

        Assert.Equal(new[] { "op.tres", "op.dois", "op.um" }, (await ListarAsync(tenant)).Select(e => e.Operation));
        Assert.Equal(new[] { "op.tres", "op.dois" }, (await ListarAsync(tenant, take: 2)).Select(e => e.Operation));
    }

    [Fact]
    public async Task Filtros_por_operacao_e_recurso()
    {
        var tenant = await banco.RegistrarTenantAsync("filtro");
        await RegistrarAsync(tenant, "op.x", "recurso-1");
        await RegistrarAsync(tenant, "op.x", "recurso-2");
        await RegistrarAsync(tenant, "op.y", "recurso-1");

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var consulta = escopo.ServiceProvider.GetRequiredService<AuditQueryService>();

        Assert.Equal(2, (await consulta.ListAsync(operation: "op.x")).Count);
        Assert.Equal(2, (await consulta.ListAsync(resourceId: "recurso-1")).Count);
        Assert.Single(await consulta.ListAsync(operation: "op.x", resourceId: "recurso-2"));
    }

    [Fact]
    public async Task Evento_anterior_ao_contexto_vai_para_o_tenant_informado_com_a_origem_authentication()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        var usuario = Guid.NewGuid();

        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<IAuditWriter>()
                .WriteForAuthenticationAsync(a.TenantId, usuario, new AuditEntry("identity.login", "user", usuario.ToString(), AuditOutcome.Denied));
        }

        var deA = Assert.Single(await ListarAsync(a));
        Assert.Equal(AuditWriter.AuthenticationOrigin, deA.ActorOrigin);
        Assert.Equal(usuario, deA.ActorUserId);
        Assert.Empty(await ListarAsync(b));
    }

    [Fact]
    public async Task Evento_de_autenticacao_nao_e_gravado_em_tenant_diferente_do_contexto_estabelecido()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(b));
        var escritor = escopo.ServiceProvider.GetRequiredService<IAuditWriter>();

        await Assert.ThrowsAsync<CrossTenantAccessException>(() =>
            escritor.WriteForAuthenticationAsync(a.TenantId, null, new AuditEntry("identity.login", "user", null, AuditOutcome.Denied)));
        Assert.Empty(await ListarAsync(a));
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_o_registro_e_a_consulta_falham_fechados()
    {
        await using var escopo = banco.NovoEscopo();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() =>
            escopo.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry("op.x", "teste", null, AuditOutcome.Success)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => escopo.ServiceProvider.GetRequiredService<AuditQueryService>().ListAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => escopo.ServiceProvider.GetRequiredService<AuditDbContext>().Events.ToListAsync());
    }

    [Fact]
    public async Task Gravar_evento_em_nome_de_outro_tenant_e_bloqueado_antes_do_banco()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b));
        var dbB = escopoB.ServiceProvider.GetRequiredService<AuditDbContext>();
        dbB.Events.Add(AuditEvent.Create(a.TenantId, null, "teste", new AuditEntry("op.intrusa", "teste", null, AuditOutcome.Success), DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        Assert.Empty(await ListarAsync(a));
    }

    [Fact]
    public async Task Usuario_sem_permissao_de_auditoria_nao_consulta_a_trilha_nem_com_dados_existentes()
    {
        var tenant = await banco.RegistrarTenantAsync("sempermissao");
        await RegistrarAsync(tenant, "op.x");
        var semPermissao = PostgresFixture.ContextoDeUsuario(tenant, Guid.NewGuid(), new PermissionGrant(Permission.OrganizationRead, null));

        await using var escopo = banco.NovoEscopo(semPermissao);

        await Assert.ThrowsAsync<AccessDeniedException>(() => escopo.ServiceProvider.GetRequiredService<AuditQueryService>().ListAsync());
    }

    // Append-only garantido pelo banco

    [Fact]
    public async Task Banco_recusa_alterar_um_evento_ja_gravado()
    {
        var tenant = await banco.RegistrarTenantAsync("update");
        await RegistrarAsync(tenant, "op.original");

        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<AuditDbContext>();
        await using var transacao = await db.Database.BeginTransactionAsync();

        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("update audit.audit_events set operation = 'op.adulterada' where tenant_id = {0}", tenant.TenantId.Value));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, falha.SqlState);
        await transacao.RollbackAsync();
        Assert.Equal("op.original", Assert.Single(await ListarAsync(tenant)).Operation);
    }

    [Fact]
    public async Task Banco_recusa_excluir_um_evento_ja_gravado()
    {
        var tenant = await banco.RegistrarTenantAsync("delete");
        await RegistrarAsync(tenant, "op.permanente");

        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<AuditDbContext>();
        await using var transacao = await db.Database.BeginTransactionAsync();

        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("delete from audit.audit_events where tenant_id = {0}", tenant.TenantId.Value));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, falha.SqlState);
        await transacao.RollbackAsync();
        Assert.Single(await ListarAsync(tenant));
    }

    [Fact]
    public async Task Banco_recusa_truncar_a_trilha()
    {
        var tenant = await banco.RegistrarTenantAsync("truncate");
        await RegistrarAsync(tenant, "op.permanente");

        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<AuditDbContext>();
        await using var transacao = await db.Database.BeginTransactionAsync();

        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("truncate table audit.audit_events"));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, falha.SqlState);
        await transacao.RollbackAsync();
        Assert.Single(await ListarAsync(tenant));
    }

    [Fact]
    public async Task Alterar_o_evento_pelo_modelo_do_aplicativo_tambem_e_recusado_pelo_banco()
    {
        var tenant = await banco.RegistrarTenantAsync("ef");
        await RegistrarAsync(tenant, "op.original");

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var db = escopo.ServiceProvider.GetRequiredService<AuditDbContext>();
        var evento = await db.Events.SingleAsync();
        db.Entry(evento).State = EntityState.Modified;

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.RestrictViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }

    [Fact]
    public async Task Excluir_o_evento_pelo_modelo_do_aplicativo_tambem_e_recusado_pelo_banco()
    {
        var tenant = await banco.RegistrarTenantAsync("efdel");
        await RegistrarAsync(tenant, "op.permanente");

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var db = escopo.ServiceProvider.GetRequiredService<AuditDbContext>();
        db.Events.Remove(await db.Events.SingleAsync());

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.RestrictViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }
}
