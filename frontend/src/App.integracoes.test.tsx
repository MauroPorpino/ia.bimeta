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

describe('App: área "Integrações" (ADR 0035)', () => {
  it('o atendente comum não vê a área e a API do motor de integração nem é chamada', async () => {
    logar();
    const f = criarFetch(rotasBase(USUARIO));
    vi.stubGlobal('fetch', f);
    renderizar();

    await screen.findByText('Ana Atendente');
    await screen.findByRole('region', { name: 'Fila de atendimento' });

    expect(screen.queryByRole('button', { name: 'Integrações' })).toBeNull();
    expect(f.mock.calls.filter(([url]) => String(url).includes('/capabilities/'))).toHaveLength(0);
  });

  it('o administrador de uma organização vê Integrações (mas não Conhecimento) e a área só é montada na primeira visita', async () => {
    logar();
    const deOrganizacao: CurrentUser = { ...ADMINISTRADOR, grants: [{ organizationId: FILA.organizationId, role: 'OrganizationAdmin' }] };
    const f = criarFetch(rotasBase(deOrganizacao, { 'GET /capabilities/connections': json([]) }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    const menu = await screen.findByRole('navigation', { name: 'Áreas do sistema' });
    expect(within(menu).queryByRole('button', { name: 'Conhecimento' })).toBeNull();
    expect(chamadasDe(f, 'GET /capabilities/connections')).toHaveLength(0);

    await usuario.click(within(menu).getByRole('button', { name: 'Integrações' }));

    expect(await screen.findByRole('region', { name: 'Conexões' })).toBeTruthy();
    expect(screen.getByRole('heading', { level: 1, name: 'ia.bimeta - Integrações' })).toBeTruthy();
    expect(within(menu).getByRole('button', { name: 'Integrações' }).getAttribute('aria-current')).toBe('page');
    expect(chamadasDe(f, 'GET /capabilities/connections')).toHaveLength(1);
  });

  it('o gerente também vê a área; voltar ao Atendimento mantém a área montada, sem nova chamada', async () => {
    logar();
    const gerente: CurrentUser = { ...USUARIO, grants: [{ organizationId: FILA.organizationId, role: 'Manager' }] };
    const f = criarFetch(rotasBase(gerente, { 'GET /capabilities/connections': json([]) }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    const menu = await screen.findByRole('navigation', { name: 'Áreas do sistema' });
    await usuario.click(within(menu).getByRole('button', { name: 'Integrações' }));
    await screen.findByRole('region', { name: 'Conexões' });
    await usuario.click(within(menu).getByRole('button', { name: 'Atendimento' }));

    expect(screen.getByRole('heading', { level: 1, name: 'ia.bimeta - Atendimento' })).toBeTruthy();
    expect(chamadasDe(f, 'GET /capabilities/connections')).toHaveLength(1);
  });

  it('o administrador do tenant vê as duas áreas', async () => {
    logar();
    vi.stubGlobal('fetch', criarFetch(rotasBase(ADMINISTRADOR)));
    renderizar();

    const menu = await screen.findByRole('navigation', { name: 'Áreas do sistema' });

    expect(within(menu).getByRole('button', { name: 'Conhecimento' })).toBeTruthy();
    expect(within(menu).getByRole('button', { name: 'Integrações' })).toBeTruthy();
  });
});
