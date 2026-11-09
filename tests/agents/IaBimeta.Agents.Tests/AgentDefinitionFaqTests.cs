using System.Reflection;
using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// Os campos da resposta direta do FAQ (ADR 0029) na definição do agente: aliases, <c>AutoReply</c> e o idioma do FAQ. Validação, a forma em que a definição os guarda e, principalmente, a REGRESSÃO: uma
/// definição antiga (JSON sem os campos novos, sem idioma) continua válida, com a resposta direta DESLIGADA e o idioma <c>pt-BR</c>.
/// </summary>
public sealed class AgentDefinitionFaqTests
{
    private static readonly DateTimeOffset T0 = Snapshots.T0;

    private static AgentDefinitionContent Conteudo(IReadOnlyList<FaqEntry> faq, string? idioma = null) =>
        new("Atenda com cortesia.", faq, null, null, idioma);

    private static void Recusa(AgentDefinitionContent conteudo) => Assert.Throws<AgentsValidationException>(conteudo.Validate);

    // Validação dos aliases e do AutoReply

    [Fact]
    public void Uma_entrada_com_aliases_e_resposta_direta_e_valida()
    {
        Conteudo(new[] { new FaqEntry("Qual o horário?", "Das 7h às 17h.", new[] { "que horas abrem", "horário de funcionamento" }, true) }).Validate();
    }

    [Fact]
    public void Ate_10_aliases_por_entrada_sao_aceitos_e_11_recusados()
    {
        var dez = Enumerable.Range(0, AgentDefinitionContent.AliasMaxCount).Select(i => $"alias {i}").ToArray();

        Conteudo(new[] { new FaqEntry("P?", "R", dez, true) }).Validate();

        Recusa(Conteudo(new[] { new FaqEntry("P?", "R", dez.Append("alias 10").ToArray(), true) }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    [InlineData("😀")]
    public void Alias_vazio_ou_sem_letras_e_numeros_e_recusado(string alias)
    {
        Recusa(Conteudo(new[] { new FaqEntry("P?", "R", new[] { alias }, true) }));
    }

    [Fact]
    public void O_alias_aceita_ate_300_caracteres_e_acima_e_recusado()
    {
        Conteudo(new[] { new FaqEntry("P?", "R", new[] { new string('a', AgentDefinitionContent.QuestionMaxLength) }, true) }).Validate();

        Recusa(Conteudo(new[] { new FaqEntry("P?", "R", new[] { new string('a', AgentDefinitionContent.QuestionMaxLength + 1) }, true) }));
    }

    [Fact]
    public void Entrada_com_resposta_direta_precisa_de_pergunta_com_letras_ou_numeros_mas_a_sem_resposta_direta_segue_valida_como_antes()
    {
        Recusa(Conteudo(new[] { new FaqEntry("???", "R", null, true) }));

        Conteudo(new[] { new FaqEntry("???", "R", null, false) }).Validate();
    }

    [Fact]
    public void Os_aliases_nao_entram_no_limite_total_do_FAQ_que_e_o_do_prompt()
    {
        var dez = Enumerable.Range(0, AgentDefinitionContent.AliasMaxCount).Select(i => new string((char)('a' + i), AgentDefinitionContent.QuestionMaxLength)).ToArray();
        var faq = Enumerable.Range(0, 20).Select(_ => new FaqEntry("p", new string('r', 1499), dez, true)).ToArray(); // 20 x (1 + 1499) = 30.000, o limite, sem contar 20 x 3.000 de aliases

        Conteudo(faq).Validate();
    }

    // Idioma do FAQ

    [Fact]
    public void Sem_idioma_o_FAQ_e_pt_BR()
    {
        var conteudo = Conteudo(new[] { new FaqEntry("P?", "R") });

        Assert.Null(conteudo.FaqLocale);
        Assert.Equal("pt-BR", conteudo.EffectiveFaqLocale.ToString());
        conteudo.Validate();
    }

    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("en-US", "en-US")]
    [InlineData("es", "es")]
    public void O_idioma_informado_e_validado_e_resolvido(string informado, string esperado)
    {
        var conteudo = Conteudo(new[] { new FaqEntry("P?", "R") }, informado);

        conteudo.Validate();
        Assert.Equal(esperado, conteudo.EffectiveFaqLocale.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("portugues")]
    [InlineData("pt_BR")]
    [InlineData("pt BR")]
    [InlineData("a")]
    public void Idioma_mal_formado_e_recusado(string idioma)
    {
        Recusa(Conteudo(new[] { new FaqEntry("P?", "R") }, idioma));
    }

    // O que a definição guarda

    [Fact]
    public void A_definicao_guarda_aliases_limpos_a_resposta_direta_e_o_idioma_canonico()
    {
        var faq = new[]
        {
            new FaqEntry("  Qual o horário?  ", "  Das 7h às 17h.  ", new[] { "  Que horas abrem?  ", "que horas ABREM", "horário de funcionamento" }, true),
            new FaqEntry("Aceitam convênio?", "Consulte.", null, false),
        };

        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo(faq, "pt-br"), T0, null);
        var conteudo = versao.Content;

        Assert.Equal("pt-BR", versao.FaqLocale);
        Assert.Equal("pt-BR", conteudo.FaqLocale);
        var primeira = conteudo.Faq[0];
        Assert.Equal(("Qual o horário?", "Das 7h às 17h.", true), (primeira.Question, primeira.Answer, primeira.AutoReply));
        Assert.Equal(new[] { "Que horas abrem?", "horário de funcionamento" }, primeira.Aliases);
        var segunda = conteudo.Faq[1];
        Assert.Equal((false, 0), (segunda.AutoReply, segunda.GetAliases().Count));
    }

    [Fact]
    public void Trocar_o_rascunho_troca_aliases_resposta_direta_e_idioma()
    {
        var versao = AgentDefinitionVersion.CreateDraft(
            TestContexts.TenantA, 1, Conteudo(new[] { new FaqEntry("P?", "R", new[] { "a1" }, true) }, "en-US"), T0, null);

        versao.ReplaceDraft(Conteudo(new[] { new FaqEntry("P?", "R", null, false) }), T0, null);

        Assert.Null(versao.FaqLocale);
        var entrada = Assert.Single(versao.Content.Faq);
        Assert.Equal((false, 0), (entrada.AutoReply, entrada.GetAliases().Count));
    }

    // Regressão: definição antiga

    [Fact]
    public void FAQ_antigo_sem_os_campos_novos_le_com_resposta_direta_desligada_sem_aliases_e_em_pt_BR()
    {
        var versao = AgentDefinitionVersion.CreateDraft(TestContexts.TenantA, 1, Conteudo(new[] { new FaqEntry("P?", "R") }), T0, null);
        // O que o banco tinha antes do ADR 0029: pares pergunta e resposta, nada mais, e nenhum idioma.
        typeof(AgentDefinitionVersion).GetProperty(nameof(AgentDefinitionVersion.FaqJson))!
            .SetValue(versao, "[{\"question\":\"Qual o horário?\",\"answer\":\"Das 7h às 17h.\"},{\"question\":\"Onde fica?\",\"answer\":\"Na matriz.\"}]");
        typeof(AgentDefinitionVersion).GetProperty(nameof(AgentDefinitionVersion.FaqLocale))!.SetValue(versao, null);

        var conteudo = versao.Content;

        Assert.Equal(2, conteudo.Faq.Count);
        Assert.All(conteudo.Faq, e => Assert.False(e.AutoReply));
        Assert.All(conteudo.Faq, e => Assert.Empty(e.GetAliases()));
        Assert.Equal("pt-BR", conteudo.EffectiveFaqLocale.ToString());
        conteudo.Validate();

        // E a busca nunca dá resposta direta numa definição antiga, mesmo com a pergunta idêntica.
        Assert.Equal(FaqMatchKind.NotAutoReply, FaqMatcher.Match(conteudo.Faq, "qual o horario").Kind);
    }

    [Fact]
    public void A_definicao_antiga_continua_valida_e_republicavel_com_duplicatas_e_perguntas_so_de_simbolos()
    {
        // Regras novas só valem para o que é novo: o FAQ antigo pode ter perguntas repetidas e sem letras.
        var antigo = new[] { new FaqEntry("???", "R1"), new FaqEntry("Qual o horário?", "R2"), new FaqEntry("Qual o horário?", "R3") };

        Conteudo(antigo).Validate();
    }

    // O prompt nunca leva os aliases nem a marca

    [Fact]
    public void O_prompt_leva_so_pergunta_e_resposta_nunca_os_aliases_nem_a_resposta_direta()
    {
        var definicao = Conteudo(new[] { new FaqEntry("Qual o horário de atendimento?", "Das 7h às 17h.", new[] { "ALIAS-SO-DA-BUSCA-123" }, true) });

        var prompt = AgentPrompt.BuildSystemInstruction(definicao, Snapshots.OneText());

        Assert.Contains("Qual o horário de atendimento?", prompt);
        Assert.Contains("Das 7h às 17h.", prompt);
        Assert.DoesNotContain("ALIAS-SO-DA-BUSCA-123", prompt);
        Assert.DoesNotContain("AutoReply", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void O_JSON_guardado_usa_nomes_estaveis_para_o_banco()
    {
        var versao = AgentDefinitionVersion.CreateDraft(
            TestContexts.TenantA, 1, Conteudo(new[] { new FaqEntry("P?", "R", new[] { "a1" }, true) }), T0, null);

        var json = (string)typeof(AgentDefinitionVersion).GetProperty(nameof(AgentDefinitionVersion.FaqJson), BindingFlags.Public | BindingFlags.Instance)!.GetValue(versao)!;

        Assert.Contains("\"question\":\"P?\"", json);
        Assert.Contains("\"aliases\":[\"a1\"]", json);
        Assert.Contains("\"autoReply\":true", json);
    }
}
