namespace IaBimeta.Conversations.Contracts.Commands;

/// <summary>O resultado de um vínculo feito pelo atendente. <see cref="Shared"/>: o contato (ou o cliente) já está vinculado a outro, e o sistema nunca escolhe um por suposição (ADR 0041, seção 2).</summary>
public sealed record AgentLinkResult(bool Shared);

/// <summary>
/// O vínculo entre o cliente do sistema externo e o contato, feito pelo ATENDENTE em sessão embutida (ADR 0041, seções 2 e 5; E2.2). Quem decide o que é permitido é o Conversations: o usuário vem do <c>RequestContext</c>
/// (nunca de parâmetro), a conexão e os clientes em foco vêm da sessão embutida (Identity) e o contato é o da sessão de atendimento. Um vínculo dá descobribilidade, nunca visibilidade nem permissão.
/// <para>
/// Devolve <c>null</c> (vínculo) ou <c>false</c> (desvínculo) para QUALQUER recusa que não seja de permissão: sessão fora do escopo, contexto que não é de sessão embutida ou cliente que a sessão não autorizou. A mesma
/// resposta para todos os casos, sem oráculo (TEN-2). A falta da permissão <c>ConversationManage</c> é <see cref="IaBimeta.SharedKernel.AccessDeniedException"/>.
/// </para>
/// </summary>
public interface IContactLinkCommands
{
    Task<AgentLinkResult?> LinkSessionContactAsync(Guid sessionId, string customerRef, CancellationToken cancellationToken = default);

    /// <summary>Desvincula (lógico, idempotente) o cliente do contato da sessão. <c>true</c> = permitido e aplicado (existindo ou não vínculo ativo); <c>false</c> = recusado como não encontrado.</summary>
    Task<bool> UnlinkSessionContactAsync(Guid sessionId, string customerRef, CancellationToken cancellationToken = default);
}

/// <summary>Os limites do vínculo que os módulos consumidores precisam conhecer para validar antes de chamar (a regra única, no Conversations).</summary>
public static class ContactLinkLimits
{
    /// <summary>O tamanho máximo do identificador do cliente no sistema externo.</summary>
    public const int CustomerRefMaxLength = 128;
}
