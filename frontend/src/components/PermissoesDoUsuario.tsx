import type { CurrentUser, OrganizationSummary, PermissionCatalog, PermissionEffect, UserPermissionEntry } from '../api/types';
import { agruparPorGrupo, nomeDaOrganizacao, pode, podeNoTenant } from '../usuarios';

interface Props {
  me: CurrentUser;
  catalogo: PermissionCatalog;
  organizacoes: OrganizationSummary[];
  permissoes: UserPermissionEntry[];
  ocupado: boolean;
  onDefinir: (organizationId: string | null, permission: string, effect: PermissionEffect) => void;
  onRemover: (organizationId: string | null, permission: string) => void;
  onRestaurar: (escopo: { allScopes: boolean; organizationId: string | null }) => void;
}

function situacaoDaPermissao(p: UserPermissionEntry): string {
  if (p.override === 'Grant') {
    return 'Personalizada: concedida';
  }
  if (p.override === 'Deny') {
    return 'Personalizada: retirada';
  }
  return p.inProfile ? 'Herdada do perfil' : 'Não concedida pelo perfil';
}

/**
 * Modelo híbrido: cada permissão vem do perfil (herdada) ou foi personalizada (concedida ou retirada) para esse vínculo. Só quem tem `UserPermissionManage` vê os controles de personalização;
 * só aparecem as permissões do catálogo.
 */
export function PermissoesDoUsuario({ me, catalogo, organizacoes, permissoes, ocupado, onDefinir, onRemover, onRestaurar }: Props) {
  const dadosDoCatalogo = new Map(catalogo.permissions.map((p) => [p.permission, p]));
  const escopos = [...new Set(permissoes.map((p) => p.organizationId))];
  const algumaPersonalizada = permissoes.some((p) => p.override !== null);
  const podeNoEscopo = (organizacao: string | null) => (organizacao === null ? podeNoTenant(me, 'UserPermissionManage') : pode(me, 'UserPermissionManage', organizacao));

  if (escopos.length === 0) {
    return <p className="discreto">Este usuário não tem vínculos com permissões.</p>;
  }

  return (
    <div className="permissoes-usuario">
      {escopos.map((escopo) => {
        const doEscopo = permissoes.filter((p) => p.organizationId === escopo && dadosDoCatalogo.has(p.permission));
        const nome = nomeDaOrganizacao(escopo, organizacoes);
        const gerencia = podeNoEscopo(escopo);
        const personalizadaNoEscopo = doEscopo.some((p) => p.override !== null);
        const grupos = agruparPorGrupo(doEscopo.map((p) => ({ ...p, group: dadosDoCatalogo.get(p.permission)!.group })));
        return (
          <section key={escopo ?? 'tenant'} className="permissoes-escopo" aria-label={`Permissões em ${nome}`}>
            <h4>{nome}</h4>
            {grupos.map(({ grupo, itens }) => (
              <div key={grupo}>
                <strong>{grupo}</strong>
                <ul className="lista-permissoes">
                  {itens.map((p) => {
                    const rotulo = dadosDoCatalogo.get(p.permission)!.label;
                    return (
                      <li key={p.permission}>
                        <span className="permissao-nome">{rotulo}</span>
                        <span className="etiqueta">{situacaoDaPermissao(p)}</span>
                        <span className={p.effective ? 'etiqueta' : 'etiqueta etiqueta-aviso'}>{p.effective ? 'Efetiva' : 'Não efetiva'}</span>
                        {gerencia && (
                          <span className="acoes">
                            {p.effective ? (
                              <button type="button" className="btn btn-pequeno" disabled={ocupado} aria-label={`Retirar ${rotulo} (${nome})`} onClick={() => onDefinir(escopo, p.permission, 'Deny')}>
                                Retirar
                              </button>
                            ) : (escopo === null ? podeNoTenant(me, p.permission) : pode(me, p.permission, escopo)) ? (
                              <button type="button" className="btn btn-pequeno" disabled={ocupado} aria-label={`Conceder ${rotulo} (${nome})`} onClick={() => onDefinir(escopo, p.permission, 'Grant')}>
                                Conceder
                              </button>
                            ) : (
                              <span className="discreto">Sem autoridade para conceder</span>
                            )}
                            {p.override !== null && (
                              <button type="button" className="btn btn-pequeno" disabled={ocupado} aria-label={`Remover personalização de ${rotulo} (${nome})`} onClick={() => onRemover(escopo, p.permission)}>
                                Remover personalização
                              </button>
                            )}
                          </span>
                        )}
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))}
            {gerencia && personalizadaNoEscopo && (
              <div className="acoes">
                <button type="button" className="btn btn-pequeno" disabled={ocupado} aria-label={`Restaurar padrão do perfil (${nome})`} onClick={() => onRestaurar({ allScopes: false, organizationId: escopo })}>
                  Restaurar padrão do perfil
                </button>
              </div>
            )}
          </section>
        );
      })}
      {algumaPersonalizada && escopos.every(podeNoEscopo) && (
        <div className="acoes">
          <button type="button" className="btn btn-pequeno" disabled={ocupado} onClick={() => onRestaurar({ allScopes: true, organizationId: null })}>
            Restaurar padrão em todos os escopos
          </button>
        </div>
      )}
    </div>
  );
}
