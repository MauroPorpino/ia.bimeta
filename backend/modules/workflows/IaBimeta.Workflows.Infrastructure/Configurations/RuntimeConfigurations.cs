using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Workflows.Infrastructure.Configurations;

internal sealed class FlowActivationConfiguration : IEntityTypeConfiguration<FlowActivation>
{
    public void Configure(EntityTypeBuilder<FlowActivation> builder)
    {
        builder.ToTable("flow_activations", table =>
        {
            table.HasCheckConstraint("ck_flow_activations_rollout", "rollout_percent BETWEEN 0 AND 100");
            table.HasCheckConstraint("ck_flow_activations_on_failure", "on_failure IN (1, 2)");
            table.HasCheckConstraint("ck_flow_activations_revision", "revision >= 1");
        });

        builder.HasKey(a => new { a.TenantId, a.Id });
        builder.Property(a => a.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.OrganizationId).HasColumnName("organization_id");
        builder.Property(a => a.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(a => a.ChannelInstanceId).HasColumnName("channel_instance_id");
        builder.Property(a => a.WorkflowKey).HasColumnName("workflow_key").HasMaxLength(80);
        builder.Property(a => a.Enabled).HasColumnName("enabled");
        builder.Property(a => a.Priority).HasColumnName("priority");
        builder.Property(a => a.RolloutPercent).HasColumnName("rollout_percent");
        builder.Property(a => a.OnFailure).HasColumnName("on_failure").HasConversion<short>();
        builder.Property(a => a.CreatedBy).HasColumnName("created_by");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        builder.Property(a => a.Revision).HasColumnName("revision").IsConcurrencyToken();
        builder.Ignore(a => a.Specificity);

        builder.HasIndex(a => new { a.TenantId, a.OrganizationId, a.Enabled }).HasDatabaseName("ix_flow_activations_tenant_org_enabled");
    }
}

internal sealed class RoutingDecisionConfiguration : IEntityTypeConfiguration<RoutingDecision>
{
    public void Configure(EntityTypeBuilder<RoutingDecision> builder)
    {
        builder.ToTable("routing_decisions", table =>
        {
            table.HasCheckConstraint("ck_routing_decisions_owner", "owner IN (1, 2)");
            table.HasCheckConstraint("ck_routing_decisions_revision", "revision >= 1");
        });

        // A chave primária (tenant, sessão) é a barreira: de duas gravações simultâneas da decisão, só uma vence.
        builder.HasKey(d => new { d.TenantId, d.SessionId });
        builder.Property(d => d.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(d => d.SessionId).HasColumnName("session_id").ValueGeneratedNever();
        builder.Property(d => d.Owner).HasColumnName("owner").HasConversion<short>();
        builder.Property(d => d.ActivationId).HasColumnName("activation_id");
        builder.Property(d => d.DecidedAt).HasColumnName("decided_at");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
        builder.Property(d => d.Revision).HasColumnName("revision").IsConcurrencyToken();
    }
}

internal sealed class FlowInstanceConfiguration : IEntityTypeConfiguration<FlowInstance>
{
    public const string ActiveSessionIndex = "ux_flow_instances_active_session";
    public const string DueIndex = "ix_flow_instances_due";

    public void Configure(EntityTypeBuilder<FlowInstance> builder)
    {
        builder.ToTable("flow_instances", table =>
        {
            table.HasCheckConstraint("ck_flow_instances_status", "status BETWEEN 1 AND 9");
            table.HasCheckConstraint("ck_flow_instances_on_failure", "on_failure IN (1, 2)");
            table.HasCheckConstraint("ck_flow_instances_revision", "revision >= 1 AND step >= 0 AND attempts >= 0");
        });

        builder.HasKey(i => new { i.TenantId, i.Id });
        builder.Property(i => i.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(i => i.SessionId).HasColumnName("session_id");
        builder.Property(i => i.OrganizationId).HasColumnName("organization_id");
        builder.Property(i => i.WorkflowVersionId).HasColumnName("workflow_version_id");
        builder.Property(i => i.WorkflowKey).HasColumnName("workflow_key").HasMaxLength(80);
        builder.Property(i => i.VersionNumber).HasColumnName("version_number");
        builder.Property(i => i.ActivationId).HasColumnName("activation_id");
        builder.Property(i => i.OnFailure).HasColumnName("on_failure").HasConversion<short>();
        builder.Property(i => i.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(i => i.NodeId).HasColumnName("node_id").HasMaxLength(64);
        builder.Property(i => i.Attempts).HasColumnName("attempts");
        builder.Property(i => i.Step).HasColumnName("step");
        builder.Property(i => i.WaitUntil).HasColumnName("wait_until");
        builder.Property(i => i.PendingEffectKey).HasColumnName("pending_effect_key").HasMaxLength(32);
        builder.Property(i => i.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        // As variáveis (dado pessoal) ficam CIFRADAS: nenhum valor em claro no banco.
        builder.Property(i => i.VariablesCipher).HasColumnName("variables_cipher");
        builder.Property(i => i.VariablesKeyId).HasColumnName("variables_key_id").HasMaxLength(64);
        builder.Property(i => i.SeenMessageSeq).HasColumnName("seen_message_seq");
        builder.Property(i => i.NextRunAt).HasColumnName("next_run_at");
        builder.Property(i => i.CancelReason).HasColumnName("cancel_reason").HasMaxLength(64);
        builder.Property(i => i.LeaseOwner).HasColumnName("lease_owner").HasMaxLength(200);
        builder.Property(i => i.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(i => i.LeaseToken).HasColumnName("lease_token");
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at");
        builder.Property(i => i.EndedAt).HasColumnName("ended_at");
        builder.Property(i => i.Revision).HasColumnName("revision").IsConcurrencyToken();
        builder.Ignore(i => i.IsTerminal);

        // UMA instância ativa por sessão (os estados terminais são 5 a 9).
        builder.HasIndex(i => new { i.TenantId, i.SessionId }).IsUnique().HasFilter("status < 5").HasDatabaseName(ActiveSessionIndex);
        builder.HasIndex(i => new { i.NextRunAt }).HasFilter("next_run_at IS NOT NULL").HasDatabaseName(DueIndex);
        builder.HasIndex(i => new { i.TenantId, i.SessionId, i.CreatedAt }).HasDatabaseName("ix_flow_instances_tenant_session_created");
    }
}

internal sealed class FlowEffectConfiguration : IEntityTypeConfiguration<FlowEffectRecord>
{
    public const string KeyIndex = "ux_flow_effects_instance_key";

    public void Configure(EntityTypeBuilder<FlowEffectRecord> builder)
    {
        builder.ToTable("flow_effects", table =>
        {
            table.HasCheckConstraint("ck_flow_effects_state", "state BETWEEN 1 AND 6");
            table.HasCheckConstraint("ck_flow_effects_kind", "kind BETWEEN 1 AND 6");
        });

        builder.HasKey(e => new { e.TenantId, e.Id });
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.InstanceId).HasColumnName("instance_id");
        builder.Property(e => e.Key).HasColumnName("key").HasMaxLength(32);
        builder.Property(e => e.Step).HasColumnName("step");
        builder.Property(e => e.Index).HasColumnName("index");
        builder.Property(e => e.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(e => e.NodeId).HasColumnName("node_id").HasMaxLength(64);
        builder.Property(e => e.PayloadCipher).HasColumnName("payload_cipher");
        builder.Property(e => e.PayloadKeyId).HasColumnName("payload_key_id").HasMaxLength(64);
        builder.Property(e => e.State).HasColumnName("state").HasConversion<short>();
        builder.Property(e => e.ResultCode).HasColumnName("result_code").HasMaxLength(64);
        builder.Property(e => e.Attempts).HasColumnName("attempts");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Ignore(e => e.IsOpen);

        // O identificador determinístico dentro da instância: a mesma transição nunca grava o mesmo efeito duas vezes.
        builder.HasIndex(e => new { e.TenantId, e.InstanceId, e.Key }).IsUnique().HasDatabaseName(KeyIndex);
        builder.HasIndex(e => new { e.TenantId, e.InstanceId, e.State }).HasDatabaseName("ix_flow_effects_tenant_instance_state");
    }
}

internal sealed class FlowStepConfiguration : IEntityTypeConfiguration<FlowStepRecord>
{
    public void Configure(EntityTypeBuilder<FlowStepRecord> builder)
    {
        builder.ToTable("flow_steps");
        builder.HasKey(s => new { s.TenantId, s.Id });
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.InstanceId).HasColumnName("instance_id");
        builder.Property(s => s.Step).HasColumnName("step");
        builder.Property(s => s.NodeId).HasColumnName("node_id").HasMaxLength(64);
        builder.Property(s => s.Outcome).HasColumnName("outcome").HasMaxLength(96);
        builder.Property(s => s.At).HasColumnName("at");
        builder.HasIndex(s => new { s.TenantId, s.InstanceId, s.Step }).HasDatabaseName("ix_flow_steps_tenant_instance_step");
        builder.HasIndex(s => new { s.TenantId, s.NodeId, s.At }).HasDatabaseName("ix_flow_steps_tenant_node_at");
    }
}
