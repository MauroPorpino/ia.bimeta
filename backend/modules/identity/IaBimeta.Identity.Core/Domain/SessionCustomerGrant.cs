using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Um cliente do ERP em foco numa sessão embutida, autorizado pelo servidor do ERP (ticket assinado de abertura ou de contexto). O navegador nunca cria um: só o resgate de um ticket. Morre com a sessão.
/// </summary>
public sealed class SessionCustomerGrant : ITenantOwned
{
    /// <summary>Quantos clientes em foco uma sessão guarda. Passado o limite, o mais antigo sai.</summary>
    public const int MaxPerSession = 20;

    private SessionCustomerGrant(TenantId tenantId, Guid sessionId, string customerRef, DateTimeOffset grantedAt)
    {
        TenantId = tenantId;
        SessionId = sessionId;
        CustomerRef = customerRef;
        GrantedAt = grantedAt;
    }

    public TenantId TenantId { get; }

    public Guid SessionId { get; }

    public string CustomerRef { get; }

    public DateTimeOffset GrantedAt { get; private set; }

    public static SessionCustomerGrant Create(TenantId tenantId, Guid sessionId, string customerRef, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return new SessionCustomerGrant(tenantId, sessionId, LaunchTicket.NormalizeCustomerRef(customerRef) ?? throw new IdentityValidationException("O cliente do ERP é obrigatório."), now);
    }

    public void Touch(DateTimeOffset now) => GrantedAt = now;
}
