using IaBimeta.Api.Operations;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;


namespace IaBimeta.Api.Provisioning;

/// <summary>
/// O comando <c>tenant-users</c> do host (Etapa 01): a administração CONTROLADA do limite contratado de usuários e da habilitação do cadastro de uma empresa. É o mecanismo técnico de backend que antecede a interface da
/// administração da plataforma (Etapa 02): não existe rota HTTP do tenant para isto (o tenant só consulta). Uso:
/// <c>tenant-users --tenant lab-exemplo</c> (mostra a situação) ·
/// <c>tenant-users --tenant lab-exemplo --max 10 --registration on</c> · <c>tenant-users --tenant lab-exemplo --unlimited</c> (exceção explícita e auditada) ·
/// <c>tenant-users --tenant lab-exemplo --registration off</c>.
/// A alteração roda sob a mesma trava de admissão do cadastro (concorrência segura) e fica na trilha de auditoria da empresa. Reduzir o limite abaixo do uso NÃO desativa ninguém: só bloqueia novos ingressos.
/// Sai com 0 (ok), 1 (empresa não encontrada) ou 2 (uso inválido).
/// </summary>
internal static class TenantUsersCommand
{
    public const string Verb = "tenant-users";

    internal const string Usage = "Uso: tenant-users --tenant <empresa> [--max <N> | --unlimited] [--registration on|off]";

    private const string Origin = "operator-cli";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        string? tenant = null;
        int? max = null;
        var ilimitado = false;
        bool? cadastro = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tenant" when i + 1 < args.Length:
                    tenant = args[++i];
                    break;
                case "--max" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n):
                    max = n;
                    i++;
                    break;
                case "--unlimited":
                    ilimitado = true;
                    break;
                case "--registration" when i + 1 < args.Length && args[i + 1] is "on" or "off":
                    cadastro = args[++i] == "on";
                    break;
                default:
                    await output.WriteLineAsync($"Argumento desconhecido ou inválido: {args[i]}");
                    await output.WriteLineAsync(Usage);
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(tenant) || (max is not null && ilimitado))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        return await ApplyAsync(app.Services, tenant, max, ilimitado, cadastro, output);
    }

    internal static async Task<int> ApplyAsync(IServiceProvider root, string slug, int? max, bool unlimited, bool? registration, TextWriter output, CancellationToken cancellationToken = default)
    {
        await using var escopo = root.CreateAsyncScope();
        var sp = escopo.ServiceProvider;

        var tenant = await sp.GetRequiredService<ITenantDirectory>().FindBySlugAsync(slug.Trim(), cancellationToken);
        if (tenant is null)
        {
            await output.WriteLineAsync($"Empresa '{slug.Trim()}' não encontrada.");
            return 1;
        }

        sp.GetRequiredService<RequestContextHolder>().Set(new RequestContext(tenant.TenantId, Origin));

        if (max is null && !unlimited && registration is null)
        {
            await PrintAsync(sp, tenant.Slug, output, cancellationToken);
            return 0;
        }

        try
        {
            // Só muda o que foi pedido: o resto fica como está.
            var servico = sp.GetRequiredService<TenantUserPolicyService>();
            // Só os campos pedidos: a política é relida SOB a trava (uma alteração do limite nunca desfaz uma alteração independente da habilitação, e vice-versa).
            var mudanca = await servico.ChangeAsync(tenant.Slug, changeMaxUsers: unlimited || max is not null, maxUsers: unlimited ? null : max, registrationEnabled: registration, cancellationToken);
            if (mudanca is null)
            {
                await output.WriteLineAsync($"Empresa '{slug.Trim()}' não encontrada.");
                return 1;
            }

            var auditoria = sp.GetRequiredService<IAuditWriter>();
            if (mudanca.MaxUsers != mudanca.PreviousMaxUsers)
            {
                await auditoria.WriteAsync(new AuditEntry(mudanca.MaxUsers is null ? "identity.user-limit.unlimited" : "identity.user-limit.set", "tenant", tenant.TenantId.ToString(), AuditOutcome.Success), cancellationToken);
            }

            if (mudanca.RegistrationEnabled != mudanca.PreviousRegistrationEnabled)
            {
                await auditoria.WriteAsync(new AuditEntry(mudanca.RegistrationEnabled ? "identity.user-registration.enable" : "identity.user-registration.disable", "tenant", tenant.TenantId.ToString(), AuditOutcome.Success), cancellationToken);
            }

            await output.WriteLineAsync($"Empresa '{tenant.Slug}': limite {Describe(mudanca.PreviousMaxUsers)} → {Describe(mudanca.MaxUsers)}; cadastro de novos usuários {(mudanca.PreviousRegistrationEnabled ? "habilitado" : "desabilitado")} → {(mudanca.RegistrationEnabled ? "habilitado" : "desabilitado")}.");
            await PrintAsync(sp, tenant.Slug, output, cancellationToken);
            return 0;
        }
        catch (IdentityValidationException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }
    }

    private static async Task PrintAsync(IServiceProvider sp, string slug, TextWriter output, CancellationToken ct)
    {
        var (_, tenantId, politica) = (await sp.GetRequiredService<TenantUserPolicyService>().GetAsync(slug, ct))!.Value;
        var uso = await sp.GetRequiredService<IUserAdminStore>().GetSeatUsageAsync(tenantId, DateTimeOffset.UtcNow, ct);
        await output.WriteLineAsync($"Empresa '{slug}': limite {Describe(politica.MaxUsers)}{(politica.IsExplicit ? string.Empty : " (empresa anterior à Etapa 01, sem política definida)")}, cadastro {(politica.RegistrationEnabled ? "habilitado" : "desabilitado")}.");
        await output.WriteLineAsync($"  Vagas usadas: {uso.SeatsUsed} (ativos {uso.Active}, admissões pendentes com reserva {uso.PendingReserved}); inativos {uso.Inactive}; pendentes expiradas {uso.PendingExpired}; canceladas {uso.Cancelled}.");
        if (politica.MaxUsers is { } limite && uso.SeatsUsed > limite)
        {
            await output.WriteLineAsync($"  ATENÇÃO: acima do limite em {uso.SeatsUsed - limite}. Ninguém foi desativado; novos ingressos ficam bloqueados até voltar ao limite.");
        }
    }

    private static string Describe(int? limite) => limite is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "ilimitado";
}

/// <summary>
/// O comando <c>first-admin</c> do host (Etapa 01): o PRIMEIRO administrador de uma empresa, provisionado pela BImeta. Repetido para o MESMO e-mail enquanto a conta ainda não concluiu o primeiro acesso, REEMITE o token (mesma conta, token anterior invalidado); nunca para outro e-mail nem depois da conclusão. Cria o usuário pendente de escopo do tenant e emite o token de primeiro acesso (uso único, até 24 h), mostrado UMA vez na saída.
/// Consome uma vaga, vale mesmo com o cadastro desabilitado e não se repete (recusa se a empresa já tem administrador). Uso: <c>first-admin --tenant lab-exemplo --email dona@exemplo.com --name "Maria"</c>.
/// Sai com 0 (emitido), 1 (empresa não encontrada), 2 (uso inválido ou já existe administrador).
/// </summary>
internal static class FirstAdminCommand
{
    public const string Verb = "first-admin";

    internal const string Usage = "Uso: first-admin --tenant <empresa> --email <e-mail> --name <nome completo>";

    private const string Origin = "operator-cli";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        string? tenant = null;
        string? email = null;
        string? nome = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tenant" when i + 1 < args.Length:
                    tenant = args[++i];
                    break;
                case "--email" when i + 1 < args.Length:
                    email = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    nome = args[++i];
                    break;
                default:
                    await output.WriteLineAsync($"Argumento desconhecido: {args[i]}");
                    await output.WriteLineAsync(Usage);
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(nome))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        return await IssueAsync(app.Services, tenant, email, nome, output);
    }

    internal static async Task<int> IssueAsync(IServiceProvider root, string slug, string email, string name, TextWriter output, CancellationToken cancellationToken = default)
    {
        await using var escopo = root.CreateAsyncScope();
        var sp = escopo.ServiceProvider;

        var tenant = await sp.GetRequiredService<ITenantDirectory>().FindBySlugAsync(slug.Trim(), cancellationToken);
        if (tenant is null)
        {
            await output.WriteLineAsync($"Empresa '{slug.Trim()}' não encontrada.");
            return 1;
        }

        sp.GetRequiredService<RequestContextHolder>().Set(new RequestContext(tenant.TenantId, Origin));
        try
        {
            var emitido = await sp.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, name, cancellationToken);
            await output.WriteLineAsync(emitido.Reissued
                ? $"Token de primeiro acesso REEMITIDO para o administrador inicial de '{tenant.Slug}': {email.Trim()} (a mesma conta; o token anterior foi invalidado)."
                : $"Primeiro administrador de '{tenant.Slug}' provisionado: {email.Trim()} (pendente até o primeiro acesso).");
            await output.WriteLineAsync($"O token vale até {emitido.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC e só pode ser usado uma vez. Emitir outro invalida este.");
            await output.WriteLineAsync();
            await output.WriteLineAsync("TOKEN DE PRIMEIRO ACESSO (aparece UMA vez; não fica guardado em lugar nenhum):");
            await output.WriteLineAsync($"  {emitido.Token}");
            await output.WriteLineAsync();
            await output.WriteLineAsync("Entregue-o à pessoa por um canal seguro. Na tela de entrada ela escolhe \"Esqueci a senha (tenho um token)\" e informa a empresa, o e-mail, o token e a senha que ela mesma criar.");
            return 0;
        }
        catch (Exception ex) when (ex is IdentityValidationException or UserLimitReachedException or DuplicateUserEmailException)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }
    }
}
