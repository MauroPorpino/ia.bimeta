using System.Security.Cryptography;
using System.Text;
using IaBimeta.Channels.Application;

namespace IaBimeta.Channels.Infrastructure;

/// <summary>
/// Configuração da chave de cifra das credenciais (seção <c>Channels:Secrets</c>). As chaves NUNCA ficam em arquivo versionado
/// nem no banco: vêm do ambiente (variável de ambiente, user-secrets em desenvolvimento, depois um cofre). Cada chave é um
/// valor de 32 bytes em Base64. Girar a chave é acrescentar uma nova, apontá-la como ativa e manter as antigas até as
/// credenciais serem regravadas.
/// </summary>
public sealed class ChannelSecretOptions
{
    public const int KeySizeBytes = 32;

    /// <summary>O identificador da chave que cifra as credenciais novas. Sem ele, nada é cifrado (falha fechada).</summary>
    public string? ActiveKeyId { get; set; }

    /// <summary>As chaves conhecidas, por identificador, em Base64 (32 bytes cada). Decifrar aceita qualquer uma delas.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Cifra autenticada (AES-256-GCM, só biblioteca da plataforma) das credenciais. O nonce é aleatório por cifra, e o tenant, a
/// referência e o papel entram como dado autenticado adicional: o mesmo texto cifrado não abre sob outro tenant, outra
/// referência ou outro papel, e qualquer alteração do texto é detectada. O formato guardado é <c>v1:</c> seguido de
/// Base64(nonce | tag | texto cifrado). Nada do que está aqui vai para log.
/// </summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ChannelSecretOptions _options;

    public AesGcmSecretProtector(ChannelSecretOptions options)
    {
        _options = options;
    }

    public ProtectedSecret Protect(string plaintext, SecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var chaveAtiva = _options.ActiveKeyId;
        if (string.IsNullOrWhiteSpace(chaveAtiva))
        {
            throw new ChannelSecretsNotConfiguredException("não há chave ativa (Channels:Secrets:ActiveKeyId).");
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

        if (!secret.Ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Formato de credencial desconhecido.");
        }

        byte[] bruto;
        try
        {
            bruto = Convert.FromBase64String(secret.Ciphertext[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new CryptographicException("Formato de credencial inválido.");
        }

        if (bruto.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Formato de credencial inválido.");
        }

        var chave = ReadKey(secret.KeyId);
        var texto = new byte[bruto.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(chave, TagSize);
            aes.Decrypt(
                bruto.AsSpan(0, NonceSize),
                bruto.AsSpan(NonceSize + TagSize),
                bruto.AsSpan(NonceSize, TagSize),
                texto,
                AssociatedData(binding));
            return Encoding.UTF8.GetString(texto);
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
            throw new ChannelSecretsNotConfiguredException($"a chave '{keyId}' não está configurada.");
        }

        try
        {
            var chave = Convert.FromBase64String(base64);
            if (chave.Length == ChannelSecretOptions.KeySizeBytes)
            {
                return chave;
            }
        }
        catch (FormatException)
        {
            // Cai na mensagem abaixo: nada do conteúdo da chave é repetido.
        }

        throw new ChannelSecretsNotConfiguredException($"a chave '{keyId}' deve ser {ChannelSecretOptions.KeySizeBytes} bytes em Base64.");
    }

    private static byte[] AssociatedData(SecretBinding binding) =>
        Encoding.UTF8.GetBytes($"ia.bimeta/channels/secret/v1|{binding.TenantId.Value:N}|{binding.SecretId:N}|{(int)binding.Kind}");
}
