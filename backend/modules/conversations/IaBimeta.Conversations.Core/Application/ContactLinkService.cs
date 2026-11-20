using System.Security.Cryptography;
using System.Text;
using IaBimeta.Audit.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>O resultado do vínculo pedido pelo servidor do sistema externo. <c>Resolved = false</c> cobre contato inexistente, fora da organização da conexão ou não resolvível, SEM distinguir os casos (sem oráculo).</summary>
public sealed record IntegrationLinkResult(bool Resolved, bool Shared);

/// <summary>
/// O vínculo entre o cliente do sistema externo e o contato (ADR 0041, seção 2; E2.2), por duas portas de entrada: o SERVIDOR do sistema externo (chamada assinada, contexto de integração) e o ATENDENTE em sessão embutida
/// (<see cref="IContactLinkCommands"/>). O vínculo dá descobribilidade, NUNCA visibilidade nem permissão: nada aqui amplia o que um usuário enxerga.
/// <para>
/// Regras comuns: tenant só do <see cref="RequestContext"/>; organização e conexão da integração vêm do autenticador (nunca do corpo); um vínculo ativo por (conexão, cliente, contato) garantido pelo banco; desvinculação
/// LÓGICA, idempotente e que nunca apaga linha, conversa nem consentimento; revincular insere linha nova. Toda criação e desvinculação grava a auditoria no outbox na MESMA transação do efeito, e só quando há efeito
/// (repetição sem mudança não gera auditoria). O telefone nunca vai à auditoria: só o identificador do vínculo, a conexão e um hash do cliente com sal do tenant.
/// </para>
/// </summary>
public sealed class ContactLinkService : IContactLinkCommands
{
    /// <summary>A origem do contexto que o host cria para as chamadas assinadas do sistema externo. As operações de integração exigem exatamente esta origem, e um contexto de sistema qualquer não basta.</summary>
    public const string IntegrationContextOrigin = "integration";

    private const int MaxAttempts = 3;
    private const string AuditResourceType = "contact_link";
    private const string LinkOperation = "embed.customer.link";
    private const string UnlinkOperation = "embed.customer.unlink";

    private readonly IContactLinkStore _store;
    private readonly IRequestContextAccessor _context;
    private readonly IEmbeddedContextQuery _embedded;
    private readonly TimeProvider _clock;

    public ContactLinkService(IContactLinkStore store, IRequestContextAccessor context, IEmbeddedContextQuery embedded, TimeProvider clock)
    {
        _store = store;
        _context = context;
        _embedded = embedded;
        _clock = clock;
    }

    // Servidor do sistema externo

    /// <summary>
    /// Vincula o cliente (<paramref name="customerRef"/>) ao contato do telefone, dentro da organização da conexão. Só resolve contato JÁ existente (nunca cria) e só com sessão na organização da conexão. Idempotente:
    /// repetir devolve o mesmo resultado, sem linha nova nem auditoria nova.
    /// </summary>
    /// <exception cref="ConversationsValidationException">Referência do cliente ou telefone inválidos (o telefone precisa do prefixo internacional).</exception>
    public async Task<IntegrationLinkResult> LinkByIntegrationAsync(Guid connectionId, Guid organizationId, string? customerRef, string? phone, CancellationToken cancellationToken = default)
    {
        var contexto = RequireIntegrationContext(connectionId, organizationId);
        var cliente = ContactExternalLink.ValidateCustomerRef(customerRef);
        var digitos = InternationalDigits(phone);

        return await RunAsync<IntegrationLinkResult>(
            async trabalho =>
            {
                var contatos = await trabalho.FindContactIdsByPhoneAsync(digitos, organizationId, cancellationToken);
                if (contatos.Count == 0)
                {
                    // Nada a gravar nem a travar; a resposta é a mesma de qualquer outro caso não resolvido.
                    return (new IntegrationLinkResult(false, false), (IReadOnlyCollection<EventEnvelope>)Array.Empty<EventEnvelope>());
                }

                var (compartilhado, eventos) = await ApplyLinksAsync(
                    trabalho, contexto, connectionId, organizationId, cliente, contatos, LinkOrigin.Integration, ActorKind.Integration, connectionId, cancellationToken);
                return (new IntegrationLinkResult(true, compartilhado), eventos);
            },
            cancellationToken);
    }

    /// <summary>
    /// Desvincula (lógico) os vínculos ATIVOS do cliente NESTA conexão: todos, ou só os do contato do telefone informado. Idempotente e sem oráculo: devolve normalmente existindo ou não vínculo ativo. A conexão só
    /// desfaz o que é dela.
    /// </summary>
    /// <exception cref="ConversationsValidationException">Referência do cliente ou telefone (quando informado) inválidos.</exception>
    public async Task UnlinkByIntegrationAsync(Guid connectionId, Guid organizationId, string? customerRef, string? phone, CancellationToken cancellationToken = default)
    {
        var contexto = RequireIntegrationContext(connectionId, organizationId);
        var cliente = ContactExternalLink.ValidateCustomerRef(customerRef);
        var digitos = phone is null ? null : InternationalDigits(phone);

        await RunAsync<bool>(
            async trabalho =>
            {
                IReadOnlyList<Guid>? contatos = null;
                if (digitos is not null)
                {
                    contatos = await trabalho.FindContactIdsByPhoneAsync(digitos, organizationId, cancellationToken);
                    if (contatos.Count == 0)
                    {
                        return (true, (IReadOnlyCollection<EventEnvelope>)Array.Empty<EventEnvelope>());
                    }
                }

                var eventos = await ApplyUnlinkAsync(trabalho, contexto, connectionId, cliente, contatos, ActorKind.Integration, connectionId, cancellationToken);
                return (true, eventos);
            },
            cancellationToken);
    }

    // Atendente em sessão embutida

    public async Task<AgentLinkResult?> LinkSessionContactAsync(Guid sessionId, string customerRef, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var cliente = ContactExternalLink.ValidateCustomerRef(customerRef);

        return await RunAsync<AgentLinkResult?>(
            async trabalho =>
            {
                var permitido = await AuthorizeAgentAsync(trabalho, contexto, sessionId, cliente, cancellationToken);
                if (permitido is not { } autorizado)
                {
                    return ((AgentLinkResult?)null, (IReadOnlyCollection<EventEnvelope>)Array.Empty<EventEnvelope>());
                }

                var (compartilhado, eventos) = await ApplyLinksAsync(
                    trabalho, contexto, autorizado.Connection, autorizado.Organization, cliente, new[] { autorizado.Contact }, LinkOrigin.Agent, ActorKind.User, contexto.Principal!.UserId, cancellationToken);
                return (new AgentLinkResult(compartilhado), eventos);
            },
            cancellationToken);
    }

    public async Task<bool> UnlinkSessionContactAsync(Guid sessionId, string customerRef, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var cliente = ContactExternalLink.ValidateCustomerRef(customerRef);

        return await RunAsync<bool>(
            async trabalho =>
            {
                var permitido = await AuthorizeAgentAsync(trabalho, contexto, sessionId, cliente, cancellationToken);
                if (permitido is not { } autorizado)
                {
                    return (false, (IReadOnlyCollection<EventEnvelope>)Array.Empty<EventEnvelope>());
                }

                var eventos = await ApplyUnlinkAsync(
                    trabalho, contexto, autorizado.Connection, cliente, new[] { autorizado.Contact }, ActorKind.User, contexto.Principal!.UserId, cancellationToken);
                return (true, eventos);
            },
            cancellationToken);
    }

    // Núcleo

    /// <summary>
    /// A decisão do atendente, na ordem do plano: sessão (404), permissão (403), contexto embutido e cliente autorizado na sessão (404). Devolve a conexão da SESSÃO EMBUTIDA, a organização e o contato da sessão de
    /// atendimento, ou <c>null</c>. O <c>customerRef</c> nunca vem do navegador sozinho: só vale se está entre os clientes em foco assinados pelo servidor do sistema externo.
    /// </summary>
    private async Task<(Guid Connection, Guid Organization, Guid Contact)?> AuthorizeAgentAsync(
        IContactLinkUnitOfWork trabalho, RequestContext contexto, Guid sessionId, string customerRef, CancellationToken cancellationToken)
    {
        // Só um usuário (sessão embutida) vincula: o contexto de sistema e o de integração não têm "atendente".
        if (contexto.Principal is null)
        {
            return null;
        }

        var sessao = await trabalho.FindSessionAsync(sessionId, cancellationToken);
        if (sessao is null || contexto.IsOutsideEmbeddedScope(sessao.OrganizationId))
        {
            return null;
        }

        contexto.Require(Permission.ConversationManage, sessao.OrganizationId);

        var embutida = await _embedded.GetCurrentAsync(cancellationToken);
        if (embutida is null
            || embutida.Mode != EmbeddedMode.FullAttendant
            || embutida.OrganizationId != sessao.OrganizationId
            || !await _embedded.IsCustomerGrantedAsync(customerRef, cancellationToken))
        {
            return null;
        }

        return (embutida.ConnectionId, sessao.OrganizationId, sessao.ContactId);
    }

    private async Task<(bool Shared, IReadOnlyCollection<EventEnvelope> Events)> ApplyLinksAsync(
        IContactLinkUnitOfWork trabalho,
        RequestContext contexto,
        Guid connectionId,
        Guid organizationId,
        string customerRef,
        IReadOnlyList<Guid> contactIds,
        LinkOrigin origin,
        ActorKind authorKind,
        Guid authorId,
        CancellationToken cancellationToken)
    {
        await trabalho.LockAsync(connectionId, customerRef, contactIds, cancellationToken);
        var doCliente = await trabalho.ListActiveByCustomerAsync(connectionId, customerRef, cancellationToken);
        var dosContatos = await trabalho.ListActiveByContactsAsync(connectionId, contactIds, cancellationToken);

        var agora = _clock.GetUtcNow();
        var eventos = new List<EventEnvelope>();
        foreach (var contato in contactIds)
        {
            // Já vinculado e ativo: nada a criar (idempotente).
            if (doCliente.Any(l => l.ContactId == contato))
            {
                continue;
            }

            var vinculo = ContactExternalLink.Create(contexto.TenantId, organizationId, connectionId, customerRef, contato, origin, authorKind, authorId, agora);
            trabalho.Add(vinculo);
            eventos.Add(AuditEvent(contexto, LinkOperation, vinculo, agora));
        }

        // Compartilhado: o contato já está vinculado a OUTRO cliente nesta conexão, ou o cliente já está vinculado a outro contato. Nunca se escolhe um por suposição; só se sinaliza.
        var compartilhado = dosContatos.Any(l => !string.Equals(l.CustomerRef, customerRef, StringComparison.Ordinal))
            || doCliente.Any(l => !contactIds.Contains(l.ContactId));
        return (compartilhado, eventos);
    }

    private async Task<IReadOnlyCollection<EventEnvelope>> ApplyUnlinkAsync(
        IContactLinkUnitOfWork trabalho,
        RequestContext contexto,
        Guid connectionId,
        string customerRef,
        IReadOnlyList<Guid>? contactIds,
        ActorKind byKind,
        Guid byId,
        CancellationToken cancellationToken)
    {
        await trabalho.LockAsync(connectionId, customerRef, contactIds ?? Array.Empty<Guid>(), cancellationToken);
        var ativos = await trabalho.ListActiveByCustomerAsync(connectionId, customerRef, cancellationToken);

        var agora = _clock.GetUtcNow();
        var eventos = new List<EventEnvelope>();
        foreach (var vinculo in ativos.Where(l => contactIds is null || contactIds.Contains(l.ContactId)))
        {
            if (vinculo.Unlink(byKind, byId, agora))
            {
                eventos.Add(AuditEvent(contexto, UnlinkOperation, vinculo, agora));
            }
        }

        return eventos;
    }

    /// <summary>
    /// Executa a operação numa unidade de trabalho e a confirma. Se outra transação gravou o mesmo vínculo ativo ao mesmo tempo (índice único), a operação recomeça do zero (relê e agora é idempotente); depois das tentativas,
    /// contenção persistente (nada foi gravado). Sem efeito a gravar, não há transação a confirmar.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<IContactLinkUnitOfWork, Task<(T Result, IReadOnlyCollection<EventEnvelope> Events)>> operation, CancellationToken cancellationToken)
    {
        for (var tentativa = 0; tentativa < MaxAttempts; tentativa++)
        {
            await using var trabalho = await _store.BeginAsync(cancellationToken);
            var (resultado, eventos) = await operation(trabalho);
            if (await trabalho.CommitAsync(eventos, cancellationToken))
            {
                return resultado;
            }
        }

        throw new ContactLinkContentionException();
    }

    private static EventEnvelope AuditEvent(RequestContext contexto, string operation, ContactExternalLink vinculo, DateTimeOffset agora)
    {
        // Sem telefone e sem o identificador do cliente em claro: o vínculo, a conexão e um hash do cliente com sal do tenant.
        var recurso = $"{vinculo.Id:D}:{vinculo.ConnectionId:N}:{CustomerHash(contexto.TenantId, vinculo.CustomerRef)}";
        var pedido = new ConversationAuditRequestedV1(operation, AuditResourceType, recurso, AuditOutcome.Success, contexto.Principal?.UserId, contexto.Origin, agora);
        return new EventEnvelope(Guid.CreateVersion7(), ConversationAuditEvents.AuditRequested, 1, contexto.TenantId, Guid.CreateVersion7(), null, $"link:{vinculo.Id:N}", agora, pedido);
    }

    /// <summary>O hash do identificador do cliente com o tenant como sal: 16 caracteres hexadecimais, estável por tenant e sem relação entre tenants.</summary>
    internal static string CustomerHash(TenantId tenantId, string customerRef) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId.Value:N}:{customerRef}")))[..16];

    private RequestContext RequireIntegrationContext(Guid connectionId, Guid organizationId)
    {
        var contexto = _context.GetRequired();

        // Só o contexto que o host cria para a chamada assinada (sem usuário, origem de integração) chama estas operações: um usuário de sessão nunca passa por aqui, nem um contexto de sistema de outra origem.
        if (contexto.Principal is not null || !string.Equals(contexto.Origin, IntegrationContextOrigin, StringComparison.Ordinal))
        {
            throw new AccessDeniedException(Permission.SystemOperation);
        }

        if (connectionId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new ConversationsValidationException("A chamada de integração precisa da conexão e da organização.");
        }

        return contexto;
    }

    /// <summary>Os dígitos do telefone no formato internacional ESTRITO (prefixo <c>+</c> ou <c>00</c> obrigatório), sem o <c>+</c>: a forma de comparar com a referência do contato.</summary>
    private static string InternationalDigits(string? phone) =>
        E164PhoneNumber.TryNormalizeInternational(phone, out var e164)
            ? e164[1..]
            : throw new ConversationsValidationException("O telefone deve estar no formato internacional (E.164), com o prefixo + e o código do país.");
}
