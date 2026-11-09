using IaBimeta.Agents.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Application;

/// <summary>
/// Uma mensagem de cliente que o FAQ não respondeu direto e que vale uma revisão (ADR 0030). Carrega o texto NORMALIZADO só em memória, para o registrador calcular o hash; o texto nunca é gravado. As referências
/// (atendimento, conversa e mensagem) são identificadores do Conversations.
/// </summary>
public sealed record KnowledgeGapCandidate(
    TenantId Tenant,
    Guid TurnId,
    Guid SessionId,
    Guid ConversationId,
    Guid MessageId,
    string NormalizedQuestion,
    KnowledgeGapKind Kind,
    DateTimeOffset At);

/// <summary>
/// Registra a ocorrência de uma lacuna. É MELHOR ESFORÇO: o turno de IA nunca falha nem espera por isto (o <see cref="TurnRunner"/> engole a falha do registrador), e o registro é idempotente por turno (o turno
/// repetido depois de uma queda não conta duas vezes). Sem chave de hash, ou com o registro desligado, não faz nada.
/// </summary>
public interface IKnowledgeGapRecorder
{
    Task RecordAsync(KnowledgeGapCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>Uma lacuna com quantas ocorrências ela tem (contadas, não guardadas: a contagem é exata por turno).</summary>
public sealed record KnowledgeGapRow(KnowledgeGap Gap, int Occurrences);

/// <summary>O armazenamento das lacunas do tenant do contexto (filtro global por tenant). A decisão do administrador passa por aqui; o registro das ocorrências, pelo <see cref="IKnowledgeGapRecorder"/>.</summary>
public interface IKnowledgeGapStore
{
    /// <summary>As lacunas, das mais frequentes para as menos (e das mais recentes), com a contagem de ocorrências.</summary>
    Task<IReadOnlyList<KnowledgeGapRow>> ListAsync(KnowledgeGapStatus? status, int take, int skip, CancellationToken cancellationToken);

    Task<int> CountAsync(KnowledgeGapStatus? status, CancellationToken cancellationToken);

    /// <summary>A lacuna (rastreada, para a decisão), ou nulo se não existe NESTE tenant.</summary>
    Task<KnowledgeGap?> FindAsync(Guid gapId, CancellationToken cancellationToken);

    Task<int> CountOccurrencesAsync(Guid gapId, CancellationToken cancellationToken);

    /// <summary>As ocorrências mais recentes da lacuna.</summary>
    Task<IReadOnlyList<KnowledgeGapOccurrence>> LatestOccurrencesAsync(Guid gapId, int take, CancellationToken cancellationToken);

    /// <summary>Grava a decisão. Decisão concorrente sobre a mesma lacuna: <see cref="KnowledgeGapConflictException"/>.</summary>
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Outra decisão (de outro administrador) ou outra alteração do rascunho aconteceu primeiro, ou a operação não cabe na situação atual: o código diz qual.</summary>
public sealed class KnowledgeGapConflictException : Exception
{
    public KnowledgeGapConflictException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary><c>gap-changed</c>, <c>gap-not-open</c>, <c>no-definition</c>, <c>faq-conflict</c>.</summary>
    public string Code { get; }
}
