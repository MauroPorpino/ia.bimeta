using IaBimeta.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

internal sealed class LoginThrottleConfiguration : IEntityTypeConfiguration<LoginThrottle>
{
    public void Configure(EntityTypeBuilder<LoginThrottle> builder)
    {
        builder.ToTable("login_throttles");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        // Sem chave estrangeira para users: a contagem existe também para e-mail que não existe (não revela o que existe).
        builder.HasKey(t => new { t.TenantId, t.Key });
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.Key).HasColumnName("throttle_key").HasMaxLength(LoginThrottle.KeyMaxLength).IsRequired();
        builder.Property(t => t.FailedCount).HasColumnName("failed_count");
        builder.Property(t => t.WindowStartedAt).HasColumnName("window_started_at");
        builder.Property(t => t.LockedUntil).HasColumnName("locked_until");
    }
}
