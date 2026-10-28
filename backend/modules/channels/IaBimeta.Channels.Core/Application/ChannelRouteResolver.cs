using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Application;

/// <summary>
/// Resolve a rota de entrada: do identificador do número no provedor (que vem no webhook) até a instância e o tenant. Roda
/// ANTES de existir contexto de tenant, porque o tenant nunca vem de dado externo: ele é o que o cadastro diz sobre aquele
/// número. O resultado só serve para escolher o segredo da assinatura; o contexto de tenant só pode ser criado DEPOIS de a
/// autenticidade do evento ser validada (BOUNDARIES.md PRV-2, a validar antes do Receiver).
/// <para>
/// O identificador vem de fora e não é confiável: um formato inválido responde "sem rota" (nulo), sem exceção, e a consulta é
/// parametrizada. Número inexistente e número de outro tenant são a mesma resposta, porque a rota é global.
/// </para>
/// </summary>
public sealed class ChannelRouteResolver
{
    private readonly IChannelRepository _repository;

    public ChannelRouteResolver(IChannelRepository repository)
    {
        _repository = repository;
    }

    public Task<ChannelRouteResolution?> ResolveAsync(ChannelProvider provider, string? externalId, CancellationToken cancellationToken = default) =>
        Enum.IsDefined(provider) && ProviderIdentifier.IsValid(externalId)
            ? _repository.ResolveRouteAsync(provider, externalId!, cancellationToken)
            : Task.FromResult<ChannelRouteResolution?>(null);
}
