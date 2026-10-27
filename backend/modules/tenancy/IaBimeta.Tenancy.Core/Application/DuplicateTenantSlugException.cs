namespace IaBimeta.Tenancy.Application;

public sealed class DuplicateTenantSlugException : Exception
{
    public DuplicateTenantSlugException(string slug)
        : base($"Já existe um tenant com o slug '{slug}'.")
    {
    }
}
