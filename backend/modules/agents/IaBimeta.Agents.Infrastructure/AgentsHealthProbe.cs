using IaBimeta.Agents.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Agents.Infrastructure;

public static class AgentsHealthExtensions
{
    /// <summary>Registra a sonda de saúde do Agents: turnos de IA vencidos sem executar, executor interrompido e turnos mortos recentes.</summary>
    public static IServiceCollection AddAgentsHealth(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProbe, AgentsHealthProbe>());
        return services;
    }
}

/// <summary>
/// A saúde dos turnos de IA: turno aberto e já vencido (agrupamento ou recuo) que o executor não pegou, turno em andamento com o lease vencido (o executor caiu) e turnos mortos nas últimas horas (a IA falhou de vez e a
/// conversa foi encaminhada ao atendente). Somente leitura; lê só estado, motivo curto e datas, nunca texto de conversa.
/// </summary>
internal sealed class AgentsHealthProbe : IHealthProbe
{
    private readonly IServiceScopeFactory _scopes;

    public AgentsHealthProbe(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public string Component => "agents";

    public async Task<IReadOnlyList<HealthFinding>> CheckAsync(HealthContext context, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var turnos = db.Turns.IgnoreQueryFilters().AsNoTracking();
        if (context.OnlyTenant is { } uma)
        {
            var tenant = new TenantId(uma);
            turnos = turnos.Where(t => t.TenantId == tenant);
        }

        var limite = context.Now - context.Thresholds.TurnStuckAfter;
        var achados = new List<HealthFinding>();

        // Agendado ou pronto e vencido: o executor não o pegou (ou a IA está indisponível e ele espera o recuo, que também vence).
        var vencidos = await turnos
            .Where(t => (t.State == AiTurnState.Scheduled && t.FireAt < limite) || (t.State == AiTurnState.Ready && (t.NextAttemptAt ?? t.FireAt) < limite))
            .GroupBy(t => t.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count(), Mais = g.Min(t => t.NextAttemptAt ?? t.FireAt) }).ToListAsync(cancellationToken);
        foreach (var v in vencidos)
        {
            achados.Add(new HealthFinding("agents", "turnos-vencidos", HealthSeverity.Critical,
                $"{v.Total} turno(s) de IA vencido(s) sem executar há mais de {HealthText.Age(context.Thresholds.TurnStuckAfter)} (o mais antigo há {HealthText.Age(context.Now - v.Mais)}): o executor do Worker não está andando.",
                "Confira se o Worker está no ar e se a IA do tenant está configurada (status --tenant). O cliente continua sem resposta enquanto isso.", v.Tenant.Value));
        }

        var interrompidos = await turnos.Where(t => t.State == AiTurnState.Running && t.LeaseExpiresAt < limite)
            .GroupBy(t => t.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count() }).ToListAsync(cancellationToken);
        foreach (var i in interrompidos)
        {
            achados.Add(new HealthFinding("agents", "turno-interrompido", HealthSeverity.Critical,
                $"{i.Total} turno(s) de IA com o lease vencido há mais de {HealthText.Age(context.Thresholds.TurnStuckAfter)}: o executor caiu no meio da chamada.",
                "Suba o Worker: o reconciliador recupera o turno (a resposta só é aceita uma vez, por cerca de versão).", i.Tenant.Value));
        }

        var janela = context.Now - context.Thresholds.RecentFailureWindow;
        var mortos = await turnos.Where(t => t.State == AiTurnState.Dead && t.EndedAt >= janela)
            .GroupBy(t => new { t.TenantId, t.EndReason }).Select(g => new { g.Key.TenantId, g.Key.EndReason, Total = g.Count() }).ToListAsync(cancellationToken);
        foreach (var grupo in mortos.GroupBy(m => m.TenantId))
        {
            var motivos = string.Join(", ", grupo.OrderByDescending(m => m.Total).Take(3).Select(m => $"{m.EndReason}={m.Total}"));
            achados.Add(new HealthFinding("agents", "turnos-mortos", HealthSeverity.Warning,
                $"{grupo.Sum(m => m.Total)} turno(s) de IA morto(s) nas últimas {HealthText.Age(context.Thresholds.RecentFailureWindow)} ({motivos}): a IA falhou de vez e a conversa seguiu para o atendente.",
                "Falha de cota ou de credencial exige ação do tenant na conta do provedor; ai-not-configured pede a configuração de IA e a definição publicada do agente (docs/operations/NOVA-EMPRESA.md).",
                grupo.Key.Value));
        }

        if (achados.Count == 0)
        {
            achados.Add(new HealthFinding("agents", "agents", HealthSeverity.Ok, "nenhum turno de IA vencido, interrompido ou morto na janela."));
        }

        return achados;
    }
}
