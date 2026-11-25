using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Casos de uso de usuários e concessões. Tenant e permissões vêm sempre do contexto confiável (TEN-1). A
/// organização é a da rota, validada: sem leitura sobre ela, responde como inexistente; com leitura mas sem a
/// permissão da ação, a operação é negada e auditada (TEN-2).
/// </summary>
public sealed class UserService
{
    private readonly IUserRepository _users;
    private readonly IGrantRepository _grants;
    private readonly IOrganizationDirectory _organizations;
    private readonly PasswordHasher _hasher;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;
    private readonly UserAdminService _admin;
    private readonly IUserAdminStore _adminStore;
    private readonly UserAdmissionGuard _guard;
    private readonly IAuthenticationStore _auth;
    private readonly IdentityOptions _options;

    public UserService(
        IUserRepository users,
        IGrantRepository grants,
        IOrganizationDirectory organizations,
        PasswordHasher hasher,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit,
        UserAdminService admin,
        IUserAdminStore adminStore,
        UserAdmissionGuard guard,
        IAuthenticationStore auth,
        IdentityOptions options)
    {
        _users = users;
        _grants = grants;
        _organizations = organizations;
        _hasher = hasher;
        _context = context;
        _clock = clock;
        _audit = audit;
        _admin = admin;
        _adminStore = adminStore;
        _guard = guard;
        _auth = auth;
        _options = options;
    }

    /// <summary>Cria um usuário e já lhe concede o papel na organização, tudo ou nada.</summary>
    /// <exception cref="IdentityResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir usuários da organização.</exception>
    /// <exception cref="IdentityValidationException">E-mail, nome, senha ou papel inválidos.</exception>
    /// <exception cref="DuplicateUserEmailException">O e-mail já existe neste tenant.</exception>
    public async Task<UserView> CreateAsync(
        Guid organizationId,
        string? email,
        string? displayName,
        string? password,
        Role role,
        string? preferredLocale = null,
        CancellationToken cancellationToken = default)
    {
        // Mesmas regras do cadastro novo: papel atribuível, cadastro habilitado e vaga (UserAdminService).
        return await _admin.CreateWithPasswordAsync(organizationId, email, displayName, password, role, preferredLocale, cancellationToken);
    }

    /// <summary>Concede o papel ao usuário na organização. Se já havia concessão, o papel é trocado.</summary>
    /// <exception cref="IdentityResourceNotFoundException">A organização ou o usuário não existem ou não são visíveis.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir usuários da organização.</exception>
    public async Task GrantRoleAsync(Guid organizationId, Guid userId, Role role, CancellationToken cancellationToken = default)
    {
        await _admin.SetRoleAsync(organizationId, userId, role, cancellationToken);
    }

    /// <summary>Revoga a concessão do usuário na organização. As sessões dele passam a ter menos permissão na próxima requisição.</summary>
    /// <exception cref="IdentityResourceNotFoundException">A organização ou a concessão não existem ou não são visíveis.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir usuários da organização.</exception>
    public async Task RevokeRoleAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default)
    {
        // As regras (elevação, último administrador, escopo) vivem no UserAdminService, que serve a esta rota e à API nova.
        await _admin.RevokeRoleAsync(organizationId, userId, cancellationToken);
    }

    /// <exception cref="IdentityResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de ler os usuários da organização.</exception>
    public async Task<IReadOnlyList<UserAccessView>> ListAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.UserRead, organizationId,
            "identity.user.list", "organization", organizationId.ToString(), cancellationToken);

        var acessos = await _grants.ListAccessAsync(organizationId, cancellationToken);
        return acessos.Select(UserAccessView.From).ToList();
    }

    /// <summary>O usuário autenticado e as suas concessões.</summary>
    /// <exception cref="AuthenticationRequiredException">O contexto não tem usuário autenticado.</exception>
    /// <exception cref="IdentityResourceNotFoundException">O usuário da sessão já não existe.</exception>
    public async Task<CurrentUserView> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var principal = _context.GetRequired().RequirePrincipal();

        var usuario = await _users.FindByIdAsync(principal.UserId, cancellationToken)
            ?? throw new IdentityResourceNotFoundException("Usuário");
        var concessoes = await _grants.ListByUserAsync(usuario.Id, cancellationToken);

        return new CurrentUserView(
            usuario.Id,
            usuario.Email,
            usuario.DisplayName,
            concessoes.Select(c => new GrantView(c.OrganizationId, c.Role)).ToList(),
            usuario.PreferredLocale,
            principal.Grants);
    }

    /// <summary>
    /// O usuário autenticado define o próprio idioma preferido (BCP 47), ou o limpa com <c>null</c>. É dado do próprio usuário:
    /// o alvo é sempre o usuário do contexto, nunca um parâmetro, e outro usuário não é alcançável por aqui.
    /// </summary>
    /// <exception cref="AuthenticationRequiredException">O contexto não tem usuário autenticado.</exception>
    /// <exception cref="IdentityValidationException">O idioma não é um identificador BCP 47 bem formado.</exception>
    public async Task<Locale?> SetMyPreferredLocaleAsync(string? locale, CancellationToken cancellationToken = default)
    {
        var principal = _context.GetRequired().RequirePrincipal();
        var idioma = User.ParsePreferredLocale(locale);

        var usuario = await _users.FindForUpdateAsync(principal.UserId, cancellationToken)
            ?? throw new IdentityResourceNotFoundException("Usuário");
        if (usuario.PreferredLocale == idioma)
        {
            return idioma;
        }

        usuario.SetPreferredLocale(idioma);
        await _users.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(new AuditEntry("identity.user.locale.set", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return idioma;
    }

    /// <summary>
    /// O PRIMEIRO administrador do tenant com senha, para o provisionamento COMERCIAL da BImeta (comando <c>provision</c>). Diferente do seed interno (<see cref="EnsureTenantAdministratorAsync"/>), cria UM só administrador de escopo do tenant por empresa,
    /// SEMPRE como exceção do primeiro administrador (vale com o cadastro desabilitado, continua sujeita ao limite) e nunca se repete: se a empresa já tem um usuário de escopo do tenant, recusa (e o provisionamento nunca vira meio de cadastrar outros administradores).
    /// Só contexto de sistema. Idempotente para o MESMO e-mail: devolve <c>false</c> se já existe.
    /// </summary>
    /// <exception cref="IdentityValidationException">E-mail, nome ou senha inválidos, ou a empresa já tem outro administrador.</exception>
    public async Task<bool> EnsureFirstTenantAdministratorAsync(string? email, string? displayName, string? password, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsSystem)
        {
            throw new AccessDeniedException(Permission.UserManage);
        }

        var (emailNormalizado, _) = User.ValidateProfile(email, displayName);
        PasswordPolicy.Validate(password);

        if (await _users.FindByEmailAsync(emailNormalizado, cancellationToken) is not null)
        {
            return false;
        }

        var agora = _clock.GetUtcNow();
        var usuario = User.Create(contexto.TenantId, email, displayName, _hasher.Hash(password!), agora);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, null, Role.OrganizationAdmin, agora, null);
        try
        {
            await _adminStore.RunExclusiveAsync(async ct =>
            {
                await RequireExplicitPolicyAsync(contexto.TenantId, ct);
                await RequireNoAdministratorYetAsync(agora, ct);
                await _guard.RequireIngressAsync(contexto.TenantId, agora, firstAdministrator: true, ct);
                await _users.AddAsync(usuario, concessao, ct);
                return true;
            }, cancellationToken);
        }
        catch (DuplicateUserEmailException)
        {
            return false;
        }

        await _audit.WriteAsync(new AuditEntry("identity.tenant-admin.create", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return true;
    }

    /// <summary>
    /// SEED INTERNO (desenvolvimento e testes; NUNCA o provisionamento comercial, que usa <see cref="EnsureFirstTenantAdministratorAsync"/>): administrador do tenant com concessão de escopo do tenant. Só um contexto de sistema (o host,
    /// no seed de desenvolvimento) pode chamar: nenhum usuário autenticado cria administradores de tenant por aqui.
    /// Idempotente: devolve <c>false</c> se o e-mail já existe.
    /// </summary>
    /// <exception cref="AccessDeniedException">O contexto é de um usuário autenticado.</exception>
    /// <exception cref="IdentityValidationException">E-mail, nome ou senha inválidos.</exception>
    public async Task<bool> EnsureTenantAdministratorAsync(string? email, string? displayName, string? password, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsSystem)
        {
            throw new AccessDeniedException(Permission.UserManage);
        }

        var (emailNormalizado, _) = User.ValidateProfile(email, displayName);
        PasswordPolicy.Validate(password);

        if (await _users.FindByEmailAsync(emailNormalizado, cancellationToken) is not null)
        {
            return false;
        }

        var agora = _clock.GetUtcNow();
        var usuario = User.Create(contexto.TenantId, email, displayName, _hasher.Hash(password!), agora);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, null, Role.OrganizationAdmin, agora, null);

        try
        {
            await _adminStore.RunExclusiveAsync(async ct =>
            {
                // Caminho de bootstrap com senha (seed e comando provision). Com o cadastro habilitado é um ingresso comum (e o tenant pode ter mais de um administrador, criados pela operação). Só com o cadastro DESABILITADO vale a
                // exceção do primeiro administrador, que não se repete.
                var habilitado = await _guard.IsRegistrationEnabledAsync(contexto.TenantId, ct);
                if (!habilitado)
                {
                    await RequireNoAdministratorYetAsync(agora, ct);
                }

                await _guard.RequireIngressAsync(contexto.TenantId, agora, firstAdministrator: !habilitado, ct);
                await _users.AddAsync(usuario, concessao, ct);
                return true;
            }, cancellationToken);
        }
        catch (DuplicateUserEmailException)
        {
            return false;
        }

        await _audit.WriteAsync(new AuditEntry("identity.tenant-admin.create", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return true;
    }

    /// <summary>
    /// O PRIMEIRO administrador do tenant, pela plataforma (contexto de sistema): usuário pendente de escopo do tenant, com token de primeiro acesso (uso único, até 24 h, mostrado uma vez). Consome uma vaga e é a ÚNICA exceção à
    /// habilitação do cadastro: vale uma vez só (recusada se o tenant já tem administrador) e continua sujeita ao limite. A criação do usuário, da concessão e do token é atômica.
    /// </summary>
    /// <exception cref="AccessDeniedException">O contexto é de um usuário autenticado.</exception>
    /// <exception cref="IdentityValidationException">E-mail ou nome inválidos, ou o tenant já tem administrador.</exception>
    /// <exception cref="UserLimitReachedException">Não há vaga (limite zerado por engano).</exception>
    public async Task<FirstAdministratorIssued> ProvisionFirstAdministratorAsync(string? email, string? displayName, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsSystem)
        {
            throw new AccessDeniedException(Permission.UserManage);
        }

        var (emailNormalizado, _) = User.ValidateProfile(email, displayName);
        var agora = _clock.GetUtcNow();
        var usuario = User.CreatePending(contexto.TenantId, email, displayName, agora, initialAdministrator: true);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, null, Role.OrganizationAdmin, agora, null);

        var emitido = await _adminStore.RunExclusiveAsync(async ct =>
        {
            // O instante é o de DENTRO da seção exclusiva (depois da trava): a validade da reserva, a vaga e o novo token usam o MESMO instante, então uma reserva que venceu enquanto a operação esperava a trava é vista como vencida e exige vaga.
            var agoraNaTrava = _clock.GetUtcNow();
            await RequireExplicitPolicyAsync(contexto.TenantId, ct);

            // REEMISSÃO: a conta já existe e é, de forma inequívoca, o administrador inicial provisionado pela BImeta que ainda não concluiu o primeiro acesso (marca `IsInitialAdministrator`, status pendente).
            // Qualquer outro e-mail, ou uma conta que já concluiu o primeiro acesso, NÃO usa este caminho: o mecanismo nunca cadastra outro usuário nem recupera um administrador já ativo.
            var existente = await _users.FindByEmailAsync(emailNormalizado, ct);
            if (existente is not null)
            {
                if (!existente.IsInitialAdministrator || existente.Status != UserStatus.Pending)
                {
                    throw new IdentityValidationException("O primeiro administrador desta empresa já foi provisionado e concluiu o primeiro acesso, ou este e-mail não é o do administrador inicial. Novos administradores são criados pelo próprio administrador da empresa.");
                }

                var detalhe = await _adminStore.GetDetailAsync(existente.Id, agoraNaTrava, ct);
                if (detalhe is null || !detalhe.Grants.Any(g => g.OrganizationId is null && g.Role == Role.OrganizationAdmin))
                {
                    throw new IdentityValidationException("O administrador inicial não tem o vínculo de escopo do tenant esperado.");
                }

                // Reserva já viva: trocar o token não muda as vagas. Reserva vencida ou cancelada: volta a ocupar vaga, então exige o limite (a exceção da habilitação é só desta conclusão do provisionamento inicial).
                if (!detalhe.HasLiveFirstAccessToken)
                {
                    await _guard.RequireIngressAsync(contexto.TenantId, agoraNaTrava, firstAdministrator: true, ct);
                }

                return (usuario: existente, reemitido: true, token: await IssueFirstAdministratorTokenAsync(contexto.TenantId, existente.Id, agoraNaTrava, ct));
            }

            await RequireNoAdministratorYetAsync(agoraNaTrava, ct);
            await _guard.RequireIngressAsync(contexto.TenantId, agoraNaTrava, firstAdministrator: true, ct);
            await _users.AddAsync(usuario, concessao, ct);
            return (usuario, reemitido: false, token: await IssueFirstAdministratorTokenAsync(contexto.TenantId, usuario.Id, agoraNaTrava, ct));
        }, cancellationToken);

        // A auditoria registra QUEM (conta) e o quê, nunca o token.
        await _audit.WriteAsync(new AuditEntry(emitido.reemitido ? "identity.tenant-admin.first-access.reissue" : "identity.tenant-admin.provision", "user", emitido.usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new FirstAdministratorIssued(emitido.token.Token, emitido.token.ExpiresAt, emitido.reemitido);
    }

    private async Task<PasswordResetIssued> IssueFirstAdministratorTokenAsync(TenantId tenantId, Guid userId, DateTimeOffset agora, CancellationToken ct)
    {
        var duracao = _options.FirstAccessLifetime <= TimeSpan.Zero || _options.FirstAccessLifetime > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : _options.FirstAccessLifetime;
        var (token, hash) = ResetTokens.Generate();
        var registro = PasswordResetToken.Create(tenantId, userId, hash, agora, duracao, null);
        await _auth.AddPasswordResetTokenAsync(registro, agora, ct); // invalida os tokens anteriores ainda não usados do mesmo usuário
        return new PasswordResetIssued(token, registro.ExpiresAt);
    }

    /// <summary>
    /// O provisionamento COMERCIAL (primeiro administrador) só acontece com a política de usuários da empresa JÁ definida de forma explícita (limite ou a exceção ilimitada): uma falha ao criar a política nunca deixa uma empresa comercial
    /// funcionando sem limite por omissão. O seed interno (<see cref="EnsureTenantAdministratorAsync"/>) não exige.
    /// </summary>
    private async Task RequireExplicitPolicyAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        if (!(await _adminStore.GetPolicyAsync(tenantId, cancellationToken)).IsExplicit)
        {
            throw new IdentityValidationException("Defina a política de usuários da empresa (limite contratado, ou a exceção explícita de ilimitado) antes de provisionar o primeiro administrador.");
        }
    }

    /// <summary>A exceção do primeiro administrador não se repete: se o tenant já tem um usuário de escopo do tenant (em qualquer situação), recusa.</summary>
    private async Task RequireNoAdministratorYetAsync(DateTimeOffset agora, CancellationToken cancellationToken)
    {
        if ((await _adminStore.ListTenantScopeUsersAsync(agora, cancellationToken)).Any(u => u.User.Status != UserStatus.Cancelled))
        {
            throw new IdentityValidationException("O primeiro administrador desta empresa já foi provisionado. Novos administradores são criados pelo próprio administrador da empresa.");
        }
    }

    private async Task RequireVisibleOrganizationAsync(RequestContext contexto, Guid organizationId, CancellationToken cancellationToken)
    {
        if (!contexto.IsAllowed(Permission.OrganizationRead, organizationId)
            || !await _organizations.ExistsAsync(organizationId, cancellationToken))
        {
            throw new IdentityResourceNotFoundException("Organização");
        }
    }
}
