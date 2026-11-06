using IaBimeta.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("password_reset_tokens");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(t => new { t.TenantId, t.Id });
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.UserId).HasColumnName("user_id");
        builder.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(PasswordResetToken.TokenHashLength).IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        builder.Property(t => t.IssuedByUserId).HasColumnName("issued_by_user_id");
        builder.Property(t => t.UsedAt).HasColumnName("used_at");
        builder.Property(t => t.RevokedAt).HasColumnName("revoked_at");

        builder.HasOne<User>().WithMany().HasForeignKey(t => new { t.TenantId, t.UserId }).OnDelete(DeleteBehavior.Cascade);

        // O token apresentado é achado pelo hash (único na tabela inteira); o usuário e o tenant conferem depois.
        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ix_password_reset_tokens_token_hash");
        builder.HasIndex(t => new { t.TenantId, t.UserId }).HasDatabaseName("ix_password_reset_tokens_tenant_id_user_id");
    }
}
