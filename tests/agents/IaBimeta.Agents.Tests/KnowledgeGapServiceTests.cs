using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A revisão administrativa das lacunas do FAQ (ADR 0030) sobre dublês: só <c>TenantSettingsManage</c>; a pergunta e a resposta humana lidas do Conversations COM a autorização de quem pede (e a mensagem removida
/// ou fora do alcance deixa a lacuna revisável); descartar; e enviar ao RASCUNHO com a resposta direta DESLIGADA por padrão, sem publicar nada e sem tocar a versão publicada, recusando a pergunta que já existe no FAQ.
/// </summary>
public sealed class KnowledgeGapServiceTests
{
    private static readonly byte[] Chave = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset T0 = Snapshots.T0;

    // Dublês

    internal sealed class FakeGapStore : IKnowledgeGapStore
    {
        private readonly List<(KnowledgeGap Gap, List<KnowledgeGapOccurrence> Occurrences)> _rows = new();

        public int Saves { get; private set; }

        public bool ConflictOnNextSave { get; set; }

        public KnowledgeGap Add(string texto, KnowledgeGapKind kind = KnowledgeGapKind.NoMatch, int ocorrencias = 1, TenantId? tenant = null, Guid? mensagem = null, Guid? sessao = null)
        {
            var dono = tenant ?? TestContexts.TenantA;
            var lacuna = KnowledgeGap.Open(dono, new KnowledgeGapHasher(Chave).Hash(dono, texto), kind, T0);
            var lista = new List<KnowledgeGapOccurrence>();
            for (var i = 0; i < ocorrencias; i++)
            {
                lista.Add(KnowledgeGapOccurrence.Create(dono, Guid.NewGuid(), lacuna.Id, sessao ?? Guid.NewGuid(), Guid.NewGuid(), i == 0 && mensagem is not null ? mensagem.Value : Guid.NewGuid(), T0.AddMinutes(i)));
            }

            _rows.Add((lacuna, lista));
            return lacuna;
        }

        public IReadOnlyList<KnowledgeGapOccurrence> OccurrencesOf(KnowledgeGap lacuna) => _rows.Single(r => r.Gap == lacuna).Occurrences;

        public Task<IReadOnlyList<KnowledgeGapRow>> ListAsync(KnowledgeGapStatus? status, int take, int skip, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeGapRow>>(_rows
                .Where(r => status is null || r.Gap.Status == status)
                .Select(r => new KnowledgeGapRow(r.Gap, r.Occurrences.Count))
                .OrderByDescending(r => r.Occurrences).ThenByDescending(r => r.Gap.LastSeenAt)
                .Skip(skip).Take(take).ToList());

        public Task<int> CountAsync(KnowledgeGapStatus? status, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.Count(r => status is null || r.Gap.Status == status));

        public Task<KnowledgeGap?> FindAsync(Guid gapId, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.Select(r => r.Gap).FirstOrDefault(g => g.Id == gapId && g.TenantId == TestContexts.TenantA));

        public Task<int> CountOccurrencesAsync(Guid gapId, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.Single(r => r.Gap.Id == gapId).Occurrences.Count);

        public Task<IReadOnlyList<KnowledgeGapOccurrence>> LatestOccurrencesAsync(Guid gapId, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeGapOccurrence>>(_rows.Single(r => r.Gap.Id == gapId).Occurrences.OrderByDescending(o => o.OccurredAt).Take(take).ToList());

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            if (ConflictOnNextSave)
            {
                ConflictOnNextSave = false;
                throw new KnowledgeGapConflictException("gap-changed", "decidida por outra operação");
            }

            Saves++;
            return Task.CompletedTask;
        }
    }

    /// <summary>O histórico do Conversations como o real o entrega: por sessão, com os filtros de mensagem e de sequência, na ordem pedida, sempre dentro do que o usuário pode ler.</summary>
    internal sealed class FakeConversations : IConversationQueries
    {
        private readonly List<MessageView> _messages = new();

        public bool Denied { get; set; }

        public HashSet<Guid> HiddenSessions { get; } = new();

        public List<GetHistoryQuery> Queries { get; } = new();

        public MessageView Add(Guid sessao, long seq, MessageDirection direcao, MessageAuthor autor, string? texto, MessageKind tipo = MessageKind.Text, ContentState estado = ContentState.Ready, Guid? id = null)
        {
            var mensagem = new MessageView(
                id ?? Guid.NewGuid(), sessao, Guid.NewGuid(), direcao, autor, null, seq, direcao == MessageDirection.Inbound ? seq : null, tipo, estado, texto, false, T0.AddSeconds(seq), null, T0.AddSeconds(seq),
                null, null, null, null, ChannelInstanceId.New());
            _messages.Add(mensagem);
            return mensagem;
        }

        public Task<Page<MessageView>?> GetHistoryAsync(GetHistoryQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            if (Denied)
            {
                throw new AccessDeniedException(Permission.ConversationRead);
            }

            if (HiddenSessions.Contains(query.SessionId))
            {
                return Task.FromResult<Page<MessageView>?>(null);
            }

            var itens = _messages.Where(m => m.SessionId == query.SessionId);
            if (query.MessageId is { } soEsta)
            {
                itens = itens.Where(m => m.Id == soEsta);
            }

            if (query.AfterMessageSeq is { } depoisDe)
            {
                itens = itens.Where(m => m.MessageSeq > depoisDe);
            }

            var ordenado = query.Order == HistoryOrder.OldestFirst ? itens.OrderBy(m => m.MessageSeq) : itens.OrderByDescending(m => m.MessageSeq);
            return Task.FromResult<Page<MessageView>?>(new Page<MessageView>(ordenado.Take(query.Take).ToList(), null));
        }

        public Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Page<SessionView>> ListSessionsAsync(ListSessionsQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MessageTranslationView>?> GetMessageTranslationsAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionTransferView>?> GetTransfersAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EffectView> GetEffectAsync(GetEffectQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Cenario
    {
        public Cenario(RequestContextHolder? contexto = null, bool comDefinicaoPublicada = true)
        {
            Contexto = contexto ?? TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage);
            Relogio = new FakeClock();
            Definicoes = new InMemoryDefinitionStore();
            Auditoria = new FakeAudit();
            Lacunas = new FakeGapStore();
            Conversas = new FakeConversations();
            var definicaoDoAgente = new AgentDefinitionService(Contexto, Definicoes, Auditoria, Relogio);
            Servico = new KnowledgeGapService(Contexto, Lacunas, definicaoDoAgente, Definicoes, Conversas, Auditoria, Relogio);
            DefinicaoDoAgente = definicaoDoAgente;
            if (comDefinicaoPublicada)
            {
                PublicarAsync(Base()).GetAwaiter().GetResult();
                Auditoria.Entries.Clear();
            }
        }

        public RequestContextHolder Contexto { get; }

        public FakeClock Relogio { get; }

        public InMemoryDefinitionStore Definicoes { get; }

        public FakeAudit Auditoria { get; }

        public FakeGapStore Lacunas { get; }

        public FakeConversations Conversas { get; }

        public KnowledgeGapService Servico { get; }

        public AgentDefinitionService DefinicaoDoAgente { get; }

        public Guid UsuarioId => Contexto.Current!.Principal!.UserId;

        public static AgentDefinitionContent Base() =>
            new("Atenda com cortesia.", new[] { new FaqEntry("Qual o horário de atendimento?", "Das 7h às 17h.", new[] { "que horas abrem" }, true) }, "Áudio.", "Aviso.");

        public async Task PublicarAsync(AgentDefinitionContent conteudo)
        {
            await DefinicaoDoAgente.SaveDraftAsync(conteudo);
            await DefinicaoDoAgente.PublishAsync();
        }

        public AgentDefinitionVersion Publicada => Definicoes.Rows.Single(v => v.Status == AgentDefinitionStatus.Published);

        public AgentDefinitionVersion? Rascunho => Definicoes.Rows.SingleOrDefault(v => v.Status == AgentDefinitionStatus.Draft);
    }

    private static PromoteKnowledgeGapRequest Entrada(string pergunta = "Preciso levar documento para a coleta?", string resposta = "Sim, leve um documento com foto.", bool direta = false, params string[] aliases) =>
        new(pergunta, aliases, resposta, direta);

    // Permissão

    [Fact]
    public async Task Sem_a_permissao_toda_operacao_e_negada_e_nada_e_gravado_nem_auditado()
    {
        var c = new Cenario(TestContexts.User(TestContexts.TenantA, Permission.ConversationRead), comDefinicaoPublicada: false);
        var lacuna = c.Lacunas.Add("pergunta");

        var excecoes = new[]
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.ListAsync(null, 10, 0)),
            await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.GetAsync(lacuna.Id)),
            await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.DismissAsync(lacuna.Id)),
            await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada())),
        };

        Assert.All(excecoes, e => Assert.Equal(Permission.TenantSettingsManage, e.Permission));
        Assert.Equal(KnowledgeGapStatus.Open, lacuna.Status);
        Assert.Equal(0, c.Lacunas.Saves);
        Assert.Empty(c.Auditoria.Entries);
        Assert.Empty(c.Conversas.Queries); // sem a permissão, o Conversations nem é consultado
        Assert.Null(c.Rascunho);
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_falha_fechado()
    {
        var c = new Cenario(new RequestContextHolder(), comDefinicaoPublicada: false);

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.ListAsync(null, 10, 0));
    }

    // Listar

    [Fact]
    public async Task A_lista_vem_das_mais_frequentes_para_as_menos_e_nunca_traz_texto()
    {
        var c = new Cenario();
        var rara = c.Lacunas.Add("rara", ocorrencias: 1);
        var comum = c.Lacunas.Add("comum", KnowledgeGapKind.Ambiguous, ocorrencias: 5);

        var pagina = await c.Servico.ListAsync(null, 10, 0);

        Assert.Equal(2, pagina.Total);
        Assert.Equal(new[] { comum.Id, rara.Id }, pagina.Items.Select(i => i.Id));
        Assert.Equal((5, KnowledgeGapKind.Ambiguous, KnowledgeGapStatus.Open), (pagina.Items[0].Occurrences, pagina.Items[0].Kind, pagina.Items[0].Status));
        Assert.DoesNotContain(typeof(KnowledgeGapSummary).GetProperties(), p => p.PropertyType == typeof(string)); // o resumo não tem campo de texto
    }

    [Fact]
    public async Task O_filtro_por_situacao_e_a_paginacao_valem_e_a_paginacao_invalida_e_recusada()
    {
        var c = new Cenario();
        var aberta = c.Lacunas.Add("aberta");
        var descartada = c.Lacunas.Add("descartada");
        descartada.Dismiss(T0, c.UsuarioId);

        var abertas = await c.Servico.ListAsync(KnowledgeGapStatus.Open, 10, 0);
        var descartadas = await c.Servico.ListAsync(KnowledgeGapStatus.Dismissed, 10, 0);
        var segundaPagina = await c.Servico.ListAsync(null, 1, 1);

        Assert.Equal(aberta.Id, Assert.Single(abertas.Items).Id);
        Assert.Equal(descartada.Id, Assert.Single(descartadas.Items).Id);
        Assert.Equal((2, 1), (segundaPagina.Total, segundaPagina.Items.Count));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.ListAsync(null, 0, 0));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.ListAsync(null, 101, 0));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.ListAsync(null, 10, -1));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.ListAsync((KnowledgeGapStatus)99, 10, 0));
    }

    // Consultar: a pergunta e a resposta humana, com a autorização de quem pede

    [Fact]
    public async Task O_detalhe_traz_a_pergunta_original_e_a_resposta_do_atendente_como_sugestao_e_a_leitura_e_auditada_sem_conteudo()
    {
        var c = new Cenario();
        var sessao = Guid.NewGuid();
        var pergunta = c.Conversas.Add(sessao, 1, MessageDirection.Inbound, MessageAuthor.Customer, "Preciso levar documento para a coleta?");
        c.Conversas.Add(sessao, 2, MessageDirection.Outbound, MessageAuthor.Ai, "Vou chamar uma pessoa.");
        c.Conversas.Add(sessao, 3, MessageDirection.Outbound, MessageAuthor.Human, "Sim, leve um documento com foto.");
        var lacuna = c.Lacunas.Add("preciso levar documento para a coleta", ocorrencias: 1, mensagem: pergunta.Id, sessao: sessao);

        var detalhe = await c.Servico.GetAsync(lacuna.Id);

        Assert.NotNull(detalhe);
        Assert.Equal(KnowledgeGapSourceState.Available, detalhe.Source.State);
        Assert.Equal("Preciso levar documento para a coleta?", detalhe.Source.Question);
        var resposta = detalhe.Source.HumanReply!;
        Assert.Equal(("Sim, leve um documento com foto.", 0), (resposta.Text, resposta.CustomerMessagesBetween)); // a resposta da IA não é sugestão
        Assert.Equal(pergunta.Id, Assert.Single(detalhe.Occurrences).MessageId);
        var auditoria = Assert.Single(c.Auditoria.Entries);
        Assert.Equal(("agents.knowledge-gap.source-read", lacuna.Id.ToString()), (auditoria.Operation, auditoria.ResourceId));
        Assert.DoesNotContain("documento", auditoria.ToString()!);
    }

    [Fact]
    public async Task A_resposta_humana_e_so_sugestao_e_conta_as_mensagens_do_cliente_no_meio()
    {
        var c = new Cenario();
        var sessao = Guid.NewGuid();
        var pergunta = c.Conversas.Add(sessao, 1, MessageDirection.Inbound, MessageAuthor.Customer, "Aceitam cartão?");
        c.Conversas.Add(sessao, 2, MessageDirection.Inbound, MessageAuthor.Customer, "E parcelam?");
        c.Conversas.Add(sessao, 3, MessageDirection.Inbound, MessageAuthor.Customer, "Alô?");
        c.Conversas.Add(sessao, 4, MessageDirection.Outbound, MessageAuthor.Human, "Aceitamos e parcelamos em 3 vezes.");
        var lacuna = c.Lacunas.Add("aceitam cartao", mensagem: pergunta.Id, sessao: sessao);

        var detalhe = await c.Servico.GetAsync(lacuna.Id);

        Assert.Equal(2, detalhe!.Source.HumanReply!.CustomerMessagesBetween); // a resposta pode ser de outra pergunta: quem revisa precisa conferir
    }

    [Fact]
    public async Task Sem_resposta_humana_a_fonte_traz_so_a_pergunta()
    {
        var c = new Cenario();
        var sessao = Guid.NewGuid();
        var pergunta = c.Conversas.Add(sessao, 1, MessageDirection.Inbound, MessageAuthor.Customer, "Aceitam cartão?");
        c.Conversas.Add(sessao, 2, MessageDirection.Outbound, MessageAuthor.Ai, "Sim, aceitamos.");
        var lacuna = c.Lacunas.Add("aceitam cartao", mensagem: pergunta.Id, sessao: sessao);

        var detalhe = await c.Servico.GetAsync(lacuna.Id);

        Assert.Equal(KnowledgeGapSourceState.Available, detalhe!.Source.State);
        Assert.Null(detalhe.Source.HumanReply); // a resposta da IA nunca vira sugestão
    }

    [Fact]
    public async Task Mensagem_removida_deixa_a_fonte_indisponivel_sem_erro_sem_auditoria_e_a_lacuna_continua_revisavel()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("pergunta que sumiu"); // as referências apontam para mensagens que o Conversations não tem mais

        var detalhe = await c.Servico.GetAsync(lacuna.Id);

        Assert.Equal(KnowledgeGapSourceState.Unavailable, detalhe!.Source.State);
        Assert.Null(detalhe.Source.Question);
        Assert.Null(detalhe.Source.HumanReply);
        Assert.Empty(c.Auditoria.Entries); // nada de conteúdo foi lido
        var promovida = await c.Servico.PromoteAsync(lacuna.Id, Entrada("Pergunta escrita pelo administrador?", "Resposta do administrador."));
        Assert.Equal(KnowledgeGapStatus.Promoted, promovida!.Gap.Status);
    }

    [Fact]
    public async Task Sessao_fora_do_alcance_de_quem_pede_ou_sem_permissao_de_leitura_deixa_a_fonte_indisponivel()
    {
        var c = new Cenario();
        var sessao = Guid.NewGuid();
        var pergunta = c.Conversas.Add(sessao, 1, MessageDirection.Inbound, MessageAuthor.Customer, "Aceitam cartão?");
        var lacuna = c.Lacunas.Add("aceitam cartao", mensagem: pergunta.Id, sessao: sessao);

        c.Conversas.HiddenSessions.Add(sessao); // o Conversations responde como inexistente (TEN-2)
        var escondida = await c.Servico.GetAsync(lacuna.Id);
        c.Conversas.HiddenSessions.Clear();
        c.Conversas.Denied = true; // sem ConversationRead em nenhuma organização
        var negada = await c.Servico.GetAsync(lacuna.Id);

        Assert.Equal(KnowledgeGapSourceState.Unavailable, escondida!.Source.State);
        Assert.Equal(KnowledgeGapSourceState.Unavailable, negada!.Source.State);
        Assert.Empty(c.Auditoria.Entries);
    }

    [Theory]
    [InlineData(MessageKind.Image, ContentState.Ready, "legenda")]
    [InlineData(MessageKind.Text, ContentState.Pending, null)]
    [InlineData(MessageKind.Text, ContentState.Ready, "   ")]
    public async Task So_mensagem_de_texto_pronta_do_cliente_serve_de_fonte(MessageKind tipo, ContentState estado, string? texto)
    {
        var c = new Cenario();
        var sessao = Guid.NewGuid();
        var mensagem = c.Conversas.Add(sessao, 1, MessageDirection.Inbound, MessageAuthor.Customer, texto, tipo, estado);
        var lacuna = c.Lacunas.Add("x", mensagem: mensagem.Id, sessao: sessao);

        var detalhe = await c.Servico.GetAsync(lacuna.Id);

        Assert.Equal(KnowledgeGapSourceState.Unavailable, detalhe!.Source.State);
    }

    [Fact]
    public async Task A_fonte_vem_da_ocorrencia_mais_recente_cuja_mensagem_ainda_existe()
    {
        var c = new Cenario();
        var sessaoVelha = Guid.NewGuid();
        var existente = c.Conversas.Add(sessaoVelha, 1, MessageDirection.Inbound, MessageAuthor.Customer, "Aceitam cartão?");
        var lacuna = c.Lacunas.Add("aceitam cartao", ocorrencias: 2, mensagem: existente.Id, sessao: sessaoVelha);

        var detalhe = await c.Servico.GetAsync(lacuna.Id);

        Assert.Equal(KnowledgeGapSourceState.Available, detalhe!.Source.State);
        Assert.Equal("Aceitam cartão?", detalhe.Source.Question);
    }

    [Fact]
    public async Task Lacuna_inexistente_ou_de_outro_tenant_e_nula_em_toda_operacao()
    {
        var c = new Cenario();
        var deOutro = c.Lacunas.Add("de outro tenant", tenant: TestContexts.TenantB);

        Assert.Null(await c.Servico.GetAsync(Guid.NewGuid()));
        Assert.Null(await c.Servico.GetAsync(deOutro.Id));
        Assert.Null(await c.Servico.DismissAsync(deOutro.Id));
        Assert.Null(await c.Servico.PromoteAsync(deOutro.Id, Entrada()));
        Assert.Equal(KnowledgeGapStatus.Open, deOutro.Status);
        Assert.Null(c.Rascunho);
    }

    // Descartar

    [Fact]
    public async Task Descartar_marca_a_lacuna_e_audita_e_repetir_nao_audita_de_novo()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("pergunta");

        var primeira = await c.Servico.DismissAsync(lacuna.Id);
        var segunda = await c.Servico.DismissAsync(lacuna.Id);

        Assert.Equal((KnowledgeGapStatus.Dismissed, c.UsuarioId), (primeira!.Status, lacuna.DecidedBy));
        Assert.Equal(KnowledgeGapStatus.Dismissed, segunda!.Status);
        Assert.Equal(new[] { "agents.knowledge-gap.dismissed" }, c.Auditoria.Entries.Select(e => e.Operation));
        Assert.Equal(1, c.Lacunas.Saves);
    }

    [Fact]
    public async Task Lacuna_ja_enviada_ao_rascunho_nao_se_descarta()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("pergunta");
        await c.Servico.PromoteAsync(lacuna.Id, Entrada());

        var erro = await Assert.ThrowsAsync<KnowledgeGapConflictException>(() => c.Servico.DismissAsync(lacuna.Id));

        Assert.Equal("gap-not-open", erro.Code);
        Assert.Equal(KnowledgeGapStatus.Promoted, lacuna.Status);
    }

    [Fact]
    public async Task Decisao_concorrente_ao_descartar_vira_conflito_explicito()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("pergunta");
        c.Lacunas.ConflictOnNextSave = true;

        var erro = await Assert.ThrowsAsync<KnowledgeGapConflictException>(() => c.Servico.DismissAsync(lacuna.Id));

        Assert.Equal("gap-changed", erro.Code);
        Assert.Empty(c.Auditoria.Entries);
    }

    // Enviar ao rascunho

    [Fact]
    public async Task Enviar_ao_rascunho_cria_o_rascunho_a_partir_da_publicada_com_a_entrada_nova_e_a_resposta_direta_desligada_e_nada_e_publicado()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("preciso levar documento para a coleta", ocorrencias: 3);
        var publicadaAntes = c.Publicada.Content;

        var resultado = await c.Servico.PromoteAsync(lacuna.Id, Entrada("  Preciso levar documento para a coleta?  ", "  Sim, leve um documento com foto.  ", direta: false, "preciso de documento"));

        Assert.NotNull(resultado);
        Assert.Equal((2, 2, KnowledgeGapStatus.Promoted), (resultado.DraftVersion, resultado.FaqEntries, resultado.Gap.Status));
        Assert.Equal(2, resultado.Gap.PromotedVersion);
        var rascunho = c.Rascunho!.Content;
        Assert.Equal(2, rascunho.Faq.Count);
        var nova = rascunho.Faq[1];
        Assert.Equal(("Preciso levar documento para a coleta?", "Sim, leve um documento com foto.", false), (nova.Question, nova.Answer, nova.AutoReply)); // AutoReply fica desligado
        Assert.Equal(new[] { "preciso de documento" }, nova.Aliases);
        Assert.Equal(rascunho.Faq[0], publicadaAntes.Faq[0]); // as entradas existentes seguem iguais
        // Nada foi publicado: a versão publicada é a mesma de antes, o rascunho é outra linha.
        Assert.Equal(AgentDefinitionStatus.Published, c.Publicada.Status);
        Assert.Single(c.Publicada.Content.Faq);
        Assert.Equal(1, c.Publicada.Number);
        Assert.Equal(KnowledgeGapStatus.Promoted, lacuna.Status);
        Assert.Equal(c.UsuarioId, lacuna.DecidedBy);
        Assert.Equal(2, lacuna.PromotedVersion);
        Assert.Equal(new[] { "agents.definition.draft-saved", "agents.knowledge-gap.promoted" }, c.Auditoria.Entries.Select(e => e.Operation)); // nenhuma publicação
    }

    [Fact]
    public async Task A_resposta_direta_so_liga_se_o_administrador_ligar_de_proposito()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("pergunta");

        await c.Servico.PromoteAsync(lacuna.Id, Entrada(direta: true));

        Assert.True(c.Rascunho!.Content.Faq[^1].AutoReply);
        Assert.False(new PromoteKnowledgeGapRequest("P?", null, "R").AutoReply); // o padrão do pedido é desligado
    }

    [Fact]
    public async Task Com_um_rascunho_existente_a_entrada_vai_para_ele_e_nao_para_uma_copia_da_publicada()
    {
        var c = new Cenario();
        await c.DefinicaoDoAgente.SaveDraftAsync(Cenario.Base() with { Instructions = "Instruções do rascunho em andamento." });
        c.Auditoria.Entries.Clear();
        var lacuna = c.Lacunas.Add("pergunta");

        await c.Servico.PromoteAsync(lacuna.Id, Entrada());

        var rascunho = c.Rascunho!;
        Assert.Equal("Instruções do rascunho em andamento.", rascunho.Content.Instructions); // o que já estava no rascunho foi mantido
        Assert.Equal(2, rascunho.Content.Faq.Count);
        Assert.Equal(2, rascunho.Number);
    }

    [Fact]
    public async Task Duas_lacunas_enviadas_em_sequencia_acumulam_no_mesmo_rascunho()
    {
        var c = new Cenario();
        var a = c.Lacunas.Add("a");
        var b = c.Lacunas.Add("b");

        await c.Servico.PromoteAsync(a.Id, Entrada("Pergunta A?", "Resposta A."));
        var segunda = await c.Servico.PromoteAsync(b.Id, Entrada("Pergunta B?", "Resposta B."));

        Assert.Equal((3, 2), (segunda!.FaqEntries, segunda.DraftVersion));
        Assert.Equal(new[] { "Qual o horário de atendimento?", "Pergunta A?", "Pergunta B?" }, c.Rascunho!.Content.Faq.Select(f => f.Question));
    }

    [Theory]
    [InlineData("Qual o horário de atendimento?", new string[0])] // a pergunta é igual à de uma entrada
    [InlineData("QUAL O HORARIO DE ATENDIMENTO!!!", new string[0])] // igual depois de normalizada
    [InlineData("Pergunta nova?", new[] { "Que horas abrem" })] // um alias é igual a um alias da existente
    [InlineData("Pergunta nova?", new[] { "qual o horario de atendimento" })] // um alias é igual à pergunta da existente
    [InlineData("Que horas abrem?", new string[0])] // a pergunta é igual a um alias da existente
    public async Task Pergunta_ou_alias_igual_ao_que_ja_existe_no_FAQ_e_recusado_sem_gravar_nada(string pergunta, string[] aliases)
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("pergunta");

        var erro = await Assert.ThrowsAsync<KnowledgeGapConflictException>(() => c.Servico.PromoteAsync(lacuna.Id, new PromoteKnowledgeGapRequest(pergunta, aliases, "Resposta.", false)));

        Assert.Equal("faq-conflict", erro.Code);
        Assert.Equal(KnowledgeGapStatus.Open, lacuna.Status); // a lacuna segue aberta
        Assert.Null(c.Rascunho); // e nenhum rascunho foi criado
        Assert.Empty(c.Auditoria.Entries);
    }

    [Fact]
    public async Task Os_aliases_de_uma_entrada_so_conflitam_com_outras_entradas_e_o_alias_repetido_dentro_dela_e_limpo()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("pergunta");

        var resultado = await c.Servico.PromoteAsync(lacuna.Id, new PromoteKnowledgeGapRequest("Pergunta nova?", new[] { "Outro", "pergunta NOVA", "OUTRO!" }, "Resposta.", false));

        Assert.Equal(KnowledgeGapStatus.Promoted, resultado!.Gap.Status);
        Assert.Equal(new[] { "Outro", "pergunta NOVA" }, c.Rascunho!.Content.Faq[^1].Aliases); // "OUTRO!" repete "Outro" depois de normalizado
    }

    [Fact]
    public async Task A_colisao_tambem_vale_contra_as_entradas_ja_enviadas_ao_rascunho()
    {
        var c = new Cenario();
        var a = c.Lacunas.Add("a");
        var b = c.Lacunas.Add("b");
        await c.Servico.PromoteAsync(a.Id, Entrada("Aceitam cartão?", "Sim."));

        var erro = await Assert.ThrowsAsync<KnowledgeGapConflictException>(() => c.Servico.PromoteAsync(b.Id, Entrada("aceitam cartao", "Aceitamos.")));

        Assert.Equal("faq-conflict", erro.Code);
        Assert.Equal(KnowledgeGapStatus.Open, b.Status);
        Assert.Equal(2, c.Rascunho!.Content.Faq.Count);
    }

    [Fact]
    public async Task Lacuna_nao_aberta_nao_vai_ao_rascunho()
    {
        var c = new Cenario();
        var descartada = c.Lacunas.Add("a");
        descartada.Dismiss(T0, c.UsuarioId);

        var erro = await Assert.ThrowsAsync<KnowledgeGapConflictException>(() => c.Servico.PromoteAsync(descartada.Id, Entrada()));

        Assert.Equal("gap-not-open", erro.Code);
        Assert.Null(c.Rascunho);
    }

    [Fact]
    public async Task Sem_nenhuma_definicao_do_agente_nao_ha_para_onde_enviar()
    {
        var c = new Cenario(comDefinicaoPublicada: false);
        var lacuna = c.Lacunas.Add("pergunta");

        var erro = await Assert.ThrowsAsync<KnowledgeGapConflictException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada()));

        Assert.Equal("no-definition", erro.Code);
        Assert.Equal(KnowledgeGapStatus.Open, lacuna.Status);
        Assert.Empty(c.Definicoes.Rows);
    }

    [Theory]
    [InlineData("", "Resposta.")]
    [InlineData("   ", "Resposta.")]
    [InlineData("Pergunta?", "")]
    [InlineData("Pergunta?", "   ")]
    public async Task Pergunta_ou_resposta_em_branco_e_recusada_e_a_lacuna_segue_aberta(string pergunta, string resposta)
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("x");

        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada(pergunta, resposta)));

        Assert.Equal(KnowledgeGapStatus.Open, lacuna.Status);
        Assert.Null(c.Rascunho);
    }

    [Fact]
    public async Task Limites_do_FAQ_valem_na_entrada_enviada_e_nada_e_gravado_quando_violam()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("x");
        var onze = Enumerable.Range(0, 11).Select(i => $"alias {i}").ToArray();

        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada(new string('p', 301), "R")));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada("P?", new string('r', 1501))));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada("P?", "R", false, onze)));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada("P?", "R", false, "???")));

        Assert.Equal(KnowledgeGapStatus.Open, lacuna.Status);
        Assert.Null(c.Rascunho);
        Assert.Empty(c.Auditoria.Entries);
    }

    [Fact]
    public async Task FAQ_cheio_com_50_entradas_recusa_a_entrada_51()
    {
        var c = new Cenario(comDefinicaoPublicada: false);
        var cheio = Cenario.Base() with { Faq = Enumerable.Range(0, 50).Select(i => new FaqEntry($"Pergunta {i}?", "R")).ToList() };
        await c.PublicarAsync(cheio);
        c.Auditoria.Entries.Clear();
        var lacuna = c.Lacunas.Add("x");

        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada("Pergunta nova?", "R")));

        Assert.Equal(KnowledgeGapStatus.Open, lacuna.Status);
    }

    [Fact]
    public async Task Decisao_concorrente_ao_enviar_vira_conflito_e_o_rascunho_nao_e_gravado()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("x");
        c.Lacunas.ConflictOnNextSave = true; // outro administrador decidiu a mesma lacuna primeiro

        var erro = await Assert.ThrowsAsync<KnowledgeGapConflictException>(() => c.Servico.PromoteAsync(lacuna.Id, Entrada()));

        Assert.Equal("gap-changed", erro.Code);
        Assert.Null(c.Rascunho); // a decisão vem ANTES de qualquer gravação do rascunho
        Assert.Empty(c.Auditoria.Entries);
    }

    [Fact]
    public async Task Publicar_continua_sendo_um_passo_explicito_que_so_o_servico_da_definicao_faz()
    {
        var c = new Cenario();
        var lacuna = c.Lacunas.Add("x");
        await c.Servico.PromoteAsync(lacuna.Id, Entrada("Preciso de documento?", "Sim.", direta: true));

        var antes = c.Publicada.Content.Faq.Count;
        await c.DefinicaoDoAgente.PublishAsync(); // a ação autorizada do administrador

        Assert.Equal(1, antes);
        Assert.Equal(2, c.Publicada.Content.Faq.Count);
        Assert.Equal(2, c.Publicada.Number);
        Assert.True(c.Publicada.Content.Faq[1].AutoReply);
        Assert.Contains("agents.definition.published", c.Auditoria.Entries.Select(e => e.Operation));
    }
}
