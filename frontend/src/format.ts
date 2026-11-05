import type { AgentStatus, DeliveryState, Message, MessageAuthor, QueueItem } from './api/types';

/** "agora", "há 5 min", "há 2 h 10 min", "há 3 d". Datas inválidas ou no futuro viram "agora". */
export function tempoDecorrido(desdeIso: string | null | undefined, agora: number = Date.now()): string {
  if (!desdeIso) {
    return '';
  }
  const inicio = Date.parse(desdeIso);
  if (Number.isNaN(inicio)) {
    return '';
  }
  const segundos = Math.max(0, Math.floor((agora - inicio) / 1000));
  if (segundos < 60) {
    return 'agora';
  }
  const minutos = Math.floor(segundos / 60);
  if (minutos < 60) {
    return `há ${minutos} min`;
  }
  const horas = Math.floor(minutos / 60);
  if (horas < 24) {
    const resto = minutos % 60;
    return resto === 0 ? `há ${horas} h` : `há ${horas} h ${resto} min`;
  }
  return `há ${Math.floor(horas / 24)} d`;
}

export function horaCurta(iso: string): string {
  const data = new Date(iso);
  if (Number.isNaN(data.getTime())) {
    return '';
  }
  return data.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
}

export const ROTULO_AUTOR: Record<MessageAuthor, string> = {
  Customer: 'Cliente',
  Ai: 'IA',
  Human: 'Atendente',
};

export const ROTULO_PRESENCA: Record<AgentStatus, string> = {
  Available: 'Disponível',
  Paused: 'Ausente',
  Offline: 'Offline',
};

export const ROTULO_ENTREGA: Record<DeliveryState, string> = {
  Requested: 'solicitado',
  Queued: 'na fila de envio',
  Sending: 'enviando',
  Sent: 'enviado',
  Delivered: 'entregue',
  Read: 'lido',
  Failed: 'falha no envio',
  Expired: 'expirado',
  Dead: 'envio abandonado',
  SendUnknown: 'envio de resultado incerto',
};

export const SEM_TEXTO_MIDIA = 'mídia recebida (arquivo indisponível no MVP)';

/** O que mostrar no lugar do texto de uma mensagem. Nunca inventa conteúdo. */
export function corpoDaMensagem(m: Message): { texto: string; semConteudo: boolean } {
  if (m.text && m.text.trim() !== '') {
    return { texto: m.text, semConteudo: false };
  }
  if (m.contentState === 'Pending') {
    return { texto: 'conteúdo ainda em processamento', semConteudo: true };
  }
  if (m.contentState === 'Failed') {
    return { texto: 'conteúdo não pôde ser lido', semConteudo: true };
  }
  if (m.kind === 'Text') {
    return { texto: '(mensagem sem texto)', semConteudo: true };
  }
  return { texto: SEM_TEXTO_MIDIA, semConteudo: true };
}

export function situacaoDoItem(item: QueueItem, usuarioId: string | undefined): string {
  switch (item.status) {
    case 'Waiting':
      return 'Aguardando';
    case 'Assigned':
      return item.assignedUserId === usuarioId ? 'Com você' : 'Com outro atendente';
    case 'Closed':
      return 'Encerrado';
  }
}

/**
 * Identificação curta quando o nome do contato ainda não chegou (ou não existe). Usa o FIM do GUID: nos GUIDs v7 o começo é
 * o relógio e se repete entre itens criados no mesmo minuto, enquanto o fim é aleatório.
 */
export function rotuloSemNome(itemId: string): string {
  return `Atendimento #${itemId.slice(-6)}`;
}
