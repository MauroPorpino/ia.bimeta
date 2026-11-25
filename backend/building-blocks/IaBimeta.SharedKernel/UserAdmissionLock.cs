namespace IaBimeta.SharedKernel;

/// <summary>
/// A chave da trava consultiva (<c>pg_advisory_xact_lock</c>) que serializa, por tenant, tudo o que muda quem pode ENTRAR ou PERMANECER como administrador: cadastro, reserva e liberação de vagas, ativação, desativação,
/// concessões, restrições, provisionamento JIT, e a alteração do limite e da habilitação feita pela plataforma. Uma única chave para as vagas e para o último administrador evita ordem de trava invertida (deadlock).
/// O Identity e o Tenancy a usam em bancos do mesmo servidor; cada um toma a trava dentro da sua própria transação.
/// </summary>
public static class UserAdmissionLock
{
    public static string KeyFor(TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return $"identity.user-admission|{tenantId.Value:N}";
    }
}
