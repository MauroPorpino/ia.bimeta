import type { Message, Page, QueueItem } from './api/types';
import type { ArmazemLocal, PedidoPendente } from './armazem';

/**
 * O que a tela sabe de uma conversa: o item da fila, o histórico já lido e o cursor das mensagens mais antigas que ainda
 * não foram lidas (`null`: não há mais). `carregouAnteriores` diz se o atendente já pediu páginas antigas: daí em diante a
 * releitura periódica (só as mais recentes) não mexe mais no cursor.
 */
export interface EntradaDeConversa {
  item: QueueItem | null;
  mensagens: Message[];
  cursorAnterior: string | null;
  carregouAnteriores: boolean;
}

/** Onde o atendente estava na rolagem da conversa (a distância do fim vale mesmo que cheguem mensagens novas). */
export interface PosicaoDeRolagem {
  noFim: boolean;
  distanciaDoFim: number;
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
      return (
        outra !== undefined &&
        m.id === outra.id &&
        m.text === outra.text &&
        m.contentState === outra.contentState &&
        m.deliveryState === outra.deliveryState &&
        // O arquivo da mídia muda de estado depois da mensagem (Pending → Available/Unavailable/Rejected): a releitura periódica precisa trazer isso à tela.
        m.attachment?.status === outra.attachment?.status &&
        m.attachment?.failureCode === outra.attachment?.failureCode
      );
    })
  );
}

/**
 * Aplica a releitura periódica (as mensagens mais recentes) à entrada que a tela já tem. Se a janela recente NÃO encosta no
 * que a tela tinha (chegaram mais mensagens do que cabem numa página, ou ela ficou muito tempo parada), o histórico antigo
 * deixa de ser contínuo: recomeça da página recente, para nunca mostrar um buraco no meio da conversa.
 */
export function aplicarPaginaRecente(anterior: EntradaDeConversa | undefined, item: QueueItem, pagina: Page<Message>): EntradaDeConversa {
  const guardadas = anterior?.mensagens ?? [];
  const menorDaPagina = pagina.items.reduce((menor, m) => Math.min(menor, m.messageSeq), Number.POSITIVE_INFINITY);
  const maiorGuardada = guardadas.reduce((maior, m) => Math.max(maior, m.messageSeq), 0);
  const idsDaPagina = new Set(pagina.items.map((m) => m.id));
  const temBuraco = guardadas.length > 0 && pagina.items.length > 0 && menorDaPagina > maiorGuardada + 1 && !guardadas.some((m) => idsDaPagina.has(m.id));
  if (!anterior || temBuraco) {
    return {
      item,
      mensagens: mesclarMensagens([], pagina.items),
      cursorAnterior: pagina.nextCursor,
      carregouAnteriores: false,
    };
  }
  return {
    item,
    mensagens: mesclarMensagens(guardadas, pagina.items),
    cursorAnterior: anterior.carregouAnteriores ? anterior.cursorAnterior : pagina.nextCursor,
    carregouAnteriores: anterior.carregouAnteriores,
  };
}

/** Aplica uma página de mensagens mais ANTIGAS (pedida pelo atendente): entra no começo do histórico e avança o cursor. */
export function aplicarPaginaAnterior(anterior: EntradaDeConversa, pagina: Page<Message>): EntradaDeConversa {
  return {
    ...anterior,
    mensagens: mesclarMensagens(anterior.mensagens, pagina.items),
    cursorAnterior: pagina.nextCursor,
    carregouAnteriores: true,
  };
}

const LIMITE_PADRAO = 30;

export interface OpcoesDoCache {
  limite?: number;
  /** Onde rascunhos, "visto até" e o pedido pendente sobrevivem ao recarregamento (escopo de tenant e usuário). */
  armazem?: ArmazemLocal;
}

/**
 * Memória da área de atendimento para trocar de conversa sem perder nada: o histórico já lido (a tela abre na hora e o
 * polling só atualiza), a posição da rolagem, o "visto até" e o rascunho da resposta de cada uma. O histórico e a rolagem
 * vivem só enquanto o atendente está logado e guardam no máximo `limite` conversas. Rascunho, "visto até" e pedido pendente
 * também vão ao armazenamento local (se houver), com escopo de tenant e usuário, e por isso sobrevivem a recarregar a
 * página e a fechar o navegador.
 */
export class CacheDeConversas {
  private readonly entradas = new Map<string, EntradaDeConversa>();
  private readonly rascunhos = new Map<string, string>();
  private readonly pendentes = new Map<string, PedidoPendente>();
  private readonly vistos = new Map<string, number>();
  private readonly rolagens = new Map<string, PosicaoDeRolagem>();
  private readonly limite: number;
  private readonly armazem: ArmazemLocal | undefined;

  constructor(opcoes: OpcoesDoCache | number = {}) {
    const o = typeof opcoes === 'number' ? { limite: opcoes } : opcoes;
    this.limite = o.limite ?? LIMITE_PADRAO;
    this.armazem = o.armazem;
  }

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
    this.rolagens.delete(itemId);
  }

  lerRolagem(itemId: string): PosicaoDeRolagem | undefined {
    return this.rolagens.get(itemId);
  }

  gravarRolagem(itemId: string, posicao: PosicaoDeRolagem): void {
    this.rolagens.delete(itemId);
    this.rolagens.set(itemId, posicao);
    CacheDeConversas.aparar(this.rolagens, this.limite);
  }

  /** O rascunho desta conversa: o desta aba, ou o que o armazenamento local guardou (recarregamento, navegador reaberto). */
  lerRascunho(itemId: string): string {
    return this.rascunhos.get(itemId) ?? this.armazem?.lerRascunho(itemId)?.texto ?? '';
  }

  /** Texto vazio apaga o rascunho. O pedido pendente (se houver) é preservado. */
  gravarRascunho(itemId: string, texto: string): void {
    if (texto === '') {
      this.rascunhos.delete(itemId);
    } else {
      this.rascunhos.delete(itemId);
      this.rascunhos.set(itemId, texto);
      CacheDeConversas.aparar(this.rascunhos, this.limite);
    }
    this.armazem?.gravarRascunho(itemId, texto, this.lerPendente(itemId));
  }

  /** O envio que estava em andamento (requestId e texto), para o reenvio depois de recarregar não duplicar a mensagem. */
  lerPendente(itemId: string): PedidoPendente | undefined {
    return this.pendentes.get(itemId) ?? this.armazem?.lerRascunho(itemId)?.pendente;
  }

  gravarPendente(itemId: string, pendente: PedidoPendente | null): void {
    if (pendente) {
      this.pendentes.set(itemId, pendente);
    } else {
      this.pendentes.delete(itemId);
    }
    this.armazem?.gravarRascunho(itemId, this.rascunhos.get(itemId) ?? this.armazem.lerRascunho(itemId)?.texto ?? '', pendente ?? undefined);
  }

  /** A sequência da última mensagem que o atendente viu nesta conversa. */
  lerVisto(itemId: string): number | undefined {
    return this.vistos.get(itemId) ?? this.armazem?.lerVisto(itemId);
  }

  gravarVisto(itemId: string, seq: number): void {
    const atual = this.lerVisto(itemId);
    if (atual === undefined || seq > atual) {
      this.vistos.set(itemId, seq);
      this.armazem?.gravarVisto(itemId, seq);
    }
  }

  /** Apaga tudo desta conversa (o atendente a encerrou): histórico, rascunho, pendência e "visto". */
  descartarConversa(itemId: string): void {
    this.entradas.delete(itemId);
    this.rolagens.delete(itemId);
    this.rascunhos.delete(itemId);
    this.pendentes.delete(itemId);
    this.vistos.delete(itemId);
    this.armazem?.limparConversa(itemId);
  }

  /** Esvazia a memória e o armazenamento local DESTE usuário (a saída pedida por ele). */
  limparTudo(): void {
    this.entradas.clear();
    this.rascunhos.clear();
    this.pendentes.clear();
    this.vistos.clear();
    this.rolagens.clear();
    this.armazem?.limparDoUsuario();
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
