using IaBimeta.Audit.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// Os pedidos de auditoria do consentimento (ADR 0041, seção 8), guardados no outbox na MESMA transação do efeito. Nenhum leva telefone, referência do cliente, evidência nem texto de mensagem: só o identificador da linha,
/// a organização e códigos curtos. O recurso da trilha tem até 100 caracteres, só letras, números e <c>_.:-</c>.
/// </summary>
public static class ConsentAuditEvents
{
    internal const string RecordOperation = "consent.record";
    internal const string KeywordRevokeOperation = "consent.keyword-revoke";
    internal const string PossibleOptOutOperation = "consent.possible-optout";
    internal const string ResolveOperation = "consent.possible-optout-resolve";

    private const string ConsentResourceType = "contact_consent";
    private const string ReviewResourceType = "optout_review";

    /// <summary>A chave da trava consultiva do telefone na organização: a MESMA para o registro, a revogação por palavra-chave e a resolução, para todas se serializarem.</summary>
    public static string PhoneLockKey(Guid tenant, Guid organizationId, string phone) => $"consent:{tenant:N}:{organizationId:N}:{phone}";

    /// <summary>A linha de consentimento gravada. O recurso é compacto (id 36 + organização 32 + 4): finalidade (U/M), situação (G/R) e origem (I/A/C) em uma letra cada.</summary>
    internal static EventEnvelope Record(RequestContext contexto, string operation, ContactConsent linha, DateTimeOffset agora)
    {
        var recurso = $"{linha.Id:D}:{linha.OrganizationId:N}:{linha.Purpose.ToString()[0]}{linha.Status.ToString()[0]}{linha.Origin.ToString()[0]}";
        return Build(contexto, operation, ConsentResourceType, recurso, $"consent:{linha.Id:N}", agora);
    }

    internal static EventEnvelope Resolve(RequestContext contexto, OptOutReview ocorrencia, DateTimeOffset agora) =>
        Build(contexto, ResolveOperation, ReviewResourceType, $"{ocorrencia.Id:D}:{ocorrencia.OrganizationId:N}:{ocorrencia.Resolution}", $"optout-review:{ocorrencia.Id:N}:resolve", agora);

    /// <summary>A abertura da ocorrência de possível saída: o identificador, a organização e o motivo (nunca o texto nem a palavra).</summary>
    internal static EventEnvelope Opened(RequestContext contexto, OptOutReview ocorrencia, DateTimeOffset agora) =>
        Build(contexto, PossibleOptOutOperation, ReviewResourceType, $"{ocorrencia.Id:D}:{ocorrencia.OrganizationId:N}:{ocorrencia.Reason}", $"optout-review:{ocorrencia.Id:N}:open", agora);

    private static EventEnvelope Build(RequestContext contexto, string operation, string resourceType, string resource, string orderingKey, DateTimeOffset agora)
    {
        var pedido = new ConversationAuditRequestedV1(operation, resourceType, resource, AuditOutcome.Success, contexto.Principal?.UserId, contexto.Origin, agora);
        return new EventEnvelope(Guid.CreateVersion7(), ConversationAuditEvents.AuditRequested, 1, contexto.TenantId, Guid.CreateVersion7(), null, orderingKey, agora, pedido);
    }
}
