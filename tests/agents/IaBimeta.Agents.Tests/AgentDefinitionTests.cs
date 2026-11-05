using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// O conteúdo e as versões da definição do agente do tenant (ADR 0017, F): limites de tamanho (cabe inteiro no prompt) e o ciclo rascunho, publicada e aposentada. Só o rascunho muda; a versão
/// publicada nunca é alterada.
/// </summary>
public sealed class AgentDefinitionTests
{
    private static readonly DateTimeOffset T0 = Snapshots.T0;
    private static readonly Guid Usuario = Guid.Parse("33333333-0000-0000-0000-000000000001");

    private static AgentDefinitionContent Conteudo(
        string instrucoes = "Atenda com cortesia.",
        IReadOnlyList<FaqEntry>? faq = null,
        string? audio = "Pode escrever sua dúvida?",
        string? aviso = "Vou transferir você.") =>
        new(instrucoes, faq ?? new[] { new FaqEntry("Qual o horário?", "Das 7h às 17h.") }, audio, aviso);

    private static void Recusa(AgentDefinitionContent conteudo) => Assert.Throws<AgentsValidationException>(conteudo.Validate);

    private static FaqEntry[] Faq(int quantidade, int tamanhoDaPergunta = 10, int tamanhoDaResposta = 10) =>
        Enumerable.Range(0, quantidade).Select(i => new FaqEntry(new string('p', tamanhoDaPergunta), new string('r', tamanhoDaResposta))).ToArray();

    // O conteúdo

    [Fact]
    public void O_conteudo_do_padrao_e_valido()
    {
        Definitions.Default().Validate();
        Conteudo(audio: null, aviso: null).Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Instrucoes_vazias_ou_so_com_espacos_sao_recusadas(string instrucoes)
    {
        Recusa(Conteudo(instrucoes));
    }

    [Fact]
    public void Instrucoes_no_limite_de_8000_caracteres_sao_aceitas_e_acima_recusadas()
    {
        Conteudo(new string('i', AgentDefinitionContent.InstructionsMaxLength)).Validate();

        Recusa(Conteudo(new string('i', AgentDefinitionContent.InstructionsMaxLength + 1)));
    }

    [Fact]
    public void FAQ_nulo_e_recusado()
    {
        Recusa(new AgentDefinitionContent("Atenda.", null!));
    }

    [Fact]
    public void FAQ_com_50_entradas_e_aceito_e_com_51_recusado()
    {
        Conteudo(faq: Faq(AgentDefinitionContent.FaqMaxEntries)).Validate();

        Recusa(Conteudo(faq: Faq(AgentDefinitionContent.FaqMaxEntries + 1)));
    }

    [Fact]
    public void FAQ_vazio_e_aceito()
    {
        Conteudo(faq: Array.Empty<FaqEntry>()).Validate();
    }

    [Theory]
    [InlineData("", "resposta")]
    [InlineData("   ", "resposta")]
    [InlineData("pergunta", "")]
    [InlineData("pergunta", "   ")]
    public void Entrada_do_FAQ_sem_pergunta_ou_sem_resposta_e_recusada(string pergunta, string resposta)
    {
        Recusa(Conteudo(faq: new[] { new FaqEntry(pergunta, resposta) }));
    }

    [Fact]
    public void Entrada_nula_no_FAQ_e_recusada()
    {
        Recusa(Conteudo(faq: new FaqEntry[] { null! }));
    }

    [Fact]
    public void A_pergunta_aceita_300_caracteres_e_a_resposta_1500()
    {
        Conteudo(faq: Faq(1, AgentDefinitionContent.QuestionMaxLength, AgentDefinitionContent.AnswerMaxLength)).Validate();

        Recusa(Conteudo(faq: Faq(1, AgentDefinitionContent.QuestionMaxLength + 1, 10)));
        Recusa(Conteudo(faq: Faq(1, 10, AgentDefinitionContent.AnswerMaxLength + 1)));
    }

    [Fact]
    public void O_FAQ_inteiro_aceita_no_maximo_30000_caracteres_somando_perguntas_e_respostas()
    {
        // 20 entradas de 300 + 1200 = 1500 caracteres: exatamente o teto. A 21ª passa dele.
        Conteudo(faq: Faq(20, 300, 1200)).Validate();

        Recusa(Conteudo(faq: Faq(21, 300, 1200)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Os_textos_opcionais_aceitam_nulo_e_ate_600_caracteres_e_recusam_vazio_ou_longo(bool audio)
    {
        AgentDefinitionContent Com(string? texto) => audio ? Conteudo(audio: texto) : Conteudo(aviso: texto);

        Com(null).Validate();
        Com(new string('t', AgentDefinitionContent.NoticeMaxLength)).Validate();

        Recusa(Com(""));
        Recusa(Com("   "));
        Recusa(Com(new string('t', AgentDefinitionContent.NoticeMaxLength + 1)));
    }

    // A versão: o ciclo de vida

    [Fact]
    public void O_rascunho_nasce_na_versao_1_do_numero_pedido_com_o_conteudo_e_o_tenant()
    {
        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 3, Conteudo(), T0, Usuario);

        Assert.Equal((TestContexts.TenantA, 3, AgentDefinitionStatus.Draft, 1), (versao.TenantId, versao.Number, versao.Status, versao.Revision));
        Assert.Equal((T0, T0, Usuario), (versao.CreatedAt, versao.UpdatedAt, versao.UpdatedBy!.Value));
        Assert.Null(versao.PublishedAt);
        Assert.Null(versao.PublishedBy);
        Assert.NotEqual(Guid.Empty, versao.Id);
    }

    [Fact]
    public void O_rascunho_recusa_numero_invalido_e_conteudo_invalido()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 0, Conteudo(), T0, Usuario));
        Assert.Throws<AgentsValidationException>(() => AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo(""), T0, Usuario));
        Assert.Throws<ArgumentNullException>(() => AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, null!, T0, Usuario));
    }

    [Fact]
    public void O_conteudo_volta_igual_depois_de_aparado_e_de_ida_e_volta_pelo_JSON_do_FAQ()
    {
        var original = new AgentDefinitionContent(
            "  Instruções com acentuação, aspas \"duplas\" e quebra\nde linha.  ",
            new[]
            {
                new FaqEntry("  Aceitam convênio? 😀  ", "  Sim: \"Unimed\" e 'Bradesco'.\nConfirme o plano.  "),
                new FaqEntry("Horário?", "7h às 17h"),
            },
            "  Recebemos seu áudio.  ",
            "  Transferindo.  ");

        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, original, T0, null);
        var lido = versao.Content;

        Assert.Equal("Instruções com acentuação, aspas \"duplas\" e quebra\nde linha.", lido.Instructions);
        Assert.Equal(
            new[] { new FaqEntry("Aceitam convênio? 😀", "Sim: \"Unimed\" e 'Bradesco'.\nConfirme o plano."), new FaqEntry("Horário?", "7h às 17h") },
            lido.Faq);
        Assert.Equal(("Recebemos seu áudio.", "Transferindo."), (lido.AudioInformativeText, lido.HandoffNoticeText));
    }

    [Fact]
    public void O_FAQ_vazio_e_os_textos_nulos_voltam_como_estao()
    {
        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo(faq: Array.Empty<FaqEntry>(), audio: null, aviso: null), T0, null);

        Assert.Empty(versao.Content.Faq);
        Assert.Null(versao.Content.AudioInformativeText);
        Assert.Null(versao.Content.HandoffNoticeText);
    }

    [Fact]
    public void So_o_rascunho_muda_de_conteudo_e_a_revisao_sobe()
    {
        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo("Primeira."), T0, null);

        versao.ReplaceDraft(Conteudo("Segunda."), T0.AddMinutes(1), Usuario);

        Assert.Equal(("Segunda.", 2, T0.AddMinutes(1), (Guid?)Usuario), (versao.Content.Instructions, versao.Revision, versao.UpdatedAt, versao.UpdatedBy));
        Assert.Equal(T0, versao.CreatedAt);
    }

    [Fact]
    public void Trocar_o_rascunho_por_conteudo_invalido_lanca_e_nao_muda_nada()
    {
        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo("Boa."), T0, null);

        Assert.Throws<AgentsValidationException>(() => versao.ReplaceDraft(Conteudo(""), T0.AddMinutes(1), Usuario));

        Assert.Equal(("Boa.", 1), (versao.Content.Instructions, versao.Revision));
    }

    [Fact]
    public void Publicar_o_rascunho_grava_quando_e_quem_publicou_e_sobe_a_revisao()
    {
        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo(), T0, null);

        versao.Publish(T0.AddHours(1), Usuario);

        Assert.Equal((AgentDefinitionStatus.Published, 2, (DateTimeOffset?)T0.AddHours(1), (Guid?)Usuario), (versao.Status, versao.Revision, versao.PublishedAt, versao.PublishedBy));
    }

    [Fact]
    public void A_versao_publicada_nunca_e_alterada_e_nao_se_publica_duas_vezes()
    {
        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo("Aprovada."), T0, null);
        versao.Publish(T0, Usuario);

        Assert.Throws<AgentsValidationException>(() => versao.ReplaceDraft(Conteudo("Alterada."), T0.AddMinutes(1), Usuario));
        Assert.Throws<AgentsValidationException>(() => versao.Publish(T0.AddMinutes(1), Usuario));

        Assert.Equal(("Aprovada.", 2), (versao.Content.Instructions, versao.Revision));
    }

    [Fact]
    public void Aposentar_so_vale_para_a_publicada_e_a_aposentada_nao_muda_mais()
    {
        var rascunho = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo(), T0, null);
        Assert.Throws<AgentsValidationException>(() => rascunho.Retire(T0));

        rascunho.Publish(T0, Usuario);
        rascunho.Retire(T0.AddDays(1));

        Assert.Equal((AgentDefinitionStatus.Retired, 3, T0.AddDays(1)), (rascunho.Status, rascunho.Revision, rascunho.UpdatedAt));
        Assert.NotNull(rascunho.PublishedAt);
        Assert.Throws<AgentsValidationException>(() => rascunho.Retire(T0.AddDays(2)));
        Assert.Throws<AgentsValidationException>(() => rascunho.ReplaceDraft(Conteudo(), T0.AddDays(2), Usuario));
        Assert.Throws<AgentsValidationException>(() => rascunho.Publish(T0.AddDays(2), Usuario));
    }

    [Fact]
    public void A_revisao_sobe_a_cada_mudanca_do_ciclo_inteiro()
    {
        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo(), T0, null);
        var revisoes = new List<int> { versao.Revision };

        versao.ReplaceDraft(Conteudo("A."), T0, null);
        revisoes.Add(versao.Revision);
        versao.Publish(T0, null);
        revisoes.Add(versao.Revision);
        versao.Retire(T0);
        revisoes.Add(versao.Revision);

        Assert.Equal(new[] { 1, 2, 3, 4 }, revisoes);
    }

    [Fact]
    public void Duas_versoes_do_mesmo_conteudo_nao_compartilham_estado()
    {
        var conteudo = Conteudo();
        var a = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, conteudo, T0, null);
        var b = AgentDefinitionVersion.CreateDraft(TestContexts.TenantB, 1, conteudo, T0, null);

        b.ReplaceDraft(Conteudo("Só do tenant B."), T0, null);

        Assert.Equal("Atenda com cortesia.", a.Content.Instructions);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal((TestContexts.TenantA, TestContexts.TenantB), (a.TenantId, b.TenantId));
    }
}
