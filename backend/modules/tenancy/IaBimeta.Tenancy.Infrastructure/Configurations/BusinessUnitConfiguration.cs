using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Tenancy.Infrastructure.Configurations;

internal sealed class BusinessUnitConfiguration : IEntityTypeConfiguration<BusinessUnit>
{
    public const string UniqueNameIndex = "ix_business_units_tenant_id_organization_id_name";
    public const string SingleHeadquartersIndex = "ix_business_units_one_headquarters";

    public void Configure(EntityTypeBuilder<BusinessUnit> builder)
    {
        builder.ToTable("business_units");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(u => new { u.TenantId, u.Id });
        builder.Property(u => u.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(u => u.OrganizationId).HasColumnName("organization_id");
        builder.Property(u => u.Name).HasColumnName("name").HasMaxLength(BusinessUnit.NameMaxLength).IsRequired();
        builder.Property(u => u.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");

        // A referência carrega o tenant: o banco recusa uma unidade ligada à organização de outro tenant.
        builder.HasOne<Organization>().WithMany().HasForeignKey(u => new { u.TenantId, u.OrganizationId }).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(u => new { u.TenantId, u.OrganizationId, u.Name }).IsUnique().HasDatabaseName(UniqueNameIndex);

        // Uma organização tem no máximo uma matriz (Headquarters = 1).
        builder.HasIndex(u => new { u.TenantId, u.OrganizationId }).IsUnique().HasFilter("kind = 1").HasDatabaseName(SingleHeadquartersIndex);
    }
}
