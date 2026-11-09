using IaBimeta.Agents.Domain;

namespace IaBimeta.Agents;

/// <summary>
/// A busca determinística do FAQ (ADR 0029): a ÚNICA regra é igualdade do texto normalizado com a pergunta ou com um alias cadastrado. Cada teste prova uma recusa tanto quanto um acerto, porque o
/// risco de uma resposta direta é a resposta ERRADA: nada de "quase igual", percentual de termos ou erro de digitação.
/// </summary>
public sealed class FaqMatcherTests
{
    private static FaqEntry Entrada(string pergunta, string resposta = "Resposta aprovada.", bool direta = true, params string[] aliases) =>
        new(pergunta, resposta, aliases, direta);

    private static FaqMatch Buscar(string? texto, params FaqEntry[] faq) => FaqMatcher.Match(faq, texto);

    // Normalização

    [Theory]
    [InlineData("Qual o horário de atendimento?", "qual o horario de atendimento")]
    [InlineData("  QUAL   o HORÁRIO,  de atendimento!!!  ", "qual o horario de atendimento")]
    [InlineData("qual o horario de atendimento", "qual o horario de atendimento")]
    [InlineData("Coleta de sangue (jejum) - 12h?", "coleta de sangue jejum 12h")]
    [InlineData("Olá!! 😀 Tudo bem?", "ola tudo bem")]
    [InlineData("AÇÃO, ÇÃO, Ñandú, über", "acao cao nandu uber")]
    [InlineData("exame\tde\nurina", "exame de urina")]
    [InlineData("Exame 2", "exame 2")]
    [InlineData("d'água / d’água", "d agua d agua")]
    public void A_normalizacao_tira_caixa_acento_pontuacao_e_espaco_repetido_e_mantem_letras_e_numeros(string entrada, string esperado) =>
        Assert.Equal(esperado, FaqMatcher.Normalize(entrada));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!?!")]
    [InlineData("😀😀")]
    [InlineData("\ud800")] // par substituto solto: Unicode malformado
    public void Texto_vazio_so_de_simbolos_ou_malformado_normaliza_para_vazio_e_nunca_corresponde_a_nada(string? entrada)
    {
        Assert.Equal(string.Empty, FaqMatcher.Normalize(entrada));
        Assert.Equal(FaqMatchKind.NoMatch, Buscar(entrada, Entrada("Qual o horário?")).Kind);
    }

    [Fact]
    public void Numeros_diferentes_nunca_se_confundem()
    {
        var faq = new[] { Entrada("Exame 2", "Resposta do 2"), Entrada("Exame 3", "Resposta do 3") };

        Assert.Equal("Resposta do 2", Buscar("exame 2", faq).Entry!.Answer);
        Assert.Equal("Resposta do 3", Buscar("EXAME 3?", faq).Entry!.Answer);
        Assert.Equal(FaqMatchKind.NoMatch, Buscar("exame 23", faq).Kind);
    }

    // Acertos

    [Theory]
    [InlineData("Qual o horário de atendimento?")]
    [InlineData("qual o horario de atendimento")]
    [InlineData("QUAL O HORÁRIO DE ATENDIMENTO!!!")]
    [InlineData("  qual   o horario de atendimento ")]
    public void A_mensagem_igual_a_pergunta_depois_de_normalizada_e_resposta_direta(string mensagem)
    {
        var resultado = Buscar(mensagem, Entrada("Qual o horário de atendimento?", "De segunda a sexta, das 7h às 17h."));

        Assert.Equal(FaqMatchKind.Direct, resultado.Kind);
        Assert.Equal("De segunda a sexta, das 7h às 17h.", resultado.Entry!.Answer);
    }

    [Fact]
    public void O_alias_cadastrado_e_igual_a_pergunta_para_a_busca()
    {
        var faq = new[] { Entrada("Qual o horário de atendimento?", "Das 7h às 17h.", true, "horário de funcionamento", "Que horas vocês abrem?") };

        Assert.Equal(FaqMatchKind.Direct, Buscar("Horario de funcionamento", faq).Kind);
        Assert.Equal(FaqMatchKind.Direct, Buscar("que horas voces abrem", faq).Kind);
        Assert.Equal("Das 7h às 17h.", Buscar("QUE HORAS VOCÊS ABREM???", faq).Entry!.Answer);
    }

    [Fact]
    public void Entre_varias_entradas_responde_a_unica_que_corresponde()
    {
        var faq = new[]
        {
            Entrada("Qual o horário?", "A"),
            Entrada("Onde fica a unidade?", "B"),
            Entrada("Aceitam convênio?", "C"),
        };

        Assert.Equal("B", Buscar("onde fica a unidade", faq).Entry!.Answer);
    }

    [Fact]
    public void A_pergunta_igual_a_um_alias_da_mesma_entrada_conta_uma_vez_e_nao_e_ambiguidade()
    {
        var faq = new[] { Entrada("Qual o horário?", "A", true, "qual o horario", "QUAL O HORÁRIO!") };

        var resultado = Buscar("Qual o horário?", faq);

        Assert.Equal(FaqMatchKind.Direct, resultado.Kind);
    }

    // Recusas: a regra é igualdade, nada de aproximação

    [Theory]
    [InlineData("qual o horario de atendimeto")] // erro de digitação
    [InlineData("qual o horario de atendimento de sabado")] // palavras a mais
    [InlineData("horario de atendimento")] // palavras a menos
    [InlineData("o horario de atendimento qual e")] // mesma bolsa de palavras, outra ordem
    [InlineData("qual o horario de atendimento? e o preco do exame?")] // a pergunta do FAQ mais outra coisa
    [InlineData("horario")]
    [InlineData("qual o horario")]
    public void Nada_de_aproximacao_percentual_de_termos_ou_erro_de_digitacao(string mensagem)
    {
        var resultado = Buscar(mensagem, Entrada("Qual o horário de atendimento?", "Das 7h às 17h."));

        Assert.Equal(FaqMatchKind.NoMatch, resultado.Kind);
        Assert.Null(resultado.Entry);
    }

    [Fact]
    public void Nenhuma_entrada_ou_faq_vazio_nao_corresponde()
    {
        Assert.Equal(FaqMatchKind.NoMatch, Buscar("qualquer coisa").Kind);
        Assert.Equal(FaqMatchKind.NoMatch, Buscar("qualquer coisa", Entrada("Outra pergunta?")).Kind);
    }

    [Fact]
    public void Texto_enorme_nao_e_igual_a_uma_pergunta_e_nem_e_normalizado()
    {
        var enorme = new string('a', 5000);

        Assert.Equal(FaqMatchKind.NoMatch, Buscar(enorme, Entrada(enorme[..300])).Kind);
        Assert.Equal(FaqMatchKind.NoMatch, Buscar(new string('?', 5000) + "horario", Entrada("horario")).Kind);
    }

    // Opt-in por entrada

    [Fact]
    public void Entrada_sem_resposta_direta_corresponde_mas_nao_e_direta()
    {
        var resultado = Buscar("Qual o horário?", Entrada("Qual o horário?", "A", direta: false));

        Assert.Equal(FaqMatchKind.NotAutoReply, resultado.Kind);
        Assert.Equal("A", resultado.Entry!.Answer);
    }

    [Fact]
    public void O_padrao_de_uma_entrada_e_sem_resposta_direta_inclusive_no_construtor_curto()
    {
        var faq = new[] { new FaqEntry("Qual o horário?", "A") };

        Assert.False(faq[0].AutoReply);
        Assert.Empty(faq[0].GetAliases());
        Assert.Equal(FaqMatchKind.NotAutoReply, Buscar("qual o horario", faq).Kind);
    }

    [Fact]
    public void Alias_so_vale_na_propria_entrada_e_a_resposta_direta_dela_e_a_que_sai()
    {
        var faq = new[]
        {
            Entrada("Horário?", "do horário", true, "que horas abrem"),
            Entrada("Endereço?", "do endereço", true, "onde ficam"),
        };

        Assert.Equal("do endereço", Buscar("onde ficam", faq).Entry!.Answer);
        Assert.Equal("do horário", Buscar("que horas abrem", faq).Entry!.Answer);
    }

    // Ambiguidade: duas entradas para a mesma mensagem nunca dão resposta direta

    [Fact]
    public void Duas_entradas_com_a_mesma_pergunta_sao_ambiguas()
    {
        var faq = new[] { Entrada("Qual o horário?", "A"), Entrada("qual o HORÁRIO", "B") };

        var resultado = Buscar("Qual o horário?", faq);

        Assert.Equal(FaqMatchKind.Ambiguous, resultado.Kind);
        Assert.Null(resultado.Entry);
    }

    [Fact]
    public void O_alias_de_uma_entrada_igual_a_pergunta_de_outra_e_ambiguo()
    {
        var faq = new[]
        {
            Entrada("Qual o horário?", "A"),
            Entrada("Onde fica?", "B", true, "qual o horario"),
        };

        Assert.Equal(FaqMatchKind.Ambiguous, Buscar("qual o horário", faq).Kind);
        Assert.Equal("B", Buscar("onde fica", faq).Entry!.Answer); // a outra pergunta da segunda entrada continua direta
    }

    [Fact]
    public void Dois_aliases_iguais_em_entradas_diferentes_sao_ambiguos()
    {
        var faq = new[]
        {
            Entrada("Horário?", "A", true, "abre quando"),
            Entrada("Endereço?", "B", true, "ABRE QUANDO?"),
        };

        Assert.Equal(FaqMatchKind.Ambiguous, Buscar("abre quando", faq).Kind);
    }

    [Fact]
    public void A_ambiguidade_conta_tambem_a_entrada_sem_resposta_direta()
    {
        // O tenant escreveu duas respostas para a mesma pergunta: mesmo que só uma ligue a resposta direta, não há resposta segura.
        var faq = new[] { Entrada("Qual o horário?", "A", direta: true), Entrada("Qual o horário?", "B", direta: false) };

        Assert.Equal(FaqMatchKind.Ambiguous, Buscar("qual o horario", faq).Kind);
    }

    [Fact]
    public void Duas_entradas_identicas_tambem_sao_ambiguas_o_tenant_resolve_a_duplicata()
    {
        var faq = new[] { Entrada("Qual o horário?", "A"), Entrada("Qual o horário?", "A") };

        Assert.Equal(FaqMatchKind.Ambiguous, Buscar("Qual o horário?", faq).Kind);
    }

    // Aliases limpos

    [Fact]
    public void Aliases_limpos_sao_aparados_sem_repeticao_e_na_ordem_do_cadastro()
    {
        var entrada = new FaqEntry("Pergunta?", "R", new[] { "  Que horas abrem?  ", "que horas ABREM", "onde ficam", "Onde ficam?" }, true);

        Assert.Equal(new[] { "Que horas abrem?", "onde ficam" }, entrada.GetCleanAliases());
    }
}
