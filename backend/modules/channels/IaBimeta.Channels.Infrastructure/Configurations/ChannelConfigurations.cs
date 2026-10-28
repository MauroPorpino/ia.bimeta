using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Channels.Infrastructure.Configurations;

internal sealed class ChannelAccountConfiguration : IEntityTypeConfiguration<ChannelAccount>
{
    public const string UniqueProviderAccountIndex = "ux_channel_accounts_provider_account_id";

    public void Configure(EntityTypeBuilder<ChannelAccount> builder)
    {
        builder.ToTable("channel_accounts", table => table.HasCheckConstraint("ck_channel_accounts_provider", "provider IN (1)"));

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(a => new { a.TenantId, a.Id });
        builder.Property(a => a.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.OrganizationId).HasColumnName("organization_id");
        builder.Property(a => a.Provider).HasColumnName("provider").HasConversion<short>();
        builder.Property(a => a.ProviderAccountId).HasColumnName("provider_account_id").HasMaxLength(ProviderIdentifier.MaxLength).IsRequired();
        builder.Property(a => a.Name).HasColumnName("name").HasMaxLength(ChannelAccount.NameMaxLength).IsRequired();
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        // O identificador da conta no provedor é único na plataforma: uma conta do provedor não serve a dois tenants.
        builder.HasIndex(a => new { a.Provider, a.ProviderAccountId }).IsUnique().HasDatabaseName(UniqueProviderAccountIndex);
        builder.HasIndex(a => new { a.TenantId, a.OrganizationId }).HasDatabaseName("ix_channel_accounts_tenant_id_organization_id");
    }
}

internal sealed class ChannelInstanceConfiguration : IEntityTypeConfiguration<ChannelInstance>
{
    public const string UniquePhoneNumberIndex = "ux_channel_instances_provider_phone_number_id";
    public const string RouteKeyName = "ak_channel_instances_route_key";

    public void Configure(EntityTypeBuilder<ChannelInstance> builder)
    {
        builder.ToTable("channel_instances", table =>
        {
            table.HasCheckConstraint("ck_channel_instances_provider", "provider IN (1)");
            table.HasCheckConstraint("ck_channel_instances_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_channel_instances_version", "version >= 1");

            // O setor padrão só existe dentro da unidade padrão.
            table.HasCheckConstraint("ck_channel_instances_default_department", "default_department_id IS NULL OR default_business_unit_id IS NOT NULL");
            table.HasCheckConstraint("ck_channel_instances_default_locale", $"default_locale IS NULL OR default_locale ~ {LocaleConverter.SqlPattern}");
        });

        builder.HasKey(i => new { i.TenantId, i.Id });
        builder.Property(i => i.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(i => i.Id).HasColumnName("id").HasConversion(new ChannelInstanceIdConverter()).ValueGeneratedNever();
        builder.Property(i => i.AccountId).HasColumnName("account_id");
        builder.Property(i => i.OrganizationId).HasColumnName("organization_id");
        builder.Property(i => i.Provider).HasColumnName("provider").HasConversion<short>();
        builder.Property(i => i.PhoneNumberId).HasColumnName("phone_number_id").HasMaxLength(ProviderIdentifier.MaxLength).IsRequired();
        builder.Property(i => i.DisplayPhoneNumber).HasColumnName("display_phone_number").HasMaxLength(ChannelInstance.DisplayPhoneMaxLength);
        builder.Property(i => i.Name).HasColumnName("name").HasMaxLength(ChannelInstance.NameMaxLength).IsRequired();
        builder.Property(i => i.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(i => i.DefaultLocale).HasColumnName("default_locale").HasMaxLength(Locale.MaxLength).HasConversion(typeof(LocaleConverter));
        builder.Property(i => i.DefaultBusinessUnitId).HasColumnName("default_business_unit_id");
        builder.Property(i => i.DefaultDepartmentId).HasColumnName("default_department_id");
        builder.Property(i => i.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at");

        // A referência à conta carrega o tenant: o banco recusa número ligado à conta de outro tenant.
        builder.HasOne<ChannelAccount>().WithMany().HasForeignKey(i => new { i.TenantId, i.AccountId }).OnDelete(DeleteBehavior.Restrict);

        // As unidades autorizadas são parte do agregado: sair da lista apaga a linha.
        builder.HasMany(i => i.AuthorizedUnits).WithOne().HasForeignKey(u => new { u.TenantId, u.ChannelInstanceId }).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.AuthorizedUnits).HasField("_authorizedUnits").UsePropertyAccessMode(PropertyAccessMode.Field);

        // O número no provedor é único na plataforma toda: um número não pode servir a dois tenants.
        builder.HasIndex(i => new { i.Provider, i.PhoneNumberId }).IsUnique().HasDatabaseName(UniquePhoneNumberIndex);

        // A chave que a rota de entrada referencia: amarra a rota ao mesmo tenant e ao mesmo identificador do número.
        builder.HasAlternateKey(i => new { i.TenantId, i.Id, i.Provider, i.PhoneNumberId }).HasName(RouteKeyName);

        builder.HasIndex(i => new { i.TenantId, i.OrganizationId }).HasDatabaseName("ix_channel_instances_tenant_id_organization_id");
    }
}

internal sealed class ChannelInstanceUnitConfiguration : IEntityTypeConfiguration<ChannelInstanceUnit>
{
    public void Configure(EntityTypeBuilder<ChannelInstanceUnit> builder)
    {
        builder.ToTable("channel_instance_units");

        // Uma unidade aparece uma vez por canal. A unidade é opaca: o Tenancy é o dono, sem chave estrangeira entre esquemas.
        builder.HasKey(u => new { u.TenantId, u.ChannelInstanceId, u.BusinessUnitId });
        builder.Property(u => u.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(u => u.ChannelInstanceId).HasColumnName("channel_instance_id").HasConversion(new ChannelInstanceIdConverter()).ValueGeneratedNever();
        builder.Property(u => u.BusinessUnitId).HasColumnName("business_unit_id").ValueGeneratedNever();
        builder.Property(u => u.AuthorizedBy).HasColumnName("authorized_by");
        builder.Property(u => u.AuthorizedAt).HasColumnName("authorized_at");

        // "Quais canais atendem esta unidade", por exemplo ao encerrar ou reestruturar uma filial.
        builder.HasIndex(u => new { u.TenantId, u.BusinessUnitId }).HasDatabaseName("ix_channel_instance_units_tenant_id_business_unit_id");
    }
}

internal sealed class ChannelRouteConfiguration : IEntityTypeConfiguration<ChannelRoute>
{
    public const string UniqueRouteIndex = "ux_channel_routes_provider_external_id";

    public void Configure(EntityTypeBuilder<ChannelRoute> builder)
    {
        builder.ToTable("channel_routes", table => table.HasCheckConstraint("ck_channel_routes_provider", "provider IN (1)"));

        // TEN-3: a chave começa por tenant_id. A consulta de entrada (sem tenant) usa o índice único global abaixo.
        builder.HasKey(r => new { r.TenantId, r.Provider, r.ExternalId });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(r => r.Provider).HasColumnName("provider").HasConversion<short>().ValueGeneratedNever();
        builder.Property(r => r.ExternalId).HasColumnName("external_id").HasMaxLength(ProviderIdentifier.MaxLength).ValueGeneratedNever();
        builder.Property(r => r.ChannelInstanceId).HasColumnName("channel_instance_id").HasConversion(new ChannelInstanceIdConverter());
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        // Integridade garantida pelo banco: a rota é da instância E do mesmo identificador de número, no mesmo tenant. Não pode
        // apontar para outro tenant nem divergir do cadastro. Uma rota por instância.
        builder.HasOne<ChannelInstance>().WithOne()
            .HasForeignKey<ChannelRoute>(r => new { r.TenantId, r.ChannelInstanceId, r.Provider, r.ExternalId })
            .HasPrincipalKey<ChannelInstance>(i => new { i.TenantId, i.Id, i.Provider, i.PhoneNumberId })
            .OnDelete(DeleteBehavior.Restrict);

        // O identificador do provedor é único na plataforma: é por ele que o webhook acha o tenant.
        builder.HasIndex(r => new { r.Provider, r.ExternalId }).IsUnique().HasDatabaseName(UniqueRouteIndex);
    }
}

internal sealed class ChannelSecretConfiguration : IEntityTypeConfiguration<ChannelSecret>
{
    public const string UniqueKindIndex = "ux_channel_secrets_tenant_id_account_id_kind";

    public void Configure(EntityTypeBuilder<ChannelSecret> builder)
    {
        builder.ToTable("channel_secrets", table =>
        {
            table.HasCheckConstraint("ck_channel_secrets_kind", "kind IN (1, 2, 3)");
            table.HasCheckConstraint("ck_channel_secrets_version", "version >= 1");
            table.HasCheckConstraint("ck_channel_secrets_ciphertext", "char_length(key_id) >= 1 AND char_length(ciphertext) BETWEEN 1 AND 16384");
        });

        builder.HasKey(s => new { s.TenantId, s.Id });
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.AccountId).HasColumnName("account_id");
        builder.Property(s => s.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(s => s.KeyId).HasColumnName("key_id").HasMaxLength(64).IsRequired();

        // Só o texto cifrado fica no banco. A chave mestra nunca.
        builder.Property(s => s.Ciphertext).HasColumnName("ciphertext").HasMaxLength(16384).IsRequired();
        builder.Property(s => s.Version).HasColumnName("version");
        builder.Property(s => s.UpdatedBy).HasColumnName("updated_by");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne<ChannelAccount>().WithMany().HasForeignKey(s => new { s.TenantId, s.AccountId }).OnDelete(DeleteBehavior.Restrict);

        // Uma credencial por papel e conta: definir de novo gira o valor, não cria outra referência.
        builder.HasIndex(s => new { s.TenantId, s.AccountId, s.Kind }).IsUnique().HasDatabaseName(UniqueKindIndex);
    }
}
