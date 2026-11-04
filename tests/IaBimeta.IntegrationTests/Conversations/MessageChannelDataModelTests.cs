using System.Text.Json;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O MODELO do EF do Conversations depois da A3 (migração 6 reduzida): as duas colunas do bloco opaco de metadados do canal, a restrição que as une e a conversão dos pares
/// para o jsonb e de volta. Estes testes NÃO usam banco: constroem o contexto sem conexão (como a fábrica de tempo de design) e leem os metadados, então não pertencem à
/// coleção do PostgreSQL e podem rodar antes de a migração ser aplicada em qualquer banco. O que o banco faz de fato está em <see cref="ChannelDataPersistenceIntegrationTests"/>.
/// </summary>
public sealed class MessageChannelDataModelTests
{
    private static IProperty Propriedade(string nome)
    {
        using var contexto = new ConversationsDbContextFactory().CreateDbContext(Array.Empty<string>());
        var entidade = contexto.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Message)) ?? throw new InvalidOperationException("Message não está no modelo.");
        return entidade.FindProperty(nome) ?? throw new InvalidOperationException($"{nome} não está mapeada.");
    }

    private static IEntityType Mensagem()
    {
        using var contexto = new ConversationsDbContextFactory().CreateDbContext(Array.Empty<string>());
        return contexto.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Message))!;
    }

    private static string ParaJson(IReadOnlyDictionary<string, string> pares) =>
        (string)Propriedade(nameof(Message.ChannelDataValues)).GetValueConverter()!.ConvertToProvider(pares)!;

    private static IReadOnlyDictionary<string, string> DoJson(string json) =>
        (IReadOnlyDictionary<string, string>)Propriedade(nameof(Message.ChannelDataValues)).GetValueConverter()!.ConvertFromProvider(json)!;

    // Colunas e restrição

    [Fact]
    public void As_duas_colunas_do_bloco_tem_o_nome_o_tipo_e_a_nulidade_esperados()
    {
        var versao = Propriedade(nameof(Message.ChannelDataVersion));
        Assert.Equal(("channel_data_version", "integer", true), (versao.GetColumnName(), versao.GetColumnType(), versao.IsNullable));
        Assert.Null(versao.GetDefaultValue());

        var pares = Propriedade(nameof(Message.ChannelDataValues));
        Assert.Equal(("channel_data", "jsonb", true), (pares.GetColumnName(), pares.GetColumnType(), pares.IsNullable));
        Assert.Null(pares.GetDefaultValue());
    }

    [Fact]
    public void O_bloco_reconstruido_nao_e_coluna_e_as_unicas_colunas_novas_sao_as_duas_do_bloco()
    {
        var entidade = Mensagem();

        Assert.Null(entidade.FindProperty(nameof(Message.ChannelData)));

        var colunas = entidade.GetProperties().Select(p => p.GetColumnName()).ToList();
        Assert.Equal(new[] { "channel_data", "channel_data_version" }, colunas.Where(c => c.StartsWith("channel_data", StringComparison.Ordinal)).OrderBy(c => c, StringComparer.Ordinal).ToArray());

        // Nenhuma coluna por canal: o que é específico de um canal vive dentro do bloco opaco, nunca como coluna do domínio comum.
        Assert.DoesNotContain(colunas, c => new[] { "whatsapp", "wamid", "webchat", "instagram", "messenger", "telegram", "email" }.Any(n => c.Contains(n, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void A_restricao_une_a_versao_e_os_pares_e_so_aceita_um_objeto_JSON_com_versao_de_um_em_diante()
    {
        var restricoes = Mensagem().GetCheckConstraints().ToDictionary(r => r.Name ?? string.Empty, r => r.Sql ?? string.Empty);

        Assert.Equal(
            "(channel_data IS NULL AND channel_data_version IS NULL) OR (channel_data IS NOT NULL AND channel_data_version IS NOT NULL AND channel_data_version >= 1 AND jsonb_typeof(channel_data) = 'object')",
            restricoes["ck_messages_channel_data"]);

        // As restrições anteriores seguem no lugar.
        Assert.Contains("ck_messages_direction_shape", restricoes.Keys);
        Assert.Contains("ck_messages_sequences", restricoes.Keys);
        Assert.Contains("ck_messages_locale", restricoes.Keys);
    }

    // Conversão dos pares

    [Fact]
    public void Os_pares_viram_um_objeto_JSON_de_texto_com_as_chaves_em_ordem_e_voltam_iguais()
    {
        var pares = new Dictionary<string, string> { ["media.size"] = "48213", ["media.kind"] = "image", ["media.mime"] = "image/jpeg" };

        var json = ParaJson(pares);

        Assert.Equal("""{"media.kind":"image","media.mime":"image/jpeg","media.size":"48213"}""", json);
        Assert.Equal(pares, DoJson(json));
    }

    [Fact]
    public void O_dicionario_que_volta_nao_e_mutavel()
    {
        var volta = DoJson("""{"k":"v"}""");

        Assert.False(volta is Dictionary<string, string>);
        Assert.True(((IDictionary<string, string>)volta).IsReadOnly);
    }

    public static IEnumerable<object[]> ValoresDificeis()
    {
        yield return new object[] { "acentos: João da Silva — «ação» ç ã é" };
        yield return new object[] { "aspas \"duplas\" e 'simples'" };
        yield return new object[] { @"barra \ e caminho C:\temp\x e barra / normal" };
        yield return new object[] { "emoji 😀👍🏽 e escritas 你好 مرحبا Привет" };
        yield return new object[] { "html <b>&amp;</b> + = ? #" };
        yield return new object[] { "\u2028 separador de linha e \u2029 de parágrafo" };
        yield return new object[] { string.Empty };
        yield return new object[] { "   espaços nas pontas   " };
        yield return new object[] { new string('é', 1024) };
    }

    [Theory]
    [MemberData(nameof(ValoresDificeis))]
    public void Valores_com_acentos_aspas_barra_emoji_e_vazio_voltam_exatamente_iguais(string valor)
    {
        var pares = new Dictionary<string, string> { ["k"] = valor, ["outro.valor"] = "x" };

        var json = ParaJson(pares);
        using var documento = JsonDocument.Parse(json); // é JSON válido e é um objeto

        Assert.Equal(JsonValueKind.Object, documento.RootElement.ValueKind);
        Assert.Equal(pares, DoJson(json));
        Assert.Equal(valor, DoJson(json)["k"]);
    }

    [Fact]
    public void Trinta_e_duas_entradas_e_o_total_de_8192_caracteres_voltam_exatamente_iguais()
    {
        var trintaEDuas = Enumerable.Range(0, ConversationLimits.ChannelDataMaxEntries).ToDictionary(i => $"chave.{i:00}", i => $"valor {i} " + new string('v', 100));
        var noLimiteDoTotal = Enumerable.Range(0, 8).ToDictionary(i => $"k{i}", i => new string('x', i < 7 ? 1024 : 1008));

        Assert.Equal(trintaEDuas, DoJson(ParaJson(trintaEDuas)));
        Assert.Equal(noLimiteDoTotal, DoJson(ParaJson(noLimiteDoTotal)));
        Assert.Equal(ConversationLimits.ChannelDataMaxTotalLength, noLimiteDoTotal.Sum(p => p.Key.Length + p.Value.Length));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("""{"k":1}""")]
    [InlineData("""{"k":null}""")]
    [InlineData("""{"k":{"a":"b"}}""")]
    [InlineData("não é json")]
    public void Um_valor_guardado_que_nao_e_objeto_de_texto_para_texto_falha_alto_ao_ler_e_nunca_vira_dado_silencioso(string json)
    {
        var conversor = Propriedade(nameof(Message.ChannelDataValues)).GetValueConverter()!;

        Assert.ThrowsAny<Exception>(() => conversor.ConvertFromProvider(json));
    }

    [Fact]
    public void Sem_bloco_a_coluna_fica_nula_nos_dois_sentidos_porque_o_EF_nao_chama_o_conversor_com_nulo()
    {
        var conversor = Propriedade(nameof(Message.ChannelDataValues)).GetValueConverter()!;

        Assert.Null(conversor.ConvertToProvider(null));
        Assert.Null(conversor.ConvertFromProvider(null));
    }

    // Comparação por conteúdo (rastreador de mudanças)

    [Fact]
    public void O_comparador_compara_por_conteudo_e_nao_pela_referencia_nem_pela_ordem()
    {
        var comparador = Propriedade(nameof(Message.ChannelDataValues)).GetValueComparer()!;
        IReadOnlyDictionary<string, string> a = new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" };
        IReadOnlyDictionary<string, string> mesmoEmOutraOrdem = new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" };
        IReadOnlyDictionary<string, string> valorDiferente = new Dictionary<string, string> { ["x"] = "1", ["y"] = "3" };
        IReadOnlyDictionary<string, string> chaveDiferente = new Dictionary<string, string> { ["x"] = "1", ["z"] = "2" };
        IReadOnlyDictionary<string, string> menor = new Dictionary<string, string> { ["x"] = "1" };

        Assert.True(comparador.Equals(a, mesmoEmOutraOrdem));
        Assert.Equal(comparador.GetHashCode(a), comparador.GetHashCode(mesmoEmOutraOrdem));
        Assert.False(comparador.Equals(a, valorDiferente));
        Assert.False(comparador.Equals(a, chaveDiferente));
        Assert.False(comparador.Equals(a, menor));
        Assert.True(comparador.Equals(null, null));
        Assert.False(comparador.Equals(a, null));
        Assert.False(comparador.Equals(null, a));
    }

    [Fact]
    public void O_snapshot_do_rastreador_e_uma_copia_independente_do_dicionario()
    {
        var comparador = Propriedade(nameof(Message.ChannelDataValues)).GetValueComparer()!;
        var origem = new Dictionary<string, string> { ["x"] = "1" };

        var copia = (IReadOnlyDictionary<string, string>)comparador.Snapshot(origem)!;
        origem["x"] = "mudou";

        Assert.False(ReferenceEquals(origem, copia));
        Assert.Equal("1", copia["x"]);
        Assert.Null(comparador.Snapshot(null));
    }
}
