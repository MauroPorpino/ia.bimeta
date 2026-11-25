namespace IaBimeta.Identity.Domain;

/// <summary>
/// A situação de um usuário HUMANO no tenant (Etapa 01 de prontidão). É do usuário (e não de cada organização): o <c>User</c> pertence a um único tenant. Quem acessa o produto é o usuário <see cref="Active"/>;
/// o <see cref="Pending"/> ocupa uma vaga reservada enquanto o token de primeiro acesso vale. Nenhum estado apaga o usuário: histórico e auditoria continuam apontando para ele.
/// </summary>
public enum UserStatus
{
    /// <summary>Acessa o produto e consome uma vaga (se federado, só enquanto houver vínculo externo ativo).</summary>
    Active = 1,

    /// <summary>Sem acesso e sem vaga. Preserva histórico, auditoria e concessões. Pode voltar a ser ativo, se houver vaga.</summary>
    Inactive = 2,

    /// <summary>Admitido, ainda sem primeiro acesso: a credencial é inutilizável e a vaga fica reservada só enquanto o token de primeiro acesso vale (não usado, não revogado, não expirado).</summary>
    Pending = 3,

    /// <summary>Admissão cancelada: nunca acessou, sem vaga. Readmitir é um NOVO ingresso (sujeito à habilitação e ao limite).</summary>
    Cancelled = 4,
}
