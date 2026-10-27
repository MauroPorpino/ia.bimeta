namespace IaBimeta.Tenancy.Application;

public sealed class DuplicateBusinessUnitNameException : Exception
{
    public DuplicateBusinessUnitNameException(string name)
        : base($"Já existe uma unidade chamada '{name}' nesta organização.")
    {
    }
}

public sealed class HeadquartersAlreadyExistsException : Exception
{
    public HeadquartersAlreadyExistsException()
        : base("Esta organização já tem uma matriz.")
    {
    }
}

public sealed class DuplicateDepartmentNameException : Exception
{
    public DuplicateDepartmentNameException(string name)
        : base($"Já existe um setor chamado '{name}' nesta unidade.")
    {
    }
}

/// <summary>
/// O recurso não existe no tenant ou o usuário não pode nem enxergá-lo. As duas situações são indistinguíveis de
/// propósito, para não revelar a existência do recurso (TEN-2).
/// </summary>
public sealed class TenancyResourceNotFoundException : Exception
{
    public TenancyResourceNotFoundException(string resource)
        : base($"{resource} não encontrada.")
    {
    }
}
