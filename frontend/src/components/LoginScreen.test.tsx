import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import { obterToken } from '../api/session';
import { chamadasDe, criarFetch, json, problema } from '../test/helpers';
import { LoginScreen } from './LoginScreen';

async function preencher(usuario: ReturnType<typeof userEvent.setup>) {
  await usuario.type(screen.getByLabelText('Tenant'), 'demo-a');
  await usuario.type(screen.getByLabelText('E-mail'), 'admin@demo-a.local');
  await usuario.type(screen.getByLabelText('Senha'), 'senha-sintetica');
}

describe('LoginScreen', () => {
  it('mostra os campos de tenant, e-mail e senha', () => {
    vi.stubGlobal('fetch', criarFetch({}));
    render(<LoginScreen api={createApi()} onEntrou={() => undefined} />);

    expect(screen.getByRole('heading', { name: 'Entrar no atendimento' })).toBeTruthy();
    expect((screen.getByLabelText('Tenant') as HTMLInputElement).required).toBe(true);
    expect(screen.getByLabelText('E-mail').getAttribute('type')).toBe('email');
    expect(screen.getByLabelText('Senha').getAttribute('type')).toBe('password');
  });

  it('entra: envia as credenciais, guarda o token e avisa o App', async () => {
    const f = criarFetch({ 'POST /auth/login': json({ token: 'token-sintetico', expiresAt: new Date(Date.now() + 3_600_000).toISOString() }) });
    vi.stubGlobal('fetch', f);
    const aoEntrar = vi.fn();
    const usuario = userEvent.setup();
    render(<LoginScreen api={createApi()} onEntrou={aoEntrar} />);

    await preencher(usuario);
    await usuario.click(screen.getByRole('button', { name: 'Entrar' }));

    await waitFor(() => expect(aoEntrar).toHaveBeenCalledTimes(1));
    expect(chamadasDe(f, 'POST /auth/login')[0]?.corpo).toEqual({ tenant: 'demo-a', email: 'admin@demo-a.local', password: 'senha-sintetica' });
    expect(obterToken()).toBe('token-sintetico');
    expect(sessionStorage.getItem('ia-bimeta.sessao')).toContain('token-sintetico');
  });

  it('credenciais erradas: mostra o motivo da API e não entra', async () => {
    vi.stubGlobal('fetch', criarFetch({ 'POST /auth/login': problema(401, 'Credenciais inválidas', 'Tenant, e-mail ou senha não conferem.') }));
    const aoEntrar = vi.fn();
    const usuario = userEvent.setup();
    render(<LoginScreen api={createApi()} onEntrou={aoEntrar} />);

    await preencher(usuario);
    await usuario.click(screen.getByRole('button', { name: 'Entrar' }));

    expect((await screen.findByRole('alert')).textContent).toBe('Credenciais inválidas: Tenant, e-mail ou senha não conferem.');
    expect(aoEntrar).not.toHaveBeenCalled();
    expect(obterToken()).toBeNull();
  });

  it('API fora do ar: mostra o erro de rede na própria tela, sem quebrar', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
    const usuario = userEvent.setup();
    render(<LoginScreen api={createApi()} onEntrou={() => undefined} />);

    await preencher(usuario);
    await usuario.click(screen.getByRole('button', { name: 'Entrar' }));

    expect((await screen.findByRole('alert')).textContent).toContain('Sem conexão com a API');
    expect((screen.getByRole('button', { name: 'Entrar' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('duplo clique em Entrar faz um único login', async () => {
    const f = criarFetch({ 'POST /auth/login': json({ token: 't', expiresAt: new Date(Date.now() + 3_600_000).toISOString() }) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    render(<LoginScreen api={createApi()} onEntrou={() => undefined} />);

    await preencher(usuario);
    await usuario.dblClick(screen.getByRole('button', { name: 'Entrar' }));

    await waitFor(() => expect(obterToken()).toBe('t'));
    expect(chamadasDe(f, 'POST /auth/login')).toHaveLength(1);
  });

  it('mostra o aviso de sessão expirada', () => {
    vi.stubGlobal('fetch', criarFetch({}));
    render(<LoginScreen api={createApi()} aviso="Sua sessão expirou." onEntrou={() => undefined} />);

    expect(screen.getByRole('status').textContent).toBe('Sua sessão expirou.');
  });
});
