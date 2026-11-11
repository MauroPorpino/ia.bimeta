using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Tests;

/// <summary>A publicação do conhecimento alimenta o índice semântico (ADR 0034): só texto aprovado, uma linha por pergunta e por alias, e uma falha do índice nunca falha nem desfaz a publicação.</summary>
public sealed class SemanticIndexHookTests
{
    private sealed class FakeIndexing : IKnowledgeIndexing
    {
        public List<SemanticSource> Sources { get; } = [];

        public Exception? Failure { get; set; }

        public Task<SemanticSyncResult> SyncAsync(SemanticSource source, CancellationToken cancellationToken = default)
        {
            Sources.Add(source);
            return Failure is null ? Task.FromResult(new SemanticSyncResult(source.Texts.Count, 0, source.Texts.Count, 0)) : throw Failure;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SemanticIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SemanticIndexStatus(null, "m", 8, 0, 0, 0, 0));
    }

    private static AgentDefinitionContent Conteudo(string? locale = null) => new(
        "instrucoes",
        [
            new FaqEntry("Preciso de jejum?", "Sim, 8 horas.", ["tenho que ficar sem comer?", "jejum para coleta"], AutoReply: true),
            new FaqEntry("Qual o horario?", "Das 7h as 11h."),
        ],
        null,
        null,
        locale);

    private static (AgentDefinitionService Service, InMemoryDefinitionStore Store, FakeIndexing Index) Build()
    {
        var contexto = TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage);
        var store = new InMemoryDefinitionStore();
        var index = new FakeIndexing();
        return (new AgentDefinitionService(contexto, store, new FakeAudit(), new FakeClock(), index), store, index);
    }

    [Fact]
    public async Task Publicar_envia_ao_indice_uma_linha_por_pergunta_e_por_alias_com_a_elegibilidade_da_entrada()
    {
        var (service, _, index) = Build();
        await service.SaveDraftAsync(Conteudo());

        var publicada = await service.PublishAsync();

        var fonte = Assert.Single(index.Sources);
        Assert.Equal(publicada.Number, fonte.DefinitionNumber);
        Assert.Equal("pt-BR", fonte.Locale);
        Assert.Equal(4, fonte.Texts.Count);
        Assert.Equal((0, SemanticTextKind.Question, 0, true), (fonte.Texts[0].EntryOrdinal, fonte.Texts[0].Kind, fonte.Texts[0].AliasOrdinal, fonte.Texts[0].AutoReplyEligible));
        Assert.Equal((0, SemanticTextKind.Alias, 1), (fonte.Texts[1].EntryOrdinal, fonte.Texts[1].Kind, fonte.Texts[1].AliasOrdinal));
        Assert.Equal((0, SemanticTextKind.Alias, 2), (fonte.Texts[2].EntryOrdinal, fonte.Texts[2].Kind, fonte.Texts[2].AliasOrdinal));
        Assert.Equal((1, SemanticTextKind.Question, 0, false), (fonte.Texts[3].EntryOrdinal, fonte.Texts[3].Kind, fonte.Texts[3].AliasOrdinal, fonte.Texts[3].AutoReplyEligible));
    }

    [Fact]
    public async Task Rascunho_nunca_vai_ao_indice()
    {
        var (service, _, index) = Build();
        await service.SaveDraftAsync(Conteudo());
        Assert.Empty(index.Sources);
    }

    [Fact]
    public async Task O_indice_nunca_recebe_a_resposta_nem_as_instrucoes()
    {
        var (service, _, index) = Build();
        await service.SaveDraftAsync(Conteudo());
        await service.PublishAsync();
        Assert.DoesNotContain(index.Sources.Single().Texts, t => t.Text.Contains("8 horas") || t.Text.Contains("instrucoes"));
    }

    [Fact]
    public async Task Falha_do_indice_nao_falha_nem_desfaz_a_publicacao()
    {
        var (service, store, index) = Build();
        index.Failure = new InvalidOperationException("banco sem pgvector");
        await service.SaveDraftAsync(Conteudo());

        var publicada = await service.PublishAsync();

        Assert.Equal(AgentDefinitionStatus.Published, publicada.Status);
        Assert.Equal(AgentDefinitionStatus.Published, (await service.GetPublishedAsync())!.Status);
    }

    [Fact]
    public async Task Idioma_do_conhecimento_acompanha_o_indice()
    {
        var (service, _, index) = Build();
        await service.SaveDraftAsync(Conteudo("en"));
        await service.PublishAsync();
        Assert.Equal("en", index.Sources.Single().Locale);
    }

    [Fact]
    public async Task Sem_indice_registrado_a_publicacao_funciona_como_antes()
    {
        var contexto = TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage);
        var service = new AgentDefinitionService(contexto, new InMemoryDefinitionStore(), new FakeAudit(), new FakeClock());
        await service.SaveDraftAsync(Conteudo());
        Assert.Equal(AgentDefinitionStatus.Published, (await service.PublishAsync()).Status);
    }
}
