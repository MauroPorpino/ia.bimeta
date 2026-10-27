using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Tenancy.Infrastructure.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.Slug).HasColumnName("slug").HasMaxLength(Tenant.SlugMaxLength).IsRequired();
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("ix_tenants_slug");
    }
}
