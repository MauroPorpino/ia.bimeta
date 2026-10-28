using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Tenancy.Infrastructure.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        // Defesa no banco: o idioma é um identificador BCP 47 (o aplicativo valida e normaliza; o banco recusa lixo como pt_BR).
        builder.ToTable("tenants", table => table.HasCheckConstraint("ck_tenants_default_locale", "default_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$'"));

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.Slug).HasColumnName("slug").HasMaxLength(Tenant.SlugMaxLength).IsRequired();
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.DefaultLocale).HasColumnName("default_locale").HasMaxLength(Locale.MaxLength).HasConversion(new LocaleConverter()).IsRequired();

        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("ix_tenants_slug");
    }
}
