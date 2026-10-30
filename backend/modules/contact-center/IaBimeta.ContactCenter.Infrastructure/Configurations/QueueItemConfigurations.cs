using IaBimeta.ContactCenter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.ContactCenter.Infrastructure.Configurations;

internal sealed class QueueItemConfiguration : IEntityTypeConfiguration<QueueItem>
{
    public const string ActiveSessionIndex = "ux_queue_items_active_session";

    public void Configure(EntityTypeBuilder<QueueItem> builder)
    {
        builder.ToTable("queue_items", table =>
        {
            table.HasCheckConstraint("ck_queue_items_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_queue_items_version", "version >= 1");
            table.HasCheckConstraint("ck_queue_items_priority", "priority BETWEEN 0 AND 9");
            table.HasCheckConstraint("ck_queue_items_department", "department_id IS NULL OR business_unit_id IS NOT NULL");

            // A marca de projeção adiada e o limite do histórico depois da saída do escopo são sequências: nunca negativas.
            // A identidade da colocação (a do item e a da base da operação pendente) é uma geração: a partir de 1, ou desconhecida.
            table.HasCheckConstraint("ck_queue_items_placement", "(placement_epoch IS NULL OR placement_epoch >= 1) AND (pending_basis_placement_epoch IS NULL OR pending_basis_placement_epoch >= 1)");
            table.HasCheckConstraint("ck_queue_items_projection", "(resync_state_version IS NULL OR resync_state_version >= 0) AND (history_limit_seq IS NULL OR history_limit_seq >= 0)");

            // Proveniência das fronteiras de histórico: o modelo é 0 (legado, sem prova de origem: o padrão) ou 1 (todas as fronteiras positivas certificadas). A origem do
            // limite só existe junto de um limite e de um item no modelo certificado, e é uma geração de colocação válida. Nenhum registro existente é promovido.
            table.HasCheckConstraint(
                "ck_queue_items_boundary_provenance",
                "boundary_model IN (0, 1) AND (history_limit_origin_epoch IS NULL OR (history_limit_origin_epoch >= 1 AND history_limit_seq IS NOT NULL AND boundary_model = 1))");

            // No máximo UM atendente responsável (é uma coluna só), e ele existe se, e somente se, o item está atribuído.
            table.HasCheckConstraint("ck_queue_items_assigned", "(status = 2) = (assigned_user_id IS NOT NULL)");

            // O item encerrado tem data de encerramento e não tem operação pendente.
            table.HasCheckConstraint("ck_queue_items_closed", "(status = 3) = (closed_at IS NOT NULL) AND (status <> 3 OR pending_kind IS NULL)");

            // A operação pendente é tudo ou nada: ou todos os campos obrigatórios dela existem, ou nenhum dos campos dela existe. Cada
            // ramo é um teste que só vale TRUE ou FALSE, nunca NULL: uma restrição CHECK aceita NULL, então "pending_kind IN (...)" sozinho
            // deixaria passar uma linha com o tipo vazio e o resto da operação preenchido.
            table.HasCheckConstraint(
                "ck_queue_items_pending",
                "(pending_kind IS NULL AND pending_operation_id IS NULL AND pending_transition_id IS NULL AND pending_transfer_id IS NULL " +
                "AND pending_queue_id IS NULL AND pending_user_id IS NULL AND pending_business_unit_id IS NULL AND pending_department_id IS NULL " +
                "AND pending_actor_user_id IS NULL AND pending_actor_origin IS NULL AND pending_reason IS NULL AND pending_started_at IS NULL " +
                "AND pending_basis_period IS NULL AND pending_basis_responder IS NULL AND NOT pending_basis_ai_mode AND pending_basis_placement_epoch IS NULL AND pending_next_attempt_at IS NULL) " +
                "OR (pending_kind IS NOT NULL AND pending_kind IN (1, 2, 3, 4, 5) AND pending_operation_id IS NOT NULL AND pending_transition_id IS NOT NULL " +
                "AND pending_transfer_id IS NOT NULL AND pending_queue_id IS NOT NULL AND pending_reason IS NOT NULL AND pending_started_at IS NOT NULL " +
                "AND (pending_department_id IS NULL OR pending_business_unit_id IS NOT NULL) " +
                "AND (pending_basis_responder IS NULL OR pending_basis_period IS NOT NULL) AND (pending_basis_placement_epoch IS NULL OR pending_basis_period IS NOT NULL) " +
                "AND (NOT pending_basis_ai_mode OR (pending_basis_period IS NOT NULL AND pending_basis_responder IS NULL)))");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(i => new { i.TenantId, i.Id });
        builder.Property(i => i.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(i => i.SessionId).HasColumnName("session_id");
        builder.Property(i => i.OrganizationId).HasColumnName("organization_id");
        builder.Property(i => i.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(i => i.DepartmentId).HasColumnName("department_id");
        builder.Property(i => i.QueueId).HasColumnName("queue_id");
        builder.Property(i => i.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(i => i.AssignedUserId).HasColumnName("assigned_user_id");
        builder.Property(i => i.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(i => i.EnteredAt).HasColumnName("entered_at");
        builder.Property(i => i.AssignedAt).HasColumnName("assigned_at");
        builder.Property(i => i.ClosedAt).HasColumnName("closed_at");
        builder.Property(i => i.ClosedThroughMessageSeq).HasColumnName("closed_through_message_seq");
        builder.Property(i => i.HandlingPeriod).HasColumnName("handling_period");
        builder.Property(i => i.PlacementEpoch).HasColumnName("placement_epoch");
        builder.Property(i => i.CloseReason).HasColumnName("close_reason").HasMaxLength(QueueItem.ReasonMaxLength);
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at");
        builder.Property(i => i.Priority).HasColumnName("priority").HasDefaultValue(0);
        builder.Property(i => i.RoutingRuleId).HasColumnName("routing_rule_id");
        builder.Property(i => i.ObservedStateVersion).HasColumnName("observed_state_version").HasDefaultValue(0L);
        builder.Property(i => i.ExecutorFenceToken).HasColumnName("executor_fence_token");
        builder.Property(i => i.ResyncStateVersion).HasColumnName("resync_state_version");
        builder.Property(i => i.HistoryLimitSeq).HasColumnName("history_limit_seq");
        builder.Property(i => i.HistoryLimitOriginEpoch).HasColumnName("history_limit_origin_epoch");
        builder.Property(i => i.BoundaryModel).HasColumnName("boundary_model").HasDefaultValue((short)0);

        builder.Property(i => i.PendingKindValue).HasColumnName("pending_kind").HasConversion<short>();
        builder.Property(i => i.PendingOperationId).HasColumnName("pending_operation_id");
        builder.Property(i => i.PendingTransitionId).HasColumnName("pending_transition_id");
        builder.Property(i => i.PendingTransferId).HasColumnName("pending_transfer_id");
        builder.Property(i => i.PendingQueueId).HasColumnName("pending_queue_id");
        builder.Property(i => i.PendingUserId).HasColumnName("pending_user_id");
        builder.Property(i => i.PendingBusinessUnitId).HasColumnName("pending_business_unit_id");
        builder.Property(i => i.PendingDepartmentId).HasColumnName("pending_department_id");
        builder.Property(i => i.PendingActorUserId).HasColumnName("pending_actor_user_id");
        builder.Property(i => i.PendingActorOrigin).HasColumnName("pending_actor_origin").HasMaxLength(PendingAudit.ActorOriginMaxLength);
        builder.Property(i => i.PendingBasisPeriod).HasColumnName("pending_basis_period");
        builder.Property(i => i.PendingBasisResponder).HasColumnName("pending_basis_responder");
        builder.Property(i => i.PendingBasisAiMode).HasColumnName("pending_basis_ai_mode");
        builder.Property(i => i.PendingBasisPlacementEpoch).HasColumnName("pending_basis_placement_epoch");
        builder.Property(i => i.PendingReason).HasColumnName("pending_reason").HasMaxLength(QueueItem.ReasonMaxLength);
        builder.Property(i => i.PendingStartedAt).HasColumnName("pending_started_at");
        builder.Property(i => i.PendingAttempts).HasColumnName("pending_attempts");
        builder.Property(i => i.PendingLastError).HasColumnName("pending_last_error").HasMaxLength(QueueItem.ErrorMaxLength);
        builder.Property(i => i.PendingNextAttemptAt).HasColumnName("pending_next_attempt_at");

        builder.Ignore(i => i.Pending);
        builder.Ignore(i => i.HasPending);
        builder.Ignore(i => i.IsClosed);
        builder.Ignore(i => i.ResyncOpen);
        builder.Ignore(i => i.NeedsResync);
        builder.Ignore(i => i.HistoryLimitIsValid);
        builder.Ignore(i => i.ClosedBoundaryIsCertified);

        // A referência à fila carrega o tenant: o banco recusa item ligado à fila de outro tenant.
        builder.HasOne<ServiceQueue>().WithMany().HasForeignKey(i => new { i.TenantId, i.QueueId }).OnDelete(DeleteBehavior.Restrict);

        // No máximo UM item ativo por conversa: nunca dois atendimentos (nem duas filas) para a mesma sessão ao mesmo tempo.
        builder.HasIndex(i => new { i.TenantId, i.SessionId }).IsUnique().HasFilter("status <> 3").HasDatabaseName(ActiveSessionIndex);

        // A leitura da fila, dos mais antigos para os mais novos.
        // Do mais urgente para o menos e, na mesma prioridade, do mais antigo.
        builder.HasIndex(i => new { i.TenantId, i.QueueId, i.Status, i.Priority, i.EnteredAt })
            .IsDescending(false, false, false, true, false)
            .HasDatabaseName("ix_queue_items_tenant_id_queue_id_status_priority_entered_at");
        builder.HasIndex(i => new { i.TenantId, i.AssignedUserId }).HasFilter("assigned_user_id IS NOT NULL").HasDatabaseName("ix_queue_items_tenant_id_assigned_user_id");

        // O job de reconciliação procura só o que está pendente.
        builder.HasIndex(i => new { i.TenantId, i.PendingStartedAt }).HasFilter("pending_kind IS NOT NULL").HasDatabaseName("ix_queue_items_tenant_id_pending_started_at");

        // ...e o que ficou atrás do Conversations (projeção adiada): só os itens com a marca.
        builder.HasIndex(i => new { i.TenantId, i.UpdatedAt }).HasFilter("resync_state_version IS NOT NULL").HasDatabaseName("ix_queue_items_tenant_id_resync");
    }
}

internal sealed class PendingAuditConfiguration : IEntityTypeConfiguration<PendingAudit>
{
    public void Configure(EntityTypeBuilder<PendingAudit> builder)
    {
        builder.ToTable("audit_outbox", table => table.HasCheckConstraint("ck_audit_outbox_outcome", "outcome > 0"));

        builder.HasKey(a => new { a.TenantId, a.Id });
        builder.Property(a => a.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.Operation).HasColumnName("operation").HasMaxLength(PendingAudit.OperationMaxLength).IsRequired();
        builder.Property(a => a.ResourceType).HasColumnName("resource_type").HasMaxLength(PendingAudit.ResourceTypeMaxLength).IsRequired();
        builder.Property(a => a.ResourceId).HasColumnName("resource_id").HasMaxLength(PendingAudit.ResourceIdMaxLength);
        builder.Property(a => a.Outcome).HasColumnName("outcome").HasConversion<short>();
        builder.Property(a => a.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(a => a.ActorOrigin).HasColumnName("actor_origin").HasMaxLength(PendingAudit.ActorOriginMaxLength).IsRequired().HasDefaultValue(PendingAudit.UnknownOrigin);
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        // A entrega lê as mais antigas primeiro.
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt, a.Id }).HasDatabaseName("ix_audit_outbox_tenant_id_created_at_id");
    }
}

internal sealed class QueueOperationConfiguration : IEntityTypeConfiguration<QueueOperation>
{
    public const string PrimaryKey = "pk_queue_operations";

    public void Configure(EntityTypeBuilder<QueueOperation> builder)
    {
        builder.ToTable("queue_operations", table =>
        {
            table.HasCheckConstraint("ck_queue_operations_kind", "kind IN (1, 2, 3, 4, 5, 6, 7)");
            table.HasCheckConstraint("ck_queue_operations_state", "state IN (1, 2, 3, 4, 5)");

            // O estado e os dados dele andam juntos: a operação pendente ainda não terminou nem tem resposta; a cancelada guarda a regra
            // que recusou e nenhuma resposta; as demais terminaram e guardam a resposta dada ao autor. Cada ramo vale TRUE ou FALSE,
            // nunca NULL (um CHECK aceita NULL).
            table.HasCheckConstraint(
                "ck_queue_operations_completion",
                "(state = 1 AND completed_at IS NULL AND snapshot IS NULL AND failure_rule IS NULL) " +
                "OR (state = 4 AND completed_at IS NOT NULL AND snapshot IS NULL AND failure_rule IS NOT NULL) " +
                "OR (state IN (2, 3, 5) AND completed_at IS NOT NULL AND snapshot IS NOT NULL AND failure_rule IS NULL)");
        });

        // O identificador do pedido é único POR TENANT: dois pedidos concorrentes com o mesmo identificador disputam esta chave, e só um grava.
        builder.HasKey(o => new { o.TenantId, o.Id }).HasName(PrimaryKey);
        builder.Property(o => o.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(o => o.Id).HasColumnName("operation_id").ValueGeneratedNever();
        builder.Property(o => o.ItemId).HasColumnName("item_id");
        builder.Property(o => o.SessionId).HasColumnName("session_id");
        builder.Property(o => o.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(o => o.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(o => o.QueueId).HasColumnName("queue_id");
        builder.Property(o => o.TargetUserId).HasColumnName("target_user_id");
        builder.Property(o => o.State).HasColumnName("state").HasConversion<short>();
        builder.Property(o => o.FailureRule).HasColumnName("failure_rule");
        builder.Property(o => o.Snapshot).HasColumnName("snapshot");
        builder.Property(o => o.CreatedAt).HasColumnName("created_at");
        builder.Property(o => o.CompletedAt).HasColumnName("completed_at");

        builder.Ignore(o => o.IsPending);

        // A referência ao item carrega o tenant: o banco recusa recibo ligado ao item de outro tenant.
        builder.HasOne<QueueItem>().WithMany().HasForeignKey(o => new { o.TenantId, o.ItemId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QueueItemHistoryConfiguration : IEntityTypeConfiguration<QueueItemHistory>
{
    public const string OperationIndex = "ux_queue_item_history_operation";

    public void Configure(EntityTypeBuilder<QueueItemHistory> builder)
    {
        builder.ToTable("queue_item_history", table =>
        {
            table.HasCheckConstraint("ck_queue_item_history_kind", "kind IN (1, 2, 3, 4, 5, 6, 7, 8)");
            table.HasCheckConstraint("ck_queue_item_history_priority", "(from_priority IS NULL OR from_priority BETWEEN 0 AND 9) AND (to_priority IS NULL OR to_priority BETWEEN 0 AND 9)");
        });

        builder.HasKey(h => new { h.TenantId, h.Id });
        builder.Property(h => h.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(h => h.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(h => h.ItemId).HasColumnName("item_id");
        builder.Property(h => h.SessionId).HasColumnName("session_id");
        builder.Property(h => h.OperationId).HasColumnName("operation_id");
        builder.Property(h => h.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(h => h.FromQueueId).HasColumnName("from_queue_id");
        builder.Property(h => h.ToQueueId).HasColumnName("to_queue_id");
        builder.Property(h => h.FromUserId).HasColumnName("from_user_id");
        builder.Property(h => h.ToUserId).HasColumnName("to_user_id");
        builder.Property(h => h.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(h => h.Reason).HasColumnName("reason").HasMaxLength(QueueItem.ReasonMaxLength).IsRequired();
        builder.Property(h => h.StateVersionAfter).HasColumnName("state_version_after");
        builder.Property(h => h.OccurredAt).HasColumnName("occurred_at");
        builder.Property(h => h.FromPriority).HasColumnName("from_priority");
        builder.Property(h => h.ToPriority).HasColumnName("to_priority");

        // A referência ao item carrega o tenant: o banco recusa histórico ligado ao item de outro tenant.
        builder.HasOne<QueueItem>().WithMany().HasForeignKey(h => new { h.TenantId, h.ItemId }).OnDelete(DeleteBehavior.Restrict);

        // Cada operação deixa no máximo um registro: é a garantia de idempotência do histórico.
        builder.HasIndex(h => new { h.TenantId, h.OperationId }).IsUnique().HasDatabaseName(OperationIndex);

        // A leitura do histórico de um item, na ordem em que aconteceu.
        builder.HasIndex(h => new { h.TenantId, h.ItemId, h.OccurredAt, h.Id }).HasDatabaseName("ix_queue_item_history_tenant_id_item_id_occurred_at_id");
    }
}
