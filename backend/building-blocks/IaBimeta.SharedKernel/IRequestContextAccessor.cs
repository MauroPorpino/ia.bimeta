namespace IaBimeta.SharedKernel;

/// <summary>Leitura do contexto da operação em curso. Os módulos só leem; quem estabelece é o host.</summary>
public interface IRequestContextAccessor
{
    /// <summary>O contexto confiável, ou <c>null</c> quando o host não conseguiu estabelecer um.</summary>
    RequestContext? Current { get; }
}

public static class RequestContextAccessorExtensions
{
    /// <summary>Devolve o contexto ou falha fechada com <see cref="TenantContextRequiredException"/>.</summary>
    public static RequestContext GetRequired(this IRequestContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        return accessor.Current ?? throw new TenantContextRequiredException();
    }
}
