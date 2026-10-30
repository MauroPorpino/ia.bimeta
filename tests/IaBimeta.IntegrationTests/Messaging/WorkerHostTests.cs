using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IaBimeta.IntegrationTests.Messaging;

/// <summary>
/// O Worker (IaBimeta.Worker) sobe como processo de verdade, com a mesma composição, sobre PostgreSQL real: o relay entrega os eventos
/// do outbox ao ContactCenter e o laço de reconciliação conclui as operações que ficaram pendentes. Dois Workers ao mesmo tempo
/// processam cada evento uma única vez.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerHostTests(PostgresFixture banco)
{
    private IHost Montar(Action<Dictionary<string, string?>>? ajustar = null)
    {
        var configuracao = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = banco.ConnectionString,
            ["Messaging:PollInterval"] = "00:00:00.200",
            ["Worker:ReconcileInterval"] = "00:00:00.300",
            ["Worker:ReconcileMinimumAge"] = "00:00:00",
            ["Channels:Secrets:ActiveKeyId"] = PostgresFixture.IdDaChaveDeTeste,
            [$"Channels:Secrets:Keys:{PostgresFixture.IdDaChaveDeTeste}"] = PostgresFixture.ChaveDeCifraDeTeste,
        };
        ajustar?.Invoke(configuracao);

        // Development liga a validação de escopos e de dependências na montagem: um registro com tempo de vida errado falha aqui.
        return WorkerHost.Build(new[] { "--environment=Development" }, builder => builder.Configuration.AddInMemoryCollection(configuracao));
    }

    private async Task<CenarioDeConversas> NovoAsync(string prefixo)
    {
        banco.RelogioDaMensageria.Sincronizar();
        return await CenarioDeConversas.CriarAsync(banco, prefixo, mensageria: true);
    }

    private static async Task AguardarAsync(Func<Task<bool>> condicao, string descricao)
    {
        var limite = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < limite)
        {
            if (await condicao())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"O Worker não concluiu em 30 s: {descricao}.");
    }

    [Fact]
    public async Task O_Worker_entrega_o_evento_do_outbox_ao_ContactCenter_e_o_item_acompanha_a_mudanca_externa()
    {
        var c = await NovoAsync("wkrelay");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        using var worker = Montar();
        await worker.StartAsync();
        try
        {
            await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, beto.Id, await c.VersaoDaSessaoAsync(sessao));

            await AguardarAsync(async () => (await c.ItemAsync(item.Id))!.AssignedUserId == beto.Id, "o item deveria passar para o Beto");
            await AguardarAsync(async () => (await c.LinhasDoOutboxDaSessaoAsync(sessao)).All(l => l.State == OutboxState.Processed), "o outbox da sessão deveria ficar processado");
        }
        finally
        {
            await worker.StopAsync();
        }

        var rastro = (await c.HistoricoDoItemAsync(item.Id))!.Last();
        Assert.Equal((HistoryKind.Reassigned, (Guid?)null), (rastro.Kind, rastro.ActorUserId));
    }

    [Fact]
    public async Task Dois_Workers_ao_mesmo_tempo_processam_cada_evento_uma_unica_vez()
    {
        var c = await NovoAsync("wkdois");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        using var primeiro = Montar();
        using var segundo = Montar();
        await Task.WhenAll(primeiro.StartAsync(), segundo.StartAsync());
        try
        {
            await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, beto.Id, await c.VersaoDaSessaoAsync(sessao));
            await AguardarAsync(async () => (await c.LinhasDoOutboxDaSessaoAsync(sessao)).All(l => l.State == OutboxState.Processed), "o outbox da sessão deveria ficar processado");
        }
        finally
        {
            await Task.WhenAll(primeiro.StopAsync(), segundo.StopAsync());
        }

        var linhas = await c.LinhasDoOutboxDaSessaoAsync(sessao);
        Assert.All(linhas, l => Assert.Equal(1, l.Attempts)); // cada evento foi pego uma vez, por um só dos Workers
        var historico = (await c.HistoricoDoItemAsync(item.Id))!;
        Assert.Equal(1, historico.Count(h => h.Reason == "external-change"));
        foreach (var linha in linhas.Where(l => l.EventType == "conversations.handling-mode-changed"))
        {
            Assert.Equal(1, await c.RegistrosDoInboxAsync(linha.Id));
        }
    }

    [Fact]
    public async Task O_laco_de_reconciliacao_do_Worker_conclui_a_operacao_pendente_de_cada_tenant_com_contexto_de_sistema()
    {
        var a = await NovoAsync("wkrecona");
        var b = await CenarioDeConversas.CriarAsync(banco, "wkreconb", mensageria: true);
        var pendencias = new List<(CenarioDeConversas C, Guid Item, Guid Sessao, Agente Ana)>();
        foreach (var c in new[] { a, b })
        {
            var fila = await c.CriarFilaAsync();
            var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
            var sessao = await c.NovaConversaAsync();
            var item = await c.NaFilaAsync(sessao, fila.Id);
            await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);
            pendencias.Add((c, item.Id, sessao, ana));
        }

        // O banco de teste é compartilhado e persiste entre execuções: o trabalho que outros testes deixaram elegível (a intenção inerte e a obrigação aberta
        // ficam pendentes de propósito) disputa o lote de tenants de UM ciclo, que é o mais antigo primeiro. O teste prova o laço, não o tamanho do lote.
        using var worker = Montar(c => c["Worker:ReconcileTenantBatchSize"] = "1000");
        var reconciliador = worker.Services.GetRequiredService<ContactCenterReconciler>();
        var concluidas = await reconciliador.RunOnceAsync(default);

        Assert.True(concluidas >= 2);
        foreach (var (c, item, sessao, ana) in pendencias)
        {
            var depois = (await c.ItemAsync(item))!;
            Assert.Equal((QueueItemStatus.Assigned, ana.Id, null), (depois.Status, depois.AssignedUserId, depois.Pending));
            Assert.Equal(ana.Id, (await c.SessaoAsync(sessao))!.ResponderRef);
        }
    }

    [Fact]
    public async Task O_tenant_cuja_pendencia_acabou_de_falhar_recua_e_nao_ocupa_o_lote_dos_outros_tenants()
    {
        var a = await NovoAsync("wkrecuoa");
        var b = await CenarioDeConversas.CriarAsync(banco, "wkrecuob", mensageria: true);
        var itens = new Dictionary<Guid, (CenarioDeConversas C, Guid Item)>();
        foreach (var c in new[] { a, b })
        {
            var fila = await c.CriarFilaAsync();
            var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
            var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
            await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);
            itens[c.Tenant.TenantId.Value] = (c, item.Id);
        }

        // A tentativa do tenant A falhou agora: a próxima só vale depois do recuo.
        var agora = DateTimeOffset.UtcNow;
        await a.ComoAsync(a.ContextoDeSistema, async sp =>
        {
            var repositorio = sp.GetRequiredService<IQueueItemRepository>();
            (await repositorio.FindItemAsync(itens[a.Tenant.TenantId.Value].Item, default))!.RecordAttemptFailure("TestFailure", agora);
            await repositorio.SaveChangesAsync(default);
            return 0;
        });

        async Task<IReadOnlyList<Guid>> ListarAsync(DateTimeOffset instante) =>
            await b.ComoAsync(b.ContextoDeSistema, sp => sp.GetRequiredService<IPendingWorkSource>().ListTenantsWithPendingAsync(instante.AddMinutes(1), instante, 1000, default));

        var logo = await ListarAsync(agora);
        Assert.Contains(b.Tenant.TenantId.Value, logo);
        Assert.DoesNotContain(a.Tenant.TenantId.Value, logo);

        // Passado o recuo, o tenant A volta, depois do tenant que esperava desde antes.
        var depois = await ListarAsync(agora.AddHours(1));
        Assert.Contains(a.Tenant.TenantId.Value, depois);
        Assert.True(depois.ToList().IndexOf(b.Tenant.TenantId.Value) < depois.ToList().IndexOf(a.Tenant.TenantId.Value));
    }

    [Fact]
    public async Task O_hospedeiro_do_Worker_ouve_o_relay_e_a_reconciliacao_como_servicos_de_segundo_plano_e_registra_os_handlers_do_ContactCenter()
    {
        using var worker = Montar();

        var servicos = worker.Services.GetServices<IHostedService>().Select(s => s.GetType().Name).ToList();
        Assert.Contains("OutboxRelayService", servicos);
        Assert.Contains("ContactCenterReconciliationService", servicos);
        await using var escopo = worker.Services.CreateAsyncScope();
        var tipos = escopo.ServiceProvider.GetServices<IEventHandler>().Select(h => $"{h.EventType}/{h.EventVersion}").OrderBy(t => t).ToList();
        Assert.Equal(new[] { "conversations.handling-mode-changed/1", "conversations.session-closed/1", "conversations.session-transferred/1" }, tipos);
        Assert.Single(worker.Services.GetServices<IOutboxRelay>());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Configuracao_invalida_do_Worker_ou_do_Messaging_falha_na_partida_e_nao_no_meio_do_processamento()
    {
        Assert.Throws<InvalidOperationException>(() => Montar(c => c["Worker:ReconcileBatchSize"] = "0"));
        Assert.Throws<InvalidOperationException>(() => Montar(c => c["Worker:ReconcileInterval"] = "00:00:00"));
        Assert.Throws<InvalidOperationException>(() => Montar(c => c["Messaging:MaxAttempts"] = "0"));
        Assert.Throws<InvalidOperationException>(() => Montar(c => c["Messaging:BatchSize"] = "5000"));
        await Task.CompletedTask;
    }
}
