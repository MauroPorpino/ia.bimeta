import { useCallback, useEffect, useState } from 'react';
import { descreverErro, type ApiClient } from '../api/client';
import type { FlowFunnelRow, FlowMetricRow } from '../api/types';

const PERIODOS = [1, 7, 30];

function percentual(parte: number, total: number): string {
  return total === 0 ? '—' : `${Math.round((parte / total) * 100)}%`;
}

/**
 * As métricas dos fluxos (ADR 0037 §7): por fluxo e versão, quantas conversas começaram, terminaram, foram transferidas, entregues ao agente, falharam ou foram canceladas, e o funil por nó (quantas
 * passaram por cada nó). Só contagens: nada de conteúdo de cliente.
 */
export function MetricasDeFluxos({ api }: { api: ApiClient }) {
  const [dias, setDias] = useState(7);
  const [linhas, setLinhas] = useState<FlowMetricRow[] | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [funil, setFunil] = useState<{ chave: string; versao: number; nos: FlowFunnelRow[] } | null>(null);

  const carregar = useCallback(async () => {
    try {
      setLinhas(await api.metricasDosFluxos(dias));
      setErro(null);
    } catch (e) {
      setErro(descreverErro(e));
    }
  }, [api, dias]);

  useEffect(() => {
    void carregar();
  }, [carregar]);

  async function abrirFunil(l: FlowMetricRow) {
    try {
      setFunil({ chave: l.workflowKey, versao: l.versionNumber, nos: await api.funilDoFluxo(l.workflowKey, l.versionNumber, dias) });
    } catch (e) {
      setErro(descreverErro(e));
    }
  }

  return (
    <section className="fluxo-metricas" aria-label="Métricas dos fluxos">
      <header className="fluxos-topo">
        <h3>Métricas</h3>
        <div className="fluxo-linha">
          <label htmlFor="met-dias">Período</label>
          <select id="met-dias" value={dias} onChange={(e) => { setDias(Number(e.target.value)); setFunil(null); }}>
            {PERIODOS.map((p) => (
              <option key={p} value={p}>
                {p === 1 ? 'Último dia' : `Últimos ${p} dias`}
              </option>
            ))}
          </select>
        </div>
      </header>
      {erro && <p role="alert" className="erro">{erro}</p>}
      {linhas === null && !erro && <p className="discreto">Carregando...</p>}
      {linhas !== null && linhas.length === 0 && <p className="discreto">Nenhuma conversa passou por um fluxo neste período.</p>}
      {linhas !== null && linhas.length > 0 && (
        <table className="tabela-fluxos">
          <caption className="sr-only">Métricas por fluxo e versão</caption>
          <thead>
            <tr>
              <th scope="col">Fluxo</th>
              <th scope="col">Começaram</th>
              <th scope="col">Em andamento</th>
              <th scope="col">Concluídas</th>
              <th scope="col">Transferidas</th>
              <th scope="col">Ao agente</th>
              <th scope="col">Falhas</th>
              <th scope="col">Canceladas</th>
              <th scope="col">Funil</th>
            </tr>
          </thead>
          <tbody>
            {linhas.map((l) => (
              <tr key={`${l.workflowKey}-${l.versionNumber}`}>
                <td>
                  <code>{l.workflowKey}</code> v{l.versionNumber}
                </td>
                <td>{l.started}</td>
                <td>{l.active}</td>
                <td>
                  {l.completed} ({percentual(l.completed, l.started)})
                </td>
                <td>
                  {l.handedOff} ({percentual(l.handedOff, l.started)})
                </td>
                <td>
                  {l.delegated} ({percentual(l.delegated, l.started)})
                </td>
                <td>
                  {l.failed} ({percentual(l.failed, l.started)})
                </td>
                <td>{l.cancelled}</td>
                <td>
                  <button type="button" className="btn" aria-label={`Ver o funil de ${l.workflowKey} v${l.versionNumber}`} onClick={() => void abrirFunil(l)}>
                    Ver funil
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {funil && (
        <section className="fluxo-detalhe" aria-label={`Funil de ${funil.chave} v${funil.versao}`}>
          <h4>
            Funil de {funil.chave} v{funil.versao}
          </h4>
          {funil.nos.length === 0 ? (
            <p className="discreto">Nenhuma passagem registrada neste período.</p>
          ) : (
            <table className="tabela-fluxos">
              <caption className="sr-only">Passagens por nó</caption>
              <thead>
                <tr>
                  <th scope="col">Nó</th>
                  <th scope="col">Passagens</th>
                </tr>
              </thead>
              <tbody>
                {funil.nos.map((n) => (
                  <tr key={n.nodeId}>
                    <td>{n.nodeId}</td>
                    <td>{n.visits}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <button type="button" className="btn" onClick={() => setFunil(null)}>
            Fechar
          </button>
        </section>
      )}
    </section>
  );
}
