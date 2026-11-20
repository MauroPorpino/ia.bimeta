using System.Reflection;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// E2.5 (ADR 0041, seção 5; D3): a decisão de saída do cliente é uma função PURA e DETERMINÍSTICA. Os 16 cenários de aceite, a matriz de normalização, os falsos positivos e negativos, a linguagem natural e a prova de que nenhum
/// modelo, fluxo ou agente participa da decisão.
/// </summary>
public sealed class OptOutDeciderTests
{
    private static readonly OptOutPolicy Policy = OptOutPolicy.Default;

    // Contextos explícitos: o que o Conversations já sabe da última saída.
    private static readonly OptOutContext SemSaida = OptOutContext.NoOutbound;
    private static readonly OptOutContext AposIa = new(true, MessageAuthor.Ai, false, false, false);
    private static readonly OptOutContext AposAtendente = new(true, MessageAuthor.Human, false, false, false);
    private static readonly OptOutContext AposModelo = new(true, MessageAuthor.Human, true, false, false);
    private static readonly OptOutContext AposFluxoTextoLivre = new(true, MessageAuthor.Bot, false, true, false);
    private static readonly OptOutContext AposFluxoTextoLivreJaRespondido = new(true, MessageAuthor.Bot, false, true, true);
    private static readonly OptOutContext AposFluxoSemTextoLivre = new(true, MessageAuthor.Bot, false, false, false);

    private static OptOutDecision Decidir(string? texto, OptOutContext contexto, MessageKind tipo = MessageKind.Text) => OptOutDecider.Decide(tipo, texto, contexto, Policy);

    private static void Confirmada(OptOutDecision d, string palavra)
    {
        Assert.Equal(OptOutDecisionKind.Confirmed, d.Kind);
        Assert.Equal(palavra, d.Keyword);
        Assert.Null(d.Reason);
    }

    private static void Ambigua(OptOutDecision d, string? palavra, OptOutReviewReason motivo)
    {
        Assert.Equal(OptOutDecisionKind.PossibleOptOut, d.Kind);
        Assert.Equal(palavra, d.Keyword);
        Assert.Equal(motivo, d.Reason);
    }

    // ------------------------------------------------------------------ os 16 cenários de aceite do ADR 0041, seção 5

    [Fact]
    public void Cenario_1_e_2_SAIR_ou_CANCELAR_depois_de_pergunta_de_IA_com_ou_sem_interrogacao_e_ambigua_porque_o_ponto_de_interrogacao_nao_decide()
    {
        // A decisão NÃO recebe o texto da saída: só o estado explícito (autor, modelo, marcador). Logo "Deseja cancelar?" e "Informe se deseja cancelar." são indistinguíveis, e ambas ambíguas.
        Ambigua(Decidir("CANCELAR", AposIa), "CANCELAR", OptOutReviewReason.ConversationalContext);
        Ambigua(Decidir("cancelar", AposIa), "CANCELAR", OptOutReviewReason.ConversationalContext);
    }

    [Fact]
    public void Cenario_3_e_4_SAIR_depois_de_afirmacao_da_IA_ou_de_pergunta_do_atendente_e_ambigua()
    {
        Ambigua(Decidir("SAIR", AposIa), "SAIR", OptOutReviewReason.ConversationalContext);
        Ambigua(Decidir("Sair!", AposAtendente), "SAIR", OptOutReviewReason.ConversationalContext);
    }

    [Theory]
    [InlineData("PARAR")]
    [InlineData("parar")]
    [InlineData("  Parar.  ")]
    [InlineData("PARAR!!!")]
    public void Cenario_5_PARAR_depois_de_IA_ou_de_atendente_e_sempre_confirmada(string texto)
    {
        Confirmada(Decidir(texto, AposIa), "PARAR");
        Confirmada(Decidir(texto, AposAtendente), "PARAR");
        Confirmada(Decidir(texto, SemSaida), "PARAR");
        Confirmada(Decidir(texto, AposModelo), "PARAR");
        Confirmada(Decidir(texto, AposFluxoSemTextoLivre), "PARAR");
    }

    [Theory]
    [InlineData("SAIR", "SAIR")]
    [InlineData("cancelar", "CANCELAR")]
    public void Cenario_6_SAIR_ou_CANCELAR_em_resposta_a_modelo_de_mensagem_e_confirmada(string texto, string palavra) => Confirmada(Decidir(texto, AposModelo), palavra);

    [Theory]
    [InlineData("CANCELAR", "CANCELAR")]
    [InlineData("sair", "SAIR")]
    public void Cenario_7_sem_nenhuma_saida_a_palavra_e_confirmada(string texto, string palavra) => Confirmada(Decidir(texto, SemSaida), palavra);

    [Theory]
    [InlineData("PARAR", "PARAR")]
    [InlineData("SAIR", "SAIR")]
    [InlineData("CANCELAR", "CANCELAR")]
    public void Cenario_8_fluxo_com_texto_livre_na_primeira_entrada_qualquer_das_tres_palavras_e_ambigua(string texto, string palavra) =>
        Ambigua(Decidir(texto, AposFluxoTextoLivre), palavra, OptOutReviewReason.FlowFreeText);

    [Fact]
    public void Cenario_9_o_marcador_do_fluxo_so_vale_para_a_primeira_entrada()
    {
        Confirmada(Decidir("PARAR", AposFluxoTextoLivreJaRespondido), "PARAR");
        Ambigua(Decidir("SAIR", AposFluxoTextoLivreJaRespondido), "SAIR", OptOutReviewReason.ConversationalContext);
    }

    [Theory]
    [InlineData("não quero parar")]
    [InlineData("sair do plano amanhã")]
    [InlineData("SAIR AGORA")]
    [InlineData("parar agora")]
    [InlineData("quero cancelar o exame")]
    [InlineData("vou sair")]
    public void Cenario_10_frases_que_apenas_contem_a_palavra_nunca_sao_comando_nem_possivel_saida(string texto)
    {
        foreach (var contexto in new[] { SemSaida, AposIa, AposAtendente, AposModelo, AposFluxoTextoLivre })
        {
            Assert.Equal(OptOutDecisionKind.NotCommand, Decidir(texto, contexto).Kind);
        }
    }

    [Theory]
    [InlineData("pare de me ligar")]
    [InlineData("Por favor, parem de me mandar mensagens. Pare de enviar!")]
    [InlineData("não quero mais receber mensagens")]
    [InlineData("Não quero receber propaganda")]
    [InlineData("não me envie mais nada")]
    [InlineData("nao me ligue")]
    [InlineData("não me mande isso")]
    [InlineData("quero me descadastrar")]
    [InlineData("DESCADASTRE meu número")]
    [InlineData("quero cancelar minha inscrição")]
    [InlineData("cancelar inscricao agora")]
    [InlineData("pode me tirar? sair da lista")]
    [InlineData("quero remover meu número")]
    [InlineData("parar de receber")]
    public void Cenario_10b_intencao_em_linguagem_natural_nao_revoga_mas_nao_e_descartada(string texto)
    {
        foreach (var contexto in new[] { SemSaida, AposIa, AposModelo, AposFluxoTextoLivre })
        {
            Ambigua(Decidir(texto, contexto), null, OptOutReviewReason.NaturalLanguageIntent);
        }
    }

    [Theory]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Audio)]
    [InlineData(MessageKind.Document)]
    public void Cenario_11_mensagem_que_nao_e_texto_nunca_e_comando_mesmo_com_legenda_igual_a_palavra(MessageKind tipo)
    {
        Assert.Equal(OptOutDecisionKind.NotCommand, Decidir("PARAR", SemSaida, tipo).Kind);
        Assert.Equal(OptOutDecisionKind.NotCommand, Decidir(null, SemSaida, tipo).Kind);
        Assert.Equal(OptOutDecisionKind.NotCommand, Decidir("pare de me ligar", AposIa, tipo).Kind);
    }

    [Fact]
    public void Cenario_13_modelo_de_mensagem_e_depois_resposta_de_IA_a_ultima_saida_e_a_da_IA_logo_SAIR_e_ambigua()
    {
        // A última saída é a da IA (não é modelo): o contexto explícito já diz isso.
        Ambigua(Decidir("SAIR", AposIa), "SAIR", OptOutReviewReason.ConversationalContext);
    }

    [Fact]
    public void Cenario_15_invariante_nenhuma_ambigua_e_confirmada_e_so_Confirmed_tem_palavra_sem_motivo()
    {
        var textos = new[] { "PARAR", "SAIR", "CANCELAR", "pare de me ligar", "sair", "cancelar" };
        var contextos = new[] { SemSaida, AposIa, AposAtendente, AposModelo, AposFluxoTextoLivre, AposFluxoTextoLivreJaRespondido, AposFluxoSemTextoLivre };
        foreach (var texto in textos)
        {
            foreach (var contexto in contextos)
            {
                var d = Decidir(texto, contexto);
                Assert.Equal(d.Kind == OptOutDecisionKind.PossibleOptOut, d.Reason is not null); // todo "possível" tem motivo; nenhum confirmado tem
            }
        }
    }

    // ------------------------------------------------------------------ normalização (falsos negativos: caixa, acento, pontuação, espaços)

    [Theory]
    [InlineData("PARAR")]
    [InlineData("Parar")]
    [InlineData("  parar  ")]
    [InlineData("parar!")]
    [InlineData("...parar...")]
    [InlineData("¡Parar!")]
    [InlineData("\tparar\n")]
    [InlineData("párar")] // acento combinante solto: removido
    [InlineData("​parar")] // espaço de largura zero: invisível
    [InlineData("PARAR ")] // espaço não separável
    public void Variacoes_de_caixa_pontuacao_espacos_e_caracteres_invisiveis_da_palavra_inteira_sao_a_mesma_palavra(string texto) => Confirmada(Decidir(texto, SemSaida), "PARAR");

    [Theory]
    [InlineData("SAÍR", "SAIR")]
    [InlineData("Sair,", "SAIR")]
    [InlineData("CÂNCELAR", "CANCELAR")]
    [InlineData("cancelar?", "CANCELAR")]
    public void Acentos_e_pontuacao_sao_normalizados(string texto, string palavra) => Confirmada(Decidir(texto, SemSaida), palavra);

    [Theory]
    [InlineData("PARAR 🛑")] // símbolo/emoji não é a palavra inteira: nunca se adivinha
    [InlineData("pararr")]
    [InlineData("pa rar")]
    [InlineData("p.a.r.a.r")]
    [InlineData("parar parar")]
    [InlineData("parar, por favor")]
    [InlineData("PARAR1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void O_que_nao_e_exatamente_a_palavra_inteira_nao_e_comando(string texto) => Assert.Equal(OptOutDecisionKind.NotCommand, Decidir(texto, SemSaida).Kind);

    [Fact]
    public void A_decisao_nao_depende_da_cultura_da_maquina()
    {
        var atual = Thread.CurrentThread.CurrentCulture;
        try
        {
            foreach (var cultura in new[] { "tr-TR", "pt-BR", "en-US" })
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(cultura);
                Confirmada(Decidir("SAIR", SemSaida), "SAIR"); // "I" turco não vira "ı"
                Confirmada(Decidir("sair", SemSaida), "SAIR");
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = atual;
        }
    }

    [Fact]
    public void Texto_nulo_vazio_ou_em_branco_nao_e_comando_e_contexto_ou_politica_nulos_sao_recusados()
    {
        Assert.Equal(OptOutDecisionKind.NotCommand, Decidir(null, SemSaida).Kind);
        Assert.Throws<ArgumentNullException>(() => OptOutDecider.Decide(MessageKind.Text, "PARAR", null!, Policy));
        Assert.Throws<ArgumentNullException>(() => OptOutDecider.Decide(MessageKind.Text, "PARAR", SemSaida, null!));
    }

    // ------------------------------------------------------------------ a política e a função pura (cenário 16)

    [Fact]
    public void A_politica_padrao_tem_as_tres_palavras_e_todas_as_finalidades_e_PARAR_nunca_e_contextual()
    {
        Assert.Equal(new[] { "cancelar", "parar", "sair" }, Policy.Words.Order().ToArray());
        Assert.Equal(new[] { "cancelar", "sair" }, Policy.ContextualWords.Order().ToArray());
        Assert.Equal(new[] { ConsentPurpose.Utility, ConsentPurpose.Marketing }, Policy.Purposes.ToArray());
        Assert.NotEmpty(Policy.Intents);
    }

    [Fact]
    public void Cenario_16_o_decisor_e_funcao_pura_sem_estado_sem_dependencia_e_sem_referencia_a_Agents_AiGateway_ou_LLM()
    {
        var tipo = typeof(OptOutDecider);
        Assert.True(tipo.IsAbstract && tipo.IsSealed, "O decisor é uma classe estática (sem instância nem estado).");
        Assert.Empty(tipo.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.DoesNotContain(tipo.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), f => !f.IsInitOnly && !f.IsLiteral); // nada mutável

        // Os tipos de TODOS os parâmetros e retornos do decisor são do próprio módulo, do SharedKernel ou do sistema: nenhum de Agents, AiGateway ou Workflows.
        var tipos = tipo.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)).Distinct().ToList();
        Assert.All(tipos, t => Assert.DoesNotMatch("Agents|AiGateway|Workflows|Llm|OpenAi|Anthropic", t.Assembly.GetName().Name + "." + t.FullName));

        // O assembly do Conversations.Core nem referencia os módulos de IA, de agentes ou de fluxos (a matriz de dependências já o impede; aqui fica provado no binário).
        var referencias = typeof(OptOutDecider).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Assert.DoesNotContain(referencias, r => r.Contains("Agents", StringComparison.Ordinal) || r.Contains("AiGateway", StringComparison.Ordinal) || r.Contains("Workflows", StringComparison.Ordinal));
    }

    [Fact]
    public void A_decisao_e_deterministica_a_mesma_entrada_sempre_o_mesmo_resultado()
    {
        var primeira = Decidir("Sair!", AposIa);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(primeira, Decidir("Sair!", AposIa));
        }
    }
}
