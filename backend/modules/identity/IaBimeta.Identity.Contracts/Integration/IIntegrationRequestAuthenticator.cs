using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Contracts.Integration;

/// <summary>
/// A chamada assinada (HMAC-SHA256) do servidor do sistema externo como o host a viu (ADR 0039, seção 3; ADR 0041, seção 1). Os quatro primeiros campos são os cabeçalhos assinados, ainda sem
/// interpretação; o corpo é o BRUTO, antes de qualquer leitura. O caminho é exatamente o que o Api recebe (sem o prefixo do proxy).
/// </summary>
public sealed record IntegrationSignedCall(string? KeyId, string? Timestamp, string? Nonce, string? Signature, string Method, string Path, byte[] Body);

/// <summary>
/// O chamador de integração que a assinatura autenticou. O tenant e a organização vêm da CONEXÃO da chave, nunca da requisição nem do corpo. Autenticar não concede permissão alguma: o sistema externo só afirma
/// identidade e fatos, nunca dá acesso.
/// </summary>
public sealed record IntegrationCaller(TenantId TenantId, Guid ConnectionId, Guid KeyId, Guid OrganizationId, EmbeddedMode Mode);

public enum IntegrationAuthenticationOutcome
{
    /// <summary>A assinatura conferiu e o chamador está em <see cref="IntegrationAuthenticationResult.Caller"/>.</summary>
    Authenticated = 1,

    /// <summary>Recusa genérica (401): chave, assinatura, relógio, nonce repetido, conexão ou tenant inativos, ou qualquer falha inesperada. Nada revela qual verificação falhou.</summary>
    Denied = 2,

    /// <summary>A cota estourou (429); <see cref="IntegrationAuthenticationResult.RetryAfter"/> diz quando tentar de novo.</summary>
    RateLimited = 3,

    /// <summary>O serviço não pôde decidir agora (contenção transitória ou segredo indisponível): 503, nada foi gravado.</summary>
    Unavailable = 4,
}

public sealed record IntegrationAuthenticationResult(IntegrationAuthenticationOutcome Outcome, IntegrationCaller? Caller, TimeSpan? RetryAfter)
{
    public static IntegrationAuthenticationResult Denied { get; } = new(IntegrationAuthenticationOutcome.Denied, null, null);

    public static IntegrationAuthenticationResult Unavailable { get; } = new(IntegrationAuthenticationOutcome.Unavailable, null, null);

    public static IntegrationAuthenticationResult RateLimited(TimeSpan retryAfter) => new(IntegrationAuthenticationOutcome.RateLimited, null, retryAfter);

    public static IntegrationAuthenticationResult Authenticated(IntegrationCaller caller) => new(IntegrationAuthenticationOutcome.Authenticated, caller, null);

    /// <summary>Verdadeiro só quando há chamador autenticado: o consumidor decide por aqui e trata qualquer outro caso como negação (falha fechada).</summary>
    public bool IsAuthenticated => Outcome == IntegrationAuthenticationOutcome.Authenticated && Caller is not null;
}

/// <summary>
/// A porta pela qual os demais módulos (hoje, o Conversations nas rotas servidor-a-servidor do vínculo e do consentimento) autenticam o servidor do sistema externo, sem referenciar o Identity.Core (ADR 0041, seção 1).
/// O Identity a implementa sobre o autenticador de chamadas assinadas que já existe: mesmas verificações, mesma recusa genérica, mesma trilha de auditoria. Nunca lança por falha de autenticação: devolve o resultado.
/// </summary>
public interface IIntegrationRequestAuthenticator
{
    Task<IntegrationAuthenticationResult> AuthenticateAsync(IntegrationSignedCall call, CancellationToken cancellationToken = default);
}
