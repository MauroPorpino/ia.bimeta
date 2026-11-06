using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Api.Provisioning;

/// <summary>
/// O comando <c>provision</c> do host: <c>dotnet run --project backend/host/IaBimeta.Api --no-launch-profile -- provision --file empresa.json</c> (o script <c>tools/ops/Novo-Tenant.ps1</c> o chama). Sobe a MESMA composição do
/// Api (mesma configuração, mesmos módulos), mas sem abrir porta nem iniciar serviço algum: aplica as migrations (a menos que <c>--no-migrations</c>), provisiona a empresa descrita no arquivo e imprime o relatório. Sai com 0
/// (tudo certo), 1 (alguma etapa falhou; repetir depois de corrigir completa o resto) ou 2 (uso ou arquivo inválido). Roda na máquina de quem opera, com acesso ao banco: não existe rota HTTP para isto.
/// </summary>
internal static class ProvisionCommand
{
    public const string Verb = "provision";

    public static async Task<int> RunAsync(string[] args, TextWriter output, IProvisioningSecrets? secrets = null)
    {
        string? arquivo = null;
        var migrations = true;
        var somenteValidar = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file" when i + 1 < args.Length:
                    arquivo = args[++i];
                    break;
                case "--no-migrations":
                    migrations = false;
                    break;
                case "--validate-only":
                    somenteValidar = true;
                    break;
                default:
                    await output.WriteLineAsync($"Argumento desconhecido: {args[i]}");
                    await output.WriteLineAsync(Usage);
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(arquivo))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        ProvisioningSpec spec;
        try
        {
            spec = ProvisioningSpec.Load(arquivo);
        }
        catch (ProvisioningSpecException ex)
        {
            await output.WriteLineAsync("O arquivo da empresa tem problemas:");
            foreach (var erro in ex.Errors)
            {
                await output.WriteLineAsync($"  - {erro}");
            }

            return 2;
        }

        if (somenteValidar)
        {
            // Só confere o arquivo (formato, campos obrigatórios, nomes de variáveis): não abre banco nem muda nada.
            await output.WriteLineAsync($"Arquivo válido: empresa '{spec.Tenant.Slug}', {spec.Attendants.Count} atendente(s), WhatsApp {(spec.WhatsApp is null ? "não descrito" : spec.WhatsApp.Mode == WhatsAppMode.Real ? "real" : "simulado")}. Nada foi alterado.");
            return 0;
        }

        // A mesma composição do Api, sem argumentos de linha de comando (não são configuração) e sem iniciar o host: nenhuma porta, nenhum serviço hospedado.
        // Só avisos e erros no console: o relatório é a saída do comando (e nenhum log traz segredo).
        await using var app = ApiHost.Build(Array.Empty<string>(), builder => builder.Logging.SetMinimumLevel(LogLevel.Warning));
        if (migrations)
        {
            await ApplyMigrationsAsync(app.Services, output);
        }

        var relatorio = await new TenantProvisioner(app.Services, secrets ?? new EnvironmentProvisioningSecrets()).ProvisionAsync(spec);
        await PrintAsync(relatorio, spec, output);
        return relatorio.Succeeded ? 0 : 1;
    }

    internal const string Usage = "Uso: provision --file <empresa.json> [--no-migrations] [--validate-only]";

    private static async Task ApplyMigrationsAsync(IServiceProvider services, TextWriter output)
    {
        await using var escopo = services.CreateAsyncScope();
        var sp = escopo.ServiceProvider;
        await sp.GetRequiredService<TenancyDbContext>().Database.MigrateAsync();
        await sp.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
        await sp.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        await sp.GetRequiredService<ConversationsDbContext>().Database.MigrateAsync();
        await sp.GetRequiredService<ChannelsDbContext>().Database.MigrateAsync();
        await sp.GetRequiredService<ContactCenterDbContext>().Database.MigrateAsync();
        await sp.GetRequiredService<AiGatewayDbContext>().Database.MigrateAsync();
        await sp.GetRequiredService<AgentsDbContext>().Database.MigrateAsync();
        await output.WriteLineAsync("Migrations aplicadas (nada a fazer se o banco já estava atualizado).");
    }

    internal static async Task PrintAsync(ProvisioningReport relatorio, ProvisioningSpec spec, TextWriter output)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync($"Empresa '{spec.Tenant.Slug}' - relatório do provisionamento");
        foreach (var etapa in relatorio.Steps)
        {
            var marca = etapa.Outcome switch
            {
                StepOutcome.Created => "CRIADO ",
                StepOutcome.Updated => "ATUALIZ.",
                StepOutcome.Exists => "JA EXISTE",
                StepOutcome.Skipped => "PULADO ",
                _ => "FALHOU ",
            };
            await output.WriteLineAsync($"  [{marca}] {etapa.Name}: {etapa.Detail}");
        }

        if (relatorio.GeneratedSecrets.Count > 0)
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync("SEGREDOS GERADOS (aparecem UMA vez; guarde agora, não há como recuperá-los):");
            foreach (var segredo in relatorio.GeneratedSecrets)
            {
                await output.WriteLineAsync($"  {segredo.Owner} - {segredo.Kind}: {segredo.Value}");
            }
        }

        await output.WriteLineAsync();
        if (relatorio.Succeeded)
        {
            await output.WriteLineAsync($"Pronto. Entrada na interface: tenant '{spec.Tenant.Slug}', e-mail do administrador {spec.Administrator.Email}.");
            if (relatorio.WebhookPath is not null)
            {
                await output.WriteLineAsync($"Webhook do WhatsApp: {relatorio.WebhookPath}");
            }
        }
        else
        {
            await output.WriteLineAsync("Terminou COM FALHA. Corrija o que a etapa acima aponta e rode o comando de novo: ele é idempotente e completa o que falta.");
        }
    }
}
