using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.Conversations.Infrastructure;

public static class AttachmentRetentionExtensions
{
    /// <summary>
    /// Registra o expurgo do CONTEÚDO dos arquivos (ADR 0031 e 0032), pela mesma rotina de retenção técnica (comando <c>retention</c> e agendamento do Worker): o arquivo que o atendente ENVIOU (30 dias por padrão) e o que o
    /// cliente RECEBEU (sem prazo por padrão: <c>Retention:Days:InboundAttachments = 0</c>). Cada objeto apagado vai para a trilha de auditoria (<see cref="IRetentionAuditSink"/>, se o host o registrou).
    /// </summary>
    public static IServiceCollection AddAttachmentRetention(this IServiceCollection services)
    {
        services.AddSingleton<IRetentionTask>(provider => AttachmentObjectsRetentionTask.ForOutbound(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IAttachmentContentStore>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<IRetentionAuditSink>()));
        services.AddSingleton<IRetentionTask>(provider => AttachmentObjectsRetentionTask.ForInbound(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IAttachmentContentStore>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<IRetentionAuditSink>()));
        return services;
    }
}

/// <summary>
/// O expurgo do conteúdo dos arquivos de UM sentido (enviado ou recebido), pelo prazo da categoria correspondente (ADR 0031 e 0032): o arquivo disponível criado antes do corte tem o <b>objeto apagado</b> do armazenamento e a
/// linha passa a <c>Unavailable</c> com o motivo <c>retention</c> (sem chave, tipo, tamanho nem resumo; <b>a mensagem, o nome e a legenda ficam no histórico</b>). A tarefa dos arquivos ENVIADOS também recolhe o objeto antigo sem
/// NENHUMA linha que o referencie (uma gravação que não se completou). Prazo <c>0</c> (política não definida) nunca apaga nada: a tarefa só o informa. Idempotente e seguro para repetir: o objeto é apagado ANTES da linha mudar
/// (se o processo cair no meio, a próxima rodada conclui; no intervalo, abrir ou enviar o arquivo resulta em "indisponível"), e cada lote vai para a trilha de auditoria ANTES de apagado (nada é apagado sem registro; uma
/// falha da trilha interrompe o expurgo). A passada de uma empresa nunca toca a de outra.
/// </summary>
public sealed class AttachmentObjectsRetentionTask : IRetentionTask
{
    /// <summary>O nome da tarefa dos arquivos enviados: age sobre OBJETOS da pasta privada, não sobre uma tabela técnica (ver <c>RetentionInventory.ObjectStoreTasks</c>).</summary>
    public const string OutboundName = "conversations.outbound-attachment-objects";

    /// <summary>O nome da tarefa dos arquivos recebidos.</summary>
    public const string InboundName = "conversations.inbound-attachment-objects";

    /// <summary>A operação auditada de cada objeto apagado pelo prazo de guarda.</summary>
    public const string AuditOperation = "conversations.attachment.expired";

    /// <summary>A operação auditada de cada objeto órfão (sem linha) removido.</summary>
    public const string OrphanAuditOperation = "conversations.attachment.orphan-removed";

    private const int Batch = 200;

    private readonly IServiceScopeFactory _scopes;
    private readonly IAttachmentContentStore _content;
    private readonly TimeProvider _clock;
    private readonly IRetentionAuditSink? _audit;
    private readonly MessageDirection _direction;
    private readonly bool _sweepOrphans;

    private AttachmentObjectsRetentionTask(
        IServiceScopeFactory scopes, IAttachmentContentStore content, TimeProvider clock, IRetentionAuditSink? audit, MessageDirection direction, RetentionCategory category, string name, bool sweepOrphans)
    {
        _scopes = scopes;
        _content = content;
        _clock = clock;
        _audit = audit;
        _direction = direction;
        _sweepOrphans = sweepOrphans;
        Category = category;
        Name = name;
    }

    public string Name { get; }

    public RetentionCategory Category { get; }

    /// <summary>A tarefa dos arquivos que o atendente ENVIOU (e dos objetos órfãos antigos).</summary>
    public static AttachmentObjectsRetentionTask ForOutbound(IServiceScopeFactory scopes, IAttachmentContentStore content, TimeProvider clock, IRetentionAuditSink? audit = null) =>
        new(scopes, content, clock, audit, MessageDirection.Outbound, RetentionCategory.OutboundAttachments, OutboundName, sweepOrphans: true);

    /// <summary>A tarefa dos arquivos que o CLIENTE enviou (só roda com prazo definido).</summary>
    public static AttachmentObjectsRetentionTask ForInbound(IServiceScopeFactory scopes, IAttachmentContentStore content, TimeProvider clock, IRetentionAuditSink? audit = null) =>
        new(scopes, content, clock, audit, MessageDirection.Inbound, RetentionCategory.InboundAttachments, InboundName, sweepOrphans: false);

    public async Task<RetentionTaskResult> RunAsync(RetentionPass pass, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (pass.Days <= 0)
        {
            return new RetentionTaskResult(Name, Category, pass.Label, pass.Days, 0, 0, $"sem prazo definido (Retention:Days:{Category} = 0): nenhum arquivo é apagado");
        }

        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var corte = pass.Cutoff;
        var direcao = _direction;
        var proprio = pass.TenantId is { } guid ? new TenantId(guid) : null;
        var excluidos = pass.ExcludedTenants.Select(t => new TenantId(t)).ToList();

        IQueryable<MessageAttachment> Vencidos()
        {
            var vencidos = db.Attachments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Direction == direcao && a.Status == AttachmentStatus.Available && a.CreatedAt < corte);
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

                // A trilha de auditoria vem ANTES de apagar o lote (só identificadores): se ela não puder ser gravada, NADA é apagado e a próxima rodada tenta de novo. Se o apagar falhar depois, a próxima rodada repete o
                // lote e registra de novo (a trilha pode ter uma entrada repetida, nunca uma lacuna).
                if (_audit is not null)
                {
                    await _audit.RecordAsync([.. lote.Select(l => new RetentionAuditItem(l.TenantId.Value, AuditOperation, "attachment", l.Id.ToString()))], cancellationToken);
                }

                foreach (var linha in lote)
                {
                    if (linha.StorageKey is { } chave)
                    {
                        await _content.DeleteAsync(linha.TenantId, chave, cancellationToken);
                    }

                    var mudou = await db.Attachments.IgnoreQueryFilters()
                        .Where(a => a.TenantId == linha.TenantId && a.Id == linha.Id && a.Direction == direcao && a.Status == AttachmentStatus.Available)
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
                    apagados += mudou;
                }
            }
        }

        // Os objetos órfãos: antigos (mais que o prazo) e sem linha. Um objeto mais novo que o corte pode ser de uma gravação em andamento e nunca é tocado.
        if (_sweepOrphans)
        {
            var orfaos = new List<StoredAttachmentObject>();
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
                orfaos.Add(objeto);
            }

            // Também aqui a trilha vem antes: nenhum objeto é apagado sem o registro gravado.
            if (!dryRun && orfaos.Count > 0)
            {
                if (_audit is not null)
                {
                    await _audit.RecordAsync([.. orfaos.Select(o => new RetentionAuditItem(o.TenantId, OrphanAuditOperation, "attachment", o.AttachmentId.ToString()))], cancellationToken);
                }

                foreach (var objeto in orfaos)
                {
                    await _content.DeleteAsync(new TenantId(objeto.TenantId), objeto.Key, cancellationToken);
                    apagados++;
                }
            }
        }

        return new RetentionTaskResult(Name, Category, pass.Label, pass.Days, elegiveis, apagados);
    }
}
