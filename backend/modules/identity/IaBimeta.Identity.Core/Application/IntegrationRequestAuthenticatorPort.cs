using IaBimeta.Identity.Contracts.Integration;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Implementa <see cref="IIntegrationRequestAuthenticator"/> sobre o <see cref="IntegrationRequestAuthenticator"/> existente (ADR 0041, seção 1): as MESMAS verificações, a mesma recusa genérica e a mesma trilha de
/// auditoria; só muda a forma de entregar o resultado (um valor, sem exceção do Identity vazando para outro módulo). FALHA FECHADA: só o veredito de credencial (<see cref="EmbeddedAuthenticationException"/>) é
/// <c>Denied</c> (401); contenção e qualquer falha operacional inesperada são <c>Unavailable</c> (503), nunca um chamador parcial; só o cancelamento do pedido propaga.
/// </summary>
public sealed class IntegrationRequestAuthenticatorPort : IIntegrationRequestAuthenticator
{
    private readonly IntegrationRequestAuthenticator _authenticator;

    public IntegrationRequestAuthenticatorPort(IntegrationRequestAuthenticator authenticator)
    {
        _authenticator = authenticator;
    }

    public async Task<IntegrationAuthenticationResult> AuthenticateAsync(IntegrationSignedCall call, CancellationToken cancellationToken = default)
    {
        if (call is null)
        {
            return IntegrationAuthenticationResult.Denied;
        }

        try
        {
            var autenticada = await _authenticator.AuthenticateAsync(
                new IntegrationSignedRequest(call.KeyId, call.Timestamp, call.Nonce, call.Signature, call.Method, call.Path, call.Body), cancellationToken);
            return IntegrationAuthenticationResult.Authenticated(
                new IntegrationCaller(autenticada.TenantId, autenticada.ConnectionId, autenticada.KeyId, autenticada.OrganizationId, autenticada.Mode));
        }
        catch (EmbeddedRateLimitedException ex)
        {
            return IntegrationAuthenticationResult.RateLimited(ex.RetryAfter);
        }
        catch (Exception ex) when (ex is EmbeddedBusyException or EmbeddedUnavailableException)
        {
            return IntegrationAuthenticationResult.Unavailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EmbeddedAuthenticationException)
        {
            // O ÚNICO veredito de credencial do autenticador: a mesma recusa genérica para chave, assinatura, relógio, nonce, conexão ou tenant.
            return IntegrationAuthenticationResult.Denied;
        }
        catch (Exception)
        {
            // Qualquer outra exceção NÃO é um veredito sobre a credencial: é falha operacional (banco, E/S, tempo esgotado) ou defeito. Responder 401 faria o sistema externo concluir que a chave é inválida e parar de
            // tentar; por isso é "indisponível" (503, repetir é seguro porque nada foi aceito). Continua FALHANDO FECHADO (nunca há chamador) e nada do erro vai ao cliente.
            return IntegrationAuthenticationResult.Unavailable;
        }
    }
}
