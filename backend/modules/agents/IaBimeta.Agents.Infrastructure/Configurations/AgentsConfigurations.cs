using IaBimeta.Agents.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Agents.Infrastructure.Configurations;

// Regra de ouro dos CHECKs deste esquema: um CHECK só recusa FALSE, e um resultado NULL PASSA. Por isso toda restrição abaixo é escrita só com colunas NOT NULL ou trata cada coluna anulável de forma
// explícita (IS NULL / IS NOT NULL).

internal sealed class AiTurnConfiguration : IEntityTypeConfiguration<AiTurn>
{
    /// <summary>No máximo UM turno aberto (agendado, pronto ou em andamento) por sessão (I1): a garantia no banco, além da lógica.</summary>
    public const string OpenPerSessionIndex = "ux_ai_turns_open_per_session";

    /// <summary>A busca do executor: só os turnos abertos, por estado.</summary>
    public const string OpenDueIndex = "ix_ai_turns_open_state_fire_at";

    public void Configure(EntityTypeBuilder<AiTurn> builder)
    {
        builder.ToTable("ai_turns", table =>
        {
            table.HasCheckConstraint("ck_ai_turns_state", "state IN (1, 2, 3, 4, 5, 6, 7, 8)");
            table.HasCheckConstraint("ck_ai_turns_counters", "attempts >= 0 AND supersede_count >= 0 AND version >= 1");

            // Em andamento se, e somente se, tem o token de lane, o dono e o lease.
            table.HasCheckConstraint(
                "ck_ai_turns_running",
                "(state = 3) = (lane_token IS NOT NULL AND claimed_by IS NOT NULL AND lease_expires_at IS NOT NULL)");

            // Terminal se, e somente se, tem o instante do fim e o motivo.
            table.HasCheckConstraint("ck_ai_turns_ended", "(state IN (4, 5, 6, 7, 8)) = (ended_at IS NOT NULL AND end_reason IS NOT NULL)");

            // O recuo só existe no estado pronto.
            table.HasCheckConstraint("ck_ai_turns_next_attempt", "next_attempt_at IS NULL OR state = 2");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(t => new { t.TenantId, t.Id });
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.SessionId).HasColumnName("session_id");
        builder.Property(t => t.State).HasColumnName("state").HasConversion<short>();
        builder.Property(t => t.FirstReceivedAt).HasColumnName("first_received_at");
        builder.Property(t => t.LastReceivedAt).HasColumnName("last_received_at");
        builder.Property(t => t.FireAt).HasColumnName("fire_at");
        builder.Property(t => t.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(t => t.Attempts).HasColumnName("attempts");
        builder.Property(t => t.SupersedeCount).HasColumnName("supersede_count");
        builder.Property(t => t.InputPending).HasColumnName("input_pending");
        builder.Property(t => t.LaneToken).HasColumnName("lane_token");
        builder.Property(t => t.ClaimedBy).HasColumnName("claimed_by").HasMaxLength(200);
        builder.Property(t => t.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(t => t.ContentWaitStartedAt).HasColumnName("content_wait_started_at");
        builder.Property(t => t.HandoffReason).HasColumnName("handoff_reason").HasMaxLength(100);
        builder.Property(t => t.NoticeDone).HasColumnName("notice_done");
        builder.Property(t => t.EndReason).HasColumnName("end_reason").HasMaxLength(100);
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        builder.Property(t => t.EndedAt).HasColumnName("ended_at");

        // A concorrência otimista entre o handler dos eventos e o executor: duas alterações do mesmo ponto de partida não se sobrescrevem em silêncio.
        builder.Property(t => t.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Ignore(t => t.IsOpen);
        builder.Ignore(t => t.IsTerminal);

        builder.HasIndex(t => new { t.TenantId, t.SessionId })
            .IsUnique()
            .HasFilter("state IN (1, 2, 3)")
            .HasDatabaseName(OpenPerSessionIndex);

        builder.HasIndex(t => new { t.State, t.FireAt })
            .HasFilter("state IN (1, 2, 3)")
            .HasDatabaseName(OpenDueIndex);

        // O histórico da sessão e a busca dos tenants que usam a IA.
        builder.HasIndex(t => new { t.TenantId, t.SessionId, t.CreatedAt }).HasDatabaseName("ix_ai_turns_tenant_id_session_id_created_at");
        builder.HasIndex(t => new { t.TenantId, t.CreatedAt }).HasDatabaseName("ix_ai_turns_tenant_id_created_at");
    }
}

internal sealed class AgentDefinitionVersionConfiguration : IEntityTypeConfiguration<AgentDefinitionVersion>
{
    public const string OneDraftIndex = "ux_agent_definition_versions_draft";
    public const string OnePublishedIndex = "ux_agent_definition_versions_published";

    public void Configure(EntityTypeBuilder<AgentDefinitionVersion> builder)
    {
        builder.ToTable("agent_definition_versions", table =>
        {
            table.HasCheckConstraint("ck_agent_definition_versions_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_agent_definition_versions_counters", "number >= 1 AND revision >= 1");
            table.HasCheckConstraint("ck_agent_definition_versions_instructions", $"length(instructions) BETWEEN 1 AND {AgentDefinitionContent.InstructionsMaxLength}");

            // O rascunho nunca foi publicado; a versão publicada e a aposentada têm o instante da publicação.
            table.HasCheckConstraint(
                "ck_agent_definition_versions_published",
                "(status = 1 AND published_at IS NULL) OR (status IN (2, 3) AND published_at IS NOT NULL)");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(v => new { v.TenantId, v.Id });
        builder.Property(v => v.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.Number).HasColumnName("number");
        builder.Property(v => v.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(v => v.Instructions).HasColumnName("instructions").IsRequired();
        builder.Property(v => v.FaqJson).HasColumnName("faq").HasColumnType("jsonb").IsRequired();
        builder.Property(v => v.AudioInformativeText).HasColumnName("audio_informative_text");
        builder.Property(v => v.HandoffNoticeText).HasColumnName("handoff_notice_text");
        builder.Property(v => v.FaqLocale).HasColumnName("faq_locale").HasMaxLength(Locale.MaxLength);
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        builder.Property(v => v.UpdatedBy).HasColumnName("updated_by");
        builder.Property(v => v.PublishedAt).HasColumnName("published_at");
        builder.Property(v => v.PublishedBy).HasColumnName("published_by");
        builder.Property(v => v.Revision).HasColumnName("revision").IsConcurrencyToken();
        builder.Ignore(v => v.Content);

        // O número da versão é único por tenant e nunca reaproveitado. No máximo um rascunho e uma publicada por tenant.
        // Os dois índices parciais são sobre a MESMA coluna: precisam ser declarados pelo nome, senão o EF os funde num só.
        builder.HasIndex(v => new { v.TenantId, v.Number }, "ux_agent_definition_versions_number").IsUnique();
        builder.HasIndex(v => v.TenantId, OneDraftIndex).IsUnique().HasFilter("status = 1");
        builder.HasIndex(v => v.TenantId, OnePublishedIndex).IsUnique().HasFilter("status = 2");
    }
}

internal sealed class KnowledgeGapConfiguration : IEntityTypeConfiguration<KnowledgeGap>
{
    /// <summary>Uma lacuna por pergunta (hash) e por tenant: é o que torna o agrupamento atômico (<c>ON CONFLICT</c>) entre turnos concorrentes.</summary>
    public const string UniqueHashIndex = "ux_knowledge_gaps_tenant_id_question_hash";

    public void Configure(EntityTypeBuilder<KnowledgeGap> builder)
    {
        builder.ToTable("knowledge_gaps", table =>
        {
            table.HasCheckConstraint("ck_knowledge_gaps_kind", "kind IN (1, 2, 3)");
            table.HasCheckConstraint("ck_knowledge_gaps_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_knowledge_gaps_hash", "octet_length(question_hash) = 32");
            table.HasCheckConstraint("ck_knowledge_gaps_revision", "revision >= 1");

            // Aberta se, e somente se, ainda não foi decidida; a versão do rascunho só existe em quem foi enviada a ele.
            table.HasCheckConstraint("ck_knowledge_gaps_decided", "(status = 1) = (decided_at IS NULL)");
            table.HasCheckConstraint("ck_knowledge_gaps_promoted_version", "promoted_version IS NULL OR (status = 3 AND promoted_version >= 1)");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(g => new { g.TenantId, g.Id });
        builder.Property(g => g.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(g => g.QuestionHash).HasColumnName("question_hash").HasColumnType("bytea");
        builder.Property(g => g.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(g => g.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(g => g.FirstSeenAt).HasColumnName("first_seen_at");
        builder.Property(g => g.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(g => g.DecidedAt).HasColumnName("decided_at");
        builder.Property(g => g.DecidedBy).HasColumnName("decided_by");
        builder.Property(g => g.PromotedVersion).HasColumnName("promoted_version");
        builder.Property(g => g.Revision).HasColumnName("revision").IsConcurrencyToken();

        builder.HasIndex(g => new { g.TenantId, g.QuestionHash }).IsUnique().HasDatabaseName(UniqueHashIndex);

        // A lista da revisão: por situação, das mais recentes.
        builder.HasIndex(g => new { g.TenantId, g.Status, g.LastSeenAt }).HasDatabaseName("ix_knowledge_gaps_tenant_id_status_last_seen_at");
    }
}

internal sealed class KnowledgeGapOccurrenceConfiguration : IEntityTypeConfiguration<KnowledgeGapOccurrence>
{
    public void Configure(EntityTypeBuilder<KnowledgeGapOccurrence> builder)
    {
        builder.ToTable("knowledge_gap_occurrences");

        // A chave é (tenant, turno): um turno conta UMA vez (a idempotência do registro). A referência à mensagem é um identificador opaco do Conversations: nenhuma chave estrangeira para outro esquema (DAD-2).
        builder.HasKey(o => new { o.TenantId, o.TurnId });
        builder.Property(o => o.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(o => o.TurnId).HasColumnName("turn_id").ValueGeneratedNever();
        builder.Property(o => o.GapId).HasColumnName("gap_id");
        builder.Property(o => o.SessionId).HasColumnName("session_id");
        builder.Property(o => o.ConversationId).HasColumnName("conversation_id");
        builder.Property(o => o.MessageId).HasColumnName("message_id");
        builder.Property(o => o.OccurredAt).HasColumnName("occurred_at");

        builder.HasOne<KnowledgeGap>().WithMany().HasForeignKey(o => new { o.TenantId, o.GapId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(o => new { o.TenantId, o.GapId, o.OccurredAt }).HasDatabaseName("ix_knowledge_gap_occurrences_tenant_id_gap_id_occurred_at");
    }
}
