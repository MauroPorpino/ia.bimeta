using IaBimeta.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

/// <summary>Dado da PLATAFORMA (uma linha por tenant): sem filtro global de tenant; toda consulta leva o tenant explícito e a gravação roda sob a trava de admissão.</summary>
internal sealed class TenantUserPolicyConfiguration : IEntityTypeConfiguration<TenantUserPolicy>
{
    public void Configure(EntityTypeBuilder<TenantUserPolicy> builder)
    {
        builder.ToTable("tenant_user_policies", table => table.HasCheckConstraint("ck_tenant_user_policies_max_users", "max_users IS NULL OR max_users >= 1"));

        builder.HasKey(p => p.TenantId);
        builder.Property(p => p.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(p => p.MaxUsers).HasColumnName("max_users");
        builder.Property(p => p.RegistrationEnabled).HasColumnName("registration_enabled");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
    }
}
