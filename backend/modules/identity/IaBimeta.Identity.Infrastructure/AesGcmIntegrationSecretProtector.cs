using System.Security.Cryptography;
using System.Text;
using IaBimeta.Identity.Application;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// As chaves mestras dos segredos de integração do Embedded (seção <c>Identity:Secrets</c>). São chaves PRÓPRIAS do Identity: nunca as do Channels, do Capabilities nem do AiGateway. Vêm do ambiente (variável de ambiente,
/// user-secrets em desenvolvimento, depois um cofre), nunca de arquivo versionado nem do banco. Cada chave é um valor de 32 bytes em Base64. Girar é acrescentar uma nova, apontá-la como ativa e manter as antigas até
/// os segredos serem regravados (a rotação das chaves de integração gera chaves novas).
/// </summary>
public sealed class IdentitySecretOptions
{
    public const int KeySizeBytes = 32;

    public string? ActiveKeyId { get; set; }

    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Não imprime nenhuma chave.</summary>
    public override string ToString() => $"IdentitySecretOptions {{ ActiveKeyId = {ActiveKeyId}, Keys = {Keys.Count} (valores ocultos) }}";
}

/// <summary>
/// Cifra autenticada (AES-256-GCM, só biblioteca da plataforma) do segredo das chaves de integração. O nonce é aleatório por cifra; o tenant, a conexão e a chave entram como dado autenticado adicional: o mesmo texto
/// cifrado não abre sob outro tenant, outra conexão nem outra chave, e qualquer alteração é detectada. O prefixo de domínio é do Identity. Formato guardado: <c>v1:</c> + Base64(nonce | tag | texto cifrado).
/// Nenhuma exceção repete chave, texto cifrado nem segredo.
/// </summary>
internal sealed class AesGcmIntegrationSecretProtector : IIntegrationSecretProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IdentitySecretOptions _options;

    public AesGcmIntegrationSecretProtector(IdentitySecretOptions options)
    {
        _options = options;
    }

    public bool IsConfigured
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(_options.ActiveKeyId) && ReadKey(_options.ActiveKeyId).Length == IdentitySecretOptions.KeySizeBytes;
            }
            catch (EmbeddedUnavailableException)
            {
                return false;
            }
        }
    }

    public ProtectedIntegrationSecret Protect(string plaintext, IntegrationSecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(binding);

        var chaveAtiva = _options.ActiveKeyId;
        if (string.IsNullOrWhiteSpace(chaveAtiva))
        {
            throw new EmbeddedUnavailableException("secrets-unavailable");
        }

        var chave = ReadKey(chaveAtiva);
        var texto = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cifrado = new byte[texto.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(chave, TagSize);
            aes.Encrypt(nonce, texto, cifrado, tag, AssociatedData(binding));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(texto);
            CryptographicOperations.ZeroMemory(chave);
        }

        var saida = new byte[NonceSize + TagSize + cifrado.Length];
        nonce.CopyTo(saida, 0);
        tag.CopyTo(saida, NonceSize);
        cifrado.CopyTo(saida, NonceSize + TagSize);
        return new ProtectedIntegrationSecret(chaveAtiva, Prefix + Convert.ToBase64String(saida));
    }

    public string Unprotect(ProtectedIntegrationSecret secret, IntegrationSecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(binding);

        if (!secret.Ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new EmbeddedUnavailableException("secret-invalid");
        }

        byte[] bruto;
        try
        {
            bruto = Convert.FromBase64String(secret.Ciphertext[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new EmbeddedUnavailableException("secret-invalid");
        }

        if (bruto.Length < NonceSize + TagSize)
        {
            throw new EmbeddedUnavailableException("secret-invalid");
        }

        var chave = ReadKey(secret.MasterKeyId);
        var texto = new byte[bruto.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(chave, TagSize);
            aes.Decrypt(bruto.AsSpan(0, NonceSize), bruto.AsSpan(NonceSize + TagSize), bruto.AsSpan(NonceSize, TagSize), texto, AssociatedData(binding));
            return Encoding.UTF8.GetString(texto);
        }
        catch (CryptographicException)
        {
            // Texto alterado ou aberto sob outro tenant, conexão ou chave: nunca devolve nada.
            throw new EmbeddedUnavailableException("secret-invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(texto);
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    private byte[] ReadKey(string keyId)
    {
        if (!_options.Keys.TryGetValue(keyId, out var base64) || string.IsNullOrWhiteSpace(base64))
        {
            throw new EmbeddedUnavailableException("secrets-unavailable");
        }

        try
        {
            var chave = Convert.FromBase64String(base64);
            if (chave.Length == IdentitySecretOptions.KeySizeBytes)
            {
                return chave;
            }
        }
        catch (FormatException)
        {
            // Cai na recusa abaixo: nada do conteúdo da chave é repetido.
        }

        throw new EmbeddedUnavailableException("secrets-unavailable");
    }

    private static byte[] AssociatedData(IntegrationSecretBinding binding) =>
        Encoding.UTF8.GetBytes($"ia.bimeta/identity/integration-key/v1|{binding.TenantId.Value:N}|{binding.ConnectionId:N}|{binding.KeyId:N}");
}
