namespace IaBimeta.SharedKernel;

/// <summary>
/// Avaliação de permissão sobre o <see cref="RequestContext"/> (TEN-2). Nega por padrão para usuário autenticado:
/// sem concessão que cubra a permissão e a organização, a resposta é negativa.
/// Um contexto sem usuário (<see cref="RequestContext.IsSystem"/>) é de sistema: desenvolvimento, seed, webhook ou
/// job criados por código confiável do host. Ele não passa por permissão de usuário.
/// </summary>
public static class AccessPolicy
{
    /// <param name="organizationId">A organização do recurso, derivada do recurso carregado e nunca da requisição.
    /// Nulo pergunta por permissão de escopo do tenant, que só uma concessão de tenant cobre.</param>
    public static bool IsAllowed(this RequestContext context, Permission permission, Guid? organizationId = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Principal is null)
        {
            return true;
        }

        foreach (var grant in context.Principal.Grants)
        {
            if (grant.Permission == permission && (grant.OrganizationId is null || grant.OrganizationId == organizationId))
            {
                return true;
            }
        }

        return false;
    }

    /// <exception cref="AccessDeniedException">O contexto não tem a permissão sobre a organização.</exception>
    public static void Require(this RequestContext context, Permission permission, Guid? organizationId = null)
    {
        if (!context.IsAllowed(permission, organizationId))
        {
            throw new AccessDeniedException(permission);
        }
    }

    /// <summary>
    /// As organizações sobre as quais o contexto tem a permissão, para filtrar listas (TEN-2.5).
    /// <c>null</c> significa sem restrição (contexto de sistema ou concessão de tenant).
    /// </summary>
    public static IReadOnlySet<Guid>? OrganizationsWithPermission(this RequestContext context, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Principal is null)
        {
            return null;
        }

        var organizacoes = new HashSet<Guid>();
        foreach (var grant in context.Principal.Grants)
        {
            if (grant.Permission != permission)
            {
                continue;
            }

            if (grant.OrganizationId is null)
            {
                return null;
            }

            organizacoes.Add(grant.OrganizationId.Value);
        }

        return organizacoes;
    }

    /// <summary>
    /// A sessão embutida só alcança recursos da organização da conexão, e a de modo <c>CustomerOnly</c> não alcança a fila geral nem atendimento algum até existir a autorização por cliente (E2): para ela, TUDO está fora do
    /// limite. Contexto de sistema e sessão interativa nunca estão fora (a regra deles é a das concessões). Aplique em toda decisão de acesso que NÃO passe por <see cref="IsAllowed"/>
    /// (participação em fila, responsável, recibo de operação).
    /// </summary>
    public static bool IsOutsideEmbeddedScope(this RequestContext context, Guid? organizationId)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Principal?.Embedded is { } escopo && (escopo.CustomerOnly || organizationId != escopo.OrganizationId);
    }

    /// <exception cref="AuthenticationRequiredException">O contexto não tem usuário autenticado.</exception>
    public static ActorPrincipal RequirePrincipal(this RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Principal ?? throw new AuthenticationRequiredException();
    }
}
