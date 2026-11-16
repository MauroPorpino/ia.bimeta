namespace IaBimeta.Identity.Domain;

/// <summary>Como uma conexão de integração trata um usuário externo ainda não mapeado (ADR 0039, seção 6).</summary>
public enum ProvisioningMode
{
    /// <summary>Só entra quem o administrador mapeou explicitamente. Usuário não mapeado é recusado.</summary>
    MappedOnly = 1,

    /// <summary>Na primeira entrada cria um usuário federado com o papel de atendente na organização da conexão, e nada além. Nunca se vincula a usuário existente.</summary>
    JitAttendant = 2,
}

public enum ConnectionStatus
{
    Active = 1,
    Disabled = 2,
}

public enum IntegrationKeyStatus
{
    Active = 1,
    Revoked = 2,
}

public enum TicketKind
{
    /// <summary>Abre uma sessão embutida (troca no iframe, com o verifier).</summary>
    Launch = 1,

    /// <summary>Muda o cliente em foco de uma sessão embutida já aberta.</summary>
    Context = 2,
}

public enum SessionKind
{
    /// <summary>Login por senha na aplicação independente.</summary>
    Interactive = 1,

    /// <summary>Sessão curta aberta por ticket do servidor do ERP, só em memória do iframe.</summary>
    Embedded = 2,
}

/// <summary>Como o usuário prova quem é.</summary>
public enum UserAuthMode
{
    Password = 1,

    /// <summary>Criado pela federação: nunca faz login por senha.</summary>
    Federated = 2,
}

public enum IdentitySource
{
    /// <summary>Mapeada pelo administrador.</summary>
    Mapped = 1,

    /// <summary>Criada na primeira entrada (<see cref="ProvisioningMode.JitAttendant"/>).</summary>
    Jit = 2,
}

public enum ExternalIdentityStatus
{
    Active = 1,
    Disabled = 2,
}
