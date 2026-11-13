using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Workflows.Contracts;

namespace IaBimeta.Api.Channels;

/// <summary>
/// A validação do escopo de uma ativação do chatbot visual (ADR 0038), mediada pelo host: a organização existe NO TENANT do contexto, a unidade é dessa organização e o número (canal) é dessa organização e, quando
/// há unidade, a atende. Tudo pelo tenant do <c>RequestContext</c>: um identificador de outro tenant responde como inexistente. Os motivos são códigos fixos.
/// </summary>
internal sealed class WorkflowActivationScopeValidator : IActivationScopeValidator
{
    private readonly IOrganizationDirectory _organizations;
    private readonly IBusinessUnitDirectory _units;
    private readonly IChannelRouting _channels;

    public WorkflowActivationScopeValidator(IOrganizationDirectory organizations, IBusinessUnitDirectory units, IChannelRouting channels)
    {
        _organizations = organizations;
        _units = units;
        _channels = channels;
    }

    public async Task<string?> ValidateAsync(Guid organizationId, Guid? businessUnitId, Guid? channelInstanceId, CancellationToken cancellationToken = default)
    {
        if (!await _organizations.ExistsAsync(organizationId, cancellationToken))
        {
            return "organization-not-found";
        }

        if (businessUnitId is { } unidade && await _units.FindOrganizationIdAsync(unidade, cancellationToken) != organizationId)
        {
            return "unit-not-in-organization";
        }

        if (channelInstanceId is { } canal)
        {
            var rota = await _channels.FindAsync(new ChannelInstanceId(canal), cancellationToken);
            if (rota is null || rota.OrganizationId != organizationId)
            {
                return "channel-not-in-organization";
            }

            if (businessUnitId is { } u && !rota.AuthorizedBusinessUnitIds.Contains(u))
            {
                return "channel-does-not-serve-unit";
            }
        }

        return null;
    }
}
