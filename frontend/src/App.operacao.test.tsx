import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { createApi } from './api/client';
import { definirSessao } from './api/session';
import type { QueueItem } from './api/types';
import { FILA, USUARIO, criarFetch, item, json, mensagem } from './test/helpers';

const NUMERO = 'ffffffff-ffff-4fff-8fff-ffffffffffff';

const MAURO = item({ id: '10000000-0000-4000-8000-0000000000a1', status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });
const PEDRO = item({ id: '10000000-0000-4000-8000-0000000000a2', status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });

function logar() {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
}

/** As rotas de um atendimento: o item, a sessão (nome do contato) e as mensagens (uma do cliente, com o texto dado). */
function rotasDoItem(it: QueueItem, nome: string, pergunta: string, extras: Parameters<typeof criarFetch>[0] = {}) {
  return {
    [`GET /contact-center/items/${it.id}`]: json(it),
    [`GET /contact-center/items/${it.id}/conversation`]: json({ contactDisplayName: nome }),
    [`GET /contact-center/items/${it.id}/messages`]: json({ items: [mensagem({ text: pergunta, channelInstanceId: NUMERO })], nextCursor: null }),
    ...extras,
  };
}

function rotasBase(itens: QueueItem[], extras: Parameters<typeof criarFetch>[0] = {}) {
  return {
    'GET /auth/me': json(USUARIO),
    'GET /contact-center/me/queues': json([FILA]),
    'GET /contact-center/me/presence': json({ userId: USUARIO.id, status: 'Available', updatedAt: null }),
    'GET /contact-center/items': json(itens),
    ...extras,
  };
}

describe('App: operação diária', () => {
  it('trocar de atendimento e voltar: o histórico e o rascunho de cada um continuam como estavam', async () => {
    logar();
    let apiDoMauroFora = false;
    const f = criarFetch({
      ...rotasBase([MAURO, PEDRO]),
      ...rotasDoItem(MAURO, 'Mauro', 'Pergunta do Mauro'),
      ...rotasDoItem(PEDRO, 'Pedro', 'Pergunta do Pedro'),
      // Depois da primeira exibição, a API do Mauro deixa de responder: o que aparece ao voltar vem da memória da tela.
      [`GET /contact-center/items/${MAURO.id}/messages`]: () =>
        apiDoMauroFora ? new Promise<Response>(() => undefined) : json({ items: [mensagem({ text: 'Pergunta do Mauro' })], nextCursor: null }),
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    render(<App api={createApi()} intervaloFilaMs={60_000} intervaloConversaMs={60_000} intervaloAlertasMs={60_000} />);

    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await screen.findByText('Pergunta do Mauro');
    apiDoMauroFora = true;
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Rascunho para o Mauro');

    await usuario.click(screen.getByRole('button', { name: /Pedro/ }));
    expect(await screen.findByText('Pergunta do Pedro')).toBeTruthy();
    expect(screen.queryByText('Pergunta do Mauro')).toBeNull();
    expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).value).toBe('');
    await usuario.type(screen.getByLabelText('Resposta ao cliente'), 'Rascunho para o Pedro');

    await usuario.click(screen.getByRole('button', { name: /Mauro/ }));
    expect(screen.getByText('Pergunta do Mauro')).toBeTruthy(); // na hora, antes de a API responder
    expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).value).toBe('Rascunho para o Mauro');

    await usuario.click(screen.getByRole('button', { name: /Pedro/ }));
    expect((await screen.findByLabelText('Resposta ao cliente') as HTMLTextAreaElement).value).toBe('Rascunho para o Pedro');
  });

  it('a conexão cai e volta: faixa de reconexão, a tela não esvazia e, ao voltar, tudo é relido sem duplicar', async () => {
    logar();
    let fora = false;
    const base = criarFetch({ ...rotasBase([MAURO]), ...rotasDoItem(MAURO, 'Mauro', 'Pergunta do Mauro') });
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(async (entrada, init) => {
        if (fora) {
          throw new TypeError('Failed to fetch');
        }
        return base(entrada, init);
      }),
    );
    const usuario = userEvent.setup();
    render(<App api={createApi()} intervaloFilaMs={25} intervaloConversaMs={25} intervaloAlertasMs={60_000} />);
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    await screen.findByText('Pergunta do Mauro');

    fora = true;

    const faixa = await screen.findByText(/Sem conexão com o servidor/);
    expect(faixa.closest('[role="alert"]')).toBeTruthy();
    expect(screen.getByText('Pergunta do Mauro')).toBeTruthy(); // continua na tela
    expect(screen.getAllByRole('alert')).toHaveLength(1); // uma faixa só, não uma por carregamento que falhou

    fora = false;

    expect(await screen.findByText(/Conexão restabelecida/)).toBeTruthy();
    await waitFor(() => expect(screen.queryByText(/Sem conexão com o servidor/)).toBeNull());
    expect(screen.getAllByText('Pergunta do Mauro')).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: /Mauro/ })).toHaveLength(1);
  });

  it('resposta que falhou por falta de conexão: o erro aparece, o texto fica, e quando a conexão volta o erro some e o reenvio sai uma vez só', async () => {
    logar();
    let fora = false;
    const base = criarFetch({
      ...rotasBase([MAURO]),
      ...rotasDoItem(MAURO, 'Mauro', 'Pergunta do Mauro'),
      [`POST /contact-center/items/${MAURO.id}/reply`]: json({ outcome: 'Completed' }),
    });
    const f = vi.fn<typeof fetch>(async (entrada, init) => {
      if (fora) {
        throw new TypeError('Failed to fetch');
      }
      return base(entrada, init);
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    render(<App api={createApi()} intervaloFilaMs={25} intervaloConversaMs={25} intervaloAlertasMs={60_000} />);
    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;
    await usuario.type(campo, 'Escrita durante a queda');

    fora = true;
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    expect(await screen.findByText(/Sem conexão com a API/)).toBeTruthy(); // o erro da ação
    expect(campo.value).toBe('Escrita durante a queda');

    fora = false;

    await waitFor(() => expect(screen.queryByText(/Sem conexão com a API/)).toBeNull(), { timeout: 4000 }); // some sozinho
    expect(campo.value).toBe('Escrita durante a queda');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(campo.value).toBe(''));
    const pedidos = f.mock.calls.filter(([url]) => String(url).endsWith('/reply')).map(([, init]) => (JSON.parse(String(init?.body)) as { requestId: string }).requestId);
    expect(new Set(pedidos).size).toBe(1); // as tentativas com o servidor fora e a que deu certo usam o MESMO requestId
  });

  it('API fora ao abrir a tela: tenta de novo sozinho e, quando ela volta, a fila aparece sem ninguém clicar', async () => {
    logar();
    let fora = true;
    const base = criarFetch({ ...rotasBase([MAURO]), ...rotasDoItem(MAURO, 'Mauro', 'Pergunta do Mauro') });
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(async (entrada, init) => {
        if (fora) {
          throw new TypeError('Failed to fetch');
        }
        return base(entrada, init);
      }),
    );
    render(<App api={createApi()} intervaloFilaMs={25} intervaloConversaMs={25} intervaloAlertasMs={60_000} />);
    expect(await screen.findByRole('button', { name: 'Tentar novamente' })).toBeTruthy();

    fora = false;

    expect(await screen.findByRole('button', { name: /Mauro/ })).toBeTruthy();
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    expect(screen.getByText('Ana Atendente')).toBeTruthy();
  });

  it('o navegador avisa que a rede voltou: a tela relê na hora, sem esperar o próximo ciclo', async () => {
    logar();
    const f = criarFetch({ ...rotasBase([MAURO]), ...rotasDoItem(MAURO, 'Mauro', 'Pergunta do Mauro') });
    vi.stubGlobal('fetch', f);
    render(<App api={createApi()} intervaloFilaMs={60_000} intervaloConversaMs={60_000} intervaloAlertasMs={60_000} />);
    await screen.findByRole('button', { name: /Mauro/ });
    const antes = f.mock.calls.filter(([url]) => String(url).includes('/contact-center/items?')).length;

    window.dispatchEvent(new Event('online'));

    await waitFor(() => expect(f.mock.calls.filter(([url]) => String(url).includes('/contact-center/items?')).length).toBeGreaterThan(antes));
  });

  it('conta de WhatsApp pausada: faixa no topo e aviso na conversa do número afetado', async () => {
    logar();
    vi.stubGlobal(
      'fetch',
      criarFetch({
        ...rotasBase([MAURO]),
        ...rotasDoItem(MAURO, 'Mauro', 'Pergunta do Mauro'),
        'GET /channels/sending-alerts': json([
          { accountId: 'aaaa0000-0000-4000-8000-000000000001', accountName: 'Laboratório Central', paused: true, cooldownUntil: null, channelInstanceIds: [NUMERO] },
        ]),
      }),
    );
    const usuario = userEvent.setup();
    render(<App api={createApi()} intervaloFilaMs={60_000} intervaloConversaMs={60_000} intervaloAlertasMs={60_000} />);

    expect(await screen.findByText(/"Laboratório Central" está pausado/)).toBeTruthy();

    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));
    expect(await screen.findByText(/envio por este número do WhatsApp está pausado/)).toBeTruthy();
  });

  it('sem a rota de avisos (API antiga) ou com erro nela, a operação segue sem faixa e sem erro', async () => {
    logar();
    vi.stubGlobal('fetch', criarFetch({ ...rotasBase([MAURO]), ...rotasDoItem(MAURO, 'Mauro', 'Pergunta do Mauro') }));
    render(<App api={createApi()} intervaloFilaMs={60_000} intervaloConversaMs={60_000} intervaloAlertasMs={20} />);

    expect(await screen.findByRole('button', { name: /Mauro/ })).toBeTruthy();
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByLabelText('Avisos de envio do WhatsApp')).toBeNull();
  });

  it('quando a conta é retomada, a faixa de envio some sozinha', async () => {
    logar();
    let pausada = true;
    vi.stubGlobal(
      'fetch',
      criarFetch({
        ...rotasBase([MAURO]),
        ...rotasDoItem(MAURO, 'Mauro', 'Pergunta do Mauro'),
        'GET /channels/sending-alerts': () =>
          json(pausada ? [{ accountId: 'aaaa0000-0000-4000-8000-000000000001', accountName: 'Laboratório Central', paused: true, cooldownUntil: null, channelInstanceIds: [NUMERO] }] : []),
      }),
    );
    render(<App api={createApi()} intervaloFilaMs={60_000} intervaloConversaMs={60_000} intervaloAlertasMs={20} />);
    await screen.findByText(/"Laboratório Central" está pausado/);

    pausada = false;

    await waitFor(() => expect(screen.queryByText(/"Laboratório Central" está pausado/)).toBeNull());
  });
});
