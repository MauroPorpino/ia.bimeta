import type { Message, MessageKind } from './api/types';
import { formatarTamanho } from './format';

/**
 * Envio de arquivo pelo atendente (ADR 0031): imagem, áudio ou documento. O que está aqui é só o feedback ANTECIPADO da tela (tipo, tamanho, arquivo vazio, legenda):
 * o servidor é a autoridade, descobre o tipo REAL pelo conteúdo e repete todas as verificações.
 */

export type TipoDeArquivo = 'Image' | 'Audio' | 'Document';

const MB = 1024 * 1024;

/** O tamanho máximo por tipo (o padrão do servidor; ele é quem decide). */
export const LIMITE_POR_TIPO: Record<TipoDeArquivo, number> = { Image: 5 * MB, Audio: 16 * MB, Document: 20 * MB };

/** A legenda (imagem e documento) aceita até este número de caracteres; o áudio não tem legenda. */
export const LIMITE_DA_LEGENDA = 1024;

export const ROTULO_DO_TIPO_DE_ARQUIVO: Record<TipoDeArquivo, string> = { Image: 'Imagem', Audio: 'Áudio', Document: 'Documento' };

const DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
const XLSX = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const PPTX = 'application/vnd.openxmlformats-officedocument.presentationml.presentation';

const TIPOS_POR_MIME = new Map<string, TipoDeArquivo>([
  ['image/jpeg', 'Image'],
  ['image/png', 'Image'],
  ['audio/aac', 'Audio'],
  ['audio/x-aac', 'Audio'],
  ['audio/amr', 'Audio'],
  ['audio/mpeg', 'Audio'],
  ['audio/mp3', 'Audio'],
  ['audio/mp4', 'Audio'],
  ['audio/m4a', 'Audio'],
  ['audio/x-m4a', 'Audio'],
  ['audio/ogg', 'Audio'],
  ['audio/opus', 'Audio'],
  ['application/pdf', 'Document'],
  ['text/plain', 'Document'],
  ['application/msword', 'Document'],
  [DOCX, 'Document'],
  ['application/vnd.ms-excel', 'Document'],
  [XLSX, 'Document'],
  ['application/vnd.ms-powerpoint', 'Document'],
  [PPTX, 'Document'],
]);

const TIPOS_POR_EXTENSAO = new Map<string, TipoDeArquivo>([
  ['jpg', 'Image'],
  ['jpeg', 'Image'],
  ['png', 'Image'],
  ['aac', 'Audio'],
  ['amr', 'Audio'],
  ['mp3', 'Audio'],
  ['m4a', 'Audio'],
  ['ogg', 'Audio'],
  ['opus', 'Audio'],
  ['pdf', 'Document'],
  ['txt', 'Document'],
  ['doc', 'Document'],
  ['docx', 'Document'],
  ['xls', 'Document'],
  ['xlsx', 'Document'],
  ['ppt', 'Document'],
  ['pptx', 'Document'],
]);

/** O `accept` do seletor de arquivo: as extensões e os tipos aceitos (o navegador já filtra a janela de escolha; a validação abaixo continua valendo). */
export const ACCEPT_DE_ARQUIVOS = [...[...TIPOS_POR_EXTENSAO.keys()].map((extensao) => `.${extensao}`), ...TIPOS_POR_MIME.keys()].join(',');

/** A lista de tipos aceitos, em texto, para as mensagens de recusa. */
export const TIPOS_ACEITOS_TEXTO =
  'Tipos aceitos: imagem JPEG ou PNG; áudio AAC, AMR, MP3, M4A ou Ogg Opus; documento PDF, TXT, Word, Excel ou PowerPoint.';

/** Resumo dos limites, para a dica ao lado do botão de anexar. */
export const LIMITES_RESUMO = `Imagem JPEG ou PNG até ${formatarTamanho(LIMITE_POR_TIPO.Image)}, áudio até ${formatarTamanho(LIMITE_POR_TIPO.Audio)} (sem legenda), documento até ${formatarTamanho(LIMITE_POR_TIPO.Document)}.`;

function extensaoDe(nome: string): string | null {
  const ponto = nome.lastIndexOf('.');
  if (ponto < 0 || ponto === nome.length - 1) {
    return null;
  }
  return nome.slice(ponto + 1).toLowerCase();
}

/**
 * O tipo do arquivo para a tela, ou `null` quando ele não é aceito. Vale o tipo que o navegador informou; sem ele (ou com um tipo genérico que não está na lista, como
 * `application/octet-stream`), decide a extensão. Não verifica o conteúdo: isso é do servidor.
 */
export function tipoDoArquivo(arquivo: Pick<File, 'name' | 'type'>): TipoDeArquivo | null {
  const mime = (arquivo.type.split(';')[0] ?? '').trim().toLowerCase();
  const porMime = TIPOS_POR_MIME.get(mime);
  if (porMime) {
    return porMime;
  }
  const extensao = extensaoDe(arquivo.name);
  return (extensao ? TIPOS_POR_EXTENSAO.get(extensao) : undefined) ?? null;
}

export type ValidacaoDoArquivo = { valido: true; tipo: TipoDeArquivo } | { valido: false; motivo: 'empty' | 'type-not-allowed' | 'too-large' | 'caption-too-long'; texto: string };

/** Confere o arquivo (e a legenda, que o áudio não tem) antes de enviar. Só consultivo: o servidor repete as verificações e decide. */
export function validarArquivo(arquivo: Pick<File, 'name' | 'type' | 'size'>, legenda: string): ValidacaoDoArquivo {
  if (arquivo.size === 0) {
    return { valido: false, motivo: 'empty', texto: 'O arquivo está vazio. Escolha outro arquivo.' };
  }
  const tipo = tipoDoArquivo(arquivo);
  if (tipo === null) {
    return { valido: false, motivo: 'type-not-allowed', texto: `Este tipo de arquivo não é aceito. ${TIPOS_ACEITOS_TEXTO}` };
  }
  const limite = LIMITE_POR_TIPO[tipo];
  if (arquivo.size > limite) {
    return {
      valido: false,
      motivo: 'too-large',
      texto: `O arquivo tem ${formatarTamanho(arquivo.size)} e passa do tamanho máximo para ${ROTULO_DO_TIPO_DE_ARQUIVO[tipo].toLowerCase()} (${formatarTamanho(limite)}).`,
    };
  }
  if (tipo !== 'Audio' && legenda.trim().length > LIMITE_DA_LEGENDA) {
    return { valido: false, motivo: 'caption-too-long', texto: `A legenda passa do limite de ${LIMITE_DA_LEGENDA} caracteres.` };
  }
  return { valido: true, tipo };
}

/** O texto de uma recusa do SERVIDOR (422 `MediaRejected`), pelo motivo curto; `detalhe` (o texto em português do servidor) é a reserva para um motivo desconhecido. */
export function textoDaRecusaDoArquivo(motivo: string | null, detalhe: string | null): string {
  switch (motivo) {
    case 'too-large':
      return 'O arquivo passa do tamanho máximo para este tipo.';
    case 'type-not-allowed':
      return `Este tipo de arquivo não é aceito. ${TIPOS_ACEITOS_TEXTO}`;
    case 'content-mismatch':
      return 'O conteúdo do arquivo não corresponde ao tipo ou à extensão.';
    case 'audio-codec-unsupported':
      return 'O áudio Ogg precisa ser Opus, em mono.';
    case 'caption-not-supported':
      return 'Este tipo de arquivo não aceita legenda. Envie o arquivo sem legenda.';
    case 'caption-too-long':
      return `A legenda passa do limite de ${LIMITE_DA_LEGENDA} caracteres.`;
    case 'empty':
      return 'O arquivo está vazio. Escolha outro arquivo.';
    case 'malware':
      return 'O arquivo foi bloqueado pela verificação de segurança e não pode ser enviado.';
    case 'invalid-shape':
      return 'O envio foi recusado porque o arquivo ou a legenda não estão no formato esperado. Escolha o arquivo de novo.';
    default:
      return detalhe ?? 'O servidor recusou o arquivo.';
  }
}

/** A identidade de um envio de arquivo (nome, tamanho, data de modificação e legenda): o mesmo arquivo com a mesma legenda é o MESMO pedido e reaproveita o requestId. */
export function chaveDoArquivo(arquivo: Pick<File, 'name' | 'size' | 'lastModified'>, legenda: string | null): string {
  return `${arquivo.name}|${arquivo.size}|${arquivo.lastModified}|${legenda ?? ''}`;
}

const TIPOS_DE_MENSAGEM_COM_ARQUIVO: ReadonlySet<MessageKind> = new Set<MessageKind>(['Image', 'Audio', 'Document']);

/**
 * Há, no que a tela já leu, um arquivo do atendente que NÃO chegou ao cliente e ainda não foi substituído por uma resposta que saiu? Então o aviso de falha de envio
 * também orienta a enviar o arquivo de novo (o sistema nunca reenvia o arquivo sozinho). Falha = Failed, Expired ou Dead.
 */
export function temArquivoSemEnvio(mensagens: Message[]): boolean {
  const saiu = (m: Message) => m.deliveryState === 'Sent' || m.deliveryState === 'Delivered' || m.deliveryState === 'Read';
  const falhou = (m: Message) => m.deliveryState === 'Failed' || m.deliveryState === 'Expired' || m.deliveryState === 'Dead';
  const ultimaQueSaiu = mensagens.reduce((maior, m) => (m.direction === 'Outbound' && m.author === 'Human' && saiu(m) && m.messageSeq > maior ? m.messageSeq : maior), 0);
  return mensagens.some((m) => m.direction === 'Outbound' && TIPOS_DE_MENSAGEM_COM_ARQUIVO.has(m.kind) && m.attachment != null && falhou(m) && m.messageSeq > ultimaQueSaiu);
}
