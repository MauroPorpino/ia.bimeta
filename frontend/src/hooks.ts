import { useCallback, useEffect, useRef, useState } from 'react';
import type { ApiClient } from './api/client';
import { ApiError } from './api/client';
import { CacheDeConversas } from './cache';

/** Uma memória de conversas e rascunhos por montagem da área logada (some no logout). */
export function useCacheDeConversas(): CacheDeConversas {
  const ref = useRef<CacheDeConversas | null>(null);
  ref.current ??= new CacheDeConversas();
  return ref.current;
}

export type EstadoDaConexao = 'online' | 'offline' | 'restabelecida';

const MS_DA_FAIXA_RESTABELECIDA = 4000;

/**
 * O estado da conexão com a API, visto pelos carregamentos periódicos (`semConexao`: algum falhou por não alcançar o
 * servidor) e pelo próprio navegador (eventos online/offline). Quando a conexão volta, mostra "restabelecida" por
 * alguns segundos e devolve um `pulso` novo, que o chamador usa para reler tudo na hora, sem esperar o próximo ciclo.
 * O pulso também muda quando a aba volta a ficar visível (o navegador atrasa os temporizadores das abas escondidas).
 */
export function useEstadoDaConexao(semConexao: boolean): { estado: EstadoDaConexao; pulso: number } {
  const [navegadorOffline, setNavegadorOffline] = useState(() => typeof navigator !== 'undefined' && navigator.onLine === false);
  const [restabelecida, setRestabelecida] = useState(false);
  const [pulso, setPulso] = useState(0);
  const estavaSemConexao = useRef(false);

  const fora = semConexao || navegadorOffline;

  useEffect(() => {
    const voltou = () => {
      setNavegadorOffline(false);
      setPulso((p) => p + 1);
    };
    const caiu = () => setNavegadorOffline(true);
    const visivel = () => {
      if (document.visibilityState === 'visible') {
        setPulso((p) => p + 1);
      }
    };
    window.addEventListener('online', voltou);
    window.addEventListener('offline', caiu);
    document.addEventListener('visibilitychange', visivel);
    return () => {
      window.removeEventListener('online', voltou);
      window.removeEventListener('offline', caiu);
      document.removeEventListener('visibilitychange', visivel);
    };
  }, []);

  useEffect(() => {
    if (fora) {
      estavaSemConexao.current = true;
      setRestabelecida(false);
      return undefined;
    }
    if (!estavaSemConexao.current) {
      return undefined;
    }
    estavaSemConexao.current = false;
    setRestabelecida(true);
    setPulso((p) => p + 1);
    const temporizador = setTimeout(() => setRestabelecida(false), MS_DA_FAIXA_RESTABELECIDA);
    return () => clearTimeout(temporizador);
  }, [fora]);

  return { estado: fora ? 'offline' : restabelecida ? 'restabelecida' : 'online', pulso };
}

/**
 * Chama `carregar` agora e, depois de cada conclusão, de novo após `intervaloMs` (polling sem sobreposição e sem WebSocket).
 * Reinicia quando `chave` muda e para ao desmontar. `carregar` recebe `ativo()`: um resultado atrasado de uma execução
 * antiga (a chave mudou ou o componente saiu) deve ser descartado, sem gravar estado.
 */
export function usePolling(carregar: (ativo: () => boolean) => Promise<void>, intervaloMs: number, chave: unknown): void {
  const carregarRef = useRef(carregar);
  carregarRef.current = carregar;

  useEffect(() => {
    let vivo = true;
    let temporizador: ReturnType<typeof setTimeout> | undefined;

    const ciclo = async () => {
      try {
        await carregarRef.current(() => vivo);
      } catch {
        // o chamador já trata e mostra o erro; o polling continua para se recuperar sozinho
      }
      if (vivo) {
        temporizador = setTimeout(() => void ciclo(), intervaloMs);
      }
    };
    void ciclo();

    return () => {
      vivo = false;
      if (temporizador !== undefined) {
        clearTimeout(temporizador);
      }
    };
  }, [chave, intervaloMs]);
}

/**
 * Nomes dos contatos por item. A lista de itens não traz o nome (só a sessão traz), então ele é buscado uma vez por item,
 * um de cada vez, e guardado. Falha definitiva (item encerrado, fora do alcance) não é repetida; falha de rede é.
 */
export function useNomesDosContatos(api: ApiClient) {
  const [nomes, setNomes] = useState<Record<string, string>>({});
  const conhecidos = useRef(new Set<string>());
  const fila = useRef<string[]>([]);
  const rodando = useRef(false);
  const vivo = useRef(true);

  useEffect(() => {
    vivo.current = true;
    return () => {
      vivo.current = false;
    };
  }, []);

  const processar = useCallback(async () => {
    if (rodando.current) {
      return;
    }
    rodando.current = true;
    try {
      for (let id = fila.current.shift(); id !== undefined && vivo.current; id = fila.current.shift()) {
        try {
          const conversa = await api.obterConversa(id);
          if (conversa.contactDisplayName && vivo.current) {
            const nome = conversa.contactDisplayName;
            setNomes((atual) => ({ ...atual, [id]: nome }));
          }
        } catch (erro) {
          if (erro instanceof ApiError && erro.status === 0) {
            conhecidos.current.delete(id);
          }
        }
      }
    } finally {
      rodando.current = false;
    }
  }, [api]);

  const garantir = useCallback(
    (itemIds: string[]) => {
      for (const id of itemIds) {
        if (!conhecidos.current.has(id)) {
          conhecidos.current.add(id);
          fila.current.push(id);
        }
      }
      void processar();
    },
    [processar],
  );

  return { nomes, garantir };
}
