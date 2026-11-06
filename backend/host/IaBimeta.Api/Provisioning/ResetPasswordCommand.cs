using IaBimeta.Api.Operations;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Api.Provisioning;

/// <summary>
/// O comando <c>reset-password</c> do host: <c>dotnet run --project backend/host/IaBimeta.Api --no-launch-profile -- reset-password --tenant lab-exemplo --email ana@exemplo.com</c> (o script <c>tools/ops/Redefinir-Senha.ps1</c>
/// o chama). É a emissão CONTROLADA do token de redefinição quando não há serviço de e-mail ou quando nenhum administrador da empresa pode fazê-lo (por exemplo, o único administrador esqueceu a senha). Roda na máquina de
/// quem opera, com acesso ao banco: não existe rota HTTP para isto, e o contexto é o de sistema do tenant. O token aparece UMA vez, na saída do comando; nunca vai a log, arquivo ou trilha (a trilha registra só a emissão).
/// Sai com 0 (emitido), 1 (empresa ou usuário não encontrado) ou 2 (uso inválido).
/// </summary>
internal static class ResetPasswordCommand
{
    public const string Verb = "reset-password";

    internal const string Usage = "Uso: reset-password --tenant <empresa> --email <e-mail>";

    private const string Origin = "operator-cli";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        string? tenant = null;
        string? email = null;
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
                default:
                    await output.WriteLineAsync($"Argumento desconhecido: {args[i]}");
                    await output.WriteLineAsync(Usage);
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(email))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        // A mesma composição do Api, sem abrir porta nem iniciar serviço algum, com só avisos e erros no console (nenhum log traz o token).
        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        return await IssueAsync(app.Services, tenant, email, output);
    }

    internal static async Task<int> IssueAsync(IServiceProvider root, string slug, string email, TextWriter output, CancellationToken cancellationToken = default)
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
            var emitido = await sp.GetRequiredService<PasswordService>().IssueResetForOperatorAsync(email, cancellationToken);
            await output.WriteLineAsync($"Token de redefinição emitido para {email.Trim()} (empresa '{tenant.Slug}').");
            await output.WriteLineAsync($"Vale até {emitido.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC e só pode ser usado uma vez. Emitir outro invalida este.");
            await output.WriteLineAsync();
            await output.WriteLineAsync("TOKEN (aparece UMA vez; não fica guardado em lugar nenhum):");
            await output.WriteLineAsync($"  {emitido.Token}");
            await output.WriteLineAsync();
            await output.WriteLineAsync("Entregue-o à pessoa por um canal seguro. Ela o usa na redefinição (empresa, e-mail, token e a nova senha). Todas as sessões dela caem quando a senha muda.");
            return 0;
        }
        catch (IdentityResourceNotFoundException)
        {
            await output.WriteLineAsync($"Não há usuário com o e-mail '{email.Trim()}' na empresa '{tenant.Slug}'.");
            return 1;
        }
        catch (IdentityValidationException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }
    }
}
