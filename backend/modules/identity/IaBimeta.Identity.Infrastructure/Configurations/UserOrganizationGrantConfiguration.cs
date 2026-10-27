using IaBimeta.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

internal sealed class UserOrganizationGrantConfiguration : IEntityTypeConfiguration<UserOrganizationGrant>
{
    public const string UniqueScopeIndex = "ix_user_organization_grants_tenant_user_scope";

    public void Configure(EntityTypeBuilder<UserOrganizationGrant> builder)
    {
        builder.ToTable("user_organization_grants");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(g => new { g.TenantId, g.Id });
        builder.Property(g => g.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(g => g.UserId).HasColumnName("user_id");
        builder.Property(g => g.OrganizationId).HasColumnName("organization_id");
        builder.Property(g => g.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(g => g.GrantedAt).HasColumnName("granted_at");
        builder.Property(g => g.GrantedByUserId).HasColumnName("granted_by_user_id");

        // A referência ao usuário carrega o tenant. A organização pertence a outro esquema (Tenancy): não há chave
        // estrangeira entre esquemas (DAD-2), e a existência dela é validada pelo caso de uso.
        builder.HasOne<User>().WithMany().HasForeignKey(g => new { g.TenantId, g.UserId }).OnDelete(DeleteBehavior.Cascade);

        // Uma concessão por usuário e escopo; o escopo de tenant (organização nula) também é único.
        builder.HasIndex(g => new { g.TenantId, g.UserId, g.OrganizationId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName(UniqueScopeIndex);

        builder.HasIndex(g => new { g.TenantId, g.OrganizationId }).HasDatabaseName("ix_user_organization_grants_tenant_id_organization_id");
    }
}
