using System.Security.Cryptography;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.AiGateway.Infrastructure;

/// <summary>
/// O armazenamento da configuração de IA do tenant do contexto (EF, com o filtro global por tenant). Uma alteração concorrente do mesmo ponto de partida falha com
/// <see cref="AiConfigurationConflictException"/> (token de versão); uma violação do índice único de configuração ou de credencial ativa também (outra operação gravou primeiro).
/// </summary>
internal sealed class AiTenantConfigurationStore : IAiTenantConfigurationStore
{
    private readonly AiGatewayDbContext _db;

    public AiTenantConfigurationStore(AiGatewayDbContext db)
    {
        _db = db;
    }

    public Task<AiTenantSettings?> FindSettingsAsync(CancellationToken cancellationToken) =>
        _db.Settings.SingleOrDefaultAsync(cancellationToken);

    public Task<StoredAiCredential?> FindActiveCredentialAsync(string providerId, CancellationToken cancellationToken) =>
        _db.Credentials.SingleOrDefaultAsync(c => c.ProviderId == providerId && c.Status == StoredAiCredentialStatus.Active, cancellationToken);

    public async Task SaveSettingsAsync(AiTenantSettings settings, bool isNew, CancellationToken cancellationToken)
    {
        if (isNew)
        {
            _db.Settings.Add(settings);
        }

        await SaveAsync(cancellationToken);
    }

    public async Task SaveCredentialAsync(StoredAiCredential credential, bool isNew, CancellationToken cancellationToken)
    {
        if (isNew)
        {
            _db.Credentials.Add(credential);
        }

        await SaveAsync(cancellationToken);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AiConfigurationConflictException();
        }
        catch (DbUpdateException falha) when (PostgresErrors.IsUniqueViolation(falha))
        {
            throw new AiConfigurationConflictException();
        }
    }
}

/// <summary>
/// Lê a configuração de IA do tenant e a funde com os limites padrão da plataforma. Filtra por tenant (e o AiGateway ainda confere o que voltou). Sem configuração, devolve nulo. Uma configuração
/// cujos limites não se completam (nem sobrescrita, nem padrão) volta com limites inválidos, que o seletor recusa.
/// </summary>
internal sealed class TenantAiConfigurationReader : ITenantAiConfigurationReader
{
    private readonly AiGatewayDbContext _db;
    private readonly AiDefaultLimits _defaults;

    public TenantAiConfigurationReader(AiGatewayDbContext db, AiDefaultLimits defaults)
    {
        _db = db;
        _defaults = defaults;
    }

    public async Task<AiTenantConfiguration?> GetAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        // O filtro global é o do tenant do CONTEXTO; pedir outro tenant aqui é defeito do chamador e nunca devolve a configuração alheia.
        if (tenantId != _db.CurrentTenant)
        {
            throw new CrossTenantAccessException("Operação bloqueada: a configuração de IA de outro tenant não pode ser lida neste contexto.");
        }

        var linha = await _db.Settings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return linha is null
            ? null
            : new AiTenantConfiguration(linha.TenantId, linha.ProviderId, linha.ModelId, linha.CredentialSource, _defaults.Merge(linha.LimitOverrides), linha.Status);
    }
}

/// <summary>
/// Resolve a credencial BYOK do tenant NA HORA do uso: lê a ativa de (tenant, provedor), decifra com o tenant, a credencial e o provedor como dado autenticado, e devolve só em memória.
/// Sem credencial ativa, devolve nulo. Sem a chave mestra (ou com um texto que não abre sob estes dados), a falha é FECHADA: <see cref="AiSecretsUnavailableException"/> para a chave ausente, e
/// nulo para o texto que não autentica. Nada do conteúdo vai a mensagem de erro.
/// </summary>
internal sealed class AiCredentialResolver : IAiCredentialResolver
{
    private readonly AiGatewayDbContext _db;
    private readonly IAiCredentialProtector _protector;

    public AiCredentialResolver(AiGatewayDbContext db, IAiCredentialProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<AiCredential?> ResolveAsync(TenantId tenantId, string providerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(providerId);

        if (tenantId != _db.CurrentTenant)
        {
            throw new CrossTenantAccessException("Operação bloqueada: a credencial de IA de outro tenant não pode ser lida neste contexto.");
        }

        var credencial = await _db.Credentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.ProviderId == providerId && c.Status == StoredAiCredentialStatus.Active, cancellationToken);
        if (credencial?.Ciphertext is null || credencial.KeyId is null)
        {
            return null;
        }

        try
        {
            var valor = _protector.Unprotect(new AiProtectedCredential(credencial.KeyId, credencial.Ciphertext), new AiCredentialBinding(credencial.TenantId, credencial.Id, credencial.ProviderId));
            return new AiCredential(valor);
        }
        catch (CryptographicException)
        {
            // Texto adulterado, copiado de outro tenant ou de outro provedor: não autentica, e a credencial é tratada como ausente (falha fechada), sem repetir nada do conteúdo.
            return null;
        }
    }
}
