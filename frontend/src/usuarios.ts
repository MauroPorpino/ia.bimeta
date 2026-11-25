import { ApiError, descreverErro } from './api/client';
import type { CurrentUser, OrganizationSummary, PermissionCatalog, UserListItem, UserStatus } from './api/types';

// Configurações → Usuários. O servidor aplica cada permissão (403 sem ela, 404 fora do alcance): a interface só decide o que mostrar.

export type Permissao = 'UserRead' | 'UserManage' | 'UserStatusManage' | 'UserPermissionManage';

/** O usuário logado tem a permissão na organização (ou no tenant inteiro, quando `organizationId` é nulo na permissão efetiva). Sem `organizacao`, vale em qualquer escopo. */
export function pode(me: CurrentUser, permissao: Permissao | string, organizacao?: string | null): boolean {
  return (me.permissions ?? []).some(
    (p) => p.permission === permissao && (organizacao === undefined || p.organizationId === null || p.organizationId === organizacao),
  );
}

/** Tem a permissão no escopo do tenant (vale para qualquer organização). */
export function podeNoTenant(me: CurrentUser, permissao: Permissao | string): boolean {
  return (me.permissions ?? []).some((p) => p.permission === permissao && p.organizationId === null);
}

/** O usuário logado tem a permissão sobre o usuário alvo: no tenant inteiro, ou numa das organizações em que o alvo tem vínculo. */
export function podeSobre(me: CurrentUser, permissao: Permissao, alvo: Pick<UserListItem, 'grants'>): boolean {
  return podeNoTenant(me, permissao) || alvo.grants.some((g) => g.organizationId !== null && pode(me, permissao, g.organizationId));
}

export function podeVerUsuarios(me: CurrentUser): boolean {
  return pode(me, 'UserRead');
}

/** As organizações em que o usuário logado tem a permissão. */
export function organizacoesCom(me: CurrentUser, permissao: Permissao, organizacoes: OrganizationSummary[]): OrganizationSummary[] {
  return organizacoes.filter((o) => pode(me, permissao, o.id));
}

export const PERFIS = ['OrganizationAdmin', 'Manager', 'Attendant', 'User'] as const;

export const ROTULO_PERFIL: Record<string, string> = {
  OrganizationAdmin: 'Administrador',
  Manager: 'Supervisor',
  Attendant: 'Atendente',
  User: 'Usuário',
};

export function rotuloDoPerfil(perfil: string): string {
  return ROTULO_PERFIL[perfil] ?? perfil;
}

/** Perfis acima de Atendente exigem UserPermissionManage. */
export function perfilExigePermissaoDePerfil(perfil: string): boolean {
  return perfil === 'OrganizationAdmin' || perfil === 'Manager';
}

export const ROTULO_SITUACAO: Record<UserStatus, string> = {
  Active: 'Ativo',
  Inactive: 'Inativo',
  Pending: 'Pendente',
  Expired: 'Expirado',
  Cancelled: 'Cancelado',
};

export const SITUACOES_DO_FILTRO: Array<{ valor: string; rotulo: string }> = [
  { valor: 'active', rotulo: 'Ativo' },
  { valor: 'inactive', rotulo: 'Inativo' },
  { valor: 'pending', rotulo: 'Pendente' },
  { valor: 'expired', rotulo: 'Expirado' },
  { valor: 'cancelled', rotulo: 'Cancelado' },
];

export const ORDEM_DOS_GRUPOS = ['Usuários', 'Organizações', 'Atendimento', 'Filas', 'Canais', 'Integrações', 'Fluxos', 'Configurações'];

/** Agrupa itens do catálogo pelo grupo funcional, na ordem fixa da tela (grupos desconhecidos vão ao fim). */
export function agruparPorGrupo<T extends { group: string }>(itens: T[]): Array<{ grupo: string; itens: T[] }> {
  const mapa = new Map<string, T[]>();
  for (const item of itens) {
    mapa.set(item.group, [...(mapa.get(item.group) ?? []), item]);
  }
  const posicao = (g: string) => {
    const i = ORDEM_DOS_GRUPOS.indexOf(g);
    return i === -1 ? ORDEM_DOS_GRUPOS.length : i;
  };
  return [...mapa.entries()].sort((a, b) => posicao(a[0]) - posicao(b[0])).map(([grupo, lista]) => ({ grupo, itens: lista }));
}

/** As permissões padrão de um perfil (no escopo de organização ou do tenant), só as que existem no catálogo. */
export function permissoesPadraoDoPerfil(catalogo: PermissionCatalog, perfil: string, escopoDoTenant = false): PermissionCatalog['permissions'] {
  const papel = catalogo.roles.find((r) => r.role === perfil);
  if (!papel) {
    return [];
  }
  const nomes = new Set(escopoDoTenant ? papel.tenantPermissions : papel.organizationPermissions);
  return catalogo.permissions.filter((p) => nomes.has(p.permission));
}

export function nomeDaOrganizacao(id: string | null, organizacoes: OrganizationSummary[]): string {
  if (id === null) {
    return 'Empresa inteira';
  }
  return organizacoes.find((o) => o.id === id)?.name ?? `Organização ${id.slice(0, 8)}`;
}

/** Os perfis do usuário, um texto por vínculo: "Atendente (Matriz); Usuário (Empresa inteira)". */
export function perfisDoUsuario(usuario: Pick<UserListItem, 'grants'>, organizacoes: OrganizationSummary[]): string {
  if (usuario.grants.length === 0) {
    return 'Sem vínculo';
  }
  return usuario.grants.map((g) => `${rotuloDoPerfil(g.role)} (${nomeDaOrganizacao(g.organizationId, organizacoes)})`).join('; ');
}

/**
 * A organização usada nas rotas que a levam na URL (redefinição, sessões): uma em que o usuário alvo tem vínculo e em que quem opera tem `UserManage`;
 * senão, qualquer organização em que quem opera tem `UserManage`. Nula quando não há nenhuma.
 */
export function organizacaoParaAcao(alvo: Pick<UserListItem, 'grants'>, me: CurrentUser, organizacoes: OrganizationSummary[]): string | null {
  const gerenciaveis = organizacoesCom(me, 'UserManage', organizacoes).map((o) => o.id);
  const vinculada = alvo.grants.map((g) => g.organizationId).find((o): o is string => o !== null && gerenciaveis.includes(o));
  return vinculada ?? gerenciaveis[0] ?? null;
}

/** A mensagem de um erro das operações de usuário: 409 específicos, 403 e 404 explicados; o resto cai na descrição geral. */
export function mensagemDeErroDeUsuario(erro: unknown): string {
  if (erro instanceof ApiError) {
    if (erro.status === 409) {
      if (erro.code === 'registration-disabled') {
        return 'O cadastro de novos usuários está desabilitado para esta empresa; solicite a habilitação à BImeta.';
      }
      if (erro.code === 'user-limit-reached') {
        const { limit, used } = erro.numeros;
        return `Limite de usuários atingido${limit !== undefined && used !== undefined ? ` (${used} de ${limit} vagas em uso)` : ''}. Solicite a ampliação do limite à BImeta.`;
      }
      if (erro.code === 'last-administrator') {
        return 'Esta operação deixaria a empresa sem administrador ativo.';
      }
    }
    if (erro.status === 403) {
      return 'Você não tem permissão para esta operação.';
    }
    if (erro.status === 404) {
      return 'Não encontrado: o usuário ou a organização não existe ou está fora do seu alcance.';
    }
  }
  return descreverErro(erro);
}

/** A validade de um token, para exibir. */
export function validadeDoToken(expiresAt: string): string {
  const data = new Date(expiresAt);
  return Number.isNaN(data.getTime()) ? expiresAt : data.toLocaleString('pt-BR');
}
