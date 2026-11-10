using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Api.Operations;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Api.Provisioning;

/// <summary>
/// O comando <c>migrate</c> do host (ADR 0033): <c>migrate</c> aplica as migrations pendentes de TODOS os módulos (cada um tem o seu esquema e o seu histórico) e <c>migrate --list</c> só mostra o que está pendente, sem
/// alterar nada (sai com 1 se houver pendência, para a implantação decidir antes de atualizar). Em produção o Api e o Worker NÃO aplicam migrations ao subir (só o ambiente de desenvolvimento faz isso): a implantação roda este
/// comando, <b>depois de um backup</b>. Sobe a MESMA composição do Api, sem abrir porta nem iniciar serviço. Só os identificadores das migrations saem: nenhuma string de conexão, senha ou dado de empresa. Saída: 0 (em dia ou
/// aplicado), 1 (pendente, com <c>--list</c>, ou falha) ou 2 (uso inválido).
/// </summary>
internal static class MigrateCommand
{
    public const string Verb = "migrate";

    internal const string Usage = "Uso: migrate [--list]";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        var somenteListar = false;
        foreach (var argumento in args)
        {
            if (argumento == "--list")
            {
                somenteListar = true;
                continue;
            }

            await output.WriteLineAsync($"Argumento desconhecido: {argumento}");
            await output.WriteLineAsync(Usage);
            return 2;
        }

        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        return await ExecuteAsync(app.Services, apply: !somenteListar, output);
    }

    /// <summary>Os contextos de todos os módulos, na ordem em que as migrations são aplicadas. Um módulo novo entra AQUI (o provisionamento e o <c>migrate</c> usam a mesma lista).</summary>
    internal static IReadOnlyList<(string Name, DbContext Context)> Modules(IServiceProvider services) =>
    [
        ("Tenancy", services.GetRequiredService<TenancyDbContext>()),
        ("Audit", services.GetRequiredService<AuditDbContext>()),
        ("Identity", services.GetRequiredService<IdentityDbContext>()),
        ("Conversations", services.GetRequiredService<ConversationsDbContext>()),
        ("Channels", services.GetRequiredService<ChannelsDbContext>()),
        ("ContactCenter", services.GetRequiredService<ContactCenterDbContext>()),
        ("AiGateway", services.GetRequiredService<AiGatewayDbContext>()),
        ("Agents", services.GetRequiredService<AgentsDbContext>()),
    ];

    internal static async Task<int> ExecuteAsync(IServiceProvider root, bool apply, TextWriter output, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var escopo = root.CreateAsyncScope();
            await output.WriteLineAsync(apply ? "Migrations (aplicando as pendentes):" : "Migrations (só listando; nada é alterado):");
            var pendentes = 0;
            foreach (var (nome, contexto) in Modules(escopo.ServiceProvider))
            {
                var faltam = (await contexto.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (faltam.Count == 0)
                {
                    await output.WriteLineAsync($"  {nome,-14} em dia");
                    continue;
                }

                pendentes += faltam.Count;
                if (!apply)
                {
                    await output.WriteLineAsync($"  {nome,-14} {faltam.Count} PENDENTE(S): {string.Join(", ", faltam)}");
                    continue;
                }

                await contexto.Database.MigrateAsync(cancellationToken);
                await output.WriteLineAsync($"  {nome,-14} {faltam.Count} aplicada(s): {string.Join(", ", faltam)}");
            }

            if (pendentes == 0)
            {
                await output.WriteLineAsync("Todos os módulos estão em dia.");
                return 0;
            }

            if (apply)
            {
                await output.WriteLineAsync($"{pendentes} migration(s) aplicada(s).");
                return 0;
            }

            await output.WriteLineAsync($"{pendentes} migration(s) pendente(s): rode 'migrate' (depois de um backup) para aplicá-las.");
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Só o tipo da falha: a mensagem de um erro de conexão pode trazer o servidor e o usuário.
            await output.WriteLineAsync($"FALHA ao consultar ou aplicar as migrations ({ex.GetType().Name}). Confira o banco e a configuração (ConnectionStrings) e rode de novo.");
            return 1;
        }
    }
}
