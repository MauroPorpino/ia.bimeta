using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// Autorização e isolamento das filas (TEN-1, TEN-2 e TEN-3), com PostgreSQL real: sem leitura o recurso responde como inexistente
/// (404); com leitura mas sem a permissão da ação, a operação é negada (403) e a negação entra na auditoria; o tenant é sempre o
/// do contexto; e a permissão de uma organização não vale em outra.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ServiceQueueAuthorizationTests(PostgresFixture banco)
{
    [Fact]
    public async Task Sem_permissao_de_leitura_a_fila_responde_como_inexistente_e_nada_vaza()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filasemleitura");
        var fila = await c.CriarFilaAsync();
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        await c.AdicionarAtendenteAsync(fila.Id, usuario.Id);
        var semLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead);

        Assert.Null(await c.LerFilaAsync(fila.Id, semLeitura));
        Assert.Null(await c.AtendentesAsync(fila.Id, semLeitura));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ListarFilasAsync(semLeitura));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.PausarAsync(fila.Id, semLeitura));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.AdicionarAtendenteAsync(fila.Id, usuario.Id, contexto: semLeitura));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.RemoverAtendenteAsync(fila.Id, usuario.Id, semLeitura));
        Assert.Equal(QueueStatus.Active, (await c.LerFilaAsync(fila.Id))!.Status);
        Assert.Single((await c.AtendentesAsync(fila.Id))!);
    }

    [Fact]
    public async Task Sem_ler_a_organizacao_nem_a_criacao_revela_que_ela_existe()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filasemorg");
        var semOrganizacao = c.Usuario(c.OrganizacaoId, Permission.QueueRead, Permission.QueueManage);

        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.CriarFilaAsync(contexto: semOrganizacao));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.CriarFilaAsync(organizacao: Guid.NewGuid()));
    }

    [Fact]
    public async Task Com_leitura_sem_gerir_toda_escrita_e_negada_nada_muda_e_a_negacao_e_auditada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filaleitor");
        var fila = await c.CriarFilaAsync();
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        await c.AdicionarAtendenteAsync(fila.Id, usuario.Id);
        var leitor = c.LeitorDeFilas();
        var autor = leitor.Principal!.UserId;

        Assert.Equal(fila.Id, (await c.LerFilaAsync(fila.Id, leitor))!.Id);                  // lê
        Assert.Single((await c.AtendentesAsync(fila.Id, leitor))!);                           // lê os atendentes
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.CriarFilaAsync("Outra fila", contexto: leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.PausarAsync(fila.Id, leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.RetomarAsync(fila.Id, leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.AdicionarAtendenteAsync(fila.Id, usuario.Id, QueueMemberRole.Supervisor, leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.RemoverAtendenteAsync(fila.Id, usuario.Id, leitor));

        // Nada mudou.
        var depois = (await c.LerFilaAsync(fila.Id))!;
        Assert.Equal((QueueStatus.Active, 1L), (depois.Status, depois.Version));
        Assert.Equal(QueueMemberRole.Agent, Assert.Single((await c.AtendentesAsync(fila.Id))!).Role);
        Assert.Single(await c.ListarFilasAsync());
        // Cada negação ficou na trilha, com o autor.
        var recursoDoAtendente = $"{fila.Id}:{usuario.Id}";
        foreach (var (operacao, recurso) in new[]
        {
            ("contact-center.queue.create", c.OrganizacaoId.ToString()),
            ("contact-center.queue.pause", fila.Id.ToString()),
            ("contact-center.queue.resume", fila.Id.ToString()),
            ("contact-center.queue.member.add", recursoDoAtendente),
            ("contact-center.queue.member.remove", recursoDoAtendente),
        })
        {
            // O atendente entrou por operação de sistema antes: o mesmo recurso pode ter também o evento de sucesso.
            var negada = Assert.Single(await c.AuditoriaAsync(operacao, recurso), e => e.Outcome == AuditOutcome.Denied);
            Assert.Equal(autor, negada.ActorUserId);
        }
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_toda_operacao_falha_fechada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filasemcontexto");
        var fila = await c.CriarFilaAsync();
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);

        Task<T> Sem<T>(Func<ServiceQueueService, Task<T>> acao) => c.ComoAsync(null, sp => acao(sp.GetRequiredService<ServiceQueueService>()));

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.CreateAsync(c.OrganizacaoId, null, null, "Sem contexto")));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.GetAsync(fila.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ListAsync()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.PauseAsync(fila.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ResumeAsync(fila.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.AddMemberAsync(fila.Id, usuario.Id, QueueMemberRole.Agent)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.RemoveMemberAsync(fila.Id, usuario.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ListMembersAsync(fila.Id)));

        // O repositório também não é alcançado sem contexto: o filtro global falha fechado antes de qualquer linha.
        await Assert.ThrowsAsync<TenantContextRequiredException>(() =>
            c.ComoAsync(null, sp => sp.GetRequiredService<IQueueRepository>().FindQueueReadOnlyAsync(fila.Id, default)));
    }

    [Fact]
    public async Task Outro_tenant_nao_ve_nem_altera_a_fila_e_o_atendente_dele_nao_entra_nela()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filatenanta");
        var outro = await CenarioDeConversas.CriarAsync(banco, "filatenantb");
        var fila = await c.CriarFilaAsync();
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        await c.AdicionarAtendenteAsync(fila.Id, usuario.Id);

        // Nem com o contexto de sistema do outro tenant: a fila é de outro tenant e responde como inexistente.
        Assert.Null(await outro.LerFilaAsync(fila.Id));
        Assert.Null(await outro.AtendentesAsync(fila.Id));
        Assert.Empty(await outro.ListarFilasAsync());
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.PausarAsync(fila.Id));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.AdicionarAtendenteAsync(fila.Id, usuario.Id));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.RemoverAtendenteAsync(fila.Id, usuario.Id));
        Assert.Equal((QueueStatus.Active, 1), ((await c.LerFilaAsync(fila.Id))!.Status, (await c.AtendentesAsync(fila.Id))!.Count));
    }

    [Fact]
    public async Task A_permissao_de_uma_organizacao_nao_vale_em_outra_do_mesmo_tenant()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filaorgs");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var filaDaOutra = await c.CriarFilaAsync("Fila da outra", organizacao: outra.Id);
        var gestorDaPrimeira = c.GestorDeFilas(c.OrganizacaoId);

        Assert.Null(await c.LerFilaAsync(filaDaOutra.Id, gestorDaPrimeira));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.PausarAsync(filaDaOutra.Id, gestorDaPrimeira));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.CriarFilaAsync("Intrusa", organizacao: outra.Id, contexto: gestorDaPrimeira));
        Assert.DoesNotContain(await c.ListarFilasAsync(gestorDaPrimeira), f => f.OrganizationId == outra.Id);
    }
}
