using System.Security.Cryptography;
using System.Text;
using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Domain;

namespace IaBimeta.Capabilities.Infrastructure;

/// <summary>
/// A chave mestra dos segredos do módulo (seção <c>Capabilities:Secrets</c>). São chaves PRÓPRIAS do Capabilities: nunca as do Channels nem as do AiGateway. As chaves vêm do ambiente (variável de ambiente,
/// user-secrets em desenvolvimento, depois um cofre), nunca de arquivo versionado nem do banco. Cada chave é um valor de 32 bytes em Base64. Girar a chave é acrescentar uma nova, apontá-la como ativa e manter as
/// antigas até os segredos serem regravados.
/// </summary>
public sealed class CapabilitiesSecretOptions
{
    public const int KeySizeBytes = 32;

    public string? ActiveKeyId { get; set; }

    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Não imprime nenhuma chave.</summary>
    public override string ToString() => $"CapabilitiesSecretOptions {{ ActiveKeyId = {ActiveKeyId}, Keys = {Keys.Count} (valores ocultos) }}";
}

/// <summary>
/// Cifra autenticada (AES-256-GCM, só biblioteca da plataforma) dos segredos do módulo. O nonce é aleatório por cifra; a finalidade, o tenant, o recurso e um discriminador (tipo de autenticação e versão) entram como
/// dado autenticado adicional: o mesmo texto cifrado não abre sob outro tenant, outra conexão, outra finalidade nem outra versão, e qualquer alteração é detectada. O prefixo de domínio é do Capabilities, então um texto
/// cifrado de outro módulo não abre aqui. Formato guardado: <c>v1:</c> + Base64(nonce | tag | texto cifrado). Nenhuma exceção repete chave, texto cifrado nem segredo.
/// </summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly CapabilitiesSecretOptions _options;

    public AesGcmSecretProtector(CapabilitiesSecretOptions options)
    {
        _options = options;
    }

    public bool IsConfigured
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(_options.ActiveKeyId) && ReadKey(_options.ActiveKeyId).Length == CapabilitiesSecretOptions.KeySizeBytes;
            }
            catch (CapabilitiesUnavailableException)
            {
                return false;
            }
        }
    }

    public ProtectedSecret Protect(string plaintext, SecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(binding);

        var chaveAtiva = _options.ActiveKeyId;
        if (string.IsNullOrWhiteSpace(chaveAtiva))
        {
            throw new CapabilitiesUnavailableException("secrets-unavailable");
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
        return new ProtectedSecret(chaveAtiva, Prefix + Convert.ToBase64String(saida));
    }

    public string Unprotect(ProtectedSecret secret, SecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(binding);

        if (!secret.Ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CapabilitiesUnavailableException("secret-invalid");
        }

        byte[] bruto;
        try
        {
            bruto = Convert.FromBase64String(secret.Ciphertext[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new CapabilitiesUnavailableException("secret-invalid");
        }

        if (bruto.Length < NonceSize + TagSize)
        {
            throw new CapabilitiesUnavailableException("secret-invalid");
        }

        var chave = ReadKey(secret.KeyId);
        var texto = new byte[bruto.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(chave, TagSize);
            aes.Decrypt(bruto.AsSpan(0, NonceSize), bruto.AsSpan(NonceSize + TagSize), bruto.AsSpan(NonceSize, TagSize), texto, AssociatedData(binding));
            return Encoding.UTF8.GetString(texto);
        }
        catch (CryptographicException)
        {
            // Texto alterado ou aberto sob outro tenant, recurso, finalidade ou versão: nunca devolve nada.
            throw new CapabilitiesUnavailableException("secret-invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(texto);
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    /// <summary>HMAC-SHA256 com a chave ativa (um atacante com acesso só ao banco não recupera os parâmetros por força bruta). Sem chave configurada, SHA-256 simples: continua detectando a mesma chave com parâmetros diferentes.</summary>
    public string Fingerprint(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        if (IsConfigured)
        {
            var chave = ReadKey(_options.ActiveKeyId!);
            try
            {
                return Convert.ToHexStringLower(HMACSHA256.HashData(chave, bytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(chave);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private byte[] ReadKey(string keyId)
    {
        if (!_options.Keys.TryGetValue(keyId, out var base64) || string.IsNullOrWhiteSpace(base64))
        {
            throw new CapabilitiesUnavailableException("secrets-unavailable");
        }

        try
        {
            var chave = Convert.FromBase64String(base64);
            if (chave.Length == CapabilitiesSecretOptions.KeySizeBytes)
            {
                return chave;
            }
        }
        catch (FormatException)
        {
            // Cai na recusa abaixo: nada do conteúdo da chave é repetido.
        }

        throw new CapabilitiesUnavailableException("secrets-unavailable");
    }

    private static byte[] AssociatedData(SecretBinding binding) =>
        Encoding.UTF8.GetBytes($"ia.bimeta/capabilities/{binding.Purpose}/v1|{binding.TenantId.Value:N}|{binding.ResourceId:N}|{binding.Discriminator}");
}
