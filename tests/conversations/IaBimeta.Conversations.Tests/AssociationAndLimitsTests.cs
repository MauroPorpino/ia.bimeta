using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

/// <summary>
/// Fatia A2 (ADR 0012, seção A): os limites alargados da referência do contato e do identificador do evento, o modo de associação da sessão (hoje só o do contato) e a
/// compatibilidade do contrato de ingestão. Tudo sem banco; o que o banco garante (índice, restrição, tamanho das colunas) é provado em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class AssociationAndLimitsTests
{
    private static Message Mensagem(string evento)
    {
        var sessao = Fabrica.Sessao();
        var registro = sessao.RegisterInbound(Fabrica.Agora, Fabrica.Agora);
        return Message.CreateInbound(Fabrica.Tenant, sessao.Id, Guid.NewGuid(), ChannelInstanceId.New(), registro, evento, Fabrica.Agora, MessageKind.Text, "Oi", Fabrica.Agora);
    }

    private static Contact Contato(string referencia) => Contact.Create(Fabrica.Tenant, ChannelInstanceId.New(), referencia, null, Fabrica.Agora);

    // Limites

    [Fact]
    public void Os_dois_limites_alargados_tem_os_valores_do_ADR_0012_e_os_demais_nao_mudaram()
    {
        Assert.Equal(254, ConversationLimits.ContactRefMaxLength);
        Assert.Equal(512, ConversationLimits.ProviderEventIdMaxLength);

        // Só estes dois mudam na A2: os demais limites seguem como estavam.
        Assert.Equal(4096, ConversationLimits.TextMaxLength);
        Assert.Equal(120, ConversationLimits.DisplayNameMaxLength);
        Assert.Equal(100, ConversationLimits.ReasonMaxLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)] // o limite antigo
    [InlineData(129)] // recusada antes da A2
    [InlineData(200)]
    [InlineData(254)] // o limite novo
    public void A_referencia_do_contato_ate_o_limite_novo_e_aceita_e_guardada_inteira(int tamanho)
    {
        var referencia = new string('a', tamanho);

        Assert.Equal(referencia, Contato(referencia).ExternalRef);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(1000)]
    public void A_referencia_do_contato_acima_do_limite_novo_e_recusada(int tamanho)
    {
        Assert.Throws<ConversationsValidationException>(() => Contato(new string('a', tamanho)));
    }

    [Fact]
    public void A_referencia_vazia_continua_recusada_e_o_corte_das_pontas_nao_conta_para_o_limite()
    {
        Assert.Throws<ConversationsValidationException>(() => Contato(string.Empty));
        Assert.Throws<ConversationsValidationException>(() => Contato("   "));

        // 254 caracteres úteis com espaços nas pontas: o que conta é a referência já sem os espaços.
        Assert.Equal(ConversationLimits.ContactRefMaxLength, Contato("  " + new string('a', ConversationLimits.ContactRefMaxLength) + "  ").ExternalRef.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)] // o limite antigo
    [InlineData(201)] // recusado antes da A2
    [InlineData(300)]
    [InlineData(512)] // o limite novo
    public void O_identificador_do_evento_ate_o_limite_novo_e_aceito_e_guardado_inteiro(int tamanho)
    {
        var evento = new string('e', tamanho);

        Assert.Equal(evento, Mensagem(evento).ProviderEventId);
    }

    [Theory]
    [InlineData(513)]
    [InlineData(2000)]
    public void O_identificador_do_evento_acima_do_limite_novo_e_recusado(int tamanho)
    {
        Assert.Throws<ConversationsValidationException>(() => Mensagem(new string('e', tamanho)));
    }

    // Modo de associação

    [Fact]
    public void O_modo_de_associacao_tem_um_unico_valor_o_do_contato_e_ele_vale_um()
    {
        // O valor numérico é o gravado no banco (association_mode) e o que o CHECK aceita: não pode mudar nem ganhar vizinhos sem migração e ADR.
        Assert.Equal(new[] { AssociationMode.Contact }, Enum.GetValues<AssociationMode>());
        Assert.Equal(1, (int)AssociationMode.Contact);
        Assert.False(Enum.IsDefined((AssociationMode)0));
        Assert.False(Enum.IsDefined((AssociationMode)2));
    }

    [Fact]
    public void A_sessao_criada_nasce_com_a_associacao_por_contato_e_sem_chave_de_thread()
    {
        var sessao = Fabrica.Sessao();

        Assert.Equal(AssociationMode.Contact, sessao.AssociationMode);
        Assert.Null(sessao.ThreadKey);
    }

    [Fact]
    public void O_modo_de_associacao_e_a_chave_de_thread_nao_tem_como_mudar_depois_da_criacao()
    {
        // Os dois valores só se definem na criação (set privado): nada no domínio os altera, e o CHECK do banco recusa qualquer outra combinação.
        var propriedades = new[] { nameof(ServiceSession.AssociationMode), nameof(ServiceSession.ThreadKey) };

        foreach (var nome in propriedades)
        {
            var propriedade = typeof(ServiceSession).GetProperty(nome)!;
            Assert.True(propriedade.GetMethod!.IsPublic);
            Assert.False(propriedade.SetMethod!.IsPublic, $"{nome} não pode ter setter público.");
        }
    }

    // Contrato de ingestão

    [Fact]
    public void Os_chamadores_anteriores_da_ingestao_seguem_validos_e_recebem_a_associacao_por_contato()
    {
        // Construção posicional com os nove primeiros parâmetros, como todo chamador anterior à A2 fazia.
        var antigo = new IngestInboundMessageCommand(ChannelInstanceId.New(), "5511999990001", "Mauro", Guid.NewGuid(), null, "wamid.1", DateTimeOffset.UtcNow, MessageKind.Text, "Oi");

        Assert.Equal(AssociationMode.Contact, antigo.AssociationMode);
        Assert.Null(antigo.ChannelData);
        Assert.Equal(HandlingMode.AiAutonomous, antigo.InitialHandlingMode);
    }

    [Fact]
    public void O_modo_de_associacao_e_o_ultimo_parametro_da_ingestao_e_opcional_com_o_padrao_do_contato()
    {
        var parametros = typeof(IngestInboundMessageCommand).GetConstructors().Single().GetParameters();
        var ultimo = parametros[^1];

        Assert.Equal(nameof(IngestInboundMessageCommand.AssociationMode), ultimo.Name);
        Assert.True(ultimo.HasDefaultValue);
        Assert.Equal(AssociationMode.Contact, ultimo.DefaultValue);

        // Tudo que vem depois dos nove obrigatórios é opcional: nenhum chamador anterior quebra.
        Assert.All(parametros.Skip(9), p => Assert.True(p.HasDefaultValue, $"{p.Name} precisa ser opcional."));
    }
}
