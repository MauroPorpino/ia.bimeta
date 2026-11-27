namespace IaBimeta.Identity.Domain;

/// <summary>Papéis iniciais. Cada papel dá um conjunto explícito de permissões (<see cref="Application.RolePermissions"/>).</summary>
public enum Role
{
    /// <summary>Usuário: lê a organização e a sua estrutura.</summary>
    User = 1,

    /// <summary>Gestor: além de ler, gere unidades e setores e vê os usuários da organização.</summary>
    Manager = 2,

    /// <summary>Administrador da organização: além do gestor, gere usuários e concessões da organização.</summary>
    OrganizationAdmin = 3,

    /// <summary>Atendente (ADR 0039): só o que o atendimento exige, sem estrutura, usuários, canais, integrações nem fluxos. É o papel que a federação cria (<c>JitAttendant</c>); a fila e a equipe continuam definindo o que ele atende (ContactCenter).</summary>
    Attendant = 4,

    // ---- Papéis da plataforma BImeta (Etapa 02, ADR 0043): só no tenant técnico, sempre no escopo do tenant. Uma empresa contratante nunca os atribui nem os calcula. ----

    /// <summary>Administrador BImeta: todas as permissões da plataforma, inclusive suspender empresas, gerir operadores e consultar a auditoria.</summary>
    PlatformAdministrator = 10,

    /// <summary>Operador comercial: cadastra e atende empresas (cadastro, dados, limite, habilitação e primeiro administrador). Não suspende, não gere operadores, não vê a auditoria.</summary>
    PlatformCommercial = 11,

    /// <summary>Consulta: só lê empresas e indicadores.</summary>
    PlatformViewer = 12,
}

public static class RoleExtensions
{
    /// <summary>Os papéis da plataforma BImeta (só valem no tenant técnico).</summary>
    public static bool IsPlatform(this Role role) => role is Role.PlatformAdministrator or Role.PlatformCommercial or Role.PlatformViewer;
}
