using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// Fatia A3 (ADR 0011, migração 6 reduzida): a mensagem do cliente guarda o bloco opaco de metadados do canal (versão e pares), sem interpretá-lo e sem nenhuma regra por
/// canal. Tudo sem banco; a gravação e a leitura no PostgreSQL estão em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class MessageChannelDataTests
{
    private static Message Mensagem(ChannelData? dados, MessageKind tipo = MessageKind.Text, string? texto = "Oi")
    {
        var sessao = Fabrica.Sessao();
        var registro = sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        return Message.CreateInbound(
            Fabrica.Tenant, sessao.Id, Guid.NewGuid(), ChannelInstanceId.New(), registro, "evento.1", Fabrica.Agora, tipo, texto, Fabrica.Agora, declaredLocale: null, channelData: dados);
    }

    private static Dictionary<string, string> Midia() => new()
    {
        ["media.kind"] = "image",
        ["media.mime"] = "image/jpeg",
        ["media.size"] = "48213",
        ["media.provider-id"] = "1234567890",
    };

    // Guarda e devolve

    [Fact]
    public void Mensagem_sem_ChannelData_guarda_nulo_na_versao_e_nos_pares_e_o_comportamento_de_antes_nao_muda()
    {
        var mensagem = Mensagem(null);

        Assert.Null(mensagem.ChannelDataVersion);
        Assert.Null(mensagem.ChannelDataValues);
        Assert.Null(mensagem.ChannelData);
        Assert.Equal(ContentState.Ready, mensagem.ContentState);
    }

    [Fact]
    public void Mensagem_com_ChannelData_guarda_a_versao_e_os_pares_exatamente_como_vieram()
    {
        var mensagem = Mensagem(new ChannelData(3, Midia()));

        Assert.Equal(3, mensagem.ChannelDataVersion);
        Assert.Equal(Midia(), mensagem.ChannelDataValues);

        var reconstruido = mensagem.ChannelData!;
        Assert.Equal(3, reconstruido.Version);
        Assert.Equal(Midia(), reconstruido.Values);
    }

    [Fact]
    public void Um_bloco_com_pares_vazios_e_valido_e_diferente_de_nenhum_bloco()
    {
        var mensagem = Mensagem(new ChannelData(1, new Dictionary<string, string>()));

        Assert.Equal(1, mensagem.ChannelDataVersion);
        Assert.NotNull(mensagem.ChannelDataValues);
        Assert.Empty(mensagem.ChannelDataValues!);
        Assert.NotNull(mensagem.ChannelData);
    }

    [Fact]
    public void A_mensagem_guarda_uma_copia_imutavel_e_nunca_o_dicionario_do_chamador()
    {
        var origem = Midia();
        var mensagem = Mensagem(new ChannelData(1, origem));

        // O chamador muda o dicionário depois: a mensagem não muda.
        origem["media.kind"] = "audio";
        origem["media.extra"] = "novo";
        origem.Remove("media.mime");

        Assert.Equal(Midia(), mensagem.ChannelDataValues);

        // E o que a mensagem expõe não é mutável (nem por conversão para a interface de escrita).
        Assert.False(mensagem.ChannelDataValues is Dictionary<string, string>);
        Assert.True(((IDictionary<string, string>)mensagem.ChannelDataValues!).IsReadOnly);
    }

    [Theory]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Audio)]
    [InlineData(MessageKind.Document)]
    [InlineData(MessageKind.Video)]
    public void Midia_do_cliente_guarda_o_bloco_e_continua_pendente_e_sem_texto(MessageKind tipo)
    {
        var mensagem = Mensagem(new ChannelData(1, Midia()), tipo, texto: null);

        Assert.Equal(ContentState.Pending, mensagem.ContentState);
        Assert.Null(mensagem.Text);
        Assert.Equal(Midia(), mensagem.ChannelDataValues);
    }

    [Fact]
    public void O_bloco_nao_muda_o_texto_nem_o_estado_do_conteudo_da_mensagem()
    {
        var com = Mensagem(new ChannelData(1, Midia()), MessageKind.Text, "Preciso de jejum?");
        var sem = Mensagem(null, MessageKind.Text, "Preciso de jejum?");

        Assert.Equal((sem.Text, sem.ContentState, sem.Kind), (com.Text, com.ContentState, com.Kind));
    }

    // Validação

    public static IEnumerable<object[]> BlocosInvalidos()
    {
        yield return new object[] { new ChannelData(0, new Dictionary<string, string>()) };
        yield return new object[] { new ChannelData(1, new Dictionary<string, string> { ["Chave Invalida"] = "v" }) };
        yield return new object[] { new ChannelData(1, new Dictionary<string, string> { ["k"] = new string('v', ConversationLimits.ChannelDataValueMaxLength + 1) }) };
        yield return new object[] { new ChannelData(1, Enumerable.Range(0, ConversationLimits.ChannelDataMaxEntries + 1).ToDictionary(i => $"k{i}", _ => "v")) };
        yield return new object[] { new ChannelData(1, new Dictionary<string, string> { ["k"] = "controle\u0000" }) };
    }

    [Theory]
    [MemberData(nameof(BlocosInvalidos))]
    public void Um_bloco_fora_dos_limites_nao_cria_a_mensagem(ChannelData invalido)
    {
        Assert.Throws<ConversationsValidationException>(() => Mensagem(invalido));
    }

    // Os valores com substituto sem par NÃO podem passar por InlineData: o xUnit serializa os dados do teste e troca o substituto solitário por U+FFFD (um caractere válido),
    // e o teste deixaria de provar o que diz. Por isso o dado é escolhido pelo índice, dentro do próprio teste.
    private static readonly string[] SubstitutosSemPar =
    {
        "\uD83D", // alto sozinho
        "\uDE00", // baixo sozinho
        "texto\uD83Dtexto", // alto no meio, sem o baixo
        "\uDE00\uD83D", // ordem invertida
        "\uD83D😀", // alto sobrando antes de um par válido
        "par válido 😀 e um alto no fim \uD83D", // um par válido não salva o solitário
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Valor_com_substituto_UTF16_sem_par_e_recusado_porque_nao_pode_ser_guardado_como_JSON(int indice)
    {
        var valor = SubstitutosSemPar[indice];
        var dados = new ChannelData(1, new Dictionary<string, string> { ["k"] = valor });

        var falha = Assert.Throws<ConversationsValidationException>(() => ConversationLimits.ValidateChannelData(dados));
        Assert.Contains("Unicode", falha.Message);
        Assert.Throws<ConversationsValidationException>(() => Mensagem(dados));
    }

    [Theory]
    [InlineData("😀")] // par válido
    [InlineData("a😀b")]
    [InlineData("João da Silva — «ação» 你好 مرحبا")]
    [InlineData("com \"aspas\" e \\ barra e <tag> & 'apóstrofo'")]
    [InlineData("")]
    public void Valor_em_Unicode_valido_inclusive_com_emoji_aspas_e_barra_e_aceito(string valor)
    {
        var dados = new ChannelData(1, new Dictionary<string, string> { ["k"] = valor });

        ConversationLimits.ValidateChannelData(dados);
        Assert.Equal(valor, Mensagem(dados).ChannelDataValues!["k"]);
    }

    [Fact]
    public void O_total_de_8192_caracteres_e_aceito_e_um_a_mais_e_recusado()
    {
        // Chaves k0..k7 (16 caracteres) mais sete valores de 1024 e um de 1008: exatamente 8192.
        var pares = Enumerable.Range(0, 8).ToDictionary(i => $"k{i}", i => new string('x', i < 7 ? 1024 : 1008));
        Assert.Equal(ConversationLimits.ChannelDataMaxTotalLength, pares.Sum(p => p.Key.Length + p.Value.Length));

        Assert.Equal(pares, Mensagem(new ChannelData(1, pares)).ChannelDataValues);

        pares["k7"] += "x";
        Assert.Throws<ConversationsValidationException>(() => Mensagem(new ChannelData(1, pares)));
    }

    // Só a mensagem do cliente carrega o bloco na A3

    [Fact]
    public void A_resposta_da_IA_nao_carrega_bloco_de_metadados_do_canal()
    {
        var resposta = Message.CreateAiReply(
            Fabrica.Tenant, Guid.NewGuid(), Guid.NewGuid(), ChannelInstanceId.New(), 2, Guid.NewGuid(), ReplyKind.Generated, 1, "Resposta", new[] { Guid.NewGuid() }, Fabrica.Agora, Locale.Parse("pt-BR"));

        Assert.Null(resposta.ChannelDataVersion);
        Assert.Null(resposta.ChannelDataValues);
        Assert.Null(resposta.ChannelData);
    }
}
