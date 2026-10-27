using IaBimeta.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public const string UniqueEmailIndex = "ix_users_tenant_id_email";

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(u => new { u.TenantId, u.Id });
        builder.Property(u => u.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(User.DisplayNameMaxLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(User.PasswordHashMaxLength).IsRequired();
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(u => new { u.TenantId, u.Email }).IsUnique().HasDatabaseName(UniqueEmailIndex);
    }
}
