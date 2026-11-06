import type { Message, QueueItem } from './api/types';

/** O que a tela sabe de uma conversa: o item da fila, o histórico já lido e se há mensagens mais antigas que não vieram. */
export interface EntradaDeConversa {
  item: QueueItem | null;
  mensagens: Message[];
  temMais: boolean;
}

/**
 * Junta o histórico que a tela já tem com a página recém-lida, sem nunca duplicar: a chave é o id da mensagem e a ordem
 * é a de `messageSeq`. A página lida é a mais recente, então ela prevalece (o estado de entrega só avança no backend).
 * Mensagens antigas que já estavam na tela continuam lá, mesmo que a página nova (as últimas 100) não as traga mais.
 * Quando nada mudou, devolve o MESMO array de `atuais` (a tela não se redesenha nem rola a cada ciclo de polling).
 */
export function mesclarMensagens(atuais: Message[], recebidas: readonly Message[]): Message[] {
  const porId = new Map<string, Message>();
  for (const m of atuais) {
    porId.set(m.id, m);
  }
  for (const m of recebidas) {
    porId.set(m.id, m);
  }
  const juntas = [...porId.values()].sort((a, b) => a.messageSeq - b.messageSeq);
  return mesmoHistorico(atuais, juntas) ? atuais : juntas;
}

/** Mesmas mensagens, na mesma ordem e com o mesmo conteúdo e estado de entrega. */
function mesmoHistorico(a: readonly Message[], b: readonly Message[]): boolean {
  return (
    a.length === b.length &&
    a.every((m, i) => {
      const outra = b[i];
      return outra !== undefined && m.id === outra.id && m.text === outra.text && m.contentState === outra.contentState && m.deliveryState === outra.deliveryState;
    })
  );
}

const LIMITE_PADRAO = 30;

/**
 * Memória da área de atendimento para trocar de conversa sem perder nada: o histórico já lido (a tela abre na hora e o
 * polling só atualiza) e o rascunho da resposta de cada uma. Vive só enquanto o atendente está logado (o dono é a área
 * logada, que é desmontada no logout) e guarda no máximo `limite` conversas, descartando as menos recentes.
 */
export class CacheDeConversas {
  private readonly entradas = new Map<string, EntradaDeConversa>();
  private readonly rascunhos = new Map<string, string>();

  constructor(private readonly limite: number = LIMITE_PADRAO) {}

  ler(itemId: string): EntradaDeConversa | undefined {
    return this.entradas.get(itemId);
  }

  gravar(itemId: string, entrada: EntradaDeConversa): void {
    this.entradas.delete(itemId);
    this.entradas.set(itemId, entrada);
    CacheDeConversas.aparar(this.entradas, this.limite);
  }

  /** Esquece o histórico guardado (a conversa saiu do alcance deste atendente: não se mostra mais). O rascunho fica. */
  esquecer(itemId: string): void {
    this.entradas.delete(itemId);
  }

  lerRascunho(itemId: string): string {
    return this.rascunhos.get(itemId) ?? '';
  }

  /** Texto vazio apaga o rascunho. */
  gravarRascunho(itemId: string, texto: string): void {
    this.rascunhos.delete(itemId);
    if (texto !== '') {
      this.rascunhos.set(itemId, texto);
      CacheDeConversas.aparar(this.rascunhos, this.limite);
    }
  }

  private static aparar<T>(mapa: Map<string, T>, limite: number): void {
    while (mapa.size > limite) {
      const maisAntigo = mapa.keys().next();
      if (maisAntigo.done) {
        return;
      }
      mapa.delete(maisAntigo.value);
    }
  }
}
