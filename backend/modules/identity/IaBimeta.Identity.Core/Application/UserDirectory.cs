using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>Implementação da consulta pública aos usuários do tenant do contexto.</summary>
public sealed class UserDirectory : IUserDirectory
{
    private readonly IUserRepository _users;
    private readonly IRequestContextAccessor _context;

    public UserDirectory(IUserRepository users, IRequestContextAccessor context)
    {
        _users = users;
        _context = context;
    }

    public async Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();

        return userId != Guid.Empty && await _users.FindByIdAsync(userId, cancellationToken) is not null;
    }
}
