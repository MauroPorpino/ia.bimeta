namespace IaBimeta.SharedKernel;

public enum HealthSeverity
{
    Ok = 0,

    /// <summary>Precisa de atenção, mas nada se perde agora (morto para tratar, conversa esperando, resultado ambíguo).</summary>
    Warning = 1,

    /// <summary>Precisa de intervenção: algo parou de andar (evento parado, conta pausada, turno de IA parado).</summary>
    Critical = 2,
}

/// <summary>Uma constatação de uma sonda de saúde. <paramref name="Action"/> diz o que fazer; <paramref name="TenantId"/> aponta a empresa quando a constatação é de uma só.</summary>
public sealed record HealthFinding(string Component, string Check, HealthSeverity Severity, string Message, string? Action = null, Guid? TenantId = null);

/// <summary>
/// Os limites das verificações de saúde (seção <c>Health</c>): configuração, não constantes. O que passa do limite deixa de ser "em andamento" e vira constatação. Os padrões servem a homologação; a vazão real ajusta depois.
/// </summary>
public sealed class HealthThresholds
{
    public const string SectionName = "Health";

    /// <summary>Evento do outbox elegível (ou com lease vencido) e ainda não entregue há mais que isto: o relay não está andando.</summary>
    public TimeSpan EventStuckAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Mensagem de saída ainda sem desfecho (ou esperando nova tentativa) há mais que isto.</summary>
    public TimeSpan OutboundStuckAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Turno de IA aberto e já vencido (agrupamento, recuo ou lease) há mais que isto: o executor não está andando.</summary>
    public TimeSpan TurnStuckAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Conversa esperando atendente há mais que isto (aviso: pode ser só falta de atendente).</summary>
    public TimeSpan QueueWaitingAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A meta operacional para decidir uma possível saída do cliente (<c>PossibleOptOut</c>, ADR 0041): a pendência mais antiga acima disto vira aviso. Diretriz, SEM expiração automática: nenhum prazo revoga nem mantém sozinho.
    /// O padrão é de 24 h corridas (a aproximação de "um dia útil": o calendário de dias úteis da empresa não existe no produto).
    /// </summary>
    public TimeSpan OptOutReviewTarget { get; set; } = TimeSpan.FromHours(24);

    /// <summary>O Worker some do banco (nenhuma atividade das suas conexões) há mais que isto: parado ou travado.</summary>
    public TimeSpan WorkerSilenceAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A janela em que resultado ambíguo de envio (SendUnknown) e turno morto de IA ainda são constatados.</summary>
    public TimeSpan RecentFailureWindow { get; set; } = TimeSpan.FromHours(24);

    /// <exception cref="InvalidOperationException">Algum limite é zero ou negativo.</exception>
    public void Validate()
    {
        if (EventStuckAfter <= TimeSpan.Zero || OutboundStuckAfter <= TimeSpan.Zero || TurnStuckAfter <= TimeSpan.Zero || QueueWaitingAfter <= TimeSpan.Zero
            || WorkerSilenceAfter <= TimeSpan.Zero || RecentFailureWindow <= TimeSpan.Zero || OptOutReviewTarget <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("A configuração de Health é inválida: todo limite deve ser positivo.");
        }
    }
}

/// <summary>O que uma sonda recebe: os limites, o instante e, opcionalmente, uma empresa só (<c>status --tenant</c>).</summary>
public sealed record HealthContext(HealthThresholds Thresholds, DateTimeOffset Now, Guid? OnlyTenant = null)
{
    public bool Includes(Guid tenantId) => OnlyTenant is null || OnlyTenant == tenantId;
}

/// <summary>
/// Uma verificação de saúde de UM módulo sobre as tabelas dele (a fronteira vale: cada módulo lê só as suas). Somente leitura, sem filtro de tenant (lê o ambiente todo), sem conteúdo de mensagem nem dado pessoal na
/// constatação. Falha da sonda vira constatação crítica do orquestrador, nunca exceção para quem opera.
/// </summary>
public interface IHealthProbe
{
    /// <summary>O componente que a sonda cobre (<c>channels</c>, <c>agents</c>, <c>outbox:conversations</c>...).</summary>
    string Component { get; }

    Task<IReadOnlyList<HealthFinding>> CheckAsync(HealthContext context, CancellationToken cancellationToken = default);
}

/// <summary>Texto curto de idade para as mensagens das constatações.</summary>
public static class HealthText
{
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromSeconds(90))
        {
            return $"{(int)age.TotalSeconds} s";
        }

        if (age < TimeSpan.FromMinutes(90))
        {
            return $"{(int)age.TotalMinutes} min";
        }

        return age < TimeSpan.FromHours(48) ? $"{(int)age.TotalHours} h" : $"{(int)age.TotalDays} d";
    }
}
