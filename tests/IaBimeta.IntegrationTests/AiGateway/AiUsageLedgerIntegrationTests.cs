using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.AiGateway;

/// <summary>
/// O ledger persistente do AiGateway com PostgreSQL real (ADR 0017, seção I; C1.4d): concorrência por tenant, reserva de orçamento, teto de tokens, conclusão IDEMPOTENTE, consumo
/// indeterminado, expiração no <c>TryBegin</c>, reconciliação uma única vez, janela fixa que não reinicia o consumo, gatilho do banco, isolamento entre tenants e falha fechada. O instante vem
/// do pedido (<c>Now</c>), então a passagem do tempo se prova sem esperar. Cada teste cria o próprio tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AiUsageLedgerIntegrationTests(PostgresFixture banco)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hora = TimeSpan.FromHours(1);

    private static readonly AiTenantLimits Folgados = new(
        MaxConcurrentCalls: 100, MaxCallsPerPeriod: 10_000, MaxTokensPerPeriod: 1_000_000, Period: Hora, MaxInputMessages: 50, MaxOutputTokens: 500);

    private async Task<(TenantId Tenant, RequestContext Contexto)> NovoTenantAsync(string prefixo)
    {
        var info = await banco.RegistrarTenantAsync(prefixo);
        return (info.TenantId, PostgresFixture.Contexto(info));
    }

    private async Task<T> ComoAsync<T>(RequestContext contexto, Func<IServiceProvider, Task<T>> acao)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await acao(escopo.ServiceProvider);
    }

    private static AiLeaseRequest Pedido(TenantId tenant, DateTimeOffset agora, AiTenantLimits? limites = null, long entrada = 50, int saida = 100, TimeSpan? validade = null, Guid? id = null, string correlacao = "turno-1") =>
        new(tenant, id ?? Guid.NewGuid(), correlacao, "provider-a", "model-a1", limites ?? Folgados, agora, entrada, saida, validade ?? TimeSpan.FromSeconds(60));

    private static AiRequestRecord Registro(AiLease vaga, AiUsageCertainty certeza, AiUsage? consumo = null, DateTimeOffset? quando = null) =>
        new(vaga.TenantId, vaga.AiRequestId, "turno-1", "provider-a", "model-a1", consumo ?? AiUsage.None, new AiCostEstimate(0.0001m, "USD"), TimeSpan.FromMilliseconds(120),
            AiRequestOutcome.Succeeded, AiStopReason.Completed, null, null, "req-1", quando ?? vaga.StartedAt, certeza);

    private Task<AiLeaseResult> IniciarAsync(RequestContext contexto, AiLeaseRequest pedido) =>
        ComoAsync(contexto, sp => sp.GetRequiredService<IAiUsageLedger>().TryBeginAsync(pedido, CancellationToken.None));

    private Task ConcluirAsync(RequestContext contexto, AiLease vaga, AiRequestRecord registro) =>
        ComoAsync(contexto, async sp =>
        {
            await sp.GetRequiredService<IAiUsageLedger>().CompleteAsync(vaga, registro, CancellationToken.None);
            return true;
        });

    private Task<AiRequest> LinhaAsync(RequestContext contexto, Guid id) =>
        ComoAsync(contexto, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.AsNoTracking().SingleAsync(r => r.AiRequestId == id));

    private Task<List<AiUsageWindow>> JanelasAsync(RequestContext contexto) =>
        ComoAsync(contexto, sp => sp.GetRequiredService<AiGatewayDbContext>().Windows.AsNoTracking().OrderBy(w => w.WindowStart).ToListAsync());

    private static AiUsage Uso(int entrada, int saida) => new(entrada, 0, 0, saida);

    // Conceder e concluir

    [Fact]
    public async Task Conceder_a_vaga_grava_a_chamada_reservada_e_conta_a_chamada_na_janela()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerdar");
        var pedido = Pedido(tenant, T0, entrada: 50, saida: 100);

        var concessao = await IniciarAsync(contexto, pedido);

        var vaga = Assert.IsType<AiLease>(concessao.Lease);
        Assert.Equal((150L, T0 + TimeSpan.FromSeconds(60)), (vaga.ReservedTokens, vaga.ExpiresAt));
        var linha = await LinhaAsync(contexto, pedido.AiRequestId);
        Assert.Equal((AiRequestState.Reserved, 150L, 50L, (short)0, (DateTimeOffset?)null), (linha.State, linha.ReservedTokens, linha.EstimatedInputTokens, linha.AppliedKind, linha.CompletedAt));
        Assert.Equal(("turno-1", "provider-a", "model-a1"), (linha.CorrelationId, linha.ProviderId, linha.ModelId));
        var janela = Assert.Single(await JanelasAsync(contexto));
        Assert.Equal((T0, T0 + Hora, 3600, 1, 0L, 0L), (janela.WindowStart, janela.WindowEnd, janela.PeriodSeconds, janela.Calls, janela.ConfirmedTokens, janela.IndeterminateTokens));
    }

    [Fact]
    public async Task Concluir_confirma_o_consumo_real_e_o_contador_da_janela_recebe_so_o_real()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerconf");
        var vaga = (await IniciarAsync(contexto, Pedido(tenant, T0))).Lease!;

        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(60, 20)));

        var linha = await LinhaAsync(contexto, vaga.AiRequestId);
        Assert.Equal((AiRequestState.Confirmed, (short)2, 80L, 0L, 60, 20), (linha.State, linha.AppliedKind, linha.AppliedTokens, linha.AppliedOverage, linha.InputTokens, linha.OutputTokens));
        Assert.Equal(AiUsageCertainty.Reported, linha.UsageCertainty);
        Assert.NotNull(linha.CompletedAt);
        var janela = Assert.Single(await JanelasAsync(contexto));
        Assert.Equal((1, 80L, 0L, 0L), (janela.Calls, janela.ConfirmedTokens, janela.IndeterminateTokens, janela.OverageTokens)); // o REAL, não a reserva (150)
    }

    [Fact]
    public async Task Concluir_de_novo_e_idempotente_nunca_conta_a_mesma_chamada_duas_vezes_nem_com_outro_valor()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgeridem");
        var vaga = (await IniciarAsync(contexto, Pedido(tenant, T0))).Lease!;

        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(60, 20)));
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(60, 20)));
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(9000, 9000)));
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Unknown));

        Assert.Equal(80L, (await LinhaAsync(contexto, vaga.AiRequestId)).AppliedTokens);
        Assert.Equal(80L, Assert.Single(await JanelasAsync(contexto)).ConfirmedTokens);
    }

    [Fact]
    public async Task O_consumo_real_acima_da_reserva_e_gravado_com_o_excedente_e_a_conclusao_nunca_recusa()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerexc");
        var vaga = (await IniciarAsync(contexto, Pedido(tenant, T0, entrada: 50, saida: 100))).Lease!; // reserva 150

        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(400, 100))); // 500 reais

        var linha = await LinhaAsync(contexto, vaga.AiRequestId);
        Assert.Equal((500L, 350L), (linha.AppliedTokens, linha.AppliedOverage));
        var janela = Assert.Single(await JanelasAsync(contexto));
        Assert.Equal((500L, 350L), (janela.ConfirmedTokens, janela.OverageTokens));
    }

    [Fact]
    public async Task Rejeicao_de_admissao_com_evidencia_NoneIncurred_confirma_zero_e_libera_a_reserva()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgernone");
        var vaga = (await IniciarAsync(contexto, Pedido(tenant, T0))).Lease!;

        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.NoneIncurred));

        var janela = Assert.Single(await JanelasAsync(contexto));
        Assert.Equal((1, 0L, 0L), (janela.Calls, janela.ConfirmedTokens, janela.IndeterminateTokens));
        Assert.Equal(AiRequestState.Confirmed, (await LinhaAsync(contexto, vaga.AiRequestId)).State);
    }

    // Indeterminado, expiração e reconciliação

    [Fact]
    public async Task Concluir_como_desconhecido_cobra_a_reserva_e_uma_conclusao_posterior_com_o_real_reconcilia_uma_unica_vez()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerindet");
        var vaga = (await IniciarAsync(contexto, Pedido(tenant, T0, entrada: 50, saida: 100))).Lease!;

        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Unknown));
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Unknown)); // repetir não cobra de novo
        var indeterminada = Assert.Single(await JanelasAsync(contexto));
        Assert.Equal((0L, 150L), (indeterminada.ConfirmedTokens, indeterminada.IndeterminateTokens));
        Assert.Equal((AiRequestState.Indeterminate, 150L), ((await LinhaAsync(contexto, vaga.AiRequestId)) is var l1 ? (l1.State, l1.AppliedTokens) : default));

        // O consumo real chega depois: o indeterminado sai, o real entra, e só uma vez.
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(40, 10)));
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(40, 10)));
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(7000, 7000)));

        var reconciliada = Assert.Single(await JanelasAsync(contexto));
        Assert.Equal((50L, 0L, 0L), (reconciliada.ConfirmedTokens, reconciliada.IndeterminateTokens, reconciliada.OverageTokens));
        Assert.Equal((AiRequestState.Confirmed, 50L), ((await LinhaAsync(contexto, vaga.AiRequestId)) is var l2 ? (l2.State, l2.AppliedTokens) : default));
    }

    [Fact]
    public async Task A_vaga_expirada_vira_indeterminada_no_proximo_pedido_do_tenant_cobra_a_reserva_e_a_conclusao_tardia_reconcilia()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerexp");
        var orfa = (await IniciarAsync(contexto, Pedido(tenant, T0, entrada: 50, saida: 100, validade: TimeSpan.FromSeconds(60)))).Lease!;
        Assert.Equal(150L, Assert.Single(await JanelasAsync(contexto)).ConfirmedTokens + 150); // 150 reservados, ainda nada aplicado

        // O processo caiu: ninguém concluiu. Cinco minutos depois, o próximo pedido do tenant converte a vaga em consumo indeterminado.
        var depois = T0 + TimeSpan.FromMinutes(5);
        Assert.NotNull((await IniciarAsync(contexto, Pedido(tenant, depois))).Lease);

        var linha = await LinhaAsync(contexto, orfa.AiRequestId);
        Assert.Equal((AiRequestState.Indeterminate, 150L, AiRequestOutcome.Expired, AiUsageCertainty.Unknown), (linha.State, linha.AppliedTokens, linha.Outcome, linha.UsageCertainty));
        Assert.Equal(150L, Assert.Single(await JanelasAsync(contexto)).IndeterminateTokens);

        // A conclusão tardia, ainda desconhecida, não muda nada; com o consumo real, reconcilia.
        await ConcluirAsync(contexto, orfa, Registro(orfa, AiUsageCertainty.Unknown));
        Assert.Equal(150L, Assert.Single(await JanelasAsync(contexto)).IndeterminateTokens);
        await ConcluirAsync(contexto, orfa, Registro(orfa, AiUsageCertainty.Reported, Uso(30, 10)));
        var janela = Assert.Single(await JanelasAsync(contexto));
        Assert.Equal((40L, 0L), (janela.ConfirmedTokens, janela.IndeterminateTokens));
    }

    [Fact]
    public async Task A_vaga_expirada_deixa_de_contar_na_simultaneidade_nada_vaza_depois_da_queda()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgervaza");
        var limites = Folgados with { MaxConcurrentCalls = 2 };
        await IniciarAsync(contexto, Pedido(tenant, T0, limites, validade: TimeSpan.FromSeconds(30)));
        await IniciarAsync(contexto, Pedido(tenant, T0, limites, validade: TimeSpan.FromSeconds(30)));

        var cheio = await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromSeconds(10), limites)); // as duas vagas ainda valem
        var depoisDaValidade = await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromMinutes(2), limites)); // as duas expiraram: não contam mais

        Assert.Equal(AiLimitRefusal.ConcurrentCalls, cheio.Refusal);
        Assert.NotNull(depoisDaValidade.Lease);
    }

    [Fact]
    public async Task Expirar_e_concluir_ao_mesmo_tempo_nunca_conta_duas_vezes()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgercorr");
        var total = 12;

        for (var rodada = 0; rodada < 3; rodada++)
        {
            var vagas = new List<AiLease>();
            for (var i = 0; i < total; i++)
            {
                vagas.Add((await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromSeconds(rodada * 1000 + i), validade: TimeSpan.FromSeconds(5)))).Lease!);
            }

            // Concluem (com o real) enquanto outros pedidos do tenant expiram as mesmas vagas: a ordem entre as duas coisas é qualquer uma.
            var quando = T0 + TimeSpan.FromSeconds(rodada * 1000 + 500);
            var tarefas = vagas.Select(v => ConcluirAsync(contexto, v, Registro(v, AiUsageCertainty.Reported, Uso(10, 5))))
                .Concat(Enumerable.Range(0, 6).Select(_ => IniciarAsync(contexto, Pedido(tenant, quando)).ContinueWith(t => (object)t.Result)))
                .ToList();
            await Task.WhenAll(tarefas);
        }

        // Cada chamada concluída com o real vale 15, uma vez: o cache da janela bate com o agregado de ai_requests.
        await VerificarContadoresContraOAgregadoAsync(contexto);
        var confirmadas = await ComoAsync(contexto, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.CountAsync(r => r.State == AiRequestState.Confirmed && r.AppliedTokens == 15));
        Assert.Equal(total * 3, confirmadas);
    }

    // Concorrência e orçamento

    [Fact]
    public async Task Cinquenta_pedidos_ao_mesmo_tempo_com_limite_de_cinco_concedem_exatamente_cinco_e_nunca_passam_de_cinco_abertas()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerconc");
        var limites = Folgados with { MaxConcurrentCalls = 5 };

        var resultados = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => IniciarAsync(contexto, Pedido(tenant, T0, limites))));

        Assert.Equal(5, resultados.Count(r => r.Lease is not null));
        Assert.All(resultados.Where(r => r.Lease is null), r => Assert.Equal(AiLimitRefusal.ConcurrentCalls, r.Refusal));
        var abertas = await ComoAsync(contexto, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.CountAsync(r => r.State == AiRequestState.Reserved));
        Assert.Equal(5, abertas);
        Assert.Equal(5, Assert.Single(await JanelasAsync(contexto)).Calls); // a chamada recusada não é contada
    }

    [Fact]
    public async Task Reservas_em_paralelo_nunca_somam_acima_do_teto_do_periodo()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerteto");
        var limites = Folgados with { MaxTokensPerPeriod = 1000 };

        // 40 pedidos de 100 tokens (50 + 50) disputam um teto de 1000: só 10 cabem.
        var resultados = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => IniciarAsync(contexto, Pedido(tenant, T0, limites, entrada: 50, saida: 50))));

        Assert.Equal(10, resultados.Count(r => r.Lease is not null));
        Assert.All(resultados.Where(r => r.Lease is null), r => Assert.Equal(AiLimitRefusal.TokensPerPeriod, r.Refusal));
        var reservado = await ComoAsync(contexto, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.Where(r => r.State == AiRequestState.Reserved).SumAsync(r => r.ReservedTokens));
        Assert.Equal(1000L, reservado);
    }

    [Fact]
    public async Task O_teto_considera_o_confirmado_o_indeterminado_e_as_reservas_abertas_juntos()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgersoma");
        var limites = Folgados with { MaxTokensPerPeriod = 600 };
        var confirmada = (await IniciarAsync(contexto, Pedido(tenant, T0, limites, entrada: 50, saida: 50))).Lease!; // reserva 100
        await ConcluirAsync(contexto, confirmada, Registro(confirmada, AiUsageCertainty.Reported, Uso(100, 100))); // confirmado 200
        var indeterminada = (await IniciarAsync(contexto, Pedido(tenant, T0, limites, entrada: 100, saida: 100))).Lease!; // reserva 200
        await ConcluirAsync(contexto, indeterminada, Registro(indeterminada, AiUsageCertainty.Unknown)); // indeterminado 200 (a reserva)
        var aberta = (await IniciarAsync(contexto, Pedido(tenant, T0, limites, entrada: 50, saida: 50))).Lease; // aberta 100 -> 500 no total

        var cabe = await IniciarAsync(contexto, Pedido(tenant, T0, limites, entrada: 50, saida: 50)); // 500 + 100 = 600: cabe no limite
        var naoCabe = await IniciarAsync(contexto, Pedido(tenant, T0, limites, entrada: 50, saida: 50)); // 600 + 100 = 700 > 600

        Assert.NotNull(aberta);
        Assert.NotNull(cabe.Lease);
        Assert.Equal(AiLimitRefusal.TokensPerPeriod, naoCabe.Refusal);
    }

    [Fact]
    public async Task O_limite_de_chamadas_do_periodo_recusa_a_seguinte_e_o_periodo_novo_abre_outra_janela()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerchamadas");
        var limites = Folgados with { MaxCallsPerPeriod = 3 };
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull((await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromMinutes(i), limites))).Lease);
        }

        var quarta = await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromMinutes(10), limites));
        var proximoPeriodo = await IniciarAsync(contexto, Pedido(tenant, T0 + Hora + TimeSpan.FromMinutes(1), limites));

        Assert.Equal(AiLimitRefusal.CallsPerPeriod, quarta.Refusal);
        Assert.NotNull(proximoPeriodo.Lease);
        var janelas = await JanelasAsync(contexto);
        Assert.Equal(new[] { 3, 1 }, janelas.Select(j => j.Calls).ToArray());
        Assert.Equal(T0 + Hora, janelas[1].WindowStart);
    }

    // A janela: mudar a duração ou o limite não reinicia o consumo

    [Fact]
    public async Task Mudar_a_duracao_do_periodo_no_meio_da_janela_nao_reinicia_o_consumo_e_a_nova_duracao_vale_so_na_proxima_janela()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerduracao");
        var umaHora = Folgados with { Period = Hora, MaxTokensPerPeriod = 1000 };
        var quatroHoras = umaHora with { Period = TimeSpan.FromHours(4) };

        var primeira = (await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromMinutes(10), umaHora, entrada: 50, saida: 50))).Lease!;
        await ConcluirAsync(contexto, primeira, Registro(primeira, AiUsageCertainty.Reported, Uso(300, 200))); // 500 consumidos

        // A duração muda para 4 h NO MEIO da janela de 1 h: a janela ativa mantém início, fim e duração, e o consumo continua contado.
        var segunda = await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromMinutes(40), quatroHoras, entrada: 50, saida: 50));
        var mesmaJanela = Assert.Single(await JanelasAsync(contexto));
        Assert.NotNull(segunda.Lease);
        Assert.Equal((T0, T0 + Hora, 3600, 2, 500L), (mesmaJanela.WindowStart, mesmaJanela.WindowEnd, mesmaJanela.PeriodSeconds, mesmaJanela.Calls, mesmaJanela.ConfirmedTokens));

        // Acabada a janela, a próxima começa no FIM dela (nunca antes) e já com a duração nova.
        var terceira = await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromMinutes(90), quatroHoras));
        var janelas = await JanelasAsync(contexto);
        Assert.NotNull(terceira.Lease);
        Assert.Equal(2, janelas.Count);
        Assert.Equal((T0 + Hora, T0 + Hora + TimeSpan.FromHours(4), 14400), (janelas[1].WindowStart, janelas[1].WindowEnd, janelas[1].PeriodSeconds));
        Assert.True(janelas[1].WindowStart >= janelas[0].WindowEnd, "As janelas nunca se sobrepõem.");
        Assert.Equal(0L, janelas[1].ConfirmedTokens); // o consumo da janela nova é o dela; o da anterior continua na dela
    }

    [Fact]
    public async Task Reduzir_o_limite_abaixo_do_ja_usado_bloqueia_novas_chamadas_sem_estornar_nada()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerreduz");
        var vaga = (await IniciarAsync(contexto, Pedido(tenant, T0, Folgados))).Lease!;
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(300, 200))); // 500 usados

        var reduzido = await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromMinutes(1), Folgados with { MaxTokensPerPeriod = 400 }));
        var aumentado = await IniciarAsync(contexto, Pedido(tenant, T0 + TimeSpan.FromMinutes(2), Folgados with { MaxTokensPerPeriod = 2000 }));

        Assert.Equal(AiLimitRefusal.TokensPerPeriod, reduzido.Refusal);
        Assert.NotNull(aumentado.Lease); // aumentar vale na hora
        Assert.Equal(500L, Assert.Single(await JanelasAsync(contexto)).ConfirmedTokens); // nada foi estornado nem zerado
    }

    // Isolamento entre tenants

    [Fact]
    public async Task A_contabilidade_so_age_para_o_tenant_do_contexto_e_os_contadores_de_um_nao_aparecem_no_outro()
    {
        var (tenantA, contextoA) = await NovoTenantAsync("ledgerisoa");
        var (tenantB, contextoB) = await NovoTenantAsync("ledgerisob");

        // Pedir uma vaga do tenant B no contexto do tenant A é bloqueado antes do banco.
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => IniciarAsync(contextoA, Pedido(tenantB, T0)));
        var vagaA = (await IniciarAsync(contextoA, Pedido(tenantA, T0))).Lease!;
        var vagaB = (await IniciarAsync(contextoB, Pedido(tenantB, T0))).Lease!;
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => ConcluirAsync(contextoA, vagaB, Registro(vagaB, AiUsageCertainty.Reported, Uso(1, 1))));
        await ConcluirAsync(contextoA, vagaA, Registro(vagaA, AiUsageCertainty.Reported, Uso(10, 10)));

        var janelasA = await JanelasAsync(contextoA);
        var janelasB = await JanelasAsync(contextoB);
        Assert.Equal(20L, Assert.Single(janelasA).ConfirmedTokens);
        Assert.Equal(0L, Assert.Single(janelasB).ConfirmedTokens); // a conclusão de A nunca tocou em B
        Assert.Equal(0, await ComoAsync(contextoB, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.CountAsync(r => r.AiRequestId == vagaA.AiRequestId)));
    }

    [Fact]
    public async Task Os_limites_de_simultaneidade_sao_por_tenant_a_saturacao_de_um_nao_bloqueia_o_outro()
    {
        var (tenantA, contextoA) = await NovoTenantAsync("ledgersata");
        var (tenantB, contextoB) = await NovoTenantAsync("ledgersatb");
        var um = Folgados with { MaxConcurrentCalls = 1 };

        Assert.NotNull((await IniciarAsync(contextoA, Pedido(tenantA, T0, um))).Lease);
        Assert.Equal(AiLimitRefusal.ConcurrentCalls, (await IniciarAsync(contextoA, Pedido(tenantA, T0, um))).Refusal);

        Assert.NotNull((await IniciarAsync(contextoB, Pedido(tenantB, T0, um))).Lease);
    }

    // O gatilho e as restrições do banco

    private Task<int> SqlAsync(RequestContext contexto, string sql, params object[] parametros) =>
        ComoAsync(contexto, sp => sp.GetRequiredService<AiGatewayDbContext>().Database.ExecuteSqlRawAsync(sql, parametros));

    [Theory]
    [InlineData("state = 1, completed_at = null, applied_kind = 0, applied_tokens = 0")] // confirmada -> reservada
    [InlineData("reserved_tokens = reserved_tokens + 1")] // campo imutável
    [InlineData("window_start = window_start + interval '1 hour'")] // campo imutável
    [InlineData("estimated_input_tokens = 999")] // campo imutável
    [InlineData("applied_tokens = applied_tokens + 5")] // mudar o aplicado sem transição
    public async Task O_gatilho_do_banco_recusa_transicao_invalida_e_mudanca_do_que_ja_esta_aplicado(string alteracao)
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgergatilho");
        var vaga = (await IniciarAsync(contexto, Pedido(tenant, T0))).Lease!;
        await ConcluirAsync(contexto, vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(60, 20)));

        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(contexto, $"update ai_gateway.ai_requests set {alteracao} where tenant_id = {{0}} and ai_request_id = {{1}}", tenant.Value, vaga.AiRequestId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, falha.SqlState);
        Assert.Equal(80L, (await LinhaAsync(contexto, vaga.AiRequestId)).AppliedTokens); // nada mudou
    }

    [Fact]
    public async Task O_banco_recusa_estado_e_aplicado_incoerentes_e_contador_negativo()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerck");
        var vaga = (await IniciarAsync(contexto, Pedido(tenant, T0))).Lease!;

        // Reservada com aplicado diferente de zero, e confirmada sem conclusão.
        var a = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(contexto, "update ai_gateway.usage_windows set confirmed_tokens = -1 where tenant_id = {0}", tenant.Value));
        var b = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(contexto, "update ai_gateway.ai_requests set state = 2 where tenant_id = {0} and ai_request_id = {1}", tenant.Value, vaga.AiRequestId));

        Assert.Equal("ck_usage_windows_counters", a.ConstraintName);
        Assert.Equal(PostgresErrorCodes.CheckViolation, b.SqlState);
    }

    // Consistência do cache

    private async Task VerificarContadoresContraOAgregadoAsync(RequestContext contexto)
    {
        var (janelas, linhas) = await ComoAsync(contexto, async sp =>
        {
            var db = sp.GetRequiredService<AiGatewayDbContext>();
            return (await db.Windows.AsNoTracking().ToListAsync(), await db.Requests.AsNoTracking().ToListAsync());
        });

        foreach (var janela in janelas)
        {
            var daJanela = linhas.Where(l => l.WindowStart == janela.WindowStart).ToList();
            Assert.Equal(daJanela.Where(l => l.AppliedKind == 2).Sum(l => l.AppliedTokens), janela.ConfirmedTokens);
            Assert.Equal(daJanela.Where(l => l.AppliedKind == 3).Sum(l => l.AppliedTokens), janela.IndeterminateTokens);
            Assert.Equal(daJanela.Where(l => l.AppliedKind == 2).Sum(l => l.AppliedOverage), janela.OverageTokens);
            Assert.Equal(daJanela.Count, janela.Calls);
        }
    }

    [Fact]
    public async Task Depois_de_uma_sequencia_mista_os_contadores_da_janela_batem_com_o_agregado_do_ledger()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgeragreg");
        var aleatorio = new Random(20261104);
        var pendentes = new List<AiLease>();

        for (var passo = 0; passo < 60; passo++)
        {
            var agora = T0 + TimeSpan.FromSeconds(passo * 20);
            switch (aleatorio.Next(4))
            {
                case 0 or 1:
                    var concessao = await IniciarAsync(contexto, Pedido(tenant, agora, validade: TimeSpan.FromSeconds(aleatorio.Next(5, 90)), entrada: aleatorio.Next(10, 80), saida: aleatorio.Next(10, 120)));
                    if (concessao.Lease is { } nova)
                    {
                        pendentes.Add(nova);
                    }

                    break;
                case 2 when pendentes.Count > 0:
                    var escolhida = pendentes[aleatorio.Next(pendentes.Count)];
                    var certeza = (AiUsageCertainty)aleatorio.Next(1, 4);
                    await ConcluirAsync(contexto, escolhida, Registro(escolhida, certeza, Uso(aleatorio.Next(0, 200), aleatorio.Next(0, 200)), agora));
                    break;
                default:
                    await IniciarAsync(contexto, Pedido(tenant, agora));
                    break;
            }
        }

        await VerificarContadoresContraOAgregadoAsync(contexto);
    }

    // Falha fechada

    private static PostgresAiUsageLedger LedgerSemBanco(TenantId tenant)
    {
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(tenant, "teste-integracao"));
        var opcoes = new DbContextOptionsBuilder<AiGatewayDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 1, Database = "inexistente", Username = "x", Password = "x", Timeout = 2 }.ConnectionString)
            .Options;
        return new PostgresAiUsageLedger(new AiGatewayDbContext(opcoes, holder));
    }

    [Fact]
    public async Task Com_o_banco_fora_do_ar_o_pedido_de_vaga_e_a_conclusao_viram_AiLedgerUnavailableException()
    {
        var (tenant, _) = await NovoTenantAsync("ledgerfora");
        var ledger = LedgerSemBanco(tenant);

        await Assert.ThrowsAsync<AiLedgerUnavailableException>(() => ledger.TryBeginAsync(Pedido(tenant, T0), CancellationToken.None));
        var vaga = new AiLease(tenant, Guid.NewGuid(), T0, 100, T0 + TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<AiLedgerUnavailableException>(() => ledger.CompleteAsync(vaga, Registro(vaga, AiUsageCertainty.Reported, Uso(1, 1)), CancellationToken.None));
    }

    [Fact]
    public async Task Um_defeito_de_dados_nao_e_mascarado_como_indisponibilidade()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerdefeito");

        // Uma correlação maior que a coluna (100): é defeito de quem chamou (SQLSTATE 22001), não indisponibilidade do banco.
        var falha = await Assert.ThrowsAsync<PostgresException>(() => IniciarAsync(contexto, Pedido(tenant, T0, correlacao: new string('c', 101))));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, falha.SqlState);
        Assert.Empty(await JanelasAsync(contexto)); // a transação inteira foi desfeita
    }

    [Fact]
    public async Task Concluir_uma_vaga_que_nunca_foi_concedida_neste_tenant_e_defeito_e_nao_cria_consumo()
    {
        var (tenant, contexto) = await NovoTenantAsync("ledgerestranha");
        var estranha = new AiLease(tenant, Guid.NewGuid(), T0, 100, T0 + TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConcluirAsync(contexto, estranha, Registro(estranha, AiUsageCertainty.Reported, Uso(1, 1))));
        Assert.Empty(await JanelasAsync(contexto));
    }
}
