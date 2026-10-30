using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Domain;

/// <summary>O que fazer com um atendimento que chega com a fila fechada (fora do horário).</summary>
public enum OutOfHoursBehavior
{
    /// <summary>A conversa entra na fila e espera a abertura (padrão).</summary>
    Accept = 1,

    /// <summary>O pedido é recusado: quem pediu (o Agents) informa o cliente.</summary>
    Reject = 2,

    /// <summary>A conversa vai para a fila de contingência da fila fechada.</summary>
    Fallback = 3,
}

/// <summary>O que fazer com um atendimento que chega quando a fila não tem atendente disponível.</summary>
public enum NoAgentBehavior
{
    /// <summary>A conversa entra na fila e espera um atendente (padrão).</summary>
    Wait = 1,

    /// <summary>A conversa vai para a fila de contingência, se ela tiver atendente disponível; senão espera aqui.</summary>
    Fallback = 2,
}

/// <summary>Uma faixa semanal em que a fila atende, no fuso horário da fila: do início (inclusive) ao fim (exclusive), em minutos desde a meia-noite.</summary>
public sealed class QueueScheduleWindow : ITenantOwned
{
    private QueueScheduleWindow()
    {
        TenantId = null!;
    }

    public TenantId TenantId { get; private set; }

    public Guid QueueId { get; private set; }

    public DayOfWeek DayOfWeek { get; private set; }

    public int StartMinute { get; private set; }

    public int EndMinute { get; private set; }

    public static QueueScheduleWindow Create(TenantId tenantId, Guid queueId, DayOfWeek dayOfWeek, int startMinute, int endMinute)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (!Enum.IsDefined(dayOfWeek))
        {
            throw new ContactCenterValidationException("O dia da semana da faixa de horário é inválido.");
        }

        if (startMinute < 0 || endMinute > QueueSchedule.MinutesPerDay || endMinute <= startMinute)
        {
            throw new ContactCenterValidationException("A faixa de horário deve começar a partir de 00:00, terminar até 24:00 e ter o fim depois do início.");
        }

        return new QueueScheduleWindow { TenantId = tenantId, QueueId = queueId, DayOfWeek = dayOfWeek, StartMinute = startMinute, EndMinute = endMinute };
    }
}

/// <summary>Um dia em que a fila não atende, mesmo que a faixa semanal diga o contrário (feriado, recesso). A data é a do fuso horário da fila.</summary>
public sealed class QueueClosure : ITenantOwned
{
    private QueueClosure()
    {
        TenantId = null!;
    }

    public TenantId TenantId { get; private set; }

    public Guid QueueId { get; private set; }

    public DateOnly Date { get; private set; }

    public static QueueClosure Create(TenantId tenantId, Guid queueId, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return new QueueClosure { TenantId = tenantId, QueueId = queueId, Date = date };
    }
}

/// <summary>
/// O horário de atendimento de uma fila, com fuso horário EXPLÍCITO (identificador IANA, por exemplo <c>America/Sao_Paulo</c>): o
/// mesmo instante cai em dias e horas diferentes conforme o fuso, e o horário de verão é o do próprio fuso. Uma fila sem horário
/// (nenhuma faixa, nenhum fuso) atende sempre. Uma faixa não atravessa a meia-noite: um turno da noite são duas faixas, uma em
/// cada dia. Função pura sobre o instante informado: não lê relógio.
/// </summary>
public sealed class QueueSchedule
{
    public const int MinutesPerDay = 24 * 60;
    public const int TimeZoneMaxLength = 64;
    public const int MaxWindows = 56;
    public const int MaxClosures = 400;

    private readonly TimeZoneInfo _zone;
    private readonly IReadOnlyList<QueueScheduleWindow> _windows;
    private readonly HashSet<DateOnly> _closures;

    public QueueSchedule(string timeZoneId, IEnumerable<QueueScheduleWindow> windows, IEnumerable<DateOnly> closedDates)
    {
        _zone = FindZone(timeZoneId);
        TimeZoneId = timeZoneId;
        _windows = windows.ToList();
        _closures = closedDates.ToHashSet();
    }

    public string TimeZoneId { get; }

    /// <summary>
    /// Valida o identificador do fuso e o devolve. Só aceita identificadores IANA existentes no sistema: um fuso inexistente nunca é
    /// trocado por outro (nem pelo do servidor).
    /// </summary>
    /// <exception cref="ContactCenterValidationException">O fuso horário é vazio, grande demais ou desconhecido.</exception>
    public static string ValidateTimeZone(string? timeZoneId)
    {
        var id = (timeZoneId ?? string.Empty).Trim();
        if (id.Length == 0 || id.Length > TimeZoneMaxLength)
        {
            throw new ContactCenterValidationException($"O fuso horário da fila é obrigatório e tem até {TimeZoneMaxLength} caracteres.");
        }

        FindZone(id);
        return id;
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ContactCenterValidationException("O fuso horário da fila é desconhecido: informe o identificador IANA (por exemplo, America/Sao_Paulo).");
        }
    }

    /// <summary>A data e a hora na fila (no fuso dela) no instante informado.</summary>
    public DateTime LocalTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _zone).DateTime;

    /// <summary>A fila atende neste instante? O dia fechado vence a faixa semanal.</summary>
    public bool IsOpenAt(DateTimeOffset instant)
    {
        var local = LocalTime(instant);
        if (_closures.Contains(DateOnly.FromDateTime(local)))
        {
            return false;
        }

        var minuto = (local.Hour * 60) + local.Minute;
        return _windows.Any(w => w.DayOfWeek == local.DayOfWeek && minuto >= w.StartMinute && minuto < w.EndMinute);
    }
}
