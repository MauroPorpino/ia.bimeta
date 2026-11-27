import { ApiError } from '../api/client';
import type {
  AuditFilters,
  AuditItem,
  CompanyDetail,
  CompanyListItem,
  CreateCompanyRequest,
  CreateCompanyResult,
  FirstAccess,
  OperatorDetail,
  OperatorItem,
  OperatorRole,
  OperatorToken,
  Overview,
  Pagina,
  PlatformMe,
  PlatformSession,
  RoleInfo,
  StatusChangeRequest,
  UpdateCompanyRequest,
} from './types';

// Sessão do painel da plataforma: INDEPENDENTE da sessão das empresas (chave e estado próprios).
// O token vive só em memória e no sessionStorage (some ao fechar a aba); nunca em localStorage, URL ou log.
export const CHAVE_SESSAO_PLATAFORMA = 'ia-bimeta-plataforma-sessao';
export const TENANT_DA_PLATAFORMA = 'bimeta-platform';

let token: string | null = null;
const ouvintes = new Set<() => void>();

function guardar(valor: PlatformSession | null): void {
  try {
    if (valor) {
      sessionStorage.setItem(CHAVE_SESSAO_PLATAFORMA, JSON.stringify(valor));
    } else {
      sessionStorage.removeItem(CHAVE_SESSAO_PLATAFORMA);
    }
  } catch {
    // sessionStorage indisponível: segue só em memória.
  }
}

export function restaurarSessaoPlataforma(): string | null {
  if (token) {
    return token;
  }
  try {
    const bruto = sessionStorage.getItem(CHAVE_SESSAO_PLATAFORMA);
    if (!bruto) {
      return null;
    }
    const lido = JSON.parse(bruto) as Partial<PlatformSession>;
    if (typeof lido.token !== 'string' || typeof lido.expiresAt !== 'string' || Date.parse(lido.expiresAt) <= Date.now()) {
      guardar(null);
      return null;
    }
    token = lido.token;
    return token;
  } catch {
    guardar(null);
    return null;
  }
}

export function definirSessaoPlataforma(novo: string, expiresAt: string): void {
  token = novo;
  guardar({ token: novo, expiresAt });
}

export function encerrarSessaoPlataforma(): void {
  const tinha = token !== null;
  token = null;
  guardar(null);
  if (tinha) {
    ouvintes.forEach((o) => o());
  }
}

export function aoSairDaPlataforma(ouvinte: () => void): () => void {
  ouvintes.add(ouvinte);
  return () => {
    ouvintes.delete(ouvinte);
  };
}

/** Só para testes. */
export function reiniciarSessaoPlataformaParaTeste(): void {
  token = null;
  ouvintes.clear();
  try {
    sessionStorage.removeItem(CHAVE_SESSAO_PLATAFORMA);
  } catch {
    // ignora
  }
}

export function descreverErroPlataforma(erro: unknown): string {
  if (erro instanceof ApiError) {
    if (erro.status === 403) {
      return erro.detail ? `Sem permissão: ${erro.detail}` : 'Sem permissão para esta operação.';
    }
    if (erro.status === 409 && erro.code === 'last-administrator') {
      return `Operação recusada: a plataforma precisa manter ao menos um administrador ativo.${erro.detail ? ` ${erro.detail}` : ''}`;
    }
    if (erro.status === 0) {
      return erro.detail ?? erro.title;
    }
    return erro.detail && erro.detail !== erro.title ? erro.detail : erro.title;
  }
  return erro instanceof Error ? erro.message : 'Erro inesperado.';
}

export interface PlataformaApiOptions {
  baseUrl?: string;
  fetchFn?: typeof fetch;
}

type Consulta = Record<string, string | number | boolean | undefined | null>;

export function createPlataformaApi(opcoes: PlataformaApiOptions = {}) {
  const baseUrl = opcoes.baseUrl ?? '/api';

  async function requisitar<T>(metodo: string, caminho: string, opts: { corpo?: unknown; consulta?: Consulta; semSessao?: boolean } = {}): Promise<T> {
    const parametros = new URLSearchParams();
    for (const [nome, valor] of Object.entries(opts.consulta ?? {})) {
      if (valor !== undefined && valor !== null && valor !== '') {
        parametros.set(nome, String(valor));
      }
    }
    const consulta = parametros.toString();
    const url = `${baseUrl}${caminho}${consulta ? `?${consulta}` : ''}`;
    const cabecalhos: Record<string, string> = { Accept: 'application/json' };
    if (token && !opts.semSessao) {
      cabecalhos.Authorization = `Bearer ${token}`;
    }
    let corpo: string | undefined;
    if (opts.corpo !== undefined) {
      cabecalhos['Content-Type'] = 'application/json';
      corpo = JSON.stringify(opts.corpo);
    }

    let resposta: Response;
    try {
      const executar = opcoes.fetchFn ?? ((...args: Parameters<typeof fetch>) => fetch(...args));
      resposta = await executar(url, { method: metodo, headers: cabecalhos, body: corpo });
    } catch {
      throw new ApiError(0, 'Sem conexão com a API', 'Não foi possível falar com o servidor. Confira se a API está no ar e tente de novo.');
    }

    if (resposta.ok) {
      if (resposta.status === 204) {
        return undefined as T;
      }
      const texto = await resposta.text();
      return (texto ? JSON.parse(texto) : undefined) as T;
    }

    let problema: { title?: string; detail?: string; code?: string } | null = null;
    try {
      const texto = await resposta.text();
      if (texto) {
        const lido: unknown = JSON.parse(texto);
        if (lido && typeof lido === 'object') {
          problema = lido as { title?: string; detail?: string; code?: string };
        }
      }
    } catch {
      // corpo vazio ou que não é JSON
    }
    const padrao = resposta.status === 401 ? 'Sessão expirada' : resposta.status === 403 ? 'Sem permissão' : `Erro ${resposta.status}`;
    const erro = new ApiError(
      resposta.status,
      typeof problema?.title === 'string' ? problema.title : padrao,
      typeof problema?.detail === 'string' ? problema.detail : null,
      typeof problema?.code === 'string' ? problema.code : null,
      problema === null,
    );
    if (resposta.status === 401 && !opts.semSessao) {
      encerrarSessaoPlataforma();
    }
    throw erro;
  }

  const op = (id: string) => `/platform/operators/${encodeURIComponent(id)}`;
  const emp = (slug: string) => `/platform/companies/${encodeURIComponent(slug)}`;

  return {
    login: (email: string, password: string) => requisitar<PlatformSession>('POST', '/platform/auth/login', { corpo: { email, password }, semSessao: true }),
    redimirPrimeiroAcesso: (email: string, tokenDeUso: string, newPassword: string) =>
      requisitar<void>('POST', '/auth/password-reset/redeem', { corpo: { tenant: TENANT_DA_PLATAFORMA, email, token: tokenDeUso, newPassword }, semSessao: true }),
    eu: () => requisitar<PlatformMe>('GET', '/auth/me'),
    logout: () => requisitar<void>('POST', '/auth/logout'),
    trocarSenha: (currentPassword: string, newPassword: string) => requisitar<void>('PUT', '/auth/me/password', { corpo: { currentPassword, newPassword } }),

    visaoGeral: () => requisitar<Overview>('GET', '/platform/overview'),

    listarEmpresas: (consulta: { search?: string; status?: string; page?: number; pageSize?: number }) =>
      requisitar<Pagina<CompanyListItem>>('GET', '/platform/companies', { consulta }),
    criarEmpresa: (pedido: CreateCompanyRequest) => requisitar<CreateCompanyResult>('POST', '/platform/companies', { corpo: pedido }),
    empresa: (slug: string) => requisitar<CompanyDetail>('GET', emp(slug)),
    atualizarEmpresa: (slug: string, pedido: UpdateCompanyRequest) => requisitar<CompanyDetail>('PUT', emp(slug), { corpo: pedido }),
    definirLimite: (slug: string, maxUsers: number) => requisitar<CompanyDetail>('PUT', `${emp(slug)}/quota`, { corpo: { maxUsers } }),
    definirCadastro: (slug: string, enabled: boolean) => requisitar<CompanyDetail>('PUT', `${emp(slug)}/enrollment`, { corpo: { enabled } }),
    reemitirPrimeiroAcesso: (slug: string) => requisitar<FirstAccess>('POST', `${emp(slug)}/first-administrator/reissue`),
    suspender: (slug: string, pedido: StatusChangeRequest) => requisitar<CompanyDetail>('POST', `${emp(slug)}/suspend`, { corpo: pedido }),
    reativar: (slug: string, pedido: StatusChangeRequest) => requisitar<CompanyDetail>('POST', `${emp(slug)}/reactivate`, { corpo: pedido }),

    listarOperadores: (consulta: { search?: string; status?: string; page?: number; pageSize?: number }) =>
      requisitar<Pagina<OperatorItem>>('GET', '/platform/operators', { consulta }),
    criarOperador: (pedido: { email: string; displayName: string; role: string }) => requisitar<OperatorToken>('POST', '/platform/operators', { corpo: pedido }),
    operador: (id: string) => requisitar<OperatorDetail>('GET', op(id)),
    atualizarOperador: (id: string, pedido: { email: string; displayName: string }) => requisitar<OperatorDetail>('PUT', op(id), { corpo: pedido }),
    emitirAcesso: (id: string) => requisitar<OperatorToken>('POST', `${op(id)}/access`),
    revogarSessoes: (id: string) => requisitar<void>('POST', `${op(id)}/sessions/revoke`),
    desativarOperador: (id: string) => requisitar<OperatorDetail>('POST', `${op(id)}/deactivate`),
    reativarOperador: (id: string) => requisitar<OperatorDetail>('POST', `${op(id)}/reactivate`),
    definirPerfil: (id: string, role: OperatorRole | string) => requisitar<OperatorDetail>('PUT', `${op(id)}/role`, { corpo: { role } }),
    definirPermissao: (id: string, permission: string, effect: 'Grant' | 'Deny') => requisitar<OperatorDetail>('PUT', `${op(id)}/permissions`, { corpo: { permission, effect } }),
    removerPermissao: (id: string, permission: string) => requisitar<OperatorDetail>('DELETE', `${op(id)}/permissions/${encodeURIComponent(permission)}`),
    restaurarPermissoes: (id: string) => requisitar<OperatorDetail>('POST', `${op(id)}/permissions/reset`),
    perfis: () => requisitar<RoleInfo[]>('GET', '/platform/roles'),

    /** `from` e `to` já em ISO 8601. */
    auditoria: (filtros: Partial<AuditFilters> & { page?: number; pageSize?: number }) => requisitar<Pagina<AuditItem>>('GET', '/platform/audit', { consulta: filtros }),
  };
}

export type PlataformaApi = ReturnType<typeof createPlataformaApi>;
