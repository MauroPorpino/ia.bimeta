using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Conversations.Infrastructure;

public static class ConversationsHealthExtensions
{
    /// <summary>Registra a sonda de saúde do Conversations: as possíveis saídas de clientes (<c>PossibleOptOut</c>) ainda sem decisão.</summary>
    public static IServiceCollection AddConversationsHealth(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProbe, ConversationsHealthProbe>());
        return services;
    }
}

/// <summary>
/// As ocorrências de possível saída do cliente que ainda esperam a decisão de quem tem <c>ConsentManage</c> (ADR 0041, seção 5; E2.5): a quantidade e a idade da mais antiga, por empresa. Passar da meta operacional
/// (<see cref="HealthThresholds.OptOutReviewTarget"/>) é aviso, NUNCA expiração: nenhum prazo revoga nem mantém o consentimento sozinho. Somente leitura; lê contagens e datas, nunca o texto da mensagem nem o telefone.
/// </summary>
internal sealed class ConversationsHealthProbe : IHealthProbe
{
    private readonly IServiceScopeFactory _scopes;

    public ConversationsHealthProbe(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public string Component => "consentimento";

    public async Task<IReadOnlyList<HealthFinding>> CheckAsync(HealthContext context, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var pendentes = db.OptOutReviews.IgnoreQueryFilters().AsNoTracking().Where(o => o.Resolution == null);
        if (context.OnlyTenant is { } uma)
        {
            var tenant = new TenantId(uma);
            pendentes = pendentes.Where(o => o.TenantId == tenant);
        }

        var porEmpresa = await pendentes.GroupBy(o => o.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count(), Mais = g.Min(o => o.OpenedAt) }).ToListAsync(cancellationToken);
        var achados = new List<HealthFinding>();
        foreach (var e in porEmpresa)
        {
            var idade = context.Now - e.Mais;
            var acima = idade > context.Thresholds.OptOutReviewTarget;
            achados.Add(new HealthFinding(
                "consentimento",
                "possivel-saida-pendente",
                acima ? HealthSeverity.Warning : HealthSeverity.Ok,
                $"{e.Total} possível(is) saída(s) de cliente aguardando decisão (a mais antiga há {HealthText.Age(idade)})" + (acima ? $", acima da meta de {HealthText.Age(context.Thresholds.OptOutReviewTarget)}." : "."),
                acima ? "Um supervisor ou administrador (ConsentManage) decide pela conversa: Revoked ou Kept. Nenhum prazo revoga nem mantém o consentimento sozinho." : null,
                e.Tenant.Value));
        }

        if (achados.Count == 0)
        {
            achados.Add(new HealthFinding("consentimento", "consentimento", HealthSeverity.Ok, "nenhuma possível saída de cliente aguardando decisão."));
        }

        return achados;
    }
}
