using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Testing;

namespace IaBimeta.AiGateway.Tests;

/// <summary>O esquema da saída estruturada precisa ser o mínimo comum aos provedores (ADR 0017, seção C.5): um esquema que funcionaria num provedor e não no outro é recusado.</summary>
public class StructuredOutputRulesTests
{
    private const string EsquemaValido = """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["reply", "handoff"] },
            "text": { "type": "string" },
            "reason": { "type": "string", "enum": ["none", "price", "schedule", "other"] }
          },
          "required": ["action", "text", "reason"],
          "additionalProperties": false
        }
        """;

    private static string? Problema(string json, string nome = "decisao") => AiStructuredOutputRules.FindProblem(new AiStructuredOutput(nome, json));

    [Fact]
    public void A_decisao_reply_ou_handoff_do_ADR_obedece_ao_minimo_comum()
    {
        Assert.Null(Problema(EsquemaValido));
    }

    [Fact]
    public void Sem_esquema_nome_invalido_ou_json_invalido_sao_recusados()
    {
        Assert.NotNull(AiStructuredOutputRules.FindProblem(null));
        Assert.NotNull(Problema(EsquemaValido, nome: ""));
        Assert.NotNull(Problema(EsquemaValido, nome: "nome com espaço"));
        Assert.NotNull(Problema(EsquemaValido, nome: new string('n', 65)));
        Assert.NotNull(Problema("{ isto não é json"));
        Assert.NotNull(Problema(""));
        Assert.NotNull(Problema("   "));
        Assert.NotNull(Problema(new string(' ', AiStructuredOutputRules.SchemaMaxLength + 1)));
    }

    [Fact]
    public void A_raiz_precisa_ser_objeto_sem_anyOf_e_com_additionalProperties_false()
    {
        Assert.NotNull(Problema("""{ "type": "array", "items": { "type": "string" } }"""));
        Assert.NotNull(Problema("""[ 1, 2 ]"""));
        Assert.NotNull(Problema("""{ "properties": { "a": { "type": "string" } }, "required": ["a"], "additionalProperties": false }"""));
        Assert.NotNull(Problema("""{ "type": "object", "properties": { "a": { "type": "string" } }, "required": ["a"] }"""));
        Assert.NotNull(Problema("""{ "type": "object", "properties": { "a": { "type": "string" } }, "required": ["a"], "additionalProperties": true }"""));
        Assert.NotNull(Problema("""{ "type": "object", "anyOf": [ { "properties": {} }, { "properties": {} } ], "properties": {}, "required": [], "additionalProperties": false }"""));
        Assert.NotNull(Problema("""{ "type": "object", "additionalProperties": false, "required": [] }"""));
    }

    [Fact]
    public void Todo_campo_e_obrigatorio_e_nenhum_pode_ser_opcional()
    {
        Assert.NotNull(Problema("""
            { "type": "object", "properties": { "a": { "type": "string" }, "b": { "type": "string" } }, "required": ["a"], "additionalProperties": false }
            """));
        Assert.NotNull(Problema("""
            { "type": "object", "properties": { "a": { "type": "string" } }, "additionalProperties": false }
            """));
        Assert.Null(Problema("""
            { "type": "object", "properties": {}, "required": [], "additionalProperties": false }
            """));
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("maximum")]
    [InlineData("minLength")]
    [InlineData("maxLength")]
    public void Minimo_maximo_e_tamanho_sao_recusados_em_qualquer_nivel(string palavraChave)
    {
        var json = $$"""
            { "type": "object", "properties": { "n": { "type": "integer", "{{palavraChave}}": 1 }, "m": { "type": "string" } }, "required": ["n", "m"], "additionalProperties": false }
            """;

        Assert.NotNull(Problema(json));

        var profundo = $$"""
            { "type": "object", "properties": { "o": { "type": "object", "properties": { "x": { "type": "array", "items": { "type": "integer", "{{palavraChave}}": 0 } } }, "required": ["x"], "additionalProperties": false } }, "required": ["o"], "additionalProperties": false }
            """;
        Assert.NotNull(Problema(profundo));
    }

    [Fact]
    public void Um_campo_CHAMADO_minimum_nao_e_um_limite()
    {
        // As chaves de "properties" são nomes de campo, não palavras-chave do esquema.
        Assert.Null(Problema("""
            { "type": "object", "properties": { "minimum": { "type": "string" }, "maxLength": { "type": "string" } }, "required": ["minimum", "maxLength"], "additionalProperties": false }
            """));
    }
}

/// <summary>A forma do pedido de geração é conferida antes de qualquer acesso a configuração, credencial ou provedor.</summary>
public class GenerationRequestRulesTests
{
    private static AiGenerationRequest Valido() => GatewayHarness.Request();

    private static void Recusa(AiGenerationRequest pedido) => Assert.Throws<AiGatewayValidationException>(() => AiGenerationRequestRules.Validate(pedido));

    [Fact]
    public void Um_pedido_de_texto_valido_passa()
    {
        AiGenerationRequestRules.Validate(Valido());
    }

    [Fact]
    public void Uma_conversa_com_historico_terminando_no_usuario_passa()
    {
        var pedido = Valido() with
        {
            Messages = new[]
            {
                new AiMessage(AiMessageRole.User, "oi"), new AiMessage(AiMessageRole.Assistant, "olá"), new AiMessage(AiMessageRole.User, "preciso de ajuda"),
            },
        };

        AiGenerationRequestRules.Validate(pedido);
    }

    [Fact]
    public void Correlacao_instrucao_e_mensagens_sao_obrigatorias()
    {
        Recusa(Valido() with { CorrelationId = "" });
        Recusa(Valido() with { CorrelationId = new string('c', AiGenerationRequestRules.CorrelationIdMaxLength + 1) });
        Recusa(Valido() with { SystemInstruction = "  " });
        Recusa(Valido() with { Messages = Array.Empty<AiMessage>() });
        Recusa(Valido() with { Messages = null! });
    }

    [Fact]
    public void Cada_mensagem_precisa_de_papel_valido_e_texto_e_a_ultima_e_do_usuario()
    {
        Recusa(Valido() with { Messages = new[] { new AiMessage(AiMessageRole.User, " ") } });
        Recusa(Valido() with { Messages = new[] { new AiMessage((AiMessageRole)9, "oi") } });
        Recusa(Valido() with { Messages = new AiMessage[] { null! } });
        Recusa(Valido() with { Messages = new[] { new AiMessage(AiMessageRole.User, "oi"), new AiMessage(AiMessageRole.Assistant, "olá") } });
    }

    [Fact]
    public void Teto_de_saida_e_tempo_limite_precisam_ser_positivos()
    {
        Recusa(Valido() with { MaxOutputTokens = 0 });
        Recusa(Valido() with { MaxOutputTokens = -1 });
        Recusa(Valido() with { Timeout = TimeSpan.Zero });
        Recusa(Valido() with { Timeout = TimeSpan.FromSeconds(-1) });
    }

    [Fact]
    public void O_esquema_so_vale_com_a_saida_estruturada_e_ela_exige_o_esquema()
    {
        var esquema = new AiStructuredOutput("decisao", """{ "type": "object", "properties": {}, "required": [], "additionalProperties": false }""");

        AiGenerationRequestRules.Validate(Valido() with { OutputMode = AiOutputMode.StructuredJson, StructuredOutput = esquema });
        Recusa(Valido() with { OutputMode = AiOutputMode.StructuredJson, StructuredOutput = null });
        Recusa(Valido() with { OutputMode = AiOutputMode.Text, StructuredOutput = esquema });
        Recusa(Valido() with { OutputMode = (AiOutputMode)9 });
        Recusa(Valido() with { OutputMode = AiOutputMode.StructuredJson, StructuredOutput = new AiStructuredOutput("decisao", """{ "type": "array" }""") });
    }

    [Fact]
    public void Pedido_nulo_e_erro_de_argumento()
    {
        Assert.Throws<ArgumentNullException>(() => AiGenerationRequestRules.Validate(null!));
    }
}
