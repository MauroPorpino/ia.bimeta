using IaBimeta.Agents.Domain;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Application;

/// <summary>O armazenamento da configuração semântica do tenant do contexto. A entidade volta RASTREADA para alteração.</summary>
public interface ISemanticAnswerSettingsStore
{
    Task<SemanticAnswerSettings?> FindAsync(CancellationToken cancellationToken);

    void Add(SemanticAnswerSettings settings);

    /// <exception cref="AgentDefinitionConflictException">Outra alteração aconteceu primeiro.</exception>
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Grava a decisão semântica de um turno (idempotente por turno). Melhor esforço para quem chama: o turno nunca falha por causa dela.</summary>
public interface ISemanticDecisionRecorder
{
    Task RecordAsync(SemanticDecisionRecord record, CancellationToken cancellationToken);
}

/// <summary>A configuração semântica como o administrador a vê.</summary>
public sealed record SemanticAnswerSettingsView(SemanticAnswerMode Mode, double MinScore, double MinMargin, DateTimeOffset? UpdatedAt, bool IsDefault);

/// <summary>
/// Os casos de uso da configuração da resposta por similaridade (ADR 0034): só quem tem <see cref="Permission.TenantSettingsManage"/> lê e altera; toda alteração é auditada SEM conteúdo (só o modo e os limiares).
/// Mudar o modo para <see cref="SemanticAnswerMode.Off"/> é o interruptor de emergência do tenant: vale no próximo turno, sem publicar nada.
/// </summary>
public sealed class SemanticAnswerSettingsService
{
    private const string Resource = "agents.semantic-settings";

    private readonly IRequestContextAccessor _context;
    private readonly ISemanticAnswerSettingsStore _store;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;

    public SemanticAnswerSettingsService(IRequestContextAccessor context, ISemanticAnswerSettingsStore store, IAuditWriter audit, TimeProvider clock)
    {
        _context = context;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    public async Task<SemanticAnswerSettingsView> GetAsync(CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        var atual = await _store.FindAsync(cancellationToken);
        return atual is null
            ? new SemanticAnswerSettingsView(SemanticAnswerMode.Off, SemanticAnswerSettings.DefaultMinScore, SemanticAnswerSettings.DefaultMinMargin, null, true)
            : ToView(atual);
    }

    /// <exception cref="AgentsValidationException">Limiar ou modo inválido.</exception>
    /// <exception cref="AgentDefinitionConflictException">Outra alteração aconteceu primeiro.</exception>
    public async Task<SemanticAnswerSettingsView> UpdateAsync(SemanticAnswerMode mode, double minScore, double minMargin, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);
        var agora = _clock.GetUtcNow();
        var atual = await _store.FindAsync(cancellationToken);
        if (atual is null)
        {
            atual = SemanticAnswerSettings.Default(contexto.TenantId, agora);
            _store.Add(atual);
        }

        var anterior = atual.Mode;
        atual.Apply(mode, minScore, minMargin, agora, contexto.Principal?.UserId);
        await _store.SaveAsync(cancellationToken);

        // A auditoria guarda o que mudou de relevante sem conteúdo: o modo novo vai na própria operação (os limiares estão na configuração atual).
        var operacao = anterior == mode ? "agents.semantic-settings.thresholds-updated" : $"agents.semantic-settings.mode-{mode.ToString().ToLowerInvariant()}";
        await _audit.WriteAsync(new AuditEntry(operacao, Resource, contexto.TenantId.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(atual);
    }

    private static SemanticAnswerSettingsView ToView(SemanticAnswerSettings s) => new(s.Mode, s.MinScore, s.MinMargin, s.UpdatedAt, false);
}
