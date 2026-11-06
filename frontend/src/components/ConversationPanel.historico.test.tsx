import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { Message, QueueItem } from '../api/types';
import { ArmazemLocal } from '../armazem';
import { CacheDeConversas } from '../cache';
import { GUID, OUTRO_USUARIO_ID, USUARIO, chamadasDe, criarFetch, item, json, mensagem, problema, type Requisicao } from '../test/helpers';
import { ConversationPanel } from './ConversationPanel';

const ID = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const ROTA_ITEM = `GET /contact-center/items/${ID}`;
const ROTA_MENSAGENS = `GET /contact-center/items/${ID}/messages`;
const ROTA_RESPONDER = `POST /contact-center/items/${ID}/reply`;
const ROTA_ENCERRAR = `POST /contact-center/items/${ID}/close`;

const COMIGO = item({ status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });
const ENCERRADO_POR_OUTRO = item({ status: 'Closed', assignedUserId: OUTRO_USUARIO_ID, closedAt: new Date().toISOString(), closeReason: 'resolved' });

const ANA = { tenant: 'demo-a', usuarioId: USUARIO.id };
const OUTRO_OPERADOR = { tenant: 'demo-a', usuarioId: OUTRO_USUARIO_ID };

/** Uma conversa de `total` mensagens, as mesmas (mesmo id) em todas as leituras. */
function conversaLonga(total: number): Message[] {
  return Array.from({ length: total }, (_, i) => mensagem({ messageSeq: i + 1, text: `m${i + 1}` }));
}

/** A API de mensagens com cursor de verdade: do mais novo para o mais antigo, `c<N>` = "só até a sequência N". */
function servidorDeHistorico(todas: () => Message[]) {
  return ({ consulta }: Requisicao) => {
    const take = Number(consulta.get('take'));
    const cursor = consulta.get('cursor');
    const limite = cursor ? Number(cursor.slice(1)) : Number.MAX_SAFE_INTEGER;
    const ordenadas = todas()
      .filter((m) => m.messageSeq <= limite)
      .sort((a, b) => b.messageSeq - a.messageSeq);
    const pagina = ordenadas.slice(0, take);
    const menor = pagina[pagina.length - 1]?.messageSeq ?? 1;
    return json({ items: pagina, nextCursor: ordenadas.length > take ? `c${menor - 1}` : null });
  };
}

// Mensagens fixas (mesmos ids em toda leitura), para os testes que releem a conversa.
const TRES = conversaLonga(3);
const QUATRO = conversaLonga(4);

function cursoresPedidos(f: Parameters<typeof chamadasDe>[0]): Array<string | null> {
  return chamadasDe(f, ROTA_MENSAGENS).map((c) => c.consulta.get('cursor'));
}

function chavesGuardadas(): string[] {
  return Object.keys(localStorage).filter((k) => k.startsWith('ia-bimeta.v1:'));
}

/** Só os rascunhos (e pedidos pendentes): o 'visto até' é outro registro e fica enquanto a conversa existir. */
function rascunhosGuardados(): string[] {
  return chavesGuardadas().filter((k) => k.includes(':r:'));
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

describe('ConversationPanel: histórico longo em páginas', () => {
  it('250 mensagens: abre nas 100 mais recentes e cada clique traz 50 mais antigas, pelo cursor, até acabar, sem repetir nenhuma', async () => {
    const todas = conversaLonga(250);
    const f = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => todas) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();

    await screen.findByText('m250');
    expect(screen.getAllByRole('listitem')).toHaveLength(100);
    expect(screen.queryByText('m150')).toBeNull();

    await usuario.click(await screen.findByRole('button', { name: 'Carregar mensagens anteriores' }));
    await screen.findByText('m101');
    expect(screen.getAllByRole('listitem')).toHaveLength(150);

    await usuario.click(await screen.findByRole('button', { name: 'Carregar mensagens anteriores' }));
    await screen.findByText('m51');
    await usuario.click(await screen.findByRole('button', { name: 'Carregar mensagens anteriores' }));
    await screen.findByText('m1');

    expect(screen.getAllByRole('listitem')).toHaveLength(250);
    expect(screen.queryByRole('button', { name: /anteriores/ })).toBeNull(); // a última página zerou o cursor
    expect(cursoresPedidos(f)).toEqual([null, 'c150', 'c100', 'c50']);
    expect(chamadasDe(f, ROTA_MENSAGENS).map((c) => c.consulta.get('take'))).toEqual(['100', '50', '50', '50']);
    expect(chamadasDe(f, ROTA_MENSAGENS).every((c) => c.consulta.get('order') === 'NewestFirst')).toBe(true);
  });

  it('a releitura periódica depois de carregar páginas antigas não perde o histórico, não duplica e não volta o cursor', async () => {
    let todas = conversaLonga(250);
    const f = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => todas) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar({ intervaloMs: 25 });
    await usuario.click(await screen.findByRole('button', { name: 'Carregar mensagens anteriores' }));
    await screen.findByText('m101');

    todas = [...todas, mensagem({ messageSeq: 251, text: 'm251' })];
    await screen.findByText('m251');
    await new Promise((resolve) => setTimeout(resolve, 120)); // mais ciclos

    expect(screen.getAllByRole('listitem')).toHaveLength(151);
    expect(screen.getAllByText('m101')).toHaveLength(1);
    await usuario.click(screen.getByRole('button', { name: 'Carregar mensagens anteriores' }));
    await screen.findByText('m51');
    expect(cursoresPedidos(f).filter((c) => c !== null)).toEqual(['c150', 'c100']); // o segundo clique continuou de onde o primeiro parou
  });

  it('dois cliques seguidos pedem a página uma vez só', async () => {
    const todas = conversaLonga(250);
    let liberar: (r: Response) => void = () => undefined;
    const atrasada = new Promise<Response>((resolve) => {
      liberar = resolve;
    });
    const base = servidorDeHistorico(() => todas);
    const f = criarFetch({
      [ROTA_ITEM]: json(COMIGO),
      [ROTA_MENSAGENS]: (req) => (req.consulta.get('cursor') ? atrasada : base(req)),
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    const botao = await screen.findByRole('button', { name: 'Carregar mensagens anteriores' });

    await usuario.dblClick(botao);

    expect(await screen.findByText('Carregando mensagens anteriores...')).toBeTruthy();
    expect(cursoresPedidos(f).filter((c) => c !== null)).toEqual(['c150']);
    liberar(base({ metodo: 'GET', caminho: '', consulta: new URLSearchParams({ take: '50', cursor: 'c150' }), corpo: null, cabecalhos: new Headers() }) as Response);
    await screen.findByText('m101');
  });

  it('erro HTTP ao carregar: mostra o motivo, mantém o que já está na tela e "tentar de novo" funciona', async () => {
    const todas = conversaLonga(250);
    let falhar = true;
    const base = servidorDeHistorico(() => todas);
    vi.stubGlobal(
      'fetch',
      criarFetch({
        [ROTA_ITEM]: json(COMIGO),
        [ROTA_MENSAGENS]: (req) => (req.consulta.get('cursor') && falhar ? problema(500, 'Erro interno', 'Falha ao ler.') : base(req)),
      }),
    );
    const usuario = userEvent.setup();
    renderizar();
    await usuario.click(await screen.findByRole('button', { name: 'Carregar mensagens anteriores' }));

    expect(await screen.findByText(/Não foi possível carregar as mensagens anteriores/)).toBeTruthy();
    expect(screen.getAllByRole('listitem')).toHaveLength(100);

    falhar = false;
    await usuario.click(screen.getByRole('button', { name: 'Tentar carregar de novo' }));

    await screen.findByText('m101');
    expect(screen.queryByText(/Não foi possível carregar/)).toBeNull();
  });

  it('sem conexão ao carregar: diz que é a conexão, sem apagar nada', async () => {
    const todas = conversaLonga(250);
    let fora = false;
    const base = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => todas) });
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(async (entrada, init) => {
        if (fora && String(entrada).includes('cursor=')) {
          throw new TypeError('Failed to fetch');
        }
        return base(entrada, init);
      }),
    );
    const usuario = userEvent.setup();
    renderizar();
    const botao = await screen.findByRole('button', { name: 'Carregar mensagens anteriores' });

    fora = true;
    await usuario.click(botao);

    expect(await screen.findByText('Sem conexão: não foi possível carregar as mensagens anteriores.')).toBeTruthy();
    expect(screen.getAllByRole('listitem')).toHaveLength(100);
  });

  it('o histórico carregado e o cursor ficam na memória: sair da conversa e voltar mostra as 150 e continua de onde parou', async () => {
    const todas = conversaLonga(250);
    const cache = new CacheDeConversas();
    const f = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => todas) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const primeira = renderizar({ cache });
    await usuario.click(await screen.findByRole('button', { name: 'Carregar mensagens anteriores' }));
    await screen.findByText('m101');
    primeira.unmount();

    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => new Promise<Response>(() => undefined))); // a API nem responde
    renderizar({ cache });

    expect(screen.getAllByRole('listitem')).toHaveLength(150);
    expect(screen.getByRole('button', { name: 'Carregar mensagens anteriores' })).toBeTruthy();
  });

  it('o pedido que termina depois de o atendente trocar de conversa grava o histórico na memória', async () => {
    const todas = conversaLonga(250);
    const cache = new CacheDeConversas();
    let liberar: (r: Response) => void = () => undefined;
    const atrasada = new Promise<Response>((resolve) => {
      liberar = resolve;
    });
    const base = servidorDeHistorico(() => todas);
    vi.stubGlobal(
      'fetch',
      criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: (req) => (req.consulta.get('cursor') ? atrasada : base(req)) }),
    );
    const usuario = userEvent.setup();
    const { unmount } = renderizar({ cache });
    await usuario.click(await screen.findByRole('button', { name: 'Carregar mensagens anteriores' }));
    unmount();

    liberar(json({ items: todas.filter((m) => m.messageSeq > 100 && m.messageSeq <= 150).reverse(), nextCursor: 'c100' }));

    await waitFor(() => expect(cache.ler(ID)?.mensagens).toHaveLength(150));
    expect(cache.ler(ID)?.cursorAnterior).toBe('c100');
  });
});

describe('ConversationPanel: mensagens novas', () => {
  it('abre com o marcador "Novas mensagens" antes da primeira que o atendente não tinha visto, e passa a marcar como visto', async () => {
    const cache = new CacheDeConversas();
    cache.gravarVisto(ID, 2);
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => QUATRO) }));
    renderizar({ cache });

    const marcador = await screen.findByRole('separator');

    expect(marcador.nextElementSibling?.textContent).toContain('m3');
    await waitFor(() => expect(cache.lerVisto(ID)).toBe(4));
  });
});

describe('ConversationPanel: rascunhos persistentes', () => {
  it('o texto digitado volta depois de recarregar a página (e de fechar e abrir o navegador)', async () => {
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => []) }));
    const usuario = userEvent.setup();
    const primeira = renderizar({ cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }) });
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Resposta ainda não enviada');
    primeira.unmount(); // a página foi recarregada: a memória da aba se foi, o armazenamento local não

    renderizar({ cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }) });

    expect(((await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement).value).toBe('Resposta ainda não enviada');
  });

  it('outro operador no mesmo navegador não vê o rascunho', async () => {
    new ArmazemLocal(ANA).gravarRascunho(ID, 'Texto da Ana');
    vi.stubGlobal(
      'fetch',
      criarFetch({
        [ROTA_ITEM]: json(item({ status: 'Assigned', assignedUserId: OUTRO_USUARIO_ID })),
        [ROTA_MENSAGENS]: servidorDeHistorico(() => []),
      }),
    );
    renderizar({ me: { ...USUARIO, id: OUTRO_USUARIO_ID }, cache: new CacheDeConversas({ armazem: new ArmazemLocal(OUTRO_OPERADOR) }) });

    expect(((await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement).value).toBe('');
  });

  it('o rascunho é gravado a cada tecla, com escopo de tenant e usuário, e some do armazenamento quando a resposta é enviada', async () => {
    const f = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => []), [ROTA_RESPONDER]: json({ outcome: 'Completed' }) });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar({ cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }) });
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;

    await usuario.type(campo, 'Oi');

    expect(chavesGuardadas()).toHaveLength(1);
    expect(chavesGuardadas()[0]).toContain(USUARIO.id);
    expect(chavesGuardadas()[0]).toContain('demo-a');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(campo.value).toBe(''));
    expect(chavesGuardadas()).toHaveLength(0);
  });

  it('trocar de conversa no meio do envio: ao concluir, o armazenamento também é limpo', async () => {
    let liberar: (r: Response) => void = () => undefined;
    const resposta = new Promise<Response>((resolve) => {
      liberar = resolve;
    });
    const f = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => []), [ROTA_RESPONDER]: () => resposta });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const { unmount } = renderizar({ cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }) });
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Texto em envio');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(1));
    unmount();

    liberar(json({ outcome: 'Completed' }));

    await waitFor(() => expect(chavesGuardadas()).toHaveLength(0));
  });

  it('encerrar a conversa descarta o rascunho (e a confirmação avisa disso)', async () => {
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => []), [ROTA_ENCERRAR]: json({ outcome: 'Completed' }) }));
    const usuario = userEvent.setup();
    renderizar({ cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }) });
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Não vou enviar');

    await usuario.click(screen.getByRole('button', { name: 'Encerrar' }));
    expect(screen.getByText('Encerrar este atendimento? O texto não enviado será descartado.')).toBeTruthy();
    await usuario.click(screen.getByRole('button', { name: 'Confirmar encerramento' }));

    await waitFor(() => expect(chavesGuardadas()).toHaveLength(0));
  });

  it('encerrar sem texto digitado não fala em descarte', async () => {
    vi.stubGlobal('fetch', criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => []) }));
    const usuario = userEvent.setup();
    renderizar();

    await usuario.click(await screen.findByRole('button', { name: 'Encerrar' }));

    expect(screen.getByText('Encerrar este atendimento?')).toBeTruthy();
  });
});

describe('ConversationPanel: o texto digitado não some nos erros', () => {
  it('falha de conexão no envio e página recarregada: o texto está lá e o reenvio usa o MESMO requestId (nunca duplica)', async () => {
    let fora = true;
    const base = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => []), [ROTA_RESPONDER]: json({ outcome: 'Completed' }) });
    const f = vi.fn<typeof fetch>(async (entrada, init) => {
      if (fora && String(entrada).endsWith('/reply')) {
        throw new TypeError('Failed to fetch');
      }
      return base(entrada, init);
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const primeira = renderizar({ cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }) });
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Resposta importante');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(f.mock.calls.filter(([u]) => String(u).endsWith('/reply'))).toHaveLength(1));
    primeira.unmount(); // a página é recarregada com o servidor ainda fora (a primeira tentativa pode até ter chegado lá)

    fora = false;
    renderizar({ api: createApi(), cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }) }); // API e memória novas, como numa página nova
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;
    expect(campo.value).toBe('Resposta importante');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(campo.value).toBe(''));

    const pedidos = f.mock.calls.filter(([u]) => String(u).endsWith('/reply')).map(([, init]) => (JSON.parse(String(init?.body)) as { requestId: string }).requestId);
    expect(pedidos).toHaveLength(2);
    expect(pedidos[0]).toMatch(GUID);
    expect(pedidos[1]).toBe(pedidos[0]);
    expect(chavesGuardadas()).toHaveLength(0); // concluído: nada de rascunho nem de pendência
  });

  it('texto editado depois da falha é OUTRO pedido: usa outro requestId', async () => {
    const f = criarFetch({
      [ROTA_ITEM]: json(COMIGO),
      [ROTA_MENSAGENS]: servidorDeHistorico(() => []),
      [ROTA_RESPONDER]: new Response(null, { status: 503 }),
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar({ cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }) });
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;
    await usuario.type(campo, 'Primeira versão');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(1));
    await waitFor(() => expect((screen.getByRole('button', { name: 'Responder' }) as HTMLButtonElement).disabled).toBe(false));

    await usuario.type(campo, ' e segunda');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    await waitFor(() => expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(2));
    const [a, b] = chamadasDe(f, ROTA_RESPONDER).map((c) => c.corpo?.requestId);
    expect(a).not.toBe(b);
    expect(campo.value).toBe('Primeira versão e segunda'); // a falha não apagou nada
  });

  it('resposta rejeitada pelo servidor (400): o texto continua no campo e guardado, e a pendência sai (o próximo envio é outro pedido)', async () => {
    const f = criarFetch({
      [ROTA_ITEM]: json(COMIGO),
      [ROTA_MENSAGENS]: servidorDeHistorico(() => []),
      [ROTA_RESPONDER]: problema(400, 'Resposta recusada', 'O conteúdo da resposta não é aceito.'),
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    const cache = new CacheDeConversas({ armazem: new ArmazemLocal(ANA) });
    renderizar({ cache });
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;

    await usuario.type(campo, 'Texto recusado');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    await waitFor(() => expect(chamadasDe(f, ROTA_RESPONDER)).toHaveLength(1));
    await waitFor(() => expect(cache.lerPendente(ID)).toBeUndefined());
    expect(campo.value).toBe('Texto recusado');
    expect(cache.lerRascunho(ID)).toBe('Texto recusado');
  });

  it('o colega encerrou a conversa enquanto o atendente digitava: o campo some, mas o texto continua visível, com copiar e descartar', async () => {
    const cache = new CacheDeConversas({ armazem: new ArmazemLocal(ANA) });
    let encerrada = false;
    vi.stubGlobal(
      'fetch',
      criarFetch({ [ROTA_ITEM]: () => json(encerrada ? ENCERRADO_POR_OUTRO : COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => TRES) }),
    );
    const usuario = userEvent.setup();
    renderizar({ cache, intervaloMs: 25 });
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Estava respondendo...');

    encerrada = true;

    expect(await screen.findByText('Este atendimento foi encerrado.')).toBeTruthy();
    expect(screen.queryByLabelText('Resposta ao cliente')).toBeNull();
    expect((screen.getByLabelText('Texto guardado') as HTMLTextAreaElement).value).toBe('Estava respondendo...');
    expect(screen.getByRole('button', { name: 'Copiar texto' })).toBeTruthy();
    expect(cache.lerRascunho(ID)).toBe('Estava respondendo...'); // continua guardado

    await usuario.click(screen.getByRole('button', { name: 'Descartar texto' }));
    expect(screen.queryByLabelText('Texto guardado')).toBeNull();
    expect(rascunhosGuardados()).toHaveLength(0);
  });

  it('outro atendente assumiu (403): a conversa some, o texto digitado NÃO, e volta ao campo se a conversa voltar a ser dele', async () => {
    const cache = new CacheDeConversas({ armazem: new ArmazemLocal(ANA) });
    let estado: 'minha' | 'negada' = 'minha';
    vi.stubGlobal(
      'fetch',
      criarFetch({
        [ROTA_ITEM]: () => (estado === 'minha' ? json(COMIGO) : problema(403, 'Acesso negado')),
        [ROTA_MENSAGENS]: () => (estado === 'minha' ? servidorDeHistorico(() => TRES)({ consulta: new URLSearchParams({ take: '100' }) } as Requisicao) : problema(403, 'Acesso negado')),
      }),
    );
    const usuario = userEvent.setup();
    renderizar({ cache, intervaloMs: 25 });
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Texto que não pode se perder');

    estado = 'negada';

    expect(await screen.findByText(/não está mais disponível para você/)).toBeTruthy();
    expect((screen.getByLabelText('Texto guardado') as HTMLTextAreaElement).value).toBe('Texto que não pode se perder');
    expect(cache.lerRascunho(ID)).toBe('Texto que não pode se perder');

    estado = 'minha';

    expect(((await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement).value).toBe('Texto que não pode se perder');
    expect(screen.queryByLabelText('Texto guardado')).toBeNull();
  });

  it('queda de conexão: o texto, o campo e o histórico continuam, e nada é enviado em duplicidade na volta', async () => {
    let fora = false;
    const todas = conversaLonga(3);
    const base = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: servidorDeHistorico(() => todas) });
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
    renderizar({ cache: new CacheDeConversas({ armazem: new ArmazemLocal(ANA) }), intervaloMs: 25 });
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;
    await usuario.type(campo, 'Digitando durante a queda');

    fora = true;
    await new Promise((resolve) => setTimeout(resolve, 120));
    fora = false;
    await new Promise((resolve) => setTimeout(resolve, 120));

    expect(campo.value).toBe('Digitando durante a queda');
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
  });

  it('conversa de outro operador (item de outro usuário) nunca mostra rascunho nem campo', async () => {
    new ArmazemLocal(ANA).gravarRascunho(ID, 'Era da Ana');
    vi.stubGlobal(
      'fetch',
      criarFetch({ [ROTA_ITEM]: json(item({ status: 'Assigned', assignedUserId: '33333333-3333-4333-8333-333333333333' } as Partial<QueueItem>)), [ROTA_MENSAGENS]: servidorDeHistorico(() => []) }),
    );
    renderizar({ me: { ...USUARIO, id: OUTRO_USUARIO_ID }, cache: new CacheDeConversas({ armazem: new ArmazemLocal(OUTRO_OPERADOR) }) });

    expect(await screen.findByText('Este atendimento está com outro atendente.')).toBeTruthy();
    expect(screen.queryByLabelText('Texto guardado')).toBeNull();
    expect(screen.queryByText('Era da Ana')).toBeNull();
  });
});
