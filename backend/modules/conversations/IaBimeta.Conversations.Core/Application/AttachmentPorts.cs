using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// O armazenamento PRIVADO do conteúdo dos arquivos de mídia (ADR 0028). Nada aqui produz URL: o conteúdo só sai por <see cref="OpenReadAsync"/>, chamado por uma operação já autorizada. Cada operação recebe o tenant
/// e só enxerga o dele (a chave opaca carrega o tenant e a implementação o confere). A implementação de hoje é a pasta privada do servidor; trocar por S3 ou Azure Blob é trocar esta porta (chave de objeto, sem URL
/// pública, com acesso do aplicativo por identidade).
/// </summary>
public interface IAttachmentContentStore
{
    /// <summary>Guarda o conteúdo do arquivo e devolve a chave opaca. Idempotente por arquivo: repetir regrava o mesmo objeto (nunca cria outro).</summary>
    Task<string> WriteAsync(TenantId tenant, Guid attachmentId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Abre o conteúdo para leitura, ou nulo se não existe PARA ESTE TENANT (uma chave de outro tenant é inexistente).</summary>
    Task<Stream?> OpenReadAsync(TenantId tenant, string key, CancellationToken cancellationToken);
}

public enum ScanVerdict
{
    /// <summary>Nenhum mecanismo de varredura está ligado: o arquivo NÃO foi verificado quanto a código malicioso.</summary>
    NotScanned = 1,

    Clean = 2,

    /// <summary>O mecanismo achou código malicioso: o arquivo é recusado.</summary>
    Infected = 3,
}

/// <summary>
/// O ponto de ligação de um mecanismo de varredura de código malicioso (por exemplo, um antivírus por soquete). O MVP NÃO traz nenhum: a implementação padrão devolve <see cref="ScanVerdict.NotScanned"/> e a
/// proteção do MVP é a política de tipos, a assinatura do conteúdo, o limite de tamanho e a entrega ao navegador sem execução (ADR 0028).
/// </summary>
public interface IAttachmentScanner
{
    Task<ScanVerdict> ScanAsync(MessageKind kind, string mimeType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

public sealed class NullAttachmentScanner : IAttachmentScanner
{
    public Task<ScanVerdict> ScanAsync(MessageKind kind, string mimeType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => Task.FromResult(ScanVerdict.NotScanned);
}
