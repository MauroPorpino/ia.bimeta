using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// Casos de uso das filas de atendimento e dos atendentes delas. Tenant e permissões vêm sempre do contexto confiável (TEN-1) e
/// a permissão é avaliada sobre a organização do recurso carregado (TEN-2): sem leitura sobre ela o recurso responde como
/// inexistente; com leitura mas sem a permissão da ação, a operação é negada e auditada. Quem entra em uma fila muda quem atende
/// quem, por isso toda mudança de fila e de atendente deixa rastro na auditoria.
/// </summary>
public sealed class ServiceQueueService
{
    private const string QueueResource = "service-queue";
    private const string MemberResource = "service-queue-member";

    private readonly IQueueRepository _repository;
    private readonly IOrganizationDirectory _organizations;
    private readonly IBusinessUnitDirectory _units;
    private readonly IDepartmentDirectory _departments;
    private readonly IUserDirectory _users;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public ServiceQueueService(
        IQueueRepository repository,
        IOrganizationDirectory organizations,
        IBusinessUnitDirectory units,
        IDepartmentDirectory departments,
        IUserDirectory users,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _repository = repository;
        _organizations = organizations;
        _units = units;
        _departments = departments;
        _users = users;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="ContactCenterResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir filas da organização.</exception>
    /// <exception cref="ContactCenterValidationException">Dados inválidos, ou unidade ou setor que não existem na organização.</exception>
    /// <exception cref="QueueNameInUseException">Já existe uma fila com este nome na organização.</exception>
    public async Task<ServiceQueueView> CreateAsync(Guid organizationId, Guid? businessUnitId, Guid? departmentId, string name, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsAllowed(Permission.OrganizationRead, organizationId) || !await _organizations.ExistsAsync(organizationId, cancellationToken))
        {
            throw new ContactCenterResourceNotFoundException("Organização");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.QueueManage, organizationId,
            "contact-center.queue.create", "organization", organizationId.ToString(), cancellationToken);

        // A mesma mensagem para unidade inexistente e para unidade de outra organização: quem gere esta organização não descobre,
        // por tentativa, o que existe nas outras.
        if (businessUnitId is { } unidade && unidade != Guid.Empty
            && await _units.FindOrganizationIdAsync(unidade, cancellationToken) != organizationId)
        {
            throw new ContactCenterValidationException("A unidade não existe nesta organização.");
        }

        if (departmentId is { } setor && setor != Guid.Empty
            && await _departments.FindBusinessUnitIdAsync(setor, cancellationToken) != businessUnitId)
        {
            throw new ContactCenterValidationException("O setor não existe nesta unidade.");
        }

        var fila = ServiceQueue.Create(contexto.TenantId, organizationId, businessUnitId, departmentId, name, _clock.GetUtcNow());
        await _repository.AddQueueAsync(fila, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("contact-center.queue.create", QueueResource, fila.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ServiceQueueView.From(fila);
    }

    /// <summary>Uma fila de outro tenant, ou de organização sem permissão de leitura, devolve <c>null</c> (TEN-2).</summary>
    public async Task<ServiceQueueView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var fila = await _repository.FindQueueReadOnlyAsync(id, cancellationToken);
        return fila is null || !contexto.IsAllowed(Permission.QueueRead, fila.OrganizationId) ? null : ServiceQueueView.From(fila);
    }

    /// <exception cref="AccessDeniedException">O usuário não tem a permissão de ler filas em organização alguma.</exception>
    public async Task<IReadOnlyList<ServiceQueueView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var visiveis = contexto.OrganizationsWithPermission(Permission.QueueRead);
        if (visiveis is { Count: 0 })
        {
            throw new AccessDeniedException(Permission.QueueRead);
        }

        var filas = await _repository.ListQueuesAsync(visiveis, cancellationToken);
        return filas.Select(ServiceQueueView.From).ToList();
    }

    /// <summary>Pausa a fila (idempotente: repetir não muda nada nem grava auditoria).</summary>
    public Task<ServiceQueueView> PauseAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(id, "contact-center.queue.pause", (fila, agora) => fila.Pause(agora), cancellationToken);

    /// <summary>Retoma a fila (idempotente: repetir não muda nada nem grava auditoria).</summary>
    public Task<ServiceQueueView> ResumeAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(id, "contact-center.queue.resume", (fila, agora) => fila.Resume(agora), cancellationToken);

    private async Task<ServiceQueueView> ChangeStatusAsync(Guid id, string operation, Func<ServiceQueue, DateTimeOffset, bool> change, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var fila = await FindManageableAsync(contexto, id, operation, cancellationToken);

        if (change(fila, _clock.GetUtcNow()))
        {
            await _repository.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry(operation, QueueResource, fila.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return ServiceQueueView.From(fila);
    }

    /// <summary>
    /// Coloca o usuário na fila com o papel informado, ou muda o papel se ele já está nela (idempotente: repetir o mesmo papel
    /// não muda nada nem grava auditoria). O usuário precisa existir no tenant. Pode ser de outra unidade ou organização: a
    /// equipe compartilhada é decisão de quem gere a fila.
    /// </summary>
    /// <exception cref="ContactCenterResourceNotFoundException">A fila ou o usuário não existem ou não são visíveis.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir filas da organização.</exception>
    /// <exception cref="ContactCenterValidationException">Papel inválido.</exception>
    public async Task<QueueMemberView> AddMemberAsync(Guid queueId, Guid userId, QueueMemberRole role, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!Enum.IsDefined(role))
        {
            throw new ContactCenterValidationException("O papel do atendente na fila é inválido.");
        }

        var fila = await FindManageableReadOnlyAsync(contexto, queueId, "contact-center.queue.member.add", userId, cancellationToken);
        if (!await _users.ExistsAsync(userId, cancellationToken))
        {
            throw new ContactCenterResourceNotFoundException("Usuário");
        }

        var recurso = $"{fila.Id}:{userId}";
        var existente = await _repository.FindMembershipAsync(fila.Id, userId, cancellationToken);
        if (existente is null)
        {
            var novo = QueueMembership.Create(contexto.TenantId, fila.Id, userId, role, contexto.Principal?.UserId, _clock.GetUtcNow());
            await _repository.AddMembershipAsync(novo, cancellationToken);
            await _audit.WriteAsync(new AuditEntry("contact-center.queue.member.add", MemberResource, recurso, AuditOutcome.Success), cancellationToken);
            return QueueMemberView.From(novo);
        }

        if (existente.ChangeRole(role))
        {
            await _repository.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry("contact-center.queue.member.role", MemberResource, recurso, AuditOutcome.Success), cancellationToken);
        }

        return QueueMemberView.From(existente);
    }

    /// <summary>Tira o usuário da fila. Devolve <c>false</c>, sem auditoria, se ele não estava nela (idempotente).</summary>
    public async Task<bool> RemoveMemberAsync(Guid queueId, Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var fila = await FindManageableReadOnlyAsync(contexto, queueId, "contact-center.queue.member.remove", userId, cancellationToken);

        var existente = await _repository.FindMembershipAsync(fila.Id, userId, cancellationToken);
        if (existente is null)
        {
            return false;
        }

        await _repository.RemoveMembershipAsync(existente, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("contact-center.queue.member.remove", MemberResource, $"{fila.Id}:{userId}", AuditOutcome.Success), cancellationToken);
        return true;
    }

    /// <summary>Os atendentes da fila. Fila inexistente ou sem permissão de leitura devolve <c>null</c> (TEN-2).</summary>
    public async Task<IReadOnlyList<QueueMemberView>?> ListMembersAsync(Guid queueId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var fila = await _repository.FindQueueReadOnlyAsync(queueId, cancellationToken);
        if (fila is null || !contexto.IsAllowed(Permission.QueueRead, fila.OrganizationId))
        {
            return null;
        }

        var membros = await _repository.ListMembershipsAsync(fila.Id, cancellationToken);
        return membros.Select(QueueMemberView.From).ToList();
    }

    private async Task<ServiceQueue> FindManageableAsync(RequestContext contexto, Guid id, string operation, CancellationToken cancellationToken)
    {
        var fila = await _repository.FindQueueAsync(id, cancellationToken);
        if (fila is null || !contexto.IsAllowed(Permission.QueueRead, fila.OrganizationId))
        {
            throw new ContactCenterResourceNotFoundException("Fila");
        }

        await AuditedAccess.RequireAsync(_audit, contexto, Permission.QueueManage, fila.OrganizationId, operation, QueueResource, fila.Id.ToString(), cancellationToken);
        return fila;
    }

    private async Task<ServiceQueue> FindManageableReadOnlyAsync(RequestContext contexto, Guid queueId, string operation, Guid userId, CancellationToken cancellationToken)
    {
        var fila = await _repository.FindQueueReadOnlyAsync(queueId, cancellationToken);
        if (fila is null || !contexto.IsAllowed(Permission.QueueRead, fila.OrganizationId))
        {
            throw new ContactCenterResourceNotFoundException("Fila");
        }

        await AuditedAccess.RequireAsync(_audit, contexto, Permission.QueueManage, fila.OrganizationId, operation, MemberResource, $"{fila.Id}:{userId}", cancellationToken);
        return fila;
    }
}
