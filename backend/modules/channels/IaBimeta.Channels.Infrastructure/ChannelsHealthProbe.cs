using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Channels.Infrastructure;

public static class ChannelsHealthExtensions
{
    /// <summary>Registra a sonda de saúde do Channels: contas com envio pausado e mensagens de saída paradas ou de resultado ambíguo.</summary>
    public static IServiceCollection AddChannelsHealth(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProbe, ChannelsHealthProbe>());
        return services;
    }
}

/// <summary>
/// A saúde do Channels sobre as tabelas dele: conta com o envio pausado (o atendente não consegue responder ao cliente por ela), mensagem de saída sem desfecho há mais que o limite (o Sender não está andando) e mensagens de
/// resultado ambíguo (SendUnknown), que nunca são reenviadas sozinhas. Somente leitura; não lê texto de mensagem nem número de cliente.
/// </summary>
internal sealed class ChannelsHealthProbe : IHealthProbe
{
    private readonly IServiceScopeFactory _scopes;

    public ChannelsHealthProbe(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public string Component => "channels";

    public async Task<IReadOnlyList<HealthFinding>> CheckAsync(HealthContext context, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var achados = new List<HealthFinding>();

        var contas = db.SendStates.IgnoreQueryFilters().AsNoTracking().Where(s => s.IsPaused);
        var saidas = db.OutboundMessages.IgnoreQueryFilters().AsNoTracking();
        if (context.OnlyTenant is { } uma)
        {
            var tenant = new TenantId(uma);
            contas = contas.Where(s => s.TenantId == tenant);
            saidas = saidas.Where(m => m.TenantId == tenant);
        }

        // Contas pausadas: o motivo é um código curto (sem dado de cliente).
        var pausadas = await contas.Select(s => new { s.TenantId, s.AccountId, s.PauseReason, s.PausedAt }).Take(50).ToListAsync(cancellationToken);
        foreach (var conta in pausadas)
        {
            var desde = conta.PausedAt is { } quando ? $" desde {quando:yyyy-MM-dd HH:mm} UTC (há {HealthText.Age(context.Now - quando)})" : string.Empty;
            achados.Add(new HealthFinding("channels", "conta-pausada", HealthSeverity.Critical,
                $"conta {conta.AccountId.ToString("N")[..8]} com o envio PAUSADO{desde}: {conta.PauseReason ?? "sem motivo registrado"}. O atendente não consegue responder ao cliente por ela.",
                "Corrija a causa (token de acesso expirado ou revogado, credencial) e retome com prova: tools\\ops\\Test-ProntidaoWhatsApp.ps1 -Live e POST /channels/accounts/{id}/sending/resume (docs/operations/WHATSAPP-CONEXAO-REAL.md).",
                conta.TenantId.Value));
        }

        // Saída parada: intenção sem desfecho ou esperando nova tentativa há mais que o limite.
        var limiteDaSaida = context.Now - context.Thresholds.OutboundStuckAfter;
        var paradas = await saidas
            .Where(m => ((m.State == OutboundState.Dispatching && m.Attempts == 0) || m.State == OutboundState.Retry) && m.UpdatedAt < limiteDaSaida)
            .GroupBy(m => m.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count(), Mais = g.Min(m => m.UpdatedAt) }).ToListAsync(cancellationToken);
        foreach (var p in paradas)
        {
            achados.Add(new HealthFinding("channels", "saida-parada", HealthSeverity.Critical,
                $"{p.Total} mensagem(ns) de saída sem desfecho há mais de {HealthText.Age(context.Thresholds.OutboundStuckAfter)} (a mais antiga há {HealthText.Age(context.Now - p.Mais)}): o Sender do Worker não está andando.",
                "Confira se o Worker está no ar; ele retoma o envio sem duplicar (a intenção é gravada antes de chamar o provedor). tools\\ops\\Get-DiagnosticoDeEnvio.ps1 mostra cada uma.",
                p.Tenant.Value));
        }

        // Chamada ao provedor em curso há muito tempo: o desfecho é desconhecido, e o relay a marcará como resultado ambíguo.
        var emCurso = await saidas
            .Where(m => m.State == OutboundState.Dispatching && m.Attempts > 0 && m.UpdatedAt < limiteDaSaida)
            .GroupBy(m => m.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count() }).ToListAsync(cancellationToken);
        foreach (var p in emCurso)
        {
            achados.Add(new HealthFinding("channels", "chamada-sem-desfecho", HealthSeverity.Warning,
                $"{p.Total} mensagem(ns) com a chamada ao provedor sem desfecho há mais de {HealthText.Age(context.Thresholds.OutboundStuckAfter)}: serão marcadas como resultado ambíguo (nunca reenviadas sozinhas).",
                "Se persistir, confira o Worker e o provedor; a mensagem pode ou não ter sido entregue ao cliente.",
                p.Tenant.Value));
        }

        // Resultado ambíguo recente: pode ter sido enviada; nunca é reenviada automaticamente.
        var janela = context.Now - context.Thresholds.RecentFailureWindow;
        var ambiguas = await saidas.Where(m => m.State == OutboundState.SendUnknown && m.UpdatedAt >= janela)
            .GroupBy(m => m.TenantId).Select(g => new { Tenant = g.Key, Total = g.Count() }).ToListAsync(cancellationToken);
        foreach (var p in ambiguas)
        {
            achados.Add(new HealthFinding("channels", "resultado-ambiguo", HealthSeverity.Warning,
                $"{p.Total} mensagem(ns) de saída com resultado ambíguo nas últimas {HealthText.Age(context.Thresholds.RecentFailureWindow)}: podem ter sido entregues e nunca são reenviadas sozinhas.",
                "Confira com o cliente ou pela confirmação de entrega do provedor; o reenvio só por ação autorizada do atendente.",
                p.Tenant.Value));
        }

        if (achados.Count == 0)
        {
            achados.Add(new HealthFinding("channels", "channels", HealthSeverity.Ok, "nenhuma conta pausada e nenhuma mensagem de saída parada ou ambígua."));
        }

        return achados;
    }
}
