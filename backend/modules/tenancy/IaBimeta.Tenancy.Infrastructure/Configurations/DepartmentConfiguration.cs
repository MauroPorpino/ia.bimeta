using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Tenancy.Infrastructure.Configurations;

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public const string UniqueNameIndex = "ix_departments_tenant_id_business_unit_id_name";

    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(d => new { d.TenantId, d.Id });
        builder.Property(d => d.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(d => d.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(Department.NameMaxLength).IsRequired();
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");

        // A referência carrega o tenant: o banco recusa um setor ligado à unidade de outro tenant.
        builder.HasOne<BusinessUnit>().WithMany().HasForeignKey(d => new { d.TenantId, d.BusinessUnitId }).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => new { d.TenantId, d.BusinessUnitId, d.Name }).IsUnique().HasDatabaseName(UniqueNameIndex);
    }
}
