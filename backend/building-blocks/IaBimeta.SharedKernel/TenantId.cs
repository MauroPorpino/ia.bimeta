namespace IaBimeta.SharedKernel;

/// <summary>Identificador do tenant. Acompanha toda entidade, evento e operação.</summary>
public sealed record TenantId
{
    public Guid Value { get; }

    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("TenantId não pode ser vazio.", nameof(value));
        }

        Value = value;
    }

    public override string ToString() => Value.ToString();
}
