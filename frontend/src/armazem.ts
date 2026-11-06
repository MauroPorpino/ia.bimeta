// Armazenamento local do atendente (localStorage): rascunhos das respostas, "visto até" de cada conversa e o pedido de
// envio em andamento. Tudo com escopo de tenant + usuário (a chave leva os dois), validade de 7 dias e limite por usuário.
// O token da sessão NUNCA entra aqui (fica só em memória e no sessionStorage). Se o navegador não deixa gravar (modo
// privado, cota cheia), tudo continua funcionando só em memória.

export interface EscopoDeUsuario {
  /** Slug do tenant da sessão (pode ser vazio em sessão antiga). */
  tenant: string;
  usuarioId: string;
}

/** O envio em andamento de uma resposta: o mesmo texto reenviado depois de um recarregamento reaproveita o requestId. */
export interface PedidoPendente {
  requestId: string;
  texto: string;
}

export interface RascunhoGuardado {
  texto: string;
  pendente?: PedidoPendente;
}

export const PREFIXO_DO_ARMAZEM = 'ia-bimeta.v1';
export const VALIDADE_DOS_REGISTROS_MS = 7 * 24 * 3_600_000;
const LIMITE_DE_REGISTROS_POR_USUARIO = 100;
const TAMANHO_MAXIMO_DO_TEXTO = 20_000;

interface RegistroDeRascunho {
  t: string;
  p?: { r: string; x: string };
  em: number;
}

interface RegistroDeVisto {
  s: number;
  em: number;
}

export interface OpcoesDoArmazem {
  storage?: Storage | null;
  agora?: () => number;
  validadeMs?: number;
}

function localStorageSeguro(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

export class ArmazemLocal {
  private readonly storage: Storage | null;
  private readonly agora: () => number;
  private readonly validadeMs: number;
  private readonly prefixoDoUsuario: string;

  constructor(
    readonly escopo: EscopoDeUsuario,
    opcoes: OpcoesDoArmazem = {},
  ) {
    this.storage = opcoes.storage === undefined ? localStorageSeguro() : opcoes.storage;
    this.agora = opcoes.agora ?? (() => Date.now());
    this.validadeMs = opcoes.validadeMs ?? VALIDADE_DOS_REGISTROS_MS;
    this.prefixoDoUsuario = `${PREFIXO_DO_ARMAZEM}:${encodeURIComponent(escopo.tenant)}:${encodeURIComponent(escopo.usuarioId)}:`;
    this.expurgar();
  }

  get disponivel(): boolean {
    return this.storage !== null;
  }

  private chave(tipo: 'r' | 'v', itemId: string): string {
    return `${this.prefixoDoUsuario}${tipo}:${encodeURIComponent(itemId)}`;
  }

  private ler<T extends { em: number }>(chave: string): T | null {
    if (!this.storage) {
      return null;
    }
    try {
      const bruto = this.storage.getItem(chave);
      if (bruto === null) {
        return null;
      }
      const lido = JSON.parse(bruto) as T;
      if (typeof lido?.em !== 'number' || this.agora() - lido.em > this.validadeMs) {
        this.storage.removeItem(chave);
        return null;
      }
      return lido;
    } catch {
      return null;
    }
  }

  private gravar(chave: string, registro: object): boolean {
    if (!this.storage) {
      return false;
    }
    try {
      this.storage.setItem(chave, JSON.stringify(registro));
      return true;
    } catch {
      return false;
    }
  }

  private remover(chave: string): void {
    try {
      this.storage?.removeItem(chave);
    } catch {
      // sem acesso ao armazenamento: nada a remover
    }
  }

  lerRascunho(itemId: string): RascunhoGuardado | null {
    const registro = this.ler<RegistroDeRascunho>(this.chave('r', itemId));
    if (!registro) {
      return null;
    }
    return { texto: registro.t, pendente: registro.p ? { requestId: registro.p.r, texto: registro.p.x } : undefined };
  }

  /** Grava o rascunho (e o pedido pendente, se houver). Sem texto e sem pendência, apaga o registro. Devolve se gravou. */
  gravarRascunho(itemId: string, texto: string, pendente?: PedidoPendente): boolean {
    const chave = this.chave('r', itemId);
    if (texto === '' && !pendente) {
      this.remover(chave);
      return true;
    }
    if (texto.length > TAMANHO_MAXIMO_DO_TEXTO) {
      return false;
    }
    const registro: RegistroDeRascunho = { t: texto, em: this.agora() };
    if (pendente) {
      registro.p = { r: pendente.requestId, x: pendente.texto };
    }
    return this.gravar(chave, registro);
  }

  /** A sequência da última mensagem que o atendente VIU na conversa. */
  lerVisto(itemId: string): number | undefined {
    return this.ler<RegistroDeVisto>(this.chave('v', itemId))?.s;
  }

  gravarVisto(itemId: string, seq: number): void {
    const atual = this.lerVisto(itemId);
    if (atual === undefined || seq > atual) {
      this.gravar(this.chave('v', itemId), { s: seq, em: this.agora() } satisfies RegistroDeVisto);
    }
  }

  /** Apaga o rascunho, o pedido pendente e o "visto" de uma conversa (depois de o atendente encerrá-la). */
  limparConversa(itemId: string): void {
    this.remover(this.chave('r', itemId));
    this.remover(this.chave('v', itemId));
  }

  /** Apaga TUDO o que é deste usuário (na saída pedida por ele). Registros de outros usuários não são tocados. */
  limparDoUsuario(): void {
    for (const chave of this.chavesDoArmazem()) {
      if (chave.startsWith(this.prefixoDoUsuario)) {
        this.remover(chave);
      }
    }
  }

  /**
   * Remove o que passou da validade (de QUALQUER usuário: dado vencido de outro operador não deve ficar neste navegador)
   * e o que passar do limite deste usuário, do mais antigo para o mais novo.
   */
  expurgar(): void {
    if (!this.storage) {
      return;
    }
    const doUsuario: Array<{ chave: string; em: number }> = [];
    for (const chave of this.chavesDoArmazem()) {
      let em = 0;
      try {
        const lido = JSON.parse(this.storage.getItem(chave) ?? 'null') as { em?: unknown } | null;
        em = typeof lido?.em === 'number' ? lido.em : 0;
      } catch {
        em = 0;
      }
      if (em === 0 || this.agora() - em > this.validadeMs) {
        this.remover(chave);
      } else if (chave.startsWith(this.prefixoDoUsuario)) {
        doUsuario.push({ chave, em });
      }
    }
    if (doUsuario.length > LIMITE_DE_REGISTROS_POR_USUARIO) {
      doUsuario
        .sort((a, b) => a.em - b.em)
        .slice(0, doUsuario.length - LIMITE_DE_REGISTROS_POR_USUARIO)
        .forEach((r) => this.remover(r.chave));
    }
  }

  private chavesDoArmazem(): string[] {
    const chaves: string[] = [];
    try {
      for (let i = 0; this.storage && i < this.storage.length; i += 1) {
        const chave = this.storage.key(i);
        if (chave?.startsWith(`${PREFIXO_DO_ARMAZEM}:`)) {
          chaves.push(chave);
        }
      }
    } catch {
      return [];
    }
    return chaves;
  }
}

// A conversa aberta na tela: vive no sessionStorage (só desta aba), para recarregar a página devolver o atendente onde ele estava.
function chaveDoItemAberto(escopo: EscopoDeUsuario): string {
  return `${PREFIXO_DO_ARMAZEM}:aberta:${encodeURIComponent(escopo.tenant)}:${encodeURIComponent(escopo.usuarioId)}`;
}

export function lerItemAberto(escopo: EscopoDeUsuario): string | null {
  try {
    return sessionStorage.getItem(chaveDoItemAberto(escopo));
  } catch {
    return null;
  }
}

export function gravarItemAberto(escopo: EscopoDeUsuario, itemId: string | null): void {
  try {
    if (itemId === null) {
      sessionStorage.removeItem(chaveDoItemAberto(escopo));
    } else {
      sessionStorage.setItem(chaveDoItemAberto(escopo), itemId);
    }
  } catch {
    // sem sessionStorage: a conversa aberta só não é lembrada
  }
}
