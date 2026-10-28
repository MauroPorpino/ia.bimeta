using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// Leitura de sessões e histórico com PostgreSQL real: paginação por cursor, isolamento entre tenants, autorização por
/// organização e recurso (TEN-2), e a proibição de um usuário autenticado chamar os comandos de máquina.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationsIsolationTests(PostgresFixture banco)
{
    private static async Task<List<string?>> TextosAsync(CenarioDeConversas c, Guid sessao, RequestContext leitor, int take, HistoryOrder ordem, List<int>? tamanhos = null)
    {
        var textos = new List<string?>();
        string? cursor = null;
        do
        {
            var pagina = (await c.HistoricoAsync(sessao, take, cursor, ordem, leitor))!;
            tamanhos?.Add(pagina.Items.Count);
            textos.AddRange(pagina.Items.Select(m => m.Text));
            cursor = pagina.NextCursor;
        }
        while (cursor is not null);
        return textos;
    }

    // Histórico paginado

    [Fact]
    public async Task O_historico_pagina_em_ordem_cronologica_sem_repetir_nem_perder_mensagens()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "pag");
        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        Guid sessao = default;
        for (var i = 1; i <= 25; i++)
        {
            sessao = (await c.EntrarAsync("5511999990001", $"m{i}", quando: t0.AddSeconds(i))).SessionId;
        }

        var tamanhos = new List<int>();
        var textos = await TextosAsync(c, sessao, leitor, 10, HistoryOrder.OldestFirst, tamanhos);

        Assert.Equal(new[] { 10, 10, 5 }, tamanhos);
        Assert.Equal(Enumerable.Range(1, 25).Select(i => $"m{i}"), textos);
    }

    [Fact]
    public async Task O_historico_da_mais_recente_para_a_mais_antiga_devolve_o_inverso_exato()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "pagdesc");
        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        Guid sessao = default;
        for (var i = 1; i <= 25; i++)
        {
            sessao = (await c.EntrarAsync("5511999990001", $"m{i}", quando: t0.AddSeconds(i))).SessionId;
        }

        var textos = await TextosAsync(c, sessao, leitor, 7, HistoryOrder.NewestFirst);

        Assert.Equal(Enumerable.Range(1, 25).Reverse().Select(i => $"m{i}"), textos);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(5)]
    public async Task A_ultima_pagina_nao_traz_cursor_mesmo_quando_o_total_e_multiplo_do_tamanho(int take)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "multiplo");
        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        Guid sessao = default;
        for (var i = 1; i <= 25; i++)
        {
            sessao = (await c.EntrarAsync("5511999990001", $"m{i}", quando: t0.AddSeconds(i))).SessionId;
        }

        var tamanhos = new List<int>();
        await TextosAsync(c, sessao, leitor, take, HistoryOrder.OldestFirst, tamanhos);

        Assert.Equal(25 / take, tamanhos.Count);
        Assert.All(tamanhos, t => Assert.Equal(take, t));
    }

    [Fact]
    public async Task Mensagem_nova_durante_a_paginacao_nao_repete_nem_pula_nenhuma()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "viva");
        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        Guid sessao = default;
        for (var i = 1; i <= 6; i++)
        {
            sessao = (await c.EntrarAsync("5511999990001", $"m{i}", quando: t0.AddSeconds(i))).SessionId;
        }

        var primeira = (await c.HistoricoAsync(sessao, 3, null, HistoryOrder.OldestFirst, leitor))!;
        await c.EntrarAsync("5511999990001", "m7", quando: t0.AddSeconds(7)); // chega entre as duas leituras
        var segunda = (await c.HistoricoAsync(sessao, 10, primeira.NextCursor, HistoryOrder.OldestFirst, leitor))!;

        Assert.Equal(new[] { "m1", "m2", "m3" }, primeira.Items.Select(m => m.Text));
        Assert.Equal(new[] { "m4", "m5", "m6", "m7" }, segunda.Items.Select(m => m.Text));
        Assert.Null(segunda.NextCursor);
    }

    [Fact]
    public async Task O_historico_mistura_entradas_e_respostas_na_ordem_cronologica_com_a_direcao_e_o_autor()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "mistura");
        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var pergunta = await c.EntrarAsync("5511999990001", "pergunta", quando: t0);
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(pergunta.SessionId, turno);
        await c.ResponderAsync(pergunta.SessionId, turno, inicio, pergunta.ConversationId, "resposta");
        await c.EntrarAsync("5511999990001", "seguinte", quando: DateTimeOffset.UtcNow.AddSeconds(2));

        var itens = (await c.HistoricoAsync(pergunta.SessionId, 10, null, HistoryOrder.OldestFirst, leitor))!.Items;

        Assert.Equal(new[] { "pergunta", "resposta", "seguinte" }, itens.Select(m => m.Text));
        Assert.Equal(new[] { MessageDirection.Inbound, MessageDirection.Outbound, MessageDirection.Inbound }, itens.Select(m => m.Direction));
        Assert.Equal(MessageAuthor.Ai, itens[1].Author);
        Assert.Equal(ReplyKind.Generated, itens[1].ReplyKind);
        Assert.Equal(1, itens[1].CoveredThroughSeq);
        Assert.Equal(DeliveryState.Requested, itens[1].DeliveryState);
        Assert.Equal(new long[] { 1, 2, 3 }, itens.Select(m => m.MessageSeq));
    }

    [Fact]
    public async Task Tamanho_de_pagina_cursor_e_ordem_invalidos_sao_recusados_como_erro_de_validacao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "valid");
        var r = await c.EntrarAsync("5511999990001");
        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var cursorDeSessao = (await c.ListarSessoesAsync(leitor, 1)).NextCursor
            ?? (await ComDuasSessoesAsync(c, leitor)).NextCursor!;

        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.HistoricoAsync(r.SessionId, 0, contexto: leitor));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.HistoricoAsync(r.SessionId, 101, contexto: leitor));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.HistoricoAsync(r.SessionId, 10, "isto-nao-e-um-cursor", contexto: leitor));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.HistoricoAsync(r.SessionId, 10, "%%%", contexto: leitor));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.HistoricoAsync(r.SessionId, 10, cursorDeSessao, contexto: leitor)); // cursor de outro tipo
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.HistoricoAsync(r.SessionId, 10, ordem: (HistoryOrder)99, contexto: leitor));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.ListarSessoesAsync(leitor, 0));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.ListarSessoesAsync(leitor, 101));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.ListarSessoesAsync(leitor, 10, "isto-nao-e-um-cursor"));
    }

    private static async Task<Page<SessionView>> ComDuasSessoesAsync(CenarioDeConversas c, RequestContext leitor)
    {
        await c.EntrarAsync("5511999990002");
        return await c.ListarSessoesAsync(leitor, 1);
    }

    // Lista de sessões

    [Fact]
    public async Task A_lista_de_sessoes_pagina_da_mais_recente_para_a_mais_antiga_sem_repetir_e_so_traz_o_proprio_tenant()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "lista");
        var outro = await CenarioDeConversas.CriarAsync(banco, "listaoutro");
        var criadas = new List<Guid>();
        for (var i = 1; i <= 7; i++)
        {
            criadas.Add((await c.EntrarAsync($"55119999900{i:00}")).SessionId);
            await outro.EntrarAsync($"55119999900{i:00}"); // o outro tenant tem as suas, com as mesmas referências de contato
            await Task.Delay(5);
        }

        var leitor = c.Usuario(null, Permission.ConversationRead);
        var vistas = new List<Guid>();
        var tamanhos = new List<int>();
        string? cursor = null;
        do
        {
            var pagina = await c.ListarSessoesAsync(leitor, 3, cursor);
            tamanhos.Add(pagina.Items.Count);
            vistas.AddRange(pagina.Items.Select(s => s.Id));
            cursor = pagina.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(new[] { 3, 3, 1 }, tamanhos);
        Assert.Equal(criadas.AsEnumerable().Reverse(), vistas);
    }

    [Fact]
    public async Task A_lista_filtra_por_status_da_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "status");
        var aberta = await c.EntrarAsync("5511999990001");
        var fechada = await c.EntrarAsync("5511999990002");
        Assert.True((await c.EncerrarSessaoAsync(fechada.SessionId)).Closed);
        var leitor = c.Usuario(null, Permission.ConversationRead);

        var abertas = await c.ListarSessoesAsync(leitor, 50, status: SessionStatus.Open);
        var fechadas = await c.ListarSessoesAsync(leitor, 50, status: SessionStatus.Closed);

        Assert.Equal(aberta.SessionId, Assert.Single(abertas.Items).Id);
        Assert.Equal(fechada.SessionId, Assert.Single(fechadas.Items).Id);
    }

    // Isolamento entre tenants

    [Fact]
    public async Task Usuario_de_um_tenant_nao_le_sessao_nem_historico_de_outro_e_a_resposta_e_a_de_inexistente()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "a");
        var b = await CenarioDeConversas.CriarAsync(banco, "b");
        var deA = await a.EntrarAsync("5511999990001", "segredo do tenant A");
        var deB = await b.EntrarAsync("5511999990001", "segredo do tenant B");
        var leitorDeB = b.Usuario(null, Permission.ConversationRead, Permission.ConversationManage);

        Assert.Null(await b.SessaoAsync(deA.SessionId, leitorDeB));
        Assert.Null(await b.HistoricoAsync(deA.SessionId, 10, contexto: leitorDeB));
        Assert.Null(await b.SessaoAsync(deA.SessionId, b.ContextoDeSistema));
        Assert.Null(await b.HistoricoAsync(deA.SessionId, 10, contexto: b.ContextoDeSistema));

        var lista = await b.ListarSessoesAsync(leitorDeB);
        Assert.Equal(deB.SessionId, Assert.Single(lista.Items).Id);

        // Sem rastro: nem a transição nem o encerramento nem o reenvio alcançam a sessão de outro tenant.
        Assert.Equal(TransitionOutcome.SessionNotFound, (await b.TransicionarAsync(deA.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), 1, contexto: leitorDeB)).Outcome);
        Assert.False((await b.EncerrarSessaoAsync(deA.SessionId, leitorDeB)).SessionFound);
        Assert.Equal(ResendOutcome.MessageNotFound, (await b.PedirReenvioAsync(deA.MessageId, Guid.NewGuid(), leitorDeB)).Outcome);

        var intacta = (await a.SessaoAsync(deA.SessionId))!;
        Assert.Equal((SessionStatus.Open, HandlingMode.AiAutonomous, 1L), (intacta.Status, intacta.HandlingMode, intacta.StateVersion));
    }

    [Fact]
    public async Task O_mesmo_id_de_sessao_ou_de_mensagem_nao_atravessa_o_tenant_nem_pelo_acesso_direto_ao_banco()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "a");
        var b = await CenarioDeConversas.CriarAsync(banco, "b");
        var deA = await a.EntrarAsync("5511999990001", "so do A");

        await using var escopoB = banco.NovoEscopo(b.ContextoDeSistema);
        var dbB = escopoB.ServiceProvider.GetRequiredService<ConversationsDbContext>();

        Assert.False(await dbB.Sessions.AnyAsync(s => s.Id == deA.SessionId));
        Assert.False(await dbB.Messages.AnyAsync(m => m.Id == deA.MessageId));
        Assert.False(await dbB.Conversations.AnyAsync(x => x.Id == deA.ConversationId));
        Assert.Equal(0, await dbB.Contacts.CountAsync());
    }

    // Autorização por organização e por recurso

    [Fact]
    public async Task O_usuario_so_ve_as_sessoes_das_organizacoes_em_que_tem_leitura_e_as_demais_respondem_como_inexistentes()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "orgs");
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização")).Id;
        var na1 = await c.EntrarAsync("5511999990001", "na organização 1");
        var na2 = await c.EntrarAsync("5511999990002", "na organização 2", organizacao: org2);

        var so1 = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var so2 = c.Usuario(org2, Permission.ConversationRead);
        var todas = c.Usuario(null, Permission.ConversationRead);

        Assert.Equal(na1.SessionId, Assert.Single((await c.ListarSessoesAsync(so1)).Items).Id);
        Assert.Equal(na2.SessionId, Assert.Single((await c.ListarSessoesAsync(so2)).Items).Id);
        Assert.Equal(2, (await c.ListarSessoesAsync(todas)).Items.Count);

        Assert.NotNull(await c.SessaoAsync(na1.SessionId, so1));
        Assert.Null(await c.SessaoAsync(na2.SessionId, so1));
        Assert.Null(await c.HistoricoAsync(na2.SessionId, 10, contexto: so1));
        Assert.Equal("na organização 1", Assert.Single((await c.HistoricoAsync(na1.SessionId, 10, contexto: so1))!.Items).Text);
    }

    [Fact]
    public async Task Sem_permissao_de_leitura_a_lista_e_negada_e_a_sessao_responde_como_inexistente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "semperm");
        var r = await c.EntrarAsync("5511999990001");
        var semLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead, Permission.StructureRead);
        var semNada = c.Usuario(c.OrganizacaoId);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ListarSessoesAsync(semLeitura));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ListarSessoesAsync(semNada));
        Assert.Null(await c.SessaoAsync(r.SessionId, semLeitura));
        Assert.Null(await c.HistoricoAsync(r.SessionId, 10, contexto: semLeitura));
        Assert.Null(await c.SessaoAsync(r.SessionId, semNada));
    }

    [Fact]
    public async Task Quem_le_mas_nao_gerencia_recebe_negado_e_quem_gerencia_so_o_faz_onde_tem_o_escopo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "gerencia");
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização")).Id;
        var r = await c.EntrarAsync("5511999990001");
        var soLeitura = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var gerenteDaOutra = c.Usuario(org2, Permission.ConversationRead, Permission.ConversationManage);
        var gerente = c.Usuario(c.OrganizacaoId, Permission.ConversationRead, Permission.ConversationManage);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), 1, contexto: soLeitura));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.EncerrarSessaoAsync(r.SessionId, soLeitura));
        Assert.Equal(TransitionOutcome.SessionNotFound, (await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), 1, contexto: gerenteDaOutra)).Outcome);
        Assert.Equal(1, (await c.SessaoAsync(r.SessionId))!.StateVersion); // nada mudou até aqui

        var atendente = Guid.NewGuid();
        var feito = await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, atendente, 1, contexto: gerente);

        Assert.Equal(TransitionOutcome.Applied, feito.Outcome);
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((HandlingMode.HumanAssisted, atendente, 2L), (sessao.HandlingMode, sessao.ResponderRef, sessao.StateVersion));
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_nenhuma_operacao_do_modulo_e_atendida()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "semctx");
        var r = await c.EntrarAsync("5511999990001");

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, s => s.GetRequiredService<IConversationQueries>().GetSessionAsync(r.SessionId)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, s => s.GetRequiredService<IConversationQueries>().ListSessionsAsync(new ListSessionsQuery(10, null, null))));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, s => s.GetRequiredService<IConversationQueries>().GetHistoryAsync(new GetHistoryQuery(r.SessionId, 10, null, HistoryOrder.OldestFirst))));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, s => s.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(
            new IngestInboundMessageCommand(c.CanalId, "5511999990001", null, c.OrganizacaoId, null, "evt-sem-contexto", DateTimeOffset.UtcNow, MessageKind.Text, "oi"))));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, s => s.GetRequiredService<ITurnCommands>().BeginTurnAsync(new BeginTurnCommand(r.SessionId, Guid.NewGuid(), 1))));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, s => s.GetRequiredService<IHandlingCommands>().CloseSessionAsync(new CloseSessionCommand(r.SessionId, "manual"))));

        Assert.Equal(1, (await c.SessaoAsync(r.SessionId))!.LastInboundSeq); // nada foi gravado
    }

    [Fact]
    public async Task Usuario_autenticado_nunca_chama_os_comandos_de_maquina_nem_com_todas_as_permissoes_de_conversa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "maquina");
        var r = await c.EntrarAsync("5511999990001", "oi");
        var midia = await c.EntrarAsync("5511999990001", null, tipo: MessageKind.Audio);
        var admin = c.Usuario(null, Permission.ConversationRead, Permission.ConversationManage, Permission.OrganizationRead, Permission.UserManage);

        Task<T> Como<T>(Func<IServiceProvider, Task<T>> acao) => c.ComoAsync(admin, acao);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(
            new IngestInboundMessageCommand(c.CanalId, "5511999990001", null, c.OrganizacaoId, null, "evt-do-usuario", DateTimeOffset.UtcNow, MessageKind.Text, "forjada"))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<IConversationIngestion>().SetMessageContentAsync(new SetMessageContentCommand(midia.MessageId, "transcrição forjada"))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<IConversationIngestion>().FailMessageContentAsync(new FailMessageContentCommand(midia.MessageId, "forjado"))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<IConversationIngestion>().RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(r.MessageId, DeliveryState.Delivered, null))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<ITurnCommands>().BeginTurnAsync(new BeginTurnCommand(r.SessionId, Guid.NewGuid(), 1))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<ITurnCommands>().EndTurnAsync(new EndTurnCommand(r.SessionId, Guid.NewGuid(), 1, TurnEndReason.Discarded))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<ITurnCommands>().CancelTurnAsync(new CancelTurnCommand(r.SessionId, "manual"))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<ITurnCommands>().RegisterAiReplyAsync(
            new RegisterAiReplyCommand(r.SessionId, Guid.NewGuid(), 1, 1, 1, ReplyKind.Generated, r.ConversationId, "resposta forjada", new[] { Guid.NewGuid() }))));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Como(s => s.GetRequiredService<ITurnCommands>().RaiseSessionAttentionAsync(
            new RaiseSessionAttentionCommand(r.SessionId, SessionAttentionReason.ChattyBurst, 1, 0))));

        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((1L, 2L, 0L, false), (sessao.StateVersion, sessao.LastInboundSeq, sessao.AnsweredThroughSeq, sessao.HasActiveTurn));
        var historico = (await c.HistoricoAsync(r.SessionId))!.Items;
        Assert.Equal(new[] { "oi", null }, historico.Select(m => m.Text));
        Assert.Equal(ContentState.Pending, historico[1].ContentState);
    }

    // Conteúdo pendente nunca é lido

    [Fact]
    public async Task O_historico_nao_mostra_texto_de_conteudo_que_ainda_nao_esta_pronto()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "pendente");
        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var imagem = await c.EntrarAsync("5511999990001", "legenda que não deve vazar", tipo: MessageKind.Image);

        var antes = (await c.HistoricoAsync(imagem.SessionId, 10, contexto: leitor))!.Items.Single();
        await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IConversationIngestion>().SetMessageContentAsync(new SetMessageContentCommand(imagem.MessageId, "texto extraído da imagem")));
        var depois = (await c.HistoricoAsync(imagem.SessionId, 10, contexto: leitor))!.Items.Single();

        Assert.Equal((ContentState.Pending, (string?)null), (antes.ContentState, antes.Text));
        Assert.Equal((ContentState.Ready, "texto extraído da imagem"), (depois.ContentState, depois.Text));
    }
}
