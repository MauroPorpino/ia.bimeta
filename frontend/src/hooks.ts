import { useCallback, useEffect, useRef, useState } from 'react';
import type { ApiClient } from './api/client';
import { ApiError } from './api/client';

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
