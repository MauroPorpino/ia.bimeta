import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { createApi } from './api/client';
import { definirSessao } from './api/session';
import type { CurrentUser } from './api/types';
import { ADMINISTRADOR, FILA, USUARIO, criarFetch, json } from './test/helpers';

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

describe('App: área "Fluxos" (chatbot visual, ADR 0037)', () => {
  it('o atendente comum não vê a área e a API de fluxos nem é chamada', async () => {
    logar();
    const f = criarFetch(rotasBase(USUARIO));
    vi.stubGlobal('fetch', f);
    renderizar();

    await screen.findByText('Ana Atendente');
    await screen.findByRole('region', { name: 'Fila de atendimento' });

    expect(screen.queryByRole('button', { name: 'Fluxos' })).toBeNull();
    expect(f.mock.calls.filter(([url]) => String(url).includes('/workflows/'))).toHaveLength(0);
  });

  it('o administrador abre a área sob demanda e só então a lista de fluxos é consultada', async () => {
    logar();
    const f = criarFetch(rotasBase(ADMINISTRADOR, { 'GET /workflows/definitions': json([]) }));
    vi.stubGlobal('fetch', f);
    renderizar();

    await screen.findByText('Beto Administrador');
    expect(f.mock.calls.filter(([url]) => String(url).includes('/workflows/'))).toHaveLength(0);

    await userEvent.click(await screen.findByRole('button', { name: 'Fluxos' }));

    expect(await screen.findByRole('heading', { name: 'Fluxos do chatbot' })).toBeTruthy();
    expect(await screen.findByText(/Nenhum fluxo ainda/)).toBeTruthy();
    expect(f.mock.calls.filter(([url]) => String(url).includes('/workflows/definitions'))).toHaveLength(1);
    expect(screen.getByRole('heading', { name: 'ia.bimeta - Fluxos' })).toBeTruthy();
  });
});
