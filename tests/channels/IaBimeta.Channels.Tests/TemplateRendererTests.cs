using System.Text.Json;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Tests;

/// <summary>
/// A validação dos parâmetros que o atendente preencheu, a prévia do texto que o cliente lerá e o bloco de canal que acompanha a mensagem até o Sender (ADR 0027). Dados sintéticos.
/// </summary>
public sealed class TemplateRendererTests
{
    private const string Completo = """
        {"name":"resultado_pronto","language":"pt_BR","category":"UTILITY","status":"APPROVED","id":"1","components":[
          {"type":"HEADER","format":"TEXT","text":"Resultado {{1}}"},
          {"type":"BODY","text":"Olá {{1}}, o exame {{2}} está pronto."},
          {"type":"FOOTER","text":"Laboratório Exemplo"},
          {"type":"BUTTONS","buttons":[{"type":"URL","text":"Ver","url":"https://exemplo.com/r/{{1}}"}]}
        ]}
        """;

    private static ChannelTemplate Modelo(string json = Completo, TemplateStatus? situacao = null)
    {
        var lido = TemplateDefinitionParser.Parse(JsonDocument.Parse(json).RootElement);
        var modelo = ChannelTemplate.Create(
            Fabrica.NovoTenant(), Guid.NewGuid(), lido.Name, lido.Language, lido.Category, lido.Status, lido.MetaTemplateId, lido.HeaderText, lido.BodyText, lido.FooterText, lido.Parameters, Fabrica.Agora);
        if (situacao is { } s)
        {
            modelo.SetStatus(s, Fabrica.Agora);
        }

        return modelo;
    }

    private static Dictionary<string, string> Valores() => new()
    {
        ["header.1"] = "Hemograma",
        ["body.1"] = "Mauro",
        ["body.2"] = "hemograma",
        ["button.0"] = "abc123",
    };

    [Fact]
    public void Parametros_validos_geram_a_previa_do_que_o_cliente_le_e_o_bloco_de_canal_do_Sender()
    {
        var modelo = Modelo();

        var preparo = TemplateRenderer.Prepare(modelo, Valores());

        Assert.True(preparo.IsReady);
        Assert.Equal("Resultado Hemograma\nOlá Mauro, o exame hemograma está pronto.\nLaboratório Exemplo", preparo.PreviewText);
        Assert.Equal(1, preparo.ChannelDataVersion);
        var esperado = new Dictionary<string, string>
        {
            ["tpl.id"] = modelo.Id.ToString("D"),
            ["tpl.name"] = "resultado_pronto",
            ["tpl.lang"] = "pt_BR",
            ["tpl.p.header.1"] = "Hemograma",
            ["tpl.p.body.1"] = "Mauro",
            ["tpl.p.body.2"] = "hemograma",
            ["tpl.p.button.0"] = "abc123",
        };
        Assert.Equal(esperado.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), preparo.ChannelData!.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void O_bloco_de_canal_cabe_nos_limites_do_Conversations()
    {
        var preparo = TemplateRenderer.Prepare(Modelo(), Valores());

        var dados = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(preparo.ChannelData);
        Assert.True(dados.Count <= 32);
        Assert.All(dados, par =>
        {
            Assert.Matches("^[a-z0-9][a-z0-9_.-]*$", par.Key);
            Assert.True(par.Key.Length <= 64 && par.Value.Length <= 1024);
        });
    }

    [Fact]
    public void Os_valores_sao_aparados_e_o_corpo_do_modelo_nao_muda()
    {
        var valores = Valores();
        valores["body.1"] = "  Mauro  ";

        var preparo = TemplateRenderer.Prepare(Modelo(), valores);

        Assert.Equal("Mauro", preparo.ChannelData!["tpl.p.body.1"]);
        Assert.Contains("Olá Mauro,", preparo.PreviewText);
    }

    [Fact]
    public void Parametros_nomeados_entram_na_previa_pelo_nome()
    {
        var modelo = Modelo("""
            {"name":"nomeado","language":"pt_BR","category":"UTILITY","status":"APPROVED","parameter_format":"NAMED","components":[
              {"type":"BODY","text":"Olá {{nome}}, o exame {{exame}} está pronto."}]}
            """);

        var preparo = TemplateRenderer.Prepare(modelo, new Dictionary<string, string> { ["body.nome"] = "Mauro", ["body.exame"] = "hemograma" });

        Assert.True(preparo.IsReady);
        Assert.Equal("Olá Mauro, o exame hemograma está pronto.", preparo.PreviewText);
    }

    [Fact]
    public void Modelo_sem_parametros_nao_exige_nenhum()
    {
        var modelo = Modelo("""{"name":"simples","language":"pt_BR","category":"UTILITY","status":"APPROVED","components":[{"type":"BODY","text":"Seu resultado está no portal."}]}""");

        var preparo = TemplateRenderer.Prepare(modelo, new Dictionary<string, string>());

        Assert.True(preparo.IsReady);
        Assert.Equal("Seu resultado está no portal.", preparo.PreviewText);
    }

    [Fact]
    public void Todos_os_problemas_voltam_juntos_por_chave_e_codigo()
    {
        var valores = new Dictionary<string, string>
        {
            ["body.1"] = "   ",                 // vazio
            ["body.2"] = "linha 1\nlinha 2",     // quebra de linha
            ["header.1"] = new string('x', 61),  // acima de 60
            ["intruso"] = "x",                   // não existe
            // button.0 faltando
        };

        var preparo = TemplateRenderer.Prepare(Modelo(), valores);

        Assert.False(preparo.IsReady);
        Assert.Null(preparo.ChannelData);
        Assert.Null(preparo.PreviewText);
        Assert.Null(preparo.FailureCode);
        Assert.Equal(
            new[] { ("intruso", "unknown"), ("header.1", "too-long"), ("body.1", "required"), ("body.2", "invalid-characters"), ("button.0", "required") },
            preparo.Errors.Select(e => (e.Key, e.Code)).ToArray());
    }

    [Theory]
    [InlineData("com\ttabulacao")]
    [InlineData("com    quatro espacos")]
    [InlineData("com\rretorno")]
    [InlineData("com\u0007sino")]
    public void Valor_com_quebra_tabulacao_controle_ou_mais_de_3_espacos_seguidos_e_recusado(string valor)
    {
        var valores = Valores();
        valores["body.1"] = valor;

        var preparo = TemplateRenderer.Prepare(Modelo(), valores);

        Assert.Equal(("body.1", "invalid-characters"), Assert.Single(preparo.Errors).Let(e => (e.Key, e.Code)));
    }

    [Fact]
    public void Tres_espacos_seguidos_ainda_valem()
    {
        var valores = Valores();
        valores["body.1"] = "Mauro   Porpino";

        Assert.True(TemplateRenderer.Prepare(Modelo(), valores).IsReady);
    }

    [Fact]
    public void A_mensagem_de_erro_nunca_repete_o_valor_digitado()
    {
        var valores = Valores();
        valores["body.1"] = "SEGREDO-DO-CLIENTE\nforjado";
        valores["intruso-SEGREDO"] = "x";

        var preparo = TemplateRenderer.Prepare(Modelo(), valores);

        Assert.DoesNotContain(preparo.Errors, e => e.Message.Contains("SEGREDO-DO-CLIENTE"));
    }

    [Fact]
    public void O_texto_final_ou_o_bloco_grande_demais_e_recusado_como_conjunto()
    {
        // 28 parâmetros de 400 caracteres: cada valor cabe, mas o bloco de canal passa de 8192 caracteres.
        var corpo = string.Join(" ", Enumerable.Range(1, 28).Select(i => "{{" + i + "}}"));
        var modelo = Modelo("""{"name":"grande","language":"pt_BR","category":"UTILITY","status":"APPROVED","components":[{"type":"BODY","text":"CORPO"}]}""".Replace("CORPO", corpo));
        var valores = Enumerable.Range(1, 28).ToDictionary(i => $"body.{i}", _ => new string('a', 400));

        var preparo = TemplateRenderer.Prepare(modelo, valores);

        Assert.False(preparo.IsReady);
        Assert.Equal(("*", "rendered-too-long"), Assert.Single(preparo.Errors).Let(e => (e.Key, e.Code)));
    }

    [Fact]
    public void A_chave_do_bloco_de_canal_leva_o_prefixo_dos_parametros()
    {
        Assert.Equal("tpl.p.body.1", TemplateRenderer.ChannelDataKey("body.1"));
        Assert.Equal("tpl.name", IaBimeta.Conversations.Contracts.ChannelDataKeys.TemplateName);
        Assert.Equal(IaBimeta.Conversations.Contracts.ChannelDataKeys.TemplateName, TemplateRenderer.KeyTemplateName);
    }
}

internal static class TesteExtensoes
{
    public static TResultado Let<T, TResultado>(this T valor, Func<T, TResultado> funcao) => funcao(valor);
}
