using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// Os limites estruturais FIXOS do bloco opaco de metadados do canal (fatia A1; ADR 0011, seções B e D; DP-05): quantidade, tamanho e forma. Não há
/// configuração por tenant. O que acontece com um <c>ChannelData</c> válido na ingestão (guardado com a mensagem, A3) está em <c>ServiceGuardsTests</c>,
/// <c>MessageChannelDataTests</c> e nos testes de integração.
/// </summary>
public class ChannelDataTests
{
    private static ChannelData Dados(params (string Chave, string Valor)[] itens) =>
        new(1, itens.ToDictionary(i => i.Chave, i => i.Valor));

    private static ChannelData ComEntradas(int quantidade) =>
        new(1, Enumerable.Range(0, quantidade).ToDictionary(i => $"chave.{i}", _ => "valor"));

    [Fact]
    public void Metadados_de_midia_validos_passam()
    {
        ConversationLimits.ValidateChannelData(Dados(("media.kind", "image"), ("media.mime", "image/jpeg"), ("media.size", "48213"), ("media.provider-id", "1234567890")));
    }

    [Fact]
    public void Sem_entradas_e_valor_vazio_sao_validos_pois_ausente_e_vazio_sao_coisas_diferentes()
    {
        ConversationLimits.ValidateChannelData(new ChannelData(1, new Dictionary<string, string>()));
        ConversationLimits.ValidateChannelData(Dados(("media.caption", string.Empty)));
    }

    [Fact]
    public void O_limite_de_entradas_e_exato()
    {
        ConversationLimits.ValidateChannelData(ComEntradas(ConversationLimits.ChannelDataMaxEntries));
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateChannelData(ComEntradas(ConversationLimits.ChannelDataMaxEntries + 1)));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("media.kind")]
    [InlineData("x_y-z.0")]
    public void Chaves_no_formato_do_contrato_passam(string chave)
    {
        ConversationLimits.ValidateChannelData(Dados((chave, "v")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Media")]
    [InlineData(" media")]
    [InlineData("media ")]
    [InlineData("media kind")]
    [InlineData(".media")]
    [InlineData("-media")]
    [InlineData("_media")]
    [InlineData("media/kind")]
    [InlineData("mídia")]
    [InlineData("media\n")]
    public void Chaves_fora_do_formato_sao_recusadas(string chave)
    {
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateChannelData(Dados((chave, "v"))));
    }

    [Fact]
    public void O_limite_de_tamanho_da_chave_e_exato()
    {
        ConversationLimits.ValidateChannelData(Dados((new string('a', ConversationLimits.ChannelDataKeyMaxLength), "v")));
        Assert.Throws<ConversationsValidationException>(() =>
            ConversationLimits.ValidateChannelData(Dados((new string('a', ConversationLimits.ChannelDataKeyMaxLength + 1), "v"))));
    }

    [Fact]
    public void O_limite_de_tamanho_do_valor_e_exato()
    {
        ConversationLimits.ValidateChannelData(Dados(("k", new string('v', ConversationLimits.ChannelDataValueMaxLength))));
        Assert.Throws<ConversationsValidationException>(() =>
            ConversationLimits.ValidateChannelData(Dados(("k", new string('v', ConversationLimits.ChannelDataValueMaxLength + 1)))));
    }

    [Theory]
    [InlineData("com\nquebra")]
    [InlineData("com\ttabulacao")]
    [InlineData("com\0nulo")]
    [InlineData("com\u0085controle")]
    public void Valor_com_caractere_de_controle_e_recusado(string valor)
    {
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateChannelData(Dados(("k", valor))));
    }

    [Fact]
    public void Valor_com_texto_comum_acentos_e_emoji_passa()
    {
        ConversationLimits.ValidateChannelData(Dados(("media.caption", "Exame de sangue em jejum 🙂 — ação")));
    }

    [Fact]
    public void O_limite_do_total_de_caracteres_e_exato()
    {
        // 8 entradas de 64 + 960 caracteres somam exatamente o limite de 8192 (cada valor está abaixo do limite de 1024).
        var noLimite = Enumerable.Range(0, 8).Select(i => ($"{new string('a', 63)}{i}", new string('v', 960))).ToArray();
        Assert.Equal(ConversationLimits.ChannelDataMaxTotalLength, noLimite.Sum(i => i.Item1.Length + i.Item2.Length));
        ConversationLimits.ValidateChannelData(Dados(noLimite));

        var acima = noLimite.Take(7).Append((noLimite[7].Item1, new string('v', 961))).ToArray();
        Assert.Equal(ConversationLimits.ChannelDataMaxTotalLength + 1, acima.Sum(i => i.Item1.Length + i.Item2.Length));
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateChannelData(Dados(acima)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Versao_menor_que_um_e_recusada(int versao)
    {
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateChannelData(new ChannelData(versao, new Dictionary<string, string>())));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Versao_de_um_em_diante_passa(int versao)
    {
        ConversationLimits.ValidateChannelData(new ChannelData(versao, new Dictionary<string, string>()));
    }

    [Fact]
    public void Valores_ou_valor_nulo_e_dado_nulo_sao_recusados_sem_excecao_inesperada()
    {
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateChannelData(new ChannelData(1, null!)));
        Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateChannelData(new ChannelData(1, new Dictionary<string, string> { ["k"] = null! })));
        Assert.Throws<ArgumentNullException>(() => ConversationLimits.ValidateChannelData(null!));
    }
}
