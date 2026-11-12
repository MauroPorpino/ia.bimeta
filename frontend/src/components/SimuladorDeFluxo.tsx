import { useState } from 'react';
import { ApiError, descreverErro, type ApiClient } from '../api/client';
import type { FlowGraph, FlowProblem, SimInput, SimInputKind, SimNodeStub, SimulationResult } from '../api/types';
import { ROTULO_DO_EVENTO, ROTULO_DO_RESULTADO, camposDoTexto, documentoDoGrafo, textoDoProblema } from '../fluxo';

interface Props {
  api: ApiClient;
  organizationId: string;
  grafo: FlowGraph;
  onResultado: (visitados: string[], problemas: FlowProblem[]) => void;
}

interface StubEditavel {
  achou: boolean;
  resposta: string;
  deuCerto: boolean;
  campos: string;
  codigoDeErro: string;
}

const STUB_PADRAO: StubEditavel = { achou: false, resposta: '', deuCerto: false, campos: '', codigoDeErro: '' };

const ROTULO_DO_PASSO: Record<SimInputKind, string> = {
  Say: 'Cliente escreve',
  Choose: 'Cliente toca na opção (id)',
  Media: 'Cliente envia mídia (image, audio...)',
  Wait: 'Passa o tempo (segundos)',
};

/**
 * O simulador do fluxo (ADR 0037 §9): o servidor roda o MESMO interpretador com respostas simuladas. Nenhuma IA, nenhum sistema da empresa e nenhum canal são chamados: o que seria uma chamada vira um
 * evento "(simulado)" com a resposta que você definiu aqui. Os dados digitados são fictícios e não são guardados.
 */
export function SimuladorDeFluxo({ api, organizationId, grafo, onResultado }: Props) {
  const [passos, setPassos] = useState<SimInput[]>([{ kind: 'Say', text: 'oi' }]);
  const [stubs, setStubs] = useState<Record<string, StubEditavel>>({});
  const [executando, setExecutando] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [problemas, setProblemas] = useState<FlowProblem[]>([]);
  const [resultado, setResultado] = useState<SimulationResult | null>(null);

  const assincronos = grafo.nodes.filter((n) => n.type === 'knowledge' || n.type === 'integration');

  function mudarPasso(i: number, parcial: Partial<SimInput>) {
    setPassos((atuais) => atuais.map((p, j) => (j === i ? { ...p, ...parcial } : p)));
  }

  function mudarStub(id: string, parcial: Partial<StubEditavel>) {
    setStubs((atuais) => ({ ...atuais, [id]: { ...(atuais[id] ?? STUB_PADRAO), ...parcial } }));
  }

  async function simular() {
    setExecutando(true);
    setErro(null);
    try {
      const respostas: Record<string, SimNodeStub> = {};
      for (const no of assincronos) {
        const s = stubs[no.id] ?? STUB_PADRAO;
        respostas[no.id] =
          no.type === 'knowledge'
            ? { found: s.achou, answer: s.achou ? s.resposta : null }
            : s.deuCerto
              ? { success: true, fields: camposDoTexto(s.campos) }
              : { success: false, errorCode: s.codigoDeErro.trim() || 'simulated-failure' };
      }
      const r = await api.simularFluxo(organizationId, documentoDoGrafo(grafo), { inputs: passos, stubs: respostas });
      setProblemas(r.problems);
      setResultado(r.simulation);
      onResultado(r.simulation?.visited ?? [], r.problems);
    } catch (e) {
      setResultado(null);
      setErro(e instanceof ApiError && e.status === 429 ? 'Muitas simulações seguidas. Aguarde um minuto.' : descreverErro(e));
    } finally {
      setExecutando(false);
    }
  }

  return (
    <section className="fluxo-simulador" aria-label="Simulador do fluxo">
      <p className="discreto">Simulação sem chamadas reais: nada é enviado a clientes, à IA nem ao sistema da empresa.</p>
      <h3>Conversa simulada</h3>
      <ol className="fluxo-passos">
        {passos.map((p, i) => (
          <li key={i} className="fluxo-linha">
            <select aria-label={`Tipo do passo ${i + 1}`} value={p.kind} onChange={(e) => mudarPasso(i, { kind: e.target.value as SimInputKind })}>
              {(Object.keys(ROTULO_DO_PASSO) as SimInputKind[]).map((k) => (
                <option key={k} value={k}>
                  {ROTULO_DO_PASSO[k]}
                </option>
              ))}
            </select>
            {p.kind === 'Wait' ? (
              <input aria-label={`Segundos do passo ${i + 1}`} type="number" min={0} value={p.seconds ?? 0} onChange={(e) => mudarPasso(i, { seconds: Number(e.target.value) || 0 })} />
            ) : p.kind === 'Choose' ? (
              <input aria-label={`Id da opção do passo ${i + 1}`} value={p.choiceId ?? ''} onChange={(e) => mudarPasso(i, { choiceId: e.target.value })} />
            ) : (
              <input aria-label={`Texto do passo ${i + 1}`} value={p.text ?? ''} onChange={(e) => mudarPasso(i, { text: e.target.value })} />
            )}
            <button type="button" className="btn" aria-label={`Remover passo ${i + 1}`} onClick={() => setPassos((atuais) => atuais.filter((_, j) => j !== i))}>
              ×
            </button>
          </li>
        ))}
      </ol>
      <button type="button" className="btn" disabled={passos.length >= 100} onClick={() => setPassos((atuais) => [...atuais, { kind: 'Say', text: '' }])}>
        + Passo
      </button>

      {assincronos.length > 0 && (
        <>
          <h3>Respostas simuladas</h3>
          {assincronos.map((no) => {
            const s = stubs[no.id] ?? STUB_PADRAO;
            return (
              <fieldset key={no.id} className="fluxo-stub">
                <legend>
                  {no.type === 'knowledge' ? 'Conhecimento' : 'Integração'} — {no.id}
                </legend>
                {no.type === 'knowledge' ? (
                  <>
                    <label className="fluxo-opcao">
                      <input type="checkbox" checked={s.achou} onChange={(e) => mudarStub(no.id, { achou: e.target.checked })} />
                      Encontra resposta
                    </label>
                    {s.achou && <input aria-label={`Resposta simulada de ${no.id}`} value={s.resposta} onChange={(e) => mudarStub(no.id, { resposta: e.target.value })} />}
                  </>
                ) : (
                  <>
                    <label className="fluxo-opcao">
                      <input type="checkbox" checked={s.deuCerto} onChange={(e) => mudarStub(no.id, { deuCerto: e.target.checked })} />
                      A consulta dá certo
                    </label>
                    {s.deuCerto ? (
                      <textarea aria-label={`Campos simulados de ${no.id} (campo=valor)`} rows={3} placeholder="campo=valor, um por linha" value={s.campos} onChange={(e) => mudarStub(no.id, { campos: e.target.value })} />
                    ) : (
                      <input aria-label={`Código de erro simulado de ${no.id}`} placeholder="código do erro (ex.: timeout)" value={s.codigoDeErro} onChange={(e) => mudarStub(no.id, { codigoDeErro: e.target.value })} />
                    )}
                  </>
                )}
              </fieldset>
            );
          })}
        </>
      )}

      <div className="fluxo-linha">
        <button type="button" className="btn btn-primario" disabled={executando} onClick={() => void simular()}>
          {executando ? 'Simulando...' : 'Simular conversa'}
        </button>
      </div>

      {erro && <p role="alert" className="erro">{erro}</p>}
      {problemas.length > 0 && (
        <div role="alert" className="erro">
          <p>O fluxo ainda tem problemas e não pode ser simulado:</p>
          <ul>
            {problemas.map((p, i) => (
              <li key={i}>
                {p.nodeId ? `${p.nodeId} — ` : ''}
                {textoDoProblema(p.code)}
              </li>
            ))}
          </ul>
        </div>
      )}
      {resultado && (
        <div className="fluxo-transcricao" aria-label="Resultado da simulação">
          <p>
            <strong>{ROTULO_DO_RESULTADO[resultado.outcome] ?? resultado.outcome}</strong>
          </p>
          <ol>
            {resultado.events.map((e, i) => (
              <li key={i} className={`evento-${e.kind}`}>
                <span className="evento-quem">{ROTULO_DO_EVENTO[e.kind] ?? e.kind}</span>
                {e.text !== null && <span className="evento-texto">: {e.text}</span>}
                {e.choices && e.choices.length > 0 && <span className="discreto"> [{e.choices.map((c) => c.label).join(' | ')}]</span>}
                {e.data && Object.keys(e.data).length > 0 && <span className="discreto"> {Object.entries(e.data).map(([k, v]) => `${k}=${v}`).join(', ')}</span>}
              </li>
            ))}
          </ol>
        </div>
      )}
    </section>
  );
}
