using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IaBimeta.IntegrationTests.Messaging;

/// <summary>
/// Um evento MORTO não pode sumir em silêncio (a falha de envio de uma mensagem, por exemplo, precisa chegar a quem a mostra ao atendente): o relay chama o tratamento de morto dos handlers que o pedem, depois de o
/// evento ser marcado como morto, e o REPETE até concluir, também depois de um reinício. E uma espera sem falha (<c>Defer</c>) nunca gasta tentativa, então nunca leva um evento aos mortos. PostgreSQL real, handler
/// sintético de roteiro e relógio de teste.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxRelayDeadLetterTests(PostgresFixture banco)
{
    private async Task<CenarioDeConversas> NovoAsync(string prefixo)
    {
        banco.RelogioDaMensageria.Sincronizar();
        return await CenarioDeConversas.CriarAsync(banco, prefixo, mensageria: true);
    }

    private static string Chave() => $"teste:{Guid.NewGuid():N}";

    // O tratamento do morto

    [Fact]
    public async Task Evento_que_morre_por_falha_permanente_aciona_o_tratamento_do_morto_uma_vez_com_o_motivo_e_o_tenant_do_evento()
    {
        var c = await NovoAsync("dlpermanente");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, RoteiroDeTeste.Morto("regra-violada"));
        await c.PublicarEventoDeTesteAsync(Chave(), evento);

        var resultado = await c.RodarRelayAsync();

        Assert.True(resultado.Dead >= 1);
        Assert.True(resultado.DeadHandled >= 1);
        var chamada = Assert.Single(RoteiroDeMortosDeTeste.De(evento));
        Assert.Equal(("regra-violada", c.Tenant.TenantId.Value, true), (chamada.Motivo, chamada.TenantId, chamada.EraSistema));

        // Tratado uma vez: o morto fica guardado (para a administração), marcado como tratado, e um novo ciclo não o trata de novo.
        var linha = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Dead, "regra-violada"), (linha.State, linha.DeadReason));
        Assert.NotNull(linha.ProcessedAt);
        await c.RodarRelayAsync();
        await c.Relay().SweepDeadLettersAsync();
        Assert.Single(RoteiroDeMortosDeTeste.De(evento));
    }

    [Fact]
    public async Task O_veneno_que_esgota_as_tentativas_tambem_aciona_o_tratamento_do_morto_com_o_motivo_do_esgotamento()
    {
        var c = await NovoAsync("dlveneno");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, Enumerable.Range(0, 4).Select(_ => RoteiroDeTeste.Repetir()).ToArray());
        await c.PublicarEventoDeTesteAsync(Chave(), evento);

        for (var tentativa = 0; tentativa < 4; tentativa++)
        {
            await c.RodarRelayAsync();
            banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(10));
        }

        Assert.Equal(OutboxState.Dead, (await c.LinhaDoOutboxAsync(evento))!.State);
        var chamada = Assert.Single(RoteiroDeMortosDeTeste.De(evento));
        Assert.Equal("max-attempts:transitorio", chamada.Motivo);
    }

    [Fact]
    public async Task O_evento_que_esgotou_as_tentativas_com_o_lease_vencido_vai_aos_mortos_e_aciona_o_tratamento_sem_ser_entregue_de_novo()
    {
        var c = await NovoAsync("dlmaxlease");
        var agora = banco.RelogioDaMensageria.GetUtcNow();
        var evento = await c.PublicarEventoDeTesteAsync(Chave());
        await c.AlterarOutboxAsync(evento, l =>
        {
            l.State = OutboxState.Processing;
            l.Attempts = 4;
            l.LeaseOwner = "relay-que-caiu";
            l.LeaseToken = Guid.NewGuid();
            l.LeaseExpiresAt = agora - TimeSpan.FromMinutes(1);
        });

        await c.RodarRelayAsync();

        Assert.Empty(RoteiroDeTeste.De(evento)); // não foi entregue mais uma vez
        var chamada = Assert.Single(RoteiroDeMortosDeTeste.De(evento));
        Assert.StartsWith("max-attempts", chamada.Motivo);
        Assert.NotNull((await c.LinhaDoOutboxAsync(evento))!.ProcessedAt);
    }

    [Fact]
    public async Task Tratamento_do_morto_que_pede_nova_tentativa_ou_falha_e_repetido_na_varredura_ate_concluir()
    {
        var c = await NovoAsync("dlrepete");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, RoteiroDeTeste.Morto("regra-violada"));
        RoteiroDeMortosDeTeste.Definir(evento, RoteiroDeMortosDeTeste.Repetir, RoteiroDeMortosDeTeste.Excecao(new InvalidOperationException("falha do tratamento")), RoteiroDeMortosDeTeste.Sucesso);
        await c.PublicarEventoDeTesteAsync(Chave(), evento);

        await c.RodarRelayAsync(); // 1ª: o relay pede nova tentativa
        Assert.Null((await c.LinhaDoOutboxAsync(evento))!.ProcessedAt);

        await c.Relay().SweepDeadLettersAsync(); // 2ª: exceção (só o tipo vai ao registro): continua por tratar
        Assert.Null((await c.LinhaDoOutboxAsync(evento))!.ProcessedAt);

        await c.Relay().SweepDeadLettersAsync(); // 3ª: conclui
        Assert.NotNull((await c.LinhaDoOutboxAsync(evento))!.ProcessedAt);
        Assert.Equal(3, RoteiroDeMortosDeTeste.De(evento).Count);

        await c.Relay().SweepDeadLettersAsync(); // tratado: não repete
        Assert.Equal(3, RoteiroDeMortosDeTeste.De(evento).Count);
    }

    [Fact]
    public async Task Evento_morto_ainda_sem_tratamento_depois_de_um_reinicio_e_tratado_na_primeira_varredura_do_novo_relay()
    {
        var c = await NovoAsync("dlreinicio");
        var evento = await c.PublicarEventoDeTesteAsync(Chave());

        // O processo caiu entre marcar o evento como morto e tratá-lo: a linha ficou Morta, sem a marca de tratada.
        await c.AlterarOutboxAsync(evento, l =>
        {
            l.State = OutboxState.Dead;
            l.DeadReason = "max-attempts:provider-transient";
            l.Attempts = 4;
            l.ProcessedAt = null;
        });
        Assert.Empty(RoteiroDeMortosDeTeste.De(evento));

        // Um relay NOVO (processo reiniciado) varre na primeira execução (a varredura nasce vencida) e trata o que ficou para trás.
        var provedor = c.Banco.ServicosComMensageria;
        var novo = new OutboxRelay<ConversationsDbContext>(
            provedor.GetRequiredService<IServiceScopeFactory>(),
            provedor.GetRequiredService<IMessagingScopeInitializer>(),
            provedor.GetRequiredService<MessagingOptions>(),
            provedor.GetRequiredService<TimeProvider>(),
            provedor.GetRequiredService<ILogger<OutboxRelay<ConversationsDbContext>>>());
        var resultado = await novo.RunOnceAsync("relay-reiniciado");

        Assert.True(resultado.DeadHandled >= 1);
        var chamada = Assert.Single(RoteiroDeMortosDeTeste.De(evento));
        Assert.Equal("max-attempts:provider-transient", chamada.Motivo);
        Assert.NotNull((await c.LinhaDoOutboxAsync(evento))!.ProcessedAt);
    }

    [Fact]
    public async Task Duas_varreduras_ao_mesmo_tempo_nunca_perdem_o_morto_e_o_deixam_tratado()
    {
        var c = await NovoAsync("dlconcorrente");
        var eventos = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var e = await c.PublicarEventoDeTesteAsync(Chave());
            await c.AlterarOutboxAsync(e, l =>
            {
                l.State = OutboxState.Dead;
                l.DeadReason = "regra-violada";
                l.ProcessedAt = null;
            });
            eventos.Add(e);
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => c.Relay().SweepDeadLettersAsync()));

        foreach (var e in eventos)
        {
            // Pelo menos uma vez (o tratamento é idempotente por contrato), e todos ficam marcados como tratados.
            Assert.NotEmpty(RoteiroDeMortosDeTeste.De(e));
            Assert.NotNull((await c.LinhaDoOutboxAsync(e))!.ProcessedAt);
        }
    }

    [Fact]
    public async Task O_tratamento_do_morto_roda_no_tenant_de_cada_evento_nunca_no_de_outro()
    {
        var a = await NovoAsync("dltenanta");
        var b = await CenarioDeConversas.CriarAsync(banco, "dltenantb", mensageria: true);
        var eventoA = Guid.NewGuid();
        var eventoB = Guid.NewGuid();
        RoteiroDeTeste.Definir(eventoA, RoteiroDeTeste.Morto("regra-violada"));
        RoteiroDeTeste.Definir(eventoB, RoteiroDeTeste.Morto("regra-violada"));
        await a.PublicarEventoDeTesteAsync(Chave(), eventoA);
        await b.PublicarEventoDeTesteAsync(Chave(), eventoB);

        await a.RodarRelayAsync();

        Assert.Equal(a.Tenant.TenantId.Value, Assert.Single(RoteiroDeMortosDeTeste.De(eventoA)).TenantId);
        Assert.Equal(b.Tenant.TenantId.Value, Assert.Single(RoteiroDeMortosDeTeste.De(eventoB)).TenantId);
    }

    [Fact]
    public async Task Reenfileirar_um_morto_limpa_a_marca_de_tratado_e_um_novo_fim_trata_de_novo()
    {
        var c = await NovoAsync("dlrequeue");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, RoteiroDeTeste.Morto("regra-violada"), RoteiroDeTeste.Morto("regra-violada-de-novo"));
        await c.PublicarEventoDeTesteAsync(Chave(), evento);
        await c.RodarRelayAsync();
        Assert.NotNull((await c.LinhaDoOutboxAsync(evento))!.ProcessedAt);

        Assert.True(await c.Relay().RequeueDeadAsync(c.Tenant.TenantId.Value, evento));
        var reenfileirado = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Pending, 0, null, null), (reenfileirado.State, reenfileirado.Attempts, reenfileirado.DeadReason, reenfileirado.ProcessedAt));

        await c.RodarRelayAsync();

        Assert.Equal(2, RoteiroDeMortosDeTeste.De(evento).Count);
        Assert.Equal("regra-violada-de-novo", RoteiroDeMortosDeTeste.De(evento)[1].Motivo);
    }

    // A espera sem falha (Defer)

    [Fact]
    public async Task A_espera_sem_falha_devolve_a_tentativa_nao_e_entregue_antes_da_hora_e_nunca_leva_aos_mortos()
    {
        var c = await NovoAsync("dldefer");
        var evento = Guid.NewGuid();

        // Dez adiamentos seguidos (o máximo de tentativas do teste é 4): se cada um gastasse uma tentativa, o evento já estaria morto.
        var roteiro = Enumerable.Range(0, 10).Select(indice => (Func<EventEnvelope, Task<HandlerResult>>)(envelope => Task.FromResult(HandlerResult.Defer(TimeSpan.FromMinutes(2), "rate-limited-local")))).ToList();
        roteiro.Add(RoteiroDeTeste.Sucesso);
        RoteiroDeTeste.Definir(evento, roteiro.ToArray());
        await c.PublicarEventoDeTesteAsync(Chave(), evento);

        var primeiro = await c.RodarRelayAsync();
        Assert.True(primeiro.Deferred >= 1);
        var depoisDoPrimeiro = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Pending, 0, "rate-limited-local"), (depoisDoPrimeiro.State, depoisDoPrimeiro.Attempts, depoisDoPrimeiro.LastError));
        Assert.True(depoisDoPrimeiro.NextAttemptAt >= banco.RelogioDaMensageria.GetUtcNow() + TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));

        // Antes da hora: não é entregue.
        await c.RodarRelayAsync();
        Assert.Single(RoteiroDeTeste.De(evento));

        for (var i = 0; i < 9; i++)
        {
            banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(3));
            await c.RodarRelayAsync();
            var linha = (await c.LinhaDoOutboxAsync(evento))!;
            Assert.Equal((OutboxState.Pending, 0), (linha.State, linha.Attempts));
        }

        banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(3));
        await c.RodarRelayAsync();

        var final = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Processed, 1), (final.State, final.Attempts));
        Assert.Equal(11, RoteiroDeTeste.De(evento).Count);
        Assert.Empty(RoteiroDeMortosDeTeste.De(evento));
    }

    [Fact]
    public async Task O_adiamento_segura_os_seguintes_da_mesma_chave_e_o_prazo_minimo_evita_girar_sem_parar()
    {
        var c = await NovoAsync("dldeferordem");
        var chave = Chave();
        var primeiro = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        RoteiroDeTeste.Definir(primeiro, _ => Task.FromResult(HandlerResult.Defer(TimeSpan.Zero, "sem-prazo")), RoteiroDeTeste.Sucesso);
        await c.PublicarEventoDeTesteAsync(chave, primeiro);
        await c.PublicarEventoDeTesteAsync(chave, segundo);

        await c.RodarRelayAsync();

        // Adiado com prazo zero: o relay impõe um prazo mínimo (não volta no mesmo ciclo), e o seguinte da mesma chave espera (ordem).
        Assert.Single(RoteiroDeTeste.De(primeiro));
        Assert.Empty(RoteiroDeTeste.De(segundo));
        var linha = (await c.LinhaDoOutboxAsync(primeiro))!;
        Assert.True(linha.NextAttemptAt > banco.RelogioDaMensageria.GetUtcNow());

        banco.RelogioDaMensageria.Avancar(TimeSpan.FromSeconds(1));
        await c.RodarRelayAsync();

        Assert.Equal(2, RoteiroDeTeste.De(primeiro).Count);
        Assert.Single(RoteiroDeTeste.De(segundo));
    }
}
