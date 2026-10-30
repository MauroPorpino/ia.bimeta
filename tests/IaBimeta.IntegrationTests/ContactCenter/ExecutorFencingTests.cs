using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A exclusividade de quem conclui uma operação no Conversations não depende só da trava do banco: o executor leva um token de cerca,
/// validado por quem altera a sessão, e o que foi substituído nunca mais escreve. E a transferência composta (unidade e atendente)
/// interrompida no meio é concluída ou compensada de forma coerente, com rastro e auditoria. PostgreSQL real; os pontos de intercalação
/// são determinísticos (<see cref="Interceptador{T}"/>).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExecutorFencingTests(PostgresFixture banco)
{
    /// <summary>A trava que o executor ACHA que tem, mas que o banco já perdeu (a conexão que a segurava caiu): não garante exclusividade nenhuma.</summary>
    private sealed class TravaPerdida : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Task LiberarRecuoAsync(CenarioDeConversas c, Guid item) => c.ComoAsync(c.ContextoDeSistema, async sp =>
    {
        await sp.GetRequiredService<ContactCenterDbContext>().Database.ExecuteSqlAsync($"UPDATE contact_center.queue_items SET pending_next_attempt_at = NULL WHERE id = {item}");
        return 0;
    });

    // A04

    [Fact]
    public async Task O_executor_antigo_que_perdeu_a_trava_nao_aplica_a_intencao_antiga_mesmo_com_outra_conexao_e_lendo_o_estado_atual_da_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cerca");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var bia = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id); // a operação antiga: assumir para a Ana, pendente

        var antigo = await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var (portaDeItens, controleDeItens) = Interceptador<IQueueItemRepository>.Envolver(itens);

            // A conexão que segurava a trava do item se perdeu: o executor antigo acredita que continua exclusivo.
            controleDeItens.Substituir(nameof(IQueueItemRepository.TryLockItemAsync), _ => Task.FromResult<IAsyncDisposable?>(new TravaPerdida()));
            var (gateway, controleDoGateway) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());

            // O executor antigo para JUSTO antes de escrever no Conversations. Enquanto isso, a operação sucessora roda inteira: a Ana sai da
            // fila, a reconciliação desfaz a intenção dela (o destino não é mais da fila) e a Bia assume a conversa.
            controleDoGateway.Antes(nameof(IConversationGateway.TransitionAsync), async () =>
            {
                Assert.True(await c.RemoverAtendenteAsync(fila.Id, ana.Id));
                await c.ReconciliarAsync();
                Assert.Null((await c.ItemAsync(item.Id))!.Pending);
                Assert.Equal(OperationOutcome.Completed, (await c.AssumirAsync(item.Id, bia.Contexto)).Outcome);
            });

            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, portaDeItens, gateway);
            var rastreado = (await itens.FindItemAsync(item.Id, default))!;
            return await executor.DriveAsync(rastreado, default);
        });

        // O executor antigo foi recusado pela cerca do Conversations e saiu sem escrever nada: continua "pendente" para quem conduz.
        Assert.Equal(OperationOutcome.Pending, antigo.Outcome);
        var sessao = (await c.SessaoAsync(item.SessionId))!;
        Assert.Equal(bia.Id, sessao.ResponderRef); // a Ana NÃO voltou a ser a responsável
        var atual = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, bia.Id), (atual.Status, atual.AssignedUserId));
        Assert.Null(atual.Pending);
        var atribuicao = Assert.Single((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind == HistoryKind.Assigned);
        Assert.Equal(bia.Id, atribuicao.ToUserId);
    }

    [Fact]
    public async Task O_executor_substituido_depois_de_ler_a_sessao_e_recusado_e_o_substituto_conclui_a_operacao_uma_vez_so()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercaok");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        var antigo = await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var (portaDeItens, controleDeItens) = Interceptador<IQueueItemRepository>.Envolver(itens);
            controleDeItens.Substituir(nameof(IQueueItemRepository.TryLockItemAsync), _ => Task.FromResult<IAsyncDisposable?>(new TravaPerdida()));
            var (gateway, controleDoGateway) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());

            // O substituto conclui a MESMA operação (a Ana segue na fila) enquanto o antigo está parado antes de escrever.
            controleDoGateway.Antes(nameof(IConversationGateway.TransitionAsync), async () => Assert.Equal(1, await c.ReconciliarAsync()));
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, portaDeItens, gateway);
            return await executor.DriveAsync((await itens.FindItemAsync(item.Id, default))!, default);
        });

        Assert.Equal(OperationOutcome.Pending, antigo.Outcome); // recusado pela cerca; quem concluiu foi o substituto
        var atual = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, ana.Id), (atual.Status, atual.AssignedUserId));
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
        Assert.Single((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind == HistoryKind.Assigned);
        Assert.Single(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task O_executor_que_perde_a_trava_antes_da_emissao_do_token_nao_ganha_autoridade_nova_depois_do_cancelamento_e_da_substituicao_da_intencao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercaantes");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var bia = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id); // a intenção antiga: assumir para a Ana
        var interceptou = false;

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var (portaDeItens, controleDeItens) = Interceptador<IQueueItemRepository>.Envolver(itens);
            controleDeItens.Substituir(nameof(IQueueItemRepository.TryLockItemAsync), _ => Task.FromResult<IAsyncDisposable?>(new TravaPerdida()));

            // O executor antigo já validou a intenção e perdeu a trava, e para JUSTO antes de pedir o token. Enquanto isso: a Ana sai da fila, a
            // reconciliação cancela a intenção dela, a Bia assume por outro pedido e a Ana volta para a fila (o destino da intenção antiga
            // voltou a ser válido). O token que ele pedir agora seria o MAIOR de todos, se fosse dado a quem perdeu a intenção.
            controleDeItens.Antes(nameof(IQueueItemRepository.AcquireFenceTokenAsync), async () =>
            {
                Assert.True(await c.RemoverAtendenteAsync(fila.Id, ana.Id));
                await c.ReconciliarAsync();
                Assert.Null((await c.ItemAsync(item.Id))!.Pending);
                Assert.Equal(OperationOutcome.Completed, (await c.AssumirAsync(item.Id, bia.Contexto)).Outcome);
                await c.AdicionarAtendenteAsync(fila.Id, ana.Id);
                interceptou = true;
            });

            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, portaDeItens);
            return await executor.DriveAsync((await itens.FindItemAsync(item.Id, default))!, default);
        }));

        // A intenção antiga já tinha sido recusada (o recibo dela diz isso), e o executor antigo não escreveu nada em lugar nenhum.
        Assert.True(interceptou);
        Assert.Equal(ContactCenterRule.TargetNotQueueMember, recusa.Rule);
        Assert.Equal(bia.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef); // o Conversations NÃO foi para a Ana
        var atual = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, bia.Id), (atual.Status, atual.AssignedUserId)); // e o ContactCenter concorda com ele
        Assert.Null(atual.Pending);
        Assert.Equal(bia.Id, Assert.Single((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind == HistoryKind.Assigned).ToUserId);
    }

    [Fact]
    public async Task O_executor_antigo_nao_reutiliza_a_versao_do_sucessor_ao_recarregar_o_item_tokens_t1_e_t2_versoes_v1_e_v2()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercareload");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var operacao = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);
        Assert.True(await c.RemoverAtendenteAsync(fila.Id, ana.Id)); // o executor antigo, ao decidir, cancelaria a intenção (o destino saiu da fila)
        long t1 = 0, t2 = 0, v1 = 0, v2 = 0;

        var antigo = await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var (portaDeItens, controle) = Interceptador<IQueueItemRepository>.Envolver(itens);
            controle.Substituir(nameof(IQueueItemRepository.TryLockItemAsync), _ => Task.FromResult<IAsyncDisposable?>(new TravaPerdida()));

            // O executor antigo acaba de receber o token t1 (a linha está na versão v1) e para ANTES de recarregar o item. Nesse instante o
            // sucessor toma a linha: token t2, versão v2. Recarregar agora traria a versão v2 e o token t2 do sucessor.
            controle.Depois(nameof(IQueueItemRepository.AcquireFenceTokenAsync), async () =>
            {
                var depoisDoAntigo = (await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IQueueItemRepository>().FindItemReadOnlyAsync(item.Id, default)))!;
                (t1, v1) = (depoisDoAntigo.ExecutorFenceToken!.Value, depoisDoAntigo.Version);

                var sucessor = await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IQueueItemRepository>().AcquireFenceTokenAsync(item.Id, operacao, default));
                var depoisDoSucessor = (await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IQueueItemRepository>().FindItemReadOnlyAsync(item.Id, default)))!;
                (t2, v2) = (sucessor!.Value, depoisDoSucessor.Version);
            });

            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, portaDeItens);
            return await executor.DriveAsync((await itens.FindItemAsync(item.Id, default))!, default);
        });

        Assert.True(t2 > t1 && v2 > v1); // o sucessor tem o token e a versão MAIORES
        Assert.Equal(OperationOutcome.Pending, antigo.Outcome); // o antigo viu o token do sucessor na linha e saiu: não agiu com a versão dele

        // Ele não gravou NADA: a linha continua exatamente como o sucessor a deixou (a intenção, intacta, com o token e a versão dele).
        var linha = (await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IQueueItemRepository>().FindItemReadOnlyAsync(item.Id, default)))!;
        Assert.Equal((operacao, t2, v2), (linha.Pending!.OperationId, linha.ExecutorFenceToken!.Value, linha.Version));
        Assert.DoesNotContain((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind != HistoryKind.Entered);
        Assert.Empty(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()));

        // Quem retoma depois (o job), com a autoridade vigente, decide: o destino saiu da fila, e a intenção é desfeita UMA vez.
        await c.ReconciliarAsync();
        Assert.Null((await c.ItemAsync(item.Id))!.Pending);
        Assert.Single(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task A_conclusao_local_do_executor_que_perde_a_linha_depois_de_confirmada_no_Conversations_nao_grava_e_o_sucessor_a_conclui_sem_duplicar()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercalocal");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var operacao = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);
        var versaoDoSucessor = 0L;

        // O executor antigo tem a autoridade válida e aplica a atribuição no Conversations; ANTES de concluir no ContactCenter, o sucessor
        // toma a linha (token t2, versão v2). A conclusão local do antigo, presa à versão anterior, não pode gravar.
        await Assert.ThrowsAsync<ContactCenterConflictException>(() => c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var (portaDeItens, controleDeItens) = Interceptador<IQueueItemRepository>.Envolver(itens);
            controleDeItens.Substituir(nameof(IQueueItemRepository.TryLockItemAsync), _ => Task.FromResult<IAsyncDisposable?>(new TravaPerdida()));
            var (gateway, controleDoGateway) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            controleDoGateway.Depois(nameof(IConversationGateway.TransitionAsync), async () =>
            {
                await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IQueueItemRepository>().AcquireFenceTokenAsync(item.Id, operacao, default));
                versaoDoSucessor = (await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IQueueItemRepository>().FindItemReadOnlyAsync(item.Id, default)))!.Version;
            });

            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, portaDeItens, gateway);
            return await executor.DriveAsync((await itens.FindItemAsync(item.Id, default))!, default);
        }));

        // O Conversations já tem a atribuição, e o ContactCenter ainda não concluiu: a linha é a do sucessor, e nada do antigo foi gravado.
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
        var intermediaria = (await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IQueueItemRepository>().FindItemReadOnlyAsync(item.Id, default)))!;
        Assert.Equal((operacao, versaoDoSucessor), (intermediaria.Pending!.OperationId, intermediaria.Version));
        Assert.DoesNotContain((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind == HistoryKind.Assigned);

        // O sucessor conclui reconhecendo o efeito que já existe, e a operação aparece UMA vez no histórico e na auditoria.
        Assert.Equal(1, await c.ReconciliarAsync());
        Assert.Equal((QueueItemStatus.Assigned, ana.Id), ((await c.ItemAsync(item.Id))!.Status, (await c.ItemAsync(item.Id))!.AssignedUserId));
        Assert.Single((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind == HistoryKind.Assigned);
        Assert.Single(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task O_token_so_e_dado_a_quem_ainda_tem_a_intencao_e_cada_emissao_avanca_a_versao_do_item()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cercaemite");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var operacao = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        var (primeiro, segundo, alheia, versoes) = await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var antes = (await itens.FindItemReadOnlyAsync(item.Id, default))!.Version;
            var um = await itens.AcquireFenceTokenAsync(item.Id, operacao, default);
            var dois = await itens.AcquireFenceTokenAsync(item.Id, operacao, default);
            var outra = await itens.AcquireFenceTokenAsync(item.Id, Guid.NewGuid(), default); // uma operação que não é a pendente do item
            var depois = (await itens.FindItemReadOnlyAsync(item.Id, default))!.Version;
            return (um, dois, outra, (antes, depois));
        });

        Assert.NotNull(primeiro);
        Assert.True(segundo > primeiro); // monotônico: quem pega depois leva o maior
        Assert.Null(alheia); // nenhum token para quem não tem a intenção
        Assert.Equal(versoes.antes + 2, versoes.depois); // as duas emissões avançaram a versão; a recusada não
    }

    // A05

    private sealed record Cena(CenarioDeConversas C, Guid Matriz, Guid Filial, ServiceQueueView FilaDaMatriz, ServiceQueueView FilaDaFilial, Agente Ana, Agente Bia, Guid Sessao, QueueItemView Item);

    /// <summary>Uma conversa da matriz, com a Ana (fila da matriz); a Bia é atendente da fila da filial. O número autoriza as duas unidades.</summary>
    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        var filaDaMatriz = await c.CriarFilaAsync("Matriz", unidade: matriz);
        var filaDaFilial = await c.CriarFilaAsync("Filial", unidade: filial);
        var ana = await c.AgenteNaFilaAsync(banco, filaDaMatriz.Id);
        var bia = await c.AgenteNaFilaAsync(banco, filaDaFilial.Id);
        var sessao = await c.NovaConversaAsync(unidade: matriz, canal: canal);
        var item = await c.NaFilaAsync(sessao, filaDaMatriz.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        return new Cena(c, matriz, filial, filaDaMatriz, filaDaFilial, ana, bia, sessao, item);
    }

    /// <summary>A transferência para a fila da filial, com a Bia, que aplica a unidade e é interrompida ANTES de passar o atendente.</summary>
    private static async Task<QueueItemOperationResult> TransferenciaInterrompidaAsync(Cena s, Guid pedido) =>
        await s.C.ComoAsync(s.Ana.Contexto, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            controle.Substituir(nameof(IConversationGateway.TransitionAsync), _ => Task.FromException<TransitionResult>(new InvalidOperationException("Conversations fora do ar")));
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor).TransferAsync(s.Item.Id, s.FilaDaFilial.Id, s.Bia.Id, pedido);
        });

    [Fact]
    public async Task Transferencia_interrompida_entre_a_unidade_e_o_atendente_com_o_atendente_retirado_da_fila_conclui_sem_ele_e_nao_deixa_a_fila_incompativel()
    {
        var s = await MontarAsync("parcial");
        var pedido = Guid.NewGuid();

        var interrompida = await TransferenciaInterrompidaAsync(s, pedido);

        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        var meio = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((s.Filial, s.Ana.Id), (meio.BusinessUnitId, meio.ResponderRef)); // a unidade JÁ mudou no Conversations; o atendente, ainda não

        // Depois, a Bia é retirada da fila de destino, e a reconciliação retoma a operação.
        Assert.True(await s.C.RemoverAtendenteAsync(s.FilaDaFilial.Id, s.Bia.Id));
        await LiberarRecuoAsync(s.C, s.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());

        // O item acompanha o que foi aplicado: está na fila da FILIAL (a unidade onde a sessão já está), em espera e sem responsável.
        var depois = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaDaFilial.Id, s.Filial, QueueItemStatus.Waiting, (Guid?)null), (depois.QueueId, depois.BusinessUnitId, depois.Status, depois.AssignedUserId));
        Assert.Null(depois.Pending);
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((s.Filial, HandlingMode.HumanAssisted, (Guid?)null), (sessao.BusinessUnitId, sessao.HandlingMode, sessao.ResponderRef)); // a Ana deixou de ser a responsável

        // Rastro e auditoria: a transferência aconteceu, e o que não pôde ser feito também fica dito.
        var rastro = (await s.C.HistoricoDoItemAsync(s.Item.Id))!.Last();
        Assert.Equal((HistoryKind.TransferredQueue, s.Ana.Id, (Guid?)null, s.FilaDaMatriz.Id, s.FilaDaFilial.Id), (rastro.Kind, rastro.FromUserId, rastro.ToUserId, rastro.FromQueueId, rastro.ToQueueId));
        Assert.EndsWith(QueueItem.TargetUnavailableSuffix, rastro.Reason);
        Assert.Single(await s.C.AuditoriaAsync("contact-center.item.transfer", s.Item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
        Assert.Single(await s.C.AuditoriaAsync(HandlingExecutor.TargetUnavailableOperation, s.Item.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);

        // Idempotente: repetir o pedido devolve a mesma resposta (a transferência concluída), e reconciliar de novo não faz nada.
        var repeticao = await s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaDaFilial.Id, s.Bia.Id, s.Ana.Contexto, pedido);
        Assert.Equal((OperationOutcome.AlreadyApplied, s.FilaDaFilial.Id, QueueItemStatus.Waiting), (repeticao.Outcome, repeticao.Item.QueueId, repeticao.Item.Status));
        Assert.Equal(0, await s.C.ReconciliarAsync());
        Assert.Single((await s.C.HistoricoDoItemAsync(s.Item.Id))!, h => h.Kind == HistoryKind.TransferredQueue);
        Assert.Single(await s.C.AuditoriaAsync(HandlingExecutor.TargetUnavailableOperation, s.Item.Id.ToString()));
    }

    [Fact]
    public async Task A_transferencia_concluida_sem_o_atendente_pelo_job_audita_as_duas_entradas_com_a_proveniencia_do_pedido()
    {
        var s = await MontarAsync("parcialprov");
        var antes = DateTimeOffset.UtcNow;
        var ana = new RequestContext(s.Ana.Contexto.TenantId, "session", s.Ana.Contexto.Principal);

        // A Ana pede (com a origem "session"); a unidade é aplicada e a chamada do atendente falha.
        var interrompida = await s.C.ComoAsync(ana, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            controle.Substituir(nameof(IConversationGateway.TransitionAsync), _ => Task.FromException<TransitionResult>(new InvalidOperationException("Conversations fora do ar")));
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor).TransferAsync(s.Item.Id, s.FilaDaFilial.Id, s.Bia.Id, Guid.NewGuid());
        });
        var depoisDoPedido = DateTimeOffset.UtcNow;
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);

        // A Bia sai da fila; o JOB (outra origem, outro instante) conclui a transferência sem ela.
        Assert.True(await s.C.RemoverAtendenteAsync(s.FilaDaFilial.Id, s.Bia.Id));
        await Task.Delay(100);
        await LiberarRecuoAsync(s.C, s.Item.Id);
        Assert.Equal(1, await s.C.ComoAsync(new RequestContext(s.C.Tenant.TenantId, "worker-reconciler"), sp => sp.GetRequiredService<QueueItemService>().ReconcilePendingAsync(TimeSpan.Zero, 50)));

        var sucesso = Assert.Single(await s.C.AuditoriaAsync("contact-center.item.transfer", s.Item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
        var negacao = Assert.Single(await s.C.AuditoriaAsync(HandlingExecutor.TargetUnavailableOperation, s.Item.Id.ToString()));
        Assert.All(new[] { sucesso, negacao }, e =>
        {
            Assert.Equal((s.Ana.Id, "session"), (e.ActorUserId, e.ActorOrigin));
            Assert.InRange(e.OccurredAt, antes, depoisDoPedido);
        });
    }

    [Fact]
    public async Task Transferencia_interrompida_entre_a_unidade_e_o_atendente_com_o_atendente_ainda_na_fila_conclui_inteira()
    {
        var s = await MontarAsync("parcialok");
        var pedido = Guid.NewGuid();
        Assert.Equal(OperationOutcome.Pending, (await TransferenciaInterrompidaAsync(s, pedido)).Outcome);

        await LiberarRecuoAsync(s.C, s.Item.Id);
        Assert.Equal(1, await s.C.ReconciliarAsync());

        var depois = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaDaFilial.Id, s.Filial, QueueItemStatus.Assigned, s.Bia.Id), (depois.QueueId, depois.BusinessUnitId, depois.Status, depois.AssignedUserId));
        Assert.Equal(s.Bia.Id, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef);
        Assert.DoesNotContain((await s.C.HistoricoDoItemAsync(s.Item.Id))!, h => h.Reason.EndsWith(QueueItem.TargetUnavailableSuffix, StringComparison.Ordinal));
        Assert.Empty(await s.C.AuditoriaAsync(HandlingExecutor.TargetUnavailableOperation, s.Item.Id.ToString()));
    }

    [Fact]
    public async Task Sem_nenhum_efeito_remoto_aplicado_o_atendente_retirado_desfaz_a_transferencia_inteira_e_o_item_continua_onde_estava()
    {
        var s = await MontarAsync("parcialsem");
        var pedido = Guid.NewGuid();

        // A primeira chamada (a unidade) falha: NADA foi aplicado no Conversations, e a operação fica pendente.
        var interrompida = await s.C.ComoAsync(s.Ana.Contexto, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            controle.Substituir(nameof(IConversationGateway.TransferAsync), _ => Task.FromException<TransferSessionResult>(new InvalidOperationException("Conversations fora do ar")));
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor).TransferAsync(s.Item.Id, s.FilaDaFilial.Id, s.Bia.Id, pedido);
        });
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);

        // A Bia sai da fila de destino; ao retomar, não há unidade aplicada: a operação toda é desfeita (e a recusa fica registrada).
        Assert.True(await s.C.RemoverAtendenteAsync(s.FilaDaFilial.Id, s.Bia.Id));
        await LiberarRecuoAsync(s.C, s.Item.Id);
        Assert.Equal(0, await s.C.ReconciliarAsync());

        var depois = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((s.FilaDaMatriz.Id, s.Matriz, QueueItemStatus.Assigned, s.Ana.Id), (depois.QueueId, depois.BusinessUnitId, depois.Status, depois.AssignedUserId));
        Assert.Null(depois.Pending);
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((s.Matriz, s.Ana.Id), (sessao.BusinessUnitId, sessao.ResponderRef));
        Assert.Single(await s.C.AuditoriaAsync("contact-center.item.transfer", s.Item.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);
        Assert.Empty(await s.C.AuditoriaAsync(HandlingExecutor.TargetUnavailableOperation, s.Item.Id.ToString()));
    }
}
