using System.Text.Json;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Tests;

/// <summary>
/// A leitura da definição de um modelo NO FORMATO DA META (ADR 0027): nome, idioma, categoria, situação e a estrutura de parâmetros, e a recusa, com o motivo, do que o envio de modelos do MVP não sabe montar
/// (cabeçalho de mídia, copiar código, fluxo, autenticação). Dados sintéticos.
/// </summary>
public sealed class TemplateDefinitionParserTests
{
    private static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement.Clone();

    private static string Modelo(string componentes, string nome = "resultado_pronto", string categoria = "UTILITY", string situacao = "APPROVED", string extra = "") =>
        $$"""{"name":"{{nome}}","language":"pt_BR","category":"{{categoria}}","status":"{{situacao}}","id":"1234567890"{{extra}},"components":{{componentes}}}""";

    private static ParsedTemplate Ler(string componentes, string nome = "resultado_pronto", string categoria = "UTILITY", string situacao = "APPROVED", string extra = "") =>
        TemplateDefinitionParser.Parse(Json(Modelo(componentes, nome, categoria, situacao, extra)));

    private static string Recusa(string componentes, string categoria = "UTILITY", string situacao = "APPROVED") =>
        Assert.Throws<ChannelsValidationException>(() => Ler(componentes, categoria: categoria, situacao: situacao)).Message;

    [Fact]
    public void Modelo_completo_com_cabecalho_corpo_rodape_e_botoes_extrai_a_estrutura_de_parametros()
    {
        var modelo = Ler("""
            [
              {"type":"HEADER","format":"TEXT","text":"Resultado {{1}}","example":{"header_text":["Hemograma"]}},
              {"type":"BODY","text":"Olá {{1}}, o exame {{2}} está pronto.","example":{"body_text":[["Mauro","hemograma"]]}},
              {"type":"FOOTER","text":"Laboratório Exemplo"},
              {"type":"BUTTONS","buttons":[
                {"type":"QUICK_REPLY","text":"Obrigado"},
                {"type":"PHONE_NUMBER","text":"Ligar","phone_number":"+551130000000"},
                {"type":"URL","text":"Ver","url":"https://exemplo.com/r/{{1}}","example":["https://exemplo.com/r/abc"]}
              ]}
            ]
            """);

        Assert.Equal(("resultado_pronto", "pt_BR", TemplateCategory.Utility, TemplateStatus.Approved, "1234567890"), (modelo.Name, modelo.Language, modelo.Category, modelo.Status, modelo.MetaTemplateId));
        Assert.Equal("Resultado {{1}}", modelo.HeaderText);
        Assert.Equal("Olá {{1}}, o exame {{2}} está pronto.", modelo.BodyText);
        Assert.Equal("Laboratório Exemplo", modelo.FooterText);
        Assert.Equal(new[] { "header.1", "body.1", "body.2", "button.2" }, modelo.Parameters.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "Hemograma", "Mauro", "hemograma", "abc" }, modelo.Parameters.Select(p => p.Example).ToArray());
        Assert.Equal(new[] { 60, 1024, 1024, 512 }, modelo.Parameters.Select(p => p.MaxLength).ToArray());
    }

    [Fact]
    public void O_indice_do_botao_de_link_e_a_posicao_dele_entre_os_botoes()
    {
        var modelo = Ler("""
            [
              {"type":"BODY","text":"Oi"},
              {"type":"BUTTONS","buttons":[{"type":"URL","text":"Um","url":"https://a.com/{{1}}"},{"type":"QUICK_REPLY","text":"Dois"},{"type":"URL","text":"Três","url":"https://b.com/{{1}}"}]}
            ]
            """);

        Assert.Equal(new[] { "button.0", "button.2" }, modelo.Parameters.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { 0, 2 }, modelo.Parameters.Select(p => p.Index).ToArray());
    }

    [Fact]
    public void Corpo_sem_variaveis_nao_tem_parametros()
    {
        var modelo = Ler("""[{"type":"BODY","text":"Seu resultado está disponível no portal."}]""");

        Assert.Empty(modelo.Parameters);
    }

    [Fact]
    public void Parametros_nomeados_viram_chaves_com_o_nome()
    {
        var modelo = Ler(
            """
            [{"type":"BODY","text":"Olá {{nome}}, o exame {{exame}} está pronto.","example":{"body_text_named_params":[{"param_name":"nome","example":"Mauro"},{"param_name":"exame","example":"hemograma"}]}}]
            """,
            extra: ",\"parameter_format\":\"NAMED\"");

        Assert.Equal(new[] { "body.nome", "body.exame" }, modelo.Parameters.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "nome", "exame" }, modelo.Parameters.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "Mauro", "hemograma" }, modelo.Parameters.Select(p => p.Example).ToArray());
        Assert.Equal(new[] { "nome", "exame" }, modelo.Parameters.Select(p => p.Label).ToArray());
    }

    [Fact]
    public void A_mesma_variavel_repetida_vale_um_parametro_so()
    {
        var modelo = Ler("""[{"type":"BODY","text":"{{1}}, olá! {{1}}, tudo bem? Exame {{2}}."}]""");

        Assert.Equal(new[] { "body.1", "body.2" }, modelo.Parameters.Select(p => p.Key).ToArray());
    }

    [Theory]
    [InlineData("PENDING", TemplateStatus.Pending)]
    [InlineData("IN_APPEAL", TemplateStatus.Pending)]
    [InlineData("REJECTED", TemplateStatus.Rejected)]
    [InlineData("PAUSED", TemplateStatus.Paused)]
    [InlineData("FLAGGED", TemplateStatus.Paused)]
    [InlineData("DISABLED", TemplateStatus.Disabled)]
    [InlineData("DELETED", TemplateStatus.Disabled)]
    [InlineData("approved", TemplateStatus.Approved)]
    public void A_situacao_da_Meta_vira_a_situacao_do_cadastro(string da_meta, TemplateStatus esperado)
    {
        Assert.Equal(esperado, Ler("""[{"type":"BODY","text":"Oi"}]""", situacao: da_meta).Status);
    }

    [Fact]
    public void So_o_modelo_aprovado_e_enviavel()
    {
        var aprovado = ChannelTemplate.Create(
            Fabrica.NovoTenant(), Guid.NewGuid(), "a", "pt_BR", TemplateCategory.Utility, TemplateStatus.Approved, null, null, "Oi", null, Array.Empty<TemplateParameterSpec>(), Fabrica.Agora);

        Assert.True(aprovado.IsSendable);
        foreach (var situacao in new[] { TemplateStatus.Pending, TemplateStatus.Rejected, TemplateStatus.Paused, TemplateStatus.Disabled })
        {
            aprovado.SetStatus(situacao, Fabrica.Agora);
            Assert.False(aprovado.IsSendable);
        }
    }

    [Theory]
    [InlineData("MARKETING", TemplateCategory.Marketing)]
    [InlineData("UTILITY", TemplateCategory.Utility)]
    public void As_categorias_suportadas(string categoria, TemplateCategory esperada)
    {
        Assert.Equal(esperada, Ler("""[{"type":"BODY","text":"Oi"}]""", categoria: categoria).Category);
    }

    [Fact]
    public void Autenticacao_cabecalho_de_midia_e_botao_de_copiar_codigo_sao_recusados_com_o_motivo()
    {
        Assert.Contains("autenticação", Recusa("""[{"type":"BODY","text":"Seu código {{1}}"}]""", categoria: "AUTHENTICATION"));
        Assert.Contains("IMAGE", Recusa("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Oi"}]"""));
        Assert.Contains("DOCUMENT", Recusa("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Oi"}]"""));
        Assert.Contains("COPY_CODE", Recusa("""[{"type":"BODY","text":"Oi"},{"type":"BUTTONS","buttons":[{"type":"COPY_CODE","example":"123"}]}]"""));
        Assert.Contains("FLOW", Recusa("""[{"type":"BODY","text":"Oi"},{"type":"BUTTONS","buttons":[{"type":"FLOW","text":"Abrir","flow_id":"1"}]}]"""));
        Assert.Contains("CAROUSEL", Recusa("""[{"type":"BODY","text":"Oi"},{"type":"CAROUSEL","cards":[]}]"""));
    }

    [Fact]
    public void Variaveis_mal_formadas_sao_recusadas()
    {
        Assert.Contains("numeradas de 1", Recusa("""[{"type":"BODY","text":"{{1}} e {{3}}"}]"""));
        Assert.Contains("mistura", Recusa("""[{"type":"BODY","text":"{{1}} e {{nome}}"}]"""));
        Assert.Contains("rodapé", Recusa("""[{"type":"BODY","text":"Oi"},{"type":"FOOTER","text":"Rodapé {{1}}"}]"""));
        Assert.Contains("mais de uma variável", Recusa("""[{"type":"HEADER","format":"TEXT","text":"{{1}} {{2}}"},{"type":"BODY","text":"Oi"}]"""));
        Assert.Contains("botão de link", Recusa("""[{"type":"BODY","text":"Oi"},{"type":"BUTTONS","buttons":[{"type":"URL","text":"Ver","url":"https://a.com/{{1}}/x"}]}]"""));
    }

    [Fact]
    public void Faltas_de_nome_idioma_corpo_e_categoria_sao_recusadas_sem_lancar_excecao_de_json()
    {
        Assert.Contains("corpo", Recusa("""[{"type":"HEADER","format":"TEXT","text":"Só cabeçalho"}]"""));
        Assert.Throws<ChannelsValidationException>(() => TemplateDefinitionParser.Parse(Json("""{"language":"pt_BR","category":"UTILITY","status":"APPROVED","components":[{"type":"BODY","text":"Oi"}]}""")));
        Assert.Throws<ChannelsValidationException>(() => TemplateDefinitionParser.Parse(Json("""{"name":"a","category":"UTILITY","status":"APPROVED","components":[{"type":"BODY","text":"Oi"}]}""")));
        Assert.Throws<ChannelsValidationException>(() => TemplateDefinitionParser.Parse(Json("""{"name":"a","language":"pt_BR","status":"APPROVED","components":[{"type":"BODY","text":"Oi"}]}""")));
        Assert.Throws<ChannelsValidationException>(() => TemplateDefinitionParser.Parse(Json("""{"name":"a","language":"pt_BR","category":"UTILITY","components":[{"type":"BODY","text":"Oi"}]}""")));
        Assert.Throws<ChannelsValidationException>(() => TemplateDefinitionParser.Parse(Json("""{"name":"a","language":"pt_BR","category":"UTILITY","status":"APPROVED"}""")));
        Assert.Throws<ChannelsValidationException>(() => TemplateDefinitionParser.Parse(Json("""[1,2,3]""")));
        Assert.Throws<ChannelsValidationException>(() => TemplateDefinitionParser.Parse(Json("""{"name":"a","language":"pt_BR","category":"UTILITY","status":"APPROVED","components":["texto"]}""")));
    }

    [Fact]
    public void Nome_fora_do_formato_da_Meta_e_recusado()
    {
        var ex = Assert.Throws<ChannelsValidationException>(() => Ler("""[{"type":"BODY","text":"Oi"}]""", nome: "Nome Invalido"));

        Assert.Contains("minúsculas", ex.Message);
    }

    [Fact]
    public void A_mensagem_de_recusa_nao_leva_caractere_de_controle_nem_texto_longo_do_json()
    {
        var ex = Assert.Throws<ChannelsValidationException>(() => Ler("""[{"type":"BODY","text":"Oi"}]""", nome: "x\\nforjado" + new string('y', 500)));

        Assert.DoesNotContain('\n', ex.Message);
        Assert.True(ex.Message.Length < 400);
    }

    [Fact]
    public void Mais_de_28_parametros_e_recusado()
    {
        var corpo = string.Join(" ", Enumerable.Range(1, 29).Select(i => "{{" + i + "}}"));

        Assert.Contains("parâmetros", Recusa("""[{"type":"BODY","text":"CORPO"}]""".Replace("CORPO", corpo)));
    }
}
