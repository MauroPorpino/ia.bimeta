import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { FlowGraph, WorkflowSummary } from '../api/types';
import { grafoInicial } from '../fluxo';
import { ADMINISTRADOR, chamadasDe, criarFetch, json, problema } from '../test/helpers';
import { Fluxos } from './Fluxos';

const ORG = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';

// O React Flow mede o canvas com ResizeObserver/DOMMatrix, que o jsdom não tem.
beforeEach(() => {
  class RO {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  vi.stubGlobal('ResizeObserver', RO);
  vi.stubGlobal('DOMMatrixReadOnly', class { m22 = 1; constructor() {} });
});

function resumo(sobrescrever: Partial<WorkflowSummary> = {}): WorkflowSummary {
  return {
    id: 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa',
    organizationId: ORG,
    key: 'boas-vindas',
    version: 1,
    status: 'Draft',
    name: 'Boas-vindas',
    description: null,
    graphHash: 'h',
    revision: 3,
    updatedAt: '2026-11-12T12:00:00Z',
    publishedAt: null,
    retiredAt: null,
    ...sobrescrever,
  };
}

function comGrafo(w: WorkflowSummary, g: FlowGraph = grafoInicial()): WorkflowSummary {
  return { ...w, graphJson: JSON.stringify(g) };
}

function renderizar(rotas: Parameters<typeof criarFetch>[0]) {
  const f = criarFetch({ 'GET /workflows/operations': json([]), ...rotas });
  vi.stubGlobal('fetch', f);
  render(<Fluxos api={createApi()} me={{ ...ADMINISTRADOR, grants: [{ organizationId: ORG, role: 'OrganizationAdmin' }] }} />);
  return f;
}

describe('área Fluxos', () => {
  it('lista os fluxos, mostra a situação e abre o editor com o grafo do servidor', async () => {
    const f = renderizar({ 'GET /workflows/definitions': json([resumo()]), 'GET /workflows/definitions/aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa': json(comGrafo(resumo())) });

    expect(await screen.findByText('Rascunho (v1)')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Abrir Boas-vindas' }));

    expect(await screen.findByRole('region', { name: 'Editor do fluxo boas-vindas' })).toBeTruthy();
    expect(screen.getByText(/Rascunho da versão 1/)).toBeTruthy();
    expect(chamadasDe(f, 'GET /workflows/definitions/aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa')).toHaveLength(1);
  });

  it('um fluxo novo exige chave válida e salva o documento sem nulos nem textos vazios, com a revisão esperada nula', async () => {
    const salvo = { workflow: comGrafo(resumo({ revision: 1 })), problems: [] };
    const f = renderizar({ 'GET /workflows/definitions': json([]), 'POST /workflows/definitions': json(salvo) });
    await screen.findByText(/Nenhum fluxo ainda/);

    await userEvent.click(screen.getByRole('button', { name: 'Novo fluxo' }));
    const criar = screen.getByRole('button', { name: 'Criar e abrir o editor' });
    expect((criar as HTMLButtonElement).disabled).toBe(true);
    await userEvent.type(screen.getByLabelText('Nome'), 'Boas-vindas');
    await userEvent.type(screen.getByLabelText(/Chave/), 'Chave Inválida');
    expect((criar as HTMLButtonElement).disabled).toBe(true);
    await userEvent.clear(screen.getByLabelText(/Chave/));
    await userEvent.type(screen.getByLabelText(/Chave/), 'boas-vindas');
    await userEvent.click(criar);

    await userEvent.click(await screen.findByRole('button', { name: 'Salvar rascunho' }));
    await screen.findByText(/Rascunho salvo/);

    const [chamada] = chamadasDe(f, 'POST /workflows/definitions');
    expect(chamada!.corpo).toMatchObject({ organizationId: ORG, key: 'boas-vindas', name: 'Boas-vindas', expectedRevision: null });
    const grafo = chamada!.corpo!.graph as FlowGraph;
    expect(grafo.nodes.map((n) => n.id)).toEqual(['inicio', 'fim']);
    expect(JSON.stringify(grafo)).not.toContain('null');
  });

  it('uma versão publicada abre somente para leitura e editar cria uma nova versão em rascunho', async () => {
    const publicada = resumo({ status: 'Published', publishedAt: '2026-11-12T12:00:00Z' });
    const f = renderizar({
      'GET /workflows/definitions': json([publicada]),
      'GET /workflows/definitions/aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa': json(comGrafo(publicada)),
      'POST /workflows/definitions': json({ workflow: comGrafo(resumo({ version: 2, revision: 1, id: 'cccccccc-2222-4222-8222-cccccccccccc' })), problems: [] }),
    });

    await userEvent.click(await screen.findByRole('button', { name: 'Abrir Boas-vindas' }));
    const editor = await screen.findByRole('region', { name: 'Editor do fluxo boas-vindas' });
    expect(within(editor).queryByRole('button', { name: 'Publicar' })).toBeNull();
    expect((within(editor).getByRole('button', { name: '+ Mensagem' }) as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(within(editor).getByRole('button', { name: 'Editar (cria nova versão)' }));
    await within(editor).findByText(/Criado o rascunho da versão 2/);
    expect(chamadasDe(f, 'POST /workflows/definitions')[0]!.corpo).toMatchObject({ key: 'boas-vindas', expectedRevision: null });
    expect(within(editor).getByRole('button', { name: 'Publicar' })).toBeTruthy();
  });

  it('o servidor valida: os problemas aparecem por nó e a publicação recusada é explicada em português', async () => {
    const rascunho = resumo();
    renderizar({
      'GET /workflows/definitions': json([rascunho]),
      'GET /workflows/definitions/aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa': json(comGrafo(rascunho)),
      'POST /workflows/validate': json({ valid: false, problems: [{ nodeId: 'fim', code: 'unreachable-node', message: 'x' }] }),
      'POST /workflows/definitions/aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa/publish': json({ title: 'Dados inválidos', status: 400, problems: ['inicio: port-unconnected'] }, 400),
    });

    await userEvent.click(await screen.findByRole('button', { name: 'Abrir Boas-vindas' }));
    const editor = await screen.findByRole('region', { name: 'Editor do fluxo boas-vindas' });
    await userEvent.click(within(editor).getByRole('button', { name: 'Validar' }));
    expect(await within(editor).findByText(/Este nó não é alcançável/)).toBeTruthy();
    expect(within(editor).getByRole('button', { name: 'Problemas (1)' })).toBeTruthy();

    await userEvent.click(within(editor).getByRole('button', { name: 'Publicar' }));
    expect(await within(editor).findByText(/inicio — Há uma saída sem conexão/)).toBeTruthy();
  });

  it('o simulador envia o cenário e mostra a conversa simulada sem nenhuma chamada além da simulação', async () => {
    const rascunho = resumo();
    const f = renderizar({
      'GET /workflows/definitions': json([rascunho]),
      'GET /workflows/definitions/aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa': json(comGrafo(rascunho)),
      'POST /workflows/simulate': json({
        problems: [],
        simulation: {
          outcome: 'completed',
          visited: ['inicio', 'fim'],
          events: [
            { kind: 'customer', nodeId: null, text: 'oi', choices: null, data: null },
            { kind: 'bot-message', nodeId: 'inicio', text: 'Olá! Como posso ajudar?', choices: null, data: null },
            { kind: 'bot-message', nodeId: 'fim', text: 'Até logo!', choices: null, data: null },
            { kind: 'finished', nodeId: 'fim', text: null, choices: null, data: null },
          ],
          finalState: { status: 'Completed', nodeId: 'fim', variables: {}, failureCode: null },
        },
      }),
    });

    await userEvent.click(await screen.findByRole('button', { name: 'Abrir Boas-vindas' }));
    const editor = await screen.findByRole('region', { name: 'Editor do fluxo boas-vindas' });
    await userEvent.click(within(editor).getByRole('button', { name: 'Simular' }));
    await userEvent.click(within(editor).getByRole('button', { name: 'Simular conversa' }));

    expect(await within(editor).findByText('Terminou o fluxo')).toBeTruthy();
    expect(within(within(editor).getByLabelText('Resultado da simulação')).getByText(/Olá! Como posso ajudar\?/)).toBeTruthy();
    const [chamada] = chamadasDe(f, 'POST /workflows/simulate');
    expect(chamada!.corpo).toMatchObject({ organizationId: ORG, scenario: { inputs: [{ kind: 'Say', text: 'oi' }] } });
    await waitFor(() => expect(f.mock.calls.filter(([url]) => /\/(capabilities|agents|channels)\//.test(String(url)))).toHaveLength(0));
  });

  it('a falta de permissão (403) vira uma explicação, e o limite de simulações (429) também', async () => {
    const rascunho = resumo();
    renderizar({
      'GET /workflows/definitions': json([rascunho]),
      'GET /workflows/definitions/aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa': json(comGrafo(rascunho)),
      'POST /workflows/definitions': problema(403, 'Acesso negado'),
      'POST /workflows/simulate': problema(429, 'Conflito', 'Muitas simulações', 'rate-limited'),
    });
    await userEvent.click(await screen.findByRole('button', { name: 'Abrir Boas-vindas' }));
    const editor = await screen.findByRole('region', { name: 'Editor do fluxo boas-vindas' });
    await userEvent.click(within(editor).getByRole('button', { name: '+ Mensagem' }));
    await userEvent.click(within(editor).getByRole('button', { name: 'Salvar rascunho' }));
    expect(await within(editor).findByText(/não tem permissão para esta operação com fluxos/)).toBeTruthy();

    await userEvent.click(within(editor).getByRole('button', { name: 'Simular' }));
    await userEvent.click(within(editor).getByRole('button', { name: 'Simular conversa' }));
    expect(await within(editor).findByText(/Muitas simulações seguidas/)).toBeTruthy();
  });
});
