using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// A política de usuários de um tenant (Etapa 01): o limite contratado de usuários humanos e a habilitação do cadastro de novas pessoas. É dado da PLATAFORMA: só a administração da BImeta o altera (o tenant só consulta). Vive no
/// Identity porque é lido junto com a contagem de vagas, na MESMA conexão, transação e trava de admissão do tenant (sem segunda conexão durante a transação). A existência da linha é a decisão EXPLÍCITA: a ausência de linha é só a
/// compatibilidade das empresas anteriores à Etapa 01 (ilimitadas e habilitadas); empresa comercial nova tem a linha criada ANTES de ter qualquer usuário.
/// </summary>
public sealed class TenantUserPolicy
{
    public const int MaxUsersCeiling = 1_000_000;

    private TenantUserPolicy(TenantId tenantId, int? maxUsers, bool registrationEnabled, DateTimeOffset updatedAt)
    {
        TenantId = tenantId;
        MaxUsers = maxUsers;
        RegistrationEnabled = registrationEnabled;
        UpdatedAt = updatedAt;
    }

    public TenantId TenantId { get; }

    /// <summary>O limite contratado. Nulo = ilimitado, exceção explícita e auditada.</summary>
    public int? MaxUsers { get; private set; }

    public bool RegistrationEnabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static TenantUserPolicy Create(TenantId tenantId, int? maxUsers, bool registrationEnabled, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ValidateMax(maxUsers);
        return new TenantUserPolicy(tenantId, maxUsers, registrationEnabled, now);
    }

    public void Change(int? maxUsers, bool registrationEnabled, DateTimeOffset now)
    {
        ValidateMax(maxUsers);
        MaxUsers = maxUsers;
        RegistrationEnabled = registrationEnabled;
        UpdatedAt = now;
    }

    public static void ValidateMax(int? maxUsers)
    {
        if (maxUsers is < 1 or > MaxUsersCeiling)
        {
            throw new IdentityValidationException($"O limite de usuários deve ficar entre 1 e {MaxUsersCeiling}, ou ser omitido para ilimitado.");
        }
    }
}
