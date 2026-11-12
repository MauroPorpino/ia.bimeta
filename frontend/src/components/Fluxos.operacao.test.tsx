import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { FlowActivation, FlowInstanceSummary, WorkflowSummary } from '../api/types';
import { ADMINISTRADOR, chamadasDe, criarFetch, json, problema } from '../test/helpers';
import { Fluxos } from './Fluxos';

const ORG = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const CANAL = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';

beforeEach(() => {
  class RO {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  vi.stubGlobal('ResizeObserver', RO);
});

function publicado(): WorkflowSummary {
  return {
    id: 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa',
    organizationId: ORG,
    key: 'boas-vindas',
    version: 2,
    status: 'Published',
    name: 'Boas-vindas',
    description: null,
    graphHash: 'h',
    revision: 4,
    updatedAt: '2026-11-12T12:00:00Z',
    publishedAt: '2026-11-12T12:00:00Z',
    retiredAt: null,
  };
}

function ativacao(sobrescrever: Partial<FlowActivation> = {}): FlowActivation {
  return {
    id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    organizationId: ORG,
    businessUnitId: null,
    channelInstanceId: CANAL,
    workflowKey: 'boas-vindas',
    enabled: false,
    priority: 5,
    rolloutPercent: 50,
    onFailure: 'Handoff',
    createdAt: '2026-11-12T12:00:00Z',
    updatedAt: '2026-11-12T12:00:00Z',
    revision: 1,
    ...sobrescrever,
  };
}

function instancia(sobrescrever: Partial<FlowInstanceSummary> = {}): FlowInstanceSummary {
  return {
    id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
    sessionId: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
    workflowKey: 'boas-vindas',
    versionNumber: 2,
    status: 'Completed',
    nodeId: 'fim',
    failureCode: null,
    createdAt: '2026-11-12T12:00:00Z',
    endedAt: '2026-11-12T12:01:00Z',
    ...sobrescrever,
  };
}

function renderizar(rotas: Parameters<typeof criarFetch>[0]) {
  const f = criarFetch({ 'GET /workflows/definitions': json([publicado()]), ...rotas });
  vi.stubGlobal('fetch', f);
  render(<Fluxos api={createApi()} me={{ ...ADMINISTRADOR, grants: [{ organizationId: ORG, role: 'OrganizationAdmin' }] }} />);
  return f;
}

describe('Fluxos: ativações (ADR 0038)', () => {
  it('lista as ativações, cria uma DESLIGADA com os campos certos e liga e desliga pelo servidor', async () => {
    let lista = [ativacao()];
    const f = renderizar({
      'GET /workflows/activations': () => json(lista),
      'POST /workflows/activations': () => {
        lista = [...lista, ativacao({ id: '99999999-9999-4999-8999-999999999999', priority: 1, rolloutPercent: 25 })];
        return json(lista[1]);
      },
      'POST /workflows/activations/dddddddd-dddd-4ddd-8ddd-dddddddddddd/enable': () => {
        lista = [ativacao({ enabled: true, revision: 2 })];
        return json(lista[0]);
      },
    });

    await userEvent.click(await screen.findByRole('button', { name: 'Ativações' }));
    expect(await screen.findByText('Desligada')).toBeTruthy();
    expect(screen.getByText('50%')).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: 'Nova ativação' }));
    const form = screen.getByRole('form', { name: 'Nova ativação' });
    await userEvent.clear(within(form).getByLabelText(/Prioridade/));
    await userEvent.type(within(form).getByLabelText(/Prioridade/), '1');
    await userEvent.clear(within(form).getByLabelText(/Parcela/));
    await userEvent.type(within(form).getByLabelText(/Parcela/), '25');
    await userEvent.type(within(form).getByLabelText(/Canal/), CANAL);
    await userEvent.click(within(form).getByRole('button', { name: 'Criar ativação' }));
    expect(await screen.findByText(/Ativação criada, DESLIGADA/)).toBeTruthy();

    const [criada] = chamadasDe(f, 'POST /workflows/activations');
    expect(criada!.corpo).toMatchObject({ organizationId: ORG, workflowKey: 'boas-vindas', channelInstanceId: CANAL, businessUnitId: null, priority: 1, rolloutPercent: 25, onFailure: 'Handoff' });

    await userEvent.click(screen.getAllByRole('button', { name: 'Ligar a ativação de boas-vindas' })[0]!);
    expect(await screen.findByText(/Ativação ligada/)).toBeTruthy();
    expect(chamadasDe(f, 'POST /workflows/activations/dddddddd-dddd-4ddd-8ddd-dddddddddddd/enable')).toHaveLength(1);
  });

  it('recusa localmente uma parcela fora de 0 a 100 e explica em português quando o servidor exige uma versão publicada', async () => {
    renderizar({
      'GET /workflows/activations': json([]),
      'POST /workflows/activations': problema(409, 'Conflito', 'sem versão', 'no-published-version'),
    });
    await userEvent.click(await screen.findByRole('button', { name: 'Ativações' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Nova ativação' }));
    const form = screen.getByRole('form', { name: 'Nova ativação' });
    const criar = within(form).getByRole('button', { name: 'Criar ativação' }) as HTMLButtonElement;

    await userEvent.clear(within(form).getByLabelText(/Parcela/));
    await userEvent.type(within(form).getByLabelText(/Parcela/), '150');
    expect(criar.disabled).toBe(true);
    await userEvent.clear(within(form).getByLabelText(/Parcela/));
    await userEvent.type(within(form).getByLabelText(/Parcela/), '100');
    expect(criar.disabled).toBe(false);

    await userEvent.click(criar);
    expect(await within(form).findByText(/ainda não tem uma versão publicada/)).toBeTruthy();
  });

  it('um usuário sem permissão (403) recebe a explicação', async () => {
    renderizar({ 'GET /workflows/activations': problema(403, 'Acesso negado') });
    await userEvent.click(await screen.findByRole('button', { name: 'Ativações' }));
    expect(await screen.findByText(/não tem permissão para ativar fluxos/)).toBeTruthy();
  });
});

describe('Fluxos: histórico e métricas (ADR 0037 §7)', () => {
  it('lista as execuções, filtra por situação no servidor e mostra o caminho sem nenhum texto de cliente', async () => {
    const f = renderizar({
      'GET /workflows/instances': json({ items: [instancia(), instancia({ id: '11111111-2222-4333-8444-555555555555', status: 'Failed', failureCode: 'step-limit', nodeId: 'consulta' })], total: 2 }),
      'GET /workflows/instances/eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee': json({
        ...instancia(),
        steps: [
          { step: 1, nodeId: 'ola', outcome: 'pass', at: '2026-11-12T12:00:00Z' },
          { step: 1, nodeId: 'menu', outcome: 'waiting', at: '2026-11-12T12:00:00Z' },
        ],
        effects: [{ key: '1:0', kind: 'SendMessage', state: 'Done', resultCode: 'sent', attempts: 1 }],
      }),
    });

    await userEvent.click(await screen.findByRole('button', { name: 'Histórico' }));
    expect(await screen.findByText('Falhou (step-limit)')).toBeTruthy();
    await userEvent.selectOptions(screen.getByLabelText('Situação'), 'Failed');
    expect(chamadasDe(f, 'GET /workflows/instances').some((c) => c.consulta.get('status') === 'Failed')).toBe(true);

    await userEvent.click(screen.getAllByRole('button', { name: /Ver o caminho da execução eeeeeeee/ })[0]!);
    const detalhe = await screen.findByRole('region', { name: /Caminho da execução eeeeeeee/ });
    expect(within(detalhe).getByText('menu — waiting')).toBeTruthy();
    expect(within(detalhe).getByText(/Mensagem — Feito \(sent\)/)).toBeTruthy();
    expect(detalhe.textContent).toContain('nenhum texto do cliente');
  });

  it('mostra as métricas por fluxo e versão e abre o funil por nó', async () => {
    const f = renderizar({
      'GET /workflows/metrics': json([{ workflowKey: 'boas-vindas', versionNumber: 2, started: 10, active: 1, completed: 6, handedOff: 2, delegated: 1, failed: 0, cancelled: 0 }]),
      'GET /workflows/metrics/funnel': json([{ nodeId: 'ola', visits: 10 }, { nodeId: 'menu', visits: 9 }]),
    });

    await userEvent.click(await screen.findByRole('button', { name: 'Métricas' }));
    expect(await screen.findByText('6 (60%)')).toBeTruthy();
    expect(screen.getByText('2 (20%)')).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: 'Ver o funil de boas-vindas v2' }));
    const funil = await screen.findByRole('region', { name: 'Funil de boas-vindas v2' });
    expect(within(funil).getByText('menu')).toBeTruthy();
    expect(within(funil).getByText('9')).toBeTruthy();
    expect(chamadasDe(f, 'GET /workflows/metrics/funnel')[0]!.consulta.get('version')).toBe('2');

    await userEvent.selectOptions(screen.getByLabelText('Período'), '30');
    expect(chamadasDe(f, 'GET /workflows/metrics').some((c) => c.consulta.get('days') === '30')).toBe(true);
  });
});
