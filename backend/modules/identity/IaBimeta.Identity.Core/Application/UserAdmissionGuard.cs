using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>
/// A política ÚNICA de ingresso e de vagas (Etapa 01). Todo caminho que pode aumentar as vagas usadas (cadastro, readmissão, novo token de pendente sem reserva viva, ativação de inativo, provisionamento JIT, reativação de vínculo
/// externo) a consulta DENTRO da trava de admissão (<see cref="IUserAdminStore.RunExclusiveAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>): a política (habilitação e limite) é lida do banco do Identity DEPOIS de obter
/// a trava, na MESMA conexão e transação do ingresso (sem segunda conexão), então uma alteração concorrente da plataforma (que toma a mesma trava) nunca é perdida.
/// </summary>
public sealed class UserAdmissionGuard
{
    private readonly IUserAdminStore _store;

    public UserAdmissionGuard(IUserAdminStore store)
    {
        _store = store;
    }

    /// <summary>Um INGRESSO (pessoa nova ou admissão readmitida): exige o cadastro habilitado e uma vaga. A exceção <paramref name="firstAdministrator"/> dispensa só a habilitação (nunca o limite) e é do provisionamento inicial.</summary>
    /// <exception cref="RegistrationDisabledException">O cadastro está desabilitado.</exception>
    /// <exception cref="UserLimitReachedException">Não há vaga.</exception>
    public async Task RequireIngressAsync(TenantId tenantId, DateTimeOffset now, bool firstAdministrator, CancellationToken cancellationToken)
    {
        var politica = await _store.GetPolicyAsync(tenantId, cancellationToken);
        if (!firstAdministrator && !politica.RegistrationEnabled)
        {
            throw new RegistrationDisabledException();
        }

        await RequireSeatAsync(politica.MaxUsers, tenantId, now, cancellationToken);
    }

    /// <summary>Reativar quem JÁ FOI admitido (inativo que volta, vínculo externo reativado): não é ingresso, então vale mesmo com o cadastro desabilitado, mas exige vaga.</summary>
    /// <exception cref="UserLimitReachedException">Não há vaga.</exception>
    public async Task RequireSeatForReactivationAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var politica = await _store.GetPolicyAsync(tenantId, cancellationToken);
        await RequireSeatAsync(politica.MaxUsers, tenantId, now, cancellationToken);
    }

    /// <summary>Só a habilitação, sem vaga: aceitar uma admissão pendente (a vaga já está reservada) não pode contornar a desabilitação.</summary>
    public async Task<bool> IsRegistrationEnabledAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        (await _store.GetPolicyAsync(tenantId, cancellationToken)).RegistrationEnabled;

    private async Task RequireSeatAsync(int? maxUsers, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (maxUsers is not { } limite)
        {
            return;
        }

        var uso = await _store.GetSeatUsageAsync(tenantId, now, cancellationToken);
        if (uso.SeatsUsed + 1 > limite)
        {
            throw new UserLimitReachedException(limite, uso.SeatsUsed);
        }
    }
}
