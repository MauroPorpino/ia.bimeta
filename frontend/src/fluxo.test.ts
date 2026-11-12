import { describe, expect, it } from 'vitest';
import type { FlowGraph } from './api/types';
import {
  adicionarNo,
  conectar,
  configurarNo,
  documentoDoGrafo,
  explicarProblemas,
  grafoInicial,
  lerGrafo,
  paraCanvas,
  podarArestas,
  portasDoNo,
  posicionar,
  posicoesDoCanvas,
  problemasPorNo,
  removerNo,
  textoDoProblema,
  variaveisConhecidas,
} from './fluxo';
import { ADMINISTRADOR, USUARIO } from './test/helpers';
import { podeVerFluxos } from './fluxo';

describe('portas dos nós (as mesmas do servidor)', () => {
  it('cada tipo tem as saídas esperadas e a pergunta só ganha "sem resposta" quando tem prazo', () => {
    const g = grafoInicial();
    expect(portasDoNo(g.nodes[0]!)).toEqual(['next']);
    expect(portasDoNo(g.nodes[1]!)).toEqual([]);
    const { grafo, id } = adicionarNo(g, 'question', { x: 0, y: 0 });
    const pergunta = grafo.nodes.find((n) => n.id === id)!;
    expect(portasDoNo(pergunta)).toEqual(['answered', 'invalid']);
    expect(portasDoNo({ ...pergunta, config: { ...pergunta.config, timeoutSeconds: 60 } })).toEqual(['answered', 'invalid', 'timeout']);
    expect(portasDoNo({ id: 'c', type: 'condition', config: { branches: [{ port: 'a', all: [] }, { port: 'b', all: [] }] } })).toEqual(['a', 'b', 'else']);
    expect(portasDoNo({ id: 'k', type: 'knowledge' })).toEqual(['found', 'not_found']);
    expect(portasDoNo({ id: 'i', type: 'integration' })).toEqual(['success', 'failure']);
  });
});

describe('edição que mantém o documento coerente', () => {
  it('uma conexão por porta: ligar de novo troca o destino, e porta inexistente é ignorada', () => {
    let g = adicionarNo(grafoInicial(), 'message', { x: 0, y: 200 }).grafo;
    g = conectar(g, 'inicio', 'next', 'message-1');
    expect(g.edges).toEqual([{ from: 'inicio', port: 'next', to: 'message-1' }]);
    expect(conectar(g, 'inicio', 'inexistente', 'fim')).toBe(g);
    expect(conectar(g, 'fantasma', 'next', 'fim')).toBe(g);
  });

  it('remover um nó leva as conexões dele e passa o início adiante', () => {
    const g = removerNo(grafoInicial(), 'inicio');
    expect(g.nodes.map((n) => n.id)).toEqual(['fim']);
    expect(g.edges).toEqual([]);
    expect(g.start).toBe('fim');
  });

  it('configurar uma pergunta declara a variável (pessoal, exceto escolha) e poda conexões de portas que sumiram', () => {
    let { grafo, id } = adicionarNo(grafoInicial(), 'question', { x: 0, y: 0 });
    expect(grafo.variables).toEqual([{ name: 'resposta_1', class: 'personal' }]);
    grafo = configurarNo(grafo, id, { ...grafo.nodes.find((n) => n.id === id)!.config, timeoutSeconds: 30 });
    grafo = conectar(grafo, id, 'timeout', 'fim');
    expect(grafo.edges.some((e) => e.port === 'timeout')).toBe(true);
    grafo = configurarNo(grafo, id, { ...grafo.nodes.find((n) => n.id === id)!.config, timeoutSeconds: null });
    expect(grafo.edges.some((e) => e.port === 'timeout')).toBe(false);

    grafo = configurarNo(grafo, id, { ...grafo.nodes.find((n) => n.id === id)!.config, answerType: 'choice', variable: 'menu' });
    expect(grafo.variables).toContainEqual({ name: 'menu', class: 'public' });
  });

  it('as variáveis conhecidas incluem as do sistema e o prefixo do resultado de cada integração', () => {
    const { grafo } = adicionarNo(grafoInicial(), 'integration', { x: 0, y: 0 });
    expect(variaveisConhecidas(grafo)).toEqual(['input.text', 'knowledge.answer', 'resultado_1.']);
  });

  it('o posicionamento automático põe o início na primeira coluna e o que não é alcançável por último', () => {
    const g = posicionar({ ...grafoInicial(), nodes: [...grafoInicial().nodes, { id: 'solto', type: 'end' }] });
    expect(g.nodes.find((n) => n.id === 'inicio')!.position).toEqual({ x: 0, y: 0 });
    expect(g.nodes.find((n) => n.id === 'fim')!.position).toEqual({ x: 340, y: 0 });
    expect(g.nodes.find((n) => n.id === 'solto')!.position!.x).toBeGreaterThan(340);
  });
});

describe('o documento que vai ao servidor', () => {
  it('não leva nulos, textos vazios, escolhas de pergunta que não é de escolha nem conexões de portas inexistentes', () => {
    const g: FlowGraph = {
      schemaVersion: 1,
      start: 'q',
      nodes: [
        { id: 'q', type: 'question', config: { prompt: 'Nome?', variable: 'nome', answerType: 'text', choices: [{ id: 'a', label: 'A' }], pattern: '', invalidText: '', timeoutSeconds: null }, label: null },
        { id: 'f', type: 'end', config: { text: '' } },
      ],
      edges: [
        { from: 'q', port: 'answered', to: 'f' },
        { from: 'q', port: 'timeout', to: 'f' },
      ],
      variables: [{ name: 'nome', class: 'personal' }],
    };
    const doc = documentoDoGrafo(g);
    expect(JSON.stringify(doc)).not.toContain('null');
    expect(JSON.stringify(doc)).not.toContain('""');
    expect(doc.nodes[0]!.config).toEqual({ prompt: 'Nome?', variable: 'nome', answerType: 'text' });
    expect(doc.edges).toEqual([{ from: 'q', port: 'answered', to: 'f' }]);
    expect(doc.variables).toEqual([{ name: 'nome', class: 'personal' }]);
  });

  it('lerGrafo aceita o documento do servidor e recusa lixo', () => {
    expect(lerGrafo(JSON.stringify(grafoInicial()))!.nodes).toHaveLength(2);
    expect(lerGrafo('não é json')).toBeNull();
    expect(lerGrafo('{"nodes":1}')).toBeNull();
    expect(lerGrafo(null)).toBeNull();
  });

  it('as posições do canvas voltam ao documento sem mexer no resto', () => {
    const g = grafoInicial();
    const movido = posicoesDoCanvas(g, [{ id: 'fim', position: { x: 10.4, y: 20.6 } }]);
    expect(movido.nodes.find((n) => n.id === 'fim')!.position).toEqual({ x: 10, y: 21 });
    expect(movido.edges).toEqual(g.edges);
  });
});

describe('canvas e problemas', () => {
  it('converte o grafo para o canvas marcando problema, visita e início, com uma aresta por porta', () => {
    const problemas = [{ nodeId: 'fim', code: 'port-unconnected', message: 'x' }];
    const { nos, arestas } = paraCanvas(grafoInicial(), problemas, ['inicio', 'fim']);
    expect(nos.find((n) => n.id === 'fim')!.data).toMatchObject({ comProblema: true, visitado: true, inicio: false });
    expect(nos.find((n) => n.id === 'inicio')!.data.inicio).toBe(true);
    expect(arestas).toEqual([expect.objectContaining({ id: 'inicio:next', source: 'inicio', sourceHandle: 'next', target: 'fim', animated: true })]);
    expect(problemasPorNo(problemas).get('fim')).toHaveLength(1);
    expect(podarArestas(grafoInicial()).edges).toHaveLength(1);
  });

  it('explica em português os problemas de uma publicação recusada', () => {
    expect(textoDoProblema('cycle-without-wait')).toContain('ciclo');
    expect(textoDoProblema('codigo-novo')).toBe('Problema (codigo-novo).');
    expect(explicarProblemas(['menu: port-unconnected', 'fluxo: start-missing', 'sem formato'])).toEqual([
      'menu — Há uma saída sem conexão.',
      'fluxo — O nó inicial não existe.',
      'sem formato',
    ]);
  });
});

describe('quem vê a área', () => {
  it('administradores e gerentes veem; o atendente comum não', () => {
    expect(podeVerFluxos(ADMINISTRADOR)).toBe(true);
    expect(podeVerFluxos({ ...USUARIO, grants: [{ organizationId: 'x', role: 'Manager' }] })).toBe(true);
    expect(podeVerFluxos(USUARIO)).toBe(false);
  });
});
