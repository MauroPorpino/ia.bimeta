import type { AgentStatus, DeliveryState, Message, MessageAuthor, QueueItem, ReplyOptions } from './api/types';

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

/**
 * A janela de resposta LIVRE do provedor (24 horas no WhatsApp, contada da última mensagem do cliente) vista pela tela. Sem as opções
 * (a consulta falhou ou o canal não as conhece), ou sem janela no provedor, fica aberta: quem decide é o servidor, que recusa o texto
 * livre fora dela. Com a janela, o relógio da tela fecha-a quando o prazo passa, sem esperar a próxima releitura.
 */
export function janelaDeResposta(opcoes: ReplyOptions | null, agora: number): { aberta: boolean; fechaEm: string | null; horas: number | null } {
  if (!opcoes || opcoes.windowHours === null) {
    return { aberta: true, fechaEm: null, horas: null };
  }
  const fecha = opcoes.windowClosesAt ? Date.parse(opcoes.windowClosesAt) : Number.NaN;
  const aberta = !Number.isNaN(fecha) && agora < fecha;
  return { aberta, fechaEm: opcoes.windowClosesAt, horas: opcoes.windowHours };
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

export const SEM_TEXTO_MIDIA = 'mídia recebida (arquivo não disponível)';
export const SEM_TEXTO_MIDIA_ENVIADA = 'mídia enviada (arquivo não disponível)';

/** O tamanho de um arquivo para a tela: "812 B", "48 KB", "1,2 MB" (base 1024, uma casa quando fizer diferença). */
export function formatarTamanho(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) {
    return '';
  }
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const unidades = ['KB', 'MB', 'GB'];
  let valor = bytes / 1024;
  let i = 0;
  while (valor >= 1024 && i < unidades.length - 1) {
    valor /= 1024;
    i += 1;
  }
  const texto = valor >= 10 ? String(Math.round(valor)) : valor.toFixed(1).replace('.', ',').replace(/,0$/, '');
  return `${texto} ${unidades[i]}`;
}

/** O que dizer ao atendente quando o arquivo de uma mídia NÃO está disponível, conforme o motivo que o servidor registrou. Nunca mostra texto do provedor. */
export function motivoDoAnexo(status: 'Unavailable' | 'Rejected', codigo: string | null): string {
  switch (codigo) {
    case 'expired':
      return 'O arquivo expirou no WhatsApp e não pode mais ser baixado. Peça ao cliente para enviar de novo.';
    case 'download-failed':
      return 'Não foi possível baixar o arquivo do WhatsApp depois de várias tentativas. Peça ao cliente para enviar de novo.';
    case 'too-large':
      return 'O arquivo é maior que o limite permitido e foi bloqueado.';
    case 'type-not-allowed':
      return 'Este tipo de arquivo não é permitido e foi bloqueado.';
    case 'content-mismatch':
      return 'O conteúdo não corresponde ao tipo informado pelo cliente; o arquivo foi bloqueado por segurança.';
    case 'hash-mismatch':
      return 'Não foi possível confirmar a integridade do arquivo; ele foi bloqueado por segurança.';
    case 'empty':
      return 'O arquivo chegou vazio.';
    case 'malware':
      return 'O arquivo foi bloqueado pela verificação de segurança.';
    case 'retention':
      // Arquivo ENVIADO pelo atendente (ADR 0031): o servidor o apaga depois do prazo de guarda.
      return 'O arquivo foi apagado do servidor depois do prazo de guarda; o nome e a legenda continuam aqui.';
    default:
      return status === 'Rejected' ? 'O arquivo foi recusado pela política de segurança e não pode ser aberto.' : 'O arquivo não está disponível.';
  }
}

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
  return { texto: m.direction === 'Outbound' ? SEM_TEXTO_MIDIA_ENVIADA : SEM_TEXTO_MIDIA, semConteudo: true };
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
