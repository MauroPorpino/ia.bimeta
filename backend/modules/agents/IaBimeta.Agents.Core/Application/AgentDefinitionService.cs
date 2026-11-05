using IaBimeta.Agents.Domain;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Application;

/// <summary>Uma versão da definição do agente, como o tenant a vê.</summary>
public sealed record AgentDefinitionView(int Number, AgentDefinitionStatus Status, AgentDefinitionContent Content, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

/// <summary>
/// Os casos de uso da definição do agente do tenant (ADR 0017, seção F): instruções, FAQ, resposta informativa de áudio e aviso de transferência, com rascunho e publicação versionada. O tenant e o ator
/// vêm do contexto confiável; só quem tem <see cref="Permission.TenantSettingsManage"/> altera ou lê as versões, e toda alteração é auditada SEM o conteúdo (só o identificador e o número da versão).
/// O turno só usa a versão PUBLICADA (<see cref="IAgentDefinitionReader"/>), nunca o rascunho. Não há API HTTP nesta fatia: estes casos de uso são o que a API administrativa vai chamar.
/// </summary>
public sealed class AgentDefinitionService
{
    private const string Resource = "agents.definition";

    private readonly IRequestContextAccessor _context;
    private readonly IAgentDefinitionStore _store;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;

    public AgentDefinitionService(IRequestContextAccessor context, IAgentDefinitionStore store, IAuditWriter audit, TimeProvider clock)
    {
        _context = context;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>O rascunho do tenant, ou nulo.</summary>
    public async Task<AgentDefinitionView?> GetDraftAsync(CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        return ToView(await _store.FindDraftAsync(cancellationToken));
    }

    /// <summary>A versão publicada do tenant (a que o agente usa), ou nulo.</summary>
    public async Task<AgentDefinitionView?> GetPublishedAsync(CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        return ToView(await _store.FindPublishedVersionAsync(cancellationToken));
    }

    /// <summary>Cria o rascunho ou troca o conteúdo do que já existe. A versão publicada não muda.</summary>
    /// <exception cref="AgentsValidationException">Algum limite de tamanho ou formato foi violado.</exception>
    /// <exception cref="AgentDefinitionConflictException">Outra alteração aconteceu primeiro.</exception>
    public async Task<AgentDefinitionView> SaveDraftAsync(AgentDefinitionContent content, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);
        ArgumentNullException.ThrowIfNull(content);
        content.Validate();

        var agora = _clock.GetUtcNow();
        var rascunho = await _store.FindDraftAsync(cancellationToken);
        if (rascunho is null)
        {
            rascunho = AgentDefinitionVersion.CreateDraft(contexto.TenantId, await _store.NextNumberAsync(cancellationToken), content, agora, contexto.Principal?.UserId);
            _store.Add(rascunho);
        }
        else
        {
            rascunho.ReplaceDraft(content, agora, contexto.Principal?.UserId);
        }

        await _store.SaveAsync(cancellationToken);
        await _audit.WriteAsync(new AuditEntry("agents.definition.draft-saved", Resource, rascunho.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(rascunho)!;
    }

    /// <summary>Publica o rascunho: ele vira a versão que o agente usa, e a publicada anterior é aposentada, tudo numa transação.</summary>
    /// <exception cref="AgentsValidationException">Não há rascunho para publicar.</exception>
    /// <exception cref="AgentDefinitionConflictException">Outra alteração aconteceu primeiro.</exception>
    public async Task<AgentDefinitionView> PublishAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.TenantSettingsManage);

        var publicada = await _store.InTransactionAsync(
            async () =>
            {
                var rascunho = await _store.FindDraftAsync(cancellationToken)
                    ?? throw new AgentsValidationException("Não há rascunho para publicar.");
                var anterior = await _store.FindPublishedVersionAsync(cancellationToken);
                var agora = _clock.GetUtcNow();

                // Em duas gravações dentro da mesma transação: a restrição de uma só publicada por tenant é conferida a cada comando, e a aposentadoria precisa vir antes.
                if (anterior is not null)
                {
                    anterior.Retire(agora);
                    await _store.SaveAsync(cancellationToken);
                }

                rascunho.Publish(agora, contexto.Principal?.UserId);
                await _store.SaveAsync(cancellationToken);
                return rascunho;
            },
            cancellationToken);

        await _audit.WriteAsync(new AuditEntry("agents.definition.published", Resource, publicada.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(publicada)!;
    }

    private static AgentDefinitionView? ToView(AgentDefinitionVersion? versao) =>
        versao is null ? null : new AgentDefinitionView(versao.Number, versao.Status, versao.Content, versao.UpdatedAt, versao.PublishedAt);
}
