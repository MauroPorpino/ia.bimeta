using IaBimeta.ContactCenter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.ContactCenter.Infrastructure.Configurations;

internal sealed class ServiceQueueConfiguration : IEntityTypeConfiguration<ServiceQueue>
{
    public const string UniqueNameIndex = "ux_service_queues_organization_name";

    public void Configure(EntityTypeBuilder<ServiceQueue> builder)
    {
        builder.ToTable("service_queues", table =>
        {
            table.HasCheckConstraint("ck_service_queues_status", "status IN (1, 2)");
            table.HasCheckConstraint("ck_service_queues_version", "version >= 1");

            // O setor da fila só existe dentro de uma unidade.
            table.HasCheckConstraint("ck_service_queues_department", "department_id IS NULL OR business_unit_id IS NOT NULL");

            // A política de roteamento: comportamentos válidos, contingência obrigatória quando algum comportamento a usa, e limites.
            table.HasCheckConstraint("ck_service_queues_out_of_hours", "out_of_hours IN (1, 2, 3)");
            table.HasCheckConstraint("ck_service_queues_no_agent", "no_agent IN (1, 2)");
            table.HasCheckConstraint("ck_service_queues_fallback", "(out_of_hours <> 3 AND no_agent <> 2) OR fallback_queue_id IS NOT NULL");
            table.HasCheckConstraint("ck_service_queues_fallback_self", "fallback_queue_id IS NULL OR fallback_queue_id <> id");
            table.HasCheckConstraint("ck_service_queues_default_priority", "default_priority BETWEEN 0 AND 9");
            table.HasCheckConstraint("ck_service_queues_max_items", "max_items_per_agent IS NULL OR max_items_per_agent >= 1");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(q => new { q.TenantId, q.Id });
        builder.Property(q => q.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(q => q.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(q => q.OrganizationId).HasColumnName("organization_id");
        builder.Property(q => q.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(q => q.DepartmentId).HasColumnName("department_id");
        builder.Property(q => q.Name).HasColumnName("name").HasMaxLength(ServiceQueue.NameMaxLength).IsRequired();
        builder.Property(q => q.NameKey).HasColumnName("name_key").HasMaxLength(ServiceQueue.NameMaxLength).IsRequired();
        builder.Property(q => q.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(q => q.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(q => q.CreatedAt).HasColumnName("created_at");
        builder.Property(q => q.UpdatedAt).HasColumnName("updated_at");

        // O nome é único por organização, sem diferenciar maiúsculas de minúsculas.
        builder.HasIndex(q => new { q.TenantId, q.OrganizationId, q.NameKey }).IsUnique().HasDatabaseName(UniqueNameIndex);
        builder.HasIndex(q => new { q.TenantId, q.OrganizationId, q.Status }).HasDatabaseName("ix_service_queues_tenant_id_organization_id_status");

        builder.Property(q => q.TimeZoneId).HasColumnName("time_zone").HasMaxLength(QueueSchedule.TimeZoneMaxLength);
        builder.Property(q => q.OutOfHours).HasColumnName("out_of_hours").HasConversion<short>().HasDefaultValue(OutOfHoursBehavior.Accept);
        builder.Property(q => q.NoAgent).HasColumnName("no_agent").HasConversion<short>().HasDefaultValue(NoAgentBehavior.Wait);
        builder.Property(q => q.FallbackQueueId).HasColumnName("fallback_queue_id");
        builder.Property(q => q.DefaultPriority).HasColumnName("default_priority").HasDefaultValue(0);
        builder.Property(q => q.MaxItemsPerAgent).HasColumnName("max_items_per_agent");

        // A contingência é uma fila do mesmo tenant: a chave carrega o tenant, e o banco recusa contingência de outro.
        builder.HasOne<ServiceQueue>().WithMany().HasForeignKey(q => new { q.TenantId, q.FallbackQueueId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QueueMembershipConfiguration : IEntityTypeConfiguration<QueueMembership>
{
    public void Configure(EntityTypeBuilder<QueueMembership> builder)
    {
        builder.ToTable("queue_memberships", table => table.HasCheckConstraint("ck_queue_memberships_role", "role IN (1, 2)"));

        // Um usuário aparece uma vez por fila. O usuário é opaco: o Identity é o dono, sem chave estrangeira entre esquemas.
        builder.HasKey(m => new { m.TenantId, m.QueueId, m.UserId });
        builder.Property(m => m.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(m => m.QueueId).HasColumnName("queue_id");
        builder.Property(m => m.UserId).HasColumnName("user_id");
        builder.Property(m => m.Role).HasColumnName("role").HasConversion<short>();
        builder.Property(m => m.AddedBy).HasColumnName("added_by");
        builder.Property(m => m.AddedAt).HasColumnName("added_at");

        // A referência à fila carrega o tenant: o banco recusa atendente ligado à fila de outro tenant.
        builder.HasOne<ServiceQueue>().WithMany().HasForeignKey(m => new { m.TenantId, m.QueueId }).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.TenantId, m.UserId }).HasDatabaseName("ix_queue_memberships_tenant_id_user_id");
    }
}
