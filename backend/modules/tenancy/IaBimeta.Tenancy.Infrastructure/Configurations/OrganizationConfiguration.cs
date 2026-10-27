using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Tenancy.Infrastructure.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(o => new { o.TenantId, o.Id });
        builder.Property(o => o.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(o => o.Name).HasColumnName("name").HasMaxLength(Organization.NameMaxLength).IsRequired();
        builder.Property(o => o.CreatedAt).HasColumnName("created_at");

        // Chave estrangeira dentro do próprio esquema (DAD-2 só proíbe entre esquemas): o banco recusa organização de tenant inexistente.
        builder.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(o => new { o.TenantId, o.Name }).IsUnique().HasDatabaseName("ix_organizations_tenant_id_name");
    }
}
