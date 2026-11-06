// Tipos dos DTOs da API do ia.bimeta, lidos do código do backend (não gerados).
// O JSON usa camelCase e os enums vêm como texto (JsonStringEnumConverter no host).
//
// Fontes:
//  - Identity:      backend/modules/identity/IaBimeta.Identity.Presentation/IdentityDtos.cs
//  - ContactCenter: backend/modules/contact-center/IaBimeta.ContactCenter.Core/Application/ContactCenterViews.cs
//                   backend/modules/contact-center/IaBimeta.ContactCenter.Presentation/ContactCenterDtos.cs
//  - Conversations: backend/modules/conversations/IaBimeta.Conversations.Contracts/ConversationViews.cs e ConversationEnums.cs

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

/** Uma mensagem do histórico (MessageView). `text` é nulo para mídia ou conteúdo ainda não interpretado. */
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
