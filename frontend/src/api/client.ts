import { chaveDoArquivo } from '../arquivos';
import { encerrarSessaoLocal, obterToken } from './session';
import type {
  ActionResult,
  AgentDefinition,
  AgentPresence,
  AgentStatus,
  ChangePasswordRequest,
  CloseRequest,
  Conversation,
  CurrentUser,
  FaqSuggestion,
  FaqSuggestionEdit,
  FaqSuggestionGeneration,
  FaqSuggestionKind,
  FaqSuggestionPage,
  FaqSuggestionStatus,
  HistoryOrder,
  IntegrationConnection,
  IntegrationConnectionRequest,
  IntegrationConnectionStats,
  IntegrationConnectionTest,
  IntegrationConnectionUpdate,
  IntegrationDefinition,
  IntegrationDefinitionRequest,
  IntegrationExecutionPage,
  IntegrationExecutionStatus,
  IntegrationParameterProblem,
  IntegrationResult,
  SaveWorkflowRequest,
  SaveWorkflowResult,
  SimulateWorkflowResult,
  SimulationScenario,
  FlowGraph,
  FlowProblem,
  WorkflowOperationOption,
  FlowActivation,
  FlowActivationRequest,
  FlowFunnelRow,
  FlowInstanceDetail,
  FlowInstancePage,
  FlowMetricRow,
  ListFlowInstancesParams,
  WorkflowSummary,
  ItemActionRequest,
  KnowledgeGapDetail,
  KnowledgeGapPage,
  KnowledgeGapPromotion,
  KnowledgeGapStatus,
  KnowledgeGapSummary,
  LoginRequest,
  LoginResponse,
  Message,
  Page,
  ProblemDetails,
  PromoteKnowledgeGapRequest,
  QueueItem,
  QueueItemOperationResult,
  QueueItemStatus,
  RedeemPasswordResetRequest,
  ReplyOptions,
  ReplyRequest,
  SemanticMetrics,
  SemanticSettings,
  SemanticSettingsRequest,
  SendingAlert,
  ServiceQueue,
  TemplateParameterError,
  TemplateReplyRequest,
} from './types';

/** Erro de rede ou resposta de erro da API. `status` 0 = a requisição nem chegou ao servidor. */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | null;
  /** Extensão `code` do ProblemDetails (AlreadyAssigned, OperationInProgress, ItemClosed... em 409; ContactCenterRule em 422). */
  readonly code: string | null;
  /** A resposta de erro veio SEM o corpo de problema da API (vazia ou que não é JSON): quem respondeu foi um proxy, não a API. */
  readonly semCorpo: boolean;
  /** Os problemas de parâmetro de um modelo de mensagem (422 `TemplateParametersInvalid`); vazio nos demais erros. */
  readonly erros: TemplateParameterError[];
  /** O motivo curto de uma recusa de arquivo (422 `MediaRejected`: `too-large`, `type-not-allowed`, `content-mismatch`...); nulo nos demais erros. */
  readonly motivo: string | null;
  /** Os problemas de validação do motor de integração (400 `problems`): uma frase fixa por campo, nunca o valor digitado; vazio nos demais erros. */
  readonly problemas: string[];

  constructor(
    status: number,
    title: string,
    detail: string | null = null,
    code: string | null = null,
    semCorpo = false,
    erros: TemplateParameterError[] = [],
    motivo: string | null = null,
    problemas: string[] = [],
  ) {
    super(detail ?? title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.code = code;
    this.semCorpo = semCorpo;
    this.erros = erros;
    this.motivo = motivo;
    this.problemas = problemas;
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

export interface ListKnowledgeGapsParams {
  /** Ausente = todas as situações. */
  status?: KnowledgeGapStatus;
  take?: number;
  skip?: number;
}

export interface ListFaqSuggestionsParams {
  status?: FaqSuggestionStatus;
  kind?: FaqSuggestionKind;
  take?: number;
  skip?: number;
}

export interface ListExecutionsParams {
  connectionId?: string;
  key?: string;
  status?: IntegrationExecutionStatus;
  isTest?: boolean;
  take?: number;
  skip?: number;
}

export interface MessagesParams {
  take?: number;
  order?: HistoryOrder;
  /** O `nextCursor` da página anterior (opaco): pede a página seguinte, em NewestFirst a das mensagens mais antigas. */
  cursor?: string;
}

// Respostas que provam que a operação NÃO produziu efeito: o próximo pedido pode usar outro requestId. O 413 (arquivo grande demais para o pedido) é
// recusado antes de o servidor processar qualquer coisa.
const REJEICOES_DEFINITIVAS = new Set([400, 403, 404, 413, 422]);

/** O erro prova que a operação não teve efeito (o pedido pode ser refeito com outro requestId); qualquer outro é resultado incerto. */
export function ehRejeicaoDefinitiva(erro: unknown): boolean {
  return erro instanceof ApiError && (REJEICOES_DEFINITIVAS.has(erro.status) || (erro.status === 409 && erro.code === 'RequestIdReused'));
}

export function createApi(opcoes: ApiOptions = {}) {
  const baseUrl = opcoes.baseUrl ?? '/api';

  // requestId por ação. A MESMA ação repetida (mesma chave) reaproveita o GUID até dar certo ou ser rejeitada de vez;
  // assim, um duplo clique, uma resposta 202 ou uma falha de rede nunca cria dois efeitos no backend.
  const idsPendentes = new Map<string, string>();

  async function requisitar<T>(
    metodo: string,
    caminho: string,
    opts: { corpo?: unknown; formulario?: FormData; consulta?: Record<string, string | number | boolean | undefined>; semSessao?: boolean } = {},
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
    let corpo: string | FormData | undefined;
    if (opts.formulario !== undefined) {
      // multipart/form-data: o Content-Type NÃO é definido aqui, porque só o navegador sabe o boundary que separa os campos.
      corpo = opts.formulario;
    } else if (opts.corpo !== undefined) {
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

  /**
   * O arquivo de uma mensagem (ADR 0028), como Blob: a rota exige a sessão (cabeçalho Authorization, por isso não pode ser um `src` ou `href` direto) e o conteúdo
   * nunca tem URL pública. O tipo do Blob é o VERIFICADO pelo servidor.
   */
  async function baixarArquivo(caminho: string): Promise<Blob> {
    const cabecalhos: Record<string, string> = { Accept: '*/*' };
    const token = obterToken();
    if (token) {
      cabecalhos.Authorization = `Bearer ${token}`;
    }

    let resposta: Response;
    try {
      const executar = opcoes.fetchFn ?? ((...args: Parameters<typeof fetch>) => fetch(...args));
      resposta = await executar(`${baseUrl}${caminho}`, { method: 'GET', headers: cabecalhos });
    } catch {
      throw new ApiError(0, 'Sem conexão com a API', 'Não foi possível falar com o servidor. Confira se a API está no ar (http://localhost:5155) e tente de novo.');
    }

    if (resposta.ok) {
      return resposta.blob();
    }

    const erro = await lerErro(resposta);
    if (resposta.status === 401) {
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
    const erros = Array.isArray(problema?.errors)
      ? problema.errors.filter((e): e is TemplateParameterError => !!e && typeof e.key === 'string' && typeof e.message === 'string')
      : [];
    const problemas = Array.isArray(problema?.problems) ? problema.problems.filter((p): p is string => typeof p === 'string') : [];
    return new ApiError(status, titulo, detalhe, typeof problema?.code === 'string' ? problema.code : null, problema === null, erros, typeof problema?.reason === 'string' ? problema.reason : null, problemas);
  }

  /**
   * Executa uma ação com efeito, com o requestId guardado por chave (ver `idsPendentes`). `sugerido` é o requestId de um
   * envio anterior que a tela guardou (um recarregamento perdeu a memória): reaproveitá-lo faz o servidor reconhecer o
   * mesmo pedido em vez de criar um segundo efeito.
   */
  async function acao<T extends { outcome?: string }>(chave: string, executar: (requestId: string) => Promise<T>, sugerido?: string): Promise<T> {
    const requestId = idsPendentes.get(chave) ?? sugerido ?? novoGuid();
    idsPendentes.set(chave, requestId);
    try {
      const resultado = await executar(requestId);
      if (resultado?.outcome !== 'Pending') {
        idsPendentes.delete(chave);
      }
      return resultado;
    } catch (erro) {
      if (ehRejeicaoDefinitiva(erro)) {
        idsPendentes.delete(chave);
      }
      throw erro;
    }
  }

  const rotaItem = (itemId: string) => `/contact-center/items/${encodeURIComponent(itemId)}`;
  const rotaLacuna = (lacunaId: string) => `/agents/knowledge-gaps/${encodeURIComponent(lacunaId)}`;
  const rotaConexao = (id: string) => `/capabilities/connections/${encodeURIComponent(id)}`;
  const rotaOperacao = (id: string) => `/capabilities/definitions/${encodeURIComponent(id)}`;

  /** Uma versão da definição que pode não existir: o 404 do servidor ("não há rascunho" ou "não há publicada") vira `null`; qualquer outro erro continua erro. */
  async function versaoOuNula(caminho: string): Promise<AgentDefinition | null> {
    try {
      return await requisitar<AgentDefinition>('GET', caminho);
    } catch (erro) {
      if (erro instanceof ApiError && erro.status === 404) {
        return null;
      }
      throw erro;
    }
  }

  return {
    // ---- Identity
    login: (pedido: LoginRequest) => requisitar<LoginResponse>('POST', '/auth/login', { corpo: pedido, semSessao: true }),
    me: () => requisitar<CurrentUser>('GET', '/auth/me'),
    logout: () => requisitar<void>('POST', '/auth/logout'),
    // Troca da própria senha (exige a atual; as outras sessões caem e esta continua) e uso do token de redefinição (anônimo; todas as sessões da pessoa caem).
    trocarSenha: (pedido: ChangePasswordRequest) => requisitar<void>('PUT', '/auth/me/password', { corpo: pedido }),
    redefinirSenha: (pedido: RedeemPasswordResetRequest) => requisitar<void>('POST', '/auth/password-reset/redeem', { corpo: pedido, semSessao: true }),

    // ---- Fila
    listarMinhasFilas: () => requisitar<ServiceQueue[]>('GET', '/contact-center/me/queues'),
    listarItens: (p: ListItemsParams = {}) =>
      requisitar<QueueItem[]>('GET', '/contact-center/items', {
        consulta: { status: p.status, assignedToMe: p.assignedToMe ? true : undefined, queueId: p.queueId, take: p.take },
      }),
    obterItem: (itemId: string) => requisitar<QueueItem>('GET', rotaItem(itemId)),
    obterConversa: (itemId: string) => requisitar<Conversation>('GET', `${rotaItem(itemId)}/conversation`),
    obterMensagens: (itemId: string, p: MessagesParams = {}) =>
      requisitar<Page<Message>>('GET', `${rotaItem(itemId)}/messages`, { consulta: { take: p.take, order: p.order, cursor: p.cursor } }),

    // ---- Arquivo de uma mensagem de mídia (imagem, áudio ou documento): só do atendimento que o atendente pode ler
    obterAnexo: (itemId: string, messageId: string) => baixarArquivo(`${rotaItem(itemId)}/messages/${encodeURIComponent(messageId)}/attachment`),

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
    responder: (itemId: string, texto: string, requestIdSugerido?: string) =>
      acao<ActionResult>(
        `reply|${itemId}|${texto}`,
        (requestId) => requisitar<ActionResult>('POST', `${rotaItem(itemId)}/reply`, { corpo: { requestId, text: texto } satisfies ReplyRequest }),
        requestIdSugerido,
      ),
    // ---- Resposta por modelo de mensagem aprovado (ADR 0027): a janela de 24 horas, os modelos do número e o envio.
    // As opções são aviso secundário (quem chama não mostra erro delas): sem elas, a tela segue como antes (texto livre).
    obterOpcoesDeResposta: (itemId: string) => requisitar<ReplyOptions>('GET', `${rotaItem(itemId)}/reply-options`),
    // O requestId vale por (conversa, modelo, parâmetros): a MESMA ação repetida (rede que caiu) o reaproveita e o servidor reconhece o pedido.
    responderComModelo: (itemId: string, templateId: string, parametros: Record<string, string>) =>
      acao<ActionResult>(`reply-template|${itemId}|${templateId}|${JSON.stringify(Object.entries(parametros).sort(([a], [b]) => a.localeCompare(b)))}`, (requestId) =>
        requisitar<ActionResult>('POST', `${rotaItem(itemId)}/reply-template`, { corpo: { requestId, templateId, parameters: parametros } satisfies TemplateReplyRequest }),
      ),
    // ---- Arquivo do atendente (ADR 0031): imagem, documento ou áudio, no mesmo caminho da resposta de texto. Corpo multipart (`requestId`, `file` e `caption` só com legenda).
    // O requestId vale por (conversa, nome, tamanho, data de modificação, legenda): o MESMO arquivo repetido (rede que caiu) o reaproveita e o servidor reconhece o pedido.
    responderComArquivo: (itemId: string, arquivo: File, legenda: string | null, requestIdSugerido?: string) =>
      acao<ActionResult>(
        `reply-media|${itemId}|${chaveDoArquivo(arquivo, legenda)}`,
        (requestId) => {
          const formulario = new FormData();
          formulario.append('requestId', requestId);
          formulario.append('file', arquivo, arquivo.name);
          if (legenda) {
            formulario.append('caption', legenda);
          }
          return requisitar<ActionResult>('POST', `${rotaItem(itemId)}/reply-media`, { formulario });
        },
        requestIdSugerido,
      ),
    encerrar: (itemId: string, reasonCode = 'resolved') =>
      acao<ActionResult>(`close|${itemId}|${reasonCode}`, (requestId) =>
        requisitar<ActionResult>('POST', `${rotaItem(itemId)}/close`, { corpo: { requestId, reasonCode } satisfies CloseRequest }),
      ),

    // ---- Conhecimento do agente (ADR 0030): só o administrador do tenant (os demais recebem 403). Tenant e usuário vêm da sessão, nunca de parâmetro.
    // Lacunas do FAQ: perguntas reais de clientes que o FAQ não respondeu direto. Decidir (descartar ou enviar ao rascunho) é decisão do servidor: não há requestId.
    listarLacunasDoFaq: (p: ListKnowledgeGapsParams = {}) =>
      requisitar<KnowledgeGapPage>('GET', '/agents/knowledge-gaps', { consulta: { status: p.status, take: p.take, skip: p.skip } }),
    obterLacunaDoFaq: (lacunaId: string) => requisitar<KnowledgeGapDetail>('GET', rotaLacuna(lacunaId)),
    descartarLacunaDoFaq: (lacunaId: string) => requisitar<KnowledgeGapSummary>('POST', `${rotaLacuna(lacunaId)}/dismiss`),
    enviarLacunaAoRascunho: (lacunaId: string, entrada: PromoteKnowledgeGapRequest) =>
      requisitar<KnowledgeGapPromotion>('POST', `${rotaLacuna(lacunaId)}/promote`, { corpo: entrada }),
    // Definição do agente: o rascunho e a publicada (`null` quando não existem). Publicar é uma ação explícita, à parte.
    obterRascunhoDoAgente: () => versaoOuNula('/agents/definition/draft'),
    obterPublicadaDoAgente: () => versaoOuNula('/agents/definition/published'),
    publicarRascunhoDoAgente: () => requisitar<AgentDefinition>('POST', '/agents/definition/publish'),

    // ---- Conhecimento híbrido (ADR 0034): configuração e métricas da resposta por similaridade e as sugestões da IA local. A IA só propõe; aceitar leva ao RASCUNHO.
    obterConfiguracaoSemantica: () => requisitar<SemanticSettings>('GET', '/agents/semantic/settings'),
    salvarConfiguracaoSemantica: (pedido: SemanticSettingsRequest) => requisitar<SemanticSettings>('PUT', '/agents/semantic/settings', { corpo: pedido }),
    obterMetricasSemanticas: (dias = 7) => requisitar<SemanticMetrics>('GET', '/agents/semantic/metrics', { consulta: { days: dias } }),
    listarSugestoesDaIa: (p: ListFaqSuggestionsParams = {}) =>
      requisitar<FaqSuggestionPage>('GET', '/agents/faq-suggestions', { consulta: { status: p.status, kind: p.kind, take: p.take, skip: p.skip } }),
    gerarSugestaoDaLacuna: (lacunaId: string) => requisitar<FaqSuggestionGeneration>('POST', `/agents/faq-suggestions/generate/gap/${encodeURIComponent(lacunaId)}`),
    agruparLacunasComIa: () => requisitar<FaqSuggestionGeneration>('POST', '/agents/faq-suggestions/generate/cluster'),
    analisarFaqComIa: () => requisitar<FaqSuggestionGeneration>('POST', '/agents/faq-suggestions/generate/analysis'),
    editarSugestaoDaIa: (id: string, edicao: FaqSuggestionEdit) => requisitar<FaqSuggestion>('PUT', `/agents/faq-suggestions/${encodeURIComponent(id)}`, { corpo: edicao }),
    aceitarSugestaoDaIa: (id: string) => requisitar<FaqSuggestion>('POST', `/agents/faq-suggestions/${encodeURIComponent(id)}/approve`),
    rejeitarSugestaoDaIa: (id: string) => requisitar<FaqSuggestion>('POST', `/agents/faq-suggestions/${encodeURIComponent(id)}/reject`),

    // ---- Motor de Integração Empresarial (ADR 0035). Tenant e usuário vêm da sessão, nunca de parâmetro. O segredo de uma conexão só ENTRA (definirSegredoDaConexao) e nunca volta por nenhuma resposta.
    listarConexoes: () => requisitar<IntegrationConnection[]>('GET', '/capabilities/connections'),
    criarConexao: (pedido: IntegrationConnectionRequest) => requisitar<IntegrationConnection>('POST', '/capabilities/connections', { corpo: pedido }),
    atualizarConexao: (id: string, pedido: IntegrationConnectionUpdate) => requisitar<IntegrationConnection>('PUT', rotaConexao(id), { corpo: pedido }),
    definirSegredoDaConexao: (id: string, valores: Record<string, string>, revisaoEsperada: number) =>
      requisitar<IntegrationConnection>('PUT', `${rotaConexao(id)}/secret`, { corpo: { values: valores, expectedRevision: revisaoEsperada } }),
    habilitarConexao: (id: string) => requisitar<IntegrationConnection>('POST', `${rotaConexao(id)}/enable`),
    desabilitarConexao: (id: string) => requisitar<IntegrationConnection>('POST', `${rotaConexao(id)}/disable`),
    testarConexao: (id: string) => requisitar<IntegrationConnectionTest>('POST', `${rotaConexao(id)}/test`),
    listarOperacoes: (conexaoId?: string) => requisitar<IntegrationDefinition[]>('GET', '/capabilities/definitions', { consulta: { connectionId: conexaoId } }),
    versoesDaOperacao: (chave: string) => requisitar<IntegrationDefinition[]>('GET', `/capabilities/definitions/by-key/${encodeURIComponent(chave)}/versions`),
    salvarRascunhoDaOperacao: (pedido: IntegrationDefinitionRequest) => requisitar<IntegrationDefinition>('POST', '/capabilities/definitions', { corpo: pedido }),
    publicarOperacao: (id: string) => requisitar<IntegrationDefinition>('POST', `${rotaOperacao(id)}/publish`),
    aposentarOperacao: (id: string) => requisitar<IntegrationDefinition>('POST', `${rotaOperacao(id)}/retire`),
    ligarOperacao: (id: string) => requisitar<IntegrationDefinition>('POST', `${rotaOperacao(id)}/enable`),
    desligarOperacao: (id: string) => requisitar<IntegrationDefinition>('POST', `${rotaOperacao(id)}/disable`),
    testarOperacao: (id: string, parametros: Record<string, unknown>) => requisitar<IntegrationResult>('POST', `${rotaOperacao(id)}/test`, { corpo: { parameters: parametros } }),
    validarParametrosDaOperacao: (id: string, parametros: Record<string, unknown>) =>
      requisitar<{ valid: boolean; problems: IntegrationParameterProblem[] }>('POST', `${rotaOperacao(id)}/validate`, { corpo: { parameters: parametros } }),
    listarExecucoes: (p: ListExecutionsParams = {}) =>
      requisitar<IntegrationExecutionPage>('GET', '/capabilities/executions', { consulta: { connectionId: p.connectionId, key: p.key, status: p.status, isTest: p.isTest, take: p.take, skip: p.skip } }),
    resumoDasExecucoes: (dias = 7) => requisitar<IntegrationConnectionStats[]>('GET', '/capabilities/executions/stats', { consulta: { days: dias } }),
    confirmarExecucao: (id: string) => requisitar<IntegrationResult>('POST', `/capabilities/executions/${encodeURIComponent(id)}/confirm`),
    cancelarExecucao: (id: string) => requisitar<IntegrationResult>('POST', `/capabilities/executions/${encodeURIComponent(id)}/cancel`),

    // ---- Chatbot Visual (ADR 0037). O tenant vem da sessão; a organização do fluxo vai no corpo e é conferida no servidor. O grafo é o documento que o servidor valida de forma estrita.
    listarFluxos: () => requisitar<WorkflowSummary[]>('GET', '/workflows/definitions'),
    obterFluxo: (id: string) => requisitar<WorkflowSummary>('GET', `/workflows/definitions/${encodeURIComponent(id)}`),
    versoesDoFluxo: (chave: string) => requisitar<WorkflowSummary[]>('GET', `/workflows/definitions/by-key/${encodeURIComponent(chave)}/versions`),
    salvarFluxo: (pedido: SaveWorkflowRequest) =>
      requisitar<SaveWorkflowResult>('POST', '/workflows/definitions', {
        corpo: { organizationId: pedido.organizationId, key: pedido.key, name: pedido.name, description: pedido.description, graph: pedido.graph, expectedRevision: pedido.expectedRevision },
      }),
    publicarFluxo: (id: string) => requisitar<WorkflowSummary>('POST', `/workflows/definitions/${encodeURIComponent(id)}/publish`),
    aposentarFluxo: (id: string) => requisitar<WorkflowSummary>('POST', `/workflows/definitions/${encodeURIComponent(id)}/retire`),
    validarFluxo: (organizationId: string, grafo: FlowGraph) =>
      requisitar<{ valid: boolean; problems: FlowProblem[] }>('POST', '/workflows/validate', { corpo: { organizationId, graph: grafo } }),
    simularFluxo: (organizationId: string, grafo: FlowGraph, cenario: SimulationScenario) =>
      requisitar<SimulateWorkflowResult>('POST', '/workflows/simulate', { corpo: { organizationId, graph: grafo, scenario: cenario } }),
    operacoesParaFluxos: (organizationId: string) => requisitar<WorkflowOperationOption[]>('GET', '/workflows/operations', { consulta: { organizationId } }),
    listarAtivacoesDeFluxo: () => requisitar<FlowActivation[]>('GET', '/workflows/activations'),
    criarAtivacaoDeFluxo: (pedido: FlowActivationRequest) => requisitar<FlowActivation>('POST', '/workflows/activations', { corpo: pedido }),
    atualizarAtivacaoDeFluxo: (id: string, pedido: FlowActivationRequest) => requisitar<FlowActivation>('PUT', `/workflows/activations/${encodeURIComponent(id)}`, { corpo: pedido }),
    ligarAtivacaoDeFluxo: (id: string) => requisitar<FlowActivation>('POST', `/workflows/activations/${encodeURIComponent(id)}/enable`),
    desligarAtivacaoDeFluxo: (id: string) => requisitar<FlowActivation>('POST', `/workflows/activations/${encodeURIComponent(id)}/disable`),
    listarInstanciasDeFluxo: (p: ListFlowInstancesParams = {}) =>
      requisitar<FlowInstancePage>('GET', '/workflows/instances', { consulta: { status: p.status, key: p.key, take: p.take, skip: p.skip } }),
    detalheDaInstanciaDeFluxo: (id: string) => requisitar<FlowInstanceDetail>('GET', `/workflows/instances/${encodeURIComponent(id)}`),
    metricasDosFluxos: (dias = 7) => requisitar<FlowMetricRow[]>('GET', '/workflows/metrics', { consulta: { days: dias } }),
    funilDoFluxo: (chave: string, versao: number, dias = 7) => requisitar<FlowFunnelRow[]>('GET', '/workflows/metrics/funnel', { consulta: { key: chave, version: versao, days: dias } }),
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
