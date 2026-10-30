using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Messaging;

public enum OutboxState
{
    /// <summary>Esperando o relay (ou esperando a próxima tentativa, em <c>NextAttemptAt</c>).</summary>
    Pending = 1,

    /// <summary>Em poder de um relay, com lease vigente. Lease vencido volta a ser elegível.</summary>
    Processing = 2,

    Processed = 3,

    /// <summary>Falha permanente ou máximo de tentativas: fica guardado com o motivo, para a administração. A fila segue.</summary>
    Dead = 4,
}

/// <summary>
/// Uma linha do outbox de eventos duráveis de UM módulo (<c>&lt;módulo&gt;.outbox_message</c>, BOUNDARIES.md §12.1). É gravada na
/// MESMA transação em que o módulo muda o estado dele, então o evento existe se, e somente se, a mudança existe. O relay a entrega
/// aos handlers pelo menos uma vez, em ordem por <see cref="OrderingKey"/>. Não é entidade de negócio: não tem filtro por tenant,
/// porque o relay lê as linhas de todos os tenants; o <see cref="TenantId"/> do envelope é o que ele usa para criar o contexto do
/// handler.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>O <c>EventId</c> do envelope: a chave de idempotência dos consumidores.</summary>
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string OrderingKey { get; set; } = string.Empty;

    /// <summary>A ordem de gravação, gerada pelo banco (identidade): define a sequência de entrega dentro de uma chave.</summary>
    public long IngestSeq { get; set; }

    public string EventType { get; set; } = string.Empty;

    public int EventVersion { get; set; }

    public Guid CorrelationId { get; set; }

    public Guid? CausationId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>O contrato do evento em JSON (<c>jsonb</c>). Nunca carrega segredo nem conteúdo de mensagem de cliente.</summary>
    public string Payload { get; set; } = "{}";

    public OutboxState State { get; set; } = OutboxState.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LeaseOwner { get; set; }

    /// <summary>O token do lease vigente: concluir a entrega exige o token (impede o relay antigo de sobrescrever o novo).</summary>
    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>O tipo da última falha e o motivo curto, sem conteúdo de mensagem nem dado pessoal.</summary>
    public string? LastError { get; set; }

    public string? DeadReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>
/// Registro de que um consumidor já tratou um evento (<c>&lt;módulo&gt;.inbox_message</c>): a idempotência por <c>EventId</c> de
/// cada consumidor. Gravado na mesma transação que o efeito do handler.
/// </summary>
public sealed class InboxMessage
{
    public Guid TenantId { get; set; }

    /// <summary>O nome estável do consumidor dentro do módulo (um evento pode ter vários consumidores).</summary>
    public string Consumer { get; set; } = string.Empty;

    public Guid EventId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Mapeamento das tabelas do outbox e do inbox no esquema do módulo dono (DAD-1 e DAD-2).</summary>
public static class MessagingModelExtensions
{
    public const string OutboxTable = "outbox_message";
    public const string InboxTable = "inbox_message";
    public const string OutboxEligibleIndex = "ix_outbox_message_state_next_attempt_at_ingest_seq";

    /// <summary>Mapeia o outbox no esquema do módulo. Chame em <c>OnModelCreating</c> do DbContext do módulo emissor.</summary>
    public static ModelBuilder ConfigureOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable(OutboxTable, table =>
            {
                table.HasCheckConstraint("ck_outbox_message_state", "state IN (1, 2, 3, 4)");
                table.HasCheckConstraint("ck_outbox_message_attempts", "attempts >= 0");
                table.HasCheckConstraint("ck_outbox_message_lease", "(state = 2) = (lease_token IS NOT NULL)");
            });

            // TEN-3: a chave começa por tenant_id.
            builder.HasKey(m => new { m.TenantId, m.Id });
            builder.Property(m => m.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
            builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(m => m.OrderingKey).HasColumnName("ordering_key").HasMaxLength(200).IsRequired();
            builder.Property(m => m.IngestSeq).HasColumnName("ingest_seq").ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
            builder.Property(m => m.EventType).HasColumnName("event_type").HasMaxLength(200).IsRequired();
            builder.Property(m => m.EventVersion).HasColumnName("event_version");
            builder.Property(m => m.CorrelationId).HasColumnName("correlation_id");
            builder.Property(m => m.CausationId).HasColumnName("causation_id");
            builder.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            builder.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            builder.Property(m => m.State).HasColumnName("state").HasConversion<short>();
            builder.Property(m => m.Attempts).HasColumnName("attempts");
            builder.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at");
            builder.Property(m => m.LeaseOwner).HasColumnName("lease_owner").HasMaxLength(200);
            builder.Property(m => m.LeaseToken).HasColumnName("lease_token");
            builder.Property(m => m.LeaseExpiresAt).HasColumnName("lease_expires_at");
            builder.Property(m => m.LastError).HasColumnName("last_error").HasMaxLength(300);
            builder.Property(m => m.DeadReason).HasColumnName("dead_reason").HasMaxLength(300);
            builder.Property(m => m.CreatedAt).HasColumnName("created_at");
            builder.Property(m => m.ProcessedAt).HasColumnName("processed_at");

            // O relay procura só o que está elegível, na ordem de gravação. Os já processados saem do índice.
            builder.HasIndex(m => new { m.State, m.NextAttemptAt, m.IngestSeq })
                .HasFilter("state IN (1, 2)")
                .HasDatabaseName(OutboxEligibleIndex);

            // A regra de ordem por chave olha, para cada linha, as anteriores da mesma chave ainda não terminadas.
            builder.HasIndex(m => new { m.TenantId, m.OrderingKey, m.IngestSeq })
                .HasFilter("state IN (1, 2)")
                .HasDatabaseName("ix_outbox_message_tenant_id_ordering_key_ingest_seq");
        });

        return modelBuilder;
    }

    /// <summary>Mapeia o inbox no esquema do módulo consumidor. Chame em <c>OnModelCreating</c> do DbContext do módulo consumidor.</summary>
    public static ModelBuilder ConfigureInbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxMessage>(builder =>
        {
            builder.ToTable(InboxTable);

            // Um evento é tratado uma vez por consumidor (e por tenant: TEN-3).
            builder.HasKey(m => new { m.TenantId, m.Consumer, m.EventId });
            builder.Property(m => m.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
            builder.Property(m => m.Consumer).HasColumnName("consumer").HasMaxLength(120).IsRequired();
            builder.Property(m => m.EventId).HasColumnName("event_id").ValueGeneratedNever();
            builder.Property(m => m.ProcessedAt).HasColumnName("processed_at");
        });

        return modelBuilder;
    }
}
