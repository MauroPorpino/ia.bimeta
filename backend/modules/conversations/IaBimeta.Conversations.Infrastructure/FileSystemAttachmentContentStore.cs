using System.Text.RegularExpressions;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// O armazenamento privado de arquivos de mídia em uma pasta do servidor (ADR 0028): <c>{raiz}/{tenant}/{arquivo}.bin</c>. A pasta fica fora de qualquer caminho servido e nenhuma URL aponta para ela; o
/// conteúdo só sai por <see cref="OpenReadAsync"/>. Os nomes dos arquivos são identificadores gerados aqui (nunca texto do cliente), então não há caminho montado a partir de entrada externa; a chave que volta
/// do banco é conferida (formato exato e tenant) e o caminho resolvido tem de ficar DENTRO da raiz. A gravação é atômica (arquivo temporário e renomeação) e idempotente por arquivo. Para a nuvem, a mesma porta
/// ganha uma implementação de objeto (chave, sem URL pública).
/// </summary>
public sealed partial class FileSystemAttachmentContentStore : IAttachmentContentStore
{
    private readonly string _root;

    public FileSystemAttachmentContentStore(AttachmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var raiz = string.IsNullOrWhiteSpace(options.RootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IaBimeta", "attachments")
            : options.RootPath;
        _root = Path.GetFullPath(raiz);
    }

    /// <summary>A pasta raiz resolvida (para diagnóstico e testes).</summary>
    public string Root => _root;

    [GeneratedRegex("^[0-9a-f]{32}/[0-9a-f]{32}\\.bin$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public async Task<string> WriteAsync(TenantId tenant, Guid attachmentId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (attachmentId == Guid.Empty)
        {
            throw new ArgumentException("O identificador do arquivo é obrigatório.", nameof(attachmentId));
        }

        var chave = $"{tenant.Value:N}/{attachmentId:N}.bin";
        var destino = Resolve(chave);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

        // Arquivo temporário na mesma pasta e renomeação: quem lê nunca vê um arquivo pela metade, e repetir a gravação troca pelo mesmo conteúdo.
        var temporario = destino + "." + Guid.NewGuid().ToString("N") + ".parcial";
        try
        {
            await using (var saida = new FileStream(temporario, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await saida.WriteAsync(content, cancellationToken);
                await saida.FlushAsync(cancellationToken);
            }

            await MoveIntoPlaceAsync(temporario, destino, content.Length, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporario))
            {
                File.Delete(temporario);
            }
        }

        return chave;
    }

    /// <summary>
    /// Troca o temporário pelo destino. No Windows, duas renomeações para o MESMO destino ao mesmo tempo (dois Workers concluindo o mesmo arquivo) ou uma leitura em andamento fazem a troca falhar com "acesso negado"
    /// por um instante: tenta de novo com espera curta. O conteúdo de um arquivo é sempre o mesmo (a chave é por arquivo), então, se mesmo assim a troca não vier, mas o destino já está inteiro (mesmo tamanho),
    /// quem chegou antes já gravou o que este processo queria gravar.
    /// </summary>
    private static async Task MoveIntoPlaceAsync(string temporario, string destino, int tamanho, CancellationToken cancellationToken)
    {
        const int tentativas = 6;
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                File.Move(temporario, destino, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (tentativa >= tentativas)
                {
                    if (File.Exists(destino) && new FileInfo(destino).Length == tamanho)
                    {
                        return;
                    }

                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(15 * tentativa), cancellationToken);
            }
        }
    }

    public Task<Stream?> OpenReadAsync(TenantId tenant, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        // Só a forma exata gerada aqui, e só o tenant do contexto: uma chave de outro tenant (ou adulterada) é inexistente.
        if (string.IsNullOrEmpty(key) || !KeyPattern().IsMatch(key) || !key.StartsWith($"{tenant.Value:N}/", StringComparison.Ordinal))
        {
            return Task.FromResult<Stream?>(null);
        }

        var caminho = Resolve(key);
        if (!File.Exists(caminho))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream fluxo = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream?>(fluxo);
    }

    public Task DeleteAsync(TenantId tenant, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        // Só a forma exata gerada aqui, e só o tenant do contexto: o que não é desta empresa nunca é apagado.
        if (string.IsNullOrEmpty(key) || !KeyPattern().IsMatch(key) || !key.StartsWith($"{tenant.Value:N}/", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var caminho = Resolve(key);
        try
        {
            File.Delete(caminho);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException)
        {
            // Nada a apagar.
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredAttachmentObject> ListOlderThanAsync(DateTimeOffset cutoff, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (var pasta in Directory.EnumerateDirectories(_root))
        {
            var nomeDaPasta = Path.GetFileName(pasta);
            if (!Guid.TryParseExact(nomeDaPasta, "N", out var tenant))
            {
                continue;
            }

            foreach (var arquivo in Directory.EnumerateFiles(pasta, "*.bin"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chave = $"{nomeDaPasta}/{Path.GetFileName(arquivo)}";
                if (!KeyPattern().IsMatch(chave) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(arquivo), "N", out var anexo))
                {
                    continue;
                }

                if (new DateTimeOffset(File.GetLastWriteTimeUtc(arquivo), TimeSpan.Zero) < cutoff)
                {
                    yield return new StoredAttachmentObject(tenant, anexo, chave);
                }
            }

            await Task.Yield();
        }
    }

    private string Resolve(string key)
    {
        var completo = Path.GetFullPath(Path.Combine(_root, key));
        if (!completo.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A chave do arquivo sai da pasta de armazenamento.");
        }

        return completo;
    }
}
