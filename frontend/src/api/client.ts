import { encerrarSessaoLocal, obterToken } from './session';
import type {
  ActionResult,
  AgentPresence,
  AgentStatus,
  CloseRequest,
  Conversation,
  CurrentUser,
  HistoryOrder,
  ItemActionRequest,
  LoginRequest,
  LoginResponse,
  Message,
  Page,
  ProblemDetails,
  QueueItem,
  QueueItemOperationResult,
  QueueItemStatus,
  ReplyRequest,
  SendingAlert,
  ServiceQueue,
} from './types';

/** Erro de rede ou resposta de erro da API. `status` 0 = a requisição nem chegou ao servidor. */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | null;
  /** Extensão `code` do ProblemDetails (QueueItemConflict em 409, ContactCenterRule em 422). */
  readonly code: string | null;
  /** A resposta de erro veio SEM o corpo de problema da API (vazia ou que não é JSON): quem respondeu foi um proxy, não a API. */
  readonly semCorpo: boolean;

  constructor(status: number, title: string, detail: string | null = null, code: string | null = null, semCorpo = false) {
    super(detail ?? title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.code = code;
    this.semCorpo = semCorpo;
  }
}

/**
 * O erro é de CONEXÃO (a API não foi alcançada: rede caída, servidor desligado ou o proxy sem resposta), e não uma
 * resposta da API. A tela trata como "sem conexão": mostra a faixa de reconexão e continua tentando sozinha.
 */
export function ehFalhaDeConexao(erro: unknown): boolean {
  if (!(erro instanceof ApiError)) {
    return false;
  }
  // 502, 503 e 504 são o proxy dizendo que não alcançou a API; o proxy do Vite com a API desligada responde 500 vazio.
  // Um 500 COM corpo de problema é erro da própria API (ela está no ar): não é falta de conexão.
  return erro.status === 0 || erro.status === 502 || erro.status === 503 || erro.status === 504 || (erro.status >= 500 && erro.semCorpo);
}

/** Texto para a faixa de erro: título e detalhe quando houver, sem repetir. */
export function descreverErro(erro: unknown): string {
  if (erro instanceof ApiError) {
    return erro.detail && erro.detail !== erro.title ? `${erro.title}: ${erro.detail}` : erro.title;
  }
  return erro instanceof Error ? erro.message : 'Erro inesperado.';
}

export function novoGuid(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export interface ApiOptions {
  /** Prefixo das rotas. O proxy do Vite troca `/api` por http://localhost:5155. */
  baseUrl?: string;
  /** Para testes; por padrão usa o `fetch` global (resolvido a cada chamada). */
  fetchFn?: typeof fetch;
}

export interface ListItemsParams {
  status?: QueueItemStatus;
  assignedToMe?: boolean;
  queueId?: string;
  take?: number;
}

export interface MessagesParams {
  take?: number;
  order?: HistoryOrder;
}

// Respostas que provam que a operação NÃO produziu efeito: o próximo pedido pode usar outro requestId.
const REJEICOES_DEFINITIVAS = new Set([400, 403, 404, 422]);

export function createApi(opcoes: ApiOptions = {}) {
  const baseUrl = opcoes.baseUrl ?? '/api';

  // requestId por ação. A MESMA ação repetida (mesma chave) reaproveita o GUID até dar certo ou ser rejeitada de vez;
  // assim, um duplo clique, uma resposta 202 ou uma falha de rede nunca cria dois efeitos no backend.
  const idsPendentes = new Map<string, string>();

  async function requisitar<T>(
    metodo: string,
    caminho: string,
    opts: { corpo?: unknown; consulta?: Record<string, string | number | boolean | undefined>; semSessao?: boolean } = {},
  ): Promise<T> {
    const parametros = new URLSearchParams();
    for (const [nome, valor] of Object.entries(opts.consulta ?? {})) {
      if (valor !== undefined && valor !== '') {
        parametros.set(nome, String(valor));
      }
    }
    const consulta = parametros.toString();
    const url = `${baseUrl}${caminho}${consulta ? `?${consulta}` : ''}`;

    const cabecalhos: Record<string, string> = { Accept: 'application/json' };
    const token = obterToken();
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
      throw new ApiError(
        0,
        'Sem conexão com a API',
        'Não foi possível falar com o servidor. Confira se a API está no ar (http://localhost:5155) e tente de novo.',
      );
    }

    if (resposta.ok) {
      if (resposta.status === 204) {
        return undefined as T;
      }
      const texto = await resposta.text();
      return (texto ? JSON.parse(texto) : undefined) as T;
    }

    const erro = await lerErro(resposta);
    // 401 numa chamada autenticada = sessão inválida ou vencida: sai (o App volta à entrada). No login, 401 = credenciais erradas.
    if (resposta.status === 401 && !opts.semSessao) {
      encerrarSessaoLocal();
    }
    throw erro;
  }

  async function lerErro(resposta: Response): Promise<ApiError> {
    let problema: ProblemDetails | null = null;
    try {
      const texto = await resposta.text();
      if (texto) {
        const lido: unknown = JSON.parse(texto);
        if (lido && typeof lido === 'object') {
          problema = lido as ProblemDetails;
        }
      }
    } catch {
      // corpo vazio ou que não é JSON (o proxy do Vite responde 500 vazio com a API desligada)
    }

    const status = resposta.status;
    const titulo = problema?.title ?? tituloPadrao(status);
    const detalhe = problema?.detail ?? (problema ? null : detalhePadrao(status));
    return new ApiError(status, titulo, detalhe, typeof problema?.code === 'string' ? problema.code : null, problema === null);
  }

  /** Executa uma ação com efeito, com o requestId guardado por chave (ver `idsPendentes`). */
  async function acao<T extends { outcome?: string }>(chave: string, executar: (requestId: string) => Promise<T>): Promise<T> {
    const requestId = idsPendentes.get(chave) ?? novoGuid();
    idsPendentes.set(chave, requestId);
    try {
      const resultado = await executar(requestId);
      if (resultado?.outcome !== 'Pending') {
        idsPendentes.delete(chave);
      }
      return resultado;
    } catch (erro) {
      if (erro instanceof ApiError && (REJEICOES_DEFINITIVAS.has(erro.status) || (erro.status === 409 && erro.code === 'RequestIdReused'))) {
        idsPendentes.delete(chave);
      }
      throw erro;
    }
  }

  const rotaItem = (itemId: string) => `/contact-center/items/${encodeURIComponent(itemId)}`;

  return {
    // ---- Identity
    login: (pedido: LoginRequest) => requisitar<LoginResponse>('POST', '/auth/login', { corpo: pedido, semSessao: true }),
    me: () => requisitar<CurrentUser>('GET', '/auth/me'),
    logout: () => requisitar<void>('POST', '/auth/logout'),

    // ---- Fila
    listarMinhasFilas: () => requisitar<ServiceQueue[]>('GET', '/contact-center/me/queues'),
    listarItens: (p: ListItemsParams = {}) =>
      requisitar<QueueItem[]>('GET', '/contact-center/items', {
        consulta: { status: p.status, assignedToMe: p.assignedToMe ? true : undefined, queueId: p.queueId, take: p.take },
      }),
    obterItem: (itemId: string) => requisitar<QueueItem>('GET', rotaItem(itemId)),
    obterConversa: (itemId: string) => requisitar<Conversation>('GET', `${rotaItem(itemId)}/conversation`),
    obterMensagens: (itemId: string, p: MessagesParams = {}) =>
      requisitar<Page<Message>>('GET', `${rotaItem(itemId)}/messages`, { consulta: { take: p.take, order: p.order } }),

    // ---- Canais: aviso de contas com envio pausado (aviso secundário: quem chama não mostra erro dele)
    listarAlertasDeEnvio: () => requisitar<SendingAlert[]>('GET', '/channels/sending-alerts'),

    // ---- Presença
    obterPresenca: () => requisitar<AgentPresence>('GET', '/contact-center/me/presence'),
    definirPresenca: (status: AgentStatus) => requisitar<AgentPresence>('PUT', '/contact-center/me/presence', { corpo: { status } }),

    // ---- Ações do atendente sobre o item (cada uma leva um requestId novo, reaproveitado se a MESMA ação se repetir)
    assumir: (itemId: string) =>
      acao(`claim|${itemId}`, (requestId) =>
        requisitar<QueueItemOperationResult>('POST', `${rotaItem(itemId)}/claim`, { corpo: { requestId } satisfies ItemActionRequest }),
      ),
    devolverParaFila: (itemId: string) =>
      acao(`release|${itemId}`, (requestId) =>
        requisitar<QueueItemOperationResult>('POST', `${rotaItem(itemId)}/release`, { corpo: { requestId } satisfies ItemActionRequest }),
      ),
    devolverParaIa: (itemId: string) =>
      acao(`return-to-ai|${itemId}`, (requestId) =>
        requisitar<QueueItemOperationResult>('POST', `${rotaItem(itemId)}/return-to-ai`, { corpo: { requestId } satisfies ItemActionRequest }),
      ),
    // Rotas novas do backend (suposição documentada em types.ts). A resposta repetida do MESMO texto reusa o requestId.
    responder: (itemId: string, texto: string) =>
      acao<ActionResult>(`reply|${itemId}|${texto}`, (requestId) =>
        requisitar<ActionResult>('POST', `${rotaItem(itemId)}/reply`, { corpo: { requestId, text: texto } satisfies ReplyRequest }),
      ),
    encerrar: (itemId: string, reasonCode = 'resolved') =>
      acao<ActionResult>(`close|${itemId}|${reasonCode}`, (requestId) =>
        requisitar<ActionResult>('POST', `${rotaItem(itemId)}/close`, { corpo: { requestId, reasonCode } satisfies CloseRequest }),
      ),
  };
}

export type ApiClient = ReturnType<typeof createApi>;

function tituloPadrao(status: number): string {
  switch (status) {
    case 400:
      return 'Dados inválidos';
    case 401:
      return 'Autenticação necessária';
    case 403:
      return 'Acesso negado';
    case 404:
      return 'Não encontrado';
    case 409:
      return 'Conflito';
    case 422:
      return 'Regra não atendida';
    case 429:
      return 'Muitas tentativas';
    case 502:
    case 503:
    case 504:
      // Sem corpo, é o proxy do Vite dizendo que não alcançou a API.
      return 'API indisponível';
    default:
      return status >= 500 ? `Erro no servidor (HTTP ${status})` : `Erro HTTP ${status}`;
  }
}

function detalhePadrao(status: number): string | null {
  if (status === 404) {
    return 'O recurso não existe ou está fora do seu alcance.';
  }
  if (status === 502 || status === 503 || status === 504) {
    return 'Não foi possível falar com a API (http://localhost:5155). Se ela estiver desligada, inicie-a e tente de novo.';
  }
  if (status >= 500) {
    return 'A API não respondeu como esperado. Se ela estiver desligada, inicie-a e tente de novo.';
  }
  return null;
}
