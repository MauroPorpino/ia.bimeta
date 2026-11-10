using System.Security.Cryptography;
using System.Text;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// A cifra em repouso dos arquivos (ADR 0032): AES-256-GCM, o mesmo mecanismo e as mesmas convenções de chave das credenciais do Channels (Base64 de 32 bytes, chave ativa e chaves antigas só para decifrar). Envolve o
/// armazenamento privado: o que vai ao disco é o objeto cifrado e o que sai é o conteúdo original. A chave de cada EMPRESA é derivada da chave mestra (HKDF-SHA256 com o tenant), e o tenant, o arquivo e a chave entram
/// como dado autenticado adicional: o objeto de uma empresa não abre sob outra, um objeto trocado de lugar (outro arquivo) não abre, e qualquer alteração ou truncamento é detectada. Formato do objeto:
/// <c>IABA | versão(1) | tamanho do id da chave(1) | id da chave | nonce(12) | tag(16) | texto cifrado</c>.
/// <para>
/// Objetos antigos em texto claro (sem o cabeçalho <c>IABA</c>) continuam legíveis até <see cref="EncryptInPlaceAsync"/> (o comando <c>attachments encrypt-existing</c>) os converter. Um objeto cifrado cuja chave não
/// está configurada, ou que não autentica, lança <see cref="AttachmentContentUnreadableException"/>: nunca devolve o texto cifrado como se fosse o arquivo.
/// </para>
/// </summary>
public sealed class EncryptedAttachmentContentStore : IAttachmentContentStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const byte FormatVersion = 1;
    private static readonly byte[] Magic = "IABA"u8.ToArray();

    private readonly IAttachmentContentStore _inner;
    private readonly AttachmentEncryptionSettings _settings;

    public EncryptedAttachmentContentStore(IAttachmentContentStore inner, AttachmentEncryptionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(settings);
        _inner = inner;
        _settings = settings;
    }

    /// <summary>O objeto começa com o cabeçalho dos objetos cifrados?</summary>
    public static bool HasEncryptedHeader(ReadOnlySpan<byte> bytes) => bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);

    public async Task<string> WriteAsync(TenantId tenant, Guid attachmentId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return await _inner.WriteAsync(tenant, attachmentId, content, cancellationToken);
        }

        var objeto = Encrypt(tenant, attachmentId, _settings.ActiveKeyId!, content.Span);
        return await _inner.WriteAsync(tenant, attachmentId, objeto, cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(TenantId tenant, string key, CancellationToken cancellationToken)
    {
        var bruto = await ReadRawAsync(tenant, key, cancellationToken);
        if (bruto is null)
        {
            return null;
        }

        if (!HasEncryptedHeader(bruto))
        {
            // Objeto antigo em texto claro: segue legível (a integridade é conferida pelo resumo gravado na linha do arquivo).
            return new MemoryStream(bruto, writable: false);
        }

        return new MemoryStream(Decrypt(tenant, IdOf(key), bruto), writable: false);
    }

    public Task DeleteAsync(TenantId tenant, string key, CancellationToken cancellationToken) => _inner.DeleteAsync(tenant, key, cancellationToken);

    public IAsyncEnumerable<StoredAttachmentObject> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) => _inner.ListOlderThanAsync(cutoff, cancellationToken);

    /// <summary>Os bytes do objeto COMO ESTÃO no disco (cifrados ou não), ou nulo se não existe para o tenant. Para a manutenção (verificar e converter).</summary>
    public async Task<byte[]?> ReadRawAsync(TenantId tenant, string key, CancellationToken cancellationToken)
    {
        await using var fluxo = await _inner.OpenReadAsync(tenant, key, cancellationToken);
        if (fluxo is null)
        {
            return null;
        }

        // Nada acima do teto absoluto (mais o cabeçalho) entra na memória: um objeto maior que isso não é um arquivo que este sistema guardou.
        if (fluxo.CanSeek && fluxo.Length > AttachmentOptions.HardMaxBytes + 4096)
        {
            throw new AttachmentContentUnreadableException("O objeto é maior que o teto de armazenamento.");
        }

        using var memoria = new MemoryStream();
        await fluxo.CopyToAsync(memoria, cancellationToken);
        return memoria.ToArray();
    }

    /// <summary>
    /// Converte um objeto antigo em texto claro para o formato cifrado, no lugar (a troca é atômica: quem lê vê o antigo ou o novo, nunca um pela metade). Devolve falso se o objeto não existe ou já está cifrado.
    /// Exige a cifra ligada. Idempotente.
    /// </summary>
    public async Task<bool> EncryptInPlaceAsync(TenantId tenant, string key, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            throw new InvalidOperationException("A cifra de arquivos não está ligada (Conversations:Attachments:Encryption:ActiveKeyId).");
        }

        var bruto = await ReadRawAsync(tenant, key, cancellationToken);
        if (bruto is null || HasEncryptedHeader(bruto))
        {
            return false;
        }

        var objeto = Encrypt(tenant, IdOf(key), _settings.ActiveKeyId!, bruto);
        await _inner.WriteAsync(tenant, IdOf(key), objeto, cancellationToken);
        return true;
    }

    private byte[] Encrypt(TenantId tenant, Guid attachmentId, string keyId, ReadOnlySpan<byte> plaintext)
    {
        var chave = DeriveKey(tenant, keyId);
        try
        {
            var idBytes = Encoding.ASCII.GetBytes(keyId);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cabecalho = Magic.Length + 1 + 1 + idBytes.Length;
            var saida = new byte[cabecalho + NonceSize + TagSize + plaintext.Length];

            Magic.CopyTo(saida, 0);
            saida[Magic.Length] = FormatVersion;
            saida[Magic.Length + 1] = (byte)idBytes.Length;
            idBytes.CopyTo(saida, Magic.Length + 2);
            nonce.CopyTo(saida, cabecalho);

            using var aes = new AesGcm(chave, TagSize);
            aes.Encrypt(nonce, plaintext, saida.AsSpan(cabecalho + NonceSize + TagSize), saida.AsSpan(cabecalho + NonceSize, TagSize), AssociatedData(tenant, attachmentId, keyId));
            return saida;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    private byte[] Decrypt(TenantId tenant, Guid attachmentId, byte[] objeto)
    {
        // Cabeçalho: magia, versão, id da chave. Qualquer coisa fora do formato é ilegível (e nunca é tratada como texto claro).
        if (objeto.Length < Magic.Length + 2 || objeto[Magic.Length] != FormatVersion)
        {
            throw new AttachmentContentUnreadableException("O objeto cifrado está em um formato desconhecido.");
        }

        var tamanhoDoId = objeto[Magic.Length + 1];
        var cabecalho = Magic.Length + 2 + tamanhoDoId;
        if (tamanhoDoId == 0 || objeto.Length < cabecalho + NonceSize + TagSize)
        {
            throw new AttachmentContentUnreadableException("O objeto cifrado está truncado.");
        }

        var keyId = Encoding.ASCII.GetString(objeto, Magic.Length + 2, tamanhoDoId);
        if (!_settings.Keys.ContainsKey(keyId))
        {
            throw new AttachmentContentUnreadableException("A chave que cifrou este objeto não está configurada.");
        }

        var chave = DeriveKey(tenant, keyId);
        try
        {
            var texto = new byte[objeto.Length - cabecalho - NonceSize - TagSize];
            using var aes = new AesGcm(chave, TagSize);
            aes.Decrypt(
                objeto.AsSpan(cabecalho, NonceSize),
                objeto.AsSpan(cabecalho + NonceSize + TagSize),
                objeto.AsSpan(cabecalho + NonceSize, TagSize),
                texto,
                AssociatedData(tenant, attachmentId, keyId));
            return texto;
        }
        catch (CryptographicException)
        {
            // Chave errada, objeto adulterado, truncado ou de outra empresa/arquivo: a autenticação falha do mesmo modo e nada é devolvido.
            throw new AttachmentContentUnreadableException("O objeto cifrado não autentica (chave errada ou conteúdo alterado).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    private byte[] DeriveKey(TenantId tenant, string keyId)
    {
        if (!_settings.Keys.TryGetValue(keyId, out var base64) || !AttachmentEncryptionSettings.TryReadKey(base64, out var mestra))
        {
            throw new AttachmentContentUnreadableException("A chave de cifra de arquivos não está configurada.");
        }

        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, mestra, 32, salt: null, info: Encoding.ASCII.GetBytes($"iabimeta/attachments/v1/{tenant.Value:N}"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mestra);
        }
    }

    private static byte[] AssociatedData(TenantId tenant, Guid attachmentId, string keyId) => Encoding.ASCII.GetBytes($"iabimeta-att-v1|{tenant.Value:N}|{attachmentId:N}|{keyId}");

    /// <summary>O identificador do arquivo, tirado da chave <c>{tenant}/{arquivo}.bin</c> (a forma é conferida pelo armazenamento de baixo).</summary>
    private static Guid IdOf(string key)
    {
        var nome = Path.GetFileNameWithoutExtension(key);
        if (!Guid.TryParseExact(nome, "N", out var id))
        {
            throw new AttachmentContentUnreadableException("A chave do objeto não tem a forma esperada.");
        }

        return id;
    }
}
