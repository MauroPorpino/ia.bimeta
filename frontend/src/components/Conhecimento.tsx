import { useEffect, useState } from 'react';
import { ApiError, type ApiClient } from '../api/client';
import type { KnowledgeGapPage, KnowledgeGapStatus } from '../api/types';
import { ROTULO_SITUACAO_DA_LACUNA, ROTULO_TIPO_DA_LACUNA, dataHora, mensagemDeErroDoConhecimento } from '../conhecimento';
import { tempoDecorrido } from '../format';
import { LacunaRevisao } from './LacunaRevisao';
import { RascunhoDaDefinicao } from './RascunhoDaDefinicao';

type Filtro = KnowledgeGapStatus | 'Todas';

const FILTROS: Array<{ valor: Filtro; rotulo: string }> = [
  { valor: 'Open', rotulo: 'Abertas' },
  { valor: 'Promoted', rotulo: 'Enviadas ao rascunho' },
  { valor: 'Dismissed', rotulo: 'Descartadas' },
  { valor: 'Todas', rotulo: 'Todas' },
];

const POR_PAGINA = 20;

interface Props {
  api: ApiClient;
  /** A área está escondida (o administrador está no Atendimento): continua montada para não perder o que ele estava escrevendo. */
  oculto?: boolean;
}

function ocorrencias(quantidade: number): string {
  return quantidade === 1 ? '1 ocorrência' : `${quantidade} ocorrências`;
}

/**
 * A área "Conhecimento" do administrador do tenant (ADR 0030): as lacunas do FAQ (perguntas reais de clientes que o FAQ não respondeu direto),
 * a revisão de cada uma e o rascunho da definição do agente, com a publicação. Nada é publicado por revisar uma lacuna.
 */
export function Conhecimento({ api, oculto = false }: Props) {
  const [filtro, setFiltro] = useState<Filtro>('Open');
  const [deslocamento, setDeslocamento] = useState(0);
  const [pagina, setPagina] = useState<KnowledgeGapPage | null>(null);
  const [erroDaLista, setErroDaLista] = useState<string | null>(null);
  const [semPermissao, setSemPermissao] = useState(false);
  const [recarga, setRecarga] = useState(0);
  const [selecionada, setSelecionada] = useState<string | null>(null);
  const [versaoDoRascunho, setVersaoDoRascunho] = useState(0);

  useEffect(() => {
    let vivo = true;
    api
      .listarLacunasDoFaq({ status: filtro === 'Todas' ? undefined : filtro, take: POR_PAGINA, skip: deslocamento })
      .then((lida) => {
        if (!vivo) {
          return;
        }
        // A última página esvaziou (as lacunas dela foram decididas): volta para a última que ainda tem itens.
        const ultimaComItens = Math.floor((lida.total - 1) / POR_PAGINA) * POR_PAGINA;
        if (lida.items.length === 0 && lida.total > 0 && ultimaComItens < deslocamento) {
          setDeslocamento(ultimaComItens);
          return;
        }
        setPagina(lida);
        setErroDaLista(null);
        setSemPermissao(false);
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErroDaLista(mensagemDeErroDoConhecimento(falha));
          setSemPermissao(falha instanceof ApiError && falha.status === 403);
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, filtro, deslocamento, recarga]);

  function trocarFiltro(novo: Filtro) {
    setPagina(null);
    setDeslocamento(0);
    setFiltro(novo);
  }

  function irPara(novoDeslocamento: number) {
    setPagina(null);
    setDeslocamento(novoDeslocamento);
  }

  const itens = pagina?.items ?? null;
  const total = pagina?.total ?? 0;
  const agora = Date.now();

  return (
    <main className="conhecimento" hidden={oculto}>
      <section className="conhecimento-lista" aria-label="Lacunas do FAQ">
        <h2>Lacunas do FAQ</h2>
        <p className="discreto">Perguntas reais de clientes que o FAQ da empresa não respondeu direto. O texto do cliente não fica guardado aqui: ele é lido do atendimento, se você tiver permissão.</p>

        {!semPermissao && (
          <label className="filtro-fila">
            Situação
            <select value={filtro} onChange={(e) => trocarFiltro(e.target.value as Filtro)}>
              {FILTROS.map((f) => (
                <option key={f.valor} value={f.valor}>
                  {f.rotulo}
                </option>
              ))}
            </select>
          </label>
        )}

        {erroDaLista && (
          <p className="erro-campo" role="alert">
            {erroDaLista}
          </p>
        )}

        <div className="lista-itens">
          {itens === null && !erroDaLista && <p className="discreto centro">Carregando...</p>}
          {itens !== null && itens.length === 0 && (
            <p className="discreto centro">{filtro === 'Open' ? 'Nenhuma lacuna aberta.' : 'Nenhuma lacuna nesta situação.'}</p>
          )}
          {itens !== null && itens.length > 0 && (
            <ul>
              {itens.map((lacuna) => (
                <li key={lacuna.id}>
                  <button
                    type="button"
                    className={lacuna.id === selecionada ? 'item ativo' : 'item'}
                    aria-current={lacuna.id === selecionada ? 'true' : undefined}
                    onClick={() => setSelecionada(lacuna.id)}
                  >
                    <span className="item-nome">{ROTULO_TIPO_DA_LACUNA[lacuna.kind]}</span>
                    <span className="item-meta">{ocorrencias(lacuna.occurrences)}</span>
                    <span className="item-meta" title={dataHora(lacuna.lastSeenAt)}>
                      Última vez: {tempoDecorrido(lacuna.lastSeenAt, agora) || dataHora(lacuna.lastSeenAt)}
                    </span>
                    <span className="item-meta">
                      {ROTULO_SITUACAO_DA_LACUNA[lacuna.status]}
                      {lacuna.status === 'Promoted' && lacuna.promotedVersion !== null ? ` (versão ${lacuna.promotedVersion})` : ''}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>

        {!semPermissao && (
          <div className="paginacao">
            {itens !== null && total > 0 && (
              <span className="discreto">
                {deslocamento + 1} a {deslocamento + itens.length} de {total}
              </span>
            )}
            <div className="acoes">
              <button type="button" className="btn btn-pequeno" disabled={deslocamento === 0} onClick={() => irPara(Math.max(0, deslocamento - POR_PAGINA))}>
                Anterior
              </button>
              <button type="button" className="btn btn-pequeno" disabled={itens === null || deslocamento + itens.length >= total} onClick={() => irPara(deslocamento + POR_PAGINA)}>
                Próxima
              </button>
              <button type="button" className="btn btn-pequeno" onClick={() => setRecarga((r) => r + 1)}>
                Atualizar
              </button>
            </div>
          </div>
        )}
      </section>

      {!semPermissao && (
        <div className="conhecimento-detalhe">
          {selecionada ? (
            <LacunaRevisao
              key={selecionada}
              api={api}
              lacunaId={selecionada}
              onDecidida={() => setRecarga((r) => r + 1)}
              onRascunhoAlterado={() => setVersaoDoRascunho((v) => v + 1)}
            />
          ) : (
            <section className="revisao vazio" aria-label="Revisão da lacuna">
              <p className="discreto centro">Escolha uma lacuna da lista para revisar.</p>
            </section>
          )}
          <RascunhoDaDefinicao api={api} versao={versaoDoRascunho} />
        </div>
      )}
    </main>
  );
}
