using System.Diagnostics;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// O estado de envio da conta no PostgreSQL real (SND-2 e SND-6): o balde de fichas vale entre processos concorrentes (a linha é travada na transação), a pausa e a espera do provedor ficam gravadas, a conta de outro
/// tenant não se enxerga nem se altera, e a administração do envio exige gerir canais e é auditada. Sem adaptador de provedor registrado, a retomada segura é recusada e a conta continua pausada.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountSendStateIntegrationTests(PostgresFixture banco)
{
    private static Task<T> Loja<T>(CenarioDeConversas c, RequestContext contexto, Func<IAccountSendStateStore, Task<T>> acao) =>
        c.ComoAsync(contexto, sp => acao(sp.GetRequiredService<IAccountSendStateStore>()));

    [Fact]
    public async Task O_balde_nunca_concede_mais_que_a_capacidade_mais_o_reabastecimento_mesmo_com_muitos_processos_em_paralelo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "sndbalde");
        var conta = await c.CriarContaAsync();
        await Loja(c, c.ContextoDeSistema, l => l.SetThroughputAsync(conta.Id, 10, default).ContinueWith(_ => true));

        var relogio = Stopwatch.StartNew();
        var tentativas = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => Loja(c, c.ContextoDeSistema, l => l.TryAcquireAsync(conta.Id, default))));
        var duracao = relogio.Elapsed.TotalSeconds;

        var concedidas = tentativas.Count(t => t.Granted);

        // Capacidade de 10 fichas (vazão de 10, rajada de 1 s) mais o que o tempo da própria rodada reabasteceu (a 10 por segundo), com uma de folga: sem a trava da linha, as 60 passariam.
        Assert.InRange(concedidas, 10, 10 + (int)Math.Ceiling(duracao * 10) + 1);
        Assert.All(tentativas.Where(t => !t.Granted), t =>
        {
            Assert.False(t.Paused);
            Assert.True(t.Wait > TimeSpan.Zero);
            Assert.True(t.Wait <= TimeSpan.FromMilliseconds(100) + TimeSpan.FromMilliseconds(1), "a espera de uma ficha a 10 por segundo é de no máximo 100 ms");
        });
    }

    [Fact]
    public async Task A_vazao_da_conta_e_a_pausa_e_a_espera_do_provedor_ficam_gravadas_e_valem_para_o_proximo_pedido()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "sndestado");
        var conta = await c.CriarContaAsync();

        Assert.Null(await Loja(c, c.ContextoDeSistema, l => l.FindAsync(conta.Id, default)));

        await Loja(c, c.ContextoDeSistema, l => l.SetThroughputAsync(conta.Id, 20, default).ContinueWith(_ => true));
        Assert.True(await Loja(c, c.ContextoDeSistema, l => l.PauseAsync(conta.Id, "account-error.190", default)));
        Assert.False(await Loja(c, c.ContextoDeSistema, l => l.PauseAsync(conta.Id, "manual", default)));

        var pausada = await Loja(c, c.ContextoDeSistema, l => l.FindAsync(conta.Id, default));
        Assert.NotNull(pausada);
        Assert.True(pausada.IsPaused);
        Assert.Equal("account-error.190", pausada.PauseReason);
        Assert.Equal(20, pausada.ThroughputPerSecond);
        Assert.NotNull(pausada.PausedAt);

        // Pausada, nenhuma ficha é concedida nem gasta.
        var durante = await Loja(c, c.ContextoDeSistema, l => l.TryAcquireAsync(conta.Id, default));
        Assert.True(durante.Paused);
        Assert.False(durante.Granted);

        Assert.True(await Loja(c, c.ContextoDeSistema, l => l.ResumeAsync(conta.Id, default)));
        Assert.False(await Loja(c, c.ContextoDeSistema, l => l.ResumeAsync(conta.Id, default)));

        // Retomada gradual (uma ficha) e depois a espera imposta pelo provedor, que vale para o pedido seguinte.
        Assert.True((await Loja(c, c.ContextoDeSistema, l => l.TryAcquireAsync(conta.Id, default))).Granted);
        await Loja(c, c.ContextoDeSistema, l => l.ApplyCooldownAsync(conta.Id, TimeSpan.FromSeconds(30), default).ContinueWith(_ => true));
        var esperando = await Loja(c, c.ContextoDeSistema, l => l.TryAcquireAsync(conta.Id, default));
        Assert.False(esperando.Granted);
        Assert.False(esperando.Paused);
        Assert.InRange(esperando.Wait, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(30));

        var depois = await Loja(c, c.ContextoDeSistema, l => l.FindAsync(conta.Id, default));
        Assert.False(depois!.IsPaused);
        Assert.NotNull(depois.ResumedAt);
        Assert.NotNull(depois.CooldownUntil);
    }

    [Fact]
    public async Task O_estado_de_envio_de_uma_conta_de_outro_tenant_nao_se_enxerga_nem_se_altera()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "sndtenanta");
        var b = await CenarioDeConversas.CriarAsync(banco, "sndtenantb");
        var contaA = await a.CriarContaAsync();
        await Loja(a, a.ContextoDeSistema, l => l.PauseAsync(contaA.Id, "manual", default));

        // O tenant B não vê a linha da conta de A, e não consegue criar estado para ela (a chave estrangeira recusa: a conta não é do tenant do contexto).
        Assert.Null(await Loja(b, b.ContextoDeSistema, l => l.FindAsync(contaA.Id, default)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Loja(b, b.ContextoDeSistema, l => l.PauseAsync(contaA.Id, "manual", default)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Loja(b, b.ContextoDeSistema, l => l.TryAcquireAsync(contaA.Id, default)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Loja(b, b.ContextoDeSistema, l => l.ResumeAsync(contaA.Id, default)));

        // A conta de A segue pausada, intacta.
        var depois = await Loja(a, a.ContextoDeSistema, l => l.FindAsync(contaA.Id, default));
        Assert.True(depois!.IsPaused);
    }

    [Fact]
    public async Task O_banco_recusa_estado_incoerente_pausa_sem_motivo_vazao_fora_do_limite_e_fichas_negativas()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "sndbanco");
        var conta = await c.CriarContaAsync();
        await Loja(c, c.ContextoDeSistema, l => l.SetThroughputAsync(conta.Id, 5, default).ContinueWith(_ => true));
        var tenant = c.Tenant.TenantId.Value;

        // Pausa sem motivo, vazão 0 ou 5000 e fichas negativas: nenhuma passa pelas restrições do banco (CHECK), mesmo por SQL direto.
        foreach (var sql in new[]
        {
            "UPDATE channels.account_send_state SET is_paused = true, pause_reason = NULL, paused_at = now() WHERE tenant_id = {0} AND account_id = {1}",
            "UPDATE channels.account_send_state SET throughput_per_second = 0 WHERE tenant_id = {0} AND account_id = {1}",
            "UPDATE channels.account_send_state SET throughput_per_second = 5000 WHERE tenant_id = {0} AND account_id = {1}",
            "UPDATE channels.account_send_state SET tokens = -1 WHERE tenant_id = {0} AND account_id = {1}",
        })
        {
            await c.ComoAsync(c.ContextoDeSistema, async sp =>
            {
                var db = sp.GetRequiredService<ChannelsDbContext>();
#pragma warning disable EF1002
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql, tenant, conta.Id));
#pragma warning restore EF1002
                return true;
            });
        }
    }

    // A administração do envio

    [Fact]
    public async Task Quem_so_le_canais_ve_o_estado_mas_nao_pausa_nem_retoma_nem_muda_a_vazao_e_a_negacao_e_auditada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "sndleitor");
        var conta = await c.CriarContaAsync();
        var leitor = c.Leitor();

        var visao = await c.ComoAsync(leitor, sp => sp.GetRequiredService<AccountSendingService>().GetAsync(conta.Id));
        Assert.False(visao.Paused);
        Assert.Equal(80, visao.ThroughputPerSecond);
        Assert.True(visao.ThroughputIsDefault);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(leitor, sp => sp.GetRequiredService<AccountSendingService>().PauseAsync(conta.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(leitor, sp => sp.GetRequiredService<AccountSendingService>().ResumeAsync(conta.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(leitor, sp => sp.GetRequiredService<AccountSendingService>().SetThroughputAsync(conta.Id, 20)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(leitor, sp => sp.GetRequiredService<ChannelReadinessService>().CheckAsync(conta.Id, false)));

        Assert.Null(await Loja(c, c.ContextoDeSistema, l => l.FindAsync(conta.Id, default)));
        Assert.Contains(await c.AuditoriaAsync("channels.account.pause", conta.Id.ToString()), e => e.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Denied);
        Assert.Contains(await c.AuditoriaAsync("channels.account.resume", conta.Id.ToString()), e => e.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Denied);
        Assert.Contains(await c.AuditoriaAsync("channels.account.throughput.set", conta.Id.ToString()), e => e.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Denied);
        Assert.Contains(await c.AuditoriaAsync("channels.account.readiness", conta.Id.ToString()), e => e.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Denied);
    }

    [Fact]
    public async Task Conta_de_outro_tenant_responde_como_inexistente_na_administracao_do_envio()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "sndadmina");
        var b = await CenarioDeConversas.CriarAsync(banco, "sndadminb");
        var contaA = await a.CriarContaAsync();

        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => b.ComoAsync(b.Administrador(), sp => sp.GetRequiredService<AccountSendingService>().GetAsync(contaA.Id)));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => b.ComoAsync(b.Administrador(), sp => sp.GetRequiredService<AccountSendingService>().PauseAsync(contaA.Id)));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => b.ComoAsync(b.Administrador(), sp => sp.GetRequiredService<ChannelReadinessService>().CheckAsync(contaA.Id, true)));
    }

    [Fact]
    public async Task O_administrador_pausa_ajusta_a_vazao_e_a_retomada_sem_adaptador_e_recusada_e_a_conta_continua_pausada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "sndadmin");
        var conta = await c.CriarContaAsync();
        var admin = c.Administrador();

        var pausada = await c.ComoAsync(admin, sp => sp.GetRequiredService<AccountSendingService>().PauseAsync(conta.Id));
        Assert.True(pausada.Paused);
        Assert.Equal("manual", pausada.PauseReason);

        var ajustada = await c.ComoAsync(admin, sp => sp.GetRequiredService<AccountSendingService>().SetThroughputAsync(conta.Id, 20));
        Assert.Equal(20, ajustada.ThroughputPerSecond);
        Assert.False(ajustada.ThroughputIsDefault);
        await Assert.ThrowsAsync<IaBimeta.Channels.Domain.ChannelsValidationException>(() => c.ComoAsync(admin, sp => sp.GetRequiredService<AccountSendingService>().SetThroughputAsync(conta.Id, 0)));
        await Assert.ThrowsAsync<IaBimeta.Channels.Domain.ChannelsValidationException>(() => c.ComoAsync(admin, sp => sp.GetRequiredService<AccountSendingService>().SetThroughputAsync(conta.Id, 1001)));

        // A composição padrão dos testes não tem adaptador de provedor: não há como provar que o provedor aceita a credencial, então a retomada é recusada.
        await Assert.ThrowsAsync<ChannelResumeRefusedException>(() => c.ComoAsync(admin, sp => sp.GetRequiredService<AccountSendingService>().ResumeAsync(conta.Id)));
        Assert.True((await c.ComoAsync(admin, sp => sp.GetRequiredService<AccountSendingService>().GetAsync(conta.Id))).Paused);
        Assert.Contains(await c.AuditoriaAsync("channels.account.resume", conta.Id.ToString()), e => e.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Failed);

        var padrao = await c.ComoAsync(admin, sp => sp.GetRequiredService<AccountSendingService>().SetThroughputAsync(conta.Id, null));
        Assert.Equal(80, padrao.ThroughputPerSecond);
        Assert.True(padrao.ThroughputIsDefault);

        Assert.Contains(await c.AuditoriaAsync("channels.account.pause", conta.Id.ToString()), e => e.Outcome == IaBimeta.Audit.Contracts.AuditOutcome.Success);
    }
}
