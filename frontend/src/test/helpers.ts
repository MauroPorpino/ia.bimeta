import { vi, type Mock } from 'vitest';
import type {
  AgentDefinition,
  Attachment,
  CurrentUser,
  FaqEntry,
  KnowledgeGapDetail,
  KnowledgeGapHumanReply,
  KnowledgeGapSummary,
  Message,
  QueueItem,
  ServiceQueue,
} from '../api/types';

export const USUARIO: CurrentUser = {
  id: '11111111-1111-4111-8111-111111111111',
  email: 'ana@demo-a.local',
  displayName: 'Ana Atendente',
  grants: [],
  preferredLocale: null,
};

/** Administrador do TENANT: concessão `OrganizationAdmin` sem organização (escopo do tenant). */
export const ADMINISTRADOR: CurrentUser = {
  id: '33333333-3333-4333-8333-333333333333',
  email: 'beto@demo-a.local',
  displayName: 'Beto Administrador',
  grants: [{ organizationId: null, role: 'OrganizationAdmin' }],
  preferredLocale: null,
};

export const OUTRO_USUARIO_ID = '22222222-2222-4222-8222-222222222222';

export const FILA: ServiceQueue = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  organizationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  businessUnitId: null,
  departmentId: null,
  name: 'Atendimento geral',
  status: 'Active',
  version: 1,
};

export function item(sobrescrever: Partial<QueueItem> = {}): QueueItem {
  return {
    id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    sessionId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    organizationId: FILA.organizationId,
    businessUnitId: null,
    departmentId: null,
    queueId: FILA.id,
    status: 'Waiting',
    assignedUserId: null,
    version: 1,
    enteredAt: new Date(Date.now() - 5 * 60_000).toISOString(),
    assignedAt: null,
    closedAt: null,
    closeReason: null,
    pending: null,
    updatedAt: new Date().toISOString(),
    priority: 0,
    routingRuleId: null,
    deliveryFailures: 0,
    lastDeliveryFailureAt: null,
    ...sobrescrever,
  };
}

let sequencia = 0;

export function mensagem(sobrescrever: Partial<Message> = {}): Message {
  sequencia += 1;
  return {
    id: `00000000-0000-4000-8000-${String(sequencia).padStart(12, '0')}`,
    sessionId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    conversationId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
    direction: 'Inbound',
    author: 'Customer',
    authorRef: null,
    messageSeq: sequencia,
    inboundSeq: sequencia,
    kind: 'Text',
    contentState: 'Ready',
    text: 'Olá',
    lateArrival: false,
    occurredAt: new Date().toISOString(),
    providerTimestamp: null,
    receivedAt: new Date().toISOString(),
    deliveryState: null,
    resendOfMessageId: null,
    replyKind: null,
    coveredThroughSeq: null,
    channelInstanceId: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
    ...sobrescrever,
  };
}

/** O arquivo de uma mensagem de mídia (por padrão, uma imagem já disponível). */
export function anexo(sobrescrever: Partial<Attachment> = {}): Attachment {
  sequencia += 1;
  return {
    id: `99999999-9999-4999-8999-${String(sequencia).padStart(12, '0')}`,
    status: 'Available',
    mimeType: 'image/png',
    fileName: 'foto.png',
    sizeBytes: 2048,
    failureCode: null,
    caption: null,
    ...sobrescrever,
  };
}

/** Uma lacuna do FAQ da lista da revisão (por padrão, aberta, sem correspondência, 3 ocorrências, vista há 5 minutos). */
export function lacuna(sobrescrever: Partial<KnowledgeGapSummary> = {}): KnowledgeGapSummary {
  sequencia += 1;
  return {
    id: `aaaa0000-0000-4000-8000-${String(sequencia).padStart(12, '0')}`,
    kind: 'NoMatch',
    status: 'Open',
    occurrences: 3,
    firstSeenAt: new Date(Date.now() - 3 * 86_400_000).toISOString(),
    lastSeenAt: new Date(Date.now() - 5 * 60_000).toISOString(),
    decidedAt: null,
    promotedVersion: null,
    ...sobrescrever,
  };
}

/** O detalhe de uma lacuna com a fonte disponível: a pergunta do cliente e, opcionalmente, a resposta de um atendente. */
export function detalheDaLacuna(
  resumo: KnowledgeGapSummary,
  fonte: { pergunta?: string | null; respostaHumana?: KnowledgeGapHumanReply | null; indisponivel?: boolean } = {},
): KnowledgeGapDetail {
  const indisponivel = fonte.indisponivel === true;
  return {
    gap: resumo,
    occurrences: [
      {
        sessionId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
        conversationId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
        messageId: '00000000-0000-4000-8000-0000000000ff',
        occurredAt: resumo.lastSeenAt,
      },
    ],
    source: {
      state: indisponivel ? 'Unavailable' : 'Available',
      question: indisponivel ? null : (fonte.pergunta ?? 'Vocês fazem exame de sangue sem hora marcada?'),
      humanReply: indisponivel ? null : (fonte.respostaHumana ?? null),
    },
  };
}

export function entradaDoFaq(sobrescrever: Partial<FaqEntry> = {}): FaqEntry {
  return { question: 'Qual o horário de funcionamento?', answer: 'Funcionamos de segunda a sexta, das 7h às 17h.', aliases: null, autoReply: false, ...sobrescrever };
}

/** Uma versão da definição do agente (por padrão, o rascunho 2). */
export function definicao(faq: FaqEntry[], sobrescrever: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    number: 2,
    status: 'Draft',
    content: { instructions: 'Seja cordial.', faq, audioInformativeText: null, handoffNoticeText: null, faqLocale: null },
    updatedAt: new Date().toISOString(),
    publishedAt: null,
    ...sobrescrever,
  };
}

export function json(corpo: unknown, status = 200): Response {
  return new Response(JSON.stringify(corpo), { status, headers: { 'Content-Type': 'application/json' } });
}

export function problema(status: number, title: string, detail?: string, code?: string): Response {
  return json({ title, detail, status, code }, status);
}

export interface Requisicao {
  metodo: string;
  caminho: string;
  consulta: URLSearchParams;
  corpo: Record<string, unknown> | null;
  cabecalhos: Headers;
}

export type Manipulador = (req: Requisicao) => Response | Promise<Response>;

/**
 * `fetch` simulado. As rotas são "MÉTODO /caminho" (sem o prefixo /api). Rota que não existe responde 404 vazio,
 * como a API faz com um recurso fora do alcance.
 */
export function criarFetch(rotas: Record<string, Manipulador | Response>): Mock<typeof fetch> {
  return vi.fn<typeof fetch>(async (entrada, init) => {
    const url = new URL(String(entrada), 'http://localhost');
    const metodo = (init?.method ?? 'GET').toUpperCase();
    const caminho = url.pathname.replace(/^\/api/, '');
    const manipulador = rotas[`${metodo} ${caminho}`];
    if (!manipulador) {
      return new Response(null, { status: 404 });
    }
    if (manipulador instanceof Response) {
      return manipulador.clone();
    }
    return manipulador({
      metodo,
      caminho,
      consulta: url.searchParams,
      corpo: init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : null,
      cabecalhos: new Headers(init?.headers),
    });
  });
}

/** Chamadas feitas a uma rota ("MÉTODO /caminho"), com o corpo já lido. */
export function chamadasDe(fetchSimulado: Mock<typeof fetch>, rota: string): Array<{ corpo: Record<string, unknown> | null; consulta: URLSearchParams }> {
  return fetchSimulado.mock.calls
    .map(([entrada, init]) => {
      const url = new URL(String(entrada), 'http://localhost');
      return {
        rota: `${(init?.method ?? 'GET').toUpperCase()} ${url.pathname.replace(/^\/api/, '')}`,
        corpo: init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : null,
        consulta: url.searchParams,
      };
    })
    .filter((c) => c.rota === rota)
    .map(({ corpo, consulta }) => ({ corpo, consulta }));
}

export const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
