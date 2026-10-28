using IaBimeta.Audit.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// Fundação multi-idioma do Conversations com PostgreSQL real: o idioma efetivo de cada conversa e a origem da escolha, a
/// mudança durante a conversa (com as cercas da <c>StateVersion</c>), conversas simultâneas em idiomas diferentes, o
/// conteúdo original preservado integralmente e as traduções guardadas à parte, tudo isolado por tenant. Não há tradução
/// nem detecção de idioma aqui: só a estrutura.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationsLocaleTests(PostgresFixture banco)
{
    private const string Contato = "5511999990001";

    private static RequestContext Atendente(CenarioDeConversas c) =>
        c.Usuario(c.OrganizacaoId, Permission.ConversationRead, Permission.ConversationManage);

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

    // Escolha do idioma de uma conversa nova

    [Fact]
    public async Task Sem_nenhuma_informacao_do_cliente_a_conversa_usa_o_padrao_do_tenant_marcado_como_suposicao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "suposicao");
        await c.DefinirIdiomaPadraoDoTenantAsync("en-US");

        var r = await c.EntrarAsync(Contato, "Hello");
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        var inicio = await c.IniciarTurnoAsync(r.SessionId, Guid.NewGuid());

        Assert.Equal((Locale.Parse("en-US"), (LocaleSource?)LocaleSource.TenantDefault), (sessao.EffectiveLocale, sessao.LocaleSource));
        Assert.Null(sessao.ContactPreferredLocale);
        Assert.Equal((r.ConversationId, Locale.Parse("en-US"), LocaleSource.TenantDefault), (inicio.Snapshot!.ConversationId, inicio.Snapshot.EffectiveLocale, inicio.Snapshot.LocaleSource));
        Assert.True(LocaleResolution.IsAssumed(inicio.Snapshot.LocaleSource));
    }

    [Fact]
    public async Task O_idioma_do_tenant_nao_e_o_do_cliente_a_dica_do_canal_vence_o_padrao_do_tenant()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dica");
        await c.DefinirIdiomaPadraoDoTenantAsync("en-US");

        var r = await c.EntrarAsync(Contato, "Hola", idiomaDoContato: "es-es");

        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((Locale.Parse("es-ES"), (LocaleSource?)LocaleSource.ChannelHint), (sessao.EffectiveLocale, sessao.LocaleSource));
    }

    [Fact]
    public async Task A_dica_do_canal_vale_so_na_criacao_e_nao_vira_preferencia_do_contato()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dicaunica");
        var primeira = await c.EntrarAsync(Contato, "Hola", idiomaDoContato: "es-ES");

        await c.EntrarAsync(Contato, "Hello", idiomaDoContato: "en-US");

        var sessao = (await c.SessaoAsync(primeira.SessionId))!;
        Assert.Equal((Locale.Parse("es-ES"), (LocaleSource?)LocaleSource.ChannelHint), (sessao.EffectiveLocale, sessao.LocaleSource));
        Assert.Null(sessao.ContactPreferredLocale);
    }

    [Fact]
    public async Task A_preferencia_do_contato_vence_a_dica_do_canal_nas_conversas_novas_e_a_antiga_nao_muda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "preferencia");
        var antiga = await c.EntrarAsync(Contato, "Bonjour", idiomaDoContato: "fr-FR");
        Assert.Equal(SetContactLocaleOutcome.Applied, (await c.DefinirPreferenciaDoContatoAsync(antiga.SessionId, "de-DE")).Outcome);
        Assert.True((await c.EncerrarSessaoAsync(antiga.SessionId)).Closed);

        var nova = await c.EntrarAsync(Contato, "Hello", idiomaDoContato: "en-US");

        var daNova = (await c.SessaoAsync(nova.SessionId))!;
        var daAntiga = (await c.SessaoAsync(antiga.SessionId))!;
        Assert.NotEqual(antiga.SessionId, nova.SessionId);
        Assert.Equal((Locale.Parse("de-DE"), (LocaleSource?)LocaleSource.ContactPreference), (daNova.EffectiveLocale, daNova.LocaleSource));
        Assert.Equal((Locale.Parse("fr-FR"), (LocaleSource?)LocaleSource.ChannelHint), (daAntiga.EffectiveLocale, daAntiga.LocaleSource));
        Assert.Equal(Locale.Parse("de-DE"), daAntiga.ContactPreferredLocale); // a preferência é do contato, em todas as sessões dele
    }

    [Fact]
    public async Task Mudar_o_idioma_padrao_do_tenant_nao_muda_as_conversas_existentes_so_as_novas()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "padraomuda");
        var existente = await c.EntrarAsync("5511999990001", "Olá");

        await c.DefinirIdiomaPadraoDoTenantAsync("fr-FR");
        var nova = await c.EntrarAsync("5511999990002", "Bonjour");

        Assert.Equal(Locale.Parse("pt-BR"), (await c.SessaoAsync(existente.SessionId))!.EffectiveLocale);
        Assert.Equal(Locale.Parse("fr-FR"), (await c.SessaoAsync(nova.SessionId))!.EffectiveLocale);
    }

    // Conversas simultâneas em idiomas diferentes

    private sealed record Cliente(string Contato, string? Dica, string Idioma, string Pergunta, string Resposta);

    private static readonly Cliente[] Clientes =
    {
        new("c-pt", "pt-BR", "pt-BR", "Preciso ficar em jejum para esse exame?", "Sim, 8 horas de jejum."),
        new("c-en", "en-US", "en-US", "Do I need to fast for this exam?", "Yes, 8 hours of fasting."),
        new("c-es", "es-ES", "es-ES", "¿Necesito ayuno para este examen?", "Sí, 8 horas de ayuno."),
        new("c-fr", "fr-FR", "fr-FR", "Dois-je être à jeun pour cet examen ?", "Oui, 8 heures de jeûne."),
        new("c-de", "de-DE", "de-DE", "Muss ich für diese Untersuchung nüchtern sein?", "Ja, 8 Stunden nüchtern."),
        new("c-ja", "ja-JP", "ja-JP", "この検査には絶食が必要ですか？", "はい、8時間の絶食が必要です。"),
        new("c-ar", "ar-EG", "ar-EG", "هل أحتاج إلى الصيام لهذا الفحص؟", "نعم، ٨ ساعات من الصيام."),
        new("c-sem-dica", null, "pt-BR", "Quanto custa o exame?", "O exame custa R$ 50."), // sem informação: vale o padrão do tenant (pt-BR), como suposição
    };

    private sealed record Fluxo(Cliente Cliente, IngestInboundMessageResult Entrada, BeginTurnResult Inicio, RegisterAiReplyResult Resposta);

    private static async Task<Fluxo> ConversarAsync(CenarioDeConversas c, Cliente cliente)
    {
        var entrada = await c.EntrarAsync(cliente.Contato, cliente.Pergunta, idiomaDoContato: cliente.Dica, idiomaDaMensagem: cliente.Dica);
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(entrada.SessionId, turno);
        var resposta = await c.ResponderAsync(entrada.SessionId, turno, inicio, entrada.ConversationId, cliente.Resposta);
        return new Fluxo(cliente, entrada, inicio, resposta);
    }

    private static async Task ConferirFluxoAsync(CenarioDeConversas c, Fluxo f)
    {
        var esperado = Locale.Parse(f.Cliente.Idioma);
        var origemEsperada = f.Cliente.Dica is null ? LocaleSource.TenantDefault : LocaleSource.ChannelHint;

        // O contexto da IA: só o idioma e o texto desta conversa.
        Assert.Equal((esperado, origemEsperada, f.Entrada.ConversationId), (f.Inicio.Snapshot!.EffectiveLocale, f.Inicio.Snapshot.LocaleSource, f.Inicio.Snapshot.ConversationId));
        Assert.Equal(new[] { f.Cliente.Pergunta }, f.Inicio.Snapshot.Messages.Select(m => m.Text));
        Assert.Equal(RegisterReplyOutcome.Accepted, f.Resposta.Outcome);

        var sessao = (await c.SessaoAsync(f.Entrada.SessionId))!;
        Assert.Equal((esperado, (LocaleSource?)origemEsperada), (sessao.EffectiveLocale, sessao.LocaleSource));

        var historico = (await c.HistoricoAsync(f.Entrada.SessionId))!.Items;
        Assert.Equal(new[] { f.Cliente.Pergunta, f.Cliente.Resposta }, historico.Select(m => m.Text));
        Assert.Equal(f.Cliente.Dica is null ? null : esperado, historico[0].Locale); // o idioma da mensagem do cliente é o declarado, ou desconhecido
        Assert.Equal(f.Cliente.Dica is null ? null : (MessageLocaleOrigin?)MessageLocaleOrigin.ProviderDeclared, historico[0].LocaleOrigin);
        Assert.Equal((esperado, (MessageLocaleOrigin?)MessageLocaleOrigin.ConversationLocale), (historico[1].Locale, historico[1].LocaleOrigin));
    }

    [Fact]
    public async Task Oito_clientes_do_mesmo_tenant_conversando_ao_mesmo_tempo_em_idiomas_diferentes_nunca_misturam_idioma_nem_texto()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "multiidioma");

        var fluxos = await Task.WhenAll(Clientes.Select(cliente => ConversarAsync(c, cliente)));

        Assert.Equal(Clientes.Length, fluxos.Select(f => f.Entrada.SessionId).Distinct().Count());
        foreach (var fluxo in fluxos)
        {
            await ConferirFluxoAsync(c, fluxo);
        }

        // Nenhuma conversa mudou de idioma por causa das outras.
        Assert.Equal(0, banco.Eventos.Published.Count(e => e.TenantId == c.Tenant.TenantId && e.Type == ConversationEventTypes.ConversationLocaleChanged));
    }

    [Fact]
    public async Task Dois_tenants_com_idiomas_padrao_diferentes_conversando_ao_mesmo_tempo_nao_se_influenciam()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "idiomaa");
        var b = await CenarioDeConversas.CriarAsync(banco, "idiomab");
        await a.DefinirIdiomaPadraoDoTenantAsync("en-US");
        await b.DefinirIdiomaPadraoDoTenantAsync("es-ES");

        // O mesmo contato e a mesma pergunta nos dois tenants, sem nenhuma dica: cada um recebe o padrão do próprio tenant.
        var chamadas = Enumerable.Range(0, 6).SelectMany(i => new[]
        {
            (Cenario: a, Contato: $"cli-{i}", Idioma: "en-US"),
            (Cenario: b, Contato: $"cli-{i}", Idioma: "es-ES"),
        });
        var entradas = await Task.WhenAll(chamadas.Select(async x => (x, Entrada: await x.Cenario.EntrarAsync(x.Contato, "mesma pergunta"))));

        foreach (var (x, entrada) in entradas)
        {
            var sessao = (await x.Cenario.SessaoAsync(entrada.SessionId))!;
            Assert.Equal((Locale.Parse(x.Idioma), (LocaleSource?)LocaleSource.TenantDefault), (sessao.EffectiveLocale, sessao.LocaleSource));
        }

        // E o tenant de um não enxerga a sessão do outro (nem o idioma dela).
        var deA = entradas.First(e => e.x.Cenario == a).Entrada;
        Assert.Null(await b.SessaoAsync(deA.SessionId));
    }

    // Mudança de idioma durante a conversa

    [Fact]
    public async Task Mudar_o_idioma_durante_a_conversa_recusa_a_resposta_em_andamento_e_preserva_o_historico_original()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "mudanca");
        var atendente = Atendente(c);

        // 1. O cliente escreve em português e a IA responde em português.
        var primeira = await c.EntrarAsync(Contato, "Preciso ficar em jejum para esse exame?", idiomaDaMensagem: "pt-BR");
        var turno1 = Guid.NewGuid();
        var inicio1 = await c.IniciarTurnoAsync(primeira.SessionId, turno1);
        Assert.Equal((Locale.Parse("pt-BR"), LocaleSource.TenantDefault), (inicio1.Snapshot!.EffectiveLocale, inicio1.Snapshot.LocaleSource));
        Assert.Equal(RegisterReplyOutcome.Accepted, (await c.ResponderAsync(primeira.SessionId, turno1, inicio1, primeira.ConversationId, "Sim, são 8 horas de jejum.")).Outcome);

        // 2. O cliente passa a escrever em inglês; um turno começa com o estado de ANTES da mudança.
        await c.EntrarAsync(Contato, "Can we continue in English, please?", idiomaDaMensagem: "en-US");
        var turno2 = Guid.NewGuid();
        var inicio2 = await c.IniciarTurnoAsync(primeira.SessionId, turno2, lease: 2);
        var versaoAntes = inicio2.Grant!.StateVersion;

        // 3. O atendente muda o idioma da conversa para inglês.
        var mudanca = await c.AlterarIdiomaAsync(primeira.SessionId, "en-US", versaoAntes, atendente);

        Assert.Equal((SetLocaleOutcome.Applied, versaoAntes + 1, Locale.Parse("en-US"), (LocaleSource?)LocaleSource.OperatorChoice), (mudanca.Outcome, mudanca.StateVersion!.Value, mudanca.EffectiveLocale, mudanca.Source));
        var evento = Assert.IsType<ConversationLocaleChangedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.ConversationLocaleChanged, primeira.SessionId)));
        Assert.Equal((primeira.ConversationId, Locale.Parse("pt-BR"), Locale.Parse("en-US"), LocaleSource.OperatorChoice, versaoAntes + 1), (evento.ConversationId, evento.From, evento.To, evento.Source, evento.StateVersion));

        // 4. A resposta escrita no idioma anterior é recusada: a StateVersion mudou. O turno e a cobertura não foram tocados.
        var tardia = await c.ResponderAsync(primeira.SessionId, turno2, inicio2, primeira.ConversationId, "Claro, em português mesmo.");
        Assert.Equal(RegisterReplyOutcome.StaleState, tardia.Outcome);
        var meio = (await c.SessaoAsync(primeira.SessionId))!;
        Assert.Equal((1L, true), (meio.AnsweredThroughSeq, meio.HasActiveTurn));

        // 5. Um turno novo já vê o idioma novo e a origem, e a resposta é aceita e registrada nele.
        var turno3 = Guid.NewGuid();
        var inicio3 = await c.IniciarTurnoAsync(primeira.SessionId, turno3, lease: 3);
        Assert.Equal((Locale.Parse("en-US"), LocaleSource.OperatorChoice), (inicio3.Snapshot!.EffectiveLocale, inicio3.Snapshot.LocaleSource));
        Assert.Equal(RegisterReplyOutcome.Accepted, (await c.ResponderAsync(primeira.SessionId, turno3, inicio3, primeira.ConversationId, "Of course, 8 hours of fasting.")).Outcome);

        // 6. O histórico é o original: textos e idiomas das mensagens anteriores não mudaram.
        var historico = (await c.HistoricoAsync(primeira.SessionId))!.Items;
        Assert.Equal(
            new[] { "Preciso ficar em jejum para esse exame?", "Sim, são 8 horas de jejum.", "Can we continue in English, please?", "Of course, 8 hours of fasting." },
            historico.Select(m => m.Text));
        Assert.Equal(
            new[] { Locale.Parse("pt-BR"), Locale.Parse("pt-BR"), Locale.Parse("en-US"), Locale.Parse("en-US") },
            historico.Select(m => m.Locale!));
        Assert.Equal(
            new[] { MessageLocaleOrigin.ProviderDeclared, MessageLocaleOrigin.ConversationLocale, MessageLocaleOrigin.ProviderDeclared, MessageLocaleOrigin.ConversationLocale },
            historico.Select(m => m.LocaleOrigin!.Value));
    }

    [Fact]
    public async Task A_mudanca_de_idioma_respeita_a_versao_esperada_e_e_idempotente_pela_mesma_escolha()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "versaoidioma");
        var r = await c.EntrarAsync(Contato, "Olá");
        var atendente = Atendente(c);

        var atrasada = await c.AlterarIdiomaAsync(r.SessionId, "en-US", 7, atendente);
        var aplicada = await c.AlterarIdiomaAsync(r.SessionId, "en-US", 1, atendente);
        var repetida = await c.AlterarIdiomaAsync(r.SessionId, "en-US", 2, atendente);
        var antigaDeNovo = await c.AlterarIdiomaAsync(r.SessionId, "es-ES", 1, atendente);

        Assert.Equal((SetLocaleOutcome.StaleState, (long?)1, (Locale?)Locale.Parse("pt-BR")), (atrasada.Outcome, atrasada.StateVersion, atrasada.EffectiveLocale));
        Assert.Equal((SetLocaleOutcome.Applied, (long?)2), (aplicada.Outcome, aplicada.StateVersion));
        Assert.Equal((SetLocaleOutcome.Unchanged, (long?)2), (repetida.Outcome, repetida.StateVersion));
        Assert.Equal(SetLocaleOutcome.StaleState, antigaDeNovo.Outcome);
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((Locale.Parse("en-US"), 2L), (sessao.EffectiveLocale, sessao.StateVersion));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.ConversationLocaleChanged, r.SessionId));
    }

    [Fact]
    public async Task Confirmar_o_idioma_assumido_pelo_cliente_troca_a_suposicao_por_escolha_sem_mudar_o_idioma()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "confirma");
        var r = await c.EntrarAsync(Contato, "Olá");
        Assert.True(LocaleResolution.IsAssumed((await c.SessaoAsync(r.SessionId))!.LocaleSource!.Value));

        var resultado = await c.AlterarIdiomaAsync(r.SessionId, "pt-BR", 1); // contexto de sistema: o cliente escolheu

        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((SetLocaleOutcome.Applied, Locale.Parse("pt-BR"), (LocaleSource?)LocaleSource.CustomerChoice, 2L), (resultado.Outcome, sessao.EffectiveLocale!, sessao.LocaleSource, sessao.StateVersion));
        Assert.False(LocaleResolution.IsAssumed(sessao.LocaleSource!.Value));
        var evento = Assert.IsType<ConversationLocaleChangedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.ConversationLocaleChanged, r.SessionId)));
        Assert.Equal((evento.From, evento.To), (Locale.Parse("pt-BR"), Locale.Parse("pt-BR")));
    }

    [Fact]
    public async Task Sessao_encerrada_ou_inexistente_nao_muda_de_idioma()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idiomafechada");
        var r = await c.EntrarAsync(Contato, "Olá");
        Assert.True((await c.EncerrarSessaoAsync(r.SessionId)).Closed);

        Assert.Equal(SetLocaleOutcome.SessionClosed, (await c.AlterarIdiomaAsync(r.SessionId, "en-US", 2)).Outcome);
        Assert.Equal(SetLocaleOutcome.SessionNotFound, (await c.AlterarIdiomaAsync(Guid.NewGuid(), "en-US", 1)).Outcome);
        Assert.Equal(Locale.Parse("pt-BR"), (await c.SessaoAsync(r.SessionId))!.EffectiveLocale);
    }

    [Fact]
    public async Task Quem_escolhe_o_idioma_vem_do_contexto_e_so_quem_gerencia_a_conversa_o_muda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "quemidioma");
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização")).Id;
        var r = await c.EntrarAsync(Contato, "Olá");
        var soLeitura = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var gerenteDeOutraOrganizacao = c.Usuario(org2, Permission.ConversationRead, Permission.ConversationManage);
        var semNada = c.Usuario(c.OrganizacaoId);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.AlterarIdiomaAsync(r.SessionId, "en-US", 1, soLeitura));
        Assert.Equal(SetLocaleOutcome.SessionNotFound, (await c.AlterarIdiomaAsync(r.SessionId, "en-US", 1, gerenteDeOutraOrganizacao)).Outcome);
        Assert.Equal(SetLocaleOutcome.SessionNotFound, (await c.AlterarIdiomaAsync(r.SessionId, "en-US", 1, semNada)).Outcome);
        Assert.Equal(Locale.Parse("pt-BR"), (await c.SessaoAsync(r.SessionId))!.EffectiveLocale); // nada mudou

        var feitoPeloAtendente = await c.AlterarIdiomaAsync(r.SessionId, "en-US", 1, Atendente(c));
        var feitoPeloCliente = await c.AlterarIdiomaAsync(r.SessionId, "es-ES", 2); // contexto de sistema

        Assert.Equal(LocaleSource.OperatorChoice, feitoPeloAtendente.Source);
        Assert.Equal(LocaleSource.CustomerChoice, feitoPeloCliente.Source);
        var origens = c.PayloadsDe(ConversationEventTypes.ConversationLocaleChanged, r.SessionId).Cast<ConversationLocaleChangedV1>().Select(e => e.Source);
        Assert.Equal(new[] { LocaleSource.OperatorChoice, LocaleSource.CustomerChoice }, origens);

        // A ação do usuário entra na trilha de auditoria.
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var auditadas = await escopo.ServiceProvider.GetRequiredService<AuditDbContext>().Events
            .CountAsync(e => e.Operation == "conversations.locale.change" && e.ResourceId == r.SessionId.ToString());
        Assert.Equal(1, auditadas);
    }

    [Fact]
    public async Task Dez_mudancas_simultaneas_de_idioma_a_partir_da_mesma_versao_aplicam_uma_so()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "corridaidioma");
        var r = await c.EntrarAsync(Contato, "Olá");
        var idiomas = new[] { "en-US", "es-ES", "fr-FR", "de-DE", "it-IT", "ja-JP", "ko-KR", "nl-NL", "sv-SE", "pl-PL" };

        var resultados = await Task.WhenAll(idiomas.Select(i => c.AlterarIdiomaAsync(r.SessionId, i, 1, Atendente(c))));

        Assert.Equal(1, resultados.Count(x => x.Outcome == SetLocaleOutcome.Applied));
        Assert.Equal(9, resultados.Count(x => x.Outcome == SetLocaleOutcome.StaleState));
        var vencedor = resultados.Single(x => x.Outcome == SetLocaleOutcome.Applied).EffectiveLocale;
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((vencedor, 2L), (sessao.EffectiveLocale, sessao.StateVersion));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.ConversationLocaleChanged, r.SessionId));
    }

    [Fact]
    public async Task Idioma_da_mensagem_do_cliente_nunca_e_presumido_nem_muda_com_o_idioma_da_conversa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "msgidioma");
        var sem = await c.EntrarAsync(Contato, "Hola, ¿qué tal?");
        await c.AlterarIdiomaAsync(sem.SessionId, "es-ES", 1, Atendente(c));
        var depois = await c.EntrarAsync(Contato, "Hola otra vez");

        var historico = (await c.HistoricoAsync(sem.SessionId))!.Items;

        // A conversa agora é em espanhol, mas nenhuma mensagem do cliente ganhou idioma por isso: ele só vem de declaração.
        Assert.Equal(new Locale?[] { null, null }, historico.Select(m => m.Locale));
        Assert.Equal(new MessageLocaleOrigin?[] { null, null }, historico.Select(m => m.LocaleOrigin));
        Assert.Equal(2, depois.InboundSeq);
    }

    // Preferência do contato

    [Fact]
    public async Task A_preferencia_do_contato_e_definida_limpa_e_so_por_quem_gerencia_sem_mexer_no_estado_da_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "prefcontato");
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização")).Id;
        var r = await c.EntrarAsync(Contato, "Olá");
        var atendente = Atendente(c);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.DefinirPreferenciaDoContatoAsync(r.SessionId, "es-ES", c.Usuario(c.OrganizacaoId, Permission.ConversationRead)));
        Assert.Equal(SetContactLocaleOutcome.SessionNotFound, (await c.DefinirPreferenciaDoContatoAsync(r.SessionId, "es-ES", c.Usuario(org2, Permission.ConversationRead, Permission.ConversationManage))).Outcome);
        Assert.Equal(SetContactLocaleOutcome.SessionNotFound, (await c.DefinirPreferenciaDoContatoAsync(Guid.NewGuid(), "es-ES", atendente)).Outcome);
        Assert.Null((await c.SessaoAsync(r.SessionId))!.ContactPreferredLocale);

        Assert.Equal(SetContactLocaleOutcome.Applied, (await c.DefinirPreferenciaDoContatoAsync(r.SessionId, "es-es", atendente)).Outcome);
        Assert.Equal(SetContactLocaleOutcome.Unchanged, (await c.DefinirPreferenciaDoContatoAsync(r.SessionId, "es-ES", atendente)).Outcome);
        var comPreferencia = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((Locale.Parse("es-ES"), 1L, Locale.Parse("pt-BR")), (comPreferencia.ContactPreferredLocale!, comPreferencia.StateVersion, comPreferencia.EffectiveLocale!));

        Assert.Equal(SetContactLocaleOutcome.Applied, (await c.DefinirPreferenciaDoContatoAsync(r.SessionId, null, atendente)).Outcome);
        Assert.Null((await c.SessaoAsync(r.SessionId))!.ContactPreferredLocale);
    }

    // Conteúdo original e traduções à parte

    [Fact]
    public async Task O_conteudo_original_e_preservado_integralmente_no_banco_e_a_traducao_e_um_registro_a_parte()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "original");
        const string original = "  ¿Necesito ayuno para este examen? 😀\n\tLínea 2 — con acentos, emoji y espacios finales   ";
        var r = await c.EntrarAsync(Contato, original, idiomaDaMensagem: "es-ES");
        var antes = await ConsultaAsync(c.ContextoDeSistema, db => db.Messages.Where(m => m.Id == r.MessageId).Select(m => new { m.Text, m.Locale, m.LocaleOrigin, m.ContentState }).SingleAsync());

        var traducao = await c.TraduzirAsync(r.MessageId, "pt-BR", "Preciso de jejum para este exame?", "human");
        await c.AlterarIdiomaAsync(r.SessionId, "pt-BR", 1, Atendente(c)); // a conversa muda de idioma depois da tradução

        var depois = await ConsultaAsync(c.ContextoDeSistema, db => db.Messages.Where(m => m.Id == r.MessageId).Select(m => new { m.Text, m.Locale, m.LocaleOrigin, m.ContentState }).SingleAsync());
        var historico = (await c.HistoricoAsync(r.SessionId))!.Items.Single();
        var traducoes = (await c.TraducoesAsync(r.SessionId, r.MessageId))!;

        Assert.Equal(RecordTranslationOutcome.Recorded, traducao.Outcome);
        Assert.Equal(original, antes.Text);
        Assert.Equal(antes, depois); // a tradução e a mudança de idioma não tocaram no original
        Assert.Equal(original, historico.Text);
        Assert.Equal((Locale.Parse("es-ES"), (MessageLocaleOrigin?)MessageLocaleOrigin.ProviderDeclared), (historico.Locale, historico.LocaleOrigin));
        var unica = Assert.Single(traducoes);
        Assert.Equal((Locale.Parse("pt-BR"), (Locale?)Locale.Parse("es-ES"), "Preciso de jejum para este exame?", "human"), (unica.TargetLocale, unica.SourceLocale, unica.Text, unica.Provenance));
        Assert.NotEqual(original, unica.Text);
    }

    [Fact]
    public async Task A_traducao_e_idempotente_nunca_sobrescreve_e_cada_idioma_de_destino_tem_a_sua()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "traducaoidem");
        var r = await c.EntrarAsync(Contato, "Hello, do I need to fast?", idiomaDaMensagem: "en-US");

        var primeira = await c.TraduzirAsync(r.MessageId, "pt-BR", "Olá, preciso de jejum?");
        var repetida = await c.TraduzirAsync(r.MessageId, "pt-BR", "Olá, preciso de jejum?");
        var diferente = await c.TraduzirAsync(r.MessageId, "pt-BR", "Oi, preciso ficar em jejum?", "machine.provider-x");
        var outroIdioma = await c.TraduzirAsync(r.MessageId, "es-ES", "Hola, ¿necesito ayuno?");

        Assert.Equal(RecordTranslationOutcome.Recorded, primeira.Outcome);
        Assert.Equal((RecordTranslationOutcome.AlreadyRecorded, primeira.TranslationId), (repetida.Outcome, repetida.TranslationId));
        Assert.Equal((RecordTranslationOutcome.Conflict, primeira.TranslationId), (diferente.Outcome, diferente.TranslationId));
        Assert.Equal(RecordTranslationOutcome.Recorded, outroIdioma.Outcome);
        var traducoes = (await c.TraducoesAsync(r.SessionId, r.MessageId))!;
        Assert.Equal(new[] { "Olá, preciso de jejum?", "Hola, ¿necesito ayuno?" }, traducoes.Select(t => t.Text)); // a primeira nunca foi sobrescrita
        Assert.Equal(new[] { "human", "human" }, traducoes.Select(t => t.Provenance));
        Assert.Equal("Hello, do I need to fast?", (await c.HistoricoAsync(r.SessionId))!.Items.Single().Text);
    }

    [Fact]
    public async Task Dez_traducoes_simultaneas_iguais_gravam_uma_so()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "traducaocorrida");
        var r = await c.EntrarAsync(Contato, "Bonjour");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => c.TraduzirAsync(r.MessageId, "pt-BR", "Bom dia")));

        Assert.Equal(1, resultados.Count(x => x.Outcome == RecordTranslationOutcome.Recorded));
        Assert.Equal(9, resultados.Count(x => x.Outcome == RecordTranslationOutcome.AlreadyRecorded));
        Assert.Single(resultados.Select(x => x.TranslationId).Distinct());
        Assert.Single((await c.TraducoesAsync(r.SessionId, r.MessageId))!);
    }

    [Fact]
    public async Task Conteudo_nao_pronto_ou_inexistente_nao_e_traduzido_e_entrada_invalida_e_recusada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "traducaoinvalida");
        var audio = await c.EntrarAsync(Contato, null, tipo: MessageKind.Audio);
        var texto = await c.EntrarAsync(Contato, "Oi");

        Assert.Equal(RecordTranslationOutcome.ContentNotReady, (await c.TraduzirAsync(audio.MessageId, "en-US", "Hi")).Outcome);
        await c.ComoAsync(c.ContextoDeSistema, s => s.GetRequiredService<IConversationIngestion>().SetMessageContentAsync(new SetMessageContentCommand(audio.MessageId, "transcrição")));
        Assert.Equal(RecordTranslationOutcome.Recorded, (await c.TraduzirAsync(audio.MessageId, "en-US", "transcription")).Outcome);
        Assert.Equal(RecordTranslationOutcome.MessageNotFound, (await c.TraduzirAsync(Guid.NewGuid(), "en-US", "x")).Outcome);

        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.TraduzirAsync(texto.MessageId, "en-US", "   "));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.TraduzirAsync(texto.MessageId, "en-US", new string('x', ConversationLimits.TextMaxLength + 1)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.TraduzirAsync(texto.MessageId, "en-US", "Hi", "Tradutor Humano"));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.TraduzirAsync(texto.MessageId, "en-US", "Hi", ""));
        Assert.Empty((await c.TraducoesAsync(texto.SessionId, texto.MessageId))!);
    }

    [Fact]
    public async Task So_um_contexto_de_sistema_grava_traducao_e_um_usuario_nunca_mesmo_administrador()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "traducaosistema");
        var r = await c.EntrarAsync(Contato, "Oi");
        var admin = c.Usuario(null, Permission.ConversationRead, Permission.ConversationManage, Permission.OrganizationRead, Permission.UserManage);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.TraduzirAsync(r.MessageId, "en-US", "Hi", contexto: admin));
        Assert.Empty((await c.TraducoesAsync(r.SessionId, r.MessageId))!);
    }

    [Fact]
    public async Task A_leitura_de_traducoes_respeita_tenant_sessao_e_permissao()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "trada");
        var b = await CenarioDeConversas.CriarAsync(banco, "tradb");
        var org2 = (await banco.CriarOrganizacaoAsync(a.Tenant, "Segunda organização")).Id;
        var deA = await a.EntrarAsync(Contato, "Hello");
        var outraSessaoDeA = await a.EntrarAsync("5511999990002", "Hi");
        var daOrg2 = await a.EntrarAsync("5511999990003", "Hey", organizacao: org2);
        var deB = await b.EntrarAsync(Contato, "Hello");
        await a.TraduzirAsync(deA.MessageId, "pt-BR", "Olá");
        await b.TraduzirAsync(deB.MessageId, "pt-BR", "Olá do tenant B");

        var leitor = a.Usuario(a.OrganizacaoId, Permission.ConversationRead);

        Assert.Equal("Olá", Assert.Single((await a.TraducoesAsync(deA.SessionId, deA.MessageId, leitor))!).Text);
        Assert.Null(await a.TraducoesAsync(deA.SessionId, deA.MessageId, a.Usuario(a.OrganizacaoId))); // sem leitura
        Assert.Null(await a.TraducoesAsync(daOrg2.SessionId, daOrg2.MessageId, leitor)); // outra organização
        Assert.Null(await a.TraducoesAsync(outraSessaoDeA.SessionId, deA.MessageId, leitor)); // a mensagem não é desta sessão
        Assert.Null(await a.TraducoesAsync(deA.SessionId, Guid.NewGuid(), leitor)); // mensagem inexistente
        Assert.Null(await b.TraducoesAsync(deA.SessionId, deA.MessageId)); // outro tenant, mesmo com contexto de sistema
        Assert.Null(await b.TraducoesAsync(deB.SessionId, deA.MessageId)); // mensagem de outro tenant em sessão própria
        Assert.Equal("Olá do tenant B", Assert.Single((await b.TraducoesAsync(deB.SessionId, deB.MessageId))!).Text);
        Assert.Equal(RecordTranslationOutcome.MessageNotFound, (await b.TraduzirAsync(deA.MessageId, "pt-BR", "intruso")).Outcome);
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => a.ComoAsync(null, s => s.GetRequiredService<IConversationQueries>().GetMessageTranslationsAsync(deA.SessionId, deA.MessageId)));
    }

    // Isolamento por tenant das preferências e das traduções, no próprio DbContext

    [Fact]
    public async Task Preferencia_do_contato_e_traducao_de_um_tenant_nao_existem_para_o_outro_mesmo_com_o_mesmo_contato()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "isola");
        var b = await CenarioDeConversas.CriarAsync(banco, "isolb");
        var deA = await a.EntrarAsync(Contato, "Hola");
        var deB = await b.EntrarAsync(Contato, "Hola"); // o mesmo contato (referência) no outro tenant
        await a.DefinirPreferenciaDoContatoAsync(deA.SessionId, "es-ES");
        await a.TraduzirAsync(deA.MessageId, "pt-BR", "Olá");

        Assert.Null((await b.SessaoAsync(deB.SessionId))!.ContactPreferredLocale);
        Assert.Equal(Locale.Parse("pt-BR"), (await b.SessaoAsync(deB.SessionId))!.EffectiveLocale);
        Assert.Equal(0, await ConsultaAsync(b.ContextoDeSistema, db => db.Translations.CountAsync()));
        Assert.Equal(0, await ConsultaAsync(b.ContextoDeSistema, db => db.Contacts.CountAsync(x => x.PreferredLocale != null)));
        Assert.Equal(1, await ConsultaAsync(a.ContextoDeSistema, db => db.Translations.CountAsync()));
        Assert.Equal(1, await ConsultaAsync(a.ContextoDeSistema, db => db.Contacts.CountAsync(x => x.PreferredLocale != null)));
    }

    [Fact]
    public async Task O_DbContext_bloqueia_gravar_traducao_em_nome_de_outro_tenant_e_falha_fechado_sem_contexto()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "guarda");
        var b = await CenarioDeConversas.CriarAsync(banco, "guardb");
        var deA = await a.EntrarAsync(Contato, "Hola");
        var mensagemDeA = await ConsultaAsync(a.ContextoDeSistema, db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == deA.MessageId));

        await using (var escopoB = banco.NovoEscopo(b.ContextoDeSistema))
        {
            var dbB = escopoB.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            dbB.Translations.Add(MessageTranslation.Create(a.Tenant.TenantId, mensagemDeA, Locale.Parse("pt-BR"), "intruso", "human", DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<IaBimeta.Conversations.Infrastructure.CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        }

        await using var semContexto = banco.NovoEscopo();
        var db = semContexto.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Translations.CountAsync());
        Assert.Equal(0, await ConsultaAsync(a.ContextoDeSistema, db2 => db2.Translations.CountAsync()));
    }

    [Fact]
    public async Task O_banco_recusa_idioma_fora_do_formato_traducao_repetida_e_traducao_ligada_a_mensagem_de_outro_tenant()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "bancoidioma");
        var b = await CenarioDeConversas.CriarAsync(banco, "bancoidiomab");
        var r = await a.EntrarAsync(Contato, "Olá", idiomaDaMensagem: "pt-BR");
        await a.TraduzirAsync(r.MessageId, "en-US", "Hello");
        var tenant = a.Tenant.TenantId.Value;

        // Formato: underscore, texto livre e tag longa demais.
        var conversa = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema, "update conversations.conversations set effective_locale = 'pt_BR' where tenant_id = {0}", tenant));
        var contato = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema, "update conversations.contacts set preferred_locale = 'português do Brasil' where tenant_id = {0}", tenant));
        var mensagem = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema, "update conversations.messages set locale = 'pt_BR' where tenant_id = {0} and id = {1}", tenant, r.MessageId));
        var destino = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema, "update conversations.message_translations set target_locale = 'en_US' where tenant_id = {0}", tenant));
        Assert.Equal(
            new[] { "ck_conversations_locale", "ck_contacts_preferred_locale", "ck_messages_locale", "ck_message_translations_locales" },
            new[] { conversa.ConstraintName, contato.ConstraintName, mensagem.ConstraintName, destino.ConstraintName });

        // O idioma e a origem da mensagem andam juntos; a origem da conversa é uma das conhecidas.
        var semOrigem = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema, "update conversations.messages set locale_origin = null where tenant_id = {0} and id = {1}", tenant, r.MessageId));
        var origemInvalida = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(a.ContextoDeSistema, "update conversations.conversations set locale_source = 9 where tenant_id = {0}", tenant));
        Assert.Equal(("ck_messages_locale", "ck_conversations_locale"), (semOrigem.ConstraintName, origemInvalida.ConstraintName));

        // No máximo uma tradução por mensagem e idioma de destino.
        var repetida = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            a.ContextoDeSistema,
            "insert into conversations.message_translations (tenant_id, id, message_id, target_locale, text, provenance, created_at) values ({0}, {1}, {2}, 'en-US', 'outra', 'human', now())",
            tenant, Guid.NewGuid(), r.MessageId));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, repetida.SqlState);

        // A tradução carrega o tenant: o banco recusa ligá-la à mensagem de outro tenant.
        var deOutroTenant = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            b.ContextoDeSistema,
            "insert into conversations.message_translations (tenant_id, id, message_id, target_locale, text, provenance, created_at) values ({0}, {1}, {2}, 'en-US', 'intruso', 'human', now())",
            b.Tenant.TenantId.Value, Guid.NewGuid(), r.MessageId));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deOutroTenant.SqlState);
    }

    // Saída e reenvio

    [Fact]
    public async Task O_reenvio_mantem_o_idioma_da_resposta_original_mesmo_depois_de_a_conversa_mudar()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "reenvioidioma");
        var r = await c.EntrarAsync(Contato, "Hola", idiomaDoContato: "es-ES");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        var resposta = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId, "Sí, ocho horas de ayuno.");
        await c.EntregaAsync(resposta.MessageId!.Value, DeliveryState.Failed, "template-rejected");
        await c.AlterarIdiomaAsync(r.SessionId, "en-US", 1, Atendente(c)); // a conversa muda depois da falha

        var reenvio = await c.PedirReenvioAsync(resposta.MessageId.Value, Guid.NewGuid(), Atendente(c));

        Assert.Equal(ResendOutcome.Requested, reenvio.Outcome);
        var historico = (await c.HistoricoAsync(r.SessionId))!.Items;
        var original = historico.Single(m => m.Id == resposta.MessageId);
        var novo = historico.Single(m => m.Id == reenvio.NewMessageId);
        Assert.Equal((Locale.Parse("es-ES"), (MessageLocaleOrigin?)MessageLocaleOrigin.ConversationLocale), (original.Locale, original.LocaleOrigin));
        Assert.Equal((original.Locale, original.LocaleOrigin, original.Text), (novo.Locale, novo.LocaleOrigin, novo.Text)); // reenvio: mesmo texto, mesmo idioma
    }
}
