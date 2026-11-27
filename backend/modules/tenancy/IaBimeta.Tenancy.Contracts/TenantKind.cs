namespace IaBimeta.Tenancy.Contracts;

/// <summary>
/// Natureza do tenant (ADR 0043). <see cref="Commercial"/> é uma empresa contratante. <see cref="Platform"/> é o tenant técnico reservado,
/// único, que abriga só os operadores da BImeta: não é empresa contratante, não conta nos indicadores comerciais e só ele aceita papéis e
/// permissões da plataforma.
/// </summary>
public enum TenantKind
{
    Commercial = 1,
    Platform = 2,
}
