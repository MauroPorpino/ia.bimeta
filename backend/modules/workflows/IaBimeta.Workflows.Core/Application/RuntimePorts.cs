using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

/// <summary>O armazém do runtime (instâncias, efeitos, passos, decisões e ativações). Todas as consultas valem para o tenant do contexto; as duas varreduras entre tenants (<c>ListTenantsWithDue</c>) só devolvem identificadores.</summary>
public interface IFlowRuntimeStore
{
    /// <summary>A decisão de roteamento da sessão (rastreada, para ser alterada), ou nula.</summary>
    Task<RoutingDecision?> GetDecisionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Grava a decisão e, se houver, a instância na MESMA transação. Devolve falso (sem gravar nada) se a sessão já tinha decisão (a disputa é do índice único).</summary>
    Task<bool> TryRecordAsync(RoutingDecision decision, FlowInstance? instance, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowActivation>> ListEnabledActivationsAsync(CancellationToken cancellationToken);

    Task<FlowInstance?> FindInstanceAsync(Guid id, CancellationToken cancellationToken);

    Task<FlowInstance?> FindActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<int> CountActiveAsync(CancellationToken cancellationToken);

    /// <summary>Os efeitos ainda por realizar (pendentes ou em andamento), na ordem de criação.</summary>
    Task<IReadOnlyList<FlowEffectRecord>> ListOpenEffectsAsync(Guid instanceId, CancellationToken cancellationToken);

    /// <summary>Apaga o conteúdo cifrado dos efeitos de uma instância ENCERRADA (o texto das mensagens pode ter dado pessoal): depois do fim ele não serve a nada, e só ficam o tipo, o estado e o código.</summary>
    Task WipeEffectPayloadsAsync(Guid instanceId, CancellationToken cancellationToken);

    void Add(FlowEffectRecord effect);

    void Add(FlowStepRecord step);

    /// <exception cref="WorkflowsConflictException"><c>changed</c>: a instância foi alterada por outro executor (revisão).</exception>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Os tenants com instância devida (próximo olhar vencido, sem lease vigente). Entre tenants: só identificadores.</summary>
    Task<IReadOnlyList<Guid>> ListTenantsWithDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>Reivindica até <paramref name="take"/> instâncias devidas do tenant do contexto (lease com token, revisão otimista). Devolve só os identificadores.</summary>
    Task<IReadOnlyList<Guid>> ClaimDueAsync(string owner, TimeSpan leaseFor, DateTimeOffset now, int take, CancellationToken cancellationToken);
}

/// <summary>Um texto cifrado e a chave que o cifrou.</summary>
public sealed record FlowCipherText(string KeyId, string Ciphertext);

/// <summary>
/// A cifra das variáveis e dos efeitos (ADR 0037 §5): AES-256-GCM com chaves PRÓPRIAS do módulo (<c>Workflows:Secrets</c>). O tenant, a finalidade e o recurso entram como dado autenticado: o texto cifrado de uma
/// instância não abre sob outro tenant nem outra instância. Sem chave configurada, a gravação falha fechada (nunca grava variável em claro).
/// </summary>
public interface IFlowCipher
{
    bool IsConfigured { get; }

    /// <exception cref="WorkflowsUnavailableException"><c>secrets-unavailable</c>.</exception>
    FlowCipherText Protect(string plaintext, TenantId tenant, Guid resourceId, string purpose);

    /// <exception cref="WorkflowsUnavailableException"><c>secrets-unavailable</c> ou <c>secret-invalid</c>.</exception>
    string Unprotect(FlowCipherText text, TenantId tenant, Guid resourceId, string purpose);
}

/// <summary>A cifra não está disponível (sem chave) ou o texto cifrado é inválido. O código é fixo e nunca repete conteúdo.</summary>
public sealed class WorkflowsUnavailableException : Exception
{
    public WorkflowsUnavailableException(string code)
        : base("Recurso indisponível.")
    {
        Code = code;
    }

    public string Code { get; }
}
