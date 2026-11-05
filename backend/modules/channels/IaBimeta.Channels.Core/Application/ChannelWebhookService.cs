using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

public enum WebhookReceiveOutcome
{
    /// <summary>Autenticado e gravado no Inbox (inclusive o lote sem nada novo): responde 200.</summary>
    Accepted = 1,

    /// <summary>Assinatura ausente, inválida ou sem segredo para conferir: nada foi interpretado nem gravado (401).</summary>
    InvalidSignature = 2,

    /// <summary>O corpo autenticado não é um webhook deste provedor (400): nada foi gravado.</summary>
    InvalidPayload = 3,

    /// <summary>A conta não tem adaptador para o provedor (503): nada foi gravado.</summary>
    ProviderUnavailable = 4,
}

/// <param name="Stored">Eventos novos gravados no Inbox.</param>
/// <param name="Duplicates">Eventos repetidos (já estavam no Inbox).</param>
/// <param name="Discarded">Eventos descartados: número desconhecido, de outra conta ou de outro tenant, ou canal inativo (REC-3).</param>
public sealed record WebhookReceiveResult(WebhookReceiveOutcome Outcome, int Stored = 0, int Duplicates = 0, int Discarded = 0);

/// <summary>
/// O RECEIVER do Channels (ADR 0011, seção C; BOUNDARIES.md §12.2, REC-1 a REC-6): do corpo bruto de um webhook ao Inbox durável, SEM chamar outro módulo, a IA nem o provedor (ARQ-19). A ordem importa:
/// (1) o identificador opaco da URL leva à conta (<see cref="ResolveAsync"/>); (2) a assinatura é verificada sobre os BYTES do corpo, em tempo constante, com o segredo do aplicativo da conta, ANTES de interpretar
/// o conteúdo; (3) só então o adaptador normaliza o corpo; (4) cada evento é conferido contra a conta da URL (número desconhecido, de outra conta ou de outro tenant é descartado e contado, REC-3) e contra a
/// situação do canal (só <c>Active</c> grava); (5) os eventos novos vão ao Inbox numa transação, e só depois do commit o host responde 200. O tenant vem do banco, pela URL: nunca do conteúdo do webhook. O
/// host cria o contexto de tenant (TEN-1) entre o passo 1 e os demais.
/// </summary>
public sealed class ChannelWebhookService
{
    private readonly IChannelWebhookStore _webhooks;
    private readonly IChannelRepository _repository;
    private readonly ISecretProtector _protector;
    private readonly ChannelProviderAdapters _adapters;
    private readonly IInboundEventWriter _writer;
    private readonly TimeProvider _clock;

    public ChannelWebhookService(
        IChannelWebhookStore webhooks, IChannelRepository repository, ISecretProtector protector, ChannelProviderAdapters adapters, IInboundEventWriter writer, TimeProvider clock)
    {
        _webhooks = webhooks;
        _repository = repository;
        _protector = protector;
        _adapters = adapters;
        _writer = writer;
        _clock = clock;
    }

    /// <summary>O passo 1: o identificador da URL leva à conta. Nulo (identificador desconhecido) responde 404, sem detalhe.</summary>
    public Task<WebhookTarget?> ResolveAsync(Guid webhookId, CancellationToken cancellationToken) =>
        webhookId == Guid.Empty ? Task.FromResult<WebhookTarget?>(null) : _webhooks.ResolveAsync(webhookId, cancellationToken);

    /// <summary>
    /// O pedido de verificação do webhook (GET com desafio): devolve o texto do desafio se o modo e o token conferem com o <c>WebhookVerifyToken</c> da conta, ou nulo (o host responde 403). Exige o contexto de
    /// tenant da conta.
    /// </summary>
    public async Task<string?> AnswerVerificationAsync(WebhookTarget target, string? mode, string? verifyToken, string? challenge, CancellationToken cancellationToken)
    {
        var adaptador = _adapters.Find(target.Provider);
        var esperado = await ReadSecretAsync(target, ChannelSecretKind.WebhookVerifyToken, cancellationToken);
        if (adaptador is null || esperado is null)
        {
            return null;
        }

        return adaptador.TryAnswerVerification(mode, verifyToken, challenge, esperado, out var resposta) ? resposta : null;
    }

    /// <summary>Os passos 2 a 5. Exige o contexto de tenant da conta.</summary>
    public async Task<WebhookReceiveResult> ReceiveAsync(WebhookTarget target, ReadOnlyMemory<byte> body, string? signature, CancellationToken cancellationToken)
    {
        var adaptador = _adapters.Find(target.Provider);
        if (adaptador is null)
        {
            return new WebhookReceiveResult(WebhookReceiveOutcome.ProviderUnavailable);
        }

        // 2. Autenticidade ANTES de qualquer interpretação do conteúdo. Sem segredo configurado não há como autenticar: a mesma resposta de uma assinatura inválida (nunca revela o que falta).
        var segredo = await ReadSecretAsync(target, ChannelSecretKind.AppSecret, cancellationToken);
        if (segredo is null || !adaptador.VerifySignature(body.Span, signature, segredo))
        {
            ChannelsMetrics.WebhookRejected.Add(1);
            return new WebhookReceiveResult(WebhookReceiveOutcome.InvalidSignature);
        }

        // 3. Normalização (só agora o conteúdo é interpretado).
        var webhook = adaptador.Parse(body.Span);
        if (webhook is null)
        {
            ChannelsMetrics.WebhookRejected.Add(1);
            return new WebhookReceiveResult(WebhookReceiveOutcome.InvalidPayload);
        }

        // 4. Conferência cruzada de cada evento contra a conta da URL e a situação do canal.
        var agora = _clock.GetUtcNow();
        var instancias = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var registros = new List<InboundEventRecord>();
        var descartados = webhook.IgnoredEntries;

        foreach (var mensagem in webhook.Messages)
        {
            if (await ResolveInstanceAsync(target, mensagem.ExternalAccountId, instancias, cancellationToken) is not { } instancia)
            {
                descartados++;
                continue;
            }

            registros.Add(new InboundEventRecord(
                DeterministicGuid.From($"{target.AccountId:N}|message|{mensagem.ProviderMessageId}"),
                ChannelEventTypes.ProviderMessageReceived,
                $"{instancia:N}:{mensagem.ContactExternalRef}",
                new ProviderMessageReceivedV1(
                    instancia, mensagem.ProviderMessageId, mensagem.ContactExternalRef, mensagem.ContactDisplayName, mensagem.ProviderTimestamp, mensagem.Kind, mensagem.Text, mensagem.ChannelData),
                agora));
        }

        foreach (var status in webhook.Statuses)
        {
            if (await ResolveInstanceAsync(target, status.ExternalAccountId, instancias, cancellationToken) is not { } instancia)
            {
                descartados++;
                continue;
            }

            registros.Add(new InboundEventRecord(
                DeterministicGuid.From($"{target.AccountId:N}|status|{status.ProviderMessageId}|{status.Status}|{status.ProviderTimestamp.ToUnixTimeSeconds()}"),
                ChannelEventTypes.ProviderStatusReceived,
                $"{instancia:N}:s:{status.ProviderMessageId}",
                new ProviderStatusReceivedV1(instancia, status.ProviderMessageId, status.Status, status.ProviderTimestamp, status.OpaqueCallbackData, status.ErrorCode),
                agora));
        }

        // 5. O Inbox durável, numa transação. Uma falha aqui é 5xx para o host (nunca 200): o provedor reenvia.
        var novos = registros.Count == 0 ? 0 : await _writer.WriteAsync(registros, cancellationToken);
        var repetidos = registros.Count - novos;
        ChannelsMetrics.WebhookStored.Add(novos);
        ChannelsMetrics.WebhookDuplicate.Add(repetidos);
        ChannelsMetrics.WebhookDiscarded.Add(descartados);
        return new WebhookReceiveResult(WebhookReceiveOutcome.Accepted, novos, repetidos, descartados);
    }

    /// <summary>
    /// A instância do evento, ou nulo se o evento deve ser descartado: número sem rota, rota de outro tenant, instância de OUTRA conta que a da URL ou canal que não está <c>Active</c>. Resolvido uma vez por número no lote.
    /// </summary>
    private async Task<Guid?> ResolveInstanceAsync(WebhookTarget target, string externalAccountId, Dictionary<string, Guid?> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(externalAccountId, out var existente))
        {
            return existente;
        }

        Guid? instanciaId = null;
        var rota = await _repository.ResolveRouteAsync(target.Provider, externalAccountId, cancellationToken);
        if (rota is not null && rota.TenantId == target.TenantId && rota.Status == ChannelStatus.Active)
        {
            var instancia = await _repository.FindInstanceReadOnlyAsync(rota.ChannelInstanceId, cancellationToken);
            if (instancia is not null && instancia.AccountId == target.AccountId && instancia.Status == ChannelStatus.Active)
            {
                instanciaId = instancia.Id.Value;
            }
        }

        cache[externalAccountId] = instanciaId;
        return instanciaId;
    }

    // Chave mestra ausente ou texto adulterado: nulo (falha fechada), sem repetir nada do que falhou.
    private Task<string?> ReadSecretAsync(WebhookTarget target, ChannelSecretKind kind, CancellationToken cancellationToken) =>
        ChannelSecretReader.ReadAsync(_repository, _protector, target.TenantId, target.AccountId, kind, cancellationToken);
}
