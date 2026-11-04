using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Application;

/// <summary>
/// Lê a configuração de IA de um tenant (provedor, modelo, origem da credencial, limites e situação). A implementação (Infrastructure) filtra por
/// tenant; o AiGateway ainda confere que o que voltou é do tenant do contexto e recusa o que não for (defesa em profundidade, TEN-1).
/// </summary>
public interface ITenantAiConfigurationReader
{
    /// <summary>A configuração do tenant, ou nulo se ele ainda não configurou a IA.</summary>
    Task<AiTenantConfiguration?> GetAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Resolve a credencial do tenant para o provedor, NA HORA do uso (ADR 0017, seção E.2). A implementação (Infrastructure) decifra a
/// credencial guardada sob o tenant e o provedor, e falha fechada (nulo) sem credencial utilizável. O AiGateway a mantém em memória só durante a chamada e
/// nunca a põe em log, em erro nem em resultado.
/// </summary>
public interface IAiCredentialResolver
{
    /// <summary>A credencial do tenant para o provedor, ou nulo se não há uma utilizável (nenhuma, revogada).</summary>
    /// <exception cref="AiSecretsUnavailableException">A chave mestra não está disponível: é falha da plataforma, não do tenant.</exception>
    Task<AiCredential?> ResolveAsync(TenantId tenantId, string providerId, CancellationToken cancellationToken);
}

/// <summary>Quem a credencial protegida serve: o tenant, a credencial e o provedor entram como dado autenticado da cifra (a mesma credencial não abre sob outro).</summary>
public sealed record AiCredentialBinding(TenantId TenantId, Guid CredentialId, string ProviderId);

/// <summary>A credencial cifrada: o identificador da chave que a cifrou e o texto cifrado (envelope versionado). Nunca contém o valor em claro.</summary>
public sealed record AiProtectedCredential(string KeyId, string Ciphertext)
{
    /// <summary>Não imprime o texto cifrado.</summary>
    public override string ToString() => $"AiProtectedCredential {{ KeyId = {KeyId}, Ciphertext = [oculto] }}";
}

/// <summary>
/// Cifra autenticada das credenciais BYOK (ADR 0017, seção E): AES-256-GCM, nonce aleatório por cifra e AAD ligando tenant, credencial e provedor. A chave mestra é
/// própria do AiGateway (nunca a do Channels) e nunca fica no banco, no repositório nem em log.
/// </summary>
public interface IAiCredentialProtector
{
    /// <exception cref="AiSecretsUnavailableException">Não há chave ativa configurada (falha fechada).</exception>
    AiProtectedCredential Protect(string secret, AiCredentialBinding binding);

    /// <exception cref="AiSecretsUnavailableException">A chave que cifrou a credencial não está configurada.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">O texto foi adulterado, tem formato inválido ou não serve a este tenant, credencial e provedor.</exception>
    string Unprotect(AiProtectedCredential credential, AiCredentialBinding binding);
}

/// <summary>Qual limite do tenant impediu a chamada.</summary>
public enum AiLimitRefusal
{
    /// <summary>Já há o máximo de chamadas simultâneas do tenant.</summary>
    ConcurrentCalls = 1,

    /// <summary>O tenant já fez o máximo de chamadas do período.</summary>
    CallsPerPeriod = 2,

    /// <summary>O tenant não tem tokens no período para a reserva desta chamada (consumido, indeterminado e reservas abertas mais a reserva dela passariam do máximo).</summary>
    TokensPerPeriod = 3,
}

/// <summary>
/// O pedido de uma vaga para fazer uma chamada: o tenant, a chamada, os limites vigentes, o instante e a RESERVA de tokens. A reserva é a entrada ESTIMADA (com margem
/// de segurança) mais o teto de saída já limitado: é o orçamento retido enquanto a chamada está aberta, nunca uma garantia do consumo real.
/// </summary>
/// <param name="CorrelationId">A correlação opaca do consumidor (por exemplo, o <c>TurnId</c>), guardada no registro de consumo.</param>
/// <param name="EstimatedInputTokens">A entrada estimada em tokens, já com a margem de segurança. Heurística: não é o consumo real.</param>
/// <param name="MaxOutputTokens">O teto de tokens de saída que irá na requisição ao provedor (já limitado pelo tenant, pelo modelo e pelo pedido).</param>
/// <param name="LeaseDuration">Por quanto tempo a vaga vale sem conclusão. Passado esse tempo, a vaga expira e o consumo vira indeterminado.</param>
public sealed record AiLeaseRequest(
    TenantId TenantId,
    Guid AiRequestId,
    string CorrelationId,
    string ProviderId,
    string ModelId,
    AiTenantLimits Limits,
    DateTimeOffset Now,
    long EstimatedInputTokens,
    int MaxOutputTokens,
    TimeSpan LeaseDuration)
{
    /// <summary>Os tokens que a vaga retém do orçamento do período: a entrada estimada mais o teto de saída.</summary>
    public long ReservedTokens => EstimatedInputTokens + MaxOutputTokens;
}

/// <summary>A vaga concedida para uma chamada. Mantém um lugar entre as chamadas simultâneas do tenant, e a reserva de tokens, até ser concluída em <see cref="IAiUsageLedger.CompleteAsync"/> ou expirar.</summary>
public sealed record AiLease(TenantId TenantId, Guid AiRequestId, DateTimeOffset StartedAt, long ReservedTokens, DateTimeOffset ExpiresAt);

/// <summary>A vaga concedida (<see cref="Lease"/>) ou o limite que a recusou (<see cref="Refusal"/>), nunca os dois.</summary>
public sealed record AiLeaseResult(AiLease? Lease, AiLimitRefusal? Refusal);

/// <summary>
/// A contabilidade de uso do tenant (ADR 0017, seção I): a única porta de estado dos limites. A implementação (Infrastructure) conta no banco, para valer entre
/// instâncias (BOUNDARIES LIM-1). O consumo tem quatro situações: ESTIMADO (antes da chamada), RESERVADO (vaga aberta), CONFIRMADO (o provedor informou, ou provou-se que
/// nada foi cobrado) e INDETERMINADO (pode ter sido cobrado e não se sabe quanto: cobra a reserva até a reconciliação).
/// </summary>
public interface IAiUsageLedger
{
    /// <summary>
    /// Pede, de forma ATÔMICA, a vaga para uma chamada: concede só se o tenant tem chamada simultânea livre, chamadas do período dentro do limite e tokens do período para a
    /// reserva, e então ocupa a vaga e conta a chamada. Vagas expiradas do tenant são convertidas em consumo indeterminado antes da decisão. Duas chamadas concorrentes nunca
    /// ultrapassam o limite de simultaneidade nem somam reservas acima do teto do período.
    /// </summary>
    Task<AiLeaseResult> TryBeginAsync(AiLeaseRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Conclui a chamada e registra o consumo. É chamada SEMPRE que a vaga foi concedida, também quando a chamada falha ou é cancelada, e é IDEMPOTENTE: repeti-la, ou chegar
    /// depois de a vaga ter expirado, nunca conta a mesma chamada duas vezes. Um consumo confirmado depois de uma expiração RECONCILIA o indeterminado, uma única vez.
    /// </summary>
    Task CompleteAsync(AiLease lease, AiRequestRecord record, CancellationToken cancellationToken);
}
