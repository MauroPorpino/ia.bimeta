using System.Security.Cryptography;
using System.Text;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Application;

namespace IaBimeta.Workflows.Infrastructure;

/// <summary>
/// A chave mestra da cifra das variáveis e dos efeitos do módulo (seção <c>Workflows:Secrets</c>). São chaves PRÓPRIAS do Workflows: nunca as do Capabilities, do Channels nem do AiGateway. As chaves vêm do ambiente
/// (nunca de arquivo versionado nem do banco); cada chave é um valor de 32 bytes em Base64. Girar a chave é acrescentar uma nova, apontá-la como ativa e manter as antigas até as instâncias terminarem.
/// </summary>
public sealed class WorkflowsSecretOptions
{
    public const int KeySizeBytes = 32;

    public string? ActiveKeyId { get; set; }

    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Não imprime nenhuma chave.</summary>
    public override string ToString() => $"WorkflowsSecretOptions {{ ActiveKeyId = {ActiveKeyId}, Keys = {Keys.Count} (valores ocultos) }}";
}

/// <summary>
/// Cifra autenticada (AES-256-GCM, só biblioteca da plataforma). O nonce é aleatório por cifra; a finalidade, o tenant e a instância (ou o efeito) entram como dado autenticado adicional: o mesmo texto cifrado não
/// abre sob outro tenant, outra instância nem outra finalidade, e qualquer alteração é detectada. O prefixo de domínio é do Workflows. Formato: <c>v1:</c> + Base64(nonce | tag | texto cifrado). Nenhuma exceção
/// repete chave, texto cifrado nem conteúdo.
/// </summary>
internal sealed class AesGcmFlowCipher : IFlowCipher
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly WorkflowsSecretOptions _options;

    public AesGcmFlowCipher(WorkflowsSecretOptions options)
    {
        _options = options;
    }

    public bool IsConfigured
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(_options.ActiveKeyId) && ReadKey(_options.ActiveKeyId).Length == WorkflowsSecretOptions.KeySizeBytes;
            }
            catch (WorkflowsUnavailableException)
            {
                return false;
            }
        }
    }

    public FlowCipherText Protect(string plaintext, TenantId tenant, Guid resourceId, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(tenant);

        var chaveAtiva = _options.ActiveKeyId;
        if (string.IsNullOrWhiteSpace(chaveAtiva))
        {
            throw new WorkflowsUnavailableException("secrets-unavailable");
        }

        var chave = ReadKey(chaveAtiva);
        var texto = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cifrado = new byte[texto.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(chave, TagSize);
            aes.Encrypt(nonce, texto, cifrado, tag, AssociatedData(tenant, resourceId, purpose));
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
        return new FlowCipherText(chaveAtiva, Prefix + Convert.ToBase64String(saida));
    }

    public string Unprotect(FlowCipherText text, TenantId tenant, Guid resourceId, string purpose)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tenant);

        if (!text.Ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new WorkflowsUnavailableException("secret-invalid");
        }

        byte[] bruto;
        try
        {
            bruto = Convert.FromBase64String(text.Ciphertext[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new WorkflowsUnavailableException("secret-invalid");
        }

        if (bruto.Length < NonceSize + TagSize)
        {
            throw new WorkflowsUnavailableException("secret-invalid");
        }

        var chave = ReadKey(text.KeyId);
        var claro = new byte[bruto.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(chave, TagSize);
            aes.Decrypt(bruto.AsSpan(0, NonceSize), bruto.AsSpan(NonceSize + TagSize), bruto.AsSpan(NonceSize, TagSize), claro, AssociatedData(tenant, resourceId, purpose));
            return Encoding.UTF8.GetString(claro);
        }
        catch (CryptographicException)
        {
            // Texto alterado ou aberto sob outro tenant, recurso ou finalidade: nunca devolve nada.
            throw new WorkflowsUnavailableException("secret-invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    private byte[] ReadKey(string keyId)
    {
        if (!_options.Keys.TryGetValue(keyId, out var base64) || string.IsNullOrWhiteSpace(base64))
        {
            throw new WorkflowsUnavailableException("secrets-unavailable");
        }

        try
        {
            var chave = Convert.FromBase64String(base64);
            if (chave.Length == WorkflowsSecretOptions.KeySizeBytes)
            {
                return chave;
            }
        }
        catch (FormatException)
        {
            // Cai na recusa abaixo: nada do conteúdo da chave é repetido.
        }

        throw new WorkflowsUnavailableException("secrets-unavailable");
    }

    private static byte[] AssociatedData(TenantId tenant, Guid resourceId, string purpose) =>
        Encoding.UTF8.GetBytes($"ia.bimeta/workflows/{purpose}/v1|{tenant.Value:N}|{resourceId:N}");
}
