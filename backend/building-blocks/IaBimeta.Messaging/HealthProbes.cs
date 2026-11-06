using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.Messaging;

public static class HealthExtensions
{
    /// <summary>Registra a sonda de saúde do outbox do módulo (eventos parados, lease vencido e mortos). O DbContext é o do módulo emissor.</summary>
    public static IServiceCollection AddOutboxHealth<TContext>(this IServiceCollection services, string module)
        where TContext : DbContext
    {
        services.AddSingleton<IHealthProbe>(provider => new OutboxHealthProbe<TContext>(provider.GetRequiredService<IServiceScopeFactory>(), module));
        return services;
    }
}

/// <summary>
/// A saúde do outbox de UM módulo (<c>&lt;módulo&gt;.outbox_message</c>): evento elegível que o relay não pegou há mais que o limite (o Worker não está andando), evento com lease vencido há mais que o limite (o relay
/// caiu no meio da entrega e ninguém retomou) e eventos mortos (para tratar). Somente leitura; nenhum conteúdo de payload.
/// </summary>
public sealed class OutboxHealthProbe<TContext> : IHealthProbe
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;

    public OutboxHealthProbe(IServiceScopeFactory scopes, string module)
    {
        _scopes = scopes;
        Component = "outbox:" + module;
    }

    public string Component { get; }

    public async Task<IReadOnlyList<HealthFinding>> CheckAsync(HealthContext context, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();
        var eventos = db.Set<OutboxMessage>().IgnoreQueryFilters().AsNoTracking();
        if (context.OnlyTenant is { } so)
        {
            eventos = eventos.Where(o => o.TenantId == so);
        }

        var limite = context.Now - context.Thresholds.EventStuckAfter;
        var achados = new List<HealthFinding>();

        var parados = await eventos.Where(o => o.State == OutboxState.Pending && o.NextAttemptAt < limite)
            .GroupBy(o => o.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count(), Mais = g.Min(o => o.NextAttemptAt) }).ToListAsync(cancellationToken);
        foreach (var p in parados)
        {
            achados.Add(new HealthFinding(Component, "eventos-parados", HealthSeverity.Critical,
                $"{p.Total} evento(s) elegível(is) sem ser entregue(s) há mais de {HealthText.Age(context.Thresholds.EventStuckAfter)} (o mais antigo há {HealthText.Age(context.Now - p.Mais)}): o relay do Worker não está andando.",
                "Confira se o Worker está no ar e o log do relay. Reiniciar o Worker entrega o que ficou parado, sem perda (entrega pelo menos uma vez, com idempotência por evento).", p.Tenant));
        }

        var leaseVencido = await eventos.Where(o => o.State == OutboxState.Processing && o.LeaseExpiresAt < limite)
            .GroupBy(o => o.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count(), Mais = g.Min(o => o.LeaseExpiresAt) }).ToListAsync(cancellationToken);
        foreach (var p in leaseVencido)
        {
            achados.Add(new HealthFinding(Component, "lease-vencido", HealthSeverity.Critical,
                $"{p.Total} evento(s) com a entrega interrompida (lease vencido há mais de {HealthText.Age(context.Thresholds.EventStuckAfter)}): o relay caiu no meio e nenhum outro assumiu.",
                "Suba o Worker: o evento volta a ser elegível e é reentregue sem duplicar o efeito.", p.Tenant));
        }

        var mortos = await eventos.Where(o => o.State == OutboxState.Dead)
            .GroupBy(o => o.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count(), Mais = g.Min(o => o.CreatedAt) }).ToListAsync(cancellationToken);
        foreach (var m in mortos)
        {
            achados.Add(new HealthFinding(Component, "eventos-mortos", HealthSeverity.Warning,
                $"{m.Total} evento(s) morto(s) (falha permanente ou tentativas esgotadas); o mais antigo é de {m.Mais:yyyy-MM-dd}.",
                "Veja tools\\ops\\Get-DiagnosticoDeEnvio.ps1 e o motivo de cada um; reenfileirar ou descartar só com autorização (nunca apagar à mão). Mortos antigos também bloqueiam o expurgo das marcas do Inbox.", m.Tenant));
        }

        if (achados.Count == 0)
        {
            achados.Add(new HealthFinding(Component, "outbox", HealthSeverity.Ok, "sem eventos parados, interrompidos ou mortos."));
        }

        return achados;
    }
}
