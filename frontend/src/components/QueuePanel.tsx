import { useEffect, useMemo, useRef, useState } from 'react';
import { descreverErro, ehFalhaDeConexao, type ApiClient } from '../api/client';
import type { AgentPresence, AgentStatus, CurrentUser, Message, QueueItem, ServiceQueue } from '../api/types';
import type { CacheDeConversas } from '../cache';
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
  /** Erro de carregamento periódico (some sozinho quando o polling se recupera). `null` limpa. `semConexao`: a API nem foi alcançada. */
  onErroDeRede: (origem: string, texto: string | null, semConexao?: boolean) => void;
  /** Erro de uma ação do atendente (fica até ser dispensado). */
  onErroDeAcao: (texto: string | null, semConexao?: boolean) => void;
  /** Muda para forçar uma releitura imediata (depois de uma ação na conversa). */
  versaoDaLista: number;
  /** Memória das conversas ("visto até"): com ela, as conversas do atendente com mensagem nova do cliente ganham um selo. */
  cache?: CacheDeConversas;
  intervaloMs?: number;
}

/** Quantas conversas do atendente são consultadas por ciclo para o selo de mensagens novas, e quantas mensagens cada consulta olha. */
const CONVERSAS_PARA_SELO = 15;
const MENSAGENS_PARA_SELO = 20;

/** Mensagens do cliente depois do que o atendente já viu (sem "visto", todas as da página contam). */
export function contarNaoLidas(mensagens: readonly Message[], visto: number | undefined): number {
  return mensagens.filter((m) => m.direction === 'Inbound' && m.messageSeq > (visto ?? 0)).length;
}

const POR_ABA: Record<Aba, { rotulo: string; vazio: string }> = {
  waiting: { rotulo: 'Aguardando', vazio: 'Nenhum atendimento aguardando.' },
  mine: { rotulo: 'Meus', vazio: 'Você não tem atendimentos em andamento.' },
};

/** "Aguardando (3)": o contador só aparece quando há conversas. Um texto só, para o nome acessível da aba sair inteiro. */
function rotuloDaAba(aba: Aba, quantidade: number): string {
  return quantidade > 0 ? `${POR_ABA[aba].rotulo} (${quantidade})` : POR_ABA[aba].rotulo;
}

/** O título da aba do navegador com a contagem de conversas esperando na frente, sem acumular prefixos. */
function tituloComContagem(base: string, aguardando: number): string {
  const limpo = base.replace(/^\(\d+\)\s*/, '');
  return aguardando > 0 ? `(${aguardando}) ${limpo}`.trim() : limpo;
}

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
  cache,
  intervaloMs = 3000,
}: Props) {
  const [aba, setAba] = useState<Aba>('waiting');
  const [filaId, setFilaId] = useState('');
  const [filas, setFilas] = useState<ServiceQueue[]>([]);
  // As duas listas são lidas juntas a cada ciclo: o contador da outra aba avisa de uma conversa nova mesmo com o atendente em "Meus".
  const [listas, setListas] = useState<Record<Aba, QueueItem[] | null>>({ waiting: null, mine: null });
  const [naoLidas, setNaoLidas] = useState<Record<string, number>>({});
  const [presenca, setPresenca] = useState<AgentPresence | null>(null);
  const [mudandoPresenca, setMudandoPresenca] = useState(false);

  // Filas do atendente e presença são lidas uma vez, mas a leitura se REPETE a cada ciclo enquanto não der certo: com a API fora
  // ao abrir a tela, o erro (e a faixa de conexão) sairia sozinho na volta, em vez de ficar preso.
  const filasCarregadas = useRef(false);
  const presencaCarregada = useRef(false);

  // As listas: a cada 3 s, e na hora quando o filtro ou a versão mudam (uma ação na conversa, a conexão que voltou).
  usePolling(
    async (ativo) => {
      if (!filasCarregadas.current) {
        try {
          const lista = await api.listarMinhasFilas();
          if (ativo()) {
            filasCarregadas.current = true;
            setFilas(lista);
            onErroDeRede('filas', null);
          }
        } catch (erro) {
          if (ativo()) {
            onErroDeRede('filas', descreverErro(erro), ehFalhaDeConexao(erro));
          }
        }
      }
      if (!presencaCarregada.current) {
        try {
          const p = await api.obterPresenca();
          if (ativo()) {
            presencaCarregada.current = true;
            setPresenca(p);
            onErroDeRede('presenca', null);
          }
        } catch (erro) {
          if (ativo()) {
            onErroDeRede('presenca', descreverErro(erro), ehFalhaDeConexao(erro));
          }
        }
      }
      try {
        const [aguardando, meus] = await Promise.all([
          api.listarItens({ status: 'Waiting', queueId: filaId || undefined, take: 50 }),
          api.listarItens({ status: 'Assigned', assignedToMe: true, queueId: filaId || undefined, take: 50 }),
        ]);
        if (ativo()) {
          setListas({ waiting: aguardando, mine: meus });
          onErroDeRede('fila', null);
          garantirNomes([...aguardando, ...meus].map((i) => i.id));
        }
        // O selo de mensagens novas das conversas do atendente (a aberta na tela mostra as dela mesma). Falha aqui não é erro.
        if (cache) {
          const alvos = meus.filter((i) => i.id !== itemSelecionado).slice(0, CONVERSAS_PARA_SELO);
          const contagens = await Promise.all(
            alvos.map(async (i) => {
              try {
                const pagina = await api.obterMensagens(i.id, { take: MENSAGENS_PARA_SELO, order: 'NewestFirst' });
                return [i.id, contarNaoLidas(pagina.items, cache.lerVisto(i.id))] as const;
              } catch {
                return null;
              }
            }),
          );
          if (ativo()) {
            setNaoLidas(Object.fromEntries(contagens.filter((c): c is readonly [string, number] => c !== null)));
          }
        }
      } catch (erro) {
        if (ativo()) {
          onErroDeRede('fila', descreverErro(erro), ehFalhaDeConexao(erro));
        }
      }
    },
    intervaloMs,
    `${filaId}|${versaoDaLista}`,
  );

  const itens = listas[aba];
  const aguardandoAgora = listas.waiting?.length ?? 0;

  // O título da aba do navegador mostra quantas conversas esperam: o atendente que está em outra janela vê que chegou trabalho.
  useEffect(() => {
    const original = tituloComContagem(document.title, 0);
    document.title = tituloComContagem(original, aguardandoAgora);
    return () => {
      document.title = original;
    };
  }, [aguardandoAgora]);

  const nomeDaFila = useMemo(() => new Map(filas.map((f) => [f.id, f.name])), [filas]);

  function trocarFila(id: string) {
    setListas({ waiting: null, mine: null });
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
      onErroDeAcao(descreverErro(erro), ehFalhaDeConexao(erro));
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
            onClick={() => setAba(chave)}
          >
            {rotuloDaAba(chave, listas[chave]?.length ?? 0)}
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
                    {item.deliveryFailures > 0 && <span className="etiqueta etiqueta-falha">falha no envio</span>}
                    {item.id !== itemSelecionado && (naoLidas[item.id] ?? 0) > 0 && (
                      <span className="etiqueta etiqueta-nova">
                        {(naoLidas[item.id] ?? 0) >= MENSAGENS_PARA_SELO ? `${MENSAGENS_PARA_SELO}+ novas` : (naoLidas[item.id] ?? 0) === 1 ? '1 nova' : `${naoLidas[item.id]} novas`}
                      </span>
                    )}
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
