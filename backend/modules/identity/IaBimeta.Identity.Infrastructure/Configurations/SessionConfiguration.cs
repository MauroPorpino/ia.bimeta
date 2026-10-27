using IaBimeta.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(s => new { s.TenantId, s.Id });
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.UserId).HasColumnName("user_id");
        builder.Property(s => s.TokenHash).HasColumnName("token_hash").HasMaxLength(Session.TokenHashLength).IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.ExpiresAt).HasColumnName("expires_at");
        builder.Property(s => s.RevokedAt).HasColumnName("revoked_at");

        builder.HasOne<User>().WithMany().HasForeignKey(s => new { s.TenantId, s.UserId }).OnDelete(DeleteBehavior.Cascade);

        // O token apresentado é achado pelo hash, antes de existir tenant: o hash é único na tabela inteira.
        builder.HasIndex(s => s.TokenHash).IsUnique().HasDatabaseName("ix_sessions_token_hash");
        builder.HasIndex(s => new { s.TenantId, s.UserId }).HasDatabaseName("ix_sessions_tenant_id_user_id");
    }
}
