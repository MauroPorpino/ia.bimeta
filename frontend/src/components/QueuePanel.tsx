import { useEffect, useMemo, useState } from 'react';
import { descreverErro, type ApiClient } from '../api/client';
import type { AgentPresence, AgentStatus, CurrentUser, QueueItem, ServiceQueue } from '../api/types';
import { ROTULO_PRESENCA, rotuloSemNome, situacaoDoItem, tempoDecorrido } from '../format';
import { usePolling } from '../hooks';

export type Aba = 'waiting' | 'mine';

interface Props {
  api: ApiClient;
  me: CurrentUser;
  itemSelecionado: string | null;
  onSelecionar: (itemId: string) => void;
  nomes: Record<string, string>;
  garantirNomes: (itemIds: string[]) => void;
  /** Erro de carregamento periódico (some sozinho quando o polling se recupera). `null` limpa. */
  onErroDeRede: (origem: string, texto: string | null) => void;
  /** Erro de uma ação do atendente (fica até ser dispensado). */
  onErroDeAcao: (texto: string | null) => void;
  /** Muda para forçar uma releitura imediata (depois de uma ação na conversa). */
  versaoDaLista: number;
  intervaloMs?: number;
}

const POR_ABA: Record<Aba, { rotulo: string; vazio: string }> = {
  waiting: { rotulo: 'Aguardando', vazio: 'Nenhum atendimento aguardando.' },
  mine: { rotulo: 'Meus', vazio: 'Você não tem atendimentos em andamento.' },
};

export function QueuePanel({
  api,
  me,
  itemSelecionado,
  onSelecionar,
  nomes,
  garantirNomes,
  onErroDeRede,
  onErroDeAcao,
  versaoDaLista,
  intervaloMs = 3000,
}: Props) {
  const [aba, setAba] = useState<Aba>('waiting');
  const [filaId, setFilaId] = useState('');
  const [filas, setFilas] = useState<ServiceQueue[]>([]);
  const [itens, setItens] = useState<QueueItem[] | null>(null);
  const [presenca, setPresenca] = useState<AgentPresence | null>(null);
  const [mudandoPresenca, setMudandoPresenca] = useState(false);

  // Filas do atendente e presença: uma vez ao abrir.
  useEffect(() => {
    let vivo = true;
    api
      .listarMinhasFilas()
      .then((lista) => {
        if (vivo) {
          setFilas(lista);
          onErroDeRede('filas', null);
        }
      })
      .catch((erro: unknown) => {
        if (vivo) {
          onErroDeRede('filas', descreverErro(erro));
        }
      });
    api
      .obterPresenca()
      .then((p) => {
        if (vivo) {
          setPresenca(p);
          onErroDeRede('presenca', null);
        }
      })
      .catch((erro: unknown) => {
        if (vivo) {
          onErroDeRede('presenca', descreverErro(erro));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, onErroDeRede]);

  // A lista: a cada 3 s, e na hora quando a aba, o filtro ou a versão mudam.
  usePolling(
    async (ativo) => {
      try {
        const lista = await api.listarItens({
          status: aba === 'waiting' ? 'Waiting' : 'Assigned',
          assignedToMe: aba === 'mine',
          queueId: filaId || undefined,
          take: 50,
        });
        if (ativo()) {
          setItens(lista);
          onErroDeRede('fila', null);
          garantirNomes(lista.map((i) => i.id));
        }
      } catch (erro) {
        if (ativo()) {
          onErroDeRede('fila', descreverErro(erro));
        }
      }
    },
    intervaloMs,
    `${aba}|${filaId}|${versaoDaLista}`,
  );

  const nomeDaFila = useMemo(() => new Map(filas.map((f) => [f.id, f.name])), [filas]);

  function trocarAba(nova: Aba) {
    if (nova !== aba) {
      setItens(null);
      setAba(nova);
    }
  }

  function trocarFila(id: string) {
    setItens(null);
    setFilaId(id);
  }

  async function trocarPresenca(status: AgentStatus) {
    if (mudandoPresenca) {
      return;
    }
    setMudandoPresenca(true);
    try {
      setPresenca(await api.definirPresenca(status));
      onErroDeAcao(null);
    } catch (erro) {
      onErroDeAcao(descreverErro(erro));
    } finally {
      setMudandoPresenca(false);
    }
  }

  const agora = Date.now();

  return (
    <section className="painel-fila" aria-label="Fila de atendimento">
      <div className="presenca">
        <label>
          Minha presença
          <select
            value={presenca?.status ?? ''}
            onChange={(e) => void trocarPresenca(e.target.value as AgentStatus)}
            disabled={presenca === null || mudandoPresenca}
          >
            {presenca === null && <option value="">...</option>}
            {(Object.keys(ROTULO_PRESENCA) as AgentStatus[]).map((s) => (
              <option key={s} value={s}>
                {ROTULO_PRESENCA[s]}
              </option>
            ))}
          </select>
        </label>
      </div>

      <div className="abas" role="tablist" aria-label="Situação dos atendimentos">
        {(Object.keys(POR_ABA) as Aba[]).map((chave) => (
          <button
            key={chave}
            type="button"
            role="tab"
            id={`aba-${chave}`}
            aria-selected={aba === chave}
            aria-controls="lista-itens"
            className={aba === chave ? 'aba ativa' : 'aba'}
            onClick={() => trocarAba(chave)}
          >
            {POR_ABA[chave].rotulo}
          </button>
        ))}
      </div>

      <label className="filtro-fila">
        Fila
        <select value={filaId} onChange={(e) => trocarFila(e.target.value)}>
          <option value="">Todas as minhas filas</option>
          {filas.map((f) => (
            <option key={f.id} value={f.id}>
              {f.name}
            </option>
          ))}
        </select>
      </label>

      <div id="lista-itens" role="tabpanel" aria-labelledby={`aba-${aba}`} className="lista-itens">
        {itens === null && <p className="discreto centro">Carregando...</p>}
        {itens !== null && itens.length === 0 && <p className="discreto centro">{POR_ABA[aba].vazio}</p>}
        {itens !== null && itens.length > 0 && (
          <ul>
            {itens.map((item) => {
              const desde = item.status === 'Assigned' ? (item.assignedAt ?? item.enteredAt) : item.enteredAt;
              const nome = nomes[item.id] ?? rotuloSemNome(item.id);
              return (
                <li key={item.id}>
                  <button
                    type="button"
                    className={item.id === itemSelecionado ? 'item ativo' : 'item'}
                    aria-current={item.id === itemSelecionado ? 'true' : undefined}
                    onClick={() => onSelecionar(item.id)}
                  >
                    <span className="item-nome">{nome}</span>
                    <span className="item-meta">{nomeDaFila.get(item.queueId) ?? 'Fila'}</span>
                    <span className="item-meta">
                      {situacaoDoItem(item, me.id)} {item.status === 'Closed' ? '' : tempoDecorrido(desde, agora)}
                    </span>
                    {item.priority > 0 && <span className="etiqueta">prioridade {item.priority}</span>}
                    {item.pending && <span className="etiqueta etiqueta-aviso">operação pendente</span>}
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </section>
  );
}
