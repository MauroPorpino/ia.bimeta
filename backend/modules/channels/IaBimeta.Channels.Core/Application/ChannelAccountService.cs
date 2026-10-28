using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Channels.Application;

/// <summary>
/// Casos de uso das contas do provedor e das credenciais delas. Tenant e permissões vêm sempre do contexto confiável (TEN-1) e
/// a permissão é avaliada sobre a organização do recurso carregado (TEN-2): sem leitura sobre ela o recurso responde como
/// inexistente; com leitura mas sem a permissão da ação, a operação é negada e auditada. A credencial é só de escrita: nenhuma
/// resposta, log ou registro de auditoria traz o valor.
/// </summary>
public sealed class ChannelAccountService
{
    private const string AccountResource = "channel-account";
    private const string SecretResource = "channel-secret";

    private readonly IChannelRepository _repository;
    private readonly IOrganizationDirectory _organizations;
    private readonly ISecretProtector _protector;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public ChannelAccountService(
        IChannelRepository repository,
        IOrganizationDirectory organizations,
        ISecretProtector protector,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _repository = repository;
        _organizations = organizations;
        _protector = protector;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="ChannelsResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    /// <exception cref="ChannelsValidationException">Dados inválidos.</exception>
    /// <exception cref="ChannelIdentifierInUseException">O identificador da conta no provedor já está cadastrado.</exception>
    public async Task<ChannelAccountView> CreateAsync(Guid organizationId, ChannelProvider provider, string providerAccountId, string name, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsAllowed(Permission.OrganizationRead, organizationId) || !await _organizations.ExistsAsync(organizationId, cancellationToken))
        {
            throw new ChannelsResourceNotFoundException("Organização");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.ChannelManage, organizationId,
            "channels.account.create", "organization", organizationId.ToString(), cancellationToken);

        var conta = ChannelAccount.Create(contexto.TenantId, organizationId, provider, providerAccountId, name, _clock.GetUtcNow());
        await _repository.AddAccountAsync(conta, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("channels.account.create", AccountResource, conta.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ChannelAccountView.From(conta, Array.Empty<ChannelSecret>());
    }

    /// <summary>Uma conta de outro tenant, ou de organização sem permissão de leitura, devolve <c>null</c> (TEN-2).</summary>
    public async Task<ChannelAccountView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var conta = await _repository.FindAccountAsync(id, cancellationToken);
        if (conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            return null;
        }

        var segredos = await _repository.ListSecretsAsync(new[] { conta.Id }, cancellationToken);
        return ChannelAccountView.From(conta, segredos);
    }

    /// <exception cref="AccessDeniedException">O usuário não tem a permissão de ler canais em organização alguma.</exception>
    public async Task<IReadOnlyList<ChannelAccountView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var visiveis = contexto.OrganizationsWithPermission(Permission.ChannelRead);
        if (visiveis is { Count: 0 })
        {
            throw new AccessDeniedException(Permission.ChannelRead);
        }

        var contas = await _repository.ListAccountsAsync(visiveis, cancellationToken);
        var segredos = await _repository.ListSecretsAsync(contas.Select(c => c.Id).ToList(), cancellationToken);
        return contas
            .Select(c => ChannelAccountView.From(c, segredos.Where(s => s.AccountId == c.Id)))
            .ToList();
    }

    /// <summary>
    /// Define (ou gira) uma credencial da conta. O valor é cifrado antes de tocar o banco e a resposta traz só os metadados.
    /// Definir de novo o mesmo papel troca o valor e mantém a referência.
    /// </summary>
    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    /// <exception cref="ChannelsValidationException">Papel ou valor inválido (a mensagem não reproduz o valor).</exception>
    /// <exception cref="ChannelSecretsNotConfiguredException">A chave de cifra não está configurada.</exception>
    public async Task<ChannelSecretView> SetSecretAsync(Guid accountId, ChannelSecretKind kind, string value, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!Enum.IsDefined(kind))
        {
            throw new ChannelsValidationException("O papel da credencial é inválido.");
        }

        var valido = ChannelSecret.ValidateValue(value);

        var conta = await _repository.FindAccountAsync(accountId, cancellationToken);
        if (conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            throw new ChannelsResourceNotFoundException("Conta");
        }

        var recurso = $"{conta.Id}:{kind}";
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.ChannelManage, conta.OrganizationId,
            "channels.secret.set", SecretResource, recurso, cancellationToken);

        var agora = _clock.GetUtcNow();
        var autor = contexto.Principal?.UserId;
        var existente = await _repository.FindSecretAsync(conta.Id, kind, cancellationToken);
        var id = existente?.Id ?? Guid.CreateVersion7();
        var cifrado = _protector.Protect(valido, new SecretBinding(contexto.TenantId, id, kind));

        ChannelSecret segredo;
        if (existente is null)
        {
            segredo = ChannelSecret.Create(id, contexto.TenantId, conta.Id, kind, cifrado.KeyId, cifrado.Ciphertext, autor, agora);
            await _repository.AddSecretAsync(segredo, cancellationToken);
        }
        else
        {
            segredo = existente;
            segredo.Replace(cifrado.KeyId, cifrado.Ciphertext, autor, agora);
            await _repository.SaveChangesAsync(cancellationToken);
        }

        // A trilha registra que a credencial mudou e qual papel, nunca o valor.
        await _audit.WriteAsync(new AuditEntry("channels.secret.set", SecretResource, recurso, AuditOutcome.Success), cancellationToken);
        return ChannelSecretView.From(segredo);
    }
}
