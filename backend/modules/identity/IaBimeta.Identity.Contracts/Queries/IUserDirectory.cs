namespace IaBimeta.Identity.Contracts.Queries;

/// <summary>
/// Consulta aos usuários do tenant do contexto atual, para os módulos que referenciam um usuário sem chave estrangeira entre
/// esquemas (por exemplo, o ContactCenter ao colocar um atendente em uma fila). Só diz se o usuário existe no tenant: não expõe
/// dados pessoais, papéis nem credenciais.
/// </summary>
public interface IUserDirectory
{
    /// <summary>Verdadeiro só se o usuário existe no tenant do contexto. Usuário de outro tenant responde como inexistente.</summary>
    /// <exception cref="IaBimeta.SharedKernel.TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken = default);
}
