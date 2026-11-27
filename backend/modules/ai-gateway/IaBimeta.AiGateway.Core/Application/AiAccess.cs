using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Application;

/// <summary>
/// A autorização das operações de IA do tenant (Etapa 05): a permissão ESPECÍFICA da operação (consultar, gerir o padrão, gerir credenciais, testar conectividade, testar geração) ou a permissão ampla de configurar o tenant
/// (<see cref="Permission.TenantSettingsManage"/>, que já abrangia a IA antes desta etapa). Todas são de escopo do TENANT e só o administrador do tenant as recebe por padrão. Sem nenhuma, a negação é a da permissão específica.
/// A interface só decide o que mostrar: esta verificação é a que vale.
/// </summary>
public static class AiAccess
{
    public static bool IsAllowed(RequestContext context, Permission specific) =>
        context.IsAllowed(specific) || context.IsAllowed(Permission.TenantSettingsManage);

    /// <exception cref="AccessDeniedException">Nenhuma das duas permissões existe no contexto.</exception>
    public static void Require(RequestContext context, Permission specific)
    {
        if (!IsAllowed(context, specific))
        {
            context.Require(specific);
        }
    }
}

/// <summary>O identificador de recurso da auditoria de IA: <c>provedor:modelo</c> (ou <c>provedor:credencial</c>), só com os caracteres que a trilha aceita e no máximo 100. Nunca traz segredo.</summary>
public static class AiAudit
{
    public static string Id(string providerId, string second)
    {
        static string Clean(string s) => new(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-' ? c : '_').ToArray());
        var id = $"{Clean(providerId)}:{Clean(second)}";
        return id.Length <= 100 ? id : id[..100];
    }
}
