namespace IaBimeta.Tenancy.Application;

public sealed class DuplicateOrganizationNameException : Exception
{
    public DuplicateOrganizationNameException(string name)
        : base($"Já existe uma organização chamada '{name}' neste tenant.")
    {
    }
}
