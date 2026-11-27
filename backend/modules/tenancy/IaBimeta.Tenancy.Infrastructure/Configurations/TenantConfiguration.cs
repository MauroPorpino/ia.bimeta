using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
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
        builder.Property(t => t.Kind).HasColumnName("kind").HasConversion<short>().HasDefaultValue(TenantKind.Commercial).IsRequired();
        builder.Property(t => t.LegalName).HasColumnName("legal_name").HasMaxLength(160);
        builder.Property(t => t.Document).HasColumnName("document").HasMaxLength(14);
        builder.Property(t => t.ContactName).HasColumnName("contact_name").HasMaxLength(120);
        builder.Property(t => t.ContactEmail).HasColumnName("contact_email").HasMaxLength(254);
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        builder.Property(t => t.SuspendedAt).HasColumnName("suspended_at");
        builder.Property(t => t.ReactivatedAt).HasColumnName("reactivated_at");

        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("ix_tenants_slug");

        // Um só tenant técnico da plataforma (ADR 0043).
        builder.HasIndex(t => t.Kind).IsUnique().HasFilter("kind = 2").HasDatabaseName("ux_tenants_platform");
    }
}

internal sealed class TenantStatusChangeConfiguration : IEntityTypeConfiguration<TenantStatusChange>
{
    public void Configure(EntityTypeBuilder<TenantStatusChange> builder)
    {
        builder.ToTable("tenant_status_changes");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter());
        builder.Property(c => c.FromStatus).HasColumnName("from_status").HasConversion<short>();
        builder.Property(c => c.ToStatus).HasColumnName("to_status").HasConversion<short>();
        builder.Property(c => c.Reason).HasColumnName("reason").HasMaxLength(Tenant.ReasonMaxLength).IsRequired();
        builder.Property(c => c.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(c => c.ChangedAt).HasColumnName("changed_at");
        builder.HasIndex(c => new { c.TenantId, c.ChangedAt }).HasDatabaseName("ix_tenant_status_changes_tenant");
    }
}
