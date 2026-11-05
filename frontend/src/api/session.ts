// Sessão do atendente no navegador: o token fica só em memória e no sessionStorage (some ao fechar a aba).
// Nunca vai para localStorage, URL nem log.

const CHAVE = 'ia-bimeta.sessao';

interface Guardado {
  token: string;
  expiresAt: string;
}

let token: string | null = null;
const ouvintes = new Set<() => void>();

function guardar(valor: Guardado | null): void {
  try {
    if (valor) {
      sessionStorage.setItem(CHAVE, JSON.stringify(valor));
    } else {
      sessionStorage.removeItem(CHAVE);
    }
  } catch {
    // sessionStorage indisponível (modo privado, política do navegador): segue só em memória.
  }
}

/** Retoma a sessão da aba (recarregar a página não derruba o login), descartando token vencido. */
export function restaurarSessao(): string | null {
  if (token) {
    return token;
  }
  try {
    const bruto = sessionStorage.getItem(CHAVE);
    if (!bruto) {
      return null;
    }
    const lido = JSON.parse(bruto) as Partial<Guardado>;
    if (typeof lido.token !== 'string' || typeof lido.expiresAt !== 'string' || Date.parse(lido.expiresAt) <= Date.now()) {
      guardar(null);
      return null;
    }
    token = lido.token;
    return token;
  } catch {
    guardar(null);
    return null;
  }
}

export function definirSessao(novo: string, expiresAt: string): void {
  token = novo;
  guardar({ token: novo, expiresAt });
}

export function obterToken(): string | null {
  return token;
}

/** Apaga a sessão local e avisa quem ouve (o App volta à tela de entrada). */
export function encerrarSessaoLocal(): void {
  const tinha = token !== null;
  token = null;
  guardar(null);
  if (tinha) {
    ouvintes.forEach((ouvinte) => ouvinte());
  }
}

/** O "evento de saída": disparado quando a sessão local é encerrada (401 da API ou logout). */
export function aoSair(ouvinte: () => void): () => void {
  ouvintes.add(ouvinte);
  return () => {
    ouvintes.delete(ouvinte);
  };
}

/** Só para testes: zera o estado do módulo sem disparar o evento. */
export function reiniciarSessaoParaTeste(): void {
  token = null;
  ouvintes.clear();
  try {
    sessionStorage.removeItem(CHAVE);
  } catch {
    // ignora
  }
}
