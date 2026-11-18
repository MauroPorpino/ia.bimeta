using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// A entrega da auditoria do Embedded guardada na transação do efeito (<c>identity.audit_outbox</c>), no mesmo padrão do <c>DeferredAudit</c> do ContactCenter (ADR 0010, decisão C). Cada entrega tem a sua
/// transação: a linha é travada (<c>FOR UPDATE SKIP LOCKED</c>, então dois Workers nunca pegam a mesma), entregue à trilha e SÓ DEPOIS apagada; se o processo cai no meio, a linha volta. A entrega é pelo menos uma
/// vez, e a trilha a torna idempotente: o identificador da linha é a chave de idempotência do evento. Ator, origem e instante ORIGINAIS são gravados na entrega (<see cref="IAuditRecoveryWriter"/>). O tenant é o do
/// contexto confiável criado pelo Worker para o tenant da linha: nunca vem de um parâmetro.
/// <para>
/// ENTRADA PROBLEMÁTICA (E1-R, quinta rodada): se a trilha recusa uma linha, ela NÃO é descartada e NÃO trava as demais do tenant: a falha é registrada na própria linha (contagem, só o TIPO do erro, próxima tentativa com
/// recuo crescente, de 30 s até 1 h) dentro da mesma transação que a travou, e a entrega segue para a próxima elegível. A linha volta a ser tentada quando o recuo vence, para sempre, até a trilha aceitá-la.
/// </para>
/// </summary>
internal sealed class IdentityAuditOutbox : IIdentityAuditOutbox
{
    /// <summary>Recuo da primeira falha; dobra a cada falha seguinte até <see cref="MaxBackoff"/>.</summary>
    internal static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly IdentityDbContext _db;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditRecoveryWriter _audit;
    private readonly TimeProvider _clock;

    public IdentityAuditOutbox(IdentityDbContext db, IRequestContextAccessor context, IAuditRecoveryWriter audit, TimeProvider clock)
    {
        _db = db;
        _context = context;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>O recuo depois da <paramref name="attempts"/>-ésima falha (1 = primeira).</summary>
    internal static TimeSpan BackoffAfter(int attempts)
    {
        var expoente = Math.Clamp(attempts - 1, 0, 20);
        var ticks = Math.Min(BaseBackoff.Ticks * (1L << expoente), MaxBackoff.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithPendingAsync(int take, CancellationToken cancellationToken)
    {
        var agora = _clock.GetUtcNow();
        var tenants = await _db.AuditOutbox.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.NextAttemptAt == null || a.NextAttemptAt <= agora)
            .GroupBy(a => a.TenantId)
            .OrderBy(g => g.Min(a => a.CreatedAt))
            .Select(g => g.Key)
            .Take(take)
            .ToListAsync(cancellationToken);
        return tenants.Select(t => t.Value).ToList();
    }

    public async Task<AuditOutboxStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var linhas = _db.AuditOutbox.IgnoreQueryFilters().AsNoTracking();
        var pendentes = await linhas.CountAsync(cancellationToken);
        var comFalha = await linhas.CountAsync(a => a.Attempts > 0, cancellationToken);
        var maisTentativas = await linhas.MaxAsync(a => (int?)a.Attempts, cancellationToken) ?? 0;
        var maisAntiga = await linhas.MinAsync(a => (DateTimeOffset?)a.CreatedAt, cancellationToken);
        return new AuditOutboxStatus(pendentes, comFalha, maisTentativas, maisAntiga);
    }

    public async Task<int> FlushAsync(int take, CancellationToken cancellationToken)
    {
        // De dentro de uma transação aberta a transação ainda pode ser desfeita: entregar agora seria dizer à trilha o que pode não acontecer.
        if (_db.Database.CurrentTransaction is not null)
        {
            return 0;
        }

        var tenant = _context.GetRequired().TenantId;
        var entregues = 0;
        var falhas = 0;

        // Limita as tentativas do ciclo: uma trilha fora do ar não gira em falso, e a próxima volta é o próximo ciclo.
        while (entregues < take && falhas < take)
        {
            await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
            var agora = _clock.GetUtcNow();

            var id = await _db.Database
                .SqlQuery<Guid>(
                    $"SELECT id AS \"Value\" FROM identity.audit_outbox WHERE tenant_id = {tenant.Value} AND (next_attempt_at IS NULL OR next_attempt_at <= {agora}) ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);
            if (id.Count == 0)
            {
                return entregues;
            }

            var linha = await _db.AuditOutbox.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.TenantId == tenant && a.Id == id[0], cancellationToken);
            try
            {
                await _audit.WriteRecoveredAsync(linha.ToEntry(), linha.ToProvenance(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A trilha não aceitou agora: a linha FICA, com a falha registrada e o recuo crescente, e a entrega segue para as outras. O registro é na mesma transação que a travou (nenhum outro Worker a pega enquanto isso).
                var tentativas = linha.Attempts + 1;
                var tipo = ex.GetType().Name;
                var erro = tipo.Length > PendingAudit.LastErrorMaxLength ? tipo[..PendingAudit.LastErrorMaxLength] : tipo;
                var proxima = agora + BackoffAfter(tentativas);
                await _db.AuditOutbox.IgnoreQueryFilters()
                    .Where(a => a.TenantId == tenant && a.Id == linha.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(a => a.Attempts, tentativas).SetProperty(a => a.LastError, erro).SetProperty(a => a.NextAttemptAt, proxima), cancellationToken);
                await transacao.CommitAsync(cancellationToken);
                falhas++;
                continue;
            }

            await _db.AuditOutbox.IgnoreQueryFilters().Where(a => a.TenantId == tenant && a.Id == linha.Id).ExecuteDeleteAsync(cancellationToken);
            await transacao.CommitAsync(cancellationToken);
            entregues++;
        }

        return entregues;
    }
}
