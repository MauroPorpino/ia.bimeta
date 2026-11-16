namespace IaBimeta.Identity.Application;

/// <summary>
/// A autenticação do Embedded foi recusada: chamada assinada inválida (chave, assinatura, relógio, nonce repetido, conexão ou tenant inativos) ou ticket inválido (desconhecido, expirado, já usado, verifier ou origem
/// errados). A mensagem é SEMPRE a mesma, para não revelar qual verificação falhou nem o que existe.
/// </summary>
public sealed class EmbeddedAuthenticationException : Exception
{
    public EmbeddedAuthenticationException()
        : base("Autenticação do Embedded recusada.")
    {
    }
}

/// <summary>O usuário externo não está mapeado e a conexão não provisiona (<c>MappedOnly</c>), ou o vínculo está desativado. Resposta ao servidor do ERP (já autenticado).</summary>
public sealed class ExternalIdentityNotAllowedException : Exception
{
    public ExternalIdentityNotAllowedException()
        : base("O usuário externo não tem acesso por esta conexão.")
    {
    }
}

/// <summary>O alvo do mapeamento não pode ser vinculado a uma identidade externa (conta administrativa, sem acesso à organização da conexão ou federada de outra conexão).</summary>
public sealed class ExternalIdentityTargetNotAllowedException : Exception
{
    public ExternalIdentityTargetNotAllowedException(string reason)
        : base(reason)
    {
    }
}

/// <summary>O recurso já existe ou o limite foi atingido (por exemplo, duas chaves ativas, vínculo duplicado).</summary>
public sealed class EmbeddedConflictException : Exception
{
    public EmbeddedConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>O segredo das chaves de integração não pode ser gravado nem lido (sem chave mestra configurada, texto alterado ou de outro contexto). Falha fechada, sem repetir segredo.</summary>
public sealed class EmbeddedUnavailableException : Exception
{
    public EmbeddedUnavailableException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
