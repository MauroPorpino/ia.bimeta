using IaBimeta.ContactCenter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.ContactCenter.Infrastructure.Configurations;

internal sealed class RoutingRuleConfiguration : IEntityTypeConfiguration<RoutingRule>
{
    public void Configure(EntityTypeBuilder<RoutingRule> builder)
    {
        builder.ToTable("routing_rules", table =>
        {
            table.HasCheckConstraint("ck_routing_rules_priority", "priority BETWEEN 0 AND 9");
            table.HasCheckConstraint("ck_routing_rules_version", "version >= 1");

            // O setor da regra só existe dentro de uma unidade.
            table.HasCheckConstraint("ck_routing_rules_department", "department_id IS NULL OR business_unit_id IS NOT NULL");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(r => new { r.TenantId, r.Id });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.OrganizationId).HasColumnName("organization_id");
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(RoutingRule.NameMaxLength).IsRequired();
        builder.Property(r => r.ChannelInstanceId).HasColumnName("channel_instance_id");
        builder.Property(r => r.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(r => r.DepartmentId).HasColumnName("department_id");
        builder.Property(r => r.QueueId).HasColumnName("queue_id");
        builder.Property(r => r.Priority).HasColumnName("priority");
        builder.Property(r => r.Order).HasColumnName("sort_order");
        builder.Property(r => r.Enabled).HasColumnName("enabled");
        builder.Property(r => r.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        builder.Ignore(r => r.Specificity);

        // A fila de destino carrega o tenant: o banco recusa regra apontando para a fila de outro tenant. O canal, a unidade e o
        // setor são opacos: o dono do cadastro é outro módulo (DAD-2).
        builder.HasOne<ServiceQueue>().WithMany().HasForeignKey(r => new { r.TenantId, r.QueueId }).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TenantId, r.OrganizationId, r.Enabled, r.Order }).HasDatabaseName("ix_routing_rules_tenant_id_organization_id_enabled_sort_order");
        builder.HasIndex(r => new { r.TenantId, r.QueueId }).HasDatabaseName("ix_routing_rules_tenant_id_queue_id");
    }
}

internal sealed class QueueScheduleWindowConfiguration : IEntityTypeConfiguration<QueueScheduleWindow>
{
    public void Configure(EntityTypeBuilder<QueueScheduleWindow> builder)
    {
        builder.ToTable("queue_schedule_windows", table =>
        {
            table.HasCheckConstraint("ck_queue_schedule_windows_day", "day_of_week BETWEEN 0 AND 6");
            table.HasCheckConstraint("ck_queue_schedule_windows_minutes", "start_minute >= 0 AND end_minute <= 1440 AND end_minute > start_minute");
        });

        builder.HasKey(w => new { w.TenantId, w.QueueId, w.DayOfWeek, w.StartMinute });
        builder.Property(w => w.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(w => w.QueueId).HasColumnName("queue_id");
        builder.Property(w => w.DayOfWeek).HasColumnName("day_of_week").HasConversion<short>();
        builder.Property(w => w.StartMinute).HasColumnName("start_minute");
        builder.Property(w => w.EndMinute).HasColumnName("end_minute");

        builder.HasOne<ServiceQueue>().WithMany().HasForeignKey(w => new { w.TenantId, w.QueueId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QueueClosureConfiguration : IEntityTypeConfiguration<QueueClosure>
{
    public void Configure(EntityTypeBuilder<QueueClosure> builder)
    {
        builder.ToTable("queue_closures");

        builder.HasKey(c => new { c.TenantId, c.QueueId, c.Date });
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(c => c.QueueId).HasColumnName("queue_id");
        builder.Property(c => c.Date).HasColumnName("closed_on");

        builder.HasOne<ServiceQueue>().WithMany().HasForeignKey(c => new { c.TenantId, c.QueueId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AgentPresenceConfiguration : IEntityTypeConfiguration<AgentPresence>
{
    public void Configure(EntityTypeBuilder<AgentPresence> builder)
    {
        builder.ToTable("agent_presences", table =>
        {
            table.HasCheckConstraint("ck_agent_presences_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_agent_presences_version", "version >= 1");
        });

        // Uma presença por atendente. O usuário é opaco: o Identity é o dono (DAD-2).
        builder.HasKey(p => new { p.TenantId, p.UserId });
        builder.Property(p => p.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(p => p.UserId).HasColumnName("user_id");
        builder.Property(p => p.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(p => p.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
    }
}
