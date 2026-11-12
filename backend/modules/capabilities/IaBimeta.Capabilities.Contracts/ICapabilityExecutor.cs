namespace IaBimeta.Capabilities.Contracts;

/// <summary>
/// O catálogo de operações publicadas do tenant do contexto, para quem precisa ESCOLHER uma operação (o agente, o chatbot, o assistente interno). Só entram operações publicadas, habilitadas, de conexão habilitada
/// e que aceitam o chamador. Nada de conexão, endereço nem segredo.
/// </summary>
public interface ICapabilityCatalog
{
    Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(CapabilityCaller caller, CancellationToken cancellationToken = default, Guid? scopeOrganizationId = null);

    Task<CapabilityDescriptor?> GetAsync(string key, CapabilityCaller caller, CancellationToken cancellationToken = default, Guid? scopeOrganizationId = null);
}

/// <summary>
/// O ÚNICO caminho para uma operação externa (ADR 0035). O modelo de IA e o Knowledge nunca executam: quem chama é código do produto, com o <c>RequestContext</c> confiável, e o motor valida autorização, estado,
/// parâmetros, limites e idempotência antes de qualquer chamada. Falha segura: na dúvida, não executa.
/// </summary>
public interface ICapabilityExecutor
{
    /// <summary>Executa (ou, para escrita com confirmação, registra e aguarda). Recusas de negócio voltam como <see cref="CapabilityResult"/>; falta de permissão lança <c>AccessDeniedException</c>.</summary>
    Task<CapabilityResult> ExecuteAsync(CapabilityInvocation invocation, CancellationToken cancellationToken = default);

    /// <summary>Confirma uma escrita pendente. Exige usuário com a permissão de escrita; um contexto de sistema nunca confirma.</summary>
    Task<CapabilityResult> ConfirmAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>Cancela uma escrita pendente (nada foi enviado).</summary>
    Task<CapabilityResult> CancelAsync(Guid executionId, CancellationToken cancellationToken = default);
}
