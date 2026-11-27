using IaBimeta.Api.Operations;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Api.Provisioning;

/// <summary>
/// O comando <c>platform-init</c> do host (Etapa 02, ADR 0043): cria (de forma idempotente) o tenant técnico reservado da plataforma BImeta e provisiona o PRIMEIRO administrador da plataforma, com token de
/// primeiro acesso (uso único, até 24 h, mostrado uma vez). Sem credencial padrão: a senha nasce no primeiro acesso, escolhida pela própria pessoa. Repetido para o mesmo e-mail enquanto ele não concluiu o
/// primeiro acesso, reemite o token; depois disso a plataforma já tem administrador e o comando recusa (os demais operadores são cadastrados pela interface). Uso:
/// <c>platform-init --email &lt;e-mail&gt; --name &lt;nome completo&gt;</c>. Sai com 0 (ok), 2 (uso inválido ou recusado).
/// </summary>
internal static class PlatformInitCommand
{
    public const string Verb = "platform-init";

    internal const string Usage = "Uso: platform-init --email <e-mail> --name <nome completo>";

    private const string Origin = "operator-cli";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        string? email = null;
        string? nome = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(nome))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        return await IssueAsync(app.Services, email, nome, output);
    }

    internal static async Task<int> IssueAsync(IServiceProvider root, string email, string name, TextWriter output, CancellationToken cancellationToken = default)
    {
        await using var escopo = root.CreateAsyncScope();
        var sp = escopo.ServiceProvider;

        TenantInfo plataforma;
        try
        {
            plataforma = await sp.GetRequiredService<TenantAdministrationService>().EnsurePlatformTenantAsync("Plataforma BImeta", cancellationToken);
        }
        catch (Exception ex) when (ex is DuplicateTenantSlugException)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }

        sp.GetRequiredService<RequestContextHolder>().Set(new RequestContext(plataforma.TenantId, Origin));
        try
        {
            var emitido = await sp.GetRequiredService<PlatformOperatorService>().ProvisionFirstPlatformAdministratorAsync(email, name, cancellationToken);
            await output.WriteLineAsync(emitido.Reissued
                ? $"Token de primeiro acesso REEMITIDO para o administrador da plataforma: {email.Trim()} (a mesma conta; o token anterior foi invalidado)."
                : $"Administrador da plataforma provisionado: {email.Trim()} (pendente até o primeiro acesso).");
            await output.WriteLineAsync($"O token vale até {emitido.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC e só pode ser usado uma vez. Emitir outro invalida este.");
            await output.WriteLineAsync();
            await output.WriteLineAsync("TOKEN DE PRIMEIRO ACESSO (aparece UMA vez; não fica guardado em lugar nenhum):");
            await output.WriteLineAsync($"  {emitido.Token}");
            await output.WriteLineAsync();
            await output.WriteLineAsync("Na tela de entrada da plataforma, escolha \"Primeiro acesso\" e informe o e-mail, o token e a senha nova. Não há senha padrão.");
            return 0;
        }
        catch (Exception ex) when (ex is IdentityValidationException or DuplicateUserEmailException)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }
    }
}
