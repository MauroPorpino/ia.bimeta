using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Presentation;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.Embedded;

/// <summary>
/// Cria o contexto confiável (TEN-1) das chamadas assinadas do servidor do sistema externo que chegam ao Conversations (vínculo e, adiante, consentimento). O tenant é o que o AUTENTICADOR resolveu a partir da chave (nunca
/// do corpo nem de cabeçalho). O contexto não tem usuário e tem a origem de integração: as operações do Conversations para este caso exigem exatamente essa origem, então ele não abre nenhuma operação de sistema nem de usuário.
/// Estas rotas são dispensadas do contexto de sessão (<c>RequestContextMiddleware</c>), então o contexto aqui é sempre o primeiro da requisição: um segundo é erro de programação e falha fechada.
/// </summary>
internal sealed class IntegrationRequestContext : IIntegrationRequestContext
{
    private readonly RequestContextHolder _holder;

    public IntegrationRequestContext(RequestContextHolder holder)
    {
        _holder = holder;
    }

    public void Establish(TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (_holder.Current is not null)
        {
            throw new InvalidOperationException("O contexto da requisição já foi estabelecido.");
        }

        _holder.Set(new RequestContext(tenant, ContactLinkService.IntegrationContextOrigin));
    }
}
