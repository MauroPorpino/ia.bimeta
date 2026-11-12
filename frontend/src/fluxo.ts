import type { Edge, Node } from '@xyflow/react';
import type { CurrentUser, FlowGraph, FlowNode, FlowNodeConfig, FlowNodeType, FlowProblem, FlowVariableClass } from './api/types';

/**
 * Funções puras do editor de fluxos (ADR 0037). O editor NÃO decide semântica: o documento do grafo é o contrato e o servidor o valida de forma estrita. Aqui só ficam a conversão entre o grafo e o
 * canvas, as operações de edição (que mantêm o documento coerente: uma aresta por porta, portas que existem) e os rótulos em português.
 */

/** Quem vê a área "Fluxos": administradores (do tenant ou de uma organização) e gerentes. O servidor decide cada operação (403 sem permissão, 404 fora do alcance). */
export function podeVerFluxos(usuario: CurrentUser): boolean {
  return usuario.grants.some((g) => g.role === 'OrganizationAdmin' || g.role === 'Manager');
}

export const TIPOS_DE_NO: FlowNodeType[] = ['message', 'question', 'condition', 'knowledge', 'integration', 'ai', 'handoff', 'end'];

export const ROTULO_DO_TIPO: Record<FlowNodeType, string> = {
  message: 'Mensagem',
  question: 'Pergunta',
  condition: 'Condição',
  knowledge: 'Conhecimento',
  ai: 'IA (agente)',
  integration: 'Integração',
  handoff: 'Transferir',
  end: 'Fim',
};

export const DESCRICAO_DO_TIPO: Record<FlowNodeType, string> = {
  message: 'Envia um texto ao cliente.',
  question: 'Pergunta, valida a resposta e guarda numa variável.',
  condition: 'Escolhe o caminho pelo valor de variáveis (sem script).',
  knowledge: 'Busca a resposta aprovada no conhecimento da empresa.',
  ai: 'Entrega a conversa ao agente de IA e encerra o fluxo.',
  integration: 'Consulta um dado público no sistema da empresa (só leitura).',
  handoff: 'Transfere a conversa para um atendente.',
  end: 'Encerra o fluxo.',
};

export const ROTULO_DA_PORTA: Record<string, string> = {
  next: 'seguir',
  answered: 'respondeu',
  invalid: 'inválida',
  timeout: 'sem resposta',
  else: 'senão',
  found: 'achou',
  not_found: 'não achou',
  success: 'deu certo',
  failure: 'falhou',
};

export function rotuloDaPorta(porta: string): string {
  return ROTULO_DA_PORTA[porta] ?? porta;
}

/** As saídas de um nó (o mesmo cálculo do servidor: pergunta com prazo ganha `timeout`; condição ganha um ramo por regra e `else`). */
export function portasDoNo(no: FlowNode): string[] {
  const cfg = no.config ?? {};
  switch (no.type) {
    case 'message':
      return ['next'];
    case 'question':
      return cfg.timeoutSeconds && cfg.timeoutSeconds > 0 ? ['answered', 'invalid', 'timeout'] : ['answered', 'invalid'];
    case 'condition':
      return [...(cfg.branches ?? []).map((b) => b.port).filter((p) => p !== ''), 'else'];
    case 'knowledge':
      return ['found', 'not_found'];
    case 'integration':
      return ['success', 'failure'];
    default:
      return [];
  }
}

export function grafoInicial(): FlowGraph {
  return {
    schemaVersion: 1,
    start: 'inicio',
    nodes: [
      { id: 'inicio', type: 'message', config: { text: 'Olá! Como posso ajudar?' }, position: { x: 0, y: 0 } },
      { id: 'fim', type: 'end', config: { text: 'Até logo!' }, position: { x: 340, y: 0 } },
    ],
    edges: [{ from: 'inicio', port: 'next', to: 'fim' }],
    variables: [],
  };
}

export function proximoId(grafo: FlowGraph, tipo: FlowNodeType): string {
  const usados = new Set(grafo.nodes.map((n) => n.id));
  for (let i = 1; ; i++) {
    const id = `${tipo}-${i}`;
    if (!usados.has(id)) {
      return id;
    }
  }
}

function configPadrao(tipo: FlowNodeType, id: string): FlowNodeConfig {
  const sufixo = id.replace(/^.*-/, '');
  switch (tipo) {
    case 'message':
      return { text: 'Escreva a mensagem.' };
    case 'question':
      return { prompt: 'Qual é a sua dúvida?', answerType: 'text', variable: `resposta_${sufixo}`, maxAttempts: 2, invalidText: 'Não entendi. Pode repetir?' };
    case 'condition':
      return { branches: [{ port: 'ramo_1', all: [{ variable: '', op: 'equals', value: '' }] }] };
    case 'knowledge':
      return {};
    case 'integration':
      return { operationKey: '', resultPrefix: `resultado_${sufixo}`, parameters: {} };
    case 'handoff':
      return { text: 'Vou transferir você para um atendente.', reasonCode: 'flow_handoff', summaryVariables: [] };
    case 'end':
      return { text: 'Até logo!' };
    default:
      return {};
  }
}

/** Acrescenta um nó novo (com a configuração padrão do tipo) na posição dada; a primeira posição livre é escolhida pela tela. */
export function adicionarNo(grafo: FlowGraph, tipo: FlowNodeType, posicao: { x: number; y: number }): { grafo: FlowGraph; id: string } {
  const id = proximoId(grafo, tipo);
  const no: FlowNode = { id, type: tipo, config: configPadrao(tipo, id), position: posicao };
  let proximo: FlowGraph = { ...grafo, nodes: [...grafo.nodes, no] };
  if (tipo === 'question') {
    proximo = garantirVariavel(proximo, no.config!.variable!, 'personal');
  }
  return { grafo: proximo, id };
}

export function removerNo(grafo: FlowGraph, id: string): FlowGraph {
  const nodes = grafo.nodes.filter((n) => n.id !== id);
  return {
    ...grafo,
    nodes,
    edges: grafo.edges.filter((e) => e.from !== id && e.to !== id),
    start: grafo.start === id ? (nodes[0]?.id ?? '') : grafo.start,
  };
}

/** Remove as conexões cuja porta já não existe no nó de origem (por exemplo, depois de apagar um ramo ou o prazo de uma pergunta). */
export function podarArestas(grafo: FlowGraph): FlowGraph {
  const portas = new Map(grafo.nodes.map((n) => [n.id, new Set(portasDoNo(n))]));
  return { ...grafo, edges: grafo.edges.filter((e) => portas.get(e.from)?.has(e.port) && portas.has(e.to)) };
}

/** Uma conexão por porta: ligar de novo a mesma porta troca o destino. */
export function conectar(grafo: FlowGraph, de: string, porta: string, para: string): FlowGraph {
  if (!grafo.nodes.some((n) => n.id === de) || !grafo.nodes.some((n) => n.id === para) || !portasDoNo(grafo.nodes.find((n) => n.id === de)!).includes(porta)) {
    return grafo;
  }
  return { ...grafo, edges: [...grafo.edges.filter((e) => !(e.from === de && e.port === porta)), { from: de, port: porta, to: para }] };
}

export function desconectar(grafo: FlowGraph, de: string, porta: string): FlowGraph {
  return { ...grafo, edges: grafo.edges.filter((e) => !(e.from === de && e.port === porta)) };
}

export function garantirVariavel(grafo: FlowGraph, nome: string, classe: FlowVariableClass): FlowGraph {
  if (nome.trim() === '' || grafo.variables.some((v) => v.name === nome)) {
    return grafo;
  }
  return { ...grafo, variables: [...grafo.variables, { name: nome, class: classe }] };
}

/** Substitui a configuração de um nó. Uma pergunta declara sozinha a variável da resposta (pessoal, exceto a de escolha) e as portas que sumiram perdem suas conexões. */
export function configurarNo(grafo: FlowGraph, id: string, config: FlowNodeConfig): FlowGraph {
  let proximo: FlowGraph = { ...grafo, nodes: grafo.nodes.map((n) => (n.id === id ? { ...n, config } : n)) };
  const no = proximo.nodes.find((n) => n.id === id);
  if (no?.type === 'question' && config.variable) {
    proximo = garantirVariavel(proximo, config.variable, config.answerType === 'choice' ? 'public' : 'personal');
  }
  return podarArestas(proximo);
}

export function rotularNo(grafo: FlowGraph, id: string, rotulo: string): FlowGraph {
  return { ...grafo, nodes: grafo.nodes.map((n) => (n.id === id ? { ...n, label: rotulo.trim() === '' ? null : rotulo } : n)) };
}

export function definirInicio(grafo: FlowGraph, id: string): FlowGraph {
  return grafo.nodes.some((n) => n.id === id) ? { ...grafo, start: id } : grafo;
}

export function mudarClasseDaVariavel(grafo: FlowGraph, nome: string, classe: FlowVariableClass): FlowGraph {
  return { ...grafo, variables: grafo.variables.map((v) => (v.name === nome ? { ...v, class: classe } : v)) };
}

export function removerVariavel(grafo: FlowGraph, nome: string): FlowGraph {
  return { ...grafo, variables: grafo.variables.filter((v) => v.name !== nome) };
}

/** Variáveis que o fluxo conhece (declaradas, as do sistema e as `prefixo.` de cada integração), para os seletores do painel. */
export function variaveisConhecidas(grafo: FlowGraph): string[] {
  return [
    'input.text',
    'knowledge.answer',
    ...grafo.variables.map((v) => v.name),
    ...grafo.nodes.filter((n) => n.type === 'integration' && n.config?.resultPrefix).map((n) => `${n.config!.resultPrefix}.`),
  ];
}

export function posicionar(grafo: FlowGraph): FlowGraph {
  // Camadas por largura a partir do início; o que não é alcançável vai para a última coluna.
  const camada = new Map<string, number>();
  const fila: string[] = grafo.start ? [grafo.start] : [];
  if (grafo.start) {
    camada.set(grafo.start, 0);
  }
  while (fila.length > 0) {
    const atual = fila.shift()!;
    for (const e of grafo.edges.filter((x) => x.from === atual)) {
      if (!camada.has(e.to)) {
        camada.set(e.to, (camada.get(atual) ?? 0) + 1);
        fila.push(e.to);
      }
    }
  }
  const maxima = Math.max(0, ...camada.values()) + 1;
  const linhas = new Map<number, number>();
  return {
    ...grafo,
    nodes: grafo.nodes.map((n) => {
      const c = camada.get(n.id) ?? maxima;
      const l = linhas.get(c) ?? 0;
      linhas.set(c, l + 1);
      return { ...n, position: { x: c * 340, y: l * 190 } };
    }),
  };
}

/** Garante posição em todos os nós (documentos sem posições, por exemplo os criados por API). */
export function comPosicoes(grafo: FlowGraph): FlowGraph {
  return grafo.nodes.every((n) => n.position) ? grafo : posicionar(grafo);
}

export interface DadosDoNo extends Record<string, unknown> {
  no: FlowNode;
  portas: string[];
  comProblema: boolean;
  visitado: boolean;
  inicio: boolean;
}

export type NoDoCanvas = Node<DadosDoNo, 'fluxo'>;

export function problemasPorNo(problemas: FlowProblem[]): Map<string, FlowProblem[]> {
  const mapa = new Map<string, FlowProblem[]>();
  for (const p of problemas) {
    if (p.nodeId) {
      mapa.set(p.nodeId, [...(mapa.get(p.nodeId) ?? []), p]);
    }
  }
  return mapa;
}

export function paraCanvas(grafo: FlowGraph, problemas: FlowProblem[], visitados: string[]): { nos: NoDoCanvas[]; arestas: Edge[] } {
  const comProblema = problemasPorNo(problemas);
  const vistos = new Set(visitados);
  return {
    nos: grafo.nodes.map((n) => ({
      id: n.id,
      type: 'fluxo' as const,
      position: n.position ?? { x: 0, y: 0 },
      data: { no: n, portas: portasDoNo(n), comProblema: comProblema.has(n.id), visitado: vistos.has(n.id), inicio: grafo.start === n.id },
    })),
    arestas: grafo.edges.map((e) => ({ id: `${e.from}:${e.port}`, source: e.from, sourceHandle: e.port, target: e.to, label: rotuloDaPorta(e.port), animated: vistos.has(e.from) && vistos.has(e.to) })),
  };
}

/** Aplica as posições do canvas de volta ao documento (o resto do documento vem do estado do editor, nunca do canvas). */
export function posicoesDoCanvas(grafo: FlowGraph, nos: Array<{ id: string; position: { x: number; y: number } }>): FlowGraph {
  const posicoes = new Map(nos.map((n) => [n.id, n.position]));
  return { ...grafo, nodes: grafo.nodes.map((n) => (posicoes.has(n.id) ? { ...n, position: { x: Math.round(posicoes.get(n.id)!.x), y: Math.round(posicoes.get(n.id)!.y) } } : n)) };
}

function limpar<T>(valor: T): T {
  if (Array.isArray(valor)) {
    return valor.map(limpar) as T;
  }
  if (valor !== null && typeof valor === 'object') {
    const saida: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(valor as Record<string, unknown>)) {
      if (v === undefined || v === null || v === '') {
        continue;
      }
      saida[k] = limpar(v);
    }
    return saida as T;
  }
  return valor;
}

/**
 * O documento que vai ao servidor: sem nulos nem textos vazios (o servidor lê de forma estrita e os campos opcionais simplesmente não vêm), sem escolhas em pergunta que não é de escolha e sem
 * conexões para portas inexistentes. Textos obrigatórios vazios ficam fora de propósito: a validação do servidor aponta o nó.
 */
export function documentoDoGrafo(grafo: FlowGraph): FlowGraph {
  const nodes = grafo.nodes.map((n) => {
    const config = { ...(n.config ?? {}) };
    if (n.type === 'question' && config.answerType !== 'choice') {
      delete config.choices;
    }
    if (n.type === 'question' && config.answerType !== 'pattern') {
      delete config.pattern;
    }
    return { ...n, config };
  });
  return limpar(podarArestas({ ...grafo, nodes }));
}

export function lerGrafo(texto: string | null | undefined): FlowGraph | null {
  if (!texto) {
    return null;
  }
  try {
    const g = JSON.parse(texto) as Partial<FlowGraph>;
    if (!Array.isArray(g.nodes) || !Array.isArray(g.edges) || typeof g.start !== 'string') {
      return null;
    }
    return { schemaVersion: g.schemaVersion ?? 1, start: g.start, nodes: g.nodes, edges: g.edges, variables: g.variables ?? [] };
  } catch {
    return null;
  }
}

/** Os códigos dos problemas do servidor, em português claro. O servidor nunca devolve o conteúdo digitado. */
export const ROTULO_DO_PROBLEMA: Record<string, string> = {
  'port-unconnected': 'Há uma saída sem conexão.',
  'unreachable-node': 'Este nó não é alcançável a partir do início.',
  'cycle-without-wait': 'Há um ciclo sem nenhuma pergunta: o fluxo giraria sem esperar o cliente.',
  'variable-undeclared': 'Uma variável usada não foi declarada.',
  'variable-class': 'A resposta livre do cliente precisa de variável pessoal.',
  'text-required': 'Um texto obrigatório está vazio.',
  'text-length': 'Um texto passa do tamanho máximo.',
  'operation-missing': 'A operação não existe ou não é elegível para fluxos.',
  'operation-not-eligible': 'A operação não é de leitura pública autorizada a fluxos.',
  'operation-key': 'Escolha uma operação.',
  'result-prefix': 'O prefixo do resultado é inválido ou repetido.',
  'parameter-binding': 'Cada parâmetro precisa de uma origem: variável ou valor fixo.',
  'parameter-unknown': 'Um parâmetro não existe na operação.',
  'parameter-missing': 'Falta ligar um parâmetro obrigatório.',
  'start-missing': 'O nó inicial não existe.',
  'edge-endpoint': 'Uma conexão aponta para um nó que não existe.',
  'port-invalid': 'Uma conexão usa uma saída que não existe.',
  'port-duplicate': 'Uma saída tem mais de uma conexão.',
  'node-count': 'Quantidade de nós fora do limite.',
  'edge-count': 'Quantidade de conexões fora do limite.',
  choices: 'A pergunta de escolha precisa de 2 a 10 opções.',
  'choice-label': 'Rótulo de opção vazio, repetido ou longo demais.',
  'choice-id': 'Identificador de opção inválido ou repetido.',
  branches: 'A condição precisa de ao menos um ramo.',
  'branch-port': 'Nome de ramo inválido ou repetido.',
  'condition-variable': 'A condição precisa de uma variável.',
  'condition-value': 'A condição precisa de um valor.',
  schedule: 'Horário de atendimento inválido.',
  pattern: 'Padrão inválido.',
  'schema-version': 'Versão do documento não suportada.',
};

export function textoDoProblema(codigo: string): string {
  return ROTULO_DO_PROBLEMA[codigo] ?? `Problema (${codigo}).`;
}

/** Os problemas de uma publicação recusada chegam como "nó: código" (400 `problems`). Devolve "nó — frase". */
export function explicarProblemas(problemas: string[]): string[] {
  return problemas.map((p) => {
    const i = p.indexOf(': ');
    if (i < 0) {
      return p;
    }
    return `${p.slice(0, i)} — ${textoDoProblema(p.slice(i + 2))}`;
  });
}

export const ROTULO_DO_RESULTADO: Record<string, string> = {
  completed: 'Terminou o fluxo',
  handed_off: 'Transferiu para um atendente',
  delegated: 'Entregou ao agente de IA',
  failed: 'Falhou',
  cancelled: 'Cancelado',
  waiting_for_input: 'Parou esperando uma resposta do cliente',
  'event-limit': 'Parou no limite de eventos da simulação',
};

export const ROTULO_DO_EVENTO: Record<string, string> = {
  customer: 'Cliente',
  'bot-message': 'Bot',
  'simulated-integration': 'Integração (simulada)',
  'simulated-knowledge': 'Conhecimento (simulado)',
  handoff: 'Transferência',
  'delegated-to-agent': 'Entrega ao agente de IA',
  finished: 'Fim',
  'time-passed': 'Tempo passou',
};

/** Pares "campo=valor", um por linha (a resposta simulada de uma integração). Linha sem "=" é ignorada. */
export function camposDoTexto(texto: string): Record<string, string> {
  const campos: Record<string, string> = {};
  for (const linha of texto.split(/\r?\n/)) {
    const i = linha.indexOf('=');
    if (i > 0) {
      campos[linha.slice(0, i).trim()] = linha.slice(i + 1).trim();
    }
  }
  return campos;
}
