using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>
/// As permissões EFETIVAS de um usuário: o perfil padrão de cada concessão, mais as concessões individuais, menos as restrições individuais (Etapa 01). Função pura e determinística: não depende da ordem das operações.
/// <list type="number">
/// <item>Em cada escopo com concessão, base = permissões do papel naquele escopo.</item>
/// <item>Soma as concessões individuais (Grant) do mesmo escopo (só em escopo onde existe concessão; permissão só de tenant só no escopo do tenant).</item>
/// <item>Retira as restrições (Deny) do mesmo escopo. A restrição vence a concessão.</item>
/// <item>Precedência entre escopos: a restrição de TENANT retira a permissão de TODOS os escopos (a concessão mais abrangente não contorna restrição). A restrição de organização retira só dela; e, se a permissão chegaria
/// à organização apenas por uma concessão de tenant (que cobre todas), a concessão de tenant dessa permissão é descartada por inteiro (falha fechada: nunca sobra acesso além do declarado). A escrita recusa essa combinação;
/// este passo só garante o fechamento se ela aparecer por outro caminho.</item>
/// </list>
/// O resultado continua sendo uma lista de permissões positivas, o que <see cref="AccessPolicy"/> já sabe avaliar. O teto do Embedded é aplicado DEPOIS, sobre este resultado.
/// </summary>
public static class EffectivePermissions
{
    public static IReadOnlyList<PermissionGrant> Compute(IEnumerable<UserOrganizationGrant> grants, IEnumerable<UserPermissionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(overrides);

        var ajustes = overrides.ToList();
        // O escopo do tenant usa Guid.Empty como chave (organização nunca é Guid.Empty: a concessão recusa).
        var escopos = new Dictionary<Guid, HashSet<Permission>>();
        foreach (var concessao in grants)
        {
            if (!escopos.TryGetValue(concessao.OrganizationId ?? Guid.Empty, out var conjunto))
            {
                conjunto = new HashSet<Permission>();
                escopos[concessao.OrganizationId ?? Guid.Empty] = conjunto;
            }

            foreach (var p in RolePermissions.Expand(concessao.Role, concessao.OrganizationId))
            {
                conjunto.Add(p.Permission);
            }
        }

        foreach (var ajuste in ajustes.Where(o => o.Effect == OverrideEffect.Grant))
        {
            if (escopos.TryGetValue(ajuste.OrganizationId ?? Guid.Empty, out var conjunto) && PermissionCatalog.IsValidInScope(ajuste.Permission, ajuste.OrganizationId))
            {
                conjunto.Add(ajuste.Permission);
            }
        }

        var restricoesDoTenant = ajustes.Where(o => o.Effect == OverrideEffect.Deny && o.OrganizationId is null).Select(o => o.Permission).ToHashSet();
        var restricoesPorOrganizacao = ajustes
            .Where(o => o.Effect == OverrideEffect.Deny && o.OrganizationId is not null)
            .GroupBy(o => o.OrganizationId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(o => o.Permission).ToHashSet());

        var resultado = new List<PermissionGrant>();
        foreach (var (chaveEscopo, permissoes) in escopos)
        {
            foreach (var permissao in permissoes)
            {
                if (restricoesDoTenant.Contains(permissao))
                {
                    continue;
                }

                Guid? organizacao = chaveEscopo == Guid.Empty ? null : chaveEscopo;
                if (organizacao is { } org && restricoesPorOrganizacao.TryGetValue(org, out var negadasNaOrg) && negadasNaOrg.Contains(permissao))
                {
                    continue;
                }

                // Concessão de tenant que contraria uma restrição de organização: descartada por inteiro (falha fechada).
                if (organizacao is null && restricoesPorOrganizacao.Values.Any(negadas => negadas.Contains(permissao)))
                {
                    continue;
                }

                resultado.Add(new PermissionGrant(permissao, organizacao));
            }
        }

        return resultado;
    }
}
