namespace IaBimeta.Audit.Contracts;

/// <summary>
/// O que registrar. Tenant, ator e data NÃO fazem parte da entrada: vêm do contexto confiável e do relógio.
/// Não há campo de texto livre: a trilha nunca guarda senha, token nem segredo.
/// </summary>
/// <param name="Operation">Nome estável da operação, em minúsculas (por exemplo, <c>identity.login</c>).</param>
/// <param name="ResourceType">Tipo do recurso afetado (por exemplo, <c>user</c>).</param>
/// <param name="ResourceId">Identificador do recurso, quando há. Apenas letras, números e <c>_ . : -</c>.</param>
public sealed record AuditEntry(string Operation, string ResourceType, string? ResourceId, AuditOutcome Outcome);
