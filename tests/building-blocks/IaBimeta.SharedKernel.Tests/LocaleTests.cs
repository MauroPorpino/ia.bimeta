using System.Text.Json;
using IaBimeta.SharedKernel;

namespace IaBimeta.SharedKernel.Tests;

public class LocaleTests
{
    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("en-US", "en-US")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("pt", "pt")]
    [InlineData("zh-Hant-TW", "zh-Hant-TW")]
    [InlineData("es-419", "es-419")]
    [InlineData("sr-Latn-RS", "sr-Latn-RS")]
    [InlineData("de-CH-1996", "de-CH-1996")]
    [InlineData("en-US-x-twain", "en-US-x-twain")]
    [InlineData("th-TH-u-nu-thai", "th-TH-u-nu-thai")]
    public void Tags_bem_formadas_sao_aceitas_na_forma_canonica(string entrada, string esperado)
    {
        Assert.Equal(esperado, Locale.Parse(entrada).ToString());
    }

    [Theory]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("PT-BR", "pt-BR")]
    [InlineData("EN-us", "en-US")]
    [InlineData("ZH-hANT-tw", "zh-Hant-TW")]
    [InlineData("  fr-FR  ", "fr-FR")]
    [InlineData("DE-ch-1996", "de-CH-1996")]
    [InlineData("TH-th-U-NU-THAI", "th-TH-u-nu-thai")]
    public void A_forma_canonica_corrige_maiusculas_e_ignora_espacos_nas_pontas(string entrada, string esperado)
    {
        Assert.Equal(esperado, Locale.Parse(entrada).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("p")]
    [InlineData("pt_BR")]
    [InlineData("pt BR")]
    [InlineData("pt-")]
    [InlineData("-pt")]
    [InlineData("pt--BR")]
    [InlineData("pt-B")]
    [InlineData("pt-BRA")]
    [InlineData("pt-BR-BR")]
    [InlineData("portuguese")]
    [InlineData("abcd")] // quatro letras de idioma são reservadas
    [InlineData("pt-Latn-Cyrl")]
    [InlineData("de-1996-1996")] // variante repetida
    [InlineData("en-a-bbb-a-ccc")] // extensão repetida
    [InlineData("en-a")] // extensão sem conteúdo
    [InlineData("en-x")] // uso privado sem conteúdo
    [InlineData("en-US-x-")]
    [InlineData("i-klingon")] // tags legadas não são aceitas
    [InlineData("zh-yue")] // extlang não é aceito
    [InlineData("pt-BRç")]
    [InlineData("pt-бр")] // letras não ASCII
    [InlineData("pt-BR\u0000")]
    public void Texto_que_nao_e_BCP_47_bem_formado_e_recusado(string? entrada)
    {
        Assert.False(Locale.TryParse(entrada, out var locale));
        Assert.Null(locale);
        Assert.Throws<FormatException>(() => Locale.Parse(entrada));
    }

    [Fact]
    public void O_tamanho_maximo_de_35_caracteres_vale()
    {
        var no_limite = "en-US-x-" + string.Join('-', Enumerable.Repeat("abcdefg", 3)); // 8 + 7*3 + 2 = 31
        var acima = "en-US-x-" + string.Join('-', Enumerable.Repeat("abcdefg", 4)); // 39

        Assert.True(no_limite.Length <= Locale.MaxLength);
        Assert.True(Locale.TryParse(no_limite, out _));
        Assert.True(acima.Length > Locale.MaxLength);
        Assert.False(Locale.TryParse(acima, out _));
    }

    [Fact]
    public void A_igualdade_e_pela_forma_canonica_sem_depender_de_maiusculas()
    {
        var a = Locale.Parse("pt-BR");
        var b = Locale.Parse("PT-br");
        var c = Locale.Parse("pt-PT");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.True(a != c);
        Assert.False(a.Equals(null));
        Assert.True((Locale?)null == null);
        Assert.True(a != null);
    }

    [Fact]
    public void O_JSON_le_e_escreve_texto_e_recusa_o_que_nao_e_BCP_47()
    {
        var json = JsonSerializer.Serialize(new Exemplo(Locale.Parse("en-us"), null));
        var lido = JsonSerializer.Deserialize<Exemplo>("""{"Idioma":"es-es","Opcional":"FR-fr"}""")!;

        Assert.Equal("""{"Idioma":"en-US","Opcional":null}""", json);
        Assert.Equal((Locale.Parse("es-ES"), Locale.Parse("fr-FR")), (lido.Idioma, lido.Opcional));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Exemplo>("""{"Idioma":"pt_BR"}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Exemplo>("""{"Idioma":123}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Exemplo>("""{"Idioma":""}"""));
    }

    private sealed record Exemplo(Locale Idioma, Locale? Opcional);
}
