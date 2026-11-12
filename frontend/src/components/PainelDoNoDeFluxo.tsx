import type {
  FlowAnswerType,
  FlowBranch,
  FlowChoice,
  FlowCondition,
  FlowGraph,
  FlowNode,
  FlowNodeConfig,
  FlowOperator,
  WorkflowOperationOption,
} from '../api/types';
import { DESCRICAO_DO_TIPO, ROTULO_DO_TIPO, variaveisConhecidas } from '../fluxo';

const TIPOS_DE_RESPOSTA: Array<{ valor: FlowAnswerType; rotulo: string }> = [
  { valor: 'text', rotulo: 'Texto livre' },
  { valor: 'choice', rotulo: 'Escolha (menu)' },
  { valor: 'number', rotulo: 'Número' },
  { valor: 'email', rotulo: 'E-mail' },
  { valor: 'phone', rotulo: 'Telefone' },
  { valor: 'date', rotulo: 'Data' },
  { valor: 'pattern', rotulo: 'Padrão (expressão)' },
];

const OPERADORES: Array<{ valor: FlowOperator; rotulo: string }> = [
  { valor: 'equals', rotulo: 'é igual a' },
  { valor: 'notEquals', rotulo: 'é diferente de' },
  { valor: 'contains', rotulo: 'contém' },
  { valor: 'isEmpty', rotulo: 'está vazio' },
  { valor: 'isNotEmpty', rotulo: 'não está vazio' },
  { valor: 'greaterThan', rotulo: 'é maior que' },
  { valor: 'lessThan', rotulo: 'é menor que' },
  { valor: 'in', rotulo: 'está na lista' },
  { valor: 'withinBusinessHours', rotulo: 'dentro do horário de atendimento' },
];

const DIAS = ['seg', 'ter', 'qua', 'qui', 'sex', 'sáb', 'dom'];

interface Props {
  no: FlowNode;
  grafo: FlowGraph;
  operacoes: WorkflowOperationOption[];
  somenteLeitura: boolean;
  onConfig: (config: FlowNodeConfig) => void;
  onRotulo: (rotulo: string) => void;
  onInicio: () => void;
  onRemover: () => void;
}

/** O painel de propriedades do nó selecionado. Só edita o documento: quem valida é o servidor (os problemas aparecem na aba "Problemas" e no nó). */
export function PainelDoNoDeFluxo({ no, grafo, operacoes, somenteLeitura, onConfig, onRotulo, onInicio, onRemover }: Props) {
  const cfg: FlowNodeConfig = no.config ?? {};
  const variaveis = variaveisConhecidas(grafo);
  const mudar = (parcial: FlowNodeConfig) => onConfig({ ...cfg, ...parcial });

  return (
    <section className="fluxo-painel-no" aria-label={`Propriedades do nó ${no.id}`}>
      <h3>
        {ROTULO_DO_TIPO[no.type]} <span className="discreto">({no.id})</span>
      </h3>
      <p className="discreto">{DESCRICAO_DO_TIPO[no.type]}</p>
      <fieldset disabled={somenteLeitura} className="fluxo-campos">
        <label htmlFor="no-rotulo">Nome no canvas</label>
        <input id="no-rotulo" value={no.label ?? ''} maxLength={100} onChange={(e) => onRotulo(e.target.value)} />

        {(no.type === 'message' || no.type === 'end' || no.type === 'handoff') && (
          <>
            <label htmlFor="no-texto">{no.type === 'handoff' ? 'Aviso ao cliente (opcional)' : 'Texto'}</label>
            <textarea id="no-texto" rows={4} value={cfg.text ?? ''} onChange={(e) => mudar({ text: e.target.value })} />
            <p className="discreto">Use {'{{variavel}}'} para inserir o valor de uma variável.</p>
          </>
        )}

        {no.type === 'question' && <PerguntaCampos cfg={cfg} variaveis={variaveis} mudar={mudar} />}
        {no.type === 'condition' && <CondicaoCampos cfg={cfg} variaveis={variaveis} mudar={mudar} />}

        {no.type === 'knowledge' && (
          <>
            <label htmlFor="no-consulta">Variável com a pergunta (vazio = a última mensagem do cliente)</label>
            <select id="no-consulta" value={cfg.queryVariable ?? ''} onChange={(e) => mudar({ queryVariable: e.target.value })}>
              <option value="">Última mensagem do cliente</option>
              {variaveis.filter((v) => !v.endsWith('.') && v !== 'input.text').map((v) => (
                <option key={v} value={v}>
                  {v}
                </option>
              ))}
            </select>
            <p className="discreto">O resultado fica em {'{{knowledge.answer}}'}. Só o conhecimento aprovado da empresa é usado.</p>
          </>
        )}

        {no.type === 'integration' && <IntegracaoCampos cfg={cfg} variaveis={variaveis} operacoes={operacoes} mudar={mudar} />}

        {no.type === 'handoff' && (
          <>
            <label htmlFor="no-motivo">Motivo (código)</label>
            <input id="no-motivo" value={cfg.reasonCode ?? ''} maxLength={64} onChange={(e) => mudar({ reasonCode: e.target.value })} />
            <fieldset className="fluxo-resumo">
              <legend>Variáveis que vão no resumo ao atendente</legend>
              {grafo.variables.length === 0 && <p className="discreto">Nenhuma variável declarada.</p>}
              {grafo.variables.map((v) => (
                <label key={v.name} className="fluxo-opcao">
                  <input
                    type="checkbox"
                    checked={(cfg.summaryVariables ?? []).includes(v.name)}
                    onChange={(e) => mudar({ summaryVariables: e.target.checked ? [...(cfg.summaryVariables ?? []), v.name] : (cfg.summaryVariables ?? []).filter((n) => n !== v.name) })}
                  />
                  {v.name}
                </label>
              ))}
            </fieldset>
          </>
        )}

        {no.type === 'ai' && <p>Ao chegar aqui, o agente de IA assume a conversa e o fluxo termina. Esta etapa não tem configuração.</p>}
      </fieldset>

      <div className="fluxo-acoes-no">
        <button type="button" className="btn" disabled={somenteLeitura || grafo.start === no.id} onClick={onInicio}>
          {grafo.start === no.id ? 'É o nó inicial' : 'Tornar nó inicial'}
        </button>
        <button type="button" className="btn" disabled={somenteLeitura} onClick={onRemover}>
          Remover nó
        </button>
      </div>
    </section>
  );
}

interface CamposProps {
  cfg: FlowNodeConfig;
  variaveis: string[];
  mudar: (parcial: FlowNodeConfig) => void;
}

function PerguntaCampos({ cfg, variaveis, mudar }: CamposProps) {
  const tipo = cfg.answerType ?? 'text';
  const escolhas = cfg.choices ?? [];
  const trocarEscolha = (i: number, parcial: Partial<FlowChoice>) => mudar({ choices: escolhas.map((c, j) => (j === i ? { ...c, ...parcial } : c)) });
  void variaveis;
  return (
    <>
      <label htmlFor="no-pergunta">Pergunta</label>
      <textarea id="no-pergunta" rows={3} value={cfg.prompt ?? ''} onChange={(e) => mudar({ prompt: e.target.value })} />
      <label htmlFor="no-tipo-resposta">Tipo de resposta</label>
      <select
        id="no-tipo-resposta"
        value={tipo}
        onChange={(e) => {
          const novo = e.target.value as FlowAnswerType;
          mudar({
            answerType: novo,
            choices: novo === 'choice' && escolhas.length === 0 ? [{ id: 'opcao_1', label: 'Opção 1' }, { id: 'opcao_2', label: 'Opção 2' }] : cfg.choices,
          });
        }}
      >
        {TIPOS_DE_RESPOSTA.map((t) => (
          <option key={t.valor} value={t.valor}>
            {t.rotulo}
          </option>
        ))}
      </select>
      <label htmlFor="no-variavel">Variável da resposta</label>
      <input id="no-variavel" value={cfg.variable ?? ''} maxLength={64} onChange={(e) => mudar({ variable: e.target.value.toLowerCase().replace(/[^a-z0-9_.]/g, '') })} />
      {tipo === 'choice' && (
        <fieldset className="fluxo-escolhas">
          <legend>Opções (até 10; rótulo de até 24 caracteres)</legend>
          {escolhas.map((c, i) => (
            <div key={i} className="fluxo-linha">
              <input aria-label={`Id da opção ${i + 1}`} value={c.id} maxLength={32} onChange={(e) => trocarEscolha(i, { id: e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, '') })} />
              <input aria-label={`Rótulo da opção ${i + 1}`} value={c.label} maxLength={24} onChange={(e) => trocarEscolha(i, { label: e.target.value })} />
              <button type="button" className="btn" aria-label={`Remover opção ${i + 1}`} onClick={() => mudar({ choices: escolhas.filter((_, j) => j !== i) })}>
                ×
              </button>
            </div>
          ))}
          <button type="button" className="btn" disabled={escolhas.length >= 10} onClick={() => mudar({ choices: [...escolhas, { id: `opcao_${escolhas.length + 1}`, label: `Opção ${escolhas.length + 1}` }] })}>
            + Opção
          </button>
        </fieldset>
      )}
      {tipo === 'pattern' && (
        <>
          <label htmlFor="no-padrao">Padrão (expressão regular simples, sem referências nem olhar adiante)</label>
          <input id="no-padrao" value={cfg.pattern ?? ''} maxLength={200} onChange={(e) => mudar({ pattern: e.target.value })} />
        </>
      )}
      <label htmlFor="no-tentativas">Tentativas (1 a 5)</label>
      <input id="no-tentativas" type="number" min={1} max={5} value={cfg.maxAttempts ?? 2} onChange={(e) => mudar({ maxAttempts: Number(e.target.value) || 1 })} />
      <label htmlFor="no-invalido">Texto quando a resposta não serve</label>
      <input id="no-invalido" value={cfg.invalidText ?? ''} onChange={(e) => mudar({ invalidText: e.target.value })} />
      <label htmlFor="no-prazo">Prazo para responder, em segundos (vazio = sem prazo)</label>
      <input
        id="no-prazo"
        type="number"
        min={1}
        value={cfg.timeoutSeconds ?? ''}
        onChange={(e) => mudar({ timeoutSeconds: e.target.value === '' ? null : Math.max(1, Number(e.target.value) || 1) })}
      />
    </>
  );
}

function CondicaoCampos({ cfg, variaveis, mudar }: CamposProps) {
  const ramos = cfg.branches ?? [];
  const trocarRamo = (i: number, parcial: Partial<FlowBranch>) => mudar({ branches: ramos.map((r, j) => (j === i ? { ...r, ...parcial } : r)) });
  const trocarRegra = (i: number, k: number, parcial: Partial<FlowCondition>) =>
    trocarRamo(i, { all: ramos[i]!.all.map((r, j) => (j === k ? { ...r, ...parcial } : r)) });
  const selecionaveis = variaveis.filter((v) => !v.endsWith('.'));
  return (
    <>
      {ramos.map((ramo, i) => (
        <fieldset key={i} className="fluxo-ramo">
          <legend>Ramo {i + 1}</legend>
          <label htmlFor={`ramo-${i}-nome`}>Nome do ramo</label>
          <input id={`ramo-${i}-nome`} value={ramo.port} maxLength={32} onChange={(e) => trocarRamo(i, { port: e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, '') })} />
          {ramo.all.map((regra, k) => (
            <div key={k} className="fluxo-regra">
              {regra.op !== 'withinBusinessHours' && (
                <select aria-label={`Variável da regra ${k + 1} do ramo ${i + 1}`} value={regra.variable ?? ''} onChange={(e) => trocarRegra(i, k, { variable: e.target.value })}>
                  <option value="">Variável...</option>
                  {selecionaveis.map((v) => (
                    <option key={v} value={v}>
                      {v}
                    </option>
                  ))}
                  {regra.variable && !selecionaveis.includes(regra.variable) && <option value={regra.variable}>{regra.variable}</option>}
                </select>
              )}
              <select
                aria-label={`Operador da regra ${k + 1} do ramo ${i + 1}`}
                value={regra.op}
                onChange={(e) => {
                  const op = e.target.value as FlowOperator;
                  trocarRegra(i, k, { op, schedule: op === 'withinBusinessHours' ? (regra.schedule ?? { timeZone: 'America/Sao_Paulo', windows: [{ days: [1, 2, 3, 4, 5], from: '08:00', to: '18:00' }] }) : null });
                }}
              >
                {OPERADORES.map((o) => (
                  <option key={o.valor} value={o.valor}>
                    {o.rotulo}
                  </option>
                ))}
              </select>
              {['equals', 'notEquals', 'contains', 'greaterThan', 'lessThan'].includes(regra.op) && (
                <input aria-label={`Valor da regra ${k + 1} do ramo ${i + 1}`} value={regra.value ?? ''} onChange={(e) => trocarRegra(i, k, { value: e.target.value })} />
              )}
              {regra.op === 'in' && (
                <input
                  aria-label={`Valores da regra ${k + 1} do ramo ${i + 1} (separados por vírgula)`}
                  value={(regra.values ?? []).join(', ')}
                  onChange={(e) => trocarRegra(i, k, { values: e.target.value.split(',').map((v) => v.trim()).filter((v) => v !== '') })}
                />
              )}
              {regra.op === 'withinBusinessHours' && regra.schedule && <HorarioCampos regra={regra} i={i} k={k} trocar={(parcial) => trocarRegra(i, k, parcial)} />}
              <button type="button" className="btn" aria-label={`Remover regra ${k + 1} do ramo ${i + 1}`} disabled={ramo.all.length <= 1} onClick={() => trocarRamo(i, { all: ramo.all.filter((_, j) => j !== k) })}>
                ×
              </button>
            </div>
          ))}
          <div className="fluxo-linha">
            <button type="button" className="btn" onClick={() => trocarRamo(i, { all: [...ramo.all, { variable: '', op: 'equals', value: '' }] })}>
              + E outra regra
            </button>
            <button type="button" className="btn" onClick={() => mudar({ branches: ramos.filter((_, j) => j !== i) })}>
              Remover ramo
            </button>
          </div>
        </fieldset>
      ))}
      <button type="button" className="btn" disabled={ramos.length >= 10} onClick={() => mudar({ branches: [...ramos, { port: `ramo_${ramos.length + 1}`, all: [{ variable: '', op: 'equals', value: '' }] }] })}>
        + Ramo
      </button>
      <p className="discreto">Os ramos são testados na ordem; se nenhum servir, o fluxo segue pela saída "senão".</p>
    </>
  );
}

function HorarioCampos({ regra, i, k, trocar }: { regra: FlowCondition; i: number; k: number; trocar: (p: Partial<FlowCondition>) => void }) {
  const agenda = regra.schedule!;
  const janela = agenda.windows[0] ?? { days: [1, 2, 3, 4, 5], from: '08:00', to: '18:00' };
  const mudarJanela = (parcial: Partial<typeof janela>) => trocar({ schedule: { ...agenda, windows: [{ ...janela, ...parcial }] } });
  return (
    <div className="fluxo-horario">
      <input aria-label={`Fuso horário da regra ${k + 1} do ramo ${i + 1}`} value={agenda.timeZone} onChange={(e) => trocar({ schedule: { ...agenda, timeZone: e.target.value } })} />
      <input aria-label={`Hora inicial da regra ${k + 1} do ramo ${i + 1}`} value={janela.from} maxLength={5} onChange={(e) => mudarJanela({ from: e.target.value })} />
      <input aria-label={`Hora final da regra ${k + 1} do ramo ${i + 1}`} value={janela.to} maxLength={5} onChange={(e) => mudarJanela({ to: e.target.value })} />
      <div className="fluxo-dias">
        {DIAS.map((nome, d) => (
          <label key={nome} className="fluxo-opcao">
            <input
              type="checkbox"
              checked={janela.days.includes(d + 1)}
              onChange={(e) => mudarJanela({ days: e.target.checked ? [...janela.days, d + 1].sort() : janela.days.filter((x) => x !== d + 1) })}
            />
            {nome}
          </label>
        ))}
      </div>
    </div>
  );
}

function IntegracaoCampos({ cfg, variaveis, operacoes, mudar }: CamposProps & { operacoes: WorkflowOperationOption[] }) {
  const elegiveis = operacoes.filter((o) => o.eligible);
  const atual = operacoes.find((o) => o.key === cfg.operationKey);
  const parametros = cfg.parameters ?? {};
  const selecionaveis = variaveis.filter((v) => !v.endsWith('.'));
  return (
    <>
      <label htmlFor="no-operacao">Operação</label>
      <select id="no-operacao" value={cfg.operationKey ?? ''} onChange={(e) => mudar({ operationKey: e.target.value, parameters: {} })}>
        <option value="">Escolha...</option>
        {elegiveis.map((o) => (
          <option key={o.key} value={o.key}>
            {o.name} ({o.key})
          </option>
        ))}
        {cfg.operationKey && !elegiveis.some((o) => o.key === cfg.operationKey) && <option value={cfg.operationKey}>{cfg.operationKey} (não elegível)</option>}
      </select>
      {elegiveis.length === 0 && <p className="discreto">Nenhuma operação pública de leitura está liberada para fluxos nesta organização (cadastre em Integrações e marque "Fluxo" como origem).</p>}
      {atual?.description && <p className="discreto">{atual.description}</p>}
      <label htmlFor="no-prefixo">Prefixo do resultado (ex.: preco → {'{{preco.campo}}'})</label>
      <input id="no-prefixo" value={cfg.resultPrefix ?? ''} maxLength={64} onChange={(e) => mudar({ resultPrefix: e.target.value.toLowerCase().replace(/[^a-z0-9_.]/g, '') })} />
      {atual?.outputFields && atual.outputFields.length > 0 && <p className="discreto">Campos devolvidos: {atual.outputFields.join(', ')}.</p>}
      {(atual?.parameters ?? []).map((p) => {
        const ligacao = parametros[p.name] ?? {};
        const usaVariavel = ligacao.variable !== undefined && ligacao.variable !== null;
        return (
          <div key={p.name} className="fluxo-parametro">
            <span>
              {p.name}
              {p.required ? ' *' : ''}
            </span>
            <select
              aria-label={`Origem do parâmetro ${p.name}`}
              value={usaVariavel ? 'variavel' : ligacao.literal !== undefined && ligacao.literal !== null ? 'fixo' : ''}
              onChange={(e) => {
                const { [p.name]: _removido, ...resto } = parametros;
                mudar({ parameters: e.target.value === '' ? resto : { ...resto, [p.name]: e.target.value === 'variavel' ? { variable: selecionaveis[0] ?? '' } : { literal: '' } } });
              }}
            >
              <option value="">Não ligar</option>
              <option value="variavel">Variável</option>
              <option value="fixo">Valor fixo</option>
            </select>
            {usaVariavel && (
              <select aria-label={`Variável do parâmetro ${p.name}`} value={ligacao.variable ?? ''} onChange={(e) => mudar({ parameters: { ...parametros, [p.name]: { variable: e.target.value } } })}>
                {selecionaveis.map((v) => (
                  <option key={v} value={v}>
                    {v}
                  </option>
                ))}
              </select>
            )}
            {!usaVariavel && ligacao.literal !== undefined && ligacao.literal !== null && (
              <input aria-label={`Valor fixo do parâmetro ${p.name}`} value={ligacao.literal} onChange={(e) => mudar({ parameters: { ...parametros, [p.name]: { literal: e.target.value } } })} />
            )}
          </div>
        );
      })}
    </>
  );
}
