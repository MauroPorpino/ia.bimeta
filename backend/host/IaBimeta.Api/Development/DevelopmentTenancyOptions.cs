namespace IaBimeta.Api.Development;

/// <summary>
/// Recursos de desenvolvimento (seção <c>Tenancy:Development</c>). Todos desligados por padrão e proibidos fora do
/// ambiente Development (<see cref="DevelopmentOnlyGuard"/>).
/// </summary>
internal sealed class DevelopmentTenancyOptions
{
    public const string SectionName = "Tenancy:Development";

    /// <summary>Habilita a escolha do tenant pelo cabeçalho X-Dev-Tenant (só loopback).</summary>
    public bool HeaderEnabled { get; set; }

    /// <summary>Aplica as migrations pendentes na partida (só cria esquema e tabelas novas).</summary>
    public bool ApplyMigrations { get; set; }

    /// <summary>Tenants de demonstração cadastrados na partida quando ainda não existem.</summary>
    public List<SeedTenant> SeedTenants { get; set; } = new();

    /// <summary>
    /// Administradores de tenant criados na partida quando ainda não existem (credencial sintética de desenvolvimento).
    /// Nunca existe credencial padrão fora do ambiente Development.
    /// </summary>
    public List<SeedAdministrator> SeedAdministrators { get; set; } = new();
}

internal sealed class SeedTenant
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

internal sealed class SeedAdministrator
{
    public string TenantSlug { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
