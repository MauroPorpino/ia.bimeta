using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Api.Platform;

/// <summary>Dado informado viola uma regra da administração da plataforma (400).</summary>
internal sealed class PlatformValidationException : Exception
{
    public PlatformValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>A ocupação de vagas de uma empresa, SEM nenhum dado de usuário: só contagens.</summary>
internal sealed record SeatsView(int? Limit, bool RegistrationEnabled, bool IsExplicit, int Active, int PendingReserved, int PendingExpired, int Inactive, int Used, int? Available, int Excess);

internal sealed record CompanyRow(CompanyView Company, SeatsView Seats);

internal sealed record CompanyRowPage(IReadOnlyList<CompanyRow> Items, int Total, int Page, int PageSize);

internal sealed record FirstAdministratorView(string Email, string DisplayName, string Status, bool HasLiveToken, DateTimeOffset CreatedAt);

internal sealed record CompanyDetail(CompanyView Company, SeatsView Seats, FirstAdministratorView? InitialAdministrator, IReadOnlyList<StatusChangeView> StatusHistory);

internal sealed record FirstAccessIssued(string Email, string Token, DateTimeOffset ExpiresAt, bool Reissued);

internal sealed record RegisteredCompany(CompanyDetail Detail, FirstAccessIssued FirstAccess, bool Created);

internal sealed record OverviewSeats(int Contracted, int Active, int Reserved, int Available, int CompaniesWithLimit);

internal sealed record OverviewLegacy(int Companies, int ActiveUsers);

internal sealed record OverviewView(
    int TotalCompanies,
    int ActiveCompanies,
    int SuspendedCompanies,
    int ProvisioningCompanies,
    OverviewSeats Seats,
    int CompaniesOverLimit,
    int CompaniesRegistrationDisabled,
    OverviewLegacy Legacy,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Orquestra as operações da plataforma BImeta sobre as empresas (Etapa 02, ADR 0043). Cada operação é ESPECÍFICA e nomeada: autoriza no tenant técnico da plataforma (quem chama é um operador
/// com a permissão de plataforma), executa o passo do módulo dono no contexto de SISTEMA da empresa-alvo (criado aqui, por código confiável; nunca por parâmetro de requisição) e audita no tenant da
/// plataforma. NÃO é um executor genérico: não existe operação "faça isto em qualquer empresa". Nenhuma operação cadastra usuários da empresa (só o primeiro administrador), lê usuários, conversas,
/// organizações, filas, agentes ou canais dela: só contagens agregadas de vagas e o registro cadastral.
/// </summary>
internal sealed class PlatformCompanyService
{
    private const string TargetOrigin = "platform-operation";

    private readonly IServiceProvider _root;
    private readonly TenantAdministrationService _tenants;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;

    public PlatformCompanyService(IServiceProvider root, TenantAdministrationService tenants, IRequestContextAccessor context, IAuditWriter audit, TimeProvider clock)
    {
        _root = root;
        _tenants = tenants;
        _context = context;
        _audit = audit;
        _clock = clock;
    }

    // Consultas

    public async Task<OverviewView> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var empresas = await _tenants.ListAllAsync(cancellationToken);
        int contratadas = 0, ativos = 0, reservadas = 0, disponiveis = 0, comLimite = 0, excedidas = 0, desabilitadas = 0, legadasEmpresas = 0, legadasAtivos = 0;

        // Todas as empresas de uma vez, em consultas agrupadas (só contagens): sem uma consulta por empresa.
        var resumos = await _root.GetRequiredService<IUserAdminStore>().GetSeatSummariesAsync(_clock.GetUtcNow(), cancellationToken);
        foreach (var empresa in empresas)
        {
            var resumo = resumos.TryGetValue(empresa.TenantId, out var r) ? r : new SeatSummary(UserPolicy.Legacy, new SeatUsage(0, 0, 0, 0, 0));
            var vagas = ToSeats(resumo.Policy, resumo.Usage);
            if (!vagas.RegistrationEnabled)
            {
                desabilitadas++;
            }

            if (vagas.Limit is not { } limite)
            {
                // Sem limite definido (empresa anterior à Etapa 01 ou exceção explícita): fora dos totais contratados, para não distorcer os indicadores.
                legadasEmpresas++;
                legadasAtivos += vagas.Active;
                continue;
            }

            comLimite++;
            contratadas += limite;
            ativos += vagas.Active;
            reservadas += vagas.PendingReserved;
            disponiveis += Math.Max(0, limite - vagas.Used);
            if (vagas.Used > limite)
            {
                excedidas++;
            }
        }

        return new OverviewView(
            empresas.Count,
            empresas.Count(e => e.Status == nameof(TenantStatus.Active)),
            empresas.Count(e => e.Status == nameof(TenantStatus.Suspended)),
            empresas.Count(e => e.Status == nameof(TenantStatus.Provisioning)),
            new OverviewSeats(contratadas, ativos, reservadas, disponiveis, comLimite),
            excedidas,
            desabilitadas,
            new OverviewLegacy(legadasEmpresas, legadasAtivos),
            _clock.GetUtcNow());
    }

    public async Task<CompanyRowPage> ListAsync(string? search, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var pagina = await _tenants.ListAsync(search, status, page, pageSize, cancellationToken);
        var linhas = new List<CompanyRow>();
        foreach (var empresa in pagina.Items)
        {
            linhas.Add(new CompanyRow(empresa, await GetSeatsAsync(new TenantId(empresa.TenantId), cancellationToken)));
        }

        return new CompanyRowPage(linhas, pagina.Total, pagina.Page, pagina.PageSize);
    }

    public async Task<CompanyDetail> GetAsync(string slug, CancellationToken cancellationToken)
    {
        var empresa = await _tenants.GetAsync(slug, cancellationToken);
        return await BuildDetailAsync(empresa, cancellationToken);
    }

    // Cadastro e provisionamento

    /// <summary>
    /// Cadastro completo e RETOMÁVEL: (1) a empresa nasce em provisionamento (não opera); (2) a política de usuários (limite OBRIGATÓRIO, mínimo 1) é definida; (3) o primeiro administrador é admitido (ocupa uma
    /// vaga) com o token de primeiro acesso (uso único, até 24 h, mostrado uma vez); (4) só então a empresa passa a poder operar. Se um passo falhar, a empresa fica em provisionamento e repetir o cadastro com
    /// o mesmo identificador retoma de onde parou (o token é reemitido: o anterior deixa de valer). Nenhuma fila, agente, canal ou configuração de IA é criado: o provisionamento operacional é da própria empresa.
    /// </summary>
    public async Task<RegisteredCompany> RegisterAsync(
        string? slug, string? name, string? legalName, string? document, string? contactName, string? contactEmail,
        int? maxUsers, bool registrationEnabled, string? adminEmail, string? adminName, CancellationToken cancellationToken)
    {
        await _tenants.RequirePlatformAsync(Permission.PlatformTenantCreate, "platform.tenant.register", slug, cancellationToken);
        if (maxUsers is null or < 1)
        {
            throw new PlatformValidationException("Informe o limite contratado de usuários (mínimo 1): ele é obrigatório para uma empresa nova.");
        }

        User.ValidateProfile(adminEmail, adminName);
        var (empresa, criada) = await _tenants.RegisterAsync(slug, name, legalName, document, contactName, contactEmail, cancellationToken);
        var alvo = new TenantId(empresa.TenantId);

        FirstAdministratorIssued emitido;
        try
        {
            emitido = await WithTargetAsync(alvo, async sp =>
            {
                await sp.GetRequiredService<TenantUserPolicyService>().EnsureAsync(alvo, maxUsers, registrationEnabled, cancellationToken);
                return await sp.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(adminEmail, adminName, cancellationToken);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A empresa continua em PROVISIONAMENTO (não opera) e o cadastro pode ser repetido; a falha é auditada sem detalhe do conteúdo.
            await _audit.WriteAsync(new AuditEntry("platform.tenant.provisioning-failed", "tenant", empresa.TenantId.ToString(), AuditOutcome.Failed), cancellationToken);
            throw;
        }

        await _tenants.CompleteProvisioningAsync(empresa.Slug, cancellationToken);
        await _audit.WriteAsync(new AuditEntry(emitido.Reissued ? "platform.tenant.first-admin.reissue" : "platform.tenant.first-admin.provision", "tenant", empresa.TenantId.ToString(), AuditOutcome.Success), cancellationToken);
        await _audit.WriteAsync(new AuditEntry("platform.tenant.limit.set", "tenant-limit", $"{empresa.TenantId}:none.{maxUsers}", AuditOutcome.Success), cancellationToken);

        var detalhe = await BuildDetailAsync(await _tenants.GetAsync(empresa.Slug, cancellationToken), cancellationToken);
        return new RegisteredCompany(detalhe, new FirstAccessIssued(User.NormalizeEmail(adminEmail)!, emitido.Token, emitido.ExpiresAt, emitido.Reissued), criada);
    }

    public async Task<CompanyDetail> UpdateCadastralAsync(string slug, string? name, string? legalName, string? document, string? contactName, string? contactEmail, CancellationToken cancellationToken)
    {
        var empresa = await _tenants.UpdateCadastralAsync(slug, name, legalName, document, contactName, contactEmail, cancellationToken);
        return await BuildDetailAsync(empresa, cancellationToken);
    }

    /// <summary>Reemite o token do administrador INICIAL que ainda não concluiu o primeiro acesso (mesma regra da Etapa 01). Nunca recupera uma conta ativa.</summary>
    public async Task<FirstAccessIssued> ReissueFirstAdministratorAsync(string slug, CancellationToken cancellationToken)
    {
        const string operacao = "platform.tenant.first-admin.reissue";
        var contexto = await _tenants.RequirePlatformAsync(Permission.PlatformFirstAdminManage, operacao, slug, cancellationToken);
        _ = contexto;
        var empresa = await _tenants.GetAsync(slug, cancellationToken);
        if (empresa.Status == nameof(TenantStatus.Suspended))
        {
            throw new PlatformValidationException("A empresa está suspensa. Reative-a antes de reemitir o primeiro acesso.");
        }

        var alvo = new TenantId(empresa.TenantId);
        var emitido = await WithTargetAsync(alvo, async sp =>
        {
            var inicial = (await sp.GetRequiredService<IUserAdminStore>().ListTenantScopeUsersAsync(_clock.GetUtcNow(), cancellationToken))
                .FirstOrDefault(u => u.User.IsInitialAdministrator && u.User.Status == UserStatus.Pending)
                ?? throw new PlatformValidationException("Não há administrador inicial pendente nesta empresa: o primeiro acesso já foi concluído (ou a admissão foi cancelada). Esta operação não recupera contas ativas.");
            var token = await sp.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(inicial.User.Email, inicial.User.DisplayName, cancellationToken);
            return (token, email: inicial.User.Email);
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "tenant", empresa.TenantId.ToString(), AuditOutcome.Success), cancellationToken);
        return new FirstAccessIssued(emitido.email, emitido.token.Token, emitido.token.ExpiresAt, true);
    }

    // Limite e habilitação (a política continua sendo do Identity)

    public async Task<CompanyDetail> SetLimitAsync(string slug, int? maxUsers, CancellationToken cancellationToken)
    {
        const string operacao = "platform.tenant.limit.set";
        await _tenants.RequirePlatformAsync(Permission.PlatformTenantQuotaManage, operacao, slug, cancellationToken);
        if (maxUsers is null or < 1)
        {
            throw new PlatformValidationException("O limite contratado deve ser um número inteiro de usuários, mínimo 1.");
        }

        var empresa = await _tenants.GetAsync(slug, cancellationToken);
        var mudanca = await WithTargetAsync(new TenantId(empresa.TenantId), sp =>
            sp.GetRequiredService<TenantUserPolicyService>().ChangeAsync(empresa.Slug, true, maxUsers, null, cancellationToken), cancellationToken)
            ?? throw new TenancyResourceNotFoundException("Empresa");

        await _audit.WriteAsync(new AuditEntry(operacao, "tenant-limit", $"{empresa.TenantId}:{Limit(mudanca.PreviousMaxUsers)}.{Limit(mudanca.MaxUsers)}", AuditOutcome.Success), cancellationToken);
        return await BuildDetailAsync(empresa, cancellationToken);
    }

    public async Task<CompanyDetail> SetRegistrationAsync(string slug, bool enabled, CancellationToken cancellationToken)
    {
        const string operacao = "platform.tenant.registration.set";
        await _tenants.RequirePlatformAsync(Permission.PlatformTenantQuotaManage, operacao, slug, cancellationToken);
        var empresa = await _tenants.GetAsync(slug, cancellationToken);
        var mudanca = await WithTargetAsync(new TenantId(empresa.TenantId), sp =>
            sp.GetRequiredService<TenantUserPolicyService>().ChangeAsync(empresa.Slug, false, null, enabled, cancellationToken), cancellationToken)
            ?? throw new TenancyResourceNotFoundException("Empresa");

        await _audit.WriteAsync(new AuditEntry(operacao, "tenant-registration", $"{empresa.TenantId}:{(mudanca.PreviousRegistrationEnabled ? "on" : "off")}.{(mudanca.RegistrationEnabled ? "on" : "off")}", AuditOutcome.Success), cancellationToken);
        return await BuildDetailAsync(empresa, cancellationToken);
    }

    // Suspensão e reativação

    /// <summary>
    /// Suspende a operação da empresa. Ordem: (1) a situação passa a Suspensa, com motivo e operador no histórico: a partir daqui login, sessões, Embedded, IA, fluxos e envios param (falha fechada);
    /// (2) todas as sessões abertas são revogadas. Nada é excluído. O recebimento de mensagens continua preservando-as, sem atendimento automático. Chamadas externas já iniciadas não são canceladas.
    /// </summary>
    public async Task<CompanyDetail> SuspendAsync(string slug, string? reason, string? confirmSlug, CancellationToken cancellationToken)
    {
        RequireConfirmation(slug, confirmSlug);
        var empresa = await _tenants.SuspendAsync(slug, reason, cancellationToken);
        var revogadas = await WithTargetAsync(new TenantId(empresa.TenantId), sp => sp.GetRequiredService<TenantSessionRevocationService>().RevokeAllAsync(new TenantId(empresa.TenantId), cancellationToken), cancellationToken);
        _ = revogadas;
        await _audit.WriteAsync(new AuditEntry("platform.tenant.sessions.revoke", "tenant", empresa.TenantId.ToString(), AuditOutcome.Success), cancellationToken);
        return await BuildDetailAsync(empresa, cancellationToken);
    }

    /// <summary>
    /// Reativa a empresa: as sessões abertas antes continuam revogadas (novas sessões são exigidas: revoga-se de novo antes de reativar, caso a suspensão tenha sido interrompida no passo 2), usuários,
    /// permissões, configurações, histórico, limite e habilitação ficam como estavam. O que nasceu antes do instante da reativação (mensagens recebidas, turnos, envios pendentes) não é processado automaticamente.
    /// </summary>
    public async Task<CompanyDetail> ReactivateAsync(string slug, string? reason, string? confirmSlug, CancellationToken cancellationToken)
    {
        RequireConfirmation(slug, confirmSlug);
        await _tenants.RequirePlatformAsync(Permission.PlatformTenantSuspend, "platform.tenant.reactivate", slug, cancellationToken);
        var atual = await _tenants.GetAsync(slug, cancellationToken);
        _ = await WithTargetAsync(new TenantId(atual.TenantId), sp => sp.GetRequiredService<TenantSessionRevocationService>().RevokeAllAsync(new TenantId(atual.TenantId), cancellationToken), cancellationToken);
        var empresa = await _tenants.ReactivateAsync(slug, reason, cancellationToken);
        return await BuildDetailAsync(empresa, cancellationToken);
    }

    // Internos

    private static void RequireConfirmation(string slug, string? confirmSlug)
    {
        if (!string.Equals((slug ?? string.Empty).Trim(), (confirmSlug ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            throw new PlatformValidationException("Confirme a operação digitando o identificador da empresa.");
        }
    }

    private static string Limit(int? value) => value?.ToString() ?? "u";

    private async Task<CompanyDetail> BuildDetailAsync(CompanyView empresa, CancellationToken cancellationToken)
    {
        var alvo = new TenantId(empresa.TenantId);
        var vagas = await GetSeatsAsync(alvo, cancellationToken);
        var inicial = await WithTargetAsync(alvo, async sp =>
        {
            var candidatos = await sp.GetRequiredService<IUserAdminStore>().ListTenantScopeUsersAsync(_clock.GetUtcNow(), cancellationToken);
            var admin = candidatos.FirstOrDefault(u => u.User.IsInitialAdministrator);
            return admin is null ? null : new FirstAdministratorView(admin.User.Email, admin.User.DisplayName, admin.DisplayStatus, admin.HasLiveFirstAccessToken, admin.User.CreatedAt);
        }, cancellationToken);
        var historico = await _tenants.GetStatusHistoryAsync(empresa.Slug, cancellationToken);
        return new CompanyDetail(empresa, vagas, inicial, historico);
    }

    private static SeatsView ToSeats(UserPolicy politica, SeatUsage uso)
    {
        var usadas = uso.SeatsUsed;
        return new SeatsView(
            politica.MaxUsers,
            politica.RegistrationEnabled,
            politica.IsExplicit,
            uso.Active,
            uso.PendingReserved,
            uso.PendingExpired,
            uso.Inactive,
            usadas,
            politica.MaxUsers is { } max ? Math.Max(0, max - usadas) : null,
            politica.MaxUsers is { } teto ? Math.Max(0, usadas - teto) : 0);
    }

    private async Task<SeatsView> GetSeatsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await WithTargetAsync(tenantId, async sp =>
        {
            var armazenamento = sp.GetRequiredService<IUserAdminStore>();
            var politica = await armazenamento.GetPolicyAsync(tenantId, cancellationToken);
            var uso = await armazenamento.GetSeatUsageAsync(tenantId, _clock.GetUtcNow(), cancellationToken);
            var usadas = uso.SeatsUsed;
            return new SeatsView(
                politica.MaxUsers,
                politica.RegistrationEnabled,
                politica.IsExplicit,
                uso.Active,
                uso.PendingReserved,
                uso.PendingExpired,
                uso.Inactive,
                usadas,
                politica.MaxUsers is { } max ? Math.Max(0, max - usadas) : null,
                politica.MaxUsers is { } teto ? Math.Max(0, usadas - teto) : 0);
        }, cancellationToken);

    /// <summary>
    /// Executa um passo no contexto de SISTEMA da empresa-alvo, num escopo próprio (nada vaza para o contexto do operador). O alvo é sempre um tenant resolvido a partir do registro, nunca um valor
    /// livre da requisição.
    /// </summary>
    private async Task<T> WithTargetAsync<T>(TenantId target, Func<IServiceProvider, Task<T>> action, CancellationToken cancellationToken)
    {
        _ = _context;
        await using var escopo = _root.CreateAsyncScope();
        escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(target, TargetOrigin));
        return await action(escopo.ServiceProvider);
    }
}
