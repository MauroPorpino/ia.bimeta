import { useCallback, useEffect, useState } from 'react';
import { descreverErro, type ApiClient } from '../api/client';
import type { FlowInstanceDetail, FlowInstancePage, FlowInstanceStatus } from '../api/types';

export const ROTULO_SITUACAO_DA_INSTANCIA: Record<FlowInstanceStatus, string> = {
  Created: 'Criada',
  WaitingInput: 'Esperando o cliente',
  AwaitingEffect: 'Esperando uma consulta',
  AwaitingAgent: 'Esperando o agente',
  Completed: 'Concluída',
  HandedOff: 'Transferida a um atendente',
  Delegated: 'Entregue ao agente de IA',
  Failed: 'Falhou',
  Cancelled: 'Cancelada',
};

const ROTULO_DO_EFEITO: Record<string, string> = {
  SendMessage: 'Mensagem',
  CallCapability: 'Consulta de integração',
  LookupKnowledge: 'Busca no conhecimento',
  RequestHandoff: 'Transferência',
  DelegateToAgent: 'Entrega ao agente',
  Finish: 'Fim',
};

const ROTULO_DO_ESTADO_DO_EFEITO: Record<string, string> = {
  Pending: 'Pendente',
  InFlight: 'Em andamento',
  Done: 'Feito',
  Failed: 'Falhou',
  Unknown: 'Resultado ambíguo',
  Superseded: 'Descartado',
};

const POR_PAGINA = 25;

/**
 * O histórico de execução dos fluxos (ADR 0037 §7): cada conversa que passou por um fluxo, a versão, o desfecho e, ao abrir, o caminho por nós e os efeitos. Só identificadores, estados e códigos: NUNCA o texto do
 * cliente, as variáveis nem o conteúdo das mensagens (o conteúdo da conversa fica no atendimento, com a permissão dele).
 */
export function HistoricoDeFluxos({ api }: { api: ApiClient }) {
  const [status, setStatus] = useState<FlowInstanceStatus | ''>('');
  const [chave, setChave] = useState('');
  const [pagina, setPagina] = useState(0);
  const [dados, setDados] = useState<FlowInstancePage | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [aberta, setAberta] = useState<FlowInstanceDetail | null>(null);

  const carregar = useCallback(async () => {
    try {
      setDados(await api.listarInstanciasDeFluxo({ status: status === '' ? undefined : status, key: chave.trim() === '' ? undefined : chave.trim(), take: POR_PAGINA, skip: pagina * POR_PAGINA }));
      setErro(null);
    } catch (e) {
      setErro(descreverErro(e));
    }
  }, [api, status, chave, pagina]);

  useEffect(() => {
    void carregar();
  }, [carregar]);

  async function abrir(id: string) {
    try {
      setAberta(await api.detalheDaInstanciaDeFluxo(id));
      setErro(null);
    } catch (e) {
      setErro(descreverErro(e));
    }
  }

  const total = dados?.total ?? 0;
  return (
    <section className="fluxo-historico" aria-label="Histórico de execução dos fluxos">
      <header className="fluxos-topo">
        <h3>Histórico</h3>
      </header>
      <div className="fluxo-linha">
        <label htmlFor="hist-status">Situação</label>
        <select id="hist-status" value={status} onChange={(e) => { setStatus(e.target.value as FlowInstanceStatus | ''); setPagina(0); }}>
          <option value="">Todas</option>
          {(Object.keys(ROTULO_SITUACAO_DA_INSTANCIA) as FlowInstanceStatus[]).map((s) => (
            <option key={s} value={s}>
              {ROTULO_SITUACAO_DA_INSTANCIA[s]}
            </option>
          ))}
        </select>
        <label htmlFor="hist-chave">Fluxo</label>
        <input id="hist-chave" value={chave} placeholder="chave do fluxo" onChange={(e) => { setChave(e.target.value); setPagina(0); }} />
      </div>

      {erro && <p role="alert" className="erro">{erro}</p>}
      {dados === null && !erro && <p className="discreto">Carregando...</p>}
      {dados !== null && dados.items.length === 0 && <p className="discreto">Nenhuma execução encontrada.</p>}
      {dados !== null && dados.items.length > 0 && (
        <table className="tabela-fluxos">
          <caption className="sr-only">Execuções de fluxos</caption>
          <thead>
            <tr>
              <th scope="col">Fluxo</th>
              <th scope="col">Situação</th>
              <th scope="col">Nó</th>
              <th scope="col">Início</th>
              <th scope="col">Fim</th>
              <th scope="col">Detalhe</th>
            </tr>
          </thead>
          <tbody>
            {dados.items.map((i) => (
              <tr key={i.id}>
                <td>
                  <code>{i.workflowKey}</code> v{i.versionNumber}
                </td>
                <td>
                  {ROTULO_SITUACAO_DA_INSTANCIA[i.status]}
                  {i.failureCode ? ` (${i.failureCode})` : ''}
                </td>
                <td>{i.nodeId ?? '—'}</td>
                <td>{new Date(i.createdAt).toLocaleString('pt-BR')}</td>
                <td>{i.endedAt ? new Date(i.endedAt).toLocaleString('pt-BR') : '—'}</td>
                <td>
                  <button type="button" className="btn" aria-label={`Ver o caminho da execução ${i.id.slice(0, 8)}`} onClick={() => void abrir(i.id)}>
                    Ver caminho
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {total > POR_PAGINA && (
        <div className="fluxo-linha">
          <button type="button" className="btn" disabled={pagina === 0} onClick={() => setPagina((p) => p - 1)}>
            Anterior
          </button>
          <span className="discreto">
            Página {pagina + 1} de {Math.ceil(total / POR_PAGINA)}
          </span>
          <button type="button" className="btn" disabled={(pagina + 1) * POR_PAGINA >= total} onClick={() => setPagina((p) => p + 1)}>
            Próxima
          </button>
        </div>
      )}

      {aberta && (
        <section className="fluxo-detalhe" aria-label={`Caminho da execução ${aberta.id.slice(0, 8)}`}>
          <h4>
            {aberta.workflowKey} v{aberta.versionNumber} — {ROTULO_SITUACAO_DA_INSTANCIA[aberta.status]}
          </h4>
          <p className="discreto">Só identificadores, estados e códigos: nenhum texto do cliente.</p>
          <h5>Caminho</h5>
          <ol>
            {aberta.steps.map((s, i) => (
              <li key={i}>
                {s.nodeId} — {s.outcome}
              </li>
            ))}
          </ol>
          <h5>Efeitos</h5>
          <ul>
            {aberta.effects.map((e) => (
              <li key={e.key}>
                {ROTULO_DO_EFEITO[e.kind] ?? e.kind} — {ROTULO_DO_ESTADO_DO_EFEITO[e.state] ?? e.state}
                {e.resultCode ? ` (${e.resultCode})` : ''}
              </li>
            ))}
          </ul>
          <button type="button" className="btn" onClick={() => setAberta(null)}>
            Fechar
          </button>
        </section>
      )}
    </section>
  );
}
