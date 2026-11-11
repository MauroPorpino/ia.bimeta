using System.Text.Json;
using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A revisao humana das sugestoes da IA local (ADR 0034) sobre dubles: so gera quando o administrador pede e a funcao esta ligada; o texto vai ao modelo redigido e como dado; aceitar leva ao RASCUNHO
/// (nunca publica, AutoReply desligado, resposta escrita por pessoa obrigatoria); rejeitar nao muda nada; tudo auditado sem conteudo; falha do modelo nao grava nada.
/// </summary>
public sealed class FaqSuggestionServiceTests
{
    private static readonly DateTimeOffset T0 = Snapshots.T0;

    private sealed class FakeLocal : ILocalGenerationProvider
    {
        public string ModelId => "qwen-teste";

        public List<LocalGenerationRequest> Requests { get; } = new();

        public Func<LocalGenerationRequest, string> Reply { get; set; } = _ => "{}";

        public LocalGenerationUnavailableException? Failure { get; set; }

        public Task<LocalGenerationResult> GenerateJsonAsync(LocalGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Failure is null ? Task.FromResult(new LocalGenerationResult(Reply(request), 5)) : throw Failure;
        }
    }

    private sealed class MemorySuggestions : IFaqSuggestionStore
    {
        public List<FaqSuggestion> Rows { get; } = new();

        public IReadOnlyList<FaqSuggestion> Listed(FaqSuggestionStatus? s, FaqSuggestionKind? k) => Rows.Where(r => (s is null || r.Status == s) && (k is null || r.Kind == k)).ToList();

        public Task<IReadOnlyList<FaqSuggestion>> ListAsync(FaqSuggestionStatus? status, FaqSuggestionKind? kind, int take, int skip, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaqSuggestion>>(Listed(status, kind).OrderByDescending(r => r.CreatedAt).Skip(skip).Take(take).ToList());

        public Task<int> CountAsync(FaqSuggestionStatus? status, FaqSuggestionKind? kind, CancellationToken cancellationToken) => Task.FromResult(Listed(status, kind).Count);

        public Task<FaqSuggestion?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id && r.TenantId == TestContexts.TenantA));

        public Task<FaqSuggestion?> FindPendingByKeyAsync(string dedupeKey, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.FirstOrDefault(r => r.DedupeKey == dedupeKey && r.Status == FaqSuggestionStatus.Pending));

        public void Add(FaqSuggestion suggestion) => Rows.Add(suggestion);

        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Cenario
    {
        public Cenario(bool enabled = true, bool withLocal = true, RequestContextHolder? context = null)
        {
            Contexto = context ?? TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage);
            Relogio = new FakeClock();
            Definicoes = new InMemoryDefinitionStore();
            Auditoria = new FakeAudit();
            Lacunas = new KnowledgeGapServiceTests.FakeGapStore();
            Conversas = new KnowledgeGapServiceTests.FakeConversations();
            Local = new FakeLocal();
            Sugestoes = new MemorySuggestions();
            DefinicaoDoAgente = new AgentDefinitionService(Contexto, Definicoes, Auditoria, Relogio);
            var gaps = new KnowledgeGapService(Contexto, Lacunas, DefinicaoDoAgente, Definicoes, Conversas, Auditoria, Relogio);
            Servico = new FaqSuggestionService(Contexto, Sugestoes, gaps, DefinicaoDoAgente, Auditoria, new AgentsOptions { LocalSuggestionsEnabled = enabled }, Relogio, withLocal ? Local : null);
            var semeador = new AgentDefinitionService(TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage), Definicoes, Auditoria, Relogio);
            semeador.SaveDraftAsync(new AgentDefinitionContent("Atenda.", new[] { new FaqEntry("Preciso de jejum para glicose?", "Sim, 8 horas.", new[] { "tem que ficar sem comer" }, true), new FaqEntry("Qual o horario de coleta?", "Das 7h as 11h.", null, true) }, "Audio.", "Aviso.")).GetAwaiter().GetResult();
            semeador.PublishAsync().GetAwaiter().GetResult();
            Auditoria.Entries.Clear();
        }

        public RequestContextHolder Contexto { get; }

        public FakeClock Relogio { get; }

        public InMemoryDefinitionStore Definicoes { get; }

        public FakeAudit Auditoria { get; }

        public KnowledgeGapServiceTests.FakeGapStore Lacunas { get; }

        public KnowledgeGapServiceTests.FakeConversations Conversas { get; }

        public FakeLocal Local { get; }

        public MemorySuggestions Sugestoes { get; }

        public AgentDefinitionService DefinicaoDoAgente { get; }

        public FaqSuggestionService Servico { get; }

        public AgentDefinitionVersion? Rascunho => Definicoes.Rows.SingleOrDefault(v => v.Status == AgentDefinitionStatus.Draft);

        /// <summary>Uma lacuna aberta cuja pergunta (e, se pedido, a resposta humana) o Conversations devolve.</summary>
        public KnowledgeGap Lacuna(string pergunta, string? respostaHumana = null)
        {
            var sessao = Guid.NewGuid();
            var mensagem = Guid.NewGuid();
            var lacuna = Lacunas.Add(pergunta + Guid.NewGuid(), mensagem: mensagem, sessao: sessao);
            Conversas.Add(sessao, 1, MessageDirection.Inbound, MessageAuthor.Customer, pergunta, id: mensagem);
            if (respostaHumana is not null)
            {
                Conversas.Add(sessao, 2, MessageDirection.Outbound, MessageAuthor.Human, respostaHumana);
            }

            // A ocorrencia mais recente aponta para a mensagem; as demais nao se leem.
            return lacuna;
        }
    }

    private static string NewFaq(string pergunta, string resposta = "", params string[] aliases) =>
        JsonSerializer.Serialize(new { action = "new_faq", question = pergunta, aliases, answer = resposta, targetQuestion = "", rationale = "duvida recorrente" });

    // Chaves e permissao

    [Fact]
    public async Task Desligada_ou_sem_modelo_configurado_nada_e_gerado_nem_gravado_e_o_modelo_nao_e_chamado()
    {
        var desligada = new Cenario(enabled: false);
        var lacuna = desligada.Lacuna("Posso tomar cafe?");
        var ex = await Assert.ThrowsAsync<FaqSuggestionUnavailableException>(() => desligada.Servico.GenerateForGapAsync(lacuna.Id));
        Assert.Equal("disabled", ex.Code);
        Assert.Empty(desligada.Local.Requests);
        Assert.Empty(desligada.Sugestoes.Rows);

        var semModelo = new Cenario(withLocal: false);
        var l2 = semModelo.Lacuna("Posso tomar cafe?");
        Assert.Equal("not-configured", (await Assert.ThrowsAsync<FaqSuggestionUnavailableException>(() => semModelo.Servico.GenerateForGapAsync(l2.Id))).Code);
    }

    [Fact]
    public async Task Sem_a_permissao_toda_operacao_e_negada_e_o_modelo_nao_e_chamado()
    {
        var c = new Cenario(context: TestContexts.User(TestContexts.TenantA, Permission.ConversationRead));
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.GenerateForGapAsync(id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.ClusterOpenGapsAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.AnalyzeFaqAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.ListAsync(null, null, 10, 0));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.ApproveAsync(id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.RejectAsync(id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.EditAsync(id, new FaqSuggestionEdit("x", null, null)));
        Assert.Empty(c.Local.Requests);
        Assert.Empty(c.Auditoria.Entries);
    }

    // Gerar a partir de uma lacuna

    [Fact]
    public async Task Gerar_envia_ao_modelo_so_texto_redigido_e_como_dado_e_grava_uma_sugestao_pendente_auditada_sem_conteudo()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso tomar cafe? meu email e ana@exemplo.com e meu cpf 123.456.789-09", "Pode, sem acucar. Ligue (81) 99999-1234 se tiver duvida.");
        c.Local.Reply = _ => NewFaq("Posso tomar cafe antes do exame?", "Pode, sem acucar.", "cafe antes da coleta");

        var r = await c.Servico.GenerateForGapAsync(lacuna.Id);

        var pedido = Assert.Single(c.Local.Requests);
        Assert.DoesNotContain("ana@exemplo.com", pedido.UserContent);
        Assert.DoesNotContain("123.456.789-09", pedido.UserContent);
        Assert.DoesNotContain("99999-1234", pedido.UserContent);
        Assert.Contains("[email]", pedido.UserContent);
        Assert.Contains("<dados>", pedido.UserContent);

        var sugestao = Assert.Single(r!.Suggestions);
        Assert.Equal((FaqSuggestionKind.NewFaq, FaqSuggestionStatus.Pending, "qwen-teste"), (sugestao.Kind, sugestao.Status, sugestao.ModelId));
        Assert.Equal("Posso tomar cafe antes do exame?", sugestao.Payload.Question);
        Assert.Equal("Pode, sem acucar.", sugestao.Payload.Answer);
        Assert.Equal(new[] { lacuna.Id }, sugestao.Payload.GapIds);

        var auditoria = Assert.Single(c.Auditoria.Entries, e => e.Operation == "agents.faq-suggestion.generated");
        Assert.Equal(sugestao.Id.ToString(), auditoria.ResourceId);
        Assert.Null(c.Rascunho); // gerar nunca toca o rascunho
    }

    [Fact]
    public async Task Resposta_humana_com_mensagens_do_cliente_no_meio_e_incerta_e_nao_vai_ao_modelo()
    {
        var c = new Cenario();
        var sessao = Guid.NewGuid();
        var mensagem = Guid.NewGuid();
        var lacuna = c.Lacunas.Add("duvida unica " + Guid.NewGuid(), mensagem: mensagem, sessao: sessao);
        c.Conversas.Add(sessao, 1, MessageDirection.Inbound, MessageAuthor.Customer, "Posso tomar cafe?", id: mensagem);
        c.Conversas.Add(sessao, 2, MessageDirection.Inbound, MessageAuthor.Customer, "E agua?");
        c.Conversas.Add(sessao, 3, MessageDirection.Outbound, MessageAuthor.Human, "Resposta de outra pergunta");
        c.Local.Reply = _ => NewFaq("Posso tomar cafe antes do exame?", "inventada");

        var r = await c.Servico.GenerateForGapAsync(lacuna.Id);

        Assert.DoesNotContain("Resposta de outra pergunta", Assert.Single(c.Local.Requests).UserContent);
        Assert.Null(Assert.Single(r!.Suggestions).Payload.Answer); // sem resposta humana valida, a resposta do modelo e descartada
    }

    [Fact]
    public async Task Ja_havendo_sugestao_pendente_para_a_lacuna_devolve_a_mesma_sem_chamar_o_modelo_de_novo()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso tomar cafe?");
        c.Local.Reply = _ => NewFaq("Posso tomar cafe antes do exame?");

        var primeira = await c.Servico.GenerateForGapAsync(lacuna.Id);
        var segunda = await c.Servico.GenerateForGapAsync(lacuna.Id);

        Assert.Equal(primeira!.Suggestions[0].Id, segunda!.Suggestions[0].Id);
        Assert.Single(c.Local.Requests);
        Assert.Single(c.Sugestoes.Rows);
    }

    [Fact]
    public async Task Modelo_que_pula_ou_responde_lixo_nao_grava_nada()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso tomar cafe?");
        c.Local.Reply = _ => "{\"action\":\"skip\",\"question\":\"\",\"aliases\":[],\"answer\":\"\",\"targetQuestion\":\"\",\"rationale\":\"pessoal\"}";
        var r = await c.Servico.GenerateForGapAsync(lacuna.Id);
        Assert.Empty(r!.Suggestions);
        Assert.Equal((1, "skipped-by-model"), (r.Skipped, r.SkipReason));
        Assert.Empty(c.Sugestoes.Rows);
        Assert.DoesNotContain(c.Auditoria.Entries, e => e.Operation == "agents.faq-suggestion.generated");
    }

    [Fact]
    public async Task Falha_do_modelo_vira_indisponivel_sem_gravar_nada()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso tomar cafe?");
        c.Local.Failure = new LocalGenerationUnavailableException("timeout", "x");
        var ex = await Assert.ThrowsAsync<FaqSuggestionUnavailableException>(() => c.Servico.GenerateForGapAsync(lacuna.Id));
        Assert.Equal("timeout", ex.Code);
        Assert.Empty(c.Sugestoes.Rows);
    }

    [Fact]
    public async Task Lacuna_inexistente_devolve_nulo_e_fonte_indisponivel_nao_chama_o_modelo()
    {
        var c = new Cenario();
        Assert.Null(await c.Servico.GenerateForGapAsync(Guid.NewGuid()));
        var semFonte = c.Lacunas.Add("sem fonte " + Guid.NewGuid());
        var r = await c.Servico.GenerateForGapAsync(semFonte.Id);
        Assert.Equal("source-unavailable", r!.SkipReason);
        Assert.Empty(c.Local.Requests);
    }

    // Aceitar e rejeitar

    [Fact]
    public async Task Aceitar_entrada_nova_vai_ao_rascunho_com_AutoReply_desligado_marca_a_lacuna_e_nao_publica()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso tomar cafe?", "Pode, sem acucar.");
        c.Local.Reply = _ => NewFaq("Posso tomar cafe antes do exame?", "Pode, sem acucar.", "cafe antes da coleta");
        var sugestao = (await c.Servico.GenerateForGapAsync(lacuna.Id))!.Suggestions[0];
        var publicadaAntes = c.Definicoes.Rows.Single(v => v.Status == AgentDefinitionStatus.Published).Number;

        var aceita = await c.Servico.ApproveAsync(sugestao.Id);

        Assert.Equal(FaqSuggestionStatus.Approved, aceita!.Status);
        Assert.NotNull(aceita.AppliedDraftVersion);
        var entrada = c.Rascunho!.Content.Faq.Single(f => f.Question == "Posso tomar cafe antes do exame?");
        Assert.False(entrada.AutoReply); // a resposta direta nunca e ligada pela IA
        Assert.Equal("Pode, sem acucar.", entrada.Answer);
        Assert.Equal(KnowledgeGapStatus.Promoted, lacuna.Status);
        Assert.Equal(publicadaAntes, c.Definicoes.Rows.Single(v => v.Status == AgentDefinitionStatus.Published).Number); // nada publicado
        Assert.Contains(c.Auditoria.Entries, e => e.Operation == "agents.faq-suggestion.approved");
    }

    [Fact]
    public async Task Entrada_nova_sem_resposta_escrita_por_uma_pessoa_nunca_e_aceita()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso tomar cafe?"); // sem resposta humana
        c.Local.Reply = _ => NewFaq("Posso tomar cafe antes do exame?", "inventada pelo modelo");
        var sugestao = (await c.Servico.GenerateForGapAsync(lacuna.Id))!.Suggestions[0];

        var ex = await Assert.ThrowsAsync<FaqSuggestionConflictException>(() => c.Servico.ApproveAsync(sugestao.Id));
        Assert.Equal("answer-required", ex.Code);
        Assert.Equal(FaqSuggestionStatus.Pending, c.Sugestoes.Rows.Single().Status);
        Assert.Null(c.Rascunho);

        // O administrador escreve a resposta e entao aceita.
        await c.Servico.EditAsync(sugestao.Id, new FaqSuggestionEdit(null, "Pode tomar cafe sem acucar.", null));
        var aceita = await c.Servico.ApproveAsync(sugestao.Id);
        Assert.Equal(FaqSuggestionStatus.Approved, aceita!.Status);
        Assert.Equal(1, aceita.Edits);
    }

    [Fact]
    public async Task Aceitar_alias_acrescenta_so_os_aliases_a_entrada_existente_no_rascunho()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso ficar sem comer?");
        c.Local.Reply = _ => JsonSerializer.Serialize(new { action = "alias", question = "posso ficar sem comer", aliases = new[] { "jejum de quantas horas" }, answer = "", targetQuestion = "Preciso de jejum para glicose?", rationale = "mesma duvida" });
        var sugestao = (await c.Servico.GenerateForGapAsync(lacuna.Id))!.Suggestions[0];
        Assert.Equal(FaqSuggestionKind.Alias, sugestao.Kind);

        await c.Servico.ApproveAsync(sugestao.Id);

        var entrada = c.Rascunho!.Content.Faq.Single(f => f.Question == "Preciso de jejum para glicose?");
        Assert.Contains("posso ficar sem comer", entrada.GetAliases());
        Assert.Contains("jejum de quantas horas", entrada.GetAliases());
        Assert.Contains("tem que ficar sem comer", entrada.GetAliases()); // o que ja existia continua
        Assert.Equal("Sim, 8 horas.", entrada.Answer); // a resposta nao muda
        Assert.True(entrada.AutoReply); // a configuracao existente nao muda
    }

    [Fact]
    public async Task Alias_cuja_entrada_alvo_sumiu_ou_colide_vira_conflito_e_nada_e_aplicado()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso ficar sem comer?");
        c.Local.Reply = _ => JsonSerializer.Serialize(new { action = "alias", question = "posso ficar sem comer", aliases = Array.Empty<string>(), answer = "", targetQuestion = "Preciso de jejum para glicose?", rationale = "x" });
        var sugestao = (await c.Servico.GenerateForGapAsync(lacuna.Id))!.Suggestions[0];

        // Outro administrador troca o FAQ do rascunho: a entrada alvo some.
        await c.DefinicaoDoAgente.SaveDraftAsync(new AgentDefinitionContent("Atenda.", new[] { new FaqEntry("Qual o horario de coleta?", "Das 7h as 11h.", null, true) }, "Audio.", "Aviso."));
        var ex = await Assert.ThrowsAsync<FaqSuggestionConflictException>(() => c.Servico.ApproveAsync(sugestao.Id));
        Assert.Equal("target-not-found", ex.Code);
        Assert.Equal(FaqSuggestionStatus.Pending, c.Sugestoes.Rows.Single().Status);
    }

    [Fact]
    public async Task Duplicata_e_contradicao_so_sao_reconhecidas_sem_alterar_o_FAQ()
    {
        var c = new Cenario();
        c.Local.Reply = _ => "{\"findings\":[{\"type\":\"duplicate\",\"a\":1,\"b\":2,\"rationale\":\"mesma pergunta\"}]}";
        var r = await c.Servico.AnalyzeFaqAsync();
        var achado = Assert.Single(r.Suggestions);
        Assert.Equal((FaqSuggestionKind.Duplicate, "Preciso de jejum para glicose?", "Qual o horario de coleta?"), (achado.Kind, achado.Payload.TargetQuestion, achado.Payload.OtherQuestion));

        var antes = c.Definicoes.Rows.Count;
        var aceita = await c.Servico.ApproveAsync(achado.Id);
        Assert.Equal((FaqSuggestionStatus.Approved, (int?)null), (aceita!.Status, aceita.AppliedDraftVersion));
        Assert.Equal(antes, c.Definicoes.Rows.Count);
        Assert.Null(c.Rascunho);

        // O mesmo achado nao se repete enquanto pendente.
        c.Local.Reply = _ => "{\"findings\":[{\"type\":\"duplicate\",\"a\":2,\"b\":1,\"rationale\":\"de novo\"}]}";
        var outro = await c.Servico.AnalyzeFaqAsync();
        Assert.Equal(2, c.Sugestoes.Rows.Count); // a anterior foi aceita; a nova e outra
        Assert.Single(outro.Suggestions);
    }

    [Fact]
    public async Task Rejeitar_nao_muda_o_FAQ_nem_a_lacuna_e_a_decisao_vale_uma_so_vez()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso tomar cafe?", "Pode.");
        c.Local.Reply = _ => NewFaq("Posso tomar cafe antes do exame?", "Pode.");
        var sugestao = (await c.Servico.GenerateForGapAsync(lacuna.Id))!.Suggestions[0];

        var rejeitada = await c.Servico.RejectAsync(sugestao.Id);

        Assert.Equal(FaqSuggestionStatus.Rejected, rejeitada!.Status);
        Assert.Equal(KnowledgeGapStatus.Open, lacuna.Status);
        Assert.Null(c.Rascunho);
        Assert.Equal("suggestion-decided", (await Assert.ThrowsAsync<FaqSuggestionConflictException>(() => c.Servico.ApproveAsync(sugestao.Id))).Code);
        Assert.Equal("suggestion-decided", (await Assert.ThrowsAsync<FaqSuggestionConflictException>(() => c.Servico.RejectAsync(sugestao.Id))).Code);
        Assert.Equal("suggestion-decided", (await Assert.ThrowsAsync<FaqSuggestionConflictException>(() => c.Servico.EditAsync(sugestao.Id, new FaqSuggestionEdit("x", null, null)))).Code);
        Assert.Contains(c.Auditoria.Entries, e => e.Operation == "agents.faq-suggestion.rejected");
    }

    [Fact]
    public async Task Editar_respeita_os_limites_do_FAQ()
    {
        var c = new Cenario();
        var lacuna = c.Lacuna("Posso tomar cafe?", "Pode.");
        c.Local.Reply = _ => NewFaq("Posso tomar cafe antes do exame?", "Pode.");
        var sugestao = (await c.Servico.GenerateForGapAsync(lacuna.Id))!.Suggestions[0];

        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.EditAsync(sugestao.Id, new FaqSuggestionEdit(new string('p', AgentDefinitionContent.QuestionMaxLength + 1), null, null)));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.EditAsync(sugestao.Id, new FaqSuggestionEdit(null, null, Enumerable.Range(0, 11).Select(i => $"alias {i}").ToList())));
        Assert.Equal(0, c.Sugestoes.Rows.Single().Edits);
    }

    // Grupos

    [Fact]
    public async Task Agrupar_cria_uma_sugestao_por_grupo_sem_resposta_e_aceitar_descarta_as_outras_lacunas_cobertas()
    {
        var c = new Cenario();
        var a = c.Lacuna("Aceitam cartao?");
        var b = c.Lacuna("Posso pagar no cartao?");
        var outra = c.Lacuna("Tem estacionamento?");
        c.Local.Reply = _ => "{\"groups\":[{\"members\":[1,2],\"question\":\"Aceitam cartao de credito?\",\"aliases\":[\"pago com cartao\"],\"rationale\":\"mesma duvida\"}]}";

        var r = await c.Servico.ClusterOpenGapsAsync();

        var grupo = Assert.Single(r.Suggestions);
        Assert.Null(grupo.Payload.Answer);
        Assert.Equal(2, grupo.Payload.GapIds!.Count);
        Assert.Equal(KnowledgeGapStatus.Open, a.Status); // gerar nunca decide lacuna

        await c.Servico.EditAsync(grupo.Id, new FaqSuggestionEdit(null, "Aceitamos cartao de credito e debito.", null));
        await c.Servico.ApproveAsync(grupo.Id);

        Assert.Single(new[] { a, b }, g => g.Status == KnowledgeGapStatus.Promoted);
        Assert.Single(new[] { a, b }, g => g.Status == KnowledgeGapStatus.Dismissed);
        Assert.Equal(KnowledgeGapStatus.Open, outra.Status);
        Assert.Contains(c.Rascunho!.Content.Faq, f => f.Question == "Aceitam cartao de credito?" && !f.AutoReply);
    }

    [Fact]
    public async Task Poucas_lacunas_nao_chamam_o_modelo()
    {
        var c = new Cenario();
        c.Lacuna("Aceitam cartao?");
        var r = await c.Servico.ClusterOpenGapsAsync();
        Assert.Equal("not-enough-gaps", r.SkipReason);
        Assert.Empty(c.Local.Requests);
    }

    [Fact]
    public async Task O_texto_do_cliente_nunca_chega_ao_modelo_sem_redacao_no_agrupamento()
    {
        var c = new Cenario();
        c.Lacuna("Aceitam cartao? meu cpf 123.456.789-09");
        c.Lacuna("Posso pagar no cartao? ligue (81) 99999-1234");
        c.Local.Reply = _ => "{\"groups\":[]}";
        await c.Servico.ClusterOpenGapsAsync();
        var conteudo = Assert.Single(c.Local.Requests).UserContent;
        Assert.DoesNotContain("123.456.789-09", conteudo);
        Assert.DoesNotContain("99999-1234", conteudo);
    }

    [Fact]
    public async Task A_lista_filtra_por_situacao_e_tipo_e_valida_a_paginacao()
    {
        var c = new Cenario();
        c.Local.Reply = _ => "{\"findings\":[{\"type\":\"duplicate\",\"a\":1,\"b\":2,\"rationale\":\"x\"}]}";
        await c.Servico.AnalyzeFaqAsync();

        Assert.Equal(1, (await c.Servico.ListAsync(FaqSuggestionStatus.Pending, FaqSuggestionKind.Duplicate, 10, 0)).Total);
        Assert.Equal(0, (await c.Servico.ListAsync(FaqSuggestionStatus.Approved, null, 10, 0)).Total);
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.ListAsync(null, null, 0, 0));
        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.ListAsync(null, null, 101, 0));
        Assert.Null(await c.Servico.GetAsync(Guid.NewGuid()));
    }
}
