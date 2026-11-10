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
    /// <exception cref="AttachmentContentUnreadableException">O objeto existe mas não pode ser lido ou decifrado.</exception>
    Task<Stream?> OpenReadAsync(TenantId tenant, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Apaga o objeto (ADR 0031: o prazo de guarda do arquivo enviado venceu, ou a gravação da mensagem não se completou). Idempotente: o objeto que não existe (ou a chave de outro tenant, ou adulterada) é sucesso sem efeito;
    /// o tenant é conferido como na leitura.
    /// </summary>
    Task DeleteAsync(TenantId tenant, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Os objetos de TODOS os tenants cuja última gravação é anterior ao corte (a limpeza de objetos órfãos, ADR 0031: gravados e sem linha que os referencie). Só devolve o que tem a forma exata das chaves geradas aqui.
    /// </summary>
    IAsyncEnumerable<StoredAttachmentObject> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

/// <summary>
/// O objeto existe mas o conteúdo NÃO pode ser entregue (ADR 0032): a chave de cifra que o protege não está configurada, a chave está errada, o objeto foi adulterado ou truncado (a autenticação da cifra falhou) ou o
/// sistema operacional recusou a leitura (por exemplo, o antivírus do servidor pôs o arquivo em quarentena). Nunca devolve conteúdo parcial. A mensagem nunca repete chave, caminho nem conteúdo.
/// </summary>
public sealed class AttachmentContentUnreadableException : Exception
{
    public AttachmentContentUnreadableException(string reason)
        : base(reason)
    {
    }
}

/// <summary>Um objeto guardado: o tenant e o arquivo a que a chave pertence, e a chave opaca (a mesma gravada na linha do arquivo).</summary>
public sealed record StoredAttachmentObject(Guid TenantId, Guid AttachmentId, string Key, DateTimeOffset LastWriteUtc);

public enum ScanVerdict
{
    /// <summary>Nenhum mecanismo de varredura está ligado: o arquivo NÃO foi verificado quanto a código malicioso.</summary>
    NotScanned = 1,

    Clean = 2,

    /// <summary>O mecanismo achou código malicioso: o arquivo é recusado.</summary>
    Infected = 3,

    /// <summary>O verificador está ligado mas não respondeu (fora do ar, tempo esgotado, erro, resposta ilegível): NUNCA vale como limpo. Com a varredura exigida o arquivo não é liberado; sem ela, passa como não verificado.</summary>
    Unavailable = 4,
}

/// <summary>
/// O ponto de ligação de um mecanismo de varredura de código malicioso. O host liga o <c>clamd</c> do ClamAV por soquete (ADR 0032); a implementação nula devolve <see cref="ScanVerdict.NotScanned"/>. Além da varredura,
/// a proteção é a política de tipos, a assinatura do conteúdo, o limite de tamanho e a entrega ao navegador sem execução (ADR 0028).
/// </summary>
public interface IAttachmentScanner
{
    Task<ScanVerdict> ScanAsync(MessageKind kind, string mimeType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

public sealed class NullAttachmentScanner : IAttachmentScanner
{
    public Task<ScanVerdict> ScanAsync(MessageKind kind, string mimeType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => Task.FromResult(ScanVerdict.NotScanned);
}
