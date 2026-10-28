using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// As garantias do próprio banco, independentes do aplicativo (TEN-3 e DAD-2): restrições de domínio, nome único por organização,
/// chave estrangeira composta que carrega o tenant, nenhuma chave para outro esquema, e a trava do DbContext contra gravação em
/// outro tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactCenterDatabaseGuardsTests(PostgresFixture banco)
{
    private static string Texto(Guid? valor) => valor is null ? "null" : $"'{valor}'";

    private static string InserirFila(Guid tenant, Guid id, Guid organizacao, string nome, Guid? unidade = null, Guid? setor = null, int status = 1, long versao = 1) =>
        "insert into contact_center.service_queues (tenant_id, id, organization_id, business_unit_id, department_id, name, name_key, status, version, created_at, updated_at) " +
        $"values ('{tenant}', '{id}', '{organizacao}', {Texto(unidade)}, {Texto(setor)}, '{nome}', '{nome.ToLowerInvariant()}', {status}, {versao}, now(), now())";

    private static string InserirAtendente(Guid tenant, Guid fila, Guid usuario, int papel = 1) =>
        $"insert into contact_center.queue_memberships (tenant_id, queue_id, user_id, role, added_by, added_at) values ('{tenant}', '{fila}', '{usuario}', {papel}, null, now())";

    private static Task<int> Executar(CenarioDeConversas c, string sql) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database.ExecuteSqlRawAsync(sql));

    private static async Task<PostgresException> FalhaDoBancoAsync(Task<int> operacao)
    {
        var falha = await Assert.ThrowsAnyAsync<Exception>(() => operacao);
        var atual = falha;
        while (atual is not null and not PostgresException)
        {
            atual = atual.InnerException;
        }

        return Assert.IsType<PostgresException>(atual);
    }

    [Theory]
    [InlineData(0, 1L, "ck_service_queues_status")]
    [InlineData(3, 1L, "ck_service_queues_status")]
    [InlineData(1, 0L, "ck_service_queues_version")]
    public async Task O_banco_recusa_fila_com_situacao_ou_versao_invalidas(int status, long versao, string restricao)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbfilaregra");

        var falha = await FalhaDoBancoAsync(Executar(c, InserirFila(c.Tenant.TenantId.Value, Guid.NewGuid(), c.OrganizacaoId, "Fila", status: status, versao: versao)));

        Assert.Equal((PostgresErrorCodes.CheckViolation, restricao), (falha.SqlState, falha.ConstraintName));
    }

    [Fact]
    public async Task O_banco_recusa_setor_sem_unidade_e_papel_de_atendente_invalido()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbfilasetor");
        var tenant = c.Tenant.TenantId.Value;
        var fila = Guid.NewGuid();
        await Executar(c, InserirFila(tenant, fila, c.OrganizacaoId, "Fila valida"));

        var semUnidade = await FalhaDoBancoAsync(Executar(c, InserirFila(tenant, Guid.NewGuid(), c.OrganizacaoId, "Fila b", setor: Guid.NewGuid())));
        var papelInvalido = await FalhaDoBancoAsync(Executar(c, InserirAtendente(tenant, fila, Guid.NewGuid(), papel: 9)));

        Assert.Equal("ck_service_queues_department", semUnidade.ConstraintName);
        Assert.Equal("ck_queue_memberships_role", papelInvalido.ConstraintName);
    }

    [Fact]
    public async Task O_banco_garante_nome_unico_por_organizacao_sem_diferenciar_maiusculas_e_permite_repetir_em_outra()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbfilanome");
        var tenant = c.Tenant.TenantId.Value;
        await Executar(c, InserirFila(tenant, Guid.NewGuid(), c.OrganizacaoId, "Triagem"));

        var duplicada = await FalhaDoBancoAsync(Executar(c, InserirFila(tenant, Guid.NewGuid(), c.OrganizacaoId, "TRIAGEM")));
        var outraOrganizacao = await Executar(c, InserirFila(tenant, Guid.NewGuid(), Guid.NewGuid(), "Triagem"));

        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_service_queues_organization_name"), (duplicada.SqlState, duplicada.ConstraintName));
        Assert.Equal(1, outraOrganizacao);
    }

    [Fact]
    public async Task A_chave_estrangeira_composta_recusa_atendente_ligado_a_fila_de_outro_tenant()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbfilafka");
        var outro = await CenarioDeConversas.CriarAsync(banco, "dbfilafkb");
        var filaDoOutro = Guid.NewGuid();
        await Executar(outro, InserirFila(outro.Tenant.TenantId.Value, filaDoOutro, outro.OrganizacaoId, "Fila do outro"));

        // O tenant do atendente é o de c, mas a fila é de outro tenant: a chave (tenant_id, queue_id) não existe.
        var falha = await FalhaDoBancoAsync(Executar(c, InserirAtendente(c.Tenant.TenantId.Value, filaDoOutro, Guid.NewGuid())));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
    }

    [Fact]
    public async Task Nenhuma_chave_estrangeira_do_modulo_aponta_para_outro_esquema()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbfilaesquemas");

        var chaves = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database
            .SqlQueryRaw<string>(
                "select distinct pk.table_schema::text as \"Value\" from information_schema.referential_constraints rc " +
                "join information_schema.table_constraints fk on fk.constraint_name = rc.constraint_name and fk.constraint_schema = rc.constraint_schema " +
                "join information_schema.table_constraints pk on pk.constraint_name = rc.unique_constraint_name and pk.constraint_schema = rc.unique_constraint_schema " +
                "where fk.table_schema = 'contact_center'")
            .ToListAsync());

        Assert.Equal(new[] { "contact_center" }, chaves); // só a chave da fila no próprio esquema: o conjunto não é vazio
    }

    [Fact]
    public async Task O_DbContext_recusa_gravar_fila_de_outro_tenant_antes_de_chegar_ao_banco()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbfilatrava");
        var outro = await CenarioDeConversas.CriarAsync(banco, "dbfilatravab");
        var deOutroTenant = ServiceQueue.Create(outro.Tenant.TenantId, outro.OrganizacaoId, null, null, "Fila intrusa", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ContactCenterDbContext>();
            db.Queues.Add(deOutroTenant);
            return await db.SaveChangesAsync();
        }));

        Assert.Null(await outro.LerFilaAsync(deOutroTenant.Id));
    }
}
