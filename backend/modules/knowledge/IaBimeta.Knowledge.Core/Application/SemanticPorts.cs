using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Knowledge.Application;

/// <summary>Um texto reservado para vetorizar (visão de SISTEMA: atravessa os tenants só para processar a fila).</summary>
public sealed record PendingSemanticText(TenantId Tenant, int EntryOrdinal, SemanticTextKind Kind, int AliasOrdinal, string Text, string TextHash, int Attempts);

public sealed record SemanticHit(int DefinitionNumber, int EntryOrdinal, SemanticTextKind Kind, int AliasOrdinal, double Score);

/// <summary>O armazenamento do índice semântico (PostgreSQL com pgvector). Todo método que lê ou grava dado de um tenant recebe o tenant e SÓ age nele.</summary>
public interface ISemanticStore
{
    /// <summary>Substitui o índice do tenant pela fonte: texto igual (mesmo hash, modelo e dimensão) reaproveita o vetor; texto novo ou de outro modelo fica pendente; o que saiu é removido. Transacional.</summary>
    Task<SemanticSyncResult> ReplaceAsync(TenantId tenant, SemanticSource source, string modelId, int dimension, Func<string, string> hash, CancellationToken cancellationToken);

    Task DeleteAllAsync(TenantId tenant, CancellationToken cancellationToken);

    Task<SemanticIndexStatus> StatusAsync(TenantId tenant, string modelId, int dimension, CancellationToken cancellationToken);

    /// <summary>Recoloca na fila (pendentes, sem vetor) os textos indexados por OUTRO modelo ou dimensão, recalculando o resumo no modelo vigente (visão de sistema). Devolve quantos foram recolocados.</summary>
    Task<int> RequeueOtherModelsAsync(int limit, string modelId, int dimension, Func<string, string> hash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Reserva até <paramref name="batch"/> textos pendentes (de todos os tenants), prontos para tentar agora, para o modelo vigente.</summary>
    Task<IReadOnlyList<PendingSemanticText>> ClaimPendingAsync(int batch, string modelId, int dimension, TimeSpan lease, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Grava o vetor; só se o texto continua o mesmo (o resumo confere) e ainda pendente.</summary>
    Task<bool> CompleteAsync(PendingSemanticText item, float[] embedding, string modelId, int dimension, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Registra a falha com o código curto; sem nova tentativa (<paramref name="nextAttempt"/> nulo) o texto fica <c>Failed</c>.</summary>
    Task FailAsync(PendingSemanticText item, string code, DateTimeOffset? nextAttempt, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Os textos mais parecidos do tenant, no idioma e (se pedido) só os elegíveis, pelo modelo vigente. Busca exata dentro do tenant, ordenada pela distância de cosseno.</summary>
    Task<IReadOnlyList<SemanticHit>> SearchAsync(TenantId tenant, float[] query, string locale, bool onlyAutoReply, string modelId, int dimension, int limit, CancellationToken cancellationToken);
}
