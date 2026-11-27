using IaBimeta.SharedKernel;

namespace IaBimeta.Audit.Application;

/// <summary>
/// Consulta da trilha do tenant. A trilha não tem escopo de organização, então a leitura exige a permissão
/// <see cref="Permission.AuditRead"/> de escopo do tenant (TEN-2).
/// </summary>
public sealed class AuditQueryService
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    private readonly IAuditRepository _eventos;
    private readonly IRequestContextAccessor _context;

    public AuditQueryService(IAuditRepository eventos, IRequestContextAccessor context)
    {
        _eventos = eventos;
        _context = context;
    }

    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    /// <exception cref="AccessDeniedException">O usuário não tem permissão de ler a auditoria do tenant.</exception>
    public async Task<IReadOnlyList<AuditEventView>> ListAsync(int? take = null, string? operation = null, string? resourceId = null, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.AuditRead);

        var limite = Math.Clamp(take ?? DefaultTake, 1, MaxTake);
        var eventos = await _eventos.ListAsync(new AuditQuery(limite, operation, resourceId), cancellationToken);
        return eventos.Select(AuditEventView.From).ToList();
    }

    /// <summary>
    /// A auditoria administrativa da PLATAFORMA (Etapa 02): exige a permissão <see cref="Permission.PlatformAuditRead"/>, que só existe no tenant técnico da plataforma (a trilha consultada é sempre a do tenant do contexto).
    /// Filtros por operação, recurso (por exemplo, o tenant afetado), resultado e período, com paginação.
    /// </summary>
    public async Task<(IReadOnlyList<AuditEventView> Items, int Total)> ListPlatformAsync(
        int page, int pageSize, string? operation, string? resource, string? outcome, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (contexto.Principal is null)
        {
            throw new AccessDeniedException(Permission.PlatformAuditRead);
        }

        contexto.Require(Permission.PlatformAuditRead);

        var tamanho = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, MaxTake);
        var pagina = Math.Max(1, page);
        var (itens, total) = await _eventos.ListPageAsync(new AuditPageQuery((pagina - 1) * tamanho, tamanho, operation, resource, outcome, from, to), cancellationToken);
        return (itens.Select(AuditEventView.From).ToList(), total);
    }
}
