using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.Conversations.Infrastructure;

public static class AttachmentRetentionExtensions
{
    /// <summary>Registra o expurgo do CONTEÚDO dos arquivos que o atendente enviou (ADR 0031), pela mesma rotina de retenção técnica (comando <c>retention</c> e agendamento do Worker).</summary>
    public static IServiceCollection AddAttachmentRetention(this IServiceCollection services)
    {
        services.AddSingleton<IRetentionTask>(provider => new OutboundAttachmentRetentionTask(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IAttachmentContentStore>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}

/// <summary>
/// O expurgo do conteúdo dos arquivos ENVIADOS pelo atendente (ADR 0031), em duas partes e pelo prazo da categoria <see cref="RetentionCategory.OutboundAttachments"/>:
/// (1) o arquivo disponível criado antes do corte tem o objeto apagado do armazenamento e a linha passa a <c>Unavailable</c> com o motivo <c>retention</c> (a mensagem, o nome e a legenda ficam no histórico);
/// (2) o objeto antigo sem NENHUMA linha que o referencie (uma gravação que não se completou) é recolhido. O arquivo do cliente não é tocado: o objeto dele tem linha, e a linha dele nunca é a de um arquivo enviado.
/// Idempotente e seguro para repetir: o objeto é apagado ANTES da linha mudar (se o processo cair no meio, a próxima rodada conclui; no intervalo, abrir ou enviar o arquivo resulta em "indisponível").
/// </summary>
public sealed class OutboundAttachmentRetentionTask : IRetentionTask
{
    private const int Batch = 200;

    private readonly IServiceScopeFactory _scopes;
    private readonly IAttachmentContentStore _content;
    private readonly TimeProvider _clock;

    public OutboundAttachmentRetentionTask(IServiceScopeFactory scopes, IAttachmentContentStore content, TimeProvider clock)
    {
        _scopes = scopes;
        _content = content;
        _clock = clock;
    }

    public string Name => "conversations.message_attachments";

    public RetentionCategory Category => RetentionCategory.OutboundAttachments;

    public async Task<RetentionTaskResult> RunAsync(RetentionPass pass, bool dryRun, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var corte = pass.Cutoff;
        var proprio = pass.TenantId is { } guid ? new TenantId(guid) : null;
        var excluidos = pass.ExcludedTenants.Select(t => new TenantId(t)).ToList();

        IQueryable<MessageAttachment> Vencidos()
        {
            var vencidos = db.Attachments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Direction == MessageDirection.Outbound && a.Status == AttachmentStatus.Available && a.CreatedAt < corte);
            return proprio is not null ? vencidos.Where(a => a.TenantId == proprio) : vencidos.Where(a => !excluidos.Contains(a.TenantId));
        }

        var elegiveis = await Vencidos().CountAsync(cancellationToken);
        var apagados = 0;
        if (!dryRun)
        {
            while (true)
            {
                var lote = await Vencidos().OrderBy(a => a.CreatedAt).Take(Batch).Select(a => new { a.TenantId, a.Id, a.StorageKey }).ToListAsync(cancellationToken);
                if (lote.Count == 0)
                {
                    break;
                }

                var agora = _clock.GetUtcNow();
                foreach (var linha in lote)
                {
                    if (linha.StorageKey is { } chave)
                    {
                        await _content.DeleteAsync(linha.TenantId, chave, cancellationToken);
                    }

                    apagados += await db.Attachments.IgnoreQueryFilters()
                        .Where(a => a.TenantId == linha.TenantId && a.Id == linha.Id && a.Direction == MessageDirection.Outbound && a.Status == AttachmentStatus.Available)
                        .ExecuteUpdateAsync(
                            s => s
                                .SetProperty(a => a.Status, AttachmentStatus.Unavailable)
                                .SetProperty(a => a.FailureCode, MessageAttachment.RetentionFailureCode)
                                .SetProperty(a => a.MimeType, (string?)null)
                                .SetProperty(a => a.SizeBytes, (long?)null)
                                .SetProperty(a => a.Sha256, (string?)null)
                                .SetProperty(a => a.StorageKey, (string?)null)
                                .SetProperty(a => a.UpdatedAt, agora),
                            cancellationToken);
                }
            }
        }

        // Os objetos órfãos: antigos (mais que o prazo) e sem linha. Um objeto mais novo que o corte pode ser de uma gravação em andamento e nunca é tocado.
        await foreach (var objeto in _content.ListOlderThanAsync(corte, cancellationToken))
        {
            if (!pass.Includes(objeto.TenantId))
            {
                continue;
            }

            var tenant = new TenantId(objeto.TenantId);
            if (await db.Attachments.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tenant && a.Id == objeto.AttachmentId, cancellationToken))
            {
                continue;
            }

            elegiveis++;
            if (!dryRun)
            {
                await _content.DeleteAsync(tenant, objeto.Key, cancellationToken);
                apagados++;
            }
        }

        return new RetentionTaskResult(Name, Category, pass.Label, pass.Days, elegiveis, apagados);
    }
}
