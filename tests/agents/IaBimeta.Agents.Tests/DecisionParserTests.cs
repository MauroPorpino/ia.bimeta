using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A validação da saída do modelo contra a lista fechada de ações (ADR 0017, C.5): o modelo sugere, o Agents valida e falha fechado. Saída inválida nunca executa nada e o código de erro é curto e
/// estável, nunca o conteúdo da saída.
/// </summary>
public sealed class DecisionParserTests
{
    private static string Resposta(string texto) => "{\"action\":\"reply\",\"reply_text\":" + System.Text.Json.JsonSerializer.Serialize(texto) + ",\"handoff_reason\":\"none\"}";

    private static string Transferir(string motivo) => "{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"" + motivo + "\"}";

    public static IEnumerable<object[]> Motivos() => HandoffReasons.Selectable.Select(m => new object[] { m });

    // Válidas

    [Fact]
    public void Reply_valido_devolve_a_decisao_com_o_texto_aparado()
    {
        var ok = DecisionParser.TryParse(Resposta("  Funcionamos das 7h às 17h.  "), out var decisao, out var codigo);

        Assert.True(ok);
        Assert.Null(codigo);
        Assert.Equal(new AgentDecision(AgentAction.Reply, "Funcionamos das 7h às 17h.", null), decisao);
    }

    [Theory]
    [MemberData(nameof(Motivos))]
    public void Handoff_valido_aceita_cada_motivo_da_lista_fechada(string motivo)
    {
        var ok = DecisionParser.TryParse(Transferir(motivo), out var decisao, out var codigo);

        Assert.True(ok);
        Assert.Null(codigo);
        Assert.Equal(new AgentDecision(AgentAction.Handoff, null, motivo), decisao);
    }

    [Fact]
    public void Handoff_ignora_o_texto_de_resposta_que_vier_junto()
    {
        var saida = "{\"action\":\"handoff\",\"reply_text\":\"texto que não vale\",\"handoff_reason\":\"price\"}";

        Assert.True(DecisionParser.TryParse(saida, out var decisao, out _));
        Assert.Null(decisao!.ReplyText);
    }

    [Fact]
    public void Reply_ignora_o_motivo_de_handoff_que_vier_junto()
    {
        var saida = "{\"action\":\"reply\",\"reply_text\":\"Oi!\",\"handoff_reason\":\"price\"}";

        Assert.True(DecisionParser.TryParse(saida, out var decisao, out _));
        Assert.Equal(new AgentDecision(AgentAction.Reply, "Oi!", null), decisao);
    }

    [Fact]
    public void A_ordem_dos_campos_nao_importa()
    {
        var saida = "{\"handoff_reason\":\"none\",\"reply_text\":\"Oi\",\"action\":\"reply\"}";

        Assert.True(DecisionParser.TryParse(saida, out var decisao, out _));
        Assert.Equal("Oi", decisao!.ReplyText);
    }

    [Fact]
    public void O_texto_no_limite_exato_e_aceito()
    {
        var texto = new string('a', DecisionParser.ReplyMaxLength);

        Assert.True(DecisionParser.TryParse(Resposta(texto), out var decisao, out _));
        Assert.Equal(DecisionParser.ReplyMaxLength, decisao!.ReplyText!.Length);
    }

    // Inválidas, com código estável

    [Theory]
    [InlineData(null, "empty-output")]
    [InlineData("", "empty-output")]
    [InlineData("   \n ", "empty-output")]
    [InlineData("{nao e json", "invalid-json")]
    [InlineData("Claro! Segue a resposta: {\"action\":\"reply\"}", "invalid-json")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"a\",\"handoff_reason\":\"none\",}", "invalid-json")]
    [InlineData("{\"action\":\"reply\" /* x */,\"reply_text\":\"a\",\"handoff_reason\":\"none\"}", "invalid-json")]
    [InlineData("[1,2]", "not-an-object")]
    [InlineData("\"reply\"", "not-an-object")]
    [InlineData("42", "not-an-object")]
    [InlineData("null", "not-an-object")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"oi\"}", "invalid-shape")]
    [InlineData("{}", "invalid-shape")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"oi\",\"handoff_reason\":\"none\",\"extra\":\"x\"}", "invalid-shape")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":1,\"handoff_reason\":\"none\"}", "invalid-shape")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":null,\"handoff_reason\":\"none\"}", "invalid-shape")]
    [InlineData("{\"action\":true,\"reply_text\":\"oi\",\"handoff_reason\":\"none\"}", "invalid-shape")]
    [InlineData("{\"action\":\"reply\",\"action\":\"reply\",\"reply_text\":\"oi\",\"handoff_reason\":\"none\"}", "invalid-shape")]
    [InlineData("{\"action\":\"delete\",\"reply_text\":\"oi\",\"handoff_reason\":\"none\"}", "unknown-action")]
    [InlineData("{\"action\":\"Reply\",\"reply_text\":\"oi\",\"handoff_reason\":\"none\"}", "unknown-action")]
    [InlineData("{\"action\":\"\",\"reply_text\":\"oi\",\"handoff_reason\":\"none\"}", "unknown-action")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"\",\"handoff_reason\":\"none\"}", "empty-reply")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"   \\n \",\"handoff_reason\":\"none\"}", "empty-reply")]
    [InlineData("{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"none\"}", "invalid-handoff-reason")]
    [InlineData("{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"refund\"}", "invalid-handoff-reason")]
    [InlineData("{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"\"}", "invalid-handoff-reason")]
    [InlineData("{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"Price\"}", "invalid-handoff-reason")]
    public void Saida_invalida_nao_executa_nada_e_devolve_o_codigo_estavel(string? saida, string codigoEsperado)
    {
        var ok = DecisionParser.TryParse(saida, out var decisao, out var codigo);

        Assert.False(ok);
        Assert.Null(decisao);
        Assert.Equal(codigoEsperado, codigo);
    }

    [Fact]
    public void Texto_acima_do_teto_e_recusado()
    {
        var texto = new string('a', DecisionParser.ReplyMaxLength + 1);

        Assert.False(DecisionParser.TryParse(Resposta(texto), out var decisao, out var codigo));

        Assert.Null(decisao);
        Assert.Equal("reply-too-long", codigo);
    }

    [Fact]
    public void Aninhamento_alem_do_limite_de_profundidade_e_JSON_invalido()
    {
        var fundo = string.Concat(Enumerable.Repeat("{\"a\":", 12)) + "1" + new string('}', 12);

        Assert.False(DecisionParser.TryParse(fundo, out _, out var codigo));
        Assert.Equal("invalid-json", codigo);
    }

    [Theory]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"CANARIO-SECRETO-1\",\"handoff_reason\":\"none\",\"extra\":1}")]
    [InlineData("{\"action\":\"CANARIO-SECRETO-2\",\"reply_text\":\"x\",\"handoff_reason\":\"none\"}")]
    [InlineData("{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"CANARIO-SECRETO-3\"}")]
    [InlineData("CANARIO-SECRETO-4 não é json")]
    public void O_codigo_de_erro_nunca_repete_o_conteudo_da_saida(string saida)
    {
        Assert.False(DecisionParser.TryParse(saida, out _, out var codigo));

        Assert.NotNull(codigo);
        Assert.DoesNotContain("CANARIO", codigo);
        Assert.Matches("^[a-z]+(-[a-z]+)*$", codigo);
    }

    // Os caracteres do texto

    [Fact]
    public void Par_substituto_valido_como_o_emoji_e_preservado()
    {
        var literal = Resposta("Tudo certo 😀 até logo");
        var escapado = "{\"action\":\"reply\",\"reply_text\":\"Tudo certo \\ud83d\\ude00 até logo\",\"handoff_reason\":\"none\"}";

        Assert.True(DecisionParser.TryParse(literal, out var a, out _));
        Assert.True(DecisionParser.TryParse(escapado, out var b, out _));

        Assert.Equal("Tudo certo 😀 até logo", a!.ReplyText);
        Assert.Equal(a.ReplyText, b!.ReplyText);
    }

    [Fact]
    public void Substituto_solto_no_JSON_nunca_lanca_a_saida_e_validada_e_o_texto_fica_valido()
    {
        // O modelo pode emitir um escape de substituto sem par. O parser não pode vazar exceção: ou recusa com código estável, ou devolve o texto já saneado.
        var saida = "{\"action\":\"reply\",\"reply_text\":\"oi \\ud83d tchau\",\"handoff_reason\":\"none\"}";

        var ok = DecisionParser.TryParse(saida, out var decisao, out var codigo);

        if (ok)
        {
            Assert.Equal("oi � tchau", decisao!.ReplyText);
        }
        else
        {
            Assert.Equal("invalid-json", codigo);
        }
    }

    // Os casos ficam numa lista e o teste escolhe por índice: o xUnit estraga um substituto UTF-16 solto passado como dado de teoria (vira U+FFFD antes de chegar ao teste).
    private static readonly (string Entrada, string Esperado)[] CasosDeSaneamento =
    {
        ("sem problema", "sem problema"),
        ("", ""),
        ("a\uD83Db", "a�b"),
        ("a\uDE00b", "a�b"),
        ("\uD83D", "�"),
        ("\uDE00", "�"),
        ("\uD83D😀", "�😀"),
        ("😀", "😀"),
        ("a😀b\uD83Dc", "a😀b�c"),
    };

    public static IEnumerable<object[]> IndicesDeSaneamento() => Enumerable.Range(0, CasosDeSaneamento.Length).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(IndicesDeSaneamento))]
    public void Sanitize_troca_so_o_substituto_sem_par_e_preserva_o_par_valido(int indice)
    {
        var (entrada, esperado) = CasosDeSaneamento[indice];

        Assert.Equal(esperado, DecisionParser.Sanitize(entrada));
    }

    [Fact]
    public void Sanitize_devolve_a_mesma_instancia_quando_nao_ha_o_que_corrigir()
    {
        var texto = "texto normal com acentuação";

        Assert.Same(texto, DecisionParser.Sanitize(texto));
    }

    [Fact]
    public void O_texto_com_quebras_de_linha_e_aspas_e_preservado_por_dentro()
    {
        var texto = "Linha 1\nLinha 2 com \"aspas\" e \\ barra";

        Assert.True(DecisionParser.TryParse(Resposta(texto), out var decisao, out _));
        Assert.Equal(texto, decisao!.ReplyText);
    }
}
