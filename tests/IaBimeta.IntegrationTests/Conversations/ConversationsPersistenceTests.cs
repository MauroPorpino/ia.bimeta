using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// Persistência do Conversations com PostgreSQL real: identificação de tenant, canal, contato, sessão e mensagem;
/// ingestão idempotente; ordem total por sessão sob concorrência; e as restrições do próprio banco (chaves compostas por
/// tenant, índices únicos e checks) que sustentam as invariantes mesmo contra código que erre.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationsPersistenceTests(PostgresFixture banco)
{
    private async Task<int> SqlAsync(RequestContext contexto, string sql, params object[] parametros)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>().Database.ExecuteSqlRawAsync(sql, parametros);
    }

    private async Task<T> ConsultaAsync<T>(RequestContext contexto, Func<ConversationsDbContext, Task<T>> consulta)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await consulta(escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>());
    }

    // Identificação e criação

    [Fact]
    public async Task A_primeira_mensagem_cria_contato_sessao_conversa_e_mensagem_com_as_sequencias()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "primeira");

        var resultado = await c.EntrarAsync("5511999990001", "Preciso de informação sobre o exame A");

        Assert.Equal(IngestOutcome.Registered, resultado.Outcome);
        Assert.Equal((1L, 1L), (resultado.InboundSeq, resultado.MessageSeq));
        Assert.Equal(ContentState.Ready, resultado.ContentState);
        Assert.False(resultado.LateArrival);

        var sessao = (await c.SessaoAsync(resultado.SessionId))!;
        Assert.Equal(SessionStatus.Open, sessao.Status);
        Assert.Equal(HandlingMode.AiAutonomous, sessao.HandlingMode);
        Assert.Null(sessao.ResponderRef);
        Assert.Equal(1, sessao.StateVersion);
        Assert.Equal(1, sessao.LastInboundSeq);
        Assert.Equal(0, sessao.AnsweredThroughSeq);
        Assert.False(sessao.HasActiveTurn);
        Assert.Equal(c.OrganizacaoId, sessao.OrganizationId);
        Assert.Equal(c.CanalId, sessao.ChannelInstanceId);

        var historico = (await c.HistoricoAsync(resultado.SessionId))!;
        var mensagem = Assert.Single(historico.Items);
        Assert.Equal(resultado.MessageId, mensagem.Id);
        Assert.Equal(resultado.ConversationId, mensagem.ConversationId);
        Assert.Equal(MessageDirection.Inbound, mensagem.Direction);
        Assert.Equal(MessageAuthor.Customer, mensagem.Author);
        Assert.Equal("Preciso de informação sobre o exame A", mensagem.Text);
    }

    [Fact]
    public async Task O_mesmo_contato_reaproveita_sessao_e_conversa_e_contato_novo_abre_outra_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "contatos");

        var a1 = await c.EntrarAsync("5511999990001");
        var a2 = await c.EntrarAsync("5511999990001");
        var b1 = await c.EntrarAsync("5511999990002");

        Assert.Equal(a1.SessionId, a2.SessionId);
        Assert.Equal(a1.ConversationId, a2.ConversationId);
        Assert.NotEqual(a1.SessionId, b1.SessionId);
        Assert.NotEqual(a1.ConversationId, b1.ConversationId);
        Assert.Equal(2, a2.InboundSeq);
        Assert.Equal(1, b1.InboundSeq); // a sequência é de cada sessão
    }

    [Fact]
    public async Task A_referencia_do_contato_e_aparada_e_a_caixa_importa_pois_e_o_identificador_do_provedor()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ref");

        var a = await c.EntrarAsync("  5511999990001  ");
        var b = await c.EntrarAsync("5511999990001");
        var maiuscula = await c.EntrarAsync("ABC");
        var minuscula = await c.EntrarAsync("abc");

        Assert.Equal(a.SessionId, b.SessionId);
        Assert.NotEqual(maiuscula.SessionId, minuscula.SessionId);
    }

    [Fact]
    public async Task O_mesmo_numero_em_canais_diferentes_e_contato_e_sessao_diferentes()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canais");

        var noCanal1 = await c.EntrarAsync("5511999990001", canal: ChannelInstanceId.New());
        var noCanal2 = await c.EntrarAsync("5511999990001", canal: ChannelInstanceId.New());

        Assert.NotEqual(noCanal1.SessionId, noCanal2.SessionId);
    }

    [Fact]
    public async Task O_evento_de_entrada_e_publicado_com_tenant_chave_de_ordem_e_conteudo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "evento");

        var r = await c.EntrarAsync("5511999990001", "Oi");

        var evento = Assert.IsType<InboundMessageRegisteredV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.InboundMessageRegistered, r.SessionId)));
        Assert.Equal(r.MessageId, evento.MessageId);
        Assert.Equal(1, evento.InboundSeq);
        Assert.Equal(ContentState.Ready, evento.ContentState);
        var envelope = banco.Eventos.Published.Single(e => e.TenantId == c.Tenant.TenantId && e.Type == ConversationEventTypes.InboundMessageRegistered && e.OrderingKey == $"session:{r.SessionId}");
        Assert.Equal(1, envelope.Version);
        Assert.NotEqual(Guid.Empty, envelope.EventId);
    }

    // Idempotência

    [Fact]
    public async Task O_mesmo_evento_do_provedor_repetido_devolve_a_mesma_mensagem_sem_novo_seq_e_sem_novo_evento()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idem");

        var primeira = await c.EntrarAsync("5511999990001", "Oi", evento: "wamid.UNICO");
        var repetida = await c.EntrarAsync("5511999990001", "Oi de novo", evento: "wamid.UNICO");

        Assert.Equal(IngestOutcome.Registered, primeira.Outcome);
        Assert.Equal(IngestOutcome.Duplicate, repetida.Outcome);
        Assert.Equal(primeira.MessageId, repetida.MessageId);
        Assert.Equal(primeira.InboundSeq, repetida.InboundSeq);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.InboundMessageRegistered, primeira.SessionId));

        var historico = (await c.HistoricoAsync(primeira.SessionId))!;
        var gravada = Assert.Single(historico.Items);
        Assert.Equal("Oi", gravada.Text); // vale a primeira gravação, a repetição não sobrescreve
        Assert.Equal(1, (await c.SessaoAsync(primeira.SessionId))!.LastInboundSeq);
    }

    [Fact]
    public async Task Trinta_entregas_simultaneas_do_mesmo_evento_gravam_uma_mensagem_e_geram_um_unico_processamento()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dup");
        // O contato já existe, para a corrida ser só do evento.
        var base1 = await c.EntrarAsync("5511999990001", "primeira");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => c.EntrarAsync("5511999990001", "repetida", evento: "wamid.CONCORRENTE")));

        Assert.Equal(1, resultados.Count(r => r.Outcome == IngestOutcome.Registered));
        Assert.Equal(29, resultados.Count(r => r.Outcome == IngestOutcome.Duplicate));
        Assert.Single(resultados.Select(r => r.MessageId).Distinct());
        Assert.Equal(2, (await c.SessaoAsync(base1.SessionId))!.LastInboundSeq);
        Assert.Equal(1, c.PayloadsDe(ConversationEventTypes.InboundMessageRegistered, base1.SessionId).Cast<InboundMessageRegisteredV1>().Count(e => e.InboundSeq == 2));
        Assert.Equal(2, (await c.HistoricoAsync(base1.SessionId))!.Items.Count);
    }

    [Fact]
    public async Task O_mesmo_id_de_evento_em_canais_ou_tenants_diferentes_sao_mensagens_diferentes()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "a");
        var b = await CenarioDeConversas.CriarAsync(banco, "b");

        var deA = await a.EntrarAsync("5511999990001", evento: "wamid.IGUAL");
        var deB = await b.EntrarAsync("5511999990001", evento: "wamid.IGUAL");
        var outroCanal = await a.EntrarAsync("5511999990001", evento: "wamid.IGUAL", canal: ChannelInstanceId.New());

        Assert.Equal(IngestOutcome.Registered, deA.Outcome);
        Assert.Equal(IngestOutcome.Registered, deB.Outcome);
        Assert.Equal(IngestOutcome.Registered, outroCanal.Outcome);
        Assert.Equal(3, new[] { deA.MessageId, deB.MessageId, outroCanal.MessageId }.Distinct().Count());
    }

    // Ordem sob concorrência

    [Fact]
    public async Task Quarenta_mensagens_simultaneas_da_mesma_sessao_recebem_uma_sequencia_total_sem_buracos_nem_repeticao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ordem");
        var primeira = await c.EntrarAsync("5511999990001", "m0");

        var resultados = await Task.WhenAll(Enumerable.Range(1, 40).Select(i => c.EntrarAsync("5511999990001", $"m{i}", evento: $"evt-{i}")));

        var todas = resultados.Append(primeira).ToList();
        Assert.All(todas, r => Assert.Equal(primeira.SessionId, r.SessionId));
        Assert.Equal(Enumerable.Range(1, 41).Select(i => (long)i), todas.Select(r => r.InboundSeq).OrderBy(s => s));
        Assert.Equal(Enumerable.Range(1, 41).Select(i => (long)i), todas.Select(r => r.MessageSeq).OrderBy(s => s));
        var sessao = (await c.SessaoAsync(primeira.SessionId))!;
        Assert.Equal(41, sessao.LastInboundSeq);

        var historico = (await c.HistoricoAsync(primeira.SessionId, take: 100))!.Items;
        Assert.Equal(41, historico.Count);
        Assert.Equal(41, historico.Select(m => m.MessageSeq).Distinct().Count());
    }

    [Fact]
    public async Task A_primeira_mensagem_de_um_contato_novo_chegando_em_paralelo_cria_uma_so_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "corrida");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => c.EntrarAsync("5511999990009", $"m{i}", evento: $"evt-{i}")));

        Assert.Single(resultados.Select(r => r.SessionId).Distinct());
        Assert.Single(resultados.Select(r => r.ConversationId).Distinct());
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), resultados.Select(r => r.InboundSeq).OrderBy(s => s));

        var quantosContatos = await ConsultaAsync(c.ContextoDeSistema, db => db.Contacts.CountAsync(x => x.ExternalRef == "5511999990009"));
        Assert.Equal(1, quantosContatos);
    }

    [Fact]
    public async Task Varias_sessoes_recebendo_em_paralelo_mantem_cada_uma_a_sua_propria_sequencia()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "paralelo");
        var contatos = Enumerable.Range(1, 8).Select(i => $"55119999900{i:00}").ToList();

        var chamadas = contatos.SelectMany(contato => Enumerable.Range(1, 10).Select(n => c.EntrarAsync(contato, $"{contato}-{n}", evento: $"{contato}-evt-{n}")));
        var resultados = await Task.WhenAll(chamadas);

        var porSessao = resultados.GroupBy(r => r.SessionId).ToList();
        Assert.Equal(8, porSessao.Count);
        Assert.All(porSessao, g => Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), g.Select(r => r.InboundSeq).OrderBy(s => s)));
    }

    // Chegada tardia (ORD-6)

    [Fact]
    public async Task Mensagem_atrasada_entra_marcada_na_posicao_cronologica_do_provedor_e_como_pendente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "tardia");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var primeira = await c.EntrarAsync("5511999990001", "pergunta 1", quando: t0);
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(primeira.SessionId, turno);
        var resposta = await c.ResponderAsync(primeira.SessionId, turno, inicio, primeira.ConversationId);
        Assert.Equal(RegisterReplyOutcome.Accepted, resposta.Outcome);

        // Chega agora, mas foi escrita no provedor ANTES da resposta que já foi aceita.
        var tardia = await c.EntrarAsync("5511999990001", "pergunta atrasada", quando: t0.AddSeconds(30));

        Assert.True(tardia.LateArrival);
        var historico = (await c.HistoricoAsync(primeira.SessionId))!.Items;
        Assert.Equal(new[] { "pergunta 1", "pergunta atrasada", "Sim, são 8 horas de jejum." }, historico.Select(m => m.Text));
        Assert.Equal(2, (await c.SessaoAsync(primeira.SessionId))!.LastInboundSeq);
        Assert.Equal(1, (await c.SessaoAsync(primeira.SessionId))!.AnsweredThroughSeq); // ela é pendente
    }

    // Estado do conteúdo

    [Fact]
    public async Task Midia_nasce_pendente_sem_texto_e_o_processador_a_deixa_pronta_ou_falha()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "midia");
        var audio = await c.EntrarAsync("5511999990001", texto: null, tipo: MessageKind.Audio);
        var imagem = await c.EntrarAsync("5511999990001", texto: "legenda ignorada", tipo: MessageKind.Image);
        Assert.Equal(ContentState.Pending, audio.ContentState);
        Assert.Equal(ContentState.Pending, imagem.ContentState);

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var ingestao = escopo.ServiceProvider.GetRequiredService<IConversationIngestion>();
            Assert.Equal(ContentChangeOutcome.Changed, (await ingestao.SetMessageContentAsync(new(audio.MessageId, "transcrição do áudio"))).Outcome);
            Assert.Equal(ContentChangeOutcome.Unchanged, (await ingestao.SetMessageContentAsync(new(audio.MessageId, "outra"))).Outcome);
            Assert.Equal(ContentChangeOutcome.Changed, (await ingestao.FailMessageContentAsync(new(imagem.MessageId, "ocr-timeout"))).Outcome);
            // MED-5: depois de falhar, o conteúdo pronto tarde não reabre nada.
            Assert.Equal(ContentChangeOutcome.NotAllowed, (await ingestao.SetMessageContentAsync(new(imagem.MessageId, "tarde"))).Outcome);
            Assert.Equal(ContentChangeOutcome.MessageNotFound, (await ingestao.SetMessageContentAsync(new(Guid.NewGuid(), "x"))).Outcome);
        }

        var historico = (await c.HistoricoAsync(audio.SessionId))!.Items;
        Assert.Equal(ContentState.Ready, historico.Single(m => m.Id == audio.MessageId).ContentState);
        Assert.Equal("transcrição do áudio", historico.Single(m => m.Id == audio.MessageId).Text);
        var falha = historico.Single(m => m.Id == imagem.MessageId);
        Assert.Equal(ContentState.Failed, falha.ContentState);
        Assert.Null(falha.Text);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.MessageContentReady, audio.SessionId));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.MessageContentFailed, audio.SessionId));
    }

    [Fact]
    public async Task Leitura_humana_e_snapshot_da_IA_escondem_o_texto_de_conteudo_nao_pronto_mesmo_que_o_banco_o_tenha()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "textoindevido");
        var texto = await c.EntrarAsync("5511999990001", "mensagem pronta");
        var audio = await c.EntrarAsync("5511999990001", null, tipo: MessageKind.Audio);
        var imagem = await c.EntrarAsync("5511999990001", null, tipo: MessageKind.Image);
        await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IConversationIngestion>().FailMessageContentAsync(new FailMessageContentCommand(imagem.MessageId, "ocr-timeout")));

        // Defesa em camadas: o domínio nem grava texto de conteúdo não pronto, mas as visões também não podem devolvê-lo
        // se um dado legado, uma carga manual ou um defeito futuro o deixar na tabela (I9: conteúdo indisponível nunca chega ao modelo).
        var afetadas = await SqlAsync(
            c.ContextoDeSistema,
            "update conversations.messages set text = 'texto indevido' where tenant_id = {0} and session_id = {1} and content_state <> 1",
            c.Tenant.TenantId.Value, texto.SessionId);
        Assert.Equal(2, afetadas);

        var leitura = (await c.HistoricoAsync(texto.SessionId))!.Items;
        var inicio = await c.IniciarTurnoAsync(texto.SessionId, Guid.NewGuid());

        Assert.Equal(new[] { "mensagem pronta", null, null }, leitura.Select(m => m.Text));
        Assert.Equal(new[] { ContentState.Ready, ContentState.Pending, ContentState.Failed }, leitura.Select(m => m.ContentState));
        Assert.Equal(new[] { "mensagem pronta", null, null }, inicio.Snapshot!.Messages.Select(m => m.Text));
        _ = audio;
    }

    // Organização e unidade

    [Fact]
    public async Task Organizacao_ou_unidade_inexistente_ou_de_outra_organizacao_e_recusada_e_nada_e_gravado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "org");
        var outraOrganizacao = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var unidades = escopo.ServiceProvider.GetRequiredService<BusinessUnitService>();
        var unidadeDaOutra = await unidades.CreateAsync(outraOrganizacao.Id, "Matriz da outra", BusinessUnitKind.Headquarters);
        var unidadeDaPropria = await unidades.CreateAsync(c.OrganizacaoId, "Matriz própria", BusinessUnitKind.Headquarters);

        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync("5511999990001", organizacao: Guid.NewGuid()));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync("5511999990001", unidade: Guid.NewGuid()));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync("5511999990001", unidade: unidadeDaOutra.Id));

        var valida = await c.EntrarAsync("5511999990001", unidade: unidadeDaPropria.Id);
        var sessao = (await c.SessaoAsync(valida.SessionId))!;
        Assert.Equal(unidadeDaPropria.Id, sessao.BusinessUnitId);
        Assert.Equal(1, valida.InboundSeq); // nenhuma das tentativas recusadas consumiu sequência
    }

    [Fact]
    public async Task Organizacao_de_outro_tenant_e_recusada_na_ingestao()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "a");
        var b = await CenarioDeConversas.CriarAsync(banco, "b");

        await Assert.ThrowsAsync<ConversationsValidationException>(() => b.EntrarAsync("5511999990001", organizacao: a.OrganizacaoId));
    }

    // Sessão encerrada

    [Fact]
    public async Task Mensagem_nova_de_contato_com_sessao_encerrada_abre_uma_nova_sessao_e_a_antiga_continua_encerrada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "encerrada");
        var antiga = await c.EntrarAsync("5511999990001", "antes");
        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var fechou = await escopo.ServiceProvider.GetRequiredService<IHandlingCommands>()
                .CloseSessionAsync(new CloseSessionCommand(antiga.SessionId, "manual"));
            Assert.True(fechou.Closed);
        }

        var nova = await c.EntrarAsync("5511999990001", "depois");

        Assert.NotEqual(antiga.SessionId, nova.SessionId);
        Assert.Equal(1, nova.InboundSeq);
        Assert.Equal(SessionStatus.Closed, (await c.SessaoAsync(antiga.SessionId))!.Status);
        Assert.Equal(SessionStatus.Open, (await c.SessaoAsync(nova.SessionId))!.Status);
        Assert.Single((await c.HistoricoAsync(antiga.SessionId))!.Items);
    }

    // Restrições do banco

    [Fact]
    public async Task O_banco_recusa_cobertura_maior_que_a_ultima_entrada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ck");
        var r = await c.EntrarAsync("5511999990001");

        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, "update conversations.service_sessions set answered_through_seq = 99 where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, r.SessionId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, falha.SqlState);
        Assert.Equal("ck_service_sessions_sequences", falha.ConstraintName);
    }

    [Fact]
    public async Task O_banco_recusa_versao_de_estado_zero_e_responsavel_humano_em_modo_de_IA()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ck2");
        var r = await c.EntrarAsync("5511999990001");

        var versao = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, "update conversations.service_sessions set state_version = 0 where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, r.SessionId));
        var responsavel = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, "update conversations.service_sessions set responder_ref = {2} where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, r.SessionId, Guid.NewGuid()));

        Assert.Equal("ck_service_sessions_versions", versao.ConstraintName);
        Assert.Equal("ck_service_sessions_responder", responsavel.ConstraintName);
    }

    [Fact]
    public async Task O_banco_recusa_uma_segunda_sessao_aberta_para_o_mesmo_contato()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "umaaberta");
        var r = await c.EntrarAsync("5511999990001");
        var contato = await ConsultaAsync(c.ContextoDeSistema, db => db.Sessions.Where(s => s.Id == r.SessionId).Select(s => s.ContactId).SingleAsync());

        var falha = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            c.ContextoDeSistema,
            "insert into conversations.service_sessions (tenant_id, id, organization_id, contact_id, channel_instance_id, status, handling_mode, state_version, last_message_seq, last_inbound_seq, answered_through_seq, turn_epoch, last_lease_token, reply_delivery_failed, created_at, last_message_at) " +
            "values ({0}, {1}, {2}, {3}, {4}, 1, 1, 1, 0, 0, 0, 0, 0, false, now(), now())",
            c.Tenant.TenantId.Value, Guid.NewGuid(), c.OrganizacaoId, contato, c.CanalId.Value));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, falha.SqlState);
        Assert.Equal("ux_service_sessions_open_chat", falha.ConstraintName);
    }

    [Fact]
    public async Task O_banco_recusa_sequencia_repetida_na_mesma_sessao_e_evento_repetido_no_canal()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "uq");
        var primeira = await c.EntrarAsync("5511999990001", evento: "evt-a");
        var segunda = await c.EntrarAsync("5511999990001", evento: "evt-b");

        var inbound = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, "update conversations.messages set inbound_seq = 1 where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, segunda.MessageId));
        var mensagem = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, "update conversations.messages set message_seq = 1 where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, segunda.MessageId));
        var evento = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, "update conversations.messages set provider_event_id = 'evt-a' where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, segunda.MessageId));

        Assert.Equal("ix_messages_tenant_id_session_id_inbound_seq", inbound.ConstraintName);
        Assert.Equal("ix_messages_tenant_id_session_id_message_seq", mensagem.ConstraintName);
        Assert.Equal("ix_messages_tenant_id_channel_instance_id_provider_event_id", evento.ConstraintName);
        _ = primeira;
    }

    [Fact]
    public async Task O_banco_recusa_duas_respostas_para_o_mesmo_turno()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "umaresp");
        var r = await c.EntrarAsync("5511999990001");
        var turnoUm = Guid.NewGuid();
        var inicio1 = await c.IniciarTurnoAsync(r.SessionId, turnoUm);
        Assert.Equal(RegisterReplyOutcome.Accepted, (await c.ResponderAsync(r.SessionId, turnoUm, inicio1, r.ConversationId)).Outcome);
        await c.EntrarAsync("5511999990001");
        var turnoDois = Guid.NewGuid();
        var inicio2 = await c.IniciarTurnoAsync(r.SessionId, turnoDois, lease: 2);
        var segunda = await c.ResponderAsync(r.SessionId, turnoDois, inicio2, r.ConversationId, "segunda");

        // Mesmo que o aplicativo errasse, o banco impede o segundo registro do mesmo turno.
        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, "update conversations.messages set turn_id = {2} where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, segunda.MessageId!.Value, turnoUm));

        Assert.Equal("ix_messages_one_reply_per_turn", falha.ConstraintName);
    }

    [Fact]
    public async Task O_banco_recusa_mensagem_do_cliente_sem_evento_do_provedor_pela_forma_da_direcao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "forma");
        var r = await c.EntrarAsync("5511999990001");

        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, "update conversations.messages set provider_event_id = null where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, r.MessageId));

        Assert.Equal("ck_messages_direction_shape", falha.ConstraintName);
    }

    [Fact]
    public async Task O_banco_recusa_mensagem_de_um_tenant_ligada_a_sessao_de_outro()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "a");
        var b = await CenarioDeConversas.CriarAsync(banco, "b");
        var deA = await a.EntrarAsync("5511999990001");

        // O tenant do registro é o do contexto (B), então o aplicativo deixa passar; quem recusa é a chave composta.
        var falha = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            b.ContextoDeSistema,
            "insert into conversations.messages (tenant_id, id, session_id, conversation_id, channel_instance_id, direction, message_seq, inbound_seq, author, kind, content_state, text, provider_event_id, received_at, occurred_at, late_arrival, created_at) " +
            "values ({0}, {1}, {2}, {3}, {4}, 1, 1, 1, 1, 1, 1, 'intruso', 'evt-intruso', now(), now(), false, now())",
            b.Tenant.TenantId.Value, Guid.NewGuid(), deA.SessionId, deA.ConversationId, b.CanalId.Value));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
    }

    // Isolamento no DbContext

    [Fact]
    public async Task Sem_contexto_a_consulta_e_a_gravacao_direta_ao_DbContext_falham_fechadas()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fechado");
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Sessions.ToListAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Messages.CountAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Contacts.AnyAsync());
        db.Contacts.Add(Contact.Create(c.Tenant.TenantId, c.CanalId, "5511999990001", null, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Gravar_contato_ou_mensagem_em_nome_de_outro_tenant_e_bloqueado_antes_do_banco()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "a");
        var b = await CenarioDeConversas.CriarAsync(banco, "b");
        var deA = await a.EntrarAsync("5511999990001");

        await using var escopoB = banco.NovoEscopo(b.ContextoDeSistema);
        var dbB = escopoB.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        dbB.Contacts.Add(Contact.Create(a.Tenant.TenantId, a.CanalId, "5511999990099", null, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        _ = deA;
    }

    [Fact]
    public async Task Alterar_ou_excluir_sessao_e_mensagem_de_outro_tenant_e_bloqueado()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "a");
        var b = await CenarioDeConversas.CriarAsync(banco, "b");
        var deA = await a.EntrarAsync("5511999990001");

        await using var escopoB = banco.NovoEscopo(b.ContextoDeSistema);
        var dbB = escopoB.ServiceProvider.GetRequiredService<ConversationsDbContext>();

        // O invasor contorna o filtro de leitura de propósito e tenta alterar e excluir.
        var sessao = await dbB.Sessions.IgnoreQueryFilters().SingleAsync(s => s.Id == deA.SessionId);
        sessao.NextOutboundSeq(DateTimeOffset.UtcNow); // muda LastMessageSeq: o EF marca a entidade como alterada
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        dbB.ChangeTracker.Clear();

        dbB.Messages.Remove(await dbB.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == deA.MessageId));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());

        Assert.Equal(0, (await a.SessaoAsync(deA.SessionId))!.StateVersion - 1); // intacta
    }
}
