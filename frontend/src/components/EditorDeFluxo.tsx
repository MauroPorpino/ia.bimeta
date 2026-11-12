import '@xyflow/react/dist/style.css';
import { Background, Controls, MiniMap, ReactFlow, ReactFlowProvider, type Connection, type EdgeChange, type NodeChange } from '@xyflow/react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError, descreverErro, type ApiClient } from '../api/client';
import type { FlowGraph, FlowNodeConfig, FlowNodeType, FlowProblem, FlowVariableClass, WorkflowOperationOption, WorkflowSummary } from '../api/types';
import {
  ROTULO_DO_TIPO,
  TIPOS_DE_NO,
  adicionarNo,
  comPosicoes,
  conectar,
  configurarNo,
  definirInicio,
  desconectar,
  documentoDoGrafo,
  explicarProblemas,
  garantirVariavel,
  grafoInicial,
  lerGrafo,
  mudarClasseDaVariavel,
  paraCanvas,
  posicionar,
  posicoesDoCanvas,
  removerNo,
  removerVariavel,
  rotularNo,
  textoDoProblema,
  type NoDoCanvas,
} from '../fluxo';
import { NoDoFluxo } from './NoDoFluxo';
import { PainelDoNoDeFluxo } from './PainelDoNoDeFluxo';
import { SimuladorDeFluxo } from './SimuladorDeFluxo';

const TIPOS_DE_NO_DO_CANVAS = { fluxo: NoDoFluxo };

type Aba = 'no' | 'variaveis' | 'problemas' | 'simular';

export interface FluxoEmEdicao {
  /** O fluxo carregado (rascunho, publicada ou aposentada); nulo para um fluxo novo. */
  existente: WorkflowSummary | null;
  organizationId: string;
  key: string;
  name: string;
}

interface Props {
  api: ApiClient;
  fluxo: FluxoEmEdicao;
  onFechar: () => void;
  /** O servidor aceitou uma gravação, publicação ou aposentadoria: a lista deve recarregar. */
  onAlterado: () => void;
}

function documentoInicial(fluxo: FluxoEmEdicao): FlowGraph {
  return comPosicoes(lerGrafo(fluxo.existente?.graphJson) ?? grafoInicial());
}

/**
 * O editor visual de um fluxo (ADR 0037). O canvas (React Flow) só mostra e edita o MESMO documento de grafo que o servidor valida: nada de regra de negócio mora aqui. Uma versão publicada ou aposentada
 * abre somente para leitura; editar cria uma versão nova em rascunho. Salvar não exige o fluxo completo (o servidor devolve os problemas por nó); publicar exige zero problemas.
 */
export function EditorDeFluxo(props: Props) {
  return (
    <ReactFlowProvider>
      <EditorInterno {...props} />
    </ReactFlowProvider>
  );
}

function EditorInterno({ api, fluxo, onFechar, onAlterado }: Props) {
  const [existente, setExistente] = useState<WorkflowSummary | null>(fluxo.existente);
  const [grafo, setGrafo] = useState<FlowGraph>(() => documentoInicial(fluxo));
  const [nome, setNome] = useState(fluxo.name);
  const [referencia, setReferencia] = useState(() => `${fluxo.name}|${JSON.stringify(documentoDoGrafo(documentoInicial(fluxo)))}`);
  const [problemas, setProblemas] = useState<FlowProblem[]>([]);
  const [visitados, setVisitados] = useState<string[]>([]);
  const [selecionado, setSelecionado] = useState<string | null>(null);
  const [aba, setAba] = useState<Aba>('no');
  const [operacoes, setOperacoes] = useState<WorkflowOperationOption[]>([]);
  const [ocupado, setOcupado] = useState(false);
  const [mensagem, setMensagem] = useState<string | null>(null);
  const [erros, setErros] = useState<string[]>([]);
  const [novaVariavel, setNovaVariavel] = useState('');
  const [classeNova, setClasseNova] = useState<FlowVariableClass>('personal');

  const somenteLeitura = existente !== null && existente.status !== 'Draft';
  const atual = `${nome}|${JSON.stringify(documentoDoGrafo(grafo))}`;
  const sujo = atual !== referencia;
  const noSelecionado = grafo.nodes.find((n) => n.id === selecionado) ?? null;

  useEffect(() => {
    let vivo = true;
    api
      .operacoesParaFluxos(fluxo.organizationId)
      .then((lista) => {
        if (vivo) {
          setOperacoes(lista);
        }
      })
      .catch(() => {
        // Sem permissão de integração, a lista simplesmente fica vazia: o servidor também recusa a publicação de um nó que não consegue conferir.
      });
    return () => {
      vivo = false;
    };
  }, [api, fluxo.organizationId]);

  const { nos, arestas } = useMemo(() => paraCanvas(grafo, problemas, visitados), [grafo, problemas, visitados]);

  const aoMudarNos = useCallback(
    (mudancas: NodeChange<NoDoCanvas>[]) => {
      for (const m of mudancas) {
        if (m.type === 'select' && m.selected) {
          setSelecionado(m.id);
        }
      }
      if (somenteLeitura) {
        return;
      }
      setGrafo((g) => {
        let proximo = g;
        for (const m of mudancas) {
          if (m.type === 'position' && m.position) {
            proximo = posicoesDoCanvas(proximo, [{ id: m.id, position: m.position }]);
          } else if (m.type === 'remove') {
            proximo = removerNo(proximo, m.id);
          }
        }
        return proximo;
      });
      if (mudancas.some((m) => m.type === 'remove' && m.id === selecionado)) {
        setSelecionado(null);
      }
    },
    [somenteLeitura, selecionado],
  );

  const aoMudarArestas = useCallback(
    (mudancas: EdgeChange[]) => {
      if (somenteLeitura) {
        return;
      }
      setGrafo((g) => {
        let proximo = g;
        for (const m of mudancas) {
          if (m.type === 'remove') {
            const i = m.id.lastIndexOf(':');
            proximo = desconectar(proximo, m.id.slice(0, i), m.id.slice(i + 1));
          }
        }
        return proximo;
      });
    },
    [somenteLeitura],
  );

  const aoConectar = useCallback(
    (c: Connection) => {
      if (!somenteLeitura && c.source && c.sourceHandle && c.target) {
        setGrafo((g) => conectar(g, c.source, c.sourceHandle!, c.target));
      }
    },
    [somenteLeitura],
  );

  function acrescentar(tipo: FlowNodeType) {
    const baixo = Math.max(-190, ...grafo.nodes.map((n) => n.position?.y ?? 0));
    const r = adicionarNo(grafo, tipo, { x: 0, y: baixo + 190 });
    setGrafo(r.grafo);
    setSelecionado(r.id);
    setAba('no');
  }

  async function executar<T>(acao: () => Promise<T>): Promise<T | undefined> {
    setOcupado(true);
    setErros([]);
    setMensagem(null);
    try {
      return await acao();
    } catch (e) {
      if (e instanceof ApiError && e.status === 400 && e.problemas.length > 0) {
        setErros(explicarProblemas(e.problemas));
      } else if (e instanceof ApiError && e.status === 409) {
        setErros([e.code === 'changed' ? 'Este rascunho foi alterado por outra pessoa. Feche e abra de novo para reler.' : descreverErro(e)]);
      } else if (e instanceof ApiError && e.status === 403) {
        setErros(['Você não tem permissão para esta operação com fluxos. Peça a um administrador da empresa.']);
      } else {
        setErros([descreverErro(e)]);
      }
      return undefined;
    } finally {
      setOcupado(false);
    }
  }

  async function salvar(): Promise<WorkflowSummary | undefined> {
    const resultado = await executar(() =>
      api.salvarFluxo({
        organizationId: fluxo.organizationId,
        key: fluxo.key,
        name: nome.trim() === '' ? fluxo.key : nome.trim(),
        description: existente?.description ?? null,
        graph: documentoDoGrafo(grafo),
        expectedRevision: existente?.status === 'Draft' ? existente.revision : null,
      }),
    );
    if (!resultado) {
      return undefined;
    }
    setExistente(resultado.workflow);
    setProblemas(resultado.problems);
    setReferencia(`${nome}|${JSON.stringify(documentoDoGrafo(grafo))}`);
    setMensagem(resultado.problems.length === 0 ? 'Rascunho salvo. O fluxo está pronto para publicar.' : `Rascunho salvo. Ainda há ${resultado.problems.length} problema(s) a resolver antes de publicar.`);
    onAlterado();
    if (resultado.problems.length > 0) {
      setAba('problemas');
    }
    return resultado.workflow;
  }

  async function validar() {
    const r = await executar(() => api.validarFluxo(fluxo.organizationId, documentoDoGrafo(grafo)));
    if (r) {
      setProblemas(r.problems);
      setMensagem(r.valid ? 'O fluxo é válido.' : `Há ${r.problems.length} problema(s) no fluxo.`);
      setAba(r.valid ? aba : 'problemas');
    }
  }

  async function publicar() {
    if (!existente || sujo) {
      return;
    }
    const r = await executar(() => api.publicarFluxo(existente.id));
    if (r) {
      setExistente(r);
      setMensagem(`Versão ${r.version} publicada. Ela não pode mais ser alterada: mudanças viram uma nova versão.`);
      onAlterado();
    }
  }

  async function editarNovaVersao() {
    const r = await executar(() =>
      api.salvarFluxo({ organizationId: fluxo.organizationId, key: fluxo.key, name: existente?.name ?? nome, description: existente?.description ?? null, graph: documentoDoGrafo(grafo), expectedRevision: null }),
    );
    if (r) {
      setExistente(r.workflow);
      setProblemas(r.problems);
      setReferencia(`${nome}|${JSON.stringify(documentoDoGrafo(grafo))}`);
      setMensagem(`Criado o rascunho da versão ${r.workflow.version}.`);
      onAlterado();
    }
  }

  function fechar() {
    if (sujo && !window.confirm('Há alterações não salvas. Fechar mesmo assim?')) {
      return;
    }
    onFechar();
  }

  function mudarConfig(config: FlowNodeConfig) {
    if (noSelecionado) {
      setGrafo((g) => configurarNo(g, noSelecionado.id, config));
    }
  }

  const rotuloDoEstado = existente === null ? 'Novo (não salvo)' : existente.status === 'Draft' ? `Rascunho da versão ${existente.version}` : existente.status === 'Published' ? `Versão ${existente.version} publicada` : `Versão ${existente.version} aposentada`;

  return (
    <section className="fluxo-editor" aria-label={`Editor do fluxo ${fluxo.key}`}>
      <header className="fluxo-editor-topo">
        <button type="button" className="btn" onClick={fechar}>
          ← Fluxos
        </button>
        <label htmlFor="fluxo-nome" className="sr-only">
          Nome do fluxo
        </label>
        <input id="fluxo-nome" className="fluxo-nome" value={nome} maxLength={100} disabled={somenteLeitura} onChange={(e) => setNome(e.target.value)} />
        <span className="discreto">
          {fluxo.key} · {rotuloDoEstado}
          {sujo && !somenteLeitura ? ' · alterações não salvas' : ''}
        </span>
        <div className="fluxo-editor-acoes">
          {somenteLeitura ? (
            <button type="button" className="btn btn-primario" disabled={ocupado} onClick={() => void editarNovaVersao()}>
              Editar (cria nova versão)
            </button>
          ) : (
            <>
              <button type="button" className="btn" disabled={ocupado} onClick={() => setGrafo((g) => posicionar(g))}>
                Organizar
              </button>
              <button type="button" className="btn" disabled={ocupado} onClick={() => void validar()}>
                Validar
              </button>
              <button type="button" className="btn btn-primario" disabled={ocupado || (!sujo && existente !== null)} onClick={() => void salvar()}>
                Salvar rascunho
              </button>
              <button
                type="button"
                className="btn"
                disabled={ocupado || existente === null || sujo}
                title={sujo ? 'Salve o rascunho antes de publicar' : undefined}
                onClick={() => void publicar()}
              >
                Publicar
              </button>
            </>
          )}
        </div>
      </header>

      {mensagem && <p role="status" className="fluxo-mensagem">{mensagem}</p>}
      {erros.length > 0 && (
        <div role="alert" className="erro">
          <ul>
            {erros.map((e, i) => (
              <li key={i}>{e}</li>
            ))}
          </ul>
        </div>
      )}

      <div className="fluxo-editor-corpo">
        <aside className="fluxo-paleta" aria-label="Tipos de nó">
          <h3>Adicionar</h3>
          {TIPOS_DE_NO.map((t) => (
            <button key={t} type="button" className="btn" disabled={somenteLeitura} onClick={() => acrescentar(t)}>
              + {ROTULO_DO_TIPO[t]}
            </button>
          ))}
        </aside>

        <div className="fluxo-canvas" data-testid="canvas-do-fluxo">
          <ReactFlow
            nodes={nos}
            edges={arestas}
            nodeTypes={TIPOS_DE_NO_DO_CANVAS}
            onNodesChange={aoMudarNos}
            onEdgesChange={aoMudarArestas}
            onConnect={aoConectar}
            nodesDraggable={!somenteLeitura}
            nodesConnectable={!somenteLeitura}
            deleteKeyCode={somenteLeitura ? null : 'Delete'}
            onPaneClick={() => setSelecionado(null)}
            fitView
            fitViewOptions={{ padding: 0.15 }}
            minZoom={0.2}
            proOptions={{ hideAttribution: true }}
          >
            <Background />
            <Controls showInteractive={false} />
            <MiniMap pannable zoomable nodeColor="#8b97a8" maskColor="rgba(120, 130, 150, 0.15)" />
          </ReactFlow>
        </div>

        <aside className="fluxo-lateral">
          <nav className="fluxo-abas" aria-label="Painéis do editor">
            {(
              [
                ['no', 'Nó'],
                ['variaveis', 'Variáveis'],
                ['problemas', `Problemas (${problemas.length})`],
                ['simular', 'Simular'],
              ] as Array<[Aba, string]>
            ).map(([id, rotulo]) => (
              <button key={id} type="button" className="area-aba" aria-current={aba === id ? 'page' : undefined} onClick={() => setAba(id)}>
                {rotulo}
              </button>
            ))}
          </nav>

          {aba === 'no' &&
            (noSelecionado ? (
              <PainelDoNoDeFluxo
                key={noSelecionado.id}
                no={noSelecionado}
                grafo={grafo}
                operacoes={operacoes}
                somenteLeitura={somenteLeitura}
                onConfig={mudarConfig}
                onRotulo={(r) => setGrafo((g) => rotularNo(g, noSelecionado.id, r))}
                onInicio={() => setGrafo((g) => definirInicio(g, noSelecionado.id))}
                onRemover={() => {
                  setGrafo((g) => removerNo(g, noSelecionado.id));
                  setSelecionado(null);
                }}
              />
            ) : (
              <p className="discreto">Clique em um nó para editar. Arraste de uma saída até outro nó para conectar; cada saída tem uma só conexão.</p>
            ))}

          {aba === 'variaveis' && (
            <section aria-label="Variáveis do fluxo">
              <p className="discreto">
                Variáveis pessoais (respostas livres do cliente) são cifradas em repouso e nunca aparecem em logs, auditoria nem em respostas administrativas. Variáveis públicas vêm do próprio fluxo ou de operações públicas.
              </p>
              <ul className="fluxo-variaveis">
                {grafo.variables.map((v) => (
                  <li key={v.name} className="fluxo-linha">
                    <code>{v.name}</code>
                    <select aria-label={`Classe de ${v.name}`} disabled={somenteLeitura} value={v.class} onChange={(e) => setGrafo((g) => mudarClasseDaVariavel(g, v.name, e.target.value as FlowVariableClass))}>
                      <option value="personal">Pessoal</option>
                      <option value="public">Pública</option>
                    </select>
                    <button type="button" className="btn" aria-label={`Remover variável ${v.name}`} disabled={somenteLeitura} onClick={() => setGrafo((g) => removerVariavel(g, v.name))}>
                      ×
                    </button>
                  </li>
                ))}
              </ul>
              {!somenteLeitura && (
                <div className="fluxo-linha">
                  <input aria-label="Nome da nova variável" value={novaVariavel} maxLength={64} onChange={(e) => setNovaVariavel(e.target.value.toLowerCase().replace(/[^a-z0-9_.]/g, ''))} />
                  <select aria-label="Classe da nova variável" value={classeNova} onChange={(e) => setClasseNova(e.target.value as FlowVariableClass)}>
                    <option value="personal">Pessoal</option>
                    <option value="public">Pública</option>
                  </select>
                  <button
                    type="button"
                    className="btn"
                    disabled={!/^[a-z][a-z0-9_.]{0,63}$/.test(novaVariavel)}
                    onClick={() => {
                      setGrafo((g) => garantirVariavel(g, novaVariavel, classeNova));
                      setNovaVariavel('');
                    }}
                  >
                    Declarar
                  </button>
                </div>
              )}
            </section>
          )}

          {aba === 'problemas' && (
            <section aria-label="Problemas do fluxo">
              {problemas.length === 0 ? (
                <p className="discreto">Nenhum problema conhecido. Use "Validar" ou "Salvar rascunho" para conferir com o servidor.</p>
              ) : (
                <ul className="fluxo-problemas">
                  {problemas.map((p, i) => (
                    <li key={i}>
                      {p.nodeId ? (
                        <button type="button" className="link" onClick={() => { setSelecionado(p.nodeId); setAba('no'); }}>
                          {p.nodeId}
                        </button>
                      ) : (
                        <strong>Fluxo</strong>
                      )}{' '}
                      — {textoDoProblema(p.code)}
                    </li>
                  ))}
                </ul>
              )}
            </section>
          )}

          {aba === 'simular' && (
            <SimuladorDeFluxo
              api={api}
              organizationId={fluxo.organizationId}
              grafo={grafo}
              onResultado={(v, p) => {
                setVisitados(v);
                if (p.length > 0) {
                  setProblemas(p);
                }
              }}
            />
          )}
        </aside>
      </div>
    </section>
  );
}
