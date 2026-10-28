using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public const string UniqueEmailIndex = "ix_users_tenant_id_email";

    public void Configure(EntityTypeBuilder<User> builder)
    {
        // Defesa no banco: o idioma preferido, se houver, é um identificador BCP 47 (recusa lixo como pt_BR).
        builder.ToTable("users", table => table.HasCheckConstraint("ck_users_preferred_locale", "preferred_locale IS NULL OR preferred_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$'"));

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(u => new { u.TenantId, u.Id });
        builder.Property(u => u.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(User.DisplayNameMaxLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(User.PasswordHashMaxLength).IsRequired();
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");
        builder.Property(u => u.PreferredLocale).HasColumnName("preferred_locale").HasMaxLength(Locale.MaxLength).HasConversion(typeof(LocaleConverter));

        builder.HasIndex(u => new { u.TenantId, u.Email }).IsUnique().HasDatabaseName(UniqueEmailIndex);
    }
}
