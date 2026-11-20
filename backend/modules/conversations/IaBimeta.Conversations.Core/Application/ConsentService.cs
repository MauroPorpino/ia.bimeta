using IaBimeta.Audit.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// O registro do consentimento de contato (ADR 0040, seção 4; ADR 0041, seções 4 e 5; E2.4), por três portas: o SERVIDOR do sistema externo (chamada assinada, contexto de integração), o ATENDENTE com
/// <c>ConsentManage</c> (<see cref="IConsentCommands"/>) e a resolução de possível saída. Nenhum modelo de IA participa de nenhuma decisão.
/// <para>
/// Regras: somente acréscimo (mudar a situação insere linha; nada atualiza nem apaga); a chave é (tenant, ORGANIZAÇÃO, telefone, finalidade) e a organização é a da conexão ou a da sessão, nunca do corpo; o estado efetivo
/// é a última linha por <c>seq</c>; sem evidência, finalidade, origem e data da coleta a gravação é recusada; a VINCULAÇÃO não é consentimento e a AUSÊNCIA de linha não é autorização. As operações do mesmo telefone se
/// serializam (trava consultiva); repetir o mesmo registro é idempotente (nada gravado, nada auditado). A auditoria entra no outbox na MESMA transação do efeito e nunca leva telefone, cliente nem evidência.
/// </para>
/// <para>
/// <b>Registro defasado.</b> Um consentimento (<c>Granted</c>) cuja data da coleta NÃO é posterior à da revogação em vigor é recusado (<see cref="ConsentRecordOutcome.Superseded"/>): uma repetição atrasada ou fora de ordem
/// nunca reativa um consentimento revogado, e um novo consentimento de verdade (coletado depois da revogação) é aceito. A revogação nunca é recusada por data: o sentido seguro é sempre o de revogar.
/// </para>
/// </summary>
public sealed class ConsentService : IConsentCommands
{
    private static readonly ConsentPurpose[] AllPurposes = { ConsentPurpose.Utility, ConsentPurpose.Marketing };

    private readonly IConsentStore _store;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;

    public ConsentService(IConsentStore store, IRequestContextAccessor context, TimeProvider clock)
    {
        _store = store;
        _context = context;
        _clock = clock;
    }

    // Servidor do sistema externo

    /// <summary>
    /// Registra o consentimento (ou a revogação) do telefone na ORGANIZAÇÃO DA CONEXÃO. Não exige vínculo nem contato. Idempotente (<see cref="ConsentRecordOutcome.Unchanged"/>).
    /// </summary>
    /// <exception cref="ConversationsValidationException">Com o código do campo: <c>invalid-customer-ref</c>, <c>invalid-phone</c>, <c>evidence-required</c>, <c>evidence-too-long</c>, <c>collected-at-invalid</c>.</exception>
    public async Task<ConsentRecordResult> RecordByIntegrationAsync(
        Guid connectionId, Guid organizationId, string? customerRef, string? phone, ConsentPurpose purpose, ConsentStatus status, string? evidence, DateTimeOffset? collectedAt, CancellationToken cancellationToken = default)
    {
        var contexto = RequireIntegrationContext(connectionId, organizationId);
        var cliente = ValidateCustomerRef(customerRef);
        if (!E164PhoneNumber.TryNormalizeInternational(phone, out var telefone))
        {
            throw new ConversationsValidationException("invalid-phone", "O telefone deve estar no formato internacional (E.164), com o prefixo + e o código do país.");
        }

        var agora = _clock.GetUtcNow();
        var prova = ValidateEvidence(evidence);
        var coleta = ValidateCollectedAt(collectedAt, agora);

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var (resultado, eventos) = await ApplyAsync(
            trabalho, contexto, organizationId, telefone, purpose, status, prova, coleta, ConsentOrigin.IntegrationAsserted, ActorKind.Integration, connectionId, cliente, null, null, agora, cancellationToken);
        await trabalho.CommitAsync(eventos, cancellationToken);
        return resultado;
    }

    // Atendente com ConsentManage

    public async Task<ConsentRecordResult> RecordForSessionContactAsync(Guid sessionId, ConsentRecordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var contexto = RequireUser();
        var agora = _clock.GetUtcNow();

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.FindSessionAsync(sessionId, cancellationToken);
        if (sessao is null || contexto.IsOutsideEmbeddedScope(sessao.OrganizationId))
        {
            return new ConsentRecordResult(ConsentRecordOutcome.NotFound, null, null);
        }

        // ConsentManage sobre a organização da sessão: administrador e gestor. Toda sessão embutida cai aqui (a permissão não faz parte das concedidas ao Embedded).
        contexto.Require(Permission.ConsentManage, sessao.OrganizationId);

        // A recusa de campo vira resultado com o código estável (o atendente corrige o dado), nunca exceção que atravesse o módulo. Nada foi gravado: o descarte da unidade desfaz a transação.
        try
        {
            var prova = ValidateEvidence(request.Evidence);
            var coleta = ValidateCollectedAt(request.CollectedAt, agora);
            if (!E164PhoneNumber.TryNormalize(sessao.ContactRef, out var telefone))
            {
                return new ConsentRecordResult(ConsentRecordOutcome.PhoneUnavailable, null, null);
            }

            var (resultado, eventos) = await ApplyAsync(
                trabalho, contexto, sessao.OrganizationId, telefone, request.Purpose, request.Status, prova, coleta, ConsentOrigin.AgentRecorded, ActorKind.User, contexto.Principal!.UserId, null, sessionId, null, agora, cancellationToken);
            await trabalho.CommitAsync(eventos, cancellationToken);
            return resultado;
        }
        catch (ConversationsValidationException ex)
        {
            return new ConsentRecordResult(ConsentRecordOutcome.Invalid, null, null, ex.Code ?? "invalid-request");
        }
    }

    public async Task<OptOutResolveResult> ResolveOptOutReviewAsync(Guid sessionId, Guid messageId, OptOutResolution resolution, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(resolution))
        {
            throw new ConversationsValidationException("invalid-resolution", "A decisão deve ser Revoked ou Kept.");
        }

        var contexto = RequireUser();
        var agora = _clock.GetUtcNow();

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.FindSessionAsync(sessionId, cancellationToken);
        if (sessao is null || contexto.IsOutsideEmbeddedScope(sessao.OrganizationId))
        {
            return new OptOutResolveResult(OptOutResolveOutcome.NotFound, null);
        }

        contexto.Require(Permission.ConsentManage, sessao.OrganizationId);

        // A ocorrência primeiro, o telefone depois (a ordem fixa das travas). A leitura da ocorrência vem DEPOIS da trava: a segunda decisão concorrente a enxerga resolvida.
        await trabalho.LockReviewAsync(messageId, cancellationToken);
        var ocorrencia = await trabalho.FindReviewAsync(sessionId, messageId, cancellationToken);
        if (ocorrencia is null || ocorrencia.OrganizationId != sessao.OrganizationId)
        {
            return new OptOutResolveResult(OptOutResolveOutcome.NotFound, null);
        }

        if (ocorrencia.Resolution is { } anterior)
        {
            // Nunca resolvida duas vezes: a mesma decisão é idempotente (sem efeito, sem auditoria); outra decisão é conflito e nada muda.
            return new OptOutResolveResult(anterior == resolution ? OptOutResolveOutcome.AlreadyResolved : OptOutResolveOutcome.Conflict, anterior);
        }

        var eventos = new List<EventEnvelope>();
        if (resolution == OptOutResolution.Revoked)
        {
            if (!E164PhoneNumber.TryNormalize(sessao.ContactRef, out var telefone))
            {
                return new OptOutResolveResult(OptOutResolveOutcome.PhoneUnavailable, null);
            }

            await trabalho.LockPhoneAsync(sessao.OrganizationId, telefone, cancellationToken);
            foreach (var finalidade in AllPurposes)
            {
                var ultima = await trabalho.GetLatestAsync(sessao.OrganizationId, telefone, finalidade, cancellationToken);
                if (ultima is { Status: ConsentStatus.Revoked })
                {
                    continue; // já revogada: nada a acrescentar
                }

                // A evidência é a referência da ocorrência e o motivo, nunca o texto livre da mensagem. A coleta é o instante da própria ocorrência.
                var prova = $"optout-review:{ocorrencia.Id:N}:message:{ocorrencia.MessageId:N}:reason:{ocorrencia.Reason}" + (ocorrencia.Keyword is null ? string.Empty : $":keyword:{ocorrencia.Keyword}");
                var coleta = ocorrencia.OpenedAt > agora ? agora : ocorrencia.OpenedAt;
                var linha = ContactConsent.Create(
                    contexto.TenantId, sessao.OrganizationId, telefone, finalidade, ConsentStatus.Revoked, ConsentOrigin.AgentRecorded, prova, coleta, ActorKind.User, contexto.Principal!.UserId, null, sessionId, messageId, agora);
                trabalho.Add(linha);
                eventos.Add(ConsentAuditEvents.Record(contexto, ConsentAuditEvents.RecordOperation, linha, agora));
            }
        }

        ocorrencia.Resolve(resolution, contexto.Principal!.UserId, agora);
        eventos.Add(ConsentAuditEvents.Resolve(contexto, ocorrencia, agora));
        await trabalho.CommitAsync(eventos, cancellationToken);
        return new OptOutResolveResult(OptOutResolveOutcome.Resolved, resolution);
    }

    // Núcleo

    private async Task<(ConsentRecordResult Result, IReadOnlyCollection<EventEnvelope> Events)> ApplyAsync(
        IConsentUnitOfWork trabalho,
        RequestContext contexto,
        Guid organizationId,
        string phone,
        ConsentPurpose purpose,
        ConsentStatus status,
        string evidence,
        DateTimeOffset collectedAt,
        ConsentOrigin origin,
        ActorKind actorKind,
        Guid actorId,
        string? customerRef,
        Guid? sessionId,
        Guid? messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(purpose))
        {
            throw new ConversationsValidationException("invalid-purpose", "A finalidade deve ser Utility ou Marketing.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ConversationsValidationException("invalid-status", "A situação deve ser Granted ou Revoked.");
        }

        // Trava o telefone na organização e SÓ ENTÃO lê a última linha: duas gravações concorrentes se serializam e cada uma decide sobre o estado que a outra deixou.
        await trabalho.LockPhoneAsync(organizationId, phone, cancellationToken);
        var ultima = await trabalho.GetLatestAsync(organizationId, phone, purpose, cancellationToken);
        if (ultima is not null)
        {
            if (ultima.IsSameAssertionAs(status, collectedAt))
            {
                return (new ConsentRecordResult(ConsentRecordOutcome.Unchanged, purpose, ultima.Status), Array.Empty<EventEnvelope>());
            }

        }

        // Uma concessão é comparada com a MAIOR coleta de revogação da chave (sob a mesma trava), não só com a última linha: uma revogação atrasada (coleta antiga) nunca reduz a referência.
        if (status == ConsentStatus.Granted && await trabalho.GetLatestRevocationCollectedAtAsync(organizationId, phone, purpose, cancellationToken) is { } maiorRevogacao && collectedAt <= maiorRevogacao)
        {
            return (new ConsentRecordResult(ConsentRecordOutcome.Superseded, purpose, ultima?.Status ?? ConsentStatus.Revoked), Array.Empty<EventEnvelope>());
        }

        var linha = ContactConsent.Create(contexto.TenantId, organizationId, phone, purpose, status, origin, evidence, collectedAt, actorKind, actorId, customerRef, sessionId, messageId, now);
        trabalho.Add(linha);
        return (new ConsentRecordResult(ConsentRecordOutcome.Recorded, purpose, status), new[] { ConsentAuditEvents.Record(contexto, ConsentAuditEvents.RecordOperation, linha, now) });
    }

    private static string ValidateCustomerRef(string? customerRef)
    {
        try
        {
            return ContactExternalLink.ValidateCustomerRef(customerRef);
        }
        catch (ConversationsValidationException ex)
        {
            throw new ConversationsValidationException("invalid-customer-ref", ex.Message);
        }
    }

    private static string ValidateEvidence(string? evidence)
    {
        var prova = (evidence ?? string.Empty).Trim();
        if (prova.Length == 0)
        {
            throw new ConversationsValidationException("evidence-required", "A evidência do consentimento é obrigatória.");
        }

        if (prova.Length > ContactConsent.EvidenceMaxLength)
        {
            throw new ConversationsValidationException("evidence-too-long", $"A evidência deve ter até {ContactConsent.EvidenceMaxLength} caracteres.");
        }

        return prova;
    }

    /// <summary>A data da coleta é obrigatória e não pode estar no futuro (tolerância de 5 minutos). Normalizada para UTC e microssegundos (a precisão do banco), para a comparação de repetição ser exata.</summary>
    private static DateTimeOffset ValidateCollectedAt(DateTimeOffset? collectedAt, DateTimeOffset now)
    {
        if (collectedAt is not { } valor || valor == default || valor > now + ContactConsent.FutureTolerance)
        {
            throw new ConversationsValidationException("collected-at-invalid", "A data da coleta é obrigatória e não pode estar no futuro.");
        }

        var utc = valor.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    private RequestContext RequireUser()
    {
        var contexto = _context.GetRequired();

        // Só um usuário (sessão interativa ou embutida) chega aqui: o contexto de sistema e o de integração não têm "atendente".
        if (contexto.Principal is null)
        {
            throw new AccessDeniedException(Permission.ConsentManage);
        }

        return contexto;
    }

    private RequestContext RequireIntegrationContext(Guid connectionId, Guid organizationId)
    {
        var contexto = _context.GetRequired();

        // Só o contexto que o host cria para a chamada assinada (sem usuário, origem de integração) chama a operação de integração.
        if (contexto.Principal is not null || !string.Equals(contexto.Origin, ContactLinkService.IntegrationContextOrigin, StringComparison.Ordinal))
        {
            throw new AccessDeniedException(Permission.SystemOperation);
        }

        if (connectionId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new ConversationsValidationException("A chamada de integração precisa da conexão e da organização.");
        }

        return contexto;
    }
}
