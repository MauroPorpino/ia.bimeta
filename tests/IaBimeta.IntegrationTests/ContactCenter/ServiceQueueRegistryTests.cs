using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// O cadastro das filas de atendimento e dos atendentes de cada fila, com PostgreSQL real: criação, vínculo com unidade e setor,
/// nome único por organização, pausa e retomada, entrada, troca de papel e saída de atendentes, tudo idempotente e auditado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ServiceQueueRegistryTests(PostgresFixture banco)
{
    [Fact]
    public async Task O_gestor_cria_a_fila_da_organizacao_ela_nasce_ativa_e_a_criacao_e_auditada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filacria");
        var gestor = c.GestorDeFilas();

        var fila = await c.CriarFilaAsync("Coleta domiciliar", contexto: gestor);

        Assert.Equal(("Coleta domiciliar", QueueStatus.Active, 1L, c.OrganizacaoId), (fila.Name, fila.Status, fila.Version, fila.OrganizationId));
        Assert.Null(fila.BusinessUnitId);
        Assert.Null(fila.DepartmentId);
        var lida = await c.LerFilaAsync(fila.Id, gestor);
        Assert.Equal((fila.Id, fila.Name, fila.Status, fila.Version), (lida!.Id, lida.Name, lida.Status, lida.Version));
        var auditoria = Assert.Single(await c.AuditoriaAsync("contact-center.queue.create", fila.Id.ToString()));
        Assert.Equal((AuditOutcome.Success, gestor.Principal!.UserId), (auditoria.Outcome, auditoria.ActorUserId));
    }

    [Fact]
    public async Task A_fila_pode_se_restringir_a_uma_unidade_e_a_um_setor_dela_e_o_vinculo_e_validado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filavinculo");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var setor = await c.CriarSetorAsync(matriz, "Recepção");
        var unidadeDaOutra = await c.CriarUnidadeAsync(outra.Id, "Filial da outra", matriz: true);

        var completa = await c.CriarFilaAsync("Recepção da matriz", unidade: matriz, setor: setor);
        var soUnidade = await c.CriarFilaAsync("Filial inteira", unidade: filial);

        Assert.Equal((matriz, setor), (completa.BusinessUnitId, completa.DepartmentId));
        Assert.Equal((filial, (Guid?)null), (soUnidade.BusinessUnitId, soUnidade.DepartmentId));
        // Unidade de outra organização, inexistente, setor sem unidade e setor de outra unidade: todos recusados (400), sem gravar.
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarFilaAsync("Fila b", unidade: unidadeDaOutra));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarFilaAsync("Fila c", unidade: Guid.NewGuid()));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarFilaAsync("Fila d", setor: setor));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.CriarFilaAsync("Fila e", unidade: filial, setor: setor));
        Assert.Equal(2, (await c.ListarFilasAsync()).Count);
    }

    [Fact]
    public async Task O_nome_e_unico_por_organizacao_sem_diferenciar_maiusculas_mas_pode_repetir_em_outra_organizacao_e_em_outro_tenant()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filanome");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var outroTenant = await CenarioDeConversas.CriarAsync(banco, "filanomeoutro");
        await c.CriarFilaAsync("Triagem");

        await Assert.ThrowsAsync<QueueNameInUseException>(() => c.CriarFilaAsync("  TRIAGEM  "));
        var naOutraOrganizacao = await c.CriarFilaAsync("Triagem", organizacao: outra.Id);
        var noOutroTenant = await outroTenant.CriarFilaAsync("Triagem");

        Assert.Equal(outra.Id, naOutraOrganizacao.OrganizationId);
        Assert.Equal("Triagem", noOutroTenant.Name);
        Assert.Equal(1, (await c.ListarFilasAsync()).Count(f => f.OrganizationId == c.OrganizacaoId));
    }

    [Fact]
    public async Task Duas_criacoes_simultaneas_do_mesmo_nome_deixam_uma_so_fila_e_a_outra_recebe_conflito()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filacorrida");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                await c.CriarFilaAsync("Fila disputada");
                return true;
            }
            catch (QueueNameInUseException)
            {
                return false;
            }
        }));

        Assert.Equal(1, resultados.Count(criada => criada));
        Assert.Single(await c.ListarFilasAsync());
    }

    [Fact]
    public async Task Pausar_e_retomar_sao_idempotentes_e_a_auditoria_registra_so_a_mudanca()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filapausa");
        var gestor = c.GestorDeFilas();
        var fila = await c.CriarFilaAsync(contexto: gestor);

        var pausada = await c.PausarAsync(fila.Id, gestor);
        var repetida = await c.PausarAsync(fila.Id, gestor);
        var retomada = await c.RetomarAsync(fila.Id, gestor);
        var retomadaDeNovo = await c.RetomarAsync(fila.Id, gestor);

        Assert.Equal((QueueStatus.Paused, 2L), (pausada.Status, pausada.Version));
        Assert.Equal((QueueStatus.Paused, 2L), (repetida.Status, repetida.Version));
        Assert.Equal((QueueStatus.Active, 3L), (retomada.Status, retomada.Version));
        Assert.Equal((QueueStatus.Active, 3L), (retomadaDeNovo.Status, retomadaDeNovo.Version));
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.pause", fila.Id.ToString()));
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.resume", fila.Id.ToString()));
    }

    [Fact]
    public async Task O_atendente_entra_muda_de_papel_e_sai_de_forma_idempotente_e_auditada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filamembro");
        var gestor = c.GestorDeFilas();
        var fila = await c.CriarFilaAsync(contexto: gestor);
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        var recurso = $"{fila.Id}:{usuario.Id}";

        var novo = await c.AdicionarAtendenteAsync(fila.Id, usuario.Id, QueueMemberRole.Agent, gestor);
        var repetido = await c.AdicionarAtendenteAsync(fila.Id, usuario.Id, QueueMemberRole.Agent, gestor);
        var promovido = await c.AdicionarAtendenteAsync(fila.Id, usuario.Id, QueueMemberRole.Supervisor, gestor);

        Assert.Equal((fila.Id, usuario.Id, QueueMemberRole.Agent, (Guid?)gestor.Principal!.UserId), (novo.QueueId, novo.UserId, novo.Role, novo.AddedBy));
        Assert.Equal(QueueMemberRole.Agent, repetido.Role);
        Assert.Equal(QueueMemberRole.Supervisor, promovido.Role);
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.member.add", recurso));   // a repetição não audita
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.member.role", recurso));
        var membro = Assert.Single((await c.AtendentesAsync(fila.Id, gestor))!);
        Assert.Equal((usuario.Id, QueueMemberRole.Supervisor), (membro.UserId, membro.Role));

        Assert.True(await c.RemoverAtendenteAsync(fila.Id, usuario.Id, gestor));
        Assert.False(await c.RemoverAtendenteAsync(fila.Id, usuario.Id, gestor));            // já saiu: nada a fazer, nada a auditar
        Assert.Empty((await c.AtendentesAsync(fila.Id, gestor))!);
        Assert.Single(await c.AuditoriaAsync("contact-center.queue.member.remove", recurso));
    }

    [Fact]
    public async Task Quem_entra_em_uma_fila_por_operacao_de_sistema_fica_sem_autor()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filasistema");
        var fila = await c.CriarFilaAsync();
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);

        var membro = await c.AdicionarAtendenteAsync(fila.Id, usuario.Id);

        Assert.Null(membro.AddedBy);
    }

    [Fact]
    public async Task O_atendente_precisa_existir_no_tenant_e_o_usuario_de_outro_tenant_responde_como_inexistente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filausuario");
        var outro = await CenarioDeConversas.CriarAsync(banco, "filausuariooutro");
        var fila = await c.CriarFilaAsync();
        var usuarioDoOutro = await banco.CriarUsuarioAsync(outro.Tenant, outro.OrganizacaoId, Role.User);

        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.AdicionarAtendenteAsync(fila.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.AdicionarAtendenteAsync(fila.Id, usuarioDoOutro.Id));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.AdicionarAtendenteAsync(fila.Id, Guid.Empty));
        Assert.Empty((await c.AtendentesAsync(fila.Id))!);
    }

    [Fact]
    public async Task Equipe_compartilhada_um_usuario_de_outra_organizacao_do_mesmo_tenant_pode_ser_atendente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filaequipe");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var fila = await c.CriarFilaAsync();
        var daOutra = await banco.CriarUsuarioAsync(c.Tenant, outra.Id, Role.User);

        var membro = await c.AdicionarAtendenteAsync(fila.Id, daOutra.Id, QueueMemberRole.Agent, c.GestorDeFilas());

        Assert.Equal(daOutra.Id, membro.UserId);
    }

    [Fact]
    public async Task A_lista_traz_so_as_filas_das_organizacoes_visiveis_em_ordem_de_nome()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "filalista");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var b = await c.CriarFilaAsync("B fila");
        var a = await c.CriarFilaAsync("A fila");
        var daOutra = await c.CriarFilaAsync("C fila da outra", organizacao: outra.Id);

        var soDaPrimeira = await c.ListarFilasAsync(c.LeitorDeFilas());
        var doTenant = await c.ListarFilasAsync(c.Usuario(null, Permission.QueueRead));

        Assert.Equal(new[] { a.Id, b.Id }, soDaPrimeira.Select(f => f.Id));
        Assert.Equal(new[] { a.Id, b.Id, daOutra.Id }, doTenant.Select(f => f.Id));
    }
}
