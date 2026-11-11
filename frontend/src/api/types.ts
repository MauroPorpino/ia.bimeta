// Tipos dos DTOs da API do ia.bimeta, lidos do código do backend (não gerados).
// O JSON usa camelCase e os enums vêm como texto (JsonStringEnumConverter no host).
//
// Fontes:
//  - Identity:      backend/modules/identity/IaBimeta.Identity.Presentation/IdentityDtos.cs
//  - ContactCenter: backend/modules/contact-center/IaBimeta.ContactCenter.Core/Application/ContactCenterViews.cs
//                   backend/modules/contact-center/IaBimeta.ContactCenter.Presentation/ContactCenterDtos.cs
//  - Conversations: backend/modules/conversations/IaBimeta.Conversations.Contracts/ConversationViews.cs e ConversationEnums.cs
//  - Agents:        backend/modules/agents/IaBimeta.Agents.Core/Application/KnowledgeGapService.cs e AgentDefinitionService.cs
//                   backend/modules/agents/IaBimeta.Agents.Presentation/AgentsEndpoints.cs

// ---------------------------------------------------------------- Identity

export interface LoginRequest {
  /** Slug do tenant: só escolhe onde procurar o usuário. */
  tenant: string;
  email: string;
  password: string;
}

/** Troca da PRÓPRIA senha (a atual é exigida). */
export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

/** Uso do token de redefinição emitido pelo administrador ou pelo operador (anônimo). */
export interface RedeemPasswordResetRequest {
  tenant: string;
  email: string;
  token: string;
  newPassword: string;
}

export interface LoginResponse {
  token: string;
  expiresAt: string;
}

export interface Grant {
  organizationId: string | null;
  role: string;
}

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  grants: Grant[];
  preferredLocale: string | null;
}

// ---------------------------------------------------------------- Erros (ProblemDetails)

/** Corpo de erro da API (RFC 9457). `code` é a extensão de 409/422 (409: AlreadyAssigned, OperationInProgress, ItemClosed...; 422: ContactCenterRule). */
export interface ProblemDetails {
  type?: string;
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  /** 422 `TemplateParametersInvalid`: todos os problemas dos parâmetros do modelo, por chave (`*` = do conjunto). */
  errors?: TemplateParameterError[];
  /** 422 `MediaRejected`: o motivo curto da recusa do arquivo do atendente (ver `MediaRejectionReason`); o texto em português vem em `detail`. */
  reason?: string;
  /** 400 do motor de integração (ADR 0035): os problemas de validação, uma frase fixa por campo (nome e regra, nunca o valor digitado). */
  problems?: string[];
}

// ---------------------------------------------------------------- ContactCenter

export type QueueItemStatus = 'Waiting' | 'Assigned' | 'Closed';

export type PendingKind = 'Enter' | 'Assign' | 'Release' | 'TransferQueue' | 'ReturnToAi';

export interface PendingOperation {
  kind: PendingKind;
  operationId: string;
  queueId: string;
  userId: string | null;
  attempts: number;
  lastError: string | null;
  startedAt: string;
}

/** Uma conversa na fila de atendimento (QueueItemView). */
export interface QueueItem {
  id: string;
  sessionId: string;
  organizationId: string;
  businessUnitId: string | null;
  departmentId: string | null;
  queueId: string;
  status: QueueItemStatus;
  assignedUserId: string | null;
  version: number;
  enteredAt: string;
  assignedAt: string | null;
  closedAt: string | null;
  closeReason: string | null;
  pending: PendingOperation | null;
  updatedAt: string;
  priority: number;
  routingRuleId: string | null;
  /** Mensagens de saída com falha definitiva de envio desde a última resposta do atendente (sinal da fila; 0 = nenhuma). */
  deliveryFailures: number;
  lastDeliveryFailureAt: string | null;
}

export type OperationOutcome = 'Completed' | 'AlreadyApplied' | 'Pending' | 'SessionEnded';

/** Resposta de claim, release e return-to-ai (200 aplicada, 202 quando `outcome` é Pending). */
export interface QueueItemOperationResult {
  item: QueueItem;
  outcome: OperationOutcome;
}

export type QueueStatus = 'Active' | 'Paused';

export interface ServiceQueue {
  id: string;
  organizationId: string;
  businessUnitId: string | null;
  departmentId: string | null;
  name: string;
  status: QueueStatus;
  version: number;
}

export type AgentStatus = 'Available' | 'Paused' | 'Offline';

export interface AgentPresence {
  userId: string;
  status: AgentStatus;
  updatedAt: string | null;
}

/** Corpo comum de claim, release e return-to-ai (ItemActionRequest). */
export interface ItemActionRequest {
  requestId: string;
  reason?: string | null;
}

/**
 * Corpo de POST /contact-center/items/{id}/reply. Rota NOVA: o backend ainda vai ganhá-la (suposição do front).
 * Sem tenant nem usuário no corpo: ambos vêm do contexto da sessão.
 */
export interface ReplyRequest {
  requestId: string;
  text: string;
}

/**
 * Corpo de POST /contact-center/items/{id}/close. Rota NOVA (suposição do front). `reasonCode` é um código curto,
 * sem texto livre (hoje o front só envia "resolved").
 */
export interface CloseRequest {
  requestId: string;
  reasonCode: string;
}

/**
 * Resposta 200 de reply e de close. Rotas novas: o front só depende do status HTTP e, se existir, de `outcome`
 * (`Pending` = ainda não confirmado, repetir com o mesmo requestId). Os demais campos são opcionais e informativos.
 */
export interface ActionResult {
  outcome?: OperationOutcome;
  item?: QueueItem;
  messageId?: string;
  /** A posição da mensagem no histórico (resposta de texto, de modelo e de arquivo). */
  messageSeq?: number;
}

// ---------------------------------------------------------------- Envio de arquivo pelo atendente (ADR 0031)

/**
 * Por que o servidor recusou o arquivo do atendente (422 `MediaRejected`, campo `reason`): vazio, grande demais, tipo fora da lista, conteúdo que não corresponde ao
 * tipo ou à extensão, codec de áudio não suportado (Ogg precisa ser Opus em mono), legenda não aceita (áudio), legenda longa demais, bloqueio da verificação de
 * segurança ou formato de pedido inválido. O servidor é a autoridade: descobre o tipo REAL pelo conteúdo.
 */
export type MediaRejectionReason =
  | 'empty'
  | 'too-large'
  | 'type-not-allowed'
  | 'content-mismatch'
  | 'audio-codec-unsupported'
  | 'caption-not-supported'
  | 'caption-too-long'
  | 'malware'
  | 'invalid-shape';

// ---------------------------------------------------------------- Resposta por modelo de mensagem (templates, ADR 0027)

/** Um parâmetro do modelo para o formulário: `key` é o que volta no envio (`body.1`, `body.nome`, `header.1`, `button.0`). */
export interface ReplyTemplateParameter {
  key: string;
  component: 'header' | 'body' | 'button';
  label: string;
  /** O exemplo que o administrador cadastrou na aprovação do modelo. */
  example: string | null;
  maxLength: number;
}

/** Um modelo aprovado que o número pode enviar. `bodyText` traz os marcadores (`{{1}}` ou `{{nome}}`) como a Meta o aprovou. */
export interface ReplyTemplate {
  id: string;
  name: string;
  language: string;
  category: string;
  headerText: string | null;
  bodyText: string;
  footerText: string | null;
  parameters: ReplyTemplateParameter[];
}

/**
 * GET /contact-center/items/{id}/reply-options: se o texto livre ainda é aceito (a janela de 24 horas do provedor, contada da
 * última mensagem do CLIENTE) e os modelos aprovados do número. `windowHours` nulo = o provedor não tem janela.
 */
export interface ReplyOptions {
  freeTextAllowed: boolean;
  windowOpen: boolean;
  windowHours: number | null;
  lastInboundAt: string | null;
  windowClosesAt: string | null;
  templates: ReplyTemplate[];
}

/** Corpo de POST /contact-center/items/{id}/reply-template. `parameters` por chave de parâmetro. */
export interface TemplateReplyRequest {
  requestId: string;
  templateId: string;
  parameters: Record<string, string>;
}

/** Um problema de parâmetro do modelo (a mensagem nunca repete o valor digitado). */
export interface TemplateParameterError {
  key: string;
  code: string;
  message: string;
}

// ---------------------------------------------------------------- Channels

/**
 * Conta de WhatsApp com o envio pausado ou esperando o provedor (GET /channels/sending-alerts, ADR 0022). Traz só o que
 * explica a demora de uma resposta: nunca o motivo técnico da pausa, a vazão nem credencial.
 */
export interface SendingAlert {
  accountId: string;
  accountName: string;
  paused: boolean;
  /** Enquanto não passar deste instante, o provedor mandou a conta esperar (limite de taxa). */
  cooldownUntil: string | null;
  /** Os números da conta (`Message.channelInstanceId`). */
  channelInstanceIds: string[];
}

// ---------------------------------------------------------------- Agents (lacunas do FAQ e definição do agente, ADR 0030)

/** Por que a pergunta de um cliente virou lacuna: nenhuma entrada do FAQ a responde, duas ou mais a respondem (duplicata) ou a única que a responde não tem resposta direta. */
export type KnowledgeGapKind = 'NoMatch' | 'Ambiguous' | 'NotAutoReply';

export type KnowledgeGapStatus = 'Open' | 'Dismissed' | 'Promoted';

/** Uma lacuna na lista da revisão (KnowledgeGapSummary). Nunca traz texto de cliente: só referências e contagens. */
export interface KnowledgeGapSummary {
  id: string;
  kind: KnowledgeGapKind;
  status: KnowledgeGapStatus;
  occurrences: number;
  firstSeenAt: string;
  lastSeenAt: string;
  decidedAt: string | null;
  /** A versão do rascunho que recebeu a entrada (só nas lacunas enviadas ao rascunho). */
  promotedVersion: number | null;
}

/** GET /agents/knowledge-gaps: a página (das mais frequentes para as menos) e o total da situação pedida. */
export interface KnowledgeGapPage {
  items: KnowledgeGapSummary[];
  total: number;
}

/** Onde a pergunta apareceu: só identificadores do atendimento. */
export interface KnowledgeGapOccurrence {
  sessionId: string;
  conversationId: string;
  messageId: string;
  occurredAt: string;
}

/** `Unavailable` = a mensagem foi removida ou quem pede não pode lê-la: a lacuna continua revisável (o administrador escreve a pergunta). */
export type KnowledgeGapSourceState = 'Available' | 'Unavailable';

/** A resposta de um ATENDENTE que veio depois da pergunta: só uma sugestão editável, nunca é usada sozinha. */
export interface KnowledgeGapHumanReply {
  text: string;
  occurredAt: string;
  /** Quantas mensagens do cliente vieram entre a pergunta e a resposta: com mais de zero, ela pode ser de outra pergunta. */
  customerMessagesBetween: number;
}

export interface KnowledgeGapSource {
  state: KnowledgeGapSourceState;
  question: string | null;
  humanReply: KnowledgeGapHumanReply | null;
}

/** GET /agents/knowledge-gaps/{id}. */
export interface KnowledgeGapDetail {
  gap: KnowledgeGapSummary;
  occurrences: KnowledgeGapOccurrence[];
  source: KnowledgeGapSource;
}

/** Corpo de POST /agents/knowledge-gaps/{id}/promote: a entrada de FAQ que o administrador montou. `autoReply` só vale se ele o ligou de propósito. */
export interface PromoteKnowledgeGapRequest {
  question: string;
  aliases: string[];
  answer: string;
  autoReply: boolean;
}

/** O resultado do envio ao rascunho. NADA foi publicado. */
export interface KnowledgeGapPromotion {
  gap: KnowledgeGapSummary;
  draftVersion: number;
  faqEntries: number;
}

export type AgentDefinitionStatus = 'Draft' | 'Published' | 'Retired';

export interface FaqEntry {
  question: string;
  answer: string;
  aliases: string[] | null;
  autoReply: boolean;
}

export interface AgentDefinitionContent {
  instructions: string;
  faq: FaqEntry[];
  audioInformativeText: string | null;
  handoffNoticeText: string | null;
  faqLocale: string | null;
}

/** Uma versão da definição do agente (AgentDefinitionView): o rascunho ou a publicada. */
export interface AgentDefinition {
  number: number;
  status: AgentDefinitionStatus;
  content: AgentDefinitionContent;
  updatedAt: string;
  publishedAt: string | null;
}

// ---------------------------------------------------------------- Conversations

export type MessageDirection = 'Inbound' | 'Outbound';

export type MessageAuthor = 'Customer' | 'Ai' | 'Human';

export type MessageKind = 'Text' | 'Audio' | 'Image' | 'Document' | 'Video' | 'Other';

export type ContentState = 'Ready' | 'Pending' | 'Failed';

export type DeliveryState =
  | 'Requested'
  | 'Queued'
  | 'Sending'
  | 'Sent'
  | 'Delivered'
  | 'Read'
  | 'Failed'
  | 'Expired'
  | 'Dead'
  | 'SendUnknown';

/**
 * Em que pé está o ARQUIVO de uma mensagem de mídia do cliente (ADR 0028): Pending = sendo buscado no WhatsApp; Available = guardado e
 * aberto pelo atendente; Unavailable = o WhatsApp não tem mais o arquivo ou ele não pôde ser baixado; Rejected = recusado pela política
 * de segurança (nunca é guardado nem aberto).
 */
export type AttachmentStatus = 'Pending' | 'Available' | 'Unavailable' | 'Rejected';

/** O arquivo de uma mensagem (AttachmentView). Nunca traz caminho, identificador do provedor nem URL: o conteúdo só sai pela rota autenticada do atendimento. */
export interface Attachment {
  id: string;
  status: AttachmentStatus;
  /** O tipo VERIFICADO contra o conteúdo (só com o arquivo disponível). */
  mimeType: string | null;
  fileName: string | null;
  sizeBytes: number | null;
  /**
   * O motivo quando o arquivo não está disponível (`expired`, `download-failed`, `too-large`, `type-not-allowed`, `content-mismatch`, `hash-mismatch`, `empty`...).
   * Num arquivo ENVIADO pelo atendente (ADR 0031) o único motivo é `retention`: o prazo de guarda passou (nome e legenda continuam; `mimeType` e `sizeBytes` vêm nulos).
   */
  failureCode: string | null;
  /** A legenda que o cliente (ou o atendente, nas mensagens de saída) escreveu junto, já saneada. */
  caption: string | null;
}

/**
 * Uma mensagem do histórico (MessageView). `text` é nulo para mídia ou conteúdo ainda não interpretado. Numa mensagem de arquivo do ATENDENTE
 * (`direction: 'Outbound'`, `author: 'Human'`, `kind` Image/Audio/Document) o `text` é nulo e a legenda mora em `attachment.caption`.
 */
export interface Message {
  id: string;
  sessionId: string;
  conversationId: string;
  direction: MessageDirection;
  author: MessageAuthor;
  authorRef: string | null;
  messageSeq: number;
  inboundSeq: number | null;
  kind: MessageKind;
  contentState: ContentState;
  text: string | null;
  lateArrival: boolean;
  occurredAt: string;
  providerTimestamp: string | null;
  receivedAt: string;
  deliveryState: DeliveryState | null;
  resendOfMessageId: string | null;
  replyKind: string | null;
  coveredThroughSeq: number | null;
  channelInstanceId: string;
  locale?: string | null;
  localeOrigin?: string | null;
  /** O nome do modelo de mensagem aprovado, quando a mensagem saiu por modelo (o `text` é o que o cliente leu, já com os parâmetros). */
  templateName?: string | null;
  /** O arquivo da mensagem de mídia (imagem, áudio ou documento) com o estado dele; ausente nas demais mensagens. */
  attachment?: Attachment | null;
}

/** Página de resultados; `nextCursor` é opaco e nulo na última página. */
export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

export type HistoryOrder = 'NewestFirst' | 'OldestFirst';

export type HandlingMode = 'AiAutonomous' | 'AiSupervised' | 'HumanAssisted';

/** Visão da sessão (SessionView), usada aqui só para o nome do contato. */
export interface Conversation {
  id: string;
  organizationId: string;
  businessUnitId: string | null;
  contactId: string;
  contactDisplayName: string | null;
  status: 'Open' | 'Closed';
  handlingMode: HandlingMode;
  responderRef: string | null;
  lastMessageAt: string;
}

// ---------------------------------------------------------------- Conhecimento híbrido (ADR 0034): busca semântica e sugestões da IA local

/** O modo da resposta direta por similaridade do tenant. `Off` é o interruptor de emergência (vale no próximo turno). */
export type SemanticAnswerMode = 'Off' | 'Evaluate' | 'Active';

/** GET e PUT /agents/semantic/settings. `isDefault`: o tenant nunca configurou (desligado, limiares padrão). */
export interface SemanticSettings {
  mode: SemanticAnswerMode;
  minScore: number;
  minMargin: number;
  updatedAt: string | null;
  isDefault: boolean;
}

/** Corpo de PUT /agents/semantic/settings. */
export interface SemanticSettingsRequest {
  mode: SemanticAnswerMode;
  minScore: number;
  minMargin: number;
}

/** GET /agents/semantic/metrics: só contagens e latências do tenant (nenhum texto). Tokens e custo evitados são ESTIMATIVA e ficam no relatório operacional, não aqui. */
export interface SemanticMetrics {
  windowDays: number;
  decisions: number;
  answered: number;
  wouldAnswer: number;
  noCandidates: number;
  belowThreshold: number;
  ambiguous: number;
  notAutoReply: number;
  guardRejected: number;
  stale: number;
  unavailable: number;
  faqHits: number;
  semanticHits: number;
  modelReplies: number;
  handoffs: number;
  commercialCallsAvoided: number;
  semanticShareOfAiReplies: number;
  latencyAverageMilliseconds: number | null;
  latencyP95Milliseconds: number | null;
}

export type FaqSuggestionKind = 'NewFaq' | 'Alias' | 'Duplicate' | 'Contradiction';

export type FaqSuggestionStatus = 'Pending' | 'Approved' | 'Rejected';

/** O conteúdo de uma sugestão da IA local (revisável e editável). Nunca traz o texto original do cliente. */
export interface SuggestionPayload {
  question: string | null;
  answer: string | null;
  aliases: string[] | null;
  targetQuestion: string | null;
  otherQuestion: string | null;
  gapIds: string[] | null;
  rationale: string | null;
}

export interface FaqSuggestion {
  id: string;
  kind: FaqSuggestionKind;
  status: FaqSuggestionStatus;
  gapId: string | null;
  payload: SuggestionPayload;
  modelId: string;
  createdAt: string;
  decidedAt: string | null;
  appliedDraftVersion: number | null;
  edits: number;
  revision: number;
}

export interface FaqSuggestionPage {
  items: FaqSuggestion[];
  total: number;
}

/** O resultado de pedir sugestões à IA local: as criadas (ou a que já estava pendente) e, se o modelo não propôs nada, o motivo curto. */
export interface FaqSuggestionGeneration {
  suggestions: FaqSuggestion[];
  skipped: number;
  skipReason: string | null;
}

/** Corpo de PUT /agents/faq-suggestions/{id}: campo nulo = não muda. */
export interface FaqSuggestionEdit {
  question: string | null;
  answer: string | null;
  aliases: string[] | null;
}

// ---------------------------------------------------------------- Motor de Integração Empresarial (ADR 0035)

export type IntegrationAuthType = 'None' | 'ApiKey' | 'Basic' | 'OAuth2ClientCredentials';
export type IntegrationEffect = 'Read' | 'Write';
export type IntegrationCaller = 'Operator' | 'Agent' | 'Workflow' | 'EmployeeAssistant' | 'Chatbot';
export type IntegrationParameterType = 'String' | 'Integer' | 'Number' | 'Boolean' | 'Date' | 'Enum';
export type IntegrationParameterLocation = 'Path' | 'Query' | 'Header' | 'Body';
export type IntegrationFieldType = 'String' | 'Integer' | 'Number' | 'Boolean';
export type IntegrationIdempotency = 'Unspecified' | 'None' | 'Reconcilable' | 'Proven';
export type IntegrationDefinitionStatus = 'Draft' | 'Published' | 'Retired';
export type IntegrationExecutionStatus = 'Succeeded' | 'Failed' | 'Rejected' | 'AwaitingConfirmation' | 'Unknown' | 'InProgress' | 'Cancelled';

/** Uma conexão com um sistema externo. NUNCA traz o segredo: só se há um (`hasSecret`), a versão e quando foi definido. */
export interface IntegrationConnection {
  id: string;
  organizationId: string | null;
  key: string;
  name: string;
  kind: string;
  baseUrl: string;
  authType: IntegrationAuthType;
  authSettings: Record<string, string>;
  defaultHeaders: Record<string, string>;
  allowedHeaders: string[];
  healthPath: string;
  timeoutSeconds: number;
  maxResponseBytes: number;
  enabled: boolean;
  hasSecret: boolean;
  secretVersion: number;
  secretUpdatedAt: string | null;
  createdAt: string;
  updatedAt: string;
  revision: number;
}

export interface IntegrationConnectionRequest {
  organizationId: string | null;
  key: string;
  name: string;
  baseUrl: string;
  authType: IntegrationAuthType;
  authSettings: Record<string, string>;
  defaultHeaders: Record<string, string>;
  allowedHeaders: string[];
  healthPath: string;
  timeoutSeconds: number | null;
  maxResponseBytes: number | null;
}

/** PUT /capabilities/connections/{id}: a chave e a organização não mudam; `expectedRevision` evita sobrescrever uma alteração que a pessoa não viu. */
export type IntegrationConnectionUpdate = Omit<IntegrationConnectionRequest, 'organizationId' | 'key'> & { expectedRevision: number };

export interface IntegrationConnectionTest {
  ok: boolean;
  outcome: string;
  upstreamStatus: number | null;
  elapsedMs: number;
}

export interface IntegrationParameter {
  name: string;
  type: IntegrationParameterType;
  location: IntegrationParameterLocation;
  required: boolean;
  description?: string | null;
  wireName?: string | null;
  maxLength?: number | null;
  pattern?: string | null;
  min?: number | null;
  max?: number | null;
  enumValues?: string[] | null;
  default?: string | null;
  personal?: boolean;
}

export interface IntegrationResponseField {
  name: string;
  pointer: string;
  type: IntegrationFieldType;
  required: boolean;
  maxLength?: number | null;
  personal?: boolean;
}

/** O molde de uma operação REST/JSON: tudo que sai na chamada vem daqui e dos valores validados dos parâmetros. */
export interface IntegrationSpec {
  method: string;
  pathTemplate: string;
  parameters: IntegrationParameter[];
  response: IntegrationResponseField[];
  idempotency: IntegrationIdempotency;
  idempotencyHeader?: string | null;
  responseRoot?: string | null;
  maxItems: number;
  maxAttempts: number;
}

export interface IntegrationDefinition {
  id: string;
  connectionId: string;
  key: string;
  version: number;
  status: IntegrationDefinitionStatus;
  name: string;
  description: string | null;
  effect: IntegrationEffect;
  requiresConfirmation: boolean;
  allowedCallers: IntegrationCaller[];
  enabled: boolean;
  spec: IntegrationSpec;
  createdAt: string;
  publishedAt: string | null;
  revision: number;
}

export interface IntegrationDefinitionRequest {
  connectionId: string;
  key: string;
  name: string;
  description: string | null;
  effect: IntegrationEffect;
  requiresConfirmation: boolean | null;
  allowedCallers: IntegrationCaller[];
  spec: IntegrationSpec;
  expectedRevision: number | null;
}

export interface IntegrationParameterProblem {
  parameter: string;
  rule: string;
}

export interface IntegrationError {
  code: string;
  message: string;
  retryable: boolean;
  upstreamStatus: number | null;
  problems: IntegrationParameterProblem[] | null;
}

/** O resultado de uma execução ou de um teste. `output` é conteúdo de sistema externo (não confiável): a tela o mostra como dado, nunca como instrução. */
export interface IntegrationResult {
  executionId: string;
  status: IntegrationExecutionStatus;
  output: Record<string, unknown> | null;
  error: IntegrationError | null;
  replayed: boolean;
  confirmationExpiresAt: string | null;
  untrustedContent: boolean;
}

export interface IntegrationExecution {
  id: string;
  definitionId: string;
  definitionKey: string;
  definitionVersion: number;
  connectionId: string;
  effect: IntegrationEffect;
  caller: IntegrationCaller[];
  requestedBy: string | null;
  serviceSessionId: string | null;
  status: IntegrationExecutionStatus;
  errorCode: string | null;
  upstreamStatus: number | null;
  attempts: number;
  durationMs: number | null;
  isTest: boolean;
  createdAt: string;
  completedAt: string | null;
  confirmedBy: string | null;
  confirmationExpiresAt: string | null;
}

export interface IntegrationExecutionPage {
  items: IntegrationExecution[];
  total: number;
}

export interface IntegrationConnectionStats {
  connectionId: string;
  total: number;
  failed: number;
  unknown: number;
  lastAt: string | null;
}
