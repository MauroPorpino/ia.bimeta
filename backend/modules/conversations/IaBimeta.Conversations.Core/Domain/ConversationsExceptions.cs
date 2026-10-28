namespace IaBimeta.Conversations.Domain;

/// <summary>Dado informado viola uma regra do domínio de conversas.</summary>
public sealed class ConversationsValidationException : Exception
{
    public ConversationsValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A unidade responsável pela nova sessão não está entre as unidades autorizadas do número (instância de canal) pelo qual o
/// cliente falou, ou o Channels não pôde confirmar. Nada foi gravado e nenhuma outra unidade é escolhida no lugar: quem recebe
/// a mensagem (o Receiver) decide o que fazer, por exemplo usar a unidade padrão do canal.
/// </summary>
public sealed class ChannelUnitNotAuthorizedException : Exception
{
    public ChannelUnitNotAuthorizedException()
        : base("A unidade informada não está autorizada para esta instância de canal.")
    {
    }
}

/// <summary>
/// O recurso não existe no tenant ou o usuário não pode nem enxergá-lo. As duas situações são indistinguíveis de
/// propósito, para não revelar a existência do recurso (TEN-2).
/// </summary>
public sealed class ConversationsResourceNotFoundException : Exception
{
    public ConversationsResourceNotFoundException(string resource)
        : base($"{resource} não encontrada.")
    {
    }
}

/// <summary>O mesmo evento do provedor foi gravado por outra transação no mesmo instante (corrida resolvida pelo índice único).</summary>
public sealed class DuplicateProviderEventException : Exception
{
    public DuplicateProviderEventException()
        : base("O evento do provedor já foi registrado.")
    {
    }
}
