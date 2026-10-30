using System.Globalization;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>Uma faixa semanal pedida: dia da semana, início e fim em <c>HH:mm</c> (o fim pode ser <c>24:00</c>).</summary>
public sealed record ScheduleWindowInput(DayOfWeek DayOfWeek, string Start, string End);

/// <summary>
/// A política de uma fila (o que fazer fora do horário e sem atendente, a contingência, a prioridade padrão e o limite por atendente),
/// o horário de atendimento dela com fuso explícito, e a disponibilidade dos atendentes. Quem gere as filas da organização muda; quem
/// lê as filas (ou está na fila) vê. Toda mudança vai para a auditoria, e a versão da fila decide a disputa entre duas edições.
/// </summary>
public sealed class QueuePolicyService
{
    private const string QueueResource = "service-queue";

    private readonly IQueueRepository _queues;
    private readonly IRoutingRepository _routing;
    private readonly IQueueItemRepository _items;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IDeferredAudit _audit;

    public QueuePolicyService(
        IQueueRepository queues,
        IRoutingRepository routing,
        IQueueItemRepository items,
        IRequestContextAccessor context,
        TimeProvider clock,
        IDeferredAudit audit)
    {
        _queues = queues;
        _routing = routing;
        _items = items;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>
    /// Define a política da fila. A contingência precisa ser de outra fila da MESMA organização. Repetir a mesma política não muda
    /// nada nem grava auditoria.
    /// </summary>
    /// <exception cref="ContactCenterResourceNotFoundException">A fila não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir filas da organização.</exception>
    /// <exception cref="ContactCenterValidationException">Política inválida, ou contingência que não existe nesta organização.</exception>
    /// <exception cref="ContactCenterConflictException">Outra edição da fila aconteceu primeiro.</exception>
    public async Task<ServiceQueueView> ConfigureAsync(
        Guid queueId, Guid? fallbackQueueId, OutOfHoursBehavior outOfHours, NoAgentBehavior noAgent, int defaultPriority, int? maxItemsPerAgent,
        CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var fila = await FindManageableAsync(contexto, queueId, "contact-center.queue.configure", cancellationToken);

        if (fallbackQueueId is { } contingencia && contingencia != Guid.Empty && contingencia != fila.Id)
        {
            // A mesma mensagem para fila inexistente e para fila de outra organização: não se descobre, por tentativa, o que existe nas outras.
            var alvo = await _queues.FindQueueReadOnlyAsync(contingencia, cancellationToken);
            if (alvo is null || alvo.OrganizationId != fila.OrganizationId)
            {
                throw new ContactCenterValidationException("A fila de contingência não existe nesta organização.");
            }
        }

        if (fila.Configure(fallbackQueueId, outOfHours, noAgent, defaultPriority, maxItemsPerAgent, _clock.GetUtcNow()))
        {
            _audit.Defer(new AuditEntry("contact-center.queue.configure", QueueResource, fila.Id.ToString(), AuditOutcome.Success));
            await _queues.SaveChangesAsync(cancellationToken);
            await AuditedAccess.DeliverAsync(_audit, cancellationToken);
        }

        return ServiceQueueView.From(fila);
    }

    /// <summary>
    /// Troca o horário de atendimento da fila (fuso, faixas semanais e dias fechados) por inteiro, na mesma gravação. As faixas de um
    /// mesmo dia não se sobrepõem e nenhuma atravessa a meia-noite (um turno da noite são duas faixas).
    /// </summary>
    public async Task<QueueScheduleView> SetScheduleAsync(
        Guid queueId, string timeZoneId, IReadOnlyList<ScheduleWindowInput> windows, IReadOnlyList<DateOnly> closedDates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(closedDates);
        var contexto = _context.GetRequired();
        var fila = await FindManageableAsync(contexto, queueId, "contact-center.queue.schedule.set", cancellationToken);

        var tz = QueueSchedule.ValidateTimeZone(timeZoneId);
        if (windows.Count > QueueSchedule.MaxWindows || closedDates.Count > QueueSchedule.MaxClosures)
        {
            throw new ContactCenterValidationException($"O horário aceita até {QueueSchedule.MaxWindows} faixas e {QueueSchedule.MaxClosures} dias fechados.");
        }

        var faixas = windows
            .Select(w => QueueScheduleWindow.Create(contexto.TenantId, fila.Id, w.DayOfWeek, ParseMinute(w.Start, allow24: false), ParseMinute(w.End, allow24: true)))
            .ToList();
        foreach (var dia in faixas.GroupBy(f => f.DayOfWeek))
        {
            var ordenadas = dia.OrderBy(f => f.StartMinute).ToList();
            for (var i = 1; i < ordenadas.Count; i++)
            {
                if (ordenadas[i].StartMinute < ordenadas[i - 1].EndMinute)
                {
                    throw new ContactCenterValidationException("As faixas de um mesmo dia não podem se sobrepor.");
                }
            }
        }

        var fechamentos = closedDates.Distinct().Select(d => QueueClosure.Create(contexto.TenantId, fila.Id, d)).ToList();

        fila.ReplaceSchedule(tz, _clock.GetUtcNow());
        _audit.Defer(new AuditEntry("contact-center.queue.schedule.set", QueueResource, fila.Id.ToString(), AuditOutcome.Success));
        await _routing.ReplaceScheduleAsync(fila, faixas, fechamentos, cancellationToken);
        await AuditedAccess.DeliverAsync(_audit, cancellationToken);
        return await ScheduleViewAsync(fila.Id, cancellationToken);
    }

    /// <summary>Remove o horário: a fila volta a atender sempre. Repetir é inofensivo e não grava auditoria.</summary>
    public async Task<QueueScheduleView> ClearScheduleAsync(Guid queueId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var fila = await FindManageableAsync(contexto, queueId, "contact-center.queue.schedule.clear", cancellationToken);

        if (fila.TimeZoneId is not null)
        {
            fila.ReplaceSchedule(null, _clock.GetUtcNow());
            _audit.Defer(new AuditEntry("contact-center.queue.schedule.clear", QueueResource, fila.Id.ToString(), AuditOutcome.Success));
            await _routing.ReplaceScheduleAsync(fila, Array.Empty<QueueScheduleWindow>(), Array.Empty<QueueClosure>(), cancellationToken);
            await AuditedAccess.DeliverAsync(_audit, cancellationToken);
        }

        return await ScheduleViewAsync(fila.Id, cancellationToken);
    }

    /// <summary>O horário da fila e se ela atende agora. Fila invisível ao usuário devolve <c>null</c> (TEN-2).</summary>
    public async Task<QueueScheduleView?> GetScheduleAsync(Guid queueId, CancellationToken cancellationToken = default)
    {
        var fila = await FindVisibleAsync(queueId, cancellationToken);
        return fila is null ? null : await ScheduleViewAsync(fila.Id, cancellationToken);
    }

    /// <summary>Quem pode atender a fila agora e quanto ela tem esperando. Fila invisível ao usuário devolve <c>null</c> (TEN-2).</summary>
    public async Task<QueueAvailabilityView?> GetAvailabilityAsync(Guid queueId, CancellationToken cancellationToken = default)
    {
        var fila = await FindVisibleAsync(queueId, cancellationToken);
        if (fila is null)
        {
            return null;
        }

        var membros = await _queues.ListMembershipsAsync(fila.Id, cancellationToken);
        var instantaneo = await _routing.GetPolicySnapshotAsync(fila.Id, cancellationToken) ?? throw new ContactCenterResourceNotFoundException("Fila");
        return new QueueAvailabilityView(
            fila.Id,
            membros.Count,
            await _routing.CountAvailableAgentsAsync(fila.Id, instantaneo.Queue.MaxItemsPerAgent, cancellationToken),
            await _items.CountWaitingAsync(fila.Id, cancellationToken),
            instantaneo.IsOpenAt(_clock.GetUtcNow()));
    }

    /// <summary>O horário da fila e se ela atende agora, tudo do mesmo instante (nunca o fuso de uma troca com as faixas de outra).</summary>
    private async Task<QueueScheduleView> ScheduleViewAsync(Guid queueId, CancellationToken cancellationToken)
    {
        var instantaneo = await _routing.GetPolicySnapshotAsync(queueId, cancellationToken) ?? throw new ContactCenterResourceNotFoundException("Fila");
        return new QueueScheduleView(
            queueId,
            instantaneo.Queue.TimeZoneId,
            instantaneo.Windows.OrderBy(f => f.DayOfWeek).ThenBy(f => f.StartMinute).Select(f => new ScheduleWindowView(f.DayOfWeek, FormatMinute(f.StartMinute), FormatMinute(f.EndMinute))).ToList(),
            instantaneo.Closures.Select(f => f.Date).OrderBy(d => d).ToList(),
            instantaneo.Queue.OutOfHours,
            instantaneo.IsOpenAt(_clock.GetUtcNow()));
    }

    private async Task<ServiceQueue> FindManageableAsync(RequestContext contexto, Guid queueId, string operation, CancellationToken cancellationToken)
    {
        var fila = await _queues.FindQueueAsync(queueId, cancellationToken);
        if (fila is null || !contexto.IsAllowed(Permission.QueueRead, fila.OrganizationId))
        {
            throw new ContactCenterResourceNotFoundException("Fila");
        }

        await AuditedAccess.RequireAsync(_audit, contexto, Permission.QueueManage, fila.OrganizationId, operation, QueueResource, fila.Id.ToString(), cancellationToken);
        return fila;
    }

    /// <summary>A fila é visível a quem lê as filas da organização e a quem está nela.</summary>
    private async Task<ServiceQueue?> FindVisibleAsync(Guid queueId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var fila = await _queues.FindQueueReadOnlyAsync(queueId, cancellationToken);
        if (fila is null)
        {
            return null;
        }

        if (contexto.IsAllowed(Permission.QueueRead, fila.OrganizationId))
        {
            return fila;
        }

        return contexto.Principal?.UserId is { } usuario && await _queues.FindMembershipReadOnlyAsync(fila.Id, usuario, cancellationToken) is not null ? fila : null;
    }

    private static int ParseMinute(string? texto, bool allow24)
    {
        var partes = (texto ?? string.Empty).Split(':');
        if (partes.Length == 2
            && partes[0].Length == 2 && partes[1].Length == 2
            && int.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hora)
            && int.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minuto)
            && minuto < 60
            && (hora < 24 || (allow24 && hora == 24 && minuto == 0)))
        {
            return (hora * 60) + minuto;
        }

        throw new ContactCenterValidationException("Os horários devem estar no formato HH:mm (de 00:00 a 23:59, e 24:00 só como fim).");
    }

    private static string FormatMinute(int minuto) => $"{minuto / 60:00}:{minuto % 60:00}";
}
