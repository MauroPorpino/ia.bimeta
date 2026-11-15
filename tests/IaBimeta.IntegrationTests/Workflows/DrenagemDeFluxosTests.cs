using IaBimeta.Api.Operations;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Messaging;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Domain;
using IaBimeta.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Workflows;

/// <summary>O procedimento de drenagem (<c>workflows status</c> e <c>workflows drain --confirm</c>): só encerra o que está ativo, só da empresa pedida, devolve a conversa ao agente e descarta o que ia ser enviado.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DrenagemDeFluxosTests(PostgresFixture banco)
{
    private sealed record InstanciaSemeada(Guid Id, Guid Sessao);

    private async Task<InstanciaSemeada> SemearAsync(TenantInfo tenant, Guid org, FlowStatus status,
        InstanciaSemeada? colisao = null, bool finalizada = false, Guid? sessao = null)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var agora = DateTimeOffset.UtcNow;
        var i = FlowInstance.Create(tenant.TenantId, sessao ?? colisao?.Sessao ?? Guid.NewGuid(), org,
            Guid.NewGuid(), "drenar", 1, null, OnFailurePolicy.Agent, "v1:x", "k1", agora.AddHours(-2));
        i.Apply(new FlowState(status, "menu", new Dictionary<string, string>(), 0, 1, null,
            status == FlowStatus.AwaitingEffect ? "1:0" : null), "v1:x", "k1", 1, status == FlowStatus.AwaitingEffect, agora);
        if (finalizada)
        {
            i.ParkUntil(null, agora);
        }
        if (colisao is not null)
        {
            db.Entry(i).Property(x => x.Id).CurrentValue = colisao.Id;
        }

        db.Instances.Add(i);
        db.Decisions.Add(RoutingDecision.Create(tenant.TenantId, i.SessionId, finalizada ? AutomationOwner.Agent : AutomationOwner.Flow, null, agora));
        db.Effects.Add(FlowEffectRecord.Create(tenant.TenantId, Guid.NewGuid(), i.Id, "1:0", 1, 0, EffectKind.SendMessage, "menu", "v1:conteudo", "k1", agora));
        await db.SaveChangesAsync();
        return new InstanciaSemeada(i.Id, i.SessionId);
    }

    private async Task<(FlowInstance Instance, AutomationOwner Owner, FlowEffectRecord Effect)> LerAsync(TenantInfo tenant, InstanciaSemeada semeada)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        return (await db.Instances.AsNoTracking().SingleAsync(i => i.Id == semeada.Id),
            (await db.Decisions.AsNoTracking().SingleAsync(d => d.SessionId == semeada.Sessao)).Owner,
            await db.Effects.AsNoTracking().SingleAsync(e => e.InstanceId == semeada.Id));
    }

    [Fact]
    public async Task Drenagem_exige_confirmacao_e_isola_empresas_com_os_mesmos_ids_limpa_cifras_e_e_idempotente()
    {
        var a = await banco.RegistrarTenantAsync("drena-a");
        var b = await banco.RegistrarTenantAsync("drena-b");
        var orgA = (await banco.CriarOrganizacaoAsync(a, "Org A")).Id;
        var orgB = (await banco.CriarOrganizacaoAsync(b, "Org B")).Id;
        var espera = await SemearAsync(a, orgA, FlowStatus.WaitingInput);
        var aguarda = await SemearAsync(a, orgA, FlowStatus.AwaitingEffect);
        var concluida = await SemearAsync(a, orgA, FlowStatus.Completed, finalizada: true);
        var outra = await SemearAsync(b, orgB, FlowStatus.WaitingInput, colisao: espera);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        Assert.Equal(2, await WorkflowsCommand.RunAsync(["drain"], new StringWriter())); // sem --confirm
        Assert.Equal(2, await WorkflowsCommand.RunAsync(["bogus"], new StringWriter()));

        var saida = new StringWriter();
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: false, a.Slug, saida));
        Assert.Contains("WaitingInput", saida.ToString());
        Assert.Contains("AwaitingEffect", saida.ToString());
        Assert.DoesNotContain("Completed", saida.ToString());

        var drenagem = new StringWriter();
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, a.Slug, drenagem));
        Assert.Contains("2 instância(s) encerrada(s)", drenagem.ToString());

        foreach (var semeada in new[] { espera, aguarda })
        {
            var (instancia, dono, efeito) = await LerAsync(a, semeada);
            Assert.Equal((FlowStatus.Cancelled, "operator-drain", (DateTimeOffset?)null), (instancia.Status, instancia.FailureCode, instancia.NextRunAt));
            Assert.NotNull(instancia.EndedAt);
            Assert.Equal(AutomationOwner.Agent, dono);
            Assert.Equal(EffectState.Superseded, efeito.State);
            Assert.Equal(("", "", "", ""), (instancia.VariablesCipher, instancia.VariablesKeyId, efeito.PayloadCipher, efeito.PayloadKeyId));
        }

        Assert.Equal(FlowStatus.Completed, (await LerAsync(a, concluida)).Instance.Status);
        var (instanciaB, donoB, efeitoB) = await LerAsync(b, outra);
        Assert.Equal((FlowStatus.WaitingInput, AutomationOwner.Flow, EffectState.Pending), (instanciaB.Status, donoB, efeitoB.State));
        Assert.Equal(("v1:x", "v1:conteudo"), (instanciaB.VariablesCipher, efeitoB.PayloadCipher));

        // Repetir é seguro (nada ativo na empresa A) e empresa inexistente falha sem alterar nada.
        var repetida = new StringWriter();
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, a.Slug, repetida));
        Assert.Contains("0 instância(s) encerrada(s)", repetida.ToString());
        Assert.Equal(1, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, "empresa-que-nao-existe", new StringWriter()));
        Assert.Equal(2, (await EventosAsync(a)).Count);
        Assert.Empty(await EventosAsync(b));
    }

    private async Task<List<OutboxMessage>> EventosAsync(TenantInfo tenant)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        return await db.Set<OutboxMessage>().AsNoTracking().Where(m => m.TenantId == tenant.TenantId.Value).ToListAsync();
    }

    [Fact]
    public async Task Instancia_criada_depois_da_selecao_fica_intacta_ate_a_proxima_drenagem()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "drena-insercao", fluxos: true);
        var tenant = a.Tenant;
        var org = a.C.OrganizacaoId;
        var entrada = await a.EntrarAsync("cliente", "Primeira mensagem");
        var primeira = await SemearAsync(tenant, org, FlowStatus.WaitingInput, sessao: entrada.SessionId);
        await a.RelayAsync();
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        await using var escopo = api.Services.CreateAsyncScope();
        var relogio = new PausaDaDrenagem();
        var servicos = escopo.ServiceProvider;
        var operacao = new FlowDrain(servicos.GetRequiredService<WorkflowsDbContext>(), servicos.GetRequiredService<IOutboxWriter>(), relogio);
        var drenagem = Task.Run(() => operacao.ExecuteAsync(tenant.TenantId, default));
        InstanciaSemeada nova;
        try
        {
            await relogio.Selecionadas.Task.WaitAsync(TimeSpan.FromSeconds(10));
            nova = await SemearAsync(tenant, org, FlowStatus.AwaitingEffect);
            var durante = await a.EntrarAsync("cliente", "Mensagem durante a troca de dono");
            Assert.Equal(entrada.SessionId, durante.SessionId);
            await a.RelayAsync();
            Assert.Empty(await a.TurnosAsync(entrada.SessionId));
        }
        finally
        {
            relogio.Continuar.TrySetResult();
        }

        Assert.Equal(new FlowDrainResult(1, 1, 1), await drenagem);
        Assert.Equal(FlowStatus.Cancelled, (await LerAsync(tenant, primeira)).Instance.Status);
        var (instancia, dono, efeito) = await LerAsync(tenant, nova);
        Assert.Equal((FlowStatus.AwaitingEffect, AutomationOwner.Flow, EffectState.Pending), (instancia.Status, dono, efeito.State));
        Assert.Equal(primeira.Sessao, Assert.Single(await EventosAsync(tenant)).CorrelationId);
        a.Relogio.Sincronizar();
        await a.Servicos.GetRequiredService<OutboxRelay<WorkflowsDbContext>>().RunOnceAsync("worker-durante-drenagem");
        await a.ExecutarAsync();
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(2, (await a.C.SessaoAsync(entrada.SessionId))!.AnsweredThroughSeq);

        Assert.Equal(new FlowDrainResult(1, 1, 1), await operacao.ExecuteAsync(tenant.TenantId, default));
        Assert.Equal(FlowStatus.Cancelled, (await LerAsync(tenant, nova)).Instance.Status);
    }

    [Fact]
    public async Task Efeito_incerto_mantem_o_resultado_ao_drenar_instancia_ainda_pendente_de_finalizacao()
    {
        var tenant = await banco.RegistrarTenantAsync("drena-incerto");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Org")).Id;
        var semeada = await SemearAsync(tenant, org, FlowStatus.Completed);
        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant)))
        {
            var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var efeito = await db.Effects.SingleAsync(e => e.InstanceId == semeada.Id);
            efeito.Start();
            await db.SaveChangesAsync();
        }

        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var bloqueio = new StringWriter();
        Assert.Equal(1, await WorkflowsCommand.ExecuteAsync(api.Services, true, tenant.Slug, bloqueio));
        Assert.Contains("sem resultado confirmado", bloqueio.ToString());
        Assert.Equal(EffectState.InFlight, (await LerAsync(tenant, semeada)).Effect.State);
        Assert.Empty(await EventosAsync(tenant));

        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant)))
        {
            var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var efeito = await db.Effects.SingleAsync(e => e.InstanceId == semeada.Id);
            efeito.Complete(EffectState.Unknown, "outcome-unknown", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var drenagem = new StringWriter();
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, true, tenant.Slug, drenagem));
        Assert.Contains("1 efeito(s) com resultado incerto", drenagem.ToString());
        var (instancia, dono, final) = await LerAsync(tenant, semeada);
        Assert.Equal((FlowStatus.Cancelled, AutomationOwner.Agent, EffectState.Unknown, "outcome-unknown", ""), (instancia.Status, dono, final.State, final.ResultCode, final.PayloadCipher));
    }

    [Fact]
    public async Task Falha_ao_gravar_a_retomada_desfaz_cancelamento_limpeza_e_troca_de_dono()
    {
        var tenant = await banco.RegistrarTenantAsync("drena-rollback");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Org")).Id;
        var semeada = await SemearAsync(tenant, org, FlowStatus.WaitingInput);
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        var restricao = $"test_outbox_{tenant.TenantId.Value:N}";
        await using (var proibir = new NpgsqlCommand(
            $"ALTER TABLE workflows.outbox_message ADD CONSTRAINT {restricao} CHECK (tenant_id <> '{tenant.TenantId.Value:D}')", conexao))
        {
            await proibir.ExecuteNonQueryAsync();
        }

        try
        {
            await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
            var erro = await Assert.ThrowsAsync<DbUpdateException>(() =>
                WorkflowsCommand.ExecuteAsync(api.Services, true, tenant.Slug, new StringWriter()));
            Assert.Equal(restricao, Assert.IsType<PostgresException>(erro.InnerException).ConstraintName);
            var (instancia, dono, efeito) = await LerAsync(tenant, semeada);
            Assert.Equal((FlowStatus.WaitingInput, AutomationOwner.Flow, EffectState.Pending), (instancia.Status, dono, efeito.State));
            Assert.Equal(("v1:x", "v1:conteudo"), (instancia.VariablesCipher, efeito.PayloadCipher));
            Assert.Empty(await EventosAsync(tenant));
        }
        finally
        {
            await using var liberar = new NpgsqlCommand($"ALTER TABLE workflows.outbox_message DROP CONSTRAINT {restricao}", conexao);
            await liberar.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Retomada_sobrevive_a_queda_pos_commit_e_recupera_mensagem_ignorada_sem_turno_anterior()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "drena-retoma", fluxos: true);
        var entrada = await a.EntrarAsync("cliente", "Primeira mensagem");
        await SemearAsync(a.Tenant, a.C.OrganizacaoId, FlowStatus.WaitingInput, sessao: entrada.SessionId);
        await a.RelayAsync();
        Assert.Empty(await a.TurnosAsync(entrada.SessionId));

        await using (var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao()))
        {
            await Assert.ThrowsAsync<IOException>(() => WorkflowsCommand.ExecuteAsync(api.Services, true, a.Tenant.Slug, new SaidaInterrompida()));
        }

        Assert.Equal(OutboxState.Pending, Assert.Single(await EventosAsync(a.Tenant)).State);
        Assert.Empty(await a.TurnosAsync(entrada.SessionId));
        a.Relogio.Sincronizar();
        // A composição consumidora tem Workflows desligado, como após a recriação do Worker.
        var relay = banco.ServicosComAgentes.GetRequiredService<OutboxRelay<WorkflowsDbContext>>();
        await relay.RunOnceAsync("worker-apos-queda");
        await relay.RunOnceAsync("worker-apos-queda");
        Assert.Equal(OutboxState.Processed, Assert.Single(await EventosAsync(a.Tenant)).State);
        Assert.Single(await a.TurnosAsync(entrada.SessionId));
        await a.ExecutarAsync();
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.DoesNotContain((await a.C.HistoricoAsync(entrada.SessionId))!.Items, m => m.Author == MessageAuthor.Bot);
    }

    /// <summary>
    /// O que JÁ foi enviado ao canal (efeito concluído) nunca é reaberto nem reenviado pela drenagem, e cada conversa REAL (sessão do Conversations) gera um único aviso de retomada, só na empresa pedida (teste do Claude,
    /// preservado na consolidação: o aviso agora é o evento <c>workflows.flow-drained-to-agent</c> no outbox do Workflows, não mais um comando pós-commit).
    /// </summary>
    [Fact]
    public async Task Efeito_ja_enviado_continua_concluido_e_cada_conversa_real_gera_um_unico_aviso_de_retomada()
    {
        banco.RelogioDaMensageria.Sincronizar();
        var a = await CenarioDeConversas.CriarAsync(banco, "drena-aviso-a", mensageria: true);
        var b = await CenarioDeConversas.CriarAsync(banco, "drena-aviso-b", mensageria: true);
        var sessaoA = await a.NovaConversaAsync();
        var sessaoB = await b.NovaConversaAsync();
        var semeadaA = await SemearAsync(a.Tenant, a.OrganizacaoId, FlowStatus.WaitingInput, sessao: sessaoA);
        await SemearAsync(b.Tenant, b.OrganizacaoId, FlowStatus.WaitingInput, sessao: sessaoB);

        Guid enviado;
        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant)))
        {
            var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var efeito = FlowEffectRecord.Create(a.Tenant.TenantId, Guid.NewGuid(), semeadaA.Id, "0:0", 0, 0, EffectKind.SendMessage, "inicio", "v1:conteudo", "k1", DateTimeOffset.UtcNow);
            efeito.Complete(EffectState.Done, "ok", DateTimeOffset.UtcNow);
            db.Effects.Add(efeito);
            await db.SaveChangesAsync();
            enviado = efeito.Id;
        }

        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, a.Tenant.Slug, new StringWriter()));

        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant)))
        {
            var efeito = await escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>().Effects.AsNoTracking().SingleAsync(e => e.Id == enviado);
            Assert.Equal(EffectState.Done, efeito.State);
        }

        Assert.Single(await EventosAsync(a.Tenant), e => e.OrderingKey == sessaoA.ToString("N"));
        Assert.Empty(await EventosAsync(b.Tenant));

        // Repetir não avisa de novo.
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, a.Tenant.Slug, new StringWriter()));
        Assert.Single(await EventosAsync(a.Tenant), e => e.OrderingKey == sessaoA.ToString("N"));
    }

    private sealed class PausaDaDrenagem : TimeProvider
    {
        public TaskCompletionSource Selecionadas { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continuar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            Selecionadas.TrySetResult();
            Continuar.Task.GetAwaiter().GetResult();
            return DateTimeOffset.UtcNow;
        }
    }

    private sealed class SaidaInterrompida : StringWriter
    {
        public override Task WriteLineAsync(string? value) => value?.StartsWith("Drenagem concluída:", StringComparison.Ordinal) == true
            ? throw new IOException("Simula a perda do processo antes da confirmação ao operador.")
            : base.WriteLineAsync(value);
    }
}
