import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { Message, QueueItem } from '../api/types';
import { GUID, OUTRO_USUARIO_ID, USUARIO, chamadasDe, criarFetch, item, json, mensagem, problema } from '../test/helpers';
import { ConversationPanel } from './ConversationPanel';

const ID = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const ROTA_ITEM = `GET /contact-center/items/${ID}`;
const ROTA_MENSAGENS = `GET /contact-center/items/${ID}/messages`;
const ROTA_RESPONDER = `POST /contact-center/items/${ID}/reply`;

const AGUARDANDO = item({ status: 'Waiting' });
const COMIGO = item({ status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });
const COM_OUTRO = item({ status: 'Assigned', assignedUserId: OUTRO_USUARIO_ID });

function pagina(...itens: Message[]) {
  // A API devolve do mais novo para o mais antigo.
  return json({ items: [...itens].sort((a, b) => b.messageSeq - a.messageSeq), nextCursor: null });
}

/**
 * A API simulada guarda o estado do item: uma ação que dá certo muda o que o próximo GET devolve, como no backend.
 * `acoes` recebe `definir` para as rotas de ação trocarem o estado.
 */
function cenario(
  inicial: QueueItem,
  mensagens: Message[] = [],
  acoes: (definir: (novo: QueueItem) => void) => Parameters<typeof criarFetch>[0] = () => ({}),
) {
  let atual = inicial;
  return criarFetch({
    [ROTA_ITEM]: () => json(atual),
    [ROTA_MENSAGENS]: pagina(...mensagens),
    ...acoes((novo) => {
      atual = novo;
    }),
  });
}

/** Rota de ação que dá certo: troca o estado do item e responde 200 com o novo item. */
function aplica(definir: (novo: QueueItem) => void, novo: QueueItem) {
  return () => {
    definir(novo);
    return json({ item: novo, outcome: 'Completed' });
  };
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

describe('ConversationPanel: histórico', () => {
  it('mostra as mensagens do cliente, da IA e do atendente, em ordem cronológica', async () => {
    const f = cenario(COMIGO, [
      mensagem({ author: 'Customer', direction: 'Inbound', text: 'Preciso de jejum para o exame A?', messageSeq: 1 }),
      mensagem({ author: 'Ai', direction: 'Outbound', text: 'Vou chamar um atendente.', deliveryState: 'Delivered', messageSeq: 2 }),
      mensagem({ author: 'Human', direction: 'Outbound', text: 'Oi, aqui é a Ana.', deliveryState: 'Sent', messageSeq: 3 }),
    ]);
    vi.stubGlobal('fetch', f);
    renderizar();

    const log = await screen.findByRole('log', { name: 'Mensagens da conversa' });
    const linhas = await within(log).findAllByRole('listitem');
    expect(linhas.map((l) => l.getAttribute('data-autor'))).toEqual(['Customer', 'Ai', 'Human']);
    expect(linhas[0]?.textContent).toContain('Mauro');
    expect(linhas[0]?.textContent).toContain('Preciso de jejum para o exame A?');
    expect(linhas[1]?.textContent).toContain('IA');
    expect(linhas[1]?.textContent).toContain('Vou chamar um atendente.');
    expect(linhas[2]?.textContent).toContain('Atendente');
    expect(linhas[2]?.textContent).toContain('enviado');
    expect(linhas[0]?.getAttribute('data-direcao')).toBe('Inbound');
    expect(linhas[2]?.getAttribute('data-direcao')).toBe('Outbound');

    const consulta = chamadasDe(f, ROTA_MENSAGENS)[0]?.consulta;
    expect(Object.fromEntries(consulta ?? [])).toEqual({ take: '100', order: 'NewestFirst' });
    expect(log.getAttribute('aria-live')).toBe('polite');
  });

  it('avisa o atendente quando mensagens não foram enviadas ao cliente e orienta a responder de novo', async () => {
    const umaFalha = item({ status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString(), deliveryFailures: 1, lastDeliveryFailureAt: new Date().toISOString() });
    vi.stubGlobal('fetch', cenario(umaFalha));
    const { unmount } = renderizar();

    const aviso = await screen.findByRole('alert');
    expect(aviso.textContent).toContain('Uma mensagem não foi enviada ao cliente.');
    expect(aviso.textContent).toContain('Responda de novo');
    unmount();

    vi.stubGlobal('fetch', cenario({ ...umaFalha, deliveryFailures: 3 }));
    renderizar();
    expect((await screen.findByRole('alert')).textContent).toContain('3 mensagens não foram enviadas ao cliente.');
  });

  it('sem falha de envio não mostra aviso algum', async () => {
    vi.stubGlobal('fetch', cenario(COMIGO));
    renderizar();

    await screen.findByRole('heading', { name: 'Mauro' });
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('SendUnknown aparece como "envio de resultado incerto": nem falha, nem entregue', async () => {
    vi.stubGlobal(
      'fetch',
      cenario(COMIGO, [
        mensagem({ author: 'Human', direction: 'Outbound', text: 'Resposta incerta', deliveryState: 'SendUnknown', messageSeq: 1 }),
        mensagem({ author: 'Human', direction: 'Outbound', text: 'Resposta que falhou', deliveryState: 'Failed', messageSeq: 2 }),
        mensagem({ author: 'Ai', direction: 'Outbound', text: 'Resposta entregue', deliveryState: 'Delivered', messageSeq: 3 }),
      ]),
    );
    renderizar();

    const incerta = (await screen.findByText('Resposta incerta')).closest('li');
    expect(incerta?.textContent).toContain('envio de resultado incerto');
    expect(incerta?.textContent).not.toMatch(/falha|entregue|enviado/);
    expect(incerta?.querySelector('[data-entrega="SendUnknown"]')?.className).toContain('entrega-incerta');

    const falha = screen.getByText('Resposta que falhou').closest('li');
    expect(falha?.textContent).toContain('falha no envio');
    expect(falha?.textContent).not.toContain('incerto');
    expect(screen.getByText('Resposta entregue').closest('li')?.textContent).toContain('entregue');
  });

  it('mensagem sem texto vira "mídia recebida (arquivo não disponível)"', async () => {
    vi.stubGlobal('fetch', cenario(COMIGO, [mensagem({ kind: 'Image', text: null, messageSeq: 1 })]));
    renderizar();

    expect(await screen.findByText('mídia recebida (arquivo não disponível)')).toBeTruthy();
  });

  it('não mostra estado de entrega nas mensagens do cliente', async () => {
    vi.stubGlobal('fetch', cenario(COMIGO, [mensagem({ author: 'Customer', direction: 'Inbound', text: 'oi', deliveryState: null, messageSeq: 1 })]));
    renderizar();

    const linha = (await screen.findByText('oi')).closest('li');
    expect(linha?.querySelector('.entrega')).toBeNull();
  });

  it('atendimento inexistente ou fora do alcance (404) mostra aviso, sem erro na faixa', async () => {
    vi.stubGlobal('fetch', criarFetch({}));
    const { aoErroDeRede } = renderizar();

    expect(await screen.findByText(/não está mais disponível/)).toBeTruthy();
    expect(aoErroDeRede).not.toHaveBeenCalledWith('conversa', expect.any(String));
  });

  it('atualiza sozinha (polling) e mostra a mensagem que chegou depois', async () => {
    let mensagens: Message[] = [mensagem({ text: 'primeira', messageSeq: 1 })];
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: () => pagina(...mensagens) }));
    renderizar({ intervaloMs: 20 });
    await screen.findByText('primeira');

    mensagens = [...mensagens, mensagem({ text: 'segunda', messageSeq: 2 })];

    expect(await screen.findByText('segunda')).toBeTruthy();
  });
});

describe('ConversationPanel: o que o atendente pode fazer', () => {
  it('aguardando: só "Assumir", sem campo de resposta', async () => {
    vi.stubGlobal('fetch', cenario(AGUARDANDO));
    renderizar();

    expect(await screen.findByRole('button', { name: 'Assumir' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Responder' })).toBeNull();
    expect(screen.queryByLabelText('Resposta ao cliente')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Devolver à fila' })).toBeNull();
  });

  it('assumido por outro atendente: nada de ações nem de campo de resposta', async () => {
    vi.stubGlobal('fetch', cenario(COM_OUTRO));
    renderizar();

    expect(await screen.findByText('Este atendimento está com outro atendente.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Assumir' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Responder' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Encerrar' })).toBeNull();
  });

  it('assumido por mim: campo de resposta e as ações Devolver à fila, Devolver à IA e Encerrar', async () => {
    vi.stubGlobal('fetch', cenario(COMIGO));
    renderizar();

    expect(await screen.findByRole('button', { name: 'Responder' })).toBeTruthy();
    expect(screen.getByLabelText('Resposta ao cliente')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Devolver à fila' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Devolver à IA' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Encerrar' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Assumir' })).toBeNull();
  });

  it('Assumir envia claim com um requestId GUID e libera o campo de resposta', async () => {
    const f = cenario(AGUARDANDO, [], (definir) => ({ [`POST /contact-center/items/${ID}/claim`]: aplica(definir, COMIGO) }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { aoAlterar } = renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Assumir' }));

    expect(await screen.findByLabelText('Resposta ao cliente')).toBeTruthy();
    expect(chamadasDe(f, `POST /contact-center/items/${ID}/claim`)[0]?.corpo).toEqual({ requestId: expect.stringMatching(GUID) });
    expect(aoAlterar).toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Assumir' })).toBeNull();
  });

  it('conflito ao assumir: explica que outro atendente assumiu (sem faixa de erro) e relê o estado', async () => {
    const f = cenario(AGUARDANDO, [], () => ({
      [`POST /contact-center/items/${ID}/claim`]: problema(409, 'Conflito', 'Outro atendente assumiu a conversa.', 'AlreadyAssigned'),
    }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { aoErroDeAcao } = renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Assumir' }));

    expect(await screen.findByText(/Outro atendente assumiu este atendimento antes de você/)).toBeTruthy();
    await waitFor(() => expect(chamadasDe(f, ROTA_ITEM).length).toBeGreaterThanOrEqual(2));
    expect(aoErroDeAcao).not.toHaveBeenCalledWith(expect.any(String)); // perder a corrida não é erro: a nota basta
  });

  it('claim ainda não confirmado (202, Pending): avisa para repetir e mantém o botão', async () => {
    vi.stubGlobal(
      'fetch',
      cenario(AGUARDANDO, [], () => ({ [`POST /contact-center/items/${ID}/claim`]: json({ item: AGUARDANDO, outcome: 'Pending' }, 202) })),
    );
    const usuario = userEvent.setup();
    renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Assumir' }));

    expect((await screen.findByText(/ainda não a confirmou/)).textContent).toContain('Repita a ação');
    expect(screen.getByRole('button', { name: 'Assumir' })).toBeTruthy();
  });

  it('Devolver à fila envia release com requestId e o item volta a aguardar', async () => {
    const f = cenario(COMIGO, [], (definir) => ({ [`POST /contact-center/items/${ID}/release`]: aplica(definir, AGUARDANDO) }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Devolver à fila' }));

    await waitFor(() => expect(chamadasDe(f, `POST /contact-center/items/${ID}/release`)).toHaveLength(1));
    expect(chamadasDe(f, `POST /contact-center/items/${ID}/release`)[0]?.corpo).toEqual({ requestId: expect.stringMatching(GUID) });
    // o item voltou a "aguardando": o botão de assumir aparece e o campo de resposta some
    expect(await screen.findByRole('button', { name: 'Assumir' })).toBeTruthy();
    expect(screen.queryByLabelText('Resposta ao cliente')).toBeNull();
  });

  it('Devolver à IA envia return-to-ai com requestId', async () => {
    const f = cenario(COMIGO, [], (definir) => ({
      [`POST /contact-center/items/${ID}/return-to-ai`]: aplica(definir, { ...COMIGO, status: 'Closed' }),
    }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Devolver à IA' }));

    await waitFor(() => expect(chamadasDe(f, `POST /contact-center/items/${ID}/return-to-ai`)).toHaveLength(1));
    expect(chamadasDe(f, `POST /contact-center/items/${ID}/return-to-ai`)[0]?.corpo).toEqual({ requestId: expect.stringMatching(GUID) });
    expect(await screen.findByText('Este atendimento foi encerrado.')).toBeTruthy();
  });

  it('Encerrar pede confirmação e envia close com reasonCode "resolved"', async () => {
    const f = cenario(COMIGO, [], (definir) => ({
      [`POST /contact-center/items/${ID}/close`]: aplica(definir, { ...COMIGO, status: 'Closed' }),
    }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Encerrar' }));
    expect(chamadasDe(f, `POST /contact-center/items/${ID}/close`)).toHaveLength(0);

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }));
    expect(screen.getByRole('button', { name: 'Encerrar' })).toBeTruthy();

    await usuario.click(screen.getByRole('button', { name: 'Encerrar' }));
    await usuario.click(screen.getByRole('button', { name: 'Confirmar encerramento' }));

    await waitFor(() => expect(chamadasDe(f, `POST /contact-center/items/${ID}/close`)).toHaveLength(1));
    expect(chamadasDe(f, `POST /contact-center/items/${ID}/close`)[0]?.corpo).toEqual({
      requestId: expect.stringMatching(GUID),
      reasonCode: 'resolved',
    });
    expect(await screen.findByText('Este atendimento foi encerrado.')).toBeTruthy();
    expect(screen.queryByLabelText('Resposta ao cliente')).toBeNull();
  });
});

describe('ConversationPanel: responder', () => {
  it('envia { requestId, text } e só limpa o campo depois do sucesso', async () => {
    let liberar: (r: Response) => void = () => undefined;
    const resposta = new Promise<Response>((resolve) => {
      liberar = resolve;
    });
    const f = cenario(COMIGO, [], () => ({ [ROTA_RESPONDER]: () => resposta }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;

    await usuario.type(campo, 'Bom dia, Mauro!');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    // em andamento: o texto continua no campo e o botão fica desabilitado
    await waitFor(() => expect((screen.getByRole('button', { name: 'Enviando...' }) as HTMLButtonElement).disabled).toBe(true));
    expect(campo.value).toBe('Bom dia, Mauro!');

    liberar(json({}));

    await waitFor(() => expect(campo.value).toBe(''));
    expect(chamadasDe(f, ROTA_RESPONDER)[0]?.corpo).toEqual({ requestId: expect.stringMatching(GUID), text: 'Bom dia, Mauro!' });
  });

  it('duplo clique em Responder envia uma única vez', async () => {
    let liberar: (r: Response) => void = () => undefined;
    const resposta = new Promise<Response>((resolve) => {
      liberar = resolve;
    });
    const f = cenario(COMIGO, [], () => ({ [ROTA_RESPONDER]: () => resposta }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Uma vez só');

    await usuario.dblClick(screen.getByRole('button', { name: 'Responder' }));
    liberar(json({}));

    await waitFor(() => expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).value).toBe(''));
    expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(1);
  });

  it('Enter envia e Shift+Enter quebra a linha', async () => {
    const f = cenario(COMIGO, [], () => ({ [ROTA_RESPONDER]: json({}) }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    const campo = await screen.findByLabelText('Resposta ao cliente');

    await usuario.type(campo, 'linha 1{Shift>}{Enter}{/Shift}linha 2');
    expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(0);
    await usuario.type(campo, '{Enter}');

    await waitFor(() => expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(1));
    expect(chamadasDe(f, ROTA_RESPONDER)[0]?.corpo?.text).toBe('linha 1\nlinha 2');
  });

  it('o botão fica desabilitado com o campo vazio ou só com espaços', async () => {
    vi.stubGlobal('fetch', cenario(COMIGO));
    const usuario = userEvent.setup();
    renderizar();
    const botao = (await screen.findByRole('button', { name: 'Responder' })) as HTMLButtonElement;
    expect(botao.disabled).toBe(true);

    await usuario.type(screen.getByLabelText('Resposta ao cliente'), '   ');
    expect(botao.disabled).toBe(true);
  });

  it('falha de API: mantém o texto, mostra título e detalhe e permite tentar de novo', async () => {
    const f = cenario(COMIGO, [], () => ({
      [ROTA_RESPONDER]: problema(422, 'Regra não atendida', 'A janela de atendimento terminou.', 'WindowClosed'),
    }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { aoErroDeAcao } = renderizar();
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;

    await usuario.type(campo, 'Olá');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    await waitFor(() => expect(aoErroDeAcao).toHaveBeenCalledWith('Regra não atendida: A janela de atendimento terminou.', false));
    expect(campo.value).toBe('Olá');
    expect((screen.getByRole('button', { name: 'Responder' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('repetir o MESMO texto depois de falha de rede reaproveita o requestId', async () => {
    let tentativa = 0;
    const f = cenario(COMIGO, [], () => ({ [ROTA_RESPONDER]: () => (++tentativa === 1 ? new Response(null, { status: 503 }) : json({})) }));
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;

    await usuario.type(campo, 'Mesma resposta');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(1));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Responder' }) as HTMLButtonElement).disabled).toBe(false));
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    await waitFor(() => expect(campo.value).toBe(''));
    const [a, b] = chamadasDe(f, ROTA_RESPONDER).map((c) => c.corpo?.requestId);
    expect(a).toMatch(GUID);
    expect(b).toBe(a);
  });
});
