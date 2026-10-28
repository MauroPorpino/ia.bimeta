using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Channels.Application;

/// <summary>
/// Casos de uso das instâncias de canal (os números do tenant): cadastro, unidades autorizadas, padrões, idioma e situação.
/// Tenant e permissões vêm sempre do contexto confiável (TEN-1) e a permissão é avaliada sobre a organização do recurso
/// carregado (TEN-2). Toda mudança de associação entre canal e unidade entra na trilha de auditoria, com a unidade no
/// identificador do recurso. Esta é a única porta de escrita da lista de unidades autorizadas.
/// </summary>
public sealed class ChannelInstanceService
{
    private const string InstanceResource = "channel-instance";
    private const string UnitResource = "channel-instance-unit";

    private readonly IChannelRepository _repository;
    private readonly IBusinessUnitDirectory _units;
    private readonly IDepartmentDirectory _departments;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public ChannelInstanceService(
        IChannelRepository repository,
        IBusinessUnitDirectory units,
        IDepartmentDirectory departments,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _repository = repository;
        _units = units;
        _departments = departments;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>Cadastra o número. Nasce inativo, sem unidades: quem o configura autoriza as unidades e então o ativa.</summary>
    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    /// <exception cref="ChannelsValidationException">Dados inválidos.</exception>
    /// <exception cref="ChannelIdentifierInUseException">O identificador do número já está cadastrado.</exception>
    public async Task<ChannelInstanceView> RegisterAsync(
        Guid accountId, string name, string phoneNumberId, string? displayPhoneNumber, Locale? defaultLocale, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var conta = await _repository.FindAccountAsync(accountId, cancellationToken);
        if (conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            throw new ChannelsResourceNotFoundException("Conta");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.ChannelManage, conta.OrganizationId,
            "channels.instance.register", "channel-account", conta.Id.ToString(), cancellationToken);

        var instancia = ChannelInstance.Register(contexto.TenantId, conta, phoneNumberId, displayPhoneNumber, name, defaultLocale, _clock.GetUtcNow());
        await _repository.AddInstanceAsync(instancia, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("channels.instance.register", InstanceResource, instancia.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ChannelInstanceView.From(instancia);
    }

    /// <summary>Uma instância de outro tenant, ou de organização sem permissão de leitura, devolve <c>null</c> (TEN-2).</summary>
    public async Task<ChannelInstanceView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (id == Guid.Empty)
        {
            return null;
        }

        var instancia = await _repository.FindInstanceReadOnlyAsync(new ChannelInstanceId(id), cancellationToken);
        return instancia is null || !contexto.IsAllowed(Permission.ChannelRead, instancia.OrganizationId)
            ? null
            : ChannelInstanceView.From(instancia);
    }

    /// <exception cref="AccessDeniedException">O usuário não tem a permissão de ler canais em organização alguma.</exception>
    public async Task<IReadOnlyList<ChannelInstanceView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var visiveis = contexto.OrganizationsWithPermission(Permission.ChannelRead);
        if (visiveis is { Count: 0 })
        {
            throw new AccessDeniedException(Permission.ChannelRead);
        }

        var instancias = await _repository.ListInstancesAsync(visiveis, cancellationToken);
        return instancias.Select(ChannelInstanceView.From).ToList();
    }

    /// <summary>
    /// Autoriza o número a atender a unidade. A unidade precisa existir no tenant e ser da organização do canal; uma unidade de
    /// outra organização (ou de outro tenant) é recusada com a mesma mensagem.
    /// </summary>
    /// <exception cref="ChannelsValidationException">A unidade não existe ou não pertence à organização do canal.</exception>
    public async Task<ChannelInstanceView> AuthorizeUnitAsync(Guid instanceId, Guid businessUnitId, CancellationToken cancellationToken = default)
    {
        var operacao = "channels.instance.unit.authorize";
        var (contexto, instancia) = await LoadForManagementAsync(instanceId, operacao, cancellationToken);

        if (businessUnitId == Guid.Empty || await _units.FindOrganizationIdAsync(businessUnitId, cancellationToken) != instancia.OrganizationId)
        {
            throw new ChannelsValidationException("A unidade não existe ou não pertence à organização do canal.");
        }

        if (instancia.AuthorizeUnit(businessUnitId, contexto.Principal?.UserId, _clock.GetUtcNow()))
        {
            await _repository.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry(operacao, UnitResource, $"{instancia.Id}:{businessUnitId}", AuditOutcome.Success), cancellationToken);
        }

        return ChannelInstanceView.From(instancia);
    }

    /// <exception cref="ChannelsValidationException">É a unidade padrão, ou a última de um canal ativo.</exception>
    public async Task<ChannelInstanceView> RevokeUnitAsync(Guid instanceId, Guid businessUnitId, CancellationToken cancellationToken = default)
    {
        var operacao = "channels.instance.unit.revoke";
        var (_, instancia) = await LoadForManagementAsync(instanceId, operacao, cancellationToken);

        if (instancia.RevokeUnit(businessUnitId, _clock.GetUtcNow()))
        {
            await _repository.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry(operacao, UnitResource, $"{instancia.Id}:{businessUnitId}", AuditOutcome.Success), cancellationToken);
        }

        return ChannelInstanceView.From(instancia);
    }

    /// <summary>
    /// Define a unidade e o setor padrão do número (nulos: sem padrão). A unidade precisa estar entre as autorizadas e o setor,
    /// ser da unidade. Configuração inválida é recusada, nunca ajustada.
    /// </summary>
    /// <exception cref="ChannelsValidationException">A unidade não é autorizada, ou o setor não é da unidade.</exception>
    public async Task<ChannelInstanceView> SetDefaultsAsync(Guid instanceId, Guid? businessUnitId, Guid? departmentId, CancellationToken cancellationToken = default)
    {
        var operacao = "channels.instance.defaults.set";
        var (_, instancia) = await LoadForManagementAsync(instanceId, operacao, cancellationToken);

        if (businessUnitId is { } unidade && departmentId is { } setor
            && await _departments.FindBusinessUnitIdAsync(setor, cancellationToken) != unidade)
        {
            throw new ChannelsValidationException("O setor padrão não existe ou não pertence à unidade padrão.");
        }

        if (instancia.SetDefaults(businessUnitId, departmentId, _clock.GetUtcNow()))
        {
            await _repository.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry(operacao, InstanceResource, instancia.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return ChannelInstanceView.From(instancia);
    }

    /// <summary>Define o idioma padrão do número (nulo: o canal não define e vale o do tenant).</summary>
    public async Task<ChannelInstanceView> SetDefaultLocaleAsync(Guid instanceId, Locale? locale, CancellationToken cancellationToken = default)
    {
        var operacao = "channels.instance.locale.set";
        var (_, instancia) = await LoadForManagementAsync(instanceId, operacao, cancellationToken);

        if (instancia.ChangeDefaultLocale(locale, _clock.GetUtcNow()))
        {
            await _repository.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry(operacao, InstanceResource, instancia.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return ChannelInstanceView.From(instancia);
    }

    /// <exception cref="ChannelsValidationException">O canal não tem unidade autorizada.</exception>
    public async Task<ChannelInstanceView> ActivateAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var operacao = "channels.instance.activate";
        var (_, instancia) = await LoadForManagementAsync(instanceId, operacao, cancellationToken);

        if (instancia.Activate(_clock.GetUtcNow()))
        {
            await _repository.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry(operacao, InstanceResource, instancia.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return ChannelInstanceView.From(instancia);
    }

    /// <exception cref="ChannelsValidationException">O canal ainda não foi ativado.</exception>
    public async Task<ChannelInstanceView> SuspendAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var operacao = "channels.instance.suspend";
        var (_, instancia) = await LoadForManagementAsync(instanceId, operacao, cancellationToken);

        if (instancia.Suspend(_clock.GetUtcNow()))
        {
            await _repository.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditEntry(operacao, InstanceResource, instancia.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return ChannelInstanceView.From(instancia);
    }

    /// <summary>
    /// Carrega a instância para alterar. Sem leitura sobre a organização dela, a instância responde como inexistente (TEN-2);
    /// com leitura mas sem a permissão de gerir, a operação é negada e auditada.
    /// </summary>
    private async Task<(RequestContext Context, ChannelInstance Instance)> LoadForManagementAsync(Guid instanceId, string operation, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        if (instanceId == Guid.Empty)
        {
            throw new ChannelsResourceNotFoundException("Canal");
        }

        var instancia = await _repository.FindInstanceAsync(new ChannelInstanceId(instanceId), cancellationToken);
        if (instancia is null || !contexto.IsAllowed(Permission.ChannelRead, instancia.OrganizationId))
        {
            throw new ChannelsResourceNotFoundException("Canal");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.ChannelManage, instancia.OrganizationId,
            operation, InstanceResource, instancia.Id.ToString(), cancellationToken);
        return (contexto, instancia);
    }
}
