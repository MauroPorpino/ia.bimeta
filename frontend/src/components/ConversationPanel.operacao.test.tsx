import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { Message, QueueItem, SendingAlert } from '../api/types';
import { CacheDeConversas } from '../cache';
import { OUTRO_USUARIO_ID, USUARIO, chamadasDe, criarFetch, item, json, mensagem, problema } from '../test/helpers';
import { ConversationPanel } from './ConversationPanel';

const ID = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const ROTA_ITEM = `GET /contact-center/items/${ID}`;
const ROTA_MENSAGENS = `GET /contact-center/items/${ID}/messages`;
const ROTA_RESPONDER = `POST /contact-center/items/${ID}/reply`;
const ROTA_ASSUMIR = `POST /contact-center/items/${ID}/claim`;

const AGUARDANDO = item({ status: 'Waiting' });
const COMIGO = item({ status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });
const COM_OUTRO = item({ status: 'Assigned', assignedUserId: OUTRO_USUARIO_ID });

function pagina(...itens: Message[]) {
  return json({ items: [...itens].sort((a, b) => b.messageSeq - a.messageSeq), nextCursor: null });
}

function renderizar(propriedades: Partial<Parameters<typeof ConversationPanel>[0]> = {}) {
  const aoErroDeRede = vi.fn();
  const aoErroDeAcao = vi.fn();
  const aoAlterar = vi.fn();
  const resultado = render(
    <ConversationPanel
      api={createApi()}
      itemId={ID}
      me={USUARIO}
      nomeDoContato="Mauro"
      onErroDeRede={aoErroDeRede}
      onErroDeAcao={aoErroDeAcao}
      onAlterado={aoAlterar}
      intervaloMs={60_000}
      {...propriedades}
    />,
  );
  return { ...resultado, aoErroDeRede, aoErroDeAcao, aoAlterar };
}

describe('ConversationPanel: trocar de conversa sem perder nada', () => {
  it('ao voltar, o histórico já lido aparece na hora (antes de qualquer resposta da API) e o rascunho está no campo', async () => {
    const cache = new CacheDeConversas();
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: pagina(mensagem({ text: 'Preciso de jejum?', messageSeq: 1 })) }));
    const usuario = userEvent.setup();
    const primeira = renderizar({ cache });
    await screen.findByText('Preciso de jejum?');
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Sim, oito horas');
    primeira.unmount();

    // A API demora (ou nem responde): a tela que volta mostra o que a memória já tem.
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => new Promise<Response>(() => undefined)));
    renderizar({ cache });

    expect(screen.getByText('Preciso de jejum?')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Responder' })).toBeTruthy(); // o item (assumido por mim) também veio da memória
    expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).value).toBe('Sim, oito horas');
  });

  it('cada conversa tem a sua memória: o rascunho de uma nunca aparece na outra', async () => {
    const cache = new CacheDeConversas();
    cache.gravarRascunho('outro-item', 'rascunho de outra conversa');
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: pagina() }));
    renderizar({ cache });

    expect(((await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement).value).toBe('');
  });

  it('o polling junta o que chega ao que já está na tela: nunca duplica e não perde as mensagens antigas', async () => {
    const m1 = mensagem({ text: 'primeira', messageSeq: 1 });
    const m2 = mensagem({ text: 'segunda', messageSeq: 2 });
    const m3 = mensagem({ text: 'terceira', messageSeq: 3 });
    let atual: Message[] = [m1, m2];
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: () => pagina(...atual) }));
    renderizar({ intervaloMs: 20 });
    await screen.findByText('segunda');

    // A janela da API deslizou (só as mais novas) e a segunda mensagem foi repetida: a tela fica com as três, uma vez cada.
    atual = [m2, m3];
    await screen.findByText('terceira');
    await new Promise((resolve) => setTimeout(resolve, 120)); // mais alguns ciclos de polling: nada pode se repetir

    expect(screen.getAllByText('primeira')).toHaveLength(1);
    expect(screen.getAllByText('segunda')).toHaveLength(1);
    expect(screen.getAllByText('terceira')).toHaveLength(1);
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
  });

  it('o estado de entrega de uma mensagem já exibida é atualizado no lugar, sem repetir a mensagem', async () => {
    const saida = mensagem({ direction: 'Outbound', author: 'Human', text: 'Bom dia!', messageSeq: 1, deliveryState: 'Queued' });
    let atual: Message = saida;
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: () => pagina(atual) }));
    renderizar({ intervaloMs: 20 });
    expect(await screen.findByText('na fila de envio')).toBeTruthy();

    atual = { ...saida, deliveryState: 'Delivered' };

    expect(await screen.findByText('entregue')).toBeTruthy();
    expect(screen.queryByText('na fila de envio')).toBeNull();
    expect(screen.getAllByText('Bom dia!')).toHaveLength(1);
  });

  it('trocar de conversa no meio do envio: a resposta sai uma vez e o rascunho guardado é limpo ao concluir', async () => {
    const cache = new CacheDeConversas();
    let liberar: (r: Response) => void = () => undefined;
    const resposta = new Promise<Response>((resolve) => {
      liberar = resolve;
    });
    const f = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: pagina(), [ROTA_RESPONDER]: () => resposta });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { unmount } = renderizar({ cache });
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Posso agendar para amanhã.');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(1));

    unmount(); // o atendente abriu outra conversa
    liberar(json({}));

    await waitFor(() => expect(cache.lerRascunho(ID)).toBe(''));
    expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(1);
  });
});

describe('ConversationPanel: concorrência entre atendentes', () => {
  it('perdeu a corrida ao assumir: diz que outro atendente assumiu, relê e passa a mostrar quem está com a conversa', async () => {
    let atual: QueueItem = AGUARDANDO;
    const f = criarFetch({
      [ROTA_ITEM]: () => json(atual),
      [ROTA_MENSAGENS]: pagina(),
      [ROTA_ASSUMIR]: () => {
        atual = COM_OUTRO; // o colega assumiu um instante antes
        return problema(409, 'Conflito', 'O atendimento já está atribuído.', 'QueueItemConflict');
      },
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { aoAlterar } = renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Assumir' }));

    expect(await screen.findByText(/Outro atendente assumiu este atendimento antes de você/)).toBeTruthy();
    expect(await screen.findByText('Este atendimento está com outro atendente.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Assumir' })).toBeNull();
    expect(aoAlterar).toHaveBeenCalled();
  });

  it('o colega assume e a leitura passa a ser negada (403): some a conversa e as ações, sem erro vermelho, e a fila é avisada uma vez só', async () => {
    const cache = new CacheDeConversas();
    let negado = false;
    const f = criarFetch({
      [ROTA_ITEM]: () => (negado ? problema(403, 'Acesso negado', 'Você não tem permissão para esta operação.') : json(AGUARDANDO)),
      [ROTA_MENSAGENS]: () => (negado ? problema(403, 'Acesso negado') : pagina(mensagem({ text: 'Preciso de ajuda' }))),
    });
    vi.stubGlobal('fetch', f);
    const { aoErroDeRede, aoAlterar } = renderizar({ cache, intervaloMs: 20 });
    await screen.findByText('Preciso de ajuda');
    expect(screen.getByRole('button', { name: 'Assumir' })).toBeTruthy();
    expect(cache.ler(ID)).toBeDefined();

    negado = true;

    expect(await screen.findByText(/não está mais disponível para você/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Assumir' })).toBeNull();
    expect(screen.queryByText('Preciso de ajuda')).toBeNull(); // a conversa de outro atendente não fica na tela
    expect(cache.ler(ID)).toBeUndefined(); // nem na memória
    await new Promise((resolve) => setTimeout(resolve, 120)); // vários ciclos de polling depois
    expect(aoAlterar).toHaveBeenCalledTimes(1);
    expect(aoErroDeRede).not.toHaveBeenCalledWith('conversa', expect.any(String), expect.anything());
    expect(aoErroDeRede).not.toHaveBeenCalledWith('conversa', expect.stringContaining('Acesso negado'));
  });

  it('a conversa devolvida à fila volta a aparecer para quem a tinha perdido', async () => {
    let negado = true;
    vi.stubGlobal(
      'fetch',
      criarFetch({
        [ROTA_ITEM]: () => (negado ? problema(403, 'Acesso negado') : json(AGUARDANDO)),
        [ROTA_MENSAGENS]: () => (negado ? problema(403, 'Acesso negado') : pagina(mensagem({ text: 'Voltou para a fila' }))),
      }),
    );
    renderizar({ intervaloMs: 20 });
    await screen.findByText(/não está mais disponível para você/);

    negado = false;

    expect(await screen.findByText('Voltou para a fila')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Assumir' })).toBeTruthy();
    expect(screen.queryByText(/não está mais disponível para você/)).toBeNull();
  });

  it('perdeu a corrida e o servidor já nega a leitura (o caso real): nota de que outro atendente assumiu e conversa indisponível', async () => {
    let perdeu = false;
    const f = criarFetch({
      [ROTA_ITEM]: () => (perdeu ? problema(403, 'Acesso negado') : json(AGUARDANDO)),
      [ROTA_MENSAGENS]: () => (perdeu ? problema(403, 'Acesso negado') : pagina()),
      [ROTA_ASSUMIR]: () => {
        perdeu = true;
        return problema(409, 'Conflito', 'Outro atendente já é o responsável por esta conversa.', 'QueueItemConflict');
      },
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { aoErroDeAcao } = renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Assumir' }));

    expect(await screen.findByText(/Outro atendente assumiu este atendimento antes de você/)).toBeTruthy();
    expect(await screen.findByText(/não está mais disponível para você/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Assumir' })).toBeNull();
    expect(aoErroDeAcao).not.toHaveBeenCalledWith(expect.any(String));
  });

  it('responder depois de perder a conversa (403): o texto continua no campo e a tela relê quem está com ela', async () => {
    let atual: QueueItem = COMIGO;
    const f = criarFetch({
      [ROTA_ITEM]: () => json(atual),
      [ROTA_MENSAGENS]: pagina(),
      [ROTA_RESPONDER]: () => {
        atual = COM_OUTRO; // um supervisor reatribuiu a conversa
        return problema(403, 'Acesso negado', 'Só o responsável responde.');
      },
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { aoErroDeAcao, aoAlterar } = renderizar();

    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Ainda posso responder?');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    await waitFor(() => expect(aoErroDeAcao).toHaveBeenCalledWith('Acesso negado: Só o responsável responde.', false));
    expect(await screen.findByText('Este atendimento está com outro atendente.')).toBeTruthy();
    expect(aoAlterar).toHaveBeenCalled();
  });

  it('encerrar uma conversa que já mudou (409): avisa que o atendimento mudou e não finge sucesso', async () => {
    const f = criarFetch({
      [ROTA_ITEM]: json(COMIGO),
      [ROTA_MENSAGENS]: pagina(),
      [`POST /contact-center/items/${ID}/close`]: problema(409, 'O atendimento mudou', 'O encerramento não foi aplicado.'),
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Encerrar' }));
    await usuario.click(screen.getByRole('button', { name: 'Confirmar encerramento' }));

    expect(await screen.findByText(/O atendimento mudou enquanto você agia/)).toBeTruthy();
  });
});

describe('ConversationPanel: perda de conexão', () => {
  it('sem conexão, avisa (semConexao) e mantém o que estava na tela; quando volta, o aviso é limpo e nada se repete', async () => {
    let fora = false;
    const ola = mensagem({ text: 'Olá', messageSeq: 1 });
    const base = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: () => pagina(ola) });
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(async (entrada, init) => {
        if (fora) {
          throw new TypeError('Failed to fetch');
        }
        return base(entrada, init);
      }),
    );
    const { aoErroDeRede } = renderizar({ intervaloMs: 20 });
    await screen.findByText('Olá');

    fora = true;
    await waitFor(() => expect(aoErroDeRede).toHaveBeenCalledWith('conversa', expect.stringContaining('Sem conexão com a API'), true));
    expect(screen.getByText('Olá')).toBeTruthy(); // não esvazia a conversa

    aoErroDeRede.mockClear();
    fora = false;
    await waitFor(() => expect(aoErroDeRede).toHaveBeenCalledWith('conversa', null));
    expect(screen.getAllByText('Olá')).toHaveLength(1);
  });

  it('a resposta que falhou por falta de conexão fica no campo, e repetir o mesmo texto reaproveita o requestId (nunca duplica)', async () => {
    let fora = true;
    const base = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: pagina(), [ROTA_RESPONDER]: json({ outcome: 'Completed' }) });
    const f = vi.fn<typeof fetch>(async (entrada, init) => {
      if (fora && String(entrada).endsWith('/reply')) {
        throw new TypeError('Failed to fetch');
      }
      return base(entrada, init);
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { aoErroDeAcao } = renderizar();
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;

    await usuario.type(campo, 'Resposta importante');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(aoErroDeAcao).toHaveBeenCalledWith(expect.stringContaining('Sem conexão com a API'), true));
    expect(campo.value).toBe('Resposta importante');

    fora = false;
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(campo.value).toBe(''));

    const pedidos = f.mock.calls.filter(([url]) => String(url).endsWith('/reply')).map(([, init]) => (JSON.parse(String(init?.body)) as { requestId: string }).requestId);
    expect(pedidos).toHaveLength(2);
    expect(pedidos[1]).toBe(pedidos[0]);
  });
});

describe('ConversationPanel: envio pelo WhatsApp', () => {
  const NUMERO = 'ffffffff-ffff-4fff-8fff-ffffffffffff';

  function alerta(sobrescrever: Partial<SendingAlert> = {}): SendingAlert {
    return { accountId: 'aaaa0000-0000-4000-8000-000000000001', accountName: 'WhatsApp do Laboratório', paused: true, cooldownUntil: null, channelInstanceIds: [NUMERO], ...sobrescrever };
  }

  it('número pausado: avisa ao lado do campo que a resposta fica aguardando e pode expirar', async () => {
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: pagina(mensagem({ channelInstanceId: NUMERO })) }));
    renderizar({ alertasDeEnvio: [alerta()] });

    expect(await screen.findByText(/envio por este número do WhatsApp está pausado/)).toBeTruthy();
    expect(screen.getByText(/pode expirar sem chegar ao cliente/)).toBeTruthy();
  });

  it('número esperando o provedor (sem pausa): avisa só da lentidão', async () => {
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: pagina(mensagem({ channelInstanceId: NUMERO })) }));
    renderizar({ alertasDeEnvio: [alerta({ paused: false, cooldownUntil: new Date(Date.now() + 60_000).toISOString() })] });

    expect(await screen.findByText(/envio mais lento agora/)).toBeTruthy();
    expect(screen.queryByText(/está pausado/)).toBeNull();
  });

  it('conta pausada de OUTRO número não aparece nesta conversa', async () => {
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: pagina(mensagem({ channelInstanceId: NUMERO })) }));
    renderizar({ alertasDeEnvio: [alerta({ channelInstanceIds: ['99999999-9999-4999-8999-999999999999'] })] });

    await screen.findByLabelText('Resposta ao cliente');
    expect(screen.queryByText(/envio por este número/)).toBeNull();
  });

  it('mensagem com falha definitiva de envio aparece como falha, e o aviso da conversa orienta a responder de novo', async () => {
    const falhou = mensagem({ direction: 'Outbound', author: 'Human', text: 'Resposta que não saiu', messageSeq: 1, deliveryState: 'Failed' });
    vi.stubGlobal(
      'fetch',
      criarFetch({ [ROTA_ITEM]: json({ ...COMIGO, deliveryFailures: 1, lastDeliveryFailureAt: new Date().toISOString() }), [ROTA_MENSAGENS]: pagina(falhou) }),
    );
    renderizar();

    expect(await screen.findByText('falha no envio')).toBeTruthy();
    expect(await screen.findByText(/Uma mensagem não foi enviada ao cliente/)).toBeTruthy();
  });
});
