using IaBimeta.SharedKernel;

namespace IaBimeta.Tenancy.Contracts;

/// <summary>Dados públicos do registro de um tenant. Não inclui nenhum dado de organização.</summary>
public sealed record TenantInfo(TenantId TenantId, string Slug, string Name, TenantStatus Status);
