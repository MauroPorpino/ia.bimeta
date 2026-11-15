using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Workflows.Core.Domain;
using IaBimeta.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Api.Operations;

/// <summary>
/// O comando <c>workflows</c> do host (ADR 0038): a drenagem segura do chatbot visual. <c>workflows status</c> (somente leitura) mostra, por empresa e situação, quantas instâncias de fluxo estão ativas e há quanto tempo a
/// mais antiga espera. <c>workflows drain --confirm [--tenant slug]</c> ENCERRA as instâncias ativas: cada uma vira "cancelada" (<c>operator-drain</c>), seus efeitos abertos ficam "substituídos" (nada mais é enviado) e a
/// conversa passa em definitivo ao agente de IA, que responde à próxima mensagem do cliente. Roda na máquina de quem opera, com acesso ao banco; não depende do Worker (serve também com o runtime desligado).
/// Procedimento completo em <c>docs/operations/CHATBOT-VISUAL.md</c> (§5). Sai com 0, 1 (falha) ou 2 (uso inválido ou drain sem --confirm).
/// </summary>
internal static class WorkflowsCommand
{
    public const string Verb = "workflows";

    /// <summary>A origem do contexto de SISTEMA com que a drenagem avisa o agente (sem usuário; o tenant vem da instância encerrada, nunca de argumento livre).</summary>
    internal const string Origin = "workflows-drain";

    internal const string Usage = "Uso: workflows status | workflows drain --confirm [--tenant <slug>]";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is not ("status" or "drain"))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        var drain = args[0] == "drain";
        var confirmado = false;
        string? slug = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--confirm" && drain)
            {
                confirmado = true;
            }
            else if (args[i] == "--tenant" && drain && i + 1 < args.Length)
            {
                slug = args[++i];
            }
            else
            {
                await output.WriteLineAsync($"Argumento desconhecido: {args[i]}");
                await output.WriteLineAsync(Usage);
                return 2;
            }
        }

        if (drain && !confirmado)
        {
            await output.WriteLineAsync("A drenagem só roda com --confirm. Rode antes 'workflows status' e confira o que será encerrado.");
            return 2;
        }

        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        return await ExecuteAsync(app.Services, drain, slug, output);
    }

    internal static async Task<int> ExecuteAsync(IServiceProvider root, bool drain, string? tenantSlug, TextWriter output, CancellationToken cancellationToken = default)
    {
        await using var escopo = root.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();

        TenantId? tenant = null;
        if (tenantSlug is not null)
        {
            var info = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(tenantSlug, cancellationToken);
            if (info is null)
            {
                await output.WriteLineAsync("Empresa não encontrada.");
                return 1;
            }

            tenant = info.TenantId;
        }

        IQueryable<FlowInstance> ativas() =>
            db.Instances.IgnoreQueryFilters().Where(i => i.Status < FlowStatus.Completed && (tenant == null || i.TenantId == tenant));

        var linhas = await ativas().GroupBy(i => new { i.TenantId, i.Status }).Select(g => new { g.Key.TenantId, g.Key.Status, Quantidade = g.Count(), Mais = g.Min(i => i.CreatedAt) }).ToListAsync(cancellationToken);
        await output.WriteLineAsync(drain ? "Instâncias ativas antes da drenagem:" : "Instâncias de fluxo ativas (somente leitura):");
        if (linhas.Count == 0)
        {
            await output.WriteLineAsync("  nenhuma");
        }

        foreach (var l in linhas.OrderBy(l => l.TenantId.Value).ThenBy(l => l.Status))
        {
            await output.WriteLineAsync($"  empresa {l.TenantId.Value:N} · {l.Status,-15} · {l.Quantidade,5} · a mais antiga de {l.Mais:yyyy-MM-dd HH:mm}Z");
        }

        if (!drain)
        {
            return 0;
        }

        var agora = DateTimeOffset.UtcNow;
        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        var afetadas = await ativas().Select(i => new { i.TenantId, i.SessionId }).Distinct().ToListAsync(cancellationToken);
        var ids = ativas().Select(i => i.Id);
        var sessoes = ativas().Select(i => i.SessionId);

        // Ordem: a conversa volta ao agente, o que estava por enviar é descartado e, por fim, a instância é encerrada.
        var decisoes = await db.Decisions.IgnoreQueryFilters().Where(d => d.Owner == IaBimeta.Workflows.Contracts.AutomationOwner.Flow && sessoes.Contains(d.SessionId))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Owner, IaBimeta.Workflows.Contracts.AutomationOwner.Agent).SetProperty(d => d.UpdatedAt, agora).SetProperty(d => d.Revision, d => d.Revision + 1), cancellationToken);
        var efeitos = await db.Effects.IgnoreQueryFilters().Where(e => (e.State == EffectState.Pending || e.State == EffectState.InFlight) && ids.Contains(e.InstanceId))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.State, EffectState.Superseded).SetProperty(e => e.ResultCode, "operator-drain").SetProperty(e => e.CompletedAt, agora).SetProperty(e => e.PayloadCipher, string.Empty).SetProperty(e => e.PayloadKeyId, string.Empty), cancellationToken);
        var instancias = await ativas().ExecuteUpdateAsync(
            s => s.SetProperty(i => i.Status, FlowStatus.Cancelled).SetProperty(i => i.FailureCode, "operator-drain").SetProperty(i => i.NextRunAt, (DateTimeOffset?)null).SetProperty(i => i.WaitUntil, (DateTimeOffset?)null)
                .SetProperty(i => i.PendingEffectKey, (string?)null).SetProperty(i => i.LeaseOwner, (string?)null).SetProperty(i => i.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(i => i.EndedAt, agora).SetProperty(i => i.UpdatedAt, agora).SetProperty(i => i.Revision, i => i.Revision + 1),
            cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        await output.WriteLineAsync($"Drenagem concluída: {instancias} instância(s) encerrada(s), {decisoes} conversa(s) devolvida(s) ao agente, {efeitos} efeito(s) descartado(s).");

        // Avisa o agente: SEM este sinal ele só reavalia a conversa na próxima mensagem do cliente, e uma mensagem que chegou enquanto o fluxo era o dono (e que o fluxo, já cancelado, nunca respondeu) ficaria sem
        // resposta em silêncio. O comando é o mesmo que o runtime usa ao devolver uma conversa; é idempotente (o Agents reavalia o estado real e responde uma só vez), então repetir é seguro.
        var falhas = 0;
        foreach (var porEmpresa in afetadas.GroupBy(a => a.TenantId))
        {
            await using var escopoEmpresa = root.CreateAsyncScope();
            escopoEmpresa.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(porEmpresa.Key, Origin));
            var comandos = escopoEmpresa.ServiceProvider.GetRequiredService<IFlowCommands>();
            foreach (var sessao in porEmpresa.Select(a => a.SessionId).Distinct())
            {
                try
                {
                    await comandos.ReleaseSessionToAgentAsync(new ReleaseFlowCommand(sessao, "operator-drain"), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    falhas++;
                    await output.WriteLineAsync($"  AVISO: não foi possível avisar o agente sobre a conversa {sessao:N} ({ex.GetType().Name}). A conversa já é do agente e ele a retoma na próxima mensagem do cliente.");
                }
            }
        }

        if (falhas > 0)
        {
            await output.WriteLineAsync($"{falhas} conversa(s) sem o aviso imediato ao agente: confira com o atendimento se há mensagem do cliente sem resposta.");
            return 1;
        }

        return 0;
    }
}
