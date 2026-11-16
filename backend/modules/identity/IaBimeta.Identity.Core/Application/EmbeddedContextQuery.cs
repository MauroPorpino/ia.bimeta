using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>Implementa <see cref="IEmbeddedContextQuery"/>: tenant e sessão vêm do contexto confiável, nunca de parâmetro.</summary>
public sealed class EmbeddedContextQuery : IEmbeddedContextQuery
{
    private readonly IEmbeddedStore _store;
    private readonly IRequestContextAccessor _context;

    public EmbeddedContextQuery(IEmbeddedStore store, IRequestContextAccessor context)
    {
        _store = store;
        _context = context;
    }

    public async Task<EmbeddedSessionInfo?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (contexto.Principal is null)
        {
            return null;
        }

        var sessao = await _store.FindSessionAsync(contexto.TenantId, contexto.Principal.SessionId, cancellationToken);
        if (sessao is not { Kind: SessionKind.Embedded } || sessao.IntegrationId is not { } conexaoId || sessao.ExternalUserId is null)
        {
            return null;
        }

        var conexao = await _store.FindConnectionAsync(contexto.TenantId, conexaoId, forUpdate: false, cancellationToken);
        if (conexao is null)
        {
            return null;
        }

        var clientes = await _store.ListCustomerGrantsAsync(contexto.TenantId, sessao.Id, cancellationToken);
        return new EmbeddedSessionInfo(sessao.Id, conexao.Id, conexao.OrganizationId, conexao.Mode, sessao.ExternalUserId, clientes);
    }

    public async Task<bool> IsCustomerGrantedAsync(string customerRef, CancellationToken cancellationToken = default)
    {
        var normalizado = LaunchTicket.NormalizeCustomerRef(customerRef);
        if (normalizado is null)
        {
            return false;
        }

        var atual = await GetCurrentAsync(cancellationToken);
        return atual is not null && atual.CustomerRefs.Contains(normalizado, StringComparer.Ordinal);
    }
}
