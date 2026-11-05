using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Channels.Contracts.Providers;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>O estado de envio de uma conta, como a administração o vê (nunca traz credencial).</summary>
/// <param name="Paused">Pausada: nenhuma mensagem sai da conta até a retomada (SND-6).</param>
/// <param name="PauseReason">O código do motivo da pausa (<c>manual</c> ou <c>account-error.190</c>).</param>
/// <param name="CooldownUntil">Enquanto não passar deste instante o provedor mandou a conta esperar (limite de taxa, SND-2).</param>
/// <param name="ThroughputPerSecond">A vazão em vigor, em mensagens por segundo.</param>
/// <param name="ThroughputIsDefault">Verdadeiro: é a vazão padrão da configuração; falso: foi definida para esta conta.</param>
public sealed record AccountSendingView(
    Guid AccountId,
    bool Paused,
    string? PauseReason,
    DateTimeOffset? PausedAt,
    DateTimeOffset? ResumedAt,
    DateTimeOffset? CooldownUntil,
    int ThroughputPerSecond,
    bool ThroughputIsDefault)
{
    internal static AccountSendingView From(Guid accountId, AccountSendState? state, int defaultThroughput) =>
        new(
            accountId,
            state?.IsPaused ?? false,
            state?.PauseReason,
            state?.PausedAt,
            state?.ResumedAt,
            state?.CooldownUntil,
            state?.EffectiveThroughput(defaultThroughput) ?? defaultThroughput,
            state?.ThroughputPerSecond is null);
}

/// <summary>
/// A administração do envio de uma conta (SND-2 e SND-6): ver o estado, pausar, retomar e ajustar a vazão. Ler exige leitura de canais na organização da conta; as ações exigem gerir canais (TEN-2: conta de outra
/// organização ou de outro tenant responde como inexistente). Toda ação é auditada. A RETOMADA é segura: só ocorre se a credencial da conta é legível e o provedor a aceita para cada número ativo (uma leitura, nenhuma
/// mensagem enviada); trocar a credencial NÃO retoma sozinho.
/// </summary>
public sealed class AccountSendingService
{
    private const string Resource = "channel-account";

    private readonly IChannelRepository _repository;
    private readonly IAccountSendStateStore _state;
    private readonly ChannelProviderAdapters _adapters;
    private readonly ISecretProtector _protector;
    private readonly ChannelSendingOptions _options;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditWriter _audit;

    public AccountSendingService(
        IChannelRepository repository,
        IAccountSendStateStore state,
        ChannelProviderAdapters adapters,
        ISecretProtector protector,
        ChannelSendingOptions options,
        IRequestContextAccessor context,
        IAuditWriter audit)
    {
        _repository = repository;
        _state = state;
        _adapters = adapters;
        _protector = protector;
        _options = options;
        _context = context;
        _audit = audit;
    }

    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    public async Task<AccountSendingView> GetAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var conta = await FindVisibleAsync(contexto, accountId, cancellationToken);
        return AccountSendingView.From(conta.Id, await _state.FindAsync(conta.Id, cancellationToken), _options.DefaultThroughputPerSecond);
    }

    /// <summary>Pausa a conta por decisão do administrador (por exemplo, suspeita de credencial vazada). Repetir é inofensivo.</summary>
    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    public async Task<AccountSendingView> PauseAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var conta = await FindVisibleAsync(contexto, accountId, cancellationToken);
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.ChannelManage, conta.OrganizationId, "channels.account.pause", Resource, conta.Id.ToString(), cancellationToken);

        if (await _state.PauseAsync(conta.Id, "manual", cancellationToken))
        {
            ChannelsMetrics.AccountPaused.Add(1);
        }

        await _audit.WriteAsync(new AuditEntry("channels.account.pause", Resource, conta.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return AccountSendingView.From(conta.Id, await _state.FindAsync(conta.Id, cancellationToken), _options.DefaultThroughputPerSecond);
    }

    /// <summary>
    /// Retoma a conta, depois de provar que a credencial é aceita pelo provedor para cada número ativo (SND-6, retomada segura). Conta que não está pausada: nada muda. A leitura de prova não envia mensagem.
    /// </summary>
    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    /// <exception cref="ChannelResumeRefusedException">A prova falhou (inclusive credencial ausente ou ilegível): a conta continua pausada.</exception>
    public async Task<AccountSendingView> ResumeAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var conta = await FindVisibleAsync(contexto, accountId, cancellationToken);
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.ChannelManage, conta.OrganizationId, "channels.account.resume", Resource, conta.Id.ToString(), cancellationToken);

        var atual = await _state.FindAsync(conta.Id, cancellationToken);
        if (atual is { IsPaused: true })
        {
            var prova = await AccountProbe.RunAsync(_repository, _protector, _adapters, contexto.TenantId, conta, cancellationToken);
            if (prova.Refusal is { } motivo)
            {
                await _audit.WriteAsync(new AuditEntry("channels.account.resume", Resource, conta.Id.ToString(), AuditOutcome.Failed), cancellationToken);
                throw new ChannelResumeRefusedException(motivo);
            }

            await _state.ResumeAsync(conta.Id, cancellationToken);
        }

        await _audit.WriteAsync(new AuditEntry("channels.account.resume", Resource, conta.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return AccountSendingView.From(conta.Id, await _state.FindAsync(conta.Id, cancellationToken), _options.DefaultThroughputPerSecond);
    }

    /// <summary>Define (ou, com nulo, volta ao padrão) a vazão da conta, em mensagens por segundo.</summary>
    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    /// <exception cref="ChannelsValidationException">A vazão está fora de 1 a 1000.</exception>
    public async Task<AccountSendingView> SetThroughputAsync(Guid accountId, int? perSecond, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var conta = await FindVisibleAsync(contexto, accountId, cancellationToken);
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.ChannelManage, conta.OrganizationId, "channels.account.throughput.set", Resource, conta.Id.ToString(), cancellationToken);

        if (perSecond is { } valor && (valor < AccountSendState.MinThroughputPerSecond || valor > AccountSendState.MaxThroughputPerSecond))
        {
            throw new ChannelsValidationException($"A vazão deve ficar entre {AccountSendState.MinThroughputPerSecond} e {AccountSendState.MaxThroughputPerSecond} mensagens por segundo.");
        }

        await _state.SetThroughputAsync(conta.Id, perSecond, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("channels.account.throughput.set", Resource, conta.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return AccountSendingView.From(conta.Id, await _state.FindAsync(conta.Id, cancellationToken), _options.DefaultThroughputPerSecond);
    }

    private async Task<ChannelAccount> FindVisibleAsync(RequestContext contexto, Guid accountId, CancellationToken cancellationToken)
    {
        var conta = await _repository.FindAccountAsync(accountId, cancellationToken);
        if (conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            throw new ChannelsResourceNotFoundException("Conta");
        }

        return conta;
    }
}

/// <summary>A retomada foi recusada: a prova de que o provedor aceita a credencial falhou. A mensagem é para o administrador e nunca traz valor de credencial.</summary>
public sealed class ChannelResumeRefusedException : Exception
{
    public ChannelResumeRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>O resultado da sonda de uma conta: por número ativo, o que o provedor respondeu; ou o motivo de nem poder sondar.</summary>
internal sealed record AccountProbeResult(IReadOnlyList<(string PhoneNumberId, string Name, ProviderProbeResult Result)> Instances, string? Refusal);

/// <summary>
/// A sonda de uma conta: decifra o token de acesso (só para esta chamada) e pede ao provedor a leitura de cada número ATIVO da conta. Nenhuma mensagem é enviada. Serve à retomada segura e ao teste de prontidão.
/// </summary>
internal static class AccountProbe
{
    public static async Task<AccountProbeResult> RunAsync(
        IChannelRepository repository,
        ISecretProtector protector,
        ChannelProviderAdapters adapters,
        TenantId tenant,
        ChannelAccount account,
        CancellationToken cancellationToken)
    {
        var adaptador = adapters.Find(account.Provider);
        if (adaptador is null)
        {
            return new AccountProbeResult(Array.Empty<(string, string, ProviderProbeResult)>(), "O adaptador do provedor não está disponível neste ambiente.");
        }

        var token = await ChannelSecretReader.ReadAsync(repository, protector, tenant, account.Id, ChannelSecretKind.AccessToken, cancellationToken);
        if (token is null)
        {
            return new AccountProbeResult(Array.Empty<(string, string, ProviderProbeResult)>(), "O token de acesso da conta não está cadastrado ou não pôde ser lido: cadastre-o de novo.");
        }

        var instancias = (await repository.ListInstancesAsync(new[] { account.OrganizationId }, cancellationToken))
            .Where(i => i.AccountId == account.Id && i.Status == IaBimeta.Channels.Contracts.ChannelStatus.Active)
            .ToList();
        if (instancias.Count == 0)
        {
            return new AccountProbeResult(Array.Empty<(string, string, ProviderProbeResult)>(), "A conta não tem nenhum número ativo para conferir.");
        }

        var resultados = new List<(string, string, ProviderProbeResult)>();
        foreach (var instancia in instancias)
        {
            var resultado = await adaptador.ProbeAsync(new ProviderProbeRequest(token, instancia.PhoneNumberId), cancellationToken);
            resultados.Add((instancia.PhoneNumberId, instancia.Name, resultado));
        }

        var recusa = resultados.FirstOrDefault(r => r.Item3.Outcome != ProviderProbeOutcome.Ok);
        return new AccountProbeResult(resultados, recusa.Item3 is null ? null : Describe(recusa.Item3.Outcome));
    }

    private static string Describe(ProviderProbeOutcome outcome) => outcome switch
    {
        ProviderProbeOutcome.CredentialRejected => "O provedor recusou a credencial da conta (expirada, inválida, sem permissão ou conta restrita). Cadastre um token válido e tente de novo.",
        ProviderProbeOutcome.NotFound => "O provedor não encontrou o número cadastrado, ou a credencial não tem acesso a ele. Confira o Phone Number ID e as permissões do token.",
        _ => "Não foi possível falar com o provedor agora. A conta continua pausada: tente de novo em alguns minutos.",
    };
}
