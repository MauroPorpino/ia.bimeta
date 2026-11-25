using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

internal sealed class UserPermissionOverrideConfiguration : IEntityTypeConfiguration<UserPermissionOverride>
{
    public const string UniqueScopeIndex = "ix_user_permission_overrides_tenant_user_scope_permission";

    public void Configure(EntityTypeBuilder<UserPermissionOverride> builder)
    {
        builder.ToTable("user_permission_overrides");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(o => new { o.TenantId, o.Id });
        builder.Property(o => o.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(o => o.UserId).HasColumnName("user_id");
        builder.Property(o => o.OrganizationId).HasColumnName("organization_id");
        builder.Property(o => o.Permission).HasColumnName("permission").HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(o => o.Effect).HasColumnName("effect").HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(o => o.CreatedAt).HasColumnName("created_at");
        builder.Property(o => o.CreatedByUserId).HasColumnName("created_by_user_id");

        builder.HasOne<User>().WithMany().HasForeignKey(o => new { o.TenantId, o.UserId }).OnDelete(DeleteBehavior.Cascade);

        // Um ajuste por usuário, escopo e permissão (o escopo de tenant, com organização nula, também é único).
        builder.HasIndex(o => new { o.TenantId, o.UserId, o.OrganizationId, o.Permission })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName(UniqueScopeIndex);
    }
}
