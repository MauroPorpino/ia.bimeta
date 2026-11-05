import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { createApi } from './api/client';
import { definirSessao, obterToken } from './api/session';
import { FILA, USUARIO, chamadasDe, criarFetch, item, json, mensagem, problema } from './test/helpers';

const ITEM = item({ id: '10000000-0000-4000-8000-000000000009' });

function logar() {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
}

function rotasDaFila(extras: Parameters<typeof criarFetch>[0] = {}) {
  return criarFetch({
    'GET /auth/me': json(USUARIO),
    'GET /contact-center/me/queues': json([FILA]),
    'GET /contact-center/me/presence': json({ userId: USUARIO.id, status: 'Available', updatedAt: null }),
    'GET /contact-center/items': json([ITEM]),
    [`GET /contact-center/items/${ITEM.id}/conversation`]: json({ contactDisplayName: 'Mauro' }),
    [`GET /contact-center/items/${ITEM.id}`]: json(ITEM),
    [`GET /contact-center/items/${ITEM.id}/messages`]: json({ items: [mensagem({ text: 'Preciso de jejum?' })], nextCursor: null }),
    ...extras,
  });
}

function renderizar() {
  return render(<App api={createApi()} intervaloFilaMs={60_000} intervaloConversaMs={60_000} />);
}

describe('App', () => {
  it('sem sessão mostra a tela de entrada', () => {
    vi.stubGlobal('fetch', criarFetch({}));
    renderizar();

    expect(screen.getByRole('heading', { name: 'Entrar no atendimento' })).toBeTruthy();
  });

  it('entra, mostra o nome do atendente e a fila, e abre a conversa do item escolhido', async () => {
    const f = rotasDaFila({ 'POST /auth/login': json({ token: 'token-sintetico', expiresAt: new Date(Date.now() + 3_600_000).toISOString() }) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    await usuario.type(screen.getByLabelText('Tenant'), 'demo-a');
    await usuario.type(screen.getByLabelText('E-mail'), 'ana@demo-a.local');
    await usuario.type(screen.getByLabelText('Senha'), 'senha-sintetica');
    await usuario.click(screen.getByRole('button', { name: 'Entrar' }));

    expect(await screen.findByText('Ana Atendente')).toBeTruthy();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));

    expect(await screen.findByText('Preciso de jejum?')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Mauro' })).toBeTruthy();
    // as chamadas seguintes já levam o token
    const comToken = f.mock.calls.filter(([url]) => String(url).includes('/contact-center/'));
    expect(comToken.length).toBeGreaterThan(0);
    comToken.forEach(([, init]) => expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer token-sintetico'));
  });

  it('401 em qualquer chamada volta à tela de entrada com aviso', async () => {
    logar();
    vi.stubGlobal('fetch', rotasDaFila({ 'GET /contact-center/items': problema(401, 'Autenticação necessária') }));
    renderizar();

    expect(await screen.findByRole('heading', { name: 'Entrar no atendimento' })).toBeTruthy();
    expect(screen.getByRole('status').textContent).toContain('Sua sessão expirou');
    expect(obterToken()).toBeNull();
  });

  it('Sair chama logout e volta à entrada sem aviso de expiração', async () => {
    logar();
    const f = rotasDaFila({ 'POST /auth/logout': new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    await screen.findByText('Ana Atendente');

    await usuario.click(screen.getByRole('button', { name: 'Sair' }));

    expect(await screen.findByRole('heading', { name: 'Entrar no atendimento' })).toBeTruthy();
    expect(chamadasDe(f, 'POST /auth/logout')).toHaveLength(1);
    expect(screen.queryByRole('status')).toBeNull();
    expect(obterToken()).toBeNull();
  });

  it('API fora do ar: o erro de rede aparece na faixa e a tela não quebra', async () => {
    logar();
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
    const usuario = userEvent.setup();
    renderizar();

    const faixa = await screen.findByRole('alert');
    expect(faixa.textContent).toContain('Sem conexão com a API');
    expect(screen.getByRole('button', { name: 'Tentar novamente' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Sair' })).toBeTruthy();

    await usuario.click(screen.getByRole('button', { name: 'Tentar novamente' }));
    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy());
  });
});
