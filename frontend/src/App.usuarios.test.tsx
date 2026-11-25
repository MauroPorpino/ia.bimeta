import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { createApi } from './api/client';
import { definirSessao } from './api/session';
import type { CurrentUser } from './api/types';
import { ADMINISTRADOR, FILA, USUARIO, chamadasDe, criarFetch, json } from './test/helpers';

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

describe('App: área "Configurações" (Usuários)', () => {
  it('sem UserRead a aba não aparece e a API de usuários nem é chamada (mesmo para administrador sem a permissão)', async () => {
    logar();
    const semPermissao: CurrentUser = { ...ADMINISTRADOR, permissions: [{ permission: 'QueueRead', organizationId: null }] };
    const f = criarFetch(rotasBase(semPermissao));
    vi.stubGlobal('fetch', f);
    renderizar();

    const menu = await screen.findByRole('navigation', { name: 'Áreas do sistema' });

    expect(within(menu).queryByRole('button', { name: 'Configurações' })).toBeNull();
    expect(f.mock.calls.filter(([url]) => String(url).includes('/users'))).toHaveLength(0);
  });

  it('o atendente comum (sem /auth/me com permissões) não vê a aba', async () => {
    logar();
    vi.stubGlobal('fetch', criarFetch(rotasBase(USUARIO)));
    renderizar();

    await screen.findByText('Ana Atendente');
    await screen.findByRole('region', { name: 'Fila de atendimento' });

    expect(screen.queryByRole('button', { name: 'Configurações' })).toBeNull();
  });

  it('com UserRead em qualquer escopo a aba aparece e a tela só carrega na primeira visita', async () => {
    logar();
    const org = FILA.organizationId;
    const gerente: CurrentUser = { ...USUARIO, permissions: [{ permission: 'UserRead', organizationId: org }] };
    const f = criarFetch(
      rotasBase(gerente, {
        'GET /users/usage': json({ limit: null, registrationEnabled: true, active: 1, inactive: 0, pending: 0, expired: 0, cancelled: 0, seatsUsed: 1, available: null, overLimit: false, excess: 0 }),
        'GET /users/permission-catalog': json({ permissions: [], roles: [] }),
        'GET /organizations': json([{ id: org, name: 'Matriz' }]),
        'GET /users': json({ items: [], total: 0, page: 1, pageSize: 20 }),
      }),
    );
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    const menu = await screen.findByRole('navigation', { name: 'Áreas do sistema' });
    expect(chamadasDe(f, 'GET /users')).toHaveLength(0);

    await usuario.click(within(menu).getByRole('button', { name: 'Configurações' }));

    expect(await screen.findByRole('region', { name: 'Usuários' })).toBeTruthy();
    expect(screen.getByRole('heading', { level: 1, name: 'ia.bimeta - Configurações' })).toBeTruthy();
    expect(within(menu).getByRole('button', { name: 'Configurações' }).getAttribute('aria-current')).toBe('page');
    // Só leitura: sem o botão de cadastro.
    expect(screen.queryByRole('button', { name: 'Cadastrar usuário' })).toBeNull();
    expect(chamadasDe(f, 'GET /users')).toHaveLength(1);
  });
});
