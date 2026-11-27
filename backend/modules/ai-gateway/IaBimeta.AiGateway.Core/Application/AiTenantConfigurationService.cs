using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Application;

/// <summary>O armazenamento da configuração de IA do tenant do contexto (a Infrastructure o implementa, filtrando por tenant). As entidades voltam RASTREADAS para alteração.</summary>
public interface IAiTenantConfigurationStore
{
    /// <summary>A configuração do tenant do contexto, ou nulo. Rastreada.</summary>
    Task<AiTenantSettings?> FindSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Grava a configuração (nova ou alterada). Uma alteração concorrente do mesmo ponto de partida falha com <see cref="AiConfigurationConflictException"/>.</summary>
    Task SaveSettingsAsync(AiTenantSettings settings, bool isNew, CancellationToken cancellationToken);

    /// <summary>A credencial ATIVA do tenant do contexto para o provedor, ou nulo. Rastreada.</summary>
    Task<StoredAiCredential?> FindActiveCredentialAsync(string providerId, CancellationToken cancellationToken);

    /// <summary>Grava a credencial (nova ou alterada). Uma alteração concorrente falha com <see cref="AiConfigurationConflictException"/>.</summary>
    Task SaveCredentialAsync(StoredAiCredential credential, bool isNew, CancellationToken cancellationToken);
}

/// <summary>Outra alteração da mesma configuração ou credencial aconteceu primeiro: releia e repita.</summary>
public sealed class AiConfigurationConflictException : Exception
{
    public AiConfigurationConflictException()
        : base("A configuração de IA foi alterada por outra operação. Releia e tente de novo.")
    {
    }
}

/// <summary>A situação da IA do tenant, SEM nenhum segredo: nunca devolve o valor nem o texto cifrado da credencial.</summary>
public sealed record AiTenantStatusView(
    bool Configured,
    string? ProviderId,
    string? ModelId,
    AiTenantStatus? Status,
    bool CredentialPresent,
    string? CredentialFingerprint,
    DateTimeOffset? UpdatedAt,
    bool CredentialRequired = true);

/// <summary>
/// Os casos de uso da configuração de IA do tenant (ADR 0017, seção D e E): escolher o provedor e o modelo, definir e revogar a credencial BYOK e consultar a situação. O tenant e o ator
/// vêm do contexto confiável; só quem tem <see cref="Permission.TenantSettingsManage"/> altera, a credencial nunca volta em claro, e toda alteração é auditada SEM valor de segredo. Os limites
/// por tenant e o bloqueio pela plataforma são do operador (<see cref="Permission.SystemOperation"/>). Não há API HTTP nesta fatia: estes casos de uso são o que a API administrativa
/// (C1.5) vai chamar.
/// </summary>
public sealed class AiTenantConfigurationService
{
    private const string SettingsResource = "ai-gateway.settings";
    private const string CredentialResource = "ai-gateway.credential";
    private const int MaxSecretLength = 4096;

    private readonly IRequestContextAccessor _context;
    private readonly IAiTenantConfigurationStore _store;
    private readonly AiModelCatalog _catalog;
    private readonly IAiCredentialProtector _protector;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly AiProviderRegistry? _providers;

    public AiTenantConfigurationService(
        IRequestContextAccessor context,
        IAiTenantConfigurationStore store,
        AiModelCatalog catalog,
        IAiCredentialProtector protector,
        IAuditWriter audit,
        TimeProvider clock,
        AiProviderRegistry? providers = null)
    {
        _context = context;
        _store = store;
        _catalog = catalog;
        _protector = protector;
        _audit = audit;
        _clock = clock;
        _providers = providers;
    }

    /// <summary>O provedor tem adaptador registrado neste host? Sem o registro injetado (testes antigos) não restringe; nos hosts o registro sempre existe.</summary>
    private bool IsAvailable(string providerId) => _providers is null || _providers.Find(providerId) is not null;

    /// <summary>A situação da IA do tenant: configurada ou não, provedor, modelo, situação e se há credencial (com a impressão digital). Nunca o valor da credencial.</summary>
    public async Task<AiTenantStatusView> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);

        var configuracao = await _store.FindSettingsAsync(cancellationToken);
        if (configuracao is null)
        {
            return new AiTenantStatusView(false, null, null, null, false, null, null);
        }

        var credencial = await _store.FindActiveCredentialAsync(configuracao.ProviderId, cancellationToken);
        var exigida = _providers?.Find(configuracao.ProviderId)?.RequiresCredential ?? true;
        return new AiTenantStatusView(true, configuracao.ProviderId, configuracao.ModelId, configuracao.Status, credencial is not null, credencial?.Fingerprint, configuracao.UpdatedAt, exigida);
    }

    /// <summary>
    /// Escolhe o provedor e o modelo do tenant (ou os troca). O modelo precisa estar no catálogo e ATIVO; a origem da credencial é sempre BYOK. O tenant só alterna entre habilitado e
    /// desabilitado: <see cref="AiTenantStatus.Blocked"/> é da plataforma, e um tenant bloqueado não se reabilita sozinho.
    /// </summary>
    /// <exception cref="AiGatewayValidationException">O modelo não está no catálogo ou está desativado, ou a situação pedida não é permitida ao tenant.</exception>
    /// <exception cref="AiConfigurationConflictException">Outra alteração aconteceu primeiro.</exception>
    public async Task SetConfigurationAsync(string providerId, string modelId, AiTenantStatus status = AiTenantStatus.Enabled, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);

        if (status is not (AiTenantStatus.Enabled or AiTenantStatus.Disabled))
        {
            throw new AiGatewayValidationException("O tenant só pode habilitar ou desabilitar a IA: o bloqueio é da plataforma.");
        }

        // Só um provedor com adaptador REGISTRADO neste host pode ser escolhido: o catálogo pode listar um provedor sem adaptador, e ele nunca aparece como disponível nem é aceito em silêncio.
        if (!IsAvailable(providerId))
        {
            throw new AiGatewayValidationException("O provedor não está disponível neste ambiente (nenhum adaptador registrado).");
        }

        var modelo = _catalog.Find(providerId, modelId);
        if (modelo is null || modelo.Status != AiModelStatus.Active)
        {
            throw new AiGatewayValidationException("O modelo não faz parte do catálogo ou está desativado.");
        }

        var agora = _clock.GetUtcNow();
        var atual = await _store.FindSettingsAsync(cancellationToken);
        if (atual is null)
        {
            atual = AiTenantSettings.Create(contexto.TenantId, providerId, modelId, status, agora, contexto.Principal?.UserId);
            await _store.SaveSettingsAsync(atual, isNew: true, cancellationToken);
        }
        else
        {
            if (atual.Status == AiTenantStatus.Blocked)
            {
                throw new AiGatewayValidationException("A IA deste tenant está bloqueada pela plataforma: só a plataforma a reabilita.");
            }

            atual.Configure(providerId, modelId, status, agora, contexto.Principal?.UserId);
            await _store.SaveSettingsAsync(atual, isNew: false, cancellationToken);
        }

        await _audit.WriteAsync(new AuditEntry("ai-gateway.settings.set", SettingsResource, atual.Id.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>
    /// Define a credencial BYOK do tenant para o provedor: cifra o valor (AES-256-GCM, com o tenant, a credencial e o provedor como dado autenticado) e guarda SÓ o texto cifrado e a
    /// impressão digital. Já havendo uma ativa para o provedor, troca o valor dela. O valor em claro nunca é devolvido, logado nem auditado.
    /// </summary>
    /// <exception cref="AiGatewayValidationException">O provedor não está no catálogo ou o valor é inválido.</exception>
    /// <exception cref="AiSecretsUnavailableException">Não há chave mestra ativa: nada é guardado (falha fechada).</exception>
    public async Task SetCredentialAsync(string providerId, string secret, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);

        if (!_catalog.ProviderIds.Contains(providerId, StringComparer.Ordinal) || !IsAvailable(providerId))
        {
            throw new AiGatewayValidationException("O provedor não faz parte do catálogo.");
        }

        // Um provedor sem chave comercial (servidor local) não guarda credencial: nenhum valor "de enfeite" é aceito nem cifrado.
        if (_providers?.Find(providerId)?.RequiresCredential == false)
        {
            throw new AiGatewayValidationException("Este provedor não usa credencial do tenant.");
        }

        if (string.IsNullOrWhiteSpace(secret) || secret.Length > MaxSecretLength || secret.Any(char.IsControl))
        {
            throw new AiGatewayValidationException("O valor da credencial é inválido.");
        }

        var agora = _clock.GetUtcNow();
        var existente = await _store.FindActiveCredentialAsync(providerId, cancellationToken);
        var id = existente?.Id ?? Guid.CreateVersion7();
        var protegida = _protector.Protect(secret, new AiCredentialBinding(contexto.TenantId, id, providerId));
        var impressao = StoredAiCredential.ComputeFingerprint(contexto.TenantId, id, secret);

        if (existente is null)
        {
            var nova = StoredAiCredential.Create(contexto.TenantId, id, providerId, protegida.KeyId, protegida.Ciphertext, impressao, agora, contexto.Principal?.UserId);
            await _store.SaveCredentialAsync(nova, isNew: true, cancellationToken);
        }
        else
        {
            existente.Replace(protegida.KeyId, protegida.Ciphertext, impressao, agora, contexto.Principal?.UserId);
            await _store.SaveCredentialAsync(existente, isNew: false, cancellationToken);
        }

        // Só o identificador: nunca o valor, o texto cifrado nem a impressão.
        await _audit.WriteAsync(new AuditEntry("ai-gateway.credential.set", CredentialResource, id.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Revoga a credencial ativa do tenant para o provedor: APAGA o texto cifrado. Idempotente: revogar de novo, ou sem credencial, não muda nada.</summary>
    /// <returns>Verdadeiro se havia uma credencial ativa e ela foi revogada agora.</returns>
    public async Task<bool> RevokeCredentialAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);

        var credencial = await _store.FindActiveCredentialAsync(providerId, cancellationToken);
        if (credencial is null || !credencial.Revoke(_clock.GetUtcNow(), contexto.Principal?.UserId))
        {
            return false;
        }

        await _store.SaveCredentialAsync(credencial, isNew: false, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("ai-gateway.credential.revoke", CredentialResource, credencial.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return true;
    }

    /// <summary>Operação da PLATAFORMA: define as sobrescritas de limite do tenant (nunca o próprio tenant). Exige um contexto de sistema do tenant em questão.</summary>
    public async Task SetLimitOverridesAsync(AiTenantLimitOverrides overrides, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);

        var atual = await _store.FindSettingsAsync(cancellationToken)
            ?? throw new AiGatewayValidationException("O tenant ainda não tem configuração de IA: configure o provedor e o modelo antes dos limites.");
        atual.SetLimitOverrides(overrides, _clock.GetUtcNow(), contexto.Principal?.UserId);
        await _store.SaveSettingsAsync(atual, isNew: false, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("ai-gateway.limits.set", SettingsResource, atual.Id.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Operação da PLATAFORMA: bloqueia ou desbloqueia a IA do tenant (abuso, cobrança, investigação). Exige um contexto de sistema do tenant em questão.</summary>
    public async Task SetBlockedAsync(bool blocked, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);

        var atual = await _store.FindSettingsAsync(cancellationToken)
            ?? throw new AiGatewayValidationException("O tenant ainda não tem configuração de IA.");
        atual.SetStatus(blocked ? AiTenantStatus.Blocked : AiTenantStatus.Enabled, _clock.GetUtcNow(), contexto.Principal?.UserId);
        await _store.SaveSettingsAsync(atual, isNew: false, cancellationToken);
        await _audit.WriteAsync(new AuditEntry(blocked ? "ai-gateway.tenant.block" : "ai-gateway.tenant.unblock", SettingsResource, atual.Id.ToString(), AuditOutcome.Success), cancellationToken);
    }
}
