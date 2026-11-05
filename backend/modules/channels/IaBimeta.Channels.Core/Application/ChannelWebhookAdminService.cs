using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>O endereço do webhook de uma conta. O identificador é um segredo operacional: só o administrador da conta o vê, e nunca vai a log.</summary>
/// <param name="WebhookId">O identificador opaco da URL.</param>
/// <param name="Path">O caminho a cadastrar no provedor (a URL pública do ambiente é o prefixo, e o override de webhook por conta da Meta o recebe).</param>
public sealed record ChannelWebhookView(Guid WebhookId, string Path);

/// <summary>
/// Entrega (ou cria, de forma idempotente) o identificador opaco do webhook de uma conta (ADR 0011, A.1, item 2). Exige a permissão de gerir canais da organização da conta; conta de outra organização ou de outro
/// tenant responde como inexistente (TEN-2). A criação é auditada SEM o identificador.
/// </summary>
public sealed class ChannelWebhookAdminService
{
    private const string Resource = "channel-account";
    private const string WhatsAppPathPrefix = "/webhooks/whatsapp/";

    private readonly IChannelRepository _repository;
    private readonly IChannelWebhookStore _webhooks;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditWriter _audit;

    public ChannelWebhookAdminService(IChannelRepository repository, IChannelWebhookStore webhooks, IRequestContextAccessor context, IAuditWriter audit)
    {
        _repository = repository;
        _webhooks = webhooks;
        _context = context;
        _audit = audit;
    }

    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    public async Task<ChannelWebhookView> EnsureAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var conta = await _repository.FindAccountAsync(accountId, cancellationToken);
        if (conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            throw new ChannelsResourceNotFoundException("Conta");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.ChannelManage, conta.OrganizationId, "channels.webhook.ensure", Resource, conta.Id.ToString(), cancellationToken);

        var id = await _webhooks.EnsureAsync(conta.Id, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("channels.webhook.ensure", Resource, conta.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new ChannelWebhookView(id, WhatsAppPathPrefix + id.ToString("D"));
    }
}
