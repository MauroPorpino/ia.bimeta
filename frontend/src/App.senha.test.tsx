import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { createApi } from './api/client';
import { definirSessao, obterToken } from './api/session';
import { validarNovaSenha } from './senha';
import { FILA, USUARIO, chamadasDe, criarFetch, json, problema } from './test/helpers';

const SENHA_ATUAL = 'senha-atual-sintetica';
const SENHA_NOVA = 'senha-nova-sintetica-1';
const TOKEN = `ibr_${'A'.repeat(43)}`;

function logar() {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
}

function rotasDaFila(extras: Parameters<typeof criarFetch>[0] = {}) {
  return criarFetch({
    'GET /auth/me': json(USUARIO),
    'GET /contact-center/me/queues': json([FILA]),
    'GET /contact-center/me/presence': json({ userId: USUARIO.id, status: 'Available', updatedAt: null }),
    'GET /contact-center/items': json([]),
    ...extras,
  });
}

function renderizar() {
  return render(<App api={createApi()} intervaloFilaMs={60_000} intervaloConversaMs={60_000} />);
}

describe('validarNovaSenha', () => {
  it('aplica a política do servidor e confere a confirmação e a diferença da atual', () => {
    expect(validarNovaSenha('curta', 'curta')).toContain('ao menos 12');
    expect(validarNovaSenha('a'.repeat(129), 'a'.repeat(129))).toContain('no máximo 128');
    expect(validarNovaSenha(SENHA_NOVA, 'outra-coisa-qualquer')).toContain('confirmação');
    expect(validarNovaSenha(SENHA_NOVA, SENHA_NOVA, SENHA_NOVA)).toContain('diferente da atual');
    expect(validarNovaSenha(SENHA_NOVA, SENHA_NOVA, SENHA_ATUAL)).toBeNull();
    expect(validarNovaSenha(SENHA_NOVA, SENHA_NOVA)).toBeNull();
  });
});

describe('Trocar a própria senha', () => {
  it('envia a senha atual e a nova ao servidor e avisa que as outras sessões caíram, sem sair', async () => {
    logar();
    const f = rotasDaFila({ 'PUT /auth/me/password': new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    await screen.findByText('Ana Atendente');

    await usuario.click(screen.getByRole('button', { name: 'Trocar senha' }));
    await usuario.type(screen.getByLabelText('Senha atual'), SENHA_ATUAL);
    await usuario.type(screen.getByLabelText('Nova senha'), SENHA_NOVA);
    await usuario.type(screen.getByLabelText('Confirmar a nova senha'), SENHA_NOVA);
    await usuario.click(screen.getByRole('button', { name: 'Salvar nova senha' }));

    expect((await screen.findByRole('status')).textContent).toContain('Senha alterada');
    expect(chamadasDe(f, 'PUT /auth/me/password')).toEqual([{ corpo: { currentPassword: SENHA_ATUAL, newPassword: SENHA_NOVA }, consulta: expect.anything() }]);
    expect(obterToken()).toBe('token-sintetico'); // a sessão que trocou continua
    expect((screen.getByLabelText('Senha atual') as HTMLInputElement).value).toBe(''); // nada fica nos campos
  });

  it('não envia nada quando a nova senha é fraca, diferente da confirmação ou igual à atual', async () => {
    logar();
    const f = rotasDaFila({ 'PUT /auth/me/password': new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    await screen.findByText('Ana Atendente');
    await usuario.click(screen.getByRole('button', { name: 'Trocar senha' }));

    await usuario.type(screen.getByLabelText('Senha atual'), SENHA_ATUAL);
    await usuario.type(screen.getByLabelText('Nova senha'), 'curta');
    await usuario.type(screen.getByLabelText('Confirmar a nova senha'), 'curta');
    await usuario.click(screen.getByRole('button', { name: 'Salvar nova senha' }));
    expect((await screen.findByRole('alert')).textContent).toContain('ao menos 12');

    await usuario.clear(screen.getByLabelText('Nova senha'));
    await usuario.clear(screen.getByLabelText('Confirmar a nova senha'));
    await usuario.type(screen.getByLabelText('Nova senha'), SENHA_NOVA);
    await usuario.type(screen.getByLabelText('Confirmar a nova senha'), `${SENHA_NOVA}x`);
    await usuario.click(screen.getByRole('button', { name: 'Salvar nova senha' }));
    expect((await screen.findByRole('alert')).textContent).toContain('confirmação');

    expect(chamadasDe(f, 'PUT /auth/me/password')).toHaveLength(0);
  });

  it('mostra o erro do servidor (senha atual errada) e continua logado', async () => {
    logar();
    const f = rotasDaFila({ 'PUT /auth/me/password': problema(400, 'Dados inválidos', 'A senha atual não confere.') });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    await screen.findByText('Ana Atendente');
    await usuario.click(screen.getByRole('button', { name: 'Trocar senha' }));

    await usuario.type(screen.getByLabelText('Senha atual'), 'errada-errada-1');
    await usuario.type(screen.getByLabelText('Nova senha'), SENHA_NOVA);
    await usuario.type(screen.getByLabelText('Confirmar a nova senha'), SENHA_NOVA);
    await usuario.click(screen.getByRole('button', { name: 'Salvar nova senha' }));

    expect((await screen.findByRole('alert')).textContent).toContain('A senha atual não confere.');
    expect(obterToken()).toBe('token-sintetico');
  });

  it('Fechar esconde o painel', async () => {
    logar();
    vi.stubGlobal('fetch', rotasDaFila());
    const usuario = userEvent.setup();
    renderizar();
    await screen.findByText('Ana Atendente');

    await usuario.click(screen.getByRole('button', { name: 'Trocar senha' }));
    expect(screen.getByRole('heading', { name: 'Trocar minha senha' })).toBeTruthy();
    await usuario.click(screen.getByRole('button', { name: 'Fechar' }));

    expect(screen.queryByRole('heading', { name: 'Trocar minha senha' })).toBeNull();
  });
});

describe('Redefinir a senha com o token', () => {
  async function abrirRedefinicao() {
    const usuario = userEvent.setup();
    renderizar();
    await usuario.click(screen.getByRole('button', { name: /Esqueci a senha/ }));
    expect(screen.getByRole('heading', { name: 'Redefinir a senha' })).toBeTruthy();
    return usuario;
  }

  it('envia empresa, e-mail, token e a nova senha, sem sessão, e volta à entrada com o aviso', async () => {
    const f = criarFetch({ 'POST /auth/password-reset/redeem': new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', f);
    const usuario = await abrirRedefinicao();

    await usuario.type(screen.getByLabelText('Tenant'), 'demo-a');
    await usuario.type(screen.getByLabelText('E-mail'), 'ana@demo-a.local');
    await usuario.type(screen.getByLabelText('Token de redefinição'), ` ${TOKEN} `);
    await usuario.type(screen.getByLabelText('Nova senha'), SENHA_NOVA);
    await usuario.type(screen.getByLabelText('Confirmar a nova senha'), SENHA_NOVA);
    await usuario.click(screen.getByRole('button', { name: 'Redefinir senha' }));

    expect(await screen.findByRole('heading', { name: 'Entrar no atendimento' })).toBeTruthy();
    expect(screen.getByRole('status').textContent).toContain('Senha redefinida');
    expect(chamadasDe(f, 'POST /auth/password-reset/redeem')).toEqual([
      { corpo: { tenant: 'demo-a', email: 'ana@demo-a.local', token: TOKEN, newPassword: SENHA_NOVA }, consulta: expect.anything() },
    ]);
    const [, init] = f.mock.calls[0]!;
    expect(new Headers(init?.headers).get('Authorization')).toBeNull(); // anônimo
    expect(screen.queryByDisplayValue(TOKEN)).toBeNull(); // o token não fica na tela
  });

  it('mostra a recusa do servidor (token inválido ou vencido) sem sair da tela de redefinição', async () => {
    const f = criarFetch({ 'POST /auth/password-reset/redeem': problema(400, 'Redefinição recusada', 'O token de redefinição é inválido ou expirou.') });
    vi.stubGlobal('fetch', f);
    const usuario = await abrirRedefinicao();

    await usuario.type(screen.getByLabelText('Tenant'), 'demo-a');
    await usuario.type(screen.getByLabelText('E-mail'), 'ana@demo-a.local');
    await usuario.type(screen.getByLabelText('Token de redefinição'), TOKEN);
    await usuario.type(screen.getByLabelText('Nova senha'), SENHA_NOVA);
    await usuario.type(screen.getByLabelText('Confirmar a nova senha'), SENHA_NOVA);
    await usuario.click(screen.getByRole('button', { name: 'Redefinir senha' }));

    expect((await screen.findByRole('alert')).textContent).toContain('O token de redefinição é inválido ou expirou.');
    expect(screen.getByRole('heading', { name: 'Redefinir a senha' })).toBeTruthy();
  });

  it('não envia quando a nova senha é fraca ou a confirmação não confere', async () => {
    const f = criarFetch({ 'POST /auth/password-reset/redeem': new Response(null, { status: 204 }) });
    vi.stubGlobal('fetch', f);
    const usuario = await abrirRedefinicao();

    await usuario.type(screen.getByLabelText('Tenant'), 'demo-a');
    await usuario.type(screen.getByLabelText('E-mail'), 'ana@demo-a.local');
    await usuario.type(screen.getByLabelText('Token de redefinição'), TOKEN);
    await usuario.type(screen.getByLabelText('Nova senha'), SENHA_NOVA);
    await usuario.type(screen.getByLabelText('Confirmar a nova senha'), 'diferente-da-nova-1');
    await usuario.click(screen.getByRole('button', { name: 'Redefinir senha' }));

    expect((await screen.findByRole('alert')).textContent).toContain('confirmação');
    await waitFor(() => expect(chamadasDe(f, 'POST /auth/password-reset/redeem')).toHaveLength(0));
  });

  it('Voltar para a entrada abandona a redefinição', async () => {
    vi.stubGlobal('fetch', criarFetch({}));
    const usuario = await abrirRedefinicao();

    await usuario.click(screen.getByRole('button', { name: 'Voltar para a entrada' }));

    expect(screen.getByRole('heading', { name: 'Entrar no atendimento' })).toBeTruthy();
  });
});
