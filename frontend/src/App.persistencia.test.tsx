import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { createApi } from './api/client';
import { definirSessao, reiniciarSessaoParaTeste } from './api/session';
import type { CurrentUser, Message, QueueItem } from './api/types';
import { FILA, OUTRO_USUARIO_ID, USUARIO, criarFetch, item, json, mensagem, problema } from './test/helpers';

const NUMERO = 'ffffffff-ffff-4fff-8fff-ffffffffffff';
const OUTRO_USUARIO: CurrentUser = { ...USUARIO, id: OUTRO_USUARIO_ID, email: 'pedro@demo-a.local', displayName: 'Pedro Atendente' };

const MAURO = item({ id: '10000000-0000-4000-8000-0000000000a1', status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });
const PEDRO = item({ id: '10000000-0000-4000-8000-0000000000a2', status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });

function logar(tenant = 'demo-a') {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString(), tenant);
}

function guardados(): string[] {
  return Object.keys(localStorage).filter((k) => k.startsWith('ia-bimeta.v1:') && k.includes(':r:'));
}

function rotasDoItem(it: QueueItem, nome: string, mensagens: () => Message[]) {
  return {
    [`GET /contact-center/items/${it.id}`]: json(it),
    [`GET /contact-center/items/${it.id}/conversation`]: json({ contactDisplayName: nome }),
    [`GET /contact-center/items/${it.id}/messages`]: () => json({ items: [...mensagens()].sort((a, b) => b.messageSeq - a.messageSeq), nextCursor: null }),
  };
}

function rotasBase(me: CurrentUser, itens: QueueItem[], extras: Parameters<typeof criarFetch>[0] = {}) {
  return {
    'GET /auth/me': json(me),
    'GET /contact-center/me/queues': json([FILA]),
    'GET /contact-center/me/presence': json({ userId: me.id, status: 'Available', updatedAt: null }),
    'GET /contact-center/items': json(itens),
    ...extras,
  };
}

function renderizarApp(intervalos: { fila?: number; conversa?: number } = {}) {
  return render(
    <App api={createApi()} intervaloFilaMs={intervalos.fila ?? 60_000} intervaloConversaMs={intervalos.conversa ?? 60_000} intervaloAlertasMs={60_000} />,
  );
}

const DO_MAURO = [mensagem({ messageSeq: 1, text: 'Pergunta do Mauro', channelInstanceId: NUMERO })];

describe('App: rascunhos persistentes e conversa aberta', () => {
  it('recarregar a página devolve o atendente à conversa em que estava, com o texto que digitava', async () => {
    logar();
    vi.stubGlobal('fetch', criarFetch({ ...rotasBase(USUARIO, [MAURO]), ...rotasDoItem(MAURO, 'Mauro', () => DO_MAURO) }));
    const usuario = userEvent.setup();
    const primeira = renderizarApp();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Texto que não enviei');
    primeira.unmount(); // a página foi recarregada

    renderizarApp();

    expect(((await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement).value).toBe('Texto que não enviei');
    expect(screen.getByRole('heading', { name: 'Mauro' })).toBeTruthy();
  });

  it('o navegador foi fechado e reaberto (a sessão da aba acabou): o atendente entra de novo e o rascunho continua lá', async () => {
    logar();
    vi.stubGlobal('fetch', criarFetch({ ...rotasBase(USUARIO, [MAURO]), ...rotasDoItem(MAURO, 'Mauro', () => DO_MAURO) }));
    const usuario = userEvent.setup();
    const primeira = renderizarApp();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Rascunho de ontem');
    primeira.unmount();
    sessionStorage.clear(); // fechar o navegador apaga o sessionStorage (e com ele o login e a conversa aberta); o localStorage fica
    reiniciarSessaoParaTeste();

    logar(); // entrou de novo, como o mesmo usuário
    renderizarApp();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));

    expect(((await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement).value).toBe('Rascunho de ontem');
  });

  it('a saída pedida pelo atendente apaga os rascunhos dele neste navegador', async () => {
    logar();
    vi.stubGlobal(
      'fetch',
      criarFetch({ ...rotasBase(USUARIO, [MAURO], { 'POST /auth/logout': new Response(null, { status: 204 }) }), ...rotasDoItem(MAURO, 'Mauro', () => DO_MAURO) }),
    );
    const usuario = userEvent.setup();
    renderizarApp();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Não pode ficar na máquina');
    expect(guardados()).toHaveLength(1);

    await usuario.click(screen.getByRole('button', { name: 'Sair' }));

    await screen.findByRole('heading', { name: 'Entrar no atendimento' });
    expect(guardados()).toHaveLength(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('a sessão que expira sozinha (401) NÃO apaga os rascunhos: eles voltam no próximo login', async () => {
    logar();
    let expirou = false;
    const base = criarFetch({ ...rotasBase(USUARIO, [MAURO]), ...rotasDoItem(MAURO, 'Mauro', () => DO_MAURO) });
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(async (entrada, init) => (expirou ? problema(401, 'Autenticação necessária') : base(entrada, init))),
    );
    const usuario = userEvent.setup();
    renderizarApp({ fila: 25, conversa: 25 });
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Sessão vai expirar');

    expirou = true;

    await screen.findByRole('heading', { name: 'Entrar no atendimento' });
    expect(guardados()).toHaveLength(1);
  });

  it('dois atendentes no mesmo navegador: o segundo não vê o rascunho do primeiro, e o do primeiro continua guardado para ele', async () => {
    logar();
    vi.stubGlobal('fetch', criarFetch({ ...rotasBase(USUARIO, [MAURO]), ...rotasDoItem(MAURO, 'Mauro', () => DO_MAURO) }));
    const usuario = userEvent.setup();
    const primeira = renderizarApp();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Texto da Ana');
    primeira.unmount();
    sessionStorage.clear();
    reiniciarSessaoParaTeste();

    // O Pedro entra no mesmo navegador (a mesma conversa, agora atribuída a ele).
    logar();
    const doPedro = { ...MAURO, assignedUserId: OUTRO_USUARIO_ID };
    vi.stubGlobal('fetch', criarFetch({ ...rotasBase(OUTRO_USUARIO, [doPedro]), ...rotasDoItem(doPedro, 'Mauro', () => DO_MAURO) }));
    renderizarApp();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));

    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;
    expect(campo.value).toBe('');
    expect(screen.queryByText('Texto da Ana')).toBeNull();
    expect(guardados()).toHaveLength(1); // o da Ana continua lá, só dela
    expect(guardados()[0]).toContain(USUARIO.id);
    expect(guardados()[0]).not.toContain(OUTRO_USUARIO_ID);
  });

  it('o mesmo usuário em outro tenant também não vê o rascunho', async () => {
    logar('demo-a');
    vi.stubGlobal('fetch', criarFetch({ ...rotasBase(USUARIO, [MAURO]), ...rotasDoItem(MAURO, 'Mauro', () => DO_MAURO) }));
    const usuario = userEvent.setup();
    const primeira = renderizarApp();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Texto do tenant A');
    primeira.unmount();
    sessionStorage.clear();
    reiniciarSessaoParaTeste();

    logar('demo-b');
    renderizarApp();
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));

    expect(((await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement).value).toBe('');
  });
});

describe('App: mensagens novas na fila', () => {
  it('a conversa do atendente com mensagem nova do cliente ganha o selo; ao abri-la o selo some; nova mensagem o traz de volta', async () => {
    logar();
    let doPedro = [mensagem({ messageSeq: 1, text: 'Oi do Pedro' }), mensagem({ messageSeq: 2, text: 'Alguém aí?' })];
    let doMauro = [mensagem({ messageSeq: 1, text: 'Oi do Mauro' })];
    vi.stubGlobal(
      'fetch',
      criarFetch({
        ...rotasBase(USUARIO, [MAURO, PEDRO]),
        ...rotasDoItem(MAURO, 'Mauro', () => doMauro),
        ...rotasDoItem(PEDRO, 'Pedro', () => doPedro),
      }),
    );
    const usuario = userEvent.setup();
    renderizarApp({ fila: 25, conversa: 25 });
    await usuario.click(await screen.findByRole('tab', { name: /^Meus/ }));
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));

    // Mauro está aberto; o Pedro nunca foi aberto: as duas mensagens dele são novas.
    await waitFor(() => expect(screen.getByRole('button', { name: /Pedro/ }).textContent).toContain('2 novas'));
    expect(screen.getByRole('button', { name: /Mauro/ }).textContent).not.toContain('nova');

    await usuario.click(screen.getByRole('button', { name: /Pedro/ }));
    await screen.findByText('Alguém aí?');
    await waitFor(() => expect(screen.getByRole('button', { name: /Pedro/ }).textContent).not.toContain('nova')); // aberta: sem selo

    // De volta ao Mauro, o Pedro foi visto; chega mensagem nova do Pedro: o selo volta com 1.
    await usuario.click(screen.getByRole('button', { name: /Mauro/ }));
    doPedro = [...doPedro, mensagem({ messageSeq: 3, text: 'Chegou mais uma' })];
    await waitFor(() => expect(screen.getByRole('button', { name: /Pedro/ }).textContent).toContain('1 nova'));
    expect(screen.getByRole('button', { name: /Mauro/ }).textContent).not.toContain('nova');
    doMauro = [...doMauro];
  });

  it('mensagem do próprio atendente ou da IA não conta como nova', async () => {
    logar();
    const minha = mensagem({ messageSeq: 2, direction: 'Outbound', author: 'Human', text: 'Resposta minha', deliveryState: 'Sent' });
    vi.stubGlobal(
      'fetch',
      criarFetch({
        ...rotasBase(USUARIO, [MAURO, PEDRO]),
        ...rotasDoItem(MAURO, 'Mauro', () => DO_MAURO),
        ...rotasDoItem(PEDRO, 'Pedro', () => [mensagem({ messageSeq: 1, text: 'Visto' }), minha]),
      }),
    );
    const usuario = userEvent.setup();
    renderizarApp({ fila: 25, conversa: 25 });
    await usuario.click(await screen.findByRole('tab', { name: /^Meus/ }));
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await usuario.click(await screen.findByRole('button', { name: /Pedro/ }));
    await screen.findByText('Resposta minha');
    await usuario.click(screen.getByRole('button', { name: /Mauro/ }));
    await new Promise((resolve) => setTimeout(resolve, 150));

    expect(screen.getByRole('button', { name: /Pedro/ }).textContent).not.toContain('nova');
  });
});
