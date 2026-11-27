import type { CompanyStatus, OperatorStatus, PlatformMe } from './types';

export const POR_PAGINA = 20;

/** O operador tem a permissão (a autoridade final é sempre a API; isto só decide o que mostrar). */
export function temPermissao(me: PlatformMe | null, permissao: string): boolean {
  return !!me && me.permissions.some((p) => p.permission === permissao);
}

export const SLUG_VALIDO = /^[a-z0-9]+(-[a-z0-9]+)*$/;

export function validarSlug(slug: string): string | null {
  if (slug.length < 3 || slug.length > 40) {
    return 'O identificador técnico deve ter de 3 a 40 caracteres.';
  }
  if (!SLUG_VALIDO.test(slug)) {
    return 'Use apenas letras minúsculas, números e hífens (sem hífen no início, no fim ou repetido).';
  }
  return null;
}

export const ROTULO_SITUACAO_EMPRESA: Record<CompanyStatus, string> = {
  Active: 'Ativa',
  Suspended: 'Suspensa',
  Provisioning: 'Aguardando provisionamento',
};

export const ROTULO_SITUACAO_OPERADOR: Record<OperatorStatus, string> = {
  Active: 'Ativo',
  Pending: 'Aguardando primeiro acesso',
  Expired: 'Primeiro acesso expirado',
  Inactive: 'Inativo',
  Cancelled: 'Cancelado',
};

export const ROTULO_PERFIL_OPERADOR: Record<string, string> = {
  PlatformAdministrator: 'Administrador da plataforma',
  PlatformCommercial: 'Comercial',
  PlatformViewer: 'Consulta',
};

export function rotuloDoPerfilOperador(perfil: string): string {
  return ROTULO_PERFIL_OPERADOR[perfil] ?? perfil;
}

export const ROTULO_OPERACAO: Record<string, string> = {
  'platform.tenant.create': 'Empresa cadastrada',
  'platform.tenant.update': 'Dados da empresa alterados',
  'platform.tenant.suspend': 'Empresa suspensa',
  'platform.tenant.reactivate': 'Empresa reativada',
  'platform.tenant.limit': 'Limite de usuários alterado',
  'platform.tenant.registration': 'Cadastro de usuários alterado',
  'platform.tenant.first-administrator.reissue': 'Primeiro acesso reemitido',
  'platform.operator.create': 'Operador cadastrado',
  'platform.operator.update': 'Operador alterado',
  'platform.operator.deactivate': 'Operador desativado',
  'platform.operator.reactivate': 'Operador reativado',
  'platform.operator.role': 'Perfil do operador alterado',
  'platform.operator.permission': 'Permissão do operador alterada',
  'platform.operator.access': 'Acesso do operador emitido',
  'platform.operator.sessions.revoke': 'Sessões do operador revogadas',
};

export function rotuloDaOperacao(operacao: string): string | null {
  return ROTULO_OPERACAO[operacao] ?? null;
}

export const ROTULO_RESULTADO: Record<string, string> = { Success: 'Sucesso', Denied: 'Negado', Failed: 'Falhou' };

export function formatarData(valor: string | null | undefined): string {
  if (!valor) {
    return '-';
  }
  const data = new Date(valor);
  return Number.isNaN(data.getTime()) ? valor : data.toLocaleString('pt-BR');
}

/** O início e o fim (hora local) de um dia `AAAA-MM-DD`, em ISO 8601, para o filtro de período. */
export function inicioDoDia(dia: string): string | undefined {
  if (!dia) {
    return undefined;
  }
  const [a, m, d] = dia.split('-').map(Number);
  return new Date(a ?? 1970, (m ?? 1) - 1, d ?? 1, 0, 0, 0, 0).toISOString();
}

export function fimDoDia(dia: string): string | undefined {
  if (!dia) {
    return undefined;
  }
  const [a, m, d] = dia.split('-').map(Number);
  return new Date(a ?? 1970, (m ?? 1) - 1, d ?? 1, 23, 59, 59, 999).toISOString();
}
