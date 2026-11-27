using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// O acesso a dados da administração de usuários e das vagas. A trava de admissão é uma trava consultiva de TRANSAÇÃO por tenant (<see cref="UserAdmissionLock"/>): sai sozinha no commit ou no rollback, então uma falha nunca deixa o
/// tenant preso. É reentrante por construção: se já existe uma transação aberta no contexto (o chamador já está dentro da trava), só reaproveita.
/// </summary>
internal sealed class UserAdminStore : IUserAdminStore
{
    private readonly IdentityDbContext _db;
    private readonly IRequestContextAccessor _context;

    public UserAdminStore(IdentityDbContext db, IRequestContextAccessor context)
    {
        _db = db;
        _context = context;
    }

    public Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
        RunExclusiveAsync(_context.GetRequired().TenantId, action, cancellationToken);

    public async Task<T> RunExclusiveAsync<T>(TenantId tenantId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken);
        }

        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        var chave = UserAdmissionLock.KeyFor(tenantId);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", cancellationToken);

        var resultado = await action(cancellationToken);
        await transacao.CommitAsync(cancellationToken);
        return resultado;
    }

    public async Task<SeatUsage> GetSeatUsageAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var usuarios = _db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.TenantId == tenantId);

        var ativos = await usuarios.CountAsync(u => u.Status == UserStatus.Active
            && (u.AuthMode != UserAuthMode.Federated
                || _db.ExternalIdentities.IgnoreQueryFilters().Any(i => i.TenantId == tenantId && i.UserId == u.Id && i.Status == ExternalIdentityStatus.Active)), cancellationToken);
        var inativos = await usuarios.CountAsync(u => u.Status == UserStatus.Inactive
            || (u.Status == UserStatus.Active && u.AuthMode == UserAuthMode.Federated
                && !_db.ExternalIdentities.IgnoreQueryFilters().Any(i => i.TenantId == tenantId && i.UserId == u.Id && i.Status == ExternalIdentityStatus.Active)), cancellationToken);
        var reservadas = await usuarios.CountAsync(u => u.Status == UserStatus.Pending
            && _db.PasswordResetTokens.IgnoreQueryFilters().Any(t => t.TenantId == tenantId && t.UserId == u.Id && t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt > now), cancellationToken);
        var pendentes = await usuarios.CountAsync(u => u.Status == UserStatus.Pending, cancellationToken);
        var canceladas = await usuarios.CountAsync(u => u.Status == UserStatus.Cancelled, cancellationToken);

        return new SeatUsage(ativos, inativos, reservadas, pendentes - reservadas, canceladas);
    }

    public async Task<IReadOnlyDictionary<Guid, SeatSummary>> GetSeatSummariesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Agregados de TODAS as empresas em poucas consultas agrupadas (nenhum dado de usuário sai daqui, só contagens): é o que sustenta a visão geral da plataforma sem uma consulta por empresa.
        var usuarios = _db.Users.IgnoreQueryFilters().AsNoTracking();
        var ativosExternos = _db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking().Where(i => i.Status == ExternalIdentityStatus.Active);

        var ativos = await usuarios
            .Where(u => u.Status == UserStatus.Active && (u.AuthMode != UserAuthMode.Federated || ativosExternos.Any(i => i.TenantId == u.TenantId && i.UserId == u.Id)))
            .GroupBy(u => u.TenantId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        var inativos = await usuarios
            .Where(u => u.Status == UserStatus.Inactive || (u.Status == UserStatus.Active && u.AuthMode == UserAuthMode.Federated && !ativosExternos.Any(i => i.TenantId == u.TenantId && i.UserId == u.Id)))
            .GroupBy(u => u.TenantId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        var reservadas = await usuarios
            .Where(u => u.Status == UserStatus.Pending && _db.PasswordResetTokens.IgnoreQueryFilters().Any(t => t.TenantId == u.TenantId && t.UserId == u.Id && t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt > now))
            .GroupBy(u => u.TenantId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        var pendentes = await usuarios.Where(u => u.Status == UserStatus.Pending).GroupBy(u => u.TenantId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        var canceladas = await usuarios.Where(u => u.Status == UserStatus.Cancelled).GroupBy(u => u.TenantId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        var politicas = await _db.TenantUserPolicies.AsNoTracking().ToListAsync(cancellationToken);

        static Dictionary<Guid, int> Mapa<T>(IEnumerable<T> itens, Func<T, (TenantId Id, int N)> f) => itens.Select(f).ToDictionary(x => x.Id.Value, x => x.N);
        var mAtivos = Mapa(ativos, x => (x.Key, x.N));
        var mInativos = Mapa(inativos, x => (x.Key, x.N));
        var mReservadas = Mapa(reservadas, x => (x.Key, x.N));
        var mPendentes = Mapa(pendentes, x => (x.Key, x.N));
        var mCanceladas = Mapa(canceladas, x => (x.Key, x.N));
        var mPoliticas = politicas.ToDictionary(p => p.TenantId.Value);

        var resultado = new Dictionary<Guid, SeatSummary>();
        foreach (var id in mAtivos.Keys.Concat(mInativos.Keys).Concat(mReservadas.Keys).Concat(mPendentes.Keys).Concat(mCanceladas.Keys).Concat(mPoliticas.Keys).Distinct())
        {
            mAtivos.TryGetValue(id, out var a);
            mInativos.TryGetValue(id, out var i);
            mReservadas.TryGetValue(id, out var r);
            mPendentes.TryGetValue(id, out var p);
            mCanceladas.TryGetValue(id, out var c);
            var politica = mPoliticas.TryGetValue(id, out var linha) ? new UserPolicy(linha.MaxUsers, linha.RegistrationEnabled, true) : UserPolicy.Legacy;
            resultado[id] = new SeatSummary(politica, new SeatUsage(a, i, r, p - r, c));
        }

        return resultado;
    }

    public async Task<UserPolicy> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        // Sempre o valor confirmado no banco (sem rastreamento), na conexão e transação do chamador.
        var politica = await _db.TenantUserPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);
        return politica is null ? UserPolicy.Legacy : new UserPolicy(politica.MaxUsers, politica.RegistrationEnabled, true);
    }

    public Task<(UserPolicy Previous, UserPolicy Current)> SetPolicyAsync(TenantId tenantId, int? maxUsers, bool registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken) =>
        RunExclusiveAsync(tenantId, async ct =>
        {
            var existente = await _db.TenantUserPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
            var anterior = existente is null ? UserPolicy.Legacy : new UserPolicy(existente.MaxUsers, existente.RegistrationEnabled, true);
            if (existente is null)
            {
                _db.TenantUserPolicies.Add(TenantUserPolicy.Create(tenantId, maxUsers, registrationEnabled, now));
            }
            else
            {
                existente.Change(maxUsers, registrationEnabled, now);
            }

            await _db.SaveChangesAsync(ct);
            return (anterior, new UserPolicy(maxUsers, registrationEnabled, true));
        }, cancellationToken);

    public Task<(UserPolicy Previous, UserPolicy Current)> ChangePolicyAsync(TenantId tenantId, bool changeMaxUsers, int? maxUsers, bool? registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken) =>
        RunExclusiveAsync(tenantId, async ct =>
        {
            // Relida SOB a trava: só os campos pedidos mudam; o resto é o que está confirmado agora (nunca o que foi lido antes de esperar).
            var existente = await _db.TenantUserPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
            var anterior = existente is null ? UserPolicy.Legacy : new UserPolicy(existente.MaxUsers, existente.RegistrationEnabled, true);
            var novoLimite = changeMaxUsers ? maxUsers : anterior.MaxUsers;
            var novoCadastro = registrationEnabled ?? anterior.RegistrationEnabled;
            if (existente is null)
            {
                _db.TenantUserPolicies.Add(TenantUserPolicy.Create(tenantId, novoLimite, novoCadastro, now));
            }
            else
            {
                existente.Change(novoLimite, novoCadastro, now);
            }

            await _db.SaveChangesAsync(ct);
            return (anterior, new UserPolicy(novoLimite, novoCadastro, true));
        }, cancellationToken);

    public Task<bool> EnsurePolicyAsync(TenantId tenantId, int? maxUsers, bool registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken) =>
        RunExclusiveAsync(tenantId, async ct =>
        {
            if (await _db.TenantUserPolicies.AnyAsync(p => p.TenantId == tenantId, ct))
            {
                return false;
            }

            _db.TenantUserPolicies.Add(TenantUserPolicy.Create(tenantId, maxUsers, registrationEnabled, now));
            await _db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

    public async Task<UserPage> ListUsersAsync(UserListQuery query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = _context.GetRequired().TenantId;
        var visiveis = query.VisibleOrganizations;

        var usuarios = _db.Users.AsNoTracking().AsQueryable();

        if (visiveis is not null)
        {
            usuarios = usuarios.Where(u => _db.Grants.Any(g => g.UserId == u.Id && (g.OrganizationId == null || visiveis.Contains(g.OrganizationId.Value))));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var padrao = "%" + query.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            usuarios = usuarios.Where(u => EF.Functions.ILike(u.Email, padrao, "\\") || EF.Functions.ILike(u.DisplayName, padrao, "\\"));
        }

        if (query.OrganizationId is { } organizacao)
        {
            usuarios = usuarios.Where(u => _db.Grants.Any(g => g.UserId == u.Id && (g.OrganizationId == organizacao || g.OrganizationId == null)));
        }

        if (query.Role is { } papel)
        {
            usuarios = usuarios.Where(u => _db.Grants.Any(g => g.UserId == u.Id && g.Role == papel));
        }

        usuarios = (query.Status ?? string.Empty).ToLowerInvariant() switch
        {
            "active" => usuarios.Where(u => u.Status == UserStatus.Active
                && (u.AuthMode != UserAuthMode.Federated || _db.ExternalIdentities.Any(i => i.UserId == u.Id && i.Status == ExternalIdentityStatus.Active))),
            "inactive" => usuarios.Where(u => u.Status == UserStatus.Inactive
                || (u.Status == UserStatus.Active && u.AuthMode == UserAuthMode.Federated && !_db.ExternalIdentities.Any(i => i.UserId == u.Id && i.Status == ExternalIdentityStatus.Active))),
            "pending" => usuarios.Where(u => u.Status == UserStatus.Pending
                && _db.PasswordResetTokens.Any(t => t.UserId == u.Id && t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt > now)),
            "expired" => usuarios.Where(u => u.Status == UserStatus.Pending
                && !_db.PasswordResetTokens.Any(t => t.UserId == u.Id && t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt > now)),
            "cancelled" => usuarios.Where(u => u.Status == UserStatus.Cancelled),
            _ => usuarios,
        };

        var total = await usuarios.CountAsync(cancellationToken);
        var pagina = await usuarios.OrderBy(u => u.DisplayName).ThenBy(u => u.Email).ThenBy(u => u.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);

        var itens = await BuildDetailsAsync(tenantId, pagina, now, cancellationToken);
        return new UserPage(itens, total);
    }

    public async Task<UserDetail?> GetDetailAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = _context.GetRequired().TenantId;
        var usuario = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (usuario is null)
        {
            return null;
        }

        return (await BuildDetailsAsync(tenantId, new[] { usuario }, now, cancellationToken))[0];
    }

    public async Task<IReadOnlyList<UserDetail>> ListTenantScopeUsersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = _context.GetRequired().TenantId;
        var usuarios = await _db.Users.AsNoTracking()
            .Where(u => _db.Grants.Any(g => g.UserId == u.Id && g.OrganizationId == null))
            .ToListAsync(cancellationToken);
        return await BuildDetailsAsync(tenantId, usuarios, now, cancellationToken);
    }

    public Task<UserPermissionOverride?> FindOverrideAsync(Guid userId, Guid? organizationId, Permission permission, CancellationToken cancellationToken) =>
        _db.PermissionOverrides.FirstOrDefaultAsync(o => o.UserId == userId && o.OrganizationId == organizationId && o.Permission == permission, cancellationToken);

    public async Task AddOverrideAsync(UserPermissionOverride item, CancellationToken cancellationToken)
    {
        _db.PermissionOverrides.Add(item);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveOverrideAsync(UserPermissionOverride item, CancellationToken cancellationToken)
    {
        _db.PermissionOverrides.Remove(item);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RemoveOverridesAsync(Guid userId, Guid? organizationId, CancellationToken cancellationToken) =>
        await _db.PermissionOverrides.Where(o => o.UserId == userId && o.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    private async Task<IReadOnlyList<UserDetail>> BuildDetailsAsync(TenantId tenantId, IReadOnlyList<User> usuarios, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (usuarios.Count == 0)
        {
            return Array.Empty<UserDetail>();
        }

        var ids = usuarios.Select(u => u.Id).ToList();
        var concessoes = (await _db.Grants.AsNoTracking().Where(g => ids.Contains(g.UserId)).ToListAsync(cancellationToken)).ToLookup(g => g.UserId);
        var ajustes = (await _db.PermissionOverrides.AsNoTracking().Where(o => ids.Contains(o.UserId)).ToListAsync(cancellationToken)).ToLookup(o => o.UserId);
        var comToken = (await _db.PasswordResetTokens.AsNoTracking()
            .Where(t => ids.Contains(t.UserId) && t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt > now)
            .Select(t => t.UserId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var comVinculo = (await _db.ExternalIdentities.AsNoTracking()
            .Where(i => ids.Contains(i.UserId) && i.Status == ExternalIdentityStatus.Active)
            .Select(i => i.UserId).Distinct().ToListAsync(cancellationToken)).ToHashSet();

        return usuarios
            .Select(u => new UserDetail(u, concessoes[u.Id].ToList(), ajustes[u.Id].ToList(), comToken.Contains(u.Id), comVinculo.Contains(u.Id)))
            .ToList();
    }
}
