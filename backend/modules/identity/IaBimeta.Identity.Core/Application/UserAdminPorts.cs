using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>A política de usuários do tenant. <see cref="IsExplicit"/> falso é a ausência de linha: empresa anterior à Etapa 01 (ilimitada e habilitada).</summary>
public sealed record UserPolicy(int? MaxUsers, bool RegistrationEnabled, bool IsExplicit)
{
    public static UserPolicy Legacy { get; } = new(null, true, false);
}

/// <summary>As vagas do tenant agora. <see cref="SeatsUsed"/> = ativos + admissões pendentes com reserva viva: é o que o limite contratado limita.</summary>
public sealed record SeatUsage(int Active, int Inactive, int PendingReserved, int PendingExpired, int Cancelled)
{
    public int SeatsUsed => Active + PendingReserved;
}

/// <summary>O usuário com tudo o que a administração precisa para decidir: concessões, ajustes individuais e se a admissão ainda tem reserva viva.</summary>
public sealed record UserDetail(
    User User,
    IReadOnlyList<UserOrganizationGrant> Grants,
    IReadOnlyList<UserPermissionOverride> Overrides,
    bool HasLiveFirstAccessToken,
    bool HasActiveExternalIdentity)
{
    /// <summary>A vaga que o usuário ocupa: ativo (federado só com vínculo externo ativo) ou pendente com token vivo.</summary>
    public bool OccupiesSeat => User.Status switch
    {
        UserStatus.Active => User.AuthMode != UserAuthMode.Federated || HasActiveExternalIdentity,
        UserStatus.Pending => HasLiveFirstAccessToken,
        _ => false,
    };

    /// <summary>A situação como a interface mostra: o pendente sem reserva viva é "expirado".</summary>
    public string DisplayStatus => User.Status switch
    {
        UserStatus.Pending => HasLiveFirstAccessToken ? "Pending" : "Expired",
        UserStatus.Active when User.AuthMode == UserAuthMode.Federated && !HasActiveExternalIdentity => "Inactive",
        var s => s.ToString(),
    };
}

public sealed record UserListQuery(
    string? Search,
    Role? Role,
    Guid? OrganizationId,
    string? Status,
    int Page,
    int PageSize,
    IReadOnlySet<Guid>? VisibleOrganizations);

public sealed record UserPage(IReadOnlyList<UserDetail> Items, int Total);

/// <summary>
/// O acesso a dados da administração de usuários (Etapa 01). <see cref="RunExclusiveAsync{T}"/> abre uma transação e toma a trava consultiva de admissão do tenant: tudo que muda vagas ou administradores corre dentro dela, serializado
/// por tenant; uma exceção desfaz a transação inteira.
/// </summary>
public interface IUserAdminStore
{
    /// <summary>Roda a ação dentro de uma transação com a trava de admissão do tenant do contexto. Reentrante: se já há transação, só reaproveita (a trava é do tenant e vale até o fim dela).</summary>
    Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);

    /// <summary>O mesmo, para o tenant informado (autenticação anônima: primeiro acesso).</summary>
    Task<T> RunExclusiveAsync<T>(TenantId tenantId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);

    Task<SeatUsage> GetSeatUsageAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>A política de usuários do tenant, lida na MESMA conexão e transação do chamador (quando há trava aberta, sob ela). Sem linha: <see cref="UserPolicy.Legacy"/>.</summary>
    Task<UserPolicy> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Grava a política sob a trava de admissão do tenant (na transação do chamador, se já houver uma). Devolve a anterior e a atual.</summary>
    Task<(UserPolicy Previous, UserPolicy Current)> SetPolicyAsync(TenantId tenantId, int? maxUsers, bool registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Altera SÓ os campos pedidos sobre a política relida DENTRO da trava (uma alteração do limite nunca desfaz uma alteração independente da habilitação, e vice-versa). Sem linha, parte da política legada.</summary>
    Task<(UserPolicy Previous, UserPolicy Current)> ChangePolicyAsync(TenantId tenantId, bool changeMaxUsers, int? maxUsers, bool? registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Cria a política só se AINDA não existe (provisionamento repetível). Devolve <c>true</c> se criou.</summary>
    Task<bool> EnsurePolicyAsync(TenantId tenantId, int? maxUsers, bool registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken);

    Task<UserPage> ListUsersAsync(UserListQuery query, DateTimeOffset now, CancellationToken cancellationToken);

    Task<UserDetail?> GetDetailAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Os usuários com ao menos uma concessão de escopo do tenant (candidatos a administrador).</summary>
    Task<IReadOnlyList<UserDetail>> ListTenantScopeUsersAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>O ajuste do usuário no escopo e permissão, já rastreado para alteração.</summary>
    Task<UserPermissionOverride?> FindOverrideAsync(Guid userId, Guid? organizationId, Permission permission, CancellationToken cancellationToken);

    Task AddOverrideAsync(UserPermissionOverride item, CancellationToken cancellationToken);

    Task RemoveOverrideAsync(UserPermissionOverride item, CancellationToken cancellationToken);

    /// <summary>Remove todos os ajustes do usuário num escopo (a organização, ou o tenant com nulo). Devolve quantos removeu.</summary>
    Task<int> RemoveOverridesAsync(Guid userId, Guid? organizationId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>A admissão foi recusada porque o tenant não admite novos usuários (habilitação desligada pela plataforma).</summary>
public sealed class RegistrationDisabledException : Exception
{
    public RegistrationDisabledException()
        : base("O cadastro de novos usuários está desabilitado para esta empresa. Solicite a habilitação à BImeta.")
    {
    }
}

/// <summary>A operação aumentaria as vagas usadas além do limite contratado.</summary>
public sealed class UserLimitReachedException : Exception
{
    public UserLimitReachedException(int limit, int used)
        : base($"O limite contratado de usuários foi atingido ({used} de {limit}). Solicite a ampliação do limite à BImeta.")
    {
        Limit = limit;
        Used = used;
    }

    public int Limit { get; }

    public int Used { get; }
}

/// <summary>A operação deixaria o tenant sem nenhum administrador ativo e utilizável.</summary>
public sealed class LastAdministratorException : Exception
{
    public LastAdministratorException()
        : base("Esta operação deixaria a empresa sem nenhum administrador ativo. Conceda o acesso administrativo a outro usuário antes.")
    {
    }
}
