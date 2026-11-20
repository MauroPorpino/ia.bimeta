using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Uma ocorrência de possível saída do cliente (<c>PossibleOptOut</c>) que espera a decisão de uma pessoa com <c>ConsentManage</c> (ADR 0041, seção 5; E2.4). É a ESTRUTURA de revisão e resolução: quem a abre é a
/// ingestão (reconhecimento por palavra-chave, E2.5); aqui está só o ciclo de vida. Uma ocorrência por mensagem (chave única do banco) e RESOLVIDA UMA ÚNICA VEZ: depois de resolvida a linha não muda mais (o banco recusa
/// a segunda atualização e qualquer exclusão), então a mesma ocorrência nunca tem dois efeitos conflitantes. Não guarda o texto livre da mensagem: só o motivo e a palavra normalizada (quando houve).
/// </summary>
public sealed class OptOutReview : ITenantOwned
{
    public const int KeywordMaxLength = 32;

    private OptOutReview()
    {
        TenantId = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid MessageId { get; private set; }

    public OptOutReviewReason Reason { get; private set; }

    /// <summary>A palavra normalizada (PARAR, SAIR, CANCELAR) quando houve; nunca o texto livre.</summary>
    public string? Keyword { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public OptOutResolution? Resolution { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public bool IsResolved => Resolution is not null;

    public static OptOutReview Open(TenantId tenantId, Guid organizationId, Guid sessionId, Guid messageId, OptOutReviewReason reason, string? keyword, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (organizationId == Guid.Empty || sessionId == Guid.Empty || messageId == Guid.Empty || !Enum.IsDefined(reason))
        {
            throw new ConversationsValidationException("A ocorrência de possível saída precisa de organização, sessão, mensagem e motivo.");
        }

        var palavra = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim().ToUpperInvariant();
        if (palavra is { Length: > KeywordMaxLength } || palavra?.Any(char.IsControl) == true)
        {
            throw new ConversationsValidationException("A palavra da ocorrência é inválida.");
        }

        return new OptOutReview
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrganizationId = organizationId,
            SessionId = sessionId,
            MessageId = messageId,
            Reason = reason,
            Keyword = palavra,
            OpenedAt = now,
        };
    }

    /// <summary>Resolve a ocorrência, uma única vez. <c>false</c> = já estava resolvida (a linha não muda).</summary>
    public bool Resolve(OptOutResolution resolution, Guid userId, DateTimeOffset now)
    {
        if (!Enum.IsDefined(resolution) || userId == Guid.Empty)
        {
            throw new ConversationsValidationException("A decisão da ocorrência precisa de uma resolução e do responsável.");
        }

        if (IsResolved)
        {
            return false;
        }

        Resolution = resolution;
        ResolvedAt = now;
        ResolvedBy = userId;
        return true;
    }
}
