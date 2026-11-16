using System.Security.Cryptography;
using System.Text;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Domain;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Autentica as chamadas assinadas (HMAC-SHA256) do servidor do ERP (ADR 0039, seção 3). A assinatura cobre a versão, o instante, o nonce, o método, o caminho e o SHA-256 do corpo bruto:
/// <c>v1 ⏎ timestamp ⏎ nonce ⏎ MÉTODO ⏎ caminho ⏎ sha256hex(corpo)</c>. O caminho assinado é o que o Api enxerga, SEM o prefixo <c>/api</c> do proxy (o prefixo, se presente, é ignorado). O instante precisa estar na
/// janela, o nonce não pode se repetir (só é registrado DEPOIS de a assinatura conferir), a chave precisa estar utilizável, a conexão e o tenant ativos. Toda falha é a mesma <see cref="EmbeddedAuthenticationException"/>:
/// nada revela qual verificação falhou. O tenant sai da conexão da chave, nunca da requisição.
/// </summary>
public sealed class IntegrationRequestAuthenticator
{
    public const string SignatureVersion = "v1";
    private const string ProxyPrefix = "/api";

    private readonly IEmbeddedStore _store;
    private readonly ITenantDirectory _tenants;
    private readonly IIntegrationSecretProtector _protector;
    private readonly IdentityOptions _options;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public IntegrationRequestAuthenticator(IEmbeddedStore store, ITenantDirectory tenants, IIntegrationSecretProtector protector, IdentityOptions options, TimeProvider clock, IAuditWriter audit)
    {
        _store = store;
        _tenants = tenants;
        _protector = protector;
        _options = options;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="EmbeddedAuthenticationException">Qualquer falha de autenticação (sempre a mesma).</exception>
    public async Task<AuthenticatedIntegration> AuthenticateAsync(IntegrationSignedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IntegrationKey.TryParsePublicId(request.KeyId, out var chaveId)
            || !long.TryParse(request.Timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var segundos)
            || !IsValidNonce(request.Nonce)
            || !TryParseSignature(request.Signature, out var assinatura))
        {
            throw new EmbeddedAuthenticationException();
        }

        var achada = await _store.FindKeyAsync(chaveId, cancellationToken) ?? throw new EmbeddedAuthenticationException();
        var tenantId = achada.Key.TenantId;
        var agora = _clock.GetUtcNow();

        // Daqui para baixo o tenant é conhecido: a recusa entra na trilha (uma entrada por tentativa; o limite de taxa do host contém o excesso).
        try
        {
            var instante = DateTimeOffset.FromUnixTimeSeconds(segundos);
            if ((agora - instante).Duration() > _options.IntegrationSignatureWindow
                || !achada.Key.IsUsableAt(agora)
                || !achada.Connection.IsActive)
            {
                throw new EmbeddedAuthenticationException();
            }

            var tenant = await _tenants.FindByIdAsync(tenantId, cancellationToken);
            if (tenant is not { Status: TenantStatus.Active })
            {
                throw new EmbeddedAuthenticationException();
            }

            var esperado = ComputeSignature(Unprotect(achada), request, request.Timestamp!);
            if (!CryptographicOperations.FixedTimeEquals(esperado, assinatura))
            {
                throw new EmbeddedAuthenticationException();
            }

            // A assinatura conferiu: só agora o nonce entra (um atacante sem segredo não enche a tabela). Repetido = replay.
            if (!await _store.TryRecordNonceAsync(tenantId, chaveId, request.Nonce!, agora + _options.IntegrationNonceRetention, cancellationToken))
            {
                throw new EmbeddedAuthenticationException();
            }
        }
        catch (Exception ex) when (ex is EmbeddedAuthenticationException or EmbeddedUnavailableException)
        {
            await _audit.WriteForAuthenticationAsync(tenantId, null, new AuditEntry("identity.embed.integration.authenticate", "integration_key", chaveId.ToString("N"), AuditOutcome.Denied), cancellationToken);
            throw new EmbeddedAuthenticationException();
        }

        var c = achada.Connection;
        return new AuthenticatedIntegration(tenantId, c.Id, chaveId, c.OrganizationId, c.Mode, c.Provisioning, c.AllowedOrigins, c.AllowStartConversation);
    }

    /// <summary>Calcula o HMAC esperado. Exposto para que o ERP de teste e os testes assinem exatamente como o servidor confere.</summary>
    public static byte[] ComputeSignature(string secret, string timestamp, string nonce, string method, string path, byte[] body)
    {
        var canonico = CanonicalString(timestamp, nonce, method, path, body);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonico));
    }

    public static string CanonicalString(string timestamp, string nonce, string method, string path, byte[] body) =>
        $"{SignatureVersion}\n{timestamp}\n{nonce}\n{method.ToUpperInvariant()}\n{NormalizePath(path)}\n{Convert.ToHexStringLower(SHA256.HashData(body))}";

    private static byte[] ComputeSignature(string secret, IntegrationSignedRequest request, string timestamp) =>
        ComputeSignature(secret, timestamp, request.Nonce!, request.Method, request.Path, request.Body);

    private string Unprotect(IntegrationKeyLookup achada) =>
        _protector.Unprotect(
            new ProtectedIntegrationSecret(achada.Key.SecretKeyId, achada.Key.SecretCiphertext),
            new IntegrationSecretBinding(achada.Key.TenantId, achada.Key.ConnectionId, achada.Key.Id));

    private static string NormalizePath(string path)
    {
        var caminho = path ?? string.Empty;
        return caminho.StartsWith(ProxyPrefix + "/", StringComparison.Ordinal) ? caminho[ProxyPrefix.Length..] : caminho;
    }

    private static bool IsValidNonce(string? nonce)
    {
        if (nonce is null || nonce.Length is < 16 or > 64)
        {
            return false;
        }

        foreach (var c in nonce)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseSignature(string? hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (hex is not { Length: 64 })
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
