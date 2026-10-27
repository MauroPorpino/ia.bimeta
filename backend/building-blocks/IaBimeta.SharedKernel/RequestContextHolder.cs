namespace IaBimeta.SharedKernel;

/// <summary>
/// Guarda o contexto da operação em curso (uma instância por requisição, registrada pelo host).
/// Estabelecer o contexto é privilégio dos hosts: <see cref="Set"/> é interno (TEN-1).
/// </summary>
public sealed class RequestContextHolder : IRequestContextAccessor
{
    public RequestContext? Current { get; private set; }

    internal void Set(RequestContext? context)
    {
        Current = context;
    }
}
