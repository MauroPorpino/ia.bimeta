using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O MODELO do EF do Conversations depois da A2 (ADR 0012, seção A): as colunas de associação, a restrição estrita, o índice único parcial do chat e os limites alargados.
/// Estes testes NÃO usam banco: constroem o contexto sem conexão (como a fábrica de tempo de design) e leem os metadados. Por isso não pertencem à coleção do PostgreSQL e
/// podem rodar antes de a migração ser aplicada em qualquer banco. O que o banco de fato faz é provado em <see cref="ChatSessionAssociationIntegrationTests"/>.
/// </summary>
public sealed class ChatSessionModelTests
{
    private static IModel Modelo()
    {
        using var contexto = new ConversationsDbContextFactory().CreateDbContext(Array.Empty<string>());
        return contexto.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType Entidade<T>() => Modelo().FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} não está no modelo.");

    [Fact]
    public void O_indice_de_chat_e_unico_parcial_por_tenant_e_contato_e_o_indice_antigo_nao_existe_mais()
    {
        var indices = Entidade<ServiceSession>().GetIndexes().ToList();

        var chat = Assert.Single(indices, i => i.GetDatabaseName() == "ux_service_sessions_open_chat");
        Assert.True(chat.IsUnique);
        Assert.Equal(new[] { "TenantId", "ContactId" }, chat.Properties.Select(p => p.Name));
        Assert.Equal("status = 1 AND thread_key IS NULL", chat.GetFilter());

        Assert.DoesNotContain(indices, i => i.GetDatabaseName() == "ix_service_sessions_one_open_per_contact");

        // Nenhum outro índice único cobre (tenant, contato): a unicidade da sessão aberta de chat tem UM dono.
        Assert.Single(indices, i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "ContactId" }));
    }

    [Fact]
    public void A_restricao_estrita_so_aceita_chat_sem_chave_de_thread()
    {
        var restricoes = Entidade<ServiceSession>().GetCheckConstraints().ToDictionary(r => r.Name ?? string.Empty, r => r.Sql ?? string.Empty);

        Assert.Equal("association_mode = 1 AND thread_key IS NULL", restricoes["ck_service_sessions_association"]);

        // As restrições anteriores continuam lá.
        Assert.Contains("ck_service_sessions_sequences", restricoes.Keys);
        Assert.Contains("ck_service_sessions_versions", restricoes.Keys);
        Assert.Contains("ck_service_sessions_status", restricoes.Keys);
        Assert.Contains("ck_service_sessions_responder", restricoes.Keys);
        Assert.Contains("ck_service_sessions_department", restricoes.Keys);
    }

    [Fact]
    public void As_colunas_de_associacao_tem_o_tipo_o_padrao_e_a_nulidade_esperados()
    {
        var sessao = Entidade<ServiceSession>();

        var modo = sessao.FindProperty(nameof(ServiceSession.AssociationMode))!;
        Assert.Equal("association_mode", modo.GetColumnName());
        Assert.Equal("smallint", modo.GetColumnType());
        Assert.False(modo.IsNullable);
        Assert.Equal(1, Convert.ToInt32(modo.GetDefaultValue()));

        var chave = sessao.FindProperty(nameof(ServiceSession.ThreadKey))!;
        Assert.Equal("thread_key", chave.GetColumnName());
        Assert.Equal("text", chave.GetColumnType());
        Assert.True(chave.IsNullable);
        Assert.Null(chave.GetMaxLength());
    }

    [Fact]
    public void Os_limites_alargados_estao_nas_colunas_do_modelo()
    {
        var referencia = Entidade<Contact>().FindProperty(nameof(Contact.ExternalRef))!;
        var evento = Entidade<Message>().FindProperty(nameof(Message.ProviderEventId))!;

        Assert.Equal(("external_ref", 254, "character varying(254)"), (referencia.GetColumnName(), referencia.GetMaxLength(), referencia.GetColumnType()));
        Assert.Equal(("provider_event_id", 512, "character varying(512)"), (evento.GetColumnName(), evento.GetMaxLength(), evento.GetColumnType()));

        // O modelo e o domínio dizem o mesmo número.
        Assert.Equal(ConversationLimits.ContactRefMaxLength, referencia.GetMaxLength());
        Assert.Equal(ConversationLimits.ProviderEventIdMaxLength, evento.GetMaxLength());
    }

    [Fact]
    public void Os_indices_unicos_do_contato_e_do_evento_seguem_sobre_as_colunas_alargadas()
    {
        var contato = Entidade<Contact>().GetIndexes().Single(i => i.GetDatabaseName() == "ix_contacts_tenant_id_channel_instance_id_external_ref");
        Assert.True(contato.IsUnique);
        Assert.Equal(new[] { "TenantId", "ChannelInstanceId", "ExternalRef" }, contato.Properties.Select(p => p.Name));

        var evento = Entidade<Message>().GetIndexes().Single(i => i.GetDatabaseName() == "ix_messages_tenant_id_channel_instance_id_provider_event_id");
        Assert.True(evento.IsUnique);
        Assert.Equal("provider_event_id IS NOT NULL", evento.GetFilter());
    }
}
