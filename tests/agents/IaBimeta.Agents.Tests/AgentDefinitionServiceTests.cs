using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.Audit.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// Os casos de uso da definição do agente (ADR 0017, F): só <c>TenantSettingsManage</c> lê e altera, o turno só usa a versão publicada, publicar aposenta a anterior (uma só publicada por tenant) e toda
/// alteração é auditada SEM o conteúdo (só o identificador da versão).
/// </summary>
public sealed class AgentDefinitionServiceTests
{
    private sealed class Cenario
    {
        public Cenario(RequestContextHolder? contexto = null)
        {
            Contexto = contexto ?? TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage);
            Relogio = new FakeClock();
            Banco = new InMemoryDefinitionStore();
            Auditoria = new FakeAudit();
            Servico = new AgentDefinitionService(Contexto, Banco, Auditoria, Relogio);
        }

        public RequestContextHolder Contexto { get; }

        public FakeClock Relogio { get; }

        public InMemoryDefinitionStore Banco { get; }

        public FakeAudit Auditoria { get; }

        public AgentDefinitionService Servico { get; }

        public Guid UsuarioId => Contexto.Current!.Principal!.UserId;
    }

    private static AgentDefinitionContent Conteudo(string instrucoes = "INSTRUCAO-SECRETA-DO-TENANT: atenda com cortesia.") =>
        new(instrucoes, new[] { new FaqEntry("FAQ-PERGUNTA-SECRETA?", "FAQ-RESPOSTA-SECRETA.") }, "AUDIO-TEXTO-SECRETO", "AVISO-TEXTO-SECRETO");

    // O record da visão e do conteúdo compara a lista do FAQ por referência: a comparação do conteúdo é campo a campo.
    private static void MesmoConteudo(AgentDefinitionContent esperado, AgentDefinitionContent atual)
    {
        Assert.Equal(esperado.Instructions, atual.Instructions);
        Assert.Equal(esperado.Faq, atual.Faq);
        Assert.Equal(esperado.AudioInformativeText, atual.AudioInformativeText);
        Assert.Equal(esperado.HandoffNoticeText, atual.HandoffNoticeText);
    }

    private static void MesmaVisao(AgentDefinitionView esperada, AgentDefinitionView? atual)
    {
        Assert.NotNull(atual);
        Assert.Equal((esperada.Number, esperada.Status, esperada.UpdatedAt, esperada.PublishedAt), (atual.Number, atual.Status, atual.UpdatedAt, atual.PublishedAt));
        MesmoConteudo(esperada.Content, atual.Content);
    }

    // Permissão

    [Fact]
    public async Task Sem_a_permissao_toda_operacao_e_negada_e_nada_e_gravado_nem_auditado()
    {
        var c = new Cenario(TestContexts.User(TestContexts.TenantA, Permission.ConversationRead));

        var excecoes = new[]
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.GetDraftAsync()),
            await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.GetPublishedAsync()),
            await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.SaveDraftAsync(Conteudo())),
            await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.PublishAsync()),
        };

        Assert.All(excecoes, e => Assert.Equal(Permission.TenantSettingsManage, e.Permission));
        Assert.Empty(c.Banco.Rows);
        Assert.Empty(c.Auditoria.Entries);
    }

    [Fact]
    public async Task Usuario_sem_nenhuma_permissao_tambem_e_negado()
    {
        var c = new Cenario(TestContexts.User(TestContexts.TenantA));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.SaveDraftAsync(Conteudo()));

        Assert.Empty(c.Banco.Rows);
    }

    [Fact]
    public async Task O_contexto_de_sistema_sem_usuario_passa_pelo_Require_como_nos_demais_modulos()
    {
        // O Require do SharedKernel libera o contexto sem principal (código confiável do host: seed, job). O ator fica nulo na versão.
        var c = new Cenario(TestContexts.System(TestContexts.TenantA));

        var visao = await c.Servico.SaveDraftAsync(Conteudo());

        Assert.Equal(AgentDefinitionStatus.Draft, visao.Status);
        Assert.Null(Assert.Single(c.Banco.Rows).UpdatedBy);
        Assert.Single(c.Auditoria.Entries);
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_falha_fechado()
    {
        var c = new Cenario(new RequestContextHolder());

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.SaveDraftAsync(Conteudo()));

        Assert.Empty(c.Banco.Rows);
    }

    // Rascunho

    [Fact]
    public async Task Salvar_o_rascunho_cria_a_versao_1_e_a_leitura_devolve_o_conteudo()
    {
        var c = new Cenario();

        var criada = await c.Servico.SaveDraftAsync(Conteudo());
        var lida = await c.Servico.GetDraftAsync();

        Assert.Equal((1, AgentDefinitionStatus.Draft), (criada.Number, criada.Status));
        MesmaVisao(criada, lida);
        MesmoConteudo(Conteudo(), lida!.Content);
        var linha = Assert.Single(c.Banco.Rows);
        Assert.Equal((TestContexts.TenantA, c.UsuarioId), (linha.TenantId, linha.UpdatedBy!.Value));
    }

    [Fact]
    public async Task Salvar_de_novo_troca_o_conteudo_do_mesmo_rascunho_sem_criar_outro()
    {
        var c = new Cenario();
        await c.Servico.SaveDraftAsync(Conteudo("Primeira versão do texto."));
        c.Relogio.Advance(TimeSpan.FromMinutes(5));

        var segunda = await c.Servico.SaveDraftAsync(Conteudo("Segunda versão do texto."));

        var linha = Assert.Single(c.Banco.Rows);
        Assert.Equal((1, "Segunda versão do texto.", 2), (segunda.Number, segunda.Content.Instructions, linha.Revision));
        Assert.Equal(c.Relogio.GetUtcNow(), segunda.UpdatedAt);
    }

    [Fact]
    public async Task Conteudo_invalido_nao_grava_nem_audita()
    {
        var c = new Cenario();

        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.SaveDraftAsync(Conteudo("")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => c.Servico.SaveDraftAsync(null!));

        Assert.Empty(c.Banco.Rows);
        Assert.Empty(c.Auditoria.Entries);
    }

    [Fact]
    public async Task Sem_rascunho_a_leitura_devolve_nulo()
    {
        var c = new Cenario();

        Assert.Null(await c.Servico.GetDraftAsync());
        Assert.Null(await c.Servico.GetPublishedAsync());
    }

    // Publicação

    [Fact]
    public async Task Publicar_transforma_o_rascunho_na_versao_que_o_agente_usa()
    {
        var c = new Cenario();
        await c.Servico.SaveDraftAsync(Conteudo());

        var publicada = await c.Servico.PublishAsync();

        Assert.Equal((1, AgentDefinitionStatus.Published), (publicada.Number, publicada.Status));
        Assert.NotNull(publicada.PublishedAt);
        Assert.Null(await c.Servico.GetDraftAsync());
        MesmaVisao(publicada, await c.Servico.GetPublishedAsync());
        Assert.Equal(c.UsuarioId, Assert.Single(c.Banco.Rows).PublishedBy);
    }

    [Fact]
    public async Task Publicar_sem_rascunho_e_recusado_e_nao_audita()
    {
        var c = new Cenario();

        await Assert.ThrowsAsync<AgentsValidationException>(() => c.Servico.PublishAsync());

        Assert.Empty(c.Banco.Rows);
        Assert.Empty(c.Auditoria.Entries);
    }

    [Fact]
    public async Task Publicar_de_novo_aposenta_a_anterior_e_mantem_uma_so_publicada_com_numeros_crescentes()
    {
        var c = new Cenario();
        await c.Servico.SaveDraftAsync(Conteudo("Versão um do agente."));
        await c.Servico.PublishAsync();

        var segundoRascunho = await c.Servico.SaveDraftAsync(Conteudo("Versão dois do agente."));
        var segundaPublicada = await c.Servico.PublishAsync();

        Assert.Equal(2, segundoRascunho.Number);
        Assert.Equal(2, segundaPublicada.Number);
        Assert.Equal(2, c.Banco.Rows.Count);
        Assert.Single(c.Banco.Rows, v => v.Status == AgentDefinitionStatus.Published);
        Assert.Equal(AgentDefinitionStatus.Retired, c.Banco.Rows.Single(v => v.Number == 1).Status);
        Assert.Equal("Versão dois do agente.", (await c.Servico.GetPublishedAsync())!.Content.Instructions);
    }

    [Fact]
    public async Task O_numero_da_versao_nunca_e_reaproveitado_nem_com_versoes_aposentadas()
    {
        var c = new Cenario();
        for (var i = 1; i <= 3; i++)
        {
            var rascunho = await c.Servico.SaveDraftAsync(Conteudo($"Versão número {i} do agente."));
            Assert.Equal(i, rascunho.Number);
            await c.Servico.PublishAsync();
        }

        Assert.Equal(new[] { 1, 2, 3 }, c.Banco.Rows.Select(v => v.Number).OrderBy(n => n));
        Assert.Equal(new[] { AgentDefinitionStatus.Retired, AgentDefinitionStatus.Retired, AgentDefinitionStatus.Published }, c.Banco.Rows.OrderBy(v => v.Number).Select(v => v.Status));
    }

    // O reader do turno

    [Fact]
    public async Task O_turno_nunca_le_o_rascunho_so_a_versao_publicada()
    {
        var c = new Cenario();
        await c.Servico.SaveDraftAsync(Conteudo("Rascunho que ainda não foi aprovado."));

        Assert.Null(await c.Banco.GetPublishedAsync(default));

        await c.Servico.PublishAsync();
        var publicadoAntes = await c.Banco.GetPublishedAsync(default);
        await c.Servico.SaveDraftAsync(Conteudo("Rascunho novo, ainda sem publicar."));
        var publicadoDepois = await c.Banco.GetPublishedAsync(default);

        Assert.Equal("Rascunho que ainda não foi aprovado.", publicadoAntes!.Instructions);
        MesmoConteudo(publicadoAntes, publicadoDepois!);
    }

    // Auditoria

    [Fact]
    public async Task A_auditoria_registra_o_identificador_da_versao_e_nunca_o_conteudo()
    {
        var c = new Cenario();

        var rascunho = await c.Servico.SaveDraftAsync(Conteudo());
        await c.Servico.PublishAsync();

        Assert.Equal(new[] { "agents.definition.draft-saved", "agents.definition.published" }, c.Auditoria.Entries.Select(e => e.Operation));
        var id = Assert.Single(c.Banco.Rows).Id.ToString();
        Assert.All(c.Auditoria.Entries, e => Assert.Equal(("agents.definition", id, AuditOutcome.Success), (e.ResourceType, e.ResourceId, e.Outcome)));
        Assert.Equal(1, rascunho.Number);

        // Nenhum campo da entrada carrega instruções, FAQ nem os textos do tenant.
        var tudo = string.Join("\n", c.Auditoria.Entries.Select(e => e.ToString()));
        foreach (var segredo in new[] { "INSTRUCAO-SECRETA", "FAQ-PERGUNTA", "FAQ-RESPOSTA", "AUDIO-TEXTO", "AVISO-TEXTO" })
        {
            Assert.DoesNotContain(segredo, tudo);
        }
    }

    [Fact]
    public async Task Cada_alteracao_do_rascunho_e_auditada()
    {
        var c = new Cenario();

        await c.Servico.SaveDraftAsync(Conteudo("Primeiro texto de instruções."));
        await c.Servico.SaveDraftAsync(Conteudo("Segundo texto de instruções."));

        Assert.Equal(2, c.Auditoria.Entries.Count(e => e.Operation == "agents.definition.draft-saved"));
    }

    // Conflito

    [Fact]
    public async Task Conflito_do_armazenamento_ao_salvar_propaga_e_nao_audita()
    {
        var c = new Cenario();
        c.Banco.FailNextSave = true;

        await Assert.ThrowsAsync<AgentDefinitionConflictException>(() => c.Servico.SaveDraftAsync(Conteudo()));

        Assert.Empty(c.Auditoria.Entries);
    }

    [Fact]
    public async Task Conflito_do_armazenamento_ao_publicar_propaga_e_nao_audita()
    {
        var c = new Cenario();
        await c.Servico.SaveDraftAsync(Conteudo());
        var auditadasAntes = c.Auditoria.Entries.Count;
        c.Banco.FailNextSave = true;

        await Assert.ThrowsAsync<AgentDefinitionConflictException>(() => c.Servico.PublishAsync());

        Assert.Equal(auditadasAntes, c.Auditoria.Entries.Count);
    }

    // Tenant

    [Fact]
    public async Task A_versao_criada_pertence_ao_tenant_do_contexto_e_o_outro_tenant_tem_o_proprio_conjunto()
    {
        var a = new Cenario(TestContexts.User(TestContexts.TenantA, Permission.TenantSettingsManage));
        var b = new Cenario(TestContexts.User(TestContexts.TenantB, Permission.TenantSettingsManage));

        await a.Servico.SaveDraftAsync(Conteudo("Instruções exclusivas do tenant A."));
        await a.Servico.PublishAsync();

        Assert.Equal(TestContexts.TenantA, Assert.Single(a.Banco.Rows).TenantId);
        Assert.Empty(b.Banco.Rows);
        Assert.Null(await b.Servico.GetPublishedAsync());
        Assert.Null(await b.Banco.GetPublishedAsync(default));
    }
}
