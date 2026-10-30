using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Messaging;

/// <summary>
/// A mecânica do relay do outbox sobre PostgreSQL real, com um handler sintético de roteiro e o relógio de teste (a espera entre
/// tentativas e o vencimento do lease se provam sem esperar): entrega pelo menos uma vez, espera exponencial, mortos, ordem por
/// chave, lease com fencing, exatamente um relay por evento sob concorrência e o tenant do contexto do handler.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxRelayTests(PostgresFixture banco)
{
    private async Task<CenarioDeConversas> NovoAsync(string prefixo)
    {
        banco.RelogioDaMensageria.Sincronizar();
        return await CenarioDeConversas.CriarAsync(banco, prefixo, mensageria: true);
    }

    private static string Chave() => $"teste:{Guid.NewGuid():N}";

    [Fact]
    public async Task A_entrega_com_sucesso_marca_processado_limpa_o_lease_e_o_handler_ve_o_tenant_do_evento_como_sistema()
    {
        var c = await NovoAsync("orsucesso");
        var evento = await c.PublicarEventoDeTesteAsync(Chave(), rotulo: "ola");

        var resultado = await c.RodarRelayAsync();

        Assert.True(resultado.Processed >= 1);
        var linha = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Processed, 1, null, null, null), (linha.State, linha.Attempts, linha.LeaseToken, linha.LeaseOwner, linha.LastError));
        Assert.NotNull(linha.ProcessedAt);
        var chamada = Assert.Single(RoteiroDeTeste.De(evento));
        Assert.Equal((c.Tenant.TenantId.Value, true), (chamada.TenantId, chamada.EraSistema));
    }

    [Fact]
    public async Task O_payload_chega_ao_handler_reconstruido_do_que_o_outbox_guardou_em_JSON()
    {
        var c = await NovoAsync("orpayload");
        string? visto = null;
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, e =>
        {
            visto = ((EventoDeTeste)e.Payload).Rotulo;
            return Task.FromResult(HandlerResult.Success);
        });
        await c.PublicarEventoDeTesteAsync(Chave(), evento, rotulo: "acentuação ç ã");

        await c.RodarRelayAsync();

        Assert.Equal("acentuação ç ã", visto);
        Assert.Contains("\"rotulo\"", (await c.LinhaDoOutboxAsync(evento))!.Payload);
    }

    [Fact]
    public async Task Evento_sem_consumidor_e_processado_sem_acumular_no_outbox()
    {
        var c = await NovoAsync("ormorto");
        var sessao = (await c.EntrarAsync("5511999990101")).SessionId;

        // A mensagem do cliente gera um evento que nenhum módulo consome ainda: entra no outbox e sai processado.
        var antes = (await c.LinhasDoOutboxDaSessaoAsync(sessao)).Single();
        Assert.Equal((OutboxState.Pending, "conversations.inbound-message-registered"), (antes.State, antes.EventType));

        await c.RodarRelayAsync();

        Assert.Equal(OutboxState.Processed, (await c.LinhaDoOutboxAsync(antes.Id))!.State);
    }

    [Fact]
    public async Task Falha_transitoria_espera_com_backoff_nao_e_reentregue_antes_da_hora_e_conclui_depois()
    {
        var c = await NovoAsync("orretry");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, RoteiroDeTeste.Repetir(), RoteiroDeTeste.Sucesso);
        await c.PublicarEventoDeTesteAsync(Chave(), evento);
        var agora = banco.RelogioDaMensageria.GetUtcNow();

        await c.RodarRelayAsync();

        var esperando = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Pending, 1, "transitorio", null), (esperando.State, esperando.Attempts, esperando.LastError, esperando.LeaseToken));
        // O backoff da primeira falha é de 5 a 10 s (base de 10 s com jitter): nunca imediato, nunca acima da base.
        Assert.InRange(esperando.NextAttemptAt - agora, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10.5));

        await c.RodarRelayAsync();
        Assert.Single(RoteiroDeTeste.De(evento)); // ainda não é hora

        banco.RelogioDaMensageria.Avancar(TimeSpan.FromSeconds(11));
        await c.RodarRelayAsync();

        var concluido = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Processed, 2, null), (concluido.State, concluido.Attempts, concluido.LastError));
        Assert.Equal(2, RoteiroDeTeste.De(evento).Count);
    }

    [Fact]
    public async Task A_espera_pedida_pelo_handler_vale_se_for_maior_que_o_backoff()
    {
        var c = await NovoAsync("orretryapos");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, RoteiroDeTeste.Repetir(TimeSpan.FromMinutes(3)));
        await c.PublicarEventoDeTesteAsync(Chave(), evento);
        var agora = banco.RelogioDaMensageria.GetUtcNow();

        await c.RodarRelayAsync();

        var linha = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.True(linha.NextAttemptAt - agora >= TimeSpan.FromMinutes(3) - TimeSpan.FromMilliseconds(1)); // o banco guarda microssegundos
    }

    [Fact]
    public async Task Falha_permanente_vai_para_os_mortos_com_o_motivo_e_a_fila_da_mesma_chave_segue()
    {
        var c = await NovoAsync("ordead");
        var chave = Chave();
        var primeiro = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        RoteiroDeTeste.Definir(primeiro, RoteiroDeTeste.Morto("regra-violada"));
        await c.PublicarEventoDeTesteAsync(chave, primeiro);
        await c.PublicarEventoDeTesteAsync(chave, segundo);

        await c.RodarRelayAsync();

        var morto = (await c.LinhaDoOutboxAsync(primeiro))!;
        Assert.Equal((OutboxState.Dead, "regra-violada", 1, null), (morto.State, morto.DeadReason, morto.Attempts, morto.LeaseToken));
        Assert.Equal(OutboxState.Processed, (await c.LinhaDoOutboxAsync(segundo))!.State);
    }

    [Fact]
    public async Task O_veneno_que_esgota_as_tentativas_vai_para_os_mortos_com_o_motivo_da_ultima_falha()
    {
        var c = await NovoAsync("orveneno");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, Enumerable.Range(0, 4).Select(_ => RoteiroDeTeste.Repetir()).ToArray());
        await c.PublicarEventoDeTesteAsync(Chave(), evento);

        for (var tentativa = 0; tentativa < 4; tentativa++)
        {
            await c.RodarRelayAsync();
            banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(10));
        }

        var linha = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Dead, 4, "max-attempts:transitorio"), (linha.State, linha.Attempts, linha.DeadReason));
        Assert.Equal(4, RoteiroDeTeste.De(evento).Count);

        // Morto fica guardado e não volta sozinho.
        await c.RodarRelayAsync();
        Assert.Equal(4, RoteiroDeTeste.De(evento).Count);
    }

    [Fact]
    public async Task Excecao_do_handler_e_falha_transitoria_e_so_o_tipo_vai_para_o_registro_nunca_a_mensagem()
    {
        var c = await NovoAsync("orexcecao");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, RoteiroDeTeste.Excecao(new InvalidOperationException("conteudo-secreto-do-cliente")));
        await c.PublicarEventoDeTesteAsync(Chave(), evento);

        await c.RodarRelayAsync();

        var linha = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Pending, "InvalidOperationException"), (linha.State, linha.LastError));
        Assert.DoesNotContain("secreto", linha.LastError!);
    }

    [Fact]
    public async Task Payload_ilegivel_vai_direto_para_os_mortos()
    {
        var c = await NovoAsync("orpayloadruim");
        var evento = await c.PublicarEventoDeTesteAsync(Chave());
        await c.AlterarOutboxAsync(evento, l => l.Payload = "{\"id\": \"isto-nao-e-um-guid\"}");

        await c.RodarRelayAsync();

        var linha = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Dead, "payload-invalid"), (linha.State, linha.DeadReason));
        Assert.Empty(RoteiroDeTeste.De(evento));
    }

    // Ordem por chave

    [Fact]
    public async Task Um_evento_em_nova_tentativa_segura_os_seguintes_da_mesma_chave_mas_nao_os_de_outras()
    {
        var c = await NovoAsync("orordem");
        var chave = Chave();
        var primeiro = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        var outraChave = Guid.NewGuid();
        RoteiroDeTeste.Definir(primeiro, RoteiroDeTeste.Repetir(), RoteiroDeTeste.Sucesso);
        await c.PublicarEventoDeTesteAsync(chave, primeiro);
        await c.PublicarEventoDeTesteAsync(chave, segundo);
        await c.PublicarEventoDeTesteAsync(Chave(), outraChave);

        await c.RodarRelayAsync();

        // O primeiro falhou e espera; o segundo (mesma chave) NÃO passa na frente; a outra chave segue.
        Assert.Equal(OutboxState.Pending, (await c.LinhaDoOutboxAsync(primeiro))!.State);
        Assert.Equal(OutboxState.Pending, (await c.LinhaDoOutboxAsync(segundo))!.State);
        Assert.Empty(RoteiroDeTeste.De(segundo));
        Assert.Equal(OutboxState.Processed, (await c.LinhaDoOutboxAsync(outraChave))!.State);

        banco.RelogioDaMensageria.Avancar(TimeSpan.FromSeconds(11));
        await c.RodarRelayAsync();

        var ordem = RoteiroDeTeste.De(primeiro).Concat(RoteiroDeTeste.De(segundo)).ToList();
        Assert.Equal(new[] { primeiro, primeiro, segundo }, ordem.OrderBy(x => x.Sequencia).Select(x => x.EventId).ToArray());
        Assert.Equal((OutboxState.Processed, OutboxState.Processed), ((await c.LinhaDoOutboxAsync(primeiro))!.State, (await c.LinhaDoOutboxAsync(segundo))!.State));
    }

    [Fact]
    public async Task Eventos_da_mesma_chave_sao_entregues_na_ordem_em_que_foram_gravados_mesmo_em_varios_ciclos()
    {
        var c = await NovoAsync("orordemlote");
        var chave = Chave();
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            ids.Add(await c.PublicarEventoDeTesteAsync(chave, rotulo: i.ToString()));
        }

        await c.RodarRelayAsync();

        var entregues = ids.SelectMany(RoteiroDeTeste.De).OrderBy(x => x.Sequencia).Select(x => x.EventId).ToList();
        Assert.Equal(ids, entregues);
        foreach (var id in ids)
        {
            Assert.Equal(OutboxState.Processed, (await c.LinhaDoOutboxAsync(id))!.State);
        }
    }

    // Lease

    [Fact]
    public async Task Evento_de_um_relay_que_caiu_volta_a_ser_entregue_depois_que_o_lease_vence_e_nao_antes()
    {
        var c = await NovoAsync("orlease");
        var evento = await c.PublicarEventoDeTesteAsync(Chave());
        var agora = banco.RelogioDaMensageria.GetUtcNow();
        await c.AlterarOutboxAsync(evento, l =>
        {
            l.State = OutboxState.Processing;
            l.Attempts = 1;
            l.LeaseOwner = "relay-que-caiu";
            l.LeaseToken = Guid.NewGuid();
            l.LeaseExpiresAt = agora + TimeSpan.FromMinutes(1);
        });

        await c.RodarRelayAsync();
        Assert.Empty(RoteiroDeTeste.De(evento)); // o lease ainda vale: o outro relay pode estar processando

        banco.RelogioDaMensageria.Avancar(TimeSpan.FromMinutes(2));
        await c.RodarRelayAsync();

        var linha = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Processed, 2), (linha.State, linha.Attempts));
        Assert.Single(RoteiroDeTeste.De(evento));
    }

    [Fact]
    public async Task O_evento_que_derruba_o_processo_a_cada_vez_chega_aos_mortos_pelo_maximo_de_tentativas_mesmo_sem_nenhuma_falha_registrada()
    {
        var c = await NovoAsync("ormaxlease");
        var agora = banco.RelogioDaMensageria.GetUtcNow();
        var esgotado = await c.PublicarEventoDeTesteAsync(Chave());
        var ultimaChance = await c.PublicarEventoDeTesteAsync(Chave());

        // Duas entregas de relays que caíram, com o lease vencido: uma já gastou todas as tentativas (4), a outra ainda tem uma.
        foreach (var (id, tentativas) in new[] { (esgotado, 4), (ultimaChance, 3) })
        {
            await c.AlterarOutboxAsync(id, l =>
            {
                l.State = OutboxState.Processing;
                l.Attempts = tentativas;
                l.LeaseOwner = "relay-que-caiu";
                l.LeaseToken = Guid.NewGuid();
                l.LeaseExpiresAt = agora - TimeSpan.FromMinutes(1);
            });
        }

        await c.RodarRelayAsync();

        var morto = (await c.LinhaDoOutboxAsync(esgotado))!;
        Assert.Equal(OutboxState.Dead, morto.State);
        Assert.StartsWith("max-attempts", morto.DeadReason);
        Assert.Null(morto.LeaseToken);
        Assert.Empty(RoteiroDeTeste.De(esgotado)); // o evento que já gastou as tentativas não é entregue mais uma vez

        var entregue = (await c.LinhaDoOutboxAsync(ultimaChance))!;
        Assert.Equal((OutboxState.Processed, 4), (entregue.State, entregue.Attempts));
        Assert.Single(RoteiroDeTeste.De(ultimaChance));
    }

    [Fact]
    public async Task As_metricas_de_entrega_so_contam_o_que_o_fencing_confirmou_o_lease_perdido_conta_so_como_lease_perdido()
    {
        var c = await NovoAsync("ormetricas");
        await c.RodarRelayAsync(); // esvazia o que outros testes deixaram elegível: a contagem abaixo é só deste cenário

        var contagens = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var ouvinte = new System.Diagnostics.Metrics.MeterListener();
        ouvinte.InstrumentPublished = (instrumento, listener) =>
        {
            if (instrumento.Meter.Name == MessagingMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrumento);
            }
        };
        ouvinte.SetMeasurementEventCallback<long>((instrumento, medida, _, _) => contagens.AddOrUpdate(instrumento.Name, medida, (_, atual) => atual + medida));
        ouvinte.Start();

        // Três entregas em que outro relay toma o lease durante o processamento: sucesso, nova tentativa e morte do handler.
        foreach (var resposta in new[] { RoteiroDeTeste.Sucesso, RoteiroDeTeste.Repetir(), RoteiroDeTeste.Morto("x") })
        {
            var evento = Guid.NewGuid();
            RoteiroDeTeste.Definir(evento, async e =>
            {
                await c.AlterarOutboxAsync(evento, l => l.LeaseToken = Guid.NewGuid());
                return await resposta(e);
            });
            await c.PublicarEventoDeTesteAsync(Chave(), evento);
        }

        var resultado = await c.RodarRelayAsync();

        Assert.Equal(3, resultado.LeaseLost);
        Assert.Equal((3L, 0L, 0L, 0L), (
            contagens.GetValueOrDefault("outbox.lease_lost"),
            contagens.GetValueOrDefault("outbox.processed"),
            contagens.GetValueOrDefault("outbox.retried"),
            contagens.GetValueOrDefault("outbox.dead")));
    }

    private static System.Diagnostics.Metrics.MeterListener OuvirMetricas(System.Collections.Concurrent.ConcurrentDictionary<string, long> contagens)
    {
        var ouvinte = new System.Diagnostics.Metrics.MeterListener();
        ouvinte.InstrumentPublished = (instrumento, listener) =>
        {
            if (instrumento.Meter.Name == MessagingMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrumento);
            }
        };
        ouvinte.SetMeasurementEventCallback<long>((instrumento, medida, _, _) => contagens.AddOrUpdate(instrumento.Name, medida, (_, atual) => atual + medida));
        ouvinte.Start();
        return ouvinte;
    }

    // Um evento cujo relay caiu com o lease vencido e que já gastou todas as tentativas (4): o próximo lease o manda para os mortos.
    private async Task<Guid> PublicarEventoComTentativasEsgotadasAsync(CenarioDeConversas c)
    {
        var evento = await c.PublicarEventoDeTesteAsync(Chave());
        var agora = banco.RelogioDaMensageria.GetUtcNow();
        await c.AlterarOutboxAsync(evento, l =>
        {
            l.State = OutboxState.Processing;
            l.Attempts = 4;
            l.LeaseOwner = "relay-que-caiu";
            l.LeaseToken = Guid.NewGuid();
            l.LeaseExpiresAt = agora - TimeSpan.FromMinutes(1);
        });
        return evento;
    }

    [Fact]
    public async Task O_esgotamento_das_tentativas_na_aquisicao_do_lease_entra_no_resultado_e_na_metrica_de_mortos_depois_do_commit()
    {
        var c = await NovoAsync("ormortocommit");
        await c.RodarRelayAsync(); // esvazia o que outros testes deixaram elegível: a contagem abaixo é só deste cenário
        var contagens = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var ouvinte = OuvirMetricas(contagens);
        var evento = await PublicarEventoComTentativasEsgotadasAsync(c);

        var resultado = await c.RodarRelayAsync();

        Assert.Equal(OutboxState.Dead, (await c.LinhaDoOutboxAsync(evento))!.State);
        Assert.Equal(1, resultado.Dead); // o resultado reflete o que foi marcado como morto, e só
        Assert.Equal((0, 0, 0), (resultado.Processed, resultado.Retried, resultado.LeaseLost));
        Assert.Equal(1L, contagens.GetValueOrDefault("outbox.dead"));
    }

    [Fact]
    public async Task Se_o_commit_do_lease_falha_nenhum_evento_e_contado_como_morto_e_ao_repetir_conta_uma_vez_so()
    {
        var c = await NovoAsync("ormortofalha");
        await c.RodarRelayAsync();
        var contagens = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var ouvinte = OuvirMetricas(contagens);
        var evento = await PublicarEventoComTentativasEsgotadasAsync(c);

        // A gravação do estado "morto" passa, e a falha só aparece no COMMIT (restrição adiada do banco): a transação desfaz tudo.
        var sufixo = Guid.NewGuid().ToString("N");
        await c.ExecutarSqlAsync(
            $"CREATE FUNCTION conversations.fn_teste_falha_{sufixo}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha simulada no commit'; END; $$");
        await c.ExecutarSqlAsync(
            $"CREATE CONSTRAINT TRIGGER trg_teste_falha_{sufixo} AFTER UPDATE ON conversations.outbox_message DEFERRABLE INITIALLY DEFERRED "
            + $"FOR EACH ROW WHEN (NEW.id = '{evento}'::uuid AND NEW.state = 4) EXECUTE FUNCTION conversations.fn_teste_falha_{sufixo}()");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => c.RodarRelayAsync());
        }
        finally
        {
            await c.ExecutarSqlAsync($"DROP TRIGGER trg_teste_falha_{sufixo} ON conversations.outbox_message");
            await c.ExecutarSqlAsync($"DROP FUNCTION conversations.fn_teste_falha_{sufixo}()");
        }

        var intacto = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Processing, 4), (intacto.State, intacto.Attempts)); // a transação foi desfeita
        Assert.Equal(0L, contagens.GetValueOrDefault("outbox.dead")); // e nada foi contado como morto

        // Repetindo sem a falha, o evento vai para os mortos e conta UMA vez.
        var resultado = await c.RodarRelayAsync();

        Assert.Equal(OutboxState.Dead, (await c.LinhaDoOutboxAsync(evento))!.State);
        Assert.Equal(1, resultado.Dead);
        Assert.Equal(1L, contagens.GetValueOrDefault("outbox.dead"));
    }

    [Fact]
    public async Task O_relay_que_perdeu_o_lease_nao_sobrescreve_o_estado_do_novo_dono()
    {
        var c = await NovoAsync("orfencing");
        var evento = Guid.NewGuid();
        var tokenDoOutro = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, async _ =>
        {
            // Durante a entrega, outro relay toma o evento (o lease deste venceu e outro pegou).
            await c.AlterarOutboxAsync(evento, l =>
            {
                l.LeaseToken = tokenDoOutro;
                l.LeaseOwner = "outro-relay";
            });
            return HandlerResult.Success;
        });
        await c.PublicarEventoDeTesteAsync(Chave(), evento);

        var resultado = await c.RodarRelayAsync();

        Assert.True(resultado.LeaseLost >= 1);
        var linha = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Processing, tokenDoOutro, "outro-relay"), (linha.State, linha.LeaseToken, linha.LeaseOwner)); // não foi marcado como processado por quem perdeu o lease
        Assert.Null(linha.ProcessedAt);
    }

    [Fact]
    public async Task Dois_relays_ao_mesmo_tempo_entregam_cada_evento_exatamente_uma_vez_e_cada_chave_em_ordem()
    {
        var c = await NovoAsync("orconcorrente");
        var ids = new List<Guid>();
        var chaveCompartilhada = Chave();
        for (var i = 0; i < 30; i++)
        {
            // Dez eventos na mesma chave (a ordem importa) e vinte em chaves próprias.
            ids.Add(await c.PublicarEventoDeTesteAsync(i < 10 ? chaveCompartilhada : Chave(), rotulo: i.ToString()));
        }

        var relays = await Task.WhenAll(Task.Run(() => c.RodarRelayAsync("relay-a")), Task.Run(() => c.RodarRelayAsync("relay-b")));

        Assert.True(relays.Sum(r => r.Processed) >= 30);
        foreach (var id in ids)
        {
            Assert.Single(RoteiroDeTeste.De(id));
            Assert.Equal(OutboxState.Processed, (await c.LinhaDoOutboxAsync(id))!.State);
        }

        var ordemDaChave = ids.Take(10).SelectMany(RoteiroDeTeste.De).OrderBy(x => x.Sequencia).Select(x => x.EventId).ToList();
        Assert.Equal(ids.Take(10).ToList(), ordemDaChave);
    }

    // Tenant e isolamento

    [Fact]
    public async Task O_handler_roda_no_contexto_do_tenant_do_envelope_de_cada_evento_nunca_no_de_outro()
    {
        var a = await NovoAsync("ortenanta");
        var b = await CenarioDeConversas.CriarAsync(banco, "ortenantb", mensageria: true);
        var deA = await a.PublicarEventoDeTesteAsync(Chave());
        var deB = await b.PublicarEventoDeTesteAsync(Chave());

        await a.RodarRelayAsync();

        Assert.Equal(a.Tenant.TenantId.Value, Assert.Single(RoteiroDeTeste.De(deA)).TenantId);
        Assert.Equal(b.Tenant.TenantId.Value, Assert.Single(RoteiroDeTeste.De(deB)).TenantId);
        Assert.All(RoteiroDeTeste.De(deA).Concat(RoteiroDeTeste.De(deB)), chamada => Assert.True(chamada.EraSistema));
        // A linha de cada tenant só aparece na consulta do próprio tenant.
        Assert.DoesNotContain(deB, (await a.LinhasDoOutboxDoTenantAsync()).Select(l => l.Id));
        Assert.DoesNotContain(deA, (await b.LinhasDoOutboxDoTenantAsync()).Select(l => l.Id));
    }

    [Fact]
    public async Task Gravar_no_outbox_evento_de_outro_tenant_que_o_da_operacao_e_recusado_e_nada_e_gravado()
    {
        var c = await NovoAsync("orescritor");
        var outro = new TenantId(Guid.NewGuid());
        var evento = Guid.NewGuid();

        var falha = await Assert.ThrowsAsync<InvalidOperationException>(() => c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ConversationsDbContext>();
            var agora = DateTimeOffset.UtcNow;
            sp.GetRequiredService<IOutboxWriter>().Stage(
                db,
                new[] { new EventEnvelope(evento, ManipuladorDeTeste.Tipo, 1, outro, Guid.NewGuid(), null, "x", agora, new EventoDeTeste(evento, "x")) },
                c.Tenant.TenantId,
                agora);
            await db.SaveChangesAsync();
            return 0;
        }));

        Assert.Contains("outro tenant", falha.Message);
        Assert.Null(await c.LinhaDoOutboxAsync(evento));
    }

    [Fact]
    public async Task O_evento_gravado_numa_transacao_desfeita_nao_existe_o_outbox_acompanha_a_transacao_do_modulo()
    {
        var c = await NovoAsync("oratomico");
        var evento = Guid.NewGuid();

        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ConversationsDbContext>();
            await using var transacao = await db.Database.BeginTransactionAsync();
            var agora = DateTimeOffset.UtcNow;
            sp.GetRequiredService<IOutboxWriter>().Stage(
                db,
                new[] { new EventEnvelope(evento, ManipuladorDeTeste.Tipo, 1, c.Tenant.TenantId, Guid.NewGuid(), null, "x", agora, new EventoDeTeste(evento, "x")) },
                c.Tenant.TenantId,
                agora);
            await db.SaveChangesAsync();
            await transacao.RollbackAsync();
            return 0;
        });

        Assert.Null(await c.LinhaDoOutboxAsync(evento));
    }

    // Administração e manutenção

    [Fact]
    public async Task Reenfileirar_um_morto_o_devolve_a_pendente_sem_tentativas_e_so_vale_para_morto()
    {
        var c = await NovoAsync("orreenfileira");
        var evento = Guid.NewGuid();
        RoteiroDeTeste.Definir(evento, RoteiroDeTeste.Morto("motivo"));
        await c.PublicarEventoDeTesteAsync(Chave(), evento);
        await c.RodarRelayAsync();
        Assert.Equal(OutboxState.Dead, (await c.LinhaDoOutboxAsync(evento))!.State);

        Assert.True(await c.Relay().RequeueDeadAsync(c.Tenant.TenantId.Value, evento));
        var pendente = (await c.LinhaDoOutboxAsync(evento))!;
        Assert.Equal((OutboxState.Pending, 0, null), (pendente.State, pendente.Attempts, pendente.DeadReason));
        Assert.False(await c.Relay().RequeueDeadAsync(c.Tenant.TenantId.Value, evento)); // já não está morto
        Assert.False(await c.Relay().RequeueDeadAsync(Guid.NewGuid(), evento));           // de outro tenant: inexistente

        await c.RodarRelayAsync();
        Assert.Equal(OutboxState.Processed, (await c.LinhaDoOutboxAsync(evento))!.State);
    }

    [Fact]
    public async Task A_purga_apaga_so_os_processados_mais_antigos_que_a_retencao()
    {
        var c = await NovoAsync("orpurga");
        var feito = await c.PublicarEventoDeTesteAsync(Chave());
        await c.RodarRelayAsync();
        var morto = Guid.NewGuid();
        RoteiroDeTeste.Definir(morto, RoteiroDeTeste.Morto("x"));
        await c.PublicarEventoDeTesteAsync(Chave(), morto);
        await c.RodarRelayAsync();
        var pendente = await c.PublicarEventoDeTesteAsync(Chave());

        // Dentro da retenção (1 dia), o processado não sai.
        await c.Relay().PurgeProcessedAsync();
        Assert.NotNull(await c.LinhaDoOutboxAsync(feito));

        banco.RelogioDaMensageria.Avancar(TimeSpan.FromDays(2));
        await c.Relay().PurgeProcessedAsync();

        Assert.Null(await c.LinhaDoOutboxAsync(feito));
        Assert.Equal(OutboxState.Dead, (await c.LinhaDoOutboxAsync(morto))!.State);
        Assert.Equal(OutboxState.Pending, (await c.LinhaDoOutboxAsync(pendente))!.State);
    }

    [Fact]
    public async Task A_profundidade_do_outbox_mostra_os_estados()
    {
        var c = await NovoAsync("orprofundidade");
        await c.PublicarEventoDeTesteAsync(Chave());

        var profundidade = await c.Relay().DepthAsync();

        Assert.True(profundidade.GetValueOrDefault(OutboxState.Pending) >= 1);
    }
}
