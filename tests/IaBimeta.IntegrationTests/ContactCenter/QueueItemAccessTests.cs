using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// O acesso às conversas em atendimento é limitado por tenant, fila, unidade e papel (TEN-1, TEN-2 e TEN-3). Estar em uma fila não
/// dá acesso irrestrito a outras filas, unidades ou conversas do tenant, e o acesso à conversa pelo ContactCenter não abre o
/// Conversations: o atendente continua sem permissão direta lá. O que o usuário não enxerga responde como inexistente; o que ele
/// enxerga e não pode fazer é negado e auditado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueItemAccessTests(PostgresFixture banco)
{
    [Fact]
    public async Task O_atendente_so_alcanca_os_itens_das_filas_em_que_esta_e_o_resto_responde_como_inexistente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acfilas");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var itemA = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        var itemB = await c.NaFilaAsync(await c.NovaConversaAsync(), filaB.Id);

        // Do item da fila dela, a Ana vê tudo o que pode; do item da outra fila, nada.
        Assert.Equal(itemA.Id, (await c.ItemAsync(itemA.Id, ana.Contexto))!.Id);
        Assert.Null(await c.ItemAsync(itemB.Id, ana.Contexto));
        Assert.Null(await c.HistoricoDoItemAsync(itemB.Id, ana.Contexto));
        Assert.Null(await c.ConversaDoItemAsync(itemB.Id, ana.Contexto));
        Assert.Null(await c.MensagensDoItemAsync(itemB.Id, ana.Contexto));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.AssumirAsync(itemB.Id, ana.Contexto));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.DevolverAIAAsync(itemB.Id, ana.Contexto));
        Assert.Equal(itemA.Id, Assert.Single(await c.ItensAsync(ana.Contexto)).Id);
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(itemB.Id))!.Status);
        Assert.Null((await c.SessaoAsync(itemB.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task Quem_nao_e_atendente_de_nenhuma_fila_nem_le_filas_nao_alcanca_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acnada");
        var fila = await c.CriarFilaAsync();
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var comum = await c.NovoAgenteAsync(banco);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ItensAsync(comum.Contexto));
        Assert.Null(await c.ItemAsync(item.Id, comum.Contexto));
        Assert.Null(await c.HistoricoDoItemAsync(item.Id, comum.Contexto));
        Assert.Null(await c.ConversaDoItemAsync(item.Id, comum.Contexto));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.AssumirAsync(item.Id, comum.Contexto));
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task Quem_le_as_filas_da_organizacao_ve_os_itens_mas_nao_opera_nem_le_o_conteudo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acleitor");
        var fila = await c.CriarFilaAsync();
        var leitor = c.LeitorDeFilas();
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);

        Assert.Equal(item.Id, Assert.Single(await c.ItensAsync(leitor)).Id);
        Assert.Equal(item.Id, (await c.ItemAsync(item.Id, leitor))!.Id);
        Assert.Single((await c.HistoricoDoItemAsync(item.Id, leitor))!);

        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.AssumirAsync(item.Id, leitor));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.AtribuirAsync(item.Id, Guid.NewGuid(), leitor));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.DevolverAIAAsync(item.Id, leitor));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.ConversaDoItemAsync(item.Id, leitor));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.MensagensDoItemAsync(item.Id, leitor));
        // Cada tentativa de ler (a sessão e as mensagens) ficou na trilha como negada, com o autor.
        var negadas = await c.AuditoriaAsync("contact-center.item.read-conversation", item.Id.ToString());
        Assert.Equal(2, negadas.Count);
        Assert.All(negadas, e => Assert.Equal((AuditOutcome.Denied, leitor.Principal!.UserId), (e.Outcome, e.ActorUserId)));
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task O_conteudo_da_conversa_e_de_quem_e_responsavel_do_supervisor_e_dos_atendentes_enquanto_ela_espera()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acconteudo");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var sessao = (await c.EntrarAsync(ContactCenterTestSupport.Unico("contato"), texto: "Preciso ficar em jejum?")).SessionId;
        var item = await c.NaFilaAsync(sessao, fila.Id);

        // Esperando na fila: qualquer atendente dela pode ler (poderia assumir de qualquer modo).
        var esperando = await c.MensagensDoItemAsync(item.Id, beto.Contexto);
        Assert.Equal("Preciso ficar em jejum?", Assert.Single(esperando!.Items).Text);
        Assert.Equal(sessao, (await c.ConversaDoItemAsync(item.Id, beto.Contexto))!.Id);

        await c.AssumirAsync(item.Id, ana.Contexto);

        // Depois que a Ana assumiu, o Beto continua enxergando o item, mas não o conteúdo: negado e auditado.
        Assert.Equal(ana.Id, (await c.ItemAsync(item.Id, beto.Contexto))!.AssignedUserId);
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.ConversaDoItemAsync(item.Id, beto.Contexto));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.MensagensDoItemAsync(item.Id, beto.Contexto));
        var negadas = (await c.AuditoriaAsync("contact-center.item.read-conversation", item.Id.ToString())).Where(e => e.ActorUserId == beto.Id).ToList();
        Assert.Equal(2, negadas.Count);
        Assert.All(negadas, e => Assert.Equal(AuditOutcome.Denied, e.Outcome));

        Assert.Equal("Preciso ficar em jejum?", Assert.Single((await c.MensagensDoItemAsync(item.Id, ana.Contexto))!.Items).Text);
        Assert.Equal(sessao, (await c.ConversaDoItemAsync(item.Id, supervisor.Contexto))!.Id);
    }

    [Fact]
    public async Task O_acesso_pela_fila_nao_abre_o_Conversations_o_atendente_continua_sem_permissao_direta_la()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acdireto");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        // Mesmo sendo a responsável, a Ana não lê nem muda a conversa pelo Conversations: o acesso dela é o da fila.
        Assert.Null(await c.SessaoAsync(sessao, ana.Contexto));
        Assert.Null(await c.HistoricoAsync(sessao, contexto: ana.Contexto));
        Assert.Equal(TransitionOutcome.SessionNotFound, (await c.TransicionarAsync(sessao, HandlingMode.AiAutonomous, null, await c.VersaoDaSessaoAsync(sessao), contexto: ana.Contexto)).Outcome);
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ListarSessoesAsync(ana.Contexto));
        Assert.Equal(HandlingMode.HumanAssisted, (await c.SessaoAsync(sessao))!.HandlingMode);
    }

    [Fact]
    public async Task Equipe_compartilhada_um_atendente_de_outra_organizacao_atende_so_a_fila_em_que_foi_colocado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acequipe");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var compartilhada = await c.CriarFilaAsync("Compartilhada");
        var restrita = await c.CriarFilaAsync("Restrita");
        var visitante = await c.AgenteNaFilaAsync(banco, compartilhada.Id, organizacaoDoUsuario: outra.Id);
        var itemCompartilhado = await c.NaFilaAsync(await c.NovaConversaAsync(), compartilhada.Id);
        var itemRestrito = await c.NaFilaAsync(await c.NovaConversaAsync(), restrita.Id);

        var assumida = await c.AssumirAsync(itemCompartilhado.Id, visitante.Contexto);

        Assert.Equal((OperationOutcome.Completed, visitante.Id), (assumida.Outcome, assumida.Item.AssignedUserId));
        Assert.Equal(itemCompartilhado.SessionId, (await c.ConversaDoItemAsync(itemCompartilhado.Id, visitante.Contexto))!.Id);
        Assert.Null(await c.ItemAsync(itemRestrito.Id, visitante.Contexto));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.AssumirAsync(itemRestrito.Id, visitante.Contexto));
        Assert.Equal(itemCompartilhado.Id, Assert.Single(await c.ItensAsync(visitante.Contexto)).Id);
    }

    [Fact]
    public async Task Outro_tenant_nao_ve_nem_opera_o_item_nem_com_o_mesmo_usuario_de_membro()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "actenanta");
        var outro = await CenarioDeConversas.CriarAsync(banco, "actenantb");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);

        // Um contexto do outro tenant, até com o identificador da Ana, não alcança nada: a participação dela é deste tenant.
        var impostora = PostgresFixture.ContextoDeUsuario(outro.Tenant, ana.Id, new PermissionGrant(Permission.OrganizationRead, outro.OrganizacaoId));

        Assert.Null(await outro.ItemAsync(item.Id));
        Assert.Null(await outro.ItemAsync(item.Id, impostora));
        Assert.Null(await outro.HistoricoDoItemAsync(item.Id));
        Assert.Null(await outro.ConversaDoItemAsync(item.Id));
        Assert.Empty(await outro.ItensAsync());
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.AssumirAsync(item.Id, impostora));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.DevolverAIAAsync(item.Id, outro.ContextoDeSistema));
        Assert.Equal((QueueItemStatus.Waiting, fila.Id), ((await c.ItemAsync(item.Id))!.Status, (await c.ItemAsync(item.Id))!.QueueId));
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_toda_operacao_de_itens_falha_fechada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acsemctx");
        var fila = await c.CriarFilaAsync();
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var pedido = Guid.NewGuid();

        Task<T> Sem<T>(Func<QueueItemService, Task<T>> acao) => c.ComoAsync(null, sp => acao(sp.GetRequiredService<QueueItemService>()));

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.GetAsync(item.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ListAsync(null, null, false, 10)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.GetHistoryAsync(item.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.GetConversationAsync(item.Id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.GetMessagesAsync(item.Id, 10, null, HistoryOrder.OldestFirst)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ClaimAsync(item.Id, pedido)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.AssignAsync(item.Id, Guid.NewGuid(), pedido)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ReleaseAsync(item.Id, pedido)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.TransferAsync(item.Id, fila.Id, null, pedido)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ReturnToAiAsync(item.Id, pedido)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => Sem(s => s.ReconcilePendingAsync(TimeSpan.Zero, 10)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, sp => sp.GetRequiredService<IQueueItemRepository>().FindItemReadOnlyAsync(item.Id, default)));
    }

    [Fact]
    public async Task A_lista_respeita_fila_situacao_e_o_so_meus_e_o_tamanho_da_pagina_e_limitado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "aclista");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        await c.AdicionarAtendenteAsync(filaB.Id, ana.Id);
        var gestor = c.GestorDeFilas();
        var primeiro = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        var segundo = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        var terceiro = await c.NaFilaAsync(await c.NovaConversaAsync(), filaB.Id);
        await c.AssumirAsync(segundo.Id, ana.Contexto);
        await c.DevolverAIAAsync(terceiro.Id, c.ContextoDeSistema);

        Assert.Equal(new[] { primeiro.Id, segundo.Id }, (await c.ItensAsync(ana.Contexto)).Select(i => i.Id).ToArray());          // do mais antigo para o mais novo; sem os encerrados
        Assert.Equal(new[] { primeiro.Id, segundo.Id }, (await c.ItensAsync(ana.Contexto, fila: filaA.Id)).Select(i => i.Id).ToArray());
        Assert.Empty(await c.ItensAsync(ana.Contexto, fila: filaB.Id));
        Assert.Equal(segundo.Id, Assert.Single(await c.ItensAsync(ana.Contexto, minhas: true)).Id);
        Assert.Equal(primeiro.Id, Assert.Single(await c.ItensAsync(ana.Contexto, situacao: QueueItemStatus.Waiting)).Id);
        Assert.Equal(terceiro.Id, Assert.Single(await c.ItensAsync(ana.Contexto, situacao: QueueItemStatus.Closed)).Id);
        Assert.Equal(2, (await c.ItensAsync(gestor)).Count);                                                                        // o gestor também não vê o encerrado por padrão
        Assert.Single(await c.ItensAsync(ana.Contexto, take: 1));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ItensAsync(ana.Contexto, take: 0));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ItensAsync(ana.Contexto, take: QueueItemService.MaxPageSize + 1));
        Assert.Empty(await c.ItensAsync(c.ContextoDeSistema, fila: Guid.NewGuid()));
    }

    [Fact]
    public async Task O_atendente_lista_as_proprias_filas_sem_precisar_ler_as_filas_da_organizacao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acminhas");
        var filaA = await c.CriarFilaAsync("Fila A");
        await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ListarFilasAsync(ana.Contexto));
        var minhas = await c.ComoAsync(ana.Contexto, sp => sp.GetRequiredService<ServiceQueueService>().ListMineAsync());

        Assert.Equal(filaA.Id, Assert.Single(minhas).Id);
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ServiceQueueService>().ListMineAsync()));
    }

    [Fact]
    public async Task O_gestor_das_filas_supervisiona_sem_ser_membro_e_o_supervisor_da_fila_so_a_fila_dele()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acgestor");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var supervisorDaA = await c.AgenteNaFilaAsync(banco, filaA.Id, QueueMemberRole.Supervisor);
        var gestor = await c.NovoAgenteAsync(banco, papel: Role.OrganizationAdmin);
        var itemA = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        var itemB = await c.NaFilaAsync(await c.NovaConversaAsync(), filaB.Id);
        await c.AssumirAsync(itemA.Id, ana.Contexto);

        // O supervisor da A devolve a conversa da Ana à fila, mas não alcança nada da B.
        Assert.Equal(QueueItemStatus.Waiting, (await c.DevolverAFilaAsync(itemA.Id, supervisorDaA.Contexto)).Item.Status);
        Assert.Null(await c.ItemAsync(itemB.Id, supervisorDaA.Contexto));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.DevolverAIAAsync(itemB.Id, supervisorDaA.Contexto));

        // O gestor da organização opera nas duas, sem ser atendente de nenhuma.
        var atribuida = await c.AtribuirAsync(itemA.Id, ana.Id, gestor.Contexto);
        Assert.Equal(ana.Id, atribuida.Item.AssignedUserId);
        Assert.Equal(QueueItemStatus.Closed, (await c.DevolverAIAAsync(itemB.Id, gestor.Contexto)).Item.Status);
    }
}
