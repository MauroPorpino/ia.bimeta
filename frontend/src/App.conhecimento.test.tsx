import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { createApi } from './api/client';
import { definirSessao } from './api/session';
import type { CurrentUser } from './api/types';
import { ADMINISTRADOR, FILA, USUARIO, chamadasDe, criarFetch, detalheDaLacuna, json, lacuna, problema } from './test/helpers';

function logar() {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
}

function rotasBase(usuario: CurrentUser, extras: Parameters<typeof criarFetch>[0] = {}) {
  return {
    'GET /auth/me': json(usuario),
    'GET /contact-center/me/queues': json([FILA]),
    'GET /contact-center/me/presence': json({ userId: usuario.id, status: 'Available', updatedAt: null }),
    'GET /contact-center/items': json([]),
    ...extras,
  };
}

function renderizar() {
  return render(<App api={createApi()} intervaloFilaMs={60_000} intervaloConversaMs={60_000} intervaloAlertasMs={60_000} />);
}

describe('App: área "Conhecimento" (ADR 0030)', () => {
  it('só o administrador do tenant vê a área: o atendente não vê o menu e a API de conhecimento nem é chamada', async () => {
    logar();
    const f = criarFetch(rotasBase(USUARIO));
    vi.stubGlobal('fetch', f);
    renderizar();

    await screen.findByText('Ana Atendente');
    await screen.findByRole('region', { name: 'Fila de atendimento' });

    expect(screen.queryByRole('navigation', { name: 'Áreas do sistema' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Conhecimento' })).toBeNull();
    expect(screen.getByRole('heading', { level: 1, name: 'ia.bimeta - Atendimento' })).toBeTruthy();
    expect(f.mock.calls.filter(([url]) => String(url).includes('/agents/'))).toHaveLength(0);
  });

  it('o administrador de UMA organização (concessão com organizationId) também não vê a área', async () => {
    logar();
    const deOrganizacao: CurrentUser = { ...ADMINISTRADOR, grants: [{ organizationId: FILA.organizationId, role: 'OrganizationAdmin' }] };
    vi.stubGlobal('fetch', criarFetch(rotasBase(deOrganizacao)));
    renderizar();

    await screen.findByText('Beto Administrador');

    expect(screen.queryByRole('button', { name: 'Conhecimento' })).toBeNull();
  });

  it('o administrador do tenant alterna entre Atendimento e Conhecimento; a área não é montada antes da primeira visita', async () => {
    logar();
    const l = lacuna({ occurrences: 9 });
    const f = criarFetch(rotasBase(ADMINISTRADOR, { 'GET /agents/knowledge-gaps': json({ items: [l], total: 1 }) }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    const menu = await screen.findByRole('navigation', { name: 'Áreas do sistema' });
    expect(within(menu).getByRole('button', { name: 'Atendimento' }).getAttribute('aria-current')).toBe('page');
    expect(within(menu).getByRole('button', { name: 'Conhecimento' }).getAttribute('aria-current')).toBeNull();
    expect(chamadasDe(f, 'GET /agents/knowledge-gaps')).toHaveLength(0); // nada de conhecimento até o administrador abrir a área

    await usuario.click(within(menu).getByRole('button', { name: 'Conhecimento' }));

    expect(await screen.findByRole('region', { name: 'Lacunas do FAQ' })).toBeTruthy();
    expect(await screen.findByText('9 ocorrências')).toBeTruthy();
    expect(screen.getByRole('heading', { level: 1, name: 'ia.bimeta - Conhecimento' })).toBeTruthy();
    expect(within(menu).getByRole('button', { name: 'Conhecimento' }).getAttribute('aria-current')).toBe('page');
    expect(screen.queryByRole('region', { name: 'Fila de atendimento' })).toBeNull(); // o Atendimento fica escondido

    await usuario.click(within(menu).getByRole('button', { name: 'Atendimento' }));

    expect(await screen.findByRole('region', { name: 'Fila de atendimento' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Lacunas do FAQ' })).toBeNull(); // escondida, não descartada
    expect(screen.getByRole('heading', { level: 1, name: 'ia.bimeta - Atendimento' })).toBeTruthy();
  });

  it('voltar ao Conhecimento devolve a revisão como estava, sem reler a lista nem perder o que foi escrito', async () => {
    logar();
    const l = lacuna();
    const f = criarFetch(
      rotasBase(ADMINISTRADOR, {
        'GET /agents/knowledge-gaps': json({ items: [l], total: 1 }),
        [`GET /agents/knowledge-gaps/${l.id}`]: json(detalheDaLacuna(l, { pergunta: 'Qual o prazo?' })),
      }),
    );
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Conhecimento' }));
    await usuario.click(await screen.findByRole('button', { name: /ocorrência/ }));
    await usuario.type(await screen.findByLabelText('Resposta'), 'Dois dias úteis.');

    await usuario.click(screen.getByRole('button', { name: 'Atendimento' }));
    await usuario.click(screen.getByRole('button', { name: 'Conhecimento' }));

    expect((screen.getByLabelText('Resposta') as HTMLTextAreaElement).value).toBe('Dois dias úteis.');
    expect(chamadasDe(f, 'GET /agents/knowledge-gaps')).toHaveLength(1);
  });

  it('permissão negada pelo servidor (403) na área: mensagem clara, mesmo com a concessão na tela', async () => {
    logar();
    const usuario = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      criarFetch(rotasBase(ADMINISTRADOR, { 'GET /agents/knowledge-gaps': problema(403, 'Acesso negado', 'Você não tem permissão para configurar o agente desta empresa.') })),
    );
    renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Conhecimento' }));

    await waitFor(() => expect(screen.getAllByRole('alert').map((a) => a.textContent).join('|')).toContain('Você não tem permissão para administrar o conhecimento do agente desta empresa'));
  });
});
