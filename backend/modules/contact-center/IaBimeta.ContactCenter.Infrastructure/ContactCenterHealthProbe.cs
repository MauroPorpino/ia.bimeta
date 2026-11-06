using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.ContactCenter.Infrastructure;

public static class ContactCenterHealthExtensions
{
    /// <summary>Registra a sonda de saúde do ContactCenter: conversas esperando atendente há muito tempo e auditoria pendente de entrega.</summary>
    public static IServiceCollection AddContactCenterHealth(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProbe, ContactCenterHealthProbe>());
        return services;
    }
}

/// <summary>
/// A saúde da fila de atendimento: conversa esperando atendente há mais que o limite (aviso: pode ser só falta de atendente, mas é o cliente esperando) e auditoria que o ContactCenter ainda não conseguiu entregar ao
/// Audit (a entrega é feita pelo reconciliador do Worker). Somente leitura; lê contagens e datas, nunca o conteúdo da conversa.
/// </summary>
internal sealed class ContactCenterHealthProbe : IHealthProbe
{
    private readonly IServiceScopeFactory _scopes;

    public ContactCenterHealthProbe(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public string Component => "contact-center";

    public async Task<IReadOnlyList<HealthFinding>> CheckAsync(HealthContext context, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<ContactCenterDbContext>();
        var itens = db.Items.IgnoreQueryFilters().AsNoTracking();
        var auditorias = db.AuditOutbox.IgnoreQueryFilters().AsNoTracking();
        if (context.OnlyTenant is { } uma)
        {
            var tenant = new TenantId(uma);
            itens = itens.Where(i => i.TenantId == tenant);
            auditorias = auditorias.Where(a => a.TenantId == tenant);
        }

        var achados = new List<HealthFinding>();

        var esperando = context.Now - context.Thresholds.QueueWaitingAfter;
        var emEspera = await itens.Where(i => i.Status == QueueItemStatus.Waiting && i.EnteredAt < esperando)
            .GroupBy(i => i.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count(), Mais = g.Min(i => i.EnteredAt) }).ToListAsync(cancellationToken);
        foreach (var e in emEspera)
        {
            achados.Add(new HealthFinding("contact-center", "conversas-esperando", HealthSeverity.Warning,
                $"{e.Total} conversa(s) esperando atendente há mais de {HealthText.Age(context.Thresholds.QueueWaitingAfter)} (a mais antiga há {HealthText.Age(context.Now - e.Mais)}).",
                "Confira se há atendente disponível e atribuído à fila (presença, horários da fila); a conversa não se perde, mas o cliente espera.", e.Tenant.Value));
        }

        var paradas = context.Now - context.Thresholds.EventStuckAfter;
        var pendentes = await auditorias.Where(a => a.CreatedAt < paradas)
            .GroupBy(a => a.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count(), Mais = g.Min(a => a.CreatedAt) }).ToListAsync(cancellationToken);
        foreach (var p in pendentes)
        {
            achados.Add(new HealthFinding("contact-center", "auditoria-pendente", HealthSeverity.Warning,
                $"{p.Total} registro(s) de auditoria do ContactCenter sem entrega ao Audit há mais de {HealthText.Age(context.Thresholds.EventStuckAfter)} (o mais antigo há {HealthText.Age(context.Now - p.Mais)}).",
                "O reconciliador do Worker os entrega; confira se o Worker está no ar e se o Audit está acessível. Nada se perde: o registro fica durável até a entrega.", p.Tenant.Value));
        }

        if (achados.Count == 0)
        {
            achados.Add(new HealthFinding("contact-center", "contact-center", HealthSeverity.Ok, "nenhuma conversa esperando além do limite e nenhuma auditoria parada."));
        }

        return achados;
    }
}
