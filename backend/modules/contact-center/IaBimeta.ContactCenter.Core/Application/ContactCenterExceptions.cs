namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// O recurso não existe no tenant ou o usuário não pode nem enxergá-lo. As duas situações são indistinguíveis de propósito,
/// para não revelar a existência do recurso (TEN-2).
/// </summary>
public sealed class ContactCenterResourceNotFoundException : Exception
{
    public ContactCenterResourceNotFoundException(string resource)
        : base($"{resource} não encontrada.")
    {
    }
}

/// <summary>Já existe uma fila com este nome na organização (o nome não diferencia maiúsculas de minúsculas).</summary>
public sealed class QueueNameInUseException : Exception
{
    public QueueNameInUseException()
        : base("Já existe uma fila com este nome na organização.")
    {
    }
}

/// <summary>Outra alteração do mesmo recurso aconteceu primeiro: releia e repita a operação.</summary>
public sealed class ContactCenterConflictException : Exception
{
    public ContactCenterConflictException()
        : base("O recurso foi alterado por outra operação. Releia e tente de novo.")
    {
    }
}
