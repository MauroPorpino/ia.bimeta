import { describe, expect, it, vi } from 'vitest';
import { GUID, chamadasDe, criarFetch, item, json, problema } from '../test/helpers';
import { ApiError, createApi, descreverErro } from './client';
import { aoSair, definirSessao, obterToken } from './session';

const ITEM = item();
const OK = { item: ITEM, outcome: 'Completed' };

describe('cliente da API: sessão e erros', () => {
  it('envia o token da sessão como Authorization: Bearer', async () => {
    const f = criarFetch({ 'GET /auth/me': json({ id: 'u1', email: 'a@b', displayName: 'Ana', grants: [], preferredLocale: null }) });
    vi.stubGlobal('fetch', f);
    definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());

    await createApi().me();

    expect(f.mock.calls[0]?.[0]).toBe('/api/auth/me');
    expect(new Headers(f.mock.calls[0]?.[1]?.headers).get('Authorization')).toBe('Bearer token-sintetico');
  });

  it('não envia o token no login e manda tenant, e-mail e senha', async () => {
    const f = criarFetch({ 'POST /auth/login': json({ token: 't', expiresAt: '2099-01-01T00:00:00Z' }) });
    vi.stubGlobal('fetch', f);
    definirSessao('token-antigo', new Date(Date.now() + 3_600_000).toISOString());

    await createApi().login({ tenant: 'demo-a', email: 'a@b.c', password: 'segredo' });

    const init = f.mock.calls[0]?.[1];
    expect(new Headers(init?.headers).has('Authorization')).toBe(false);
    expect(JSON.parse(String(init?.body))).toEqual({ tenant: 'demo-a', email: 'a@b.c', password: 'segredo' });
  });

  it('401 numa chamada autenticada limpa a sessão e dispara o evento de saída', async () => {
    vi.stubGlobal('fetch', criarFetch({ 'GET /contact-center/me/queues': problema(401, 'Autenticação necessária') }));
    definirSessao('t', new Date(Date.now() + 3_600_000).toISOString());
    const saiu = vi.fn();
    aoSair(saiu);

    await expect(createApi().listarMinhasFilas()).rejects.toMatchObject({ status: 401 });

    expect(saiu).toHaveBeenCalledTimes(1);
    expect(obterToken()).toBeNull();
    expect(sessionStorage.length).toBe(0);
  });

  it('401 no login (credenciais erradas) não é saída de sessão', async () => {
    vi.stubGlobal('fetch', criarFetch({ 'POST /auth/login': problema(401, 'Credenciais inválidas', 'Tenant, e-mail ou senha não conferem.') }));
    const saiu = vi.fn();
    aoSair(saiu);

    const erro = await createApi().login({ tenant: 't', email: 'e@x.y', password: 'p' }).catch((e: unknown) => e);

    expect(erro).toBeInstanceOf(ApiError);
    expect(descreverErro(erro)).toBe('Credenciais inválidas: Tenant, e-mail ou senha não conferem.');
    expect(saiu).not.toHaveBeenCalled();
  });

  it('mostra título e detalhe do ProblemDetails e guarda o código do 409', async () => {
    vi.stubGlobal(
      'fetch',
      criarFetch({ 'POST /contact-center/items/i1/claim': problema(409, 'Conflito', 'Outro atendente assumiu a conversa.', 'AlreadyAssigned') }),
    );

    const erro = await createApi().assumir('i1').catch((e: unknown) => e as ApiError);

    expect(erro).toMatchObject({ status: 409, title: 'Conflito', detail: 'Outro atendente assumiu a conversa.', code: 'AlreadyAssigned' });
  });

  it('falha de rede vira ApiError com status 0, sem quebrar', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    const erro = (await createApi().listarMinhasFilas().catch((e: unknown) => e)) as ApiError;

    expect(erro).toBeInstanceOf(ApiError);
    expect(erro.status).toBe(0);
    expect(descreverErro(erro)).toContain('Sem conexão com a API');
  });

  it('500 sem corpo (proxy com a API desligada) vira mensagem legível', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 500 })));

    const erro = (await createApi().listarMinhasFilas().catch((e: unknown) => e)) as ApiError;

    expect(erro.status).toBe(500);
    expect(descreverErro(erro)).toContain('Erro no servidor (HTTP 500)');
  });

  it('502 sem corpo (proxy do Vite sem alcançar a API) diz que a API está indisponível', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 502 })));

    const erro = (await createApi().listarMinhasFilas().catch((e: unknown) => e)) as ApiError;

    expect(erro.status).toBe(502);
    expect(descreverErro(erro)).toContain('API indisponível');
  });

  it('monta a consulta da lista só com os filtros informados', async () => {
    const f = criarFetch({ 'GET /contact-center/items': json([]) });
    vi.stubGlobal('fetch', f);

    await createApi().listarItens({ status: 'Assigned', assignedToMe: true, queueId: 'q1', take: 50 });
    await createApi().listarItens({ status: 'Waiting', assignedToMe: false });

    const [primeira, segunda] = chamadasDe(f, 'GET /contact-center/items');
    expect(Object.fromEntries(primeira!.consulta)).toEqual({ status: 'Assigned', assignedToMe: 'true', queueId: 'q1', take: '50' });
    expect(Object.fromEntries(segunda!.consulta)).toEqual({ status: 'Waiting' });
  });
});

describe('cliente da API: requestId das ações', () => {
  it('claim, release e return-to-ai levam um GUID novo no corpo', async () => {
    const f = criarFetch({
      'POST /contact-center/items/i1/claim': json(OK),
      'POST /contact-center/items/i1/release': json(OK),
      'POST /contact-center/items/i1/return-to-ai': json(OK),
    });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    await api.assumir('i1');
    await api.devolverParaFila('i1');
    await api.devolverParaIa('i1');

    const ids = ['claim', 'release', 'return-to-ai'].map((acao) => chamadasDe(f, `POST /contact-center/items/i1/${acao}`)[0]?.corpo?.requestId);
    ids.forEach((id) => expect(id).toMatch(GUID));
    expect(new Set(ids).size).toBe(3);
  });

  it('a MESMA ação repetida depois de uma falha reaproveita o requestId; depois do sucesso, usa outro', async () => {
    let tentativa = 0;
    const f = criarFetch({
      'POST /contact-center/items/i1/claim': () => (++tentativa === 1 ? new Response(null, { status: 500 }) : json(OK)),
    });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    await api.assumir('i1').catch(() => undefined); // falha de servidor: efeito desconhecido
    await api.assumir('i1'); // repetição da mesma ação: conclui
    await api.assumir('i1'); // nova ação, depois do sucesso

    const [a, b, c] = chamadasDe(f, 'POST /contact-center/items/i1/claim').map((x) => x.corpo?.requestId);
    expect(a).toMatch(GUID);
    expect(b).toBe(a);
    expect(c).not.toBe(a);
  });

  it('202 (Pending) mantém o requestId: repetir conclui a mesma operação', async () => {
    let tentativa = 0;
    const f = criarFetch({
      'POST /contact-center/items/i1/release': () =>
        ++tentativa === 1 ? json({ item: ITEM, outcome: 'Pending' }, 202) : json({ item: ITEM, outcome: 'Completed' }),
    });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    const primeiro = await api.devolverParaFila('i1');
    await api.devolverParaFila('i1');

    expect(primeiro.outcome).toBe('Pending');
    const [a, b] = chamadasDe(f, 'POST /contact-center/items/i1/release').map((x) => x.corpo?.requestId);
    expect(b).toBe(a);
  });

  it('rejeição definitiva (422) descarta o requestId: a próxima tentativa usa outro', async () => {
    const f = criarFetch({ 'POST /contact-center/items/i1/return-to-ai': problema(422, 'Regra não atendida', 'x', 'SessionClosed') });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    await api.devolverParaIa('i1').catch(() => undefined);
    await api.devolverParaIa('i1').catch(() => undefined);

    const [a, b] = chamadasDe(f, 'POST /contact-center/items/i1/return-to-ai').map((x) => x.corpo?.requestId);
    expect(a).toMatch(GUID);
    expect(b).not.toBe(a);
  });

  it('409 de operação em andamento mantém o requestId (só quem a pediu a repete)', async () => {
    const f = criarFetch({ 'POST /contact-center/items/i1/claim': problema(409, 'Conflito', 'Há outra operação em andamento.', 'OperationInProgress') });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    await api.assumir('i1').catch(() => undefined);
    await api.assumir('i1').catch(() => undefined);

    const [a, b] = chamadasDe(f, 'POST /contact-center/items/i1/claim').map((x) => x.corpo?.requestId);
    expect(b).toBe(a);
  });

  it('reply: manda { requestId, text } e reaproveita o requestId só para o MESMO texto', async () => {
    let tentativa = 0;
    const f = criarFetch({
      'POST /contact-center/items/i1/reply': () => (++tentativa === 1 ? new Response(null, { status: 504 }) : json({})),
    });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    await api.responder('i1', 'Bom dia').catch(() => undefined);
    await api.responder('i1', 'Bom dia'); // mesma ação: mesmo requestId
    await api.responder('i1', 'Bom dia'); // depois do sucesso, outra mensagem igual: novo requestId

    const chamadas = chamadasDe(f, 'POST /contact-center/items/i1/reply').map((x) => x.corpo);
    expect(chamadas[0]).toEqual({ requestId: expect.stringMatching(GUID), text: 'Bom dia' });
    expect(chamadas[1]?.requestId).toBe(chamadas[0]?.requestId);
    expect(chamadas[2]?.requestId).not.toBe(chamadas[0]?.requestId);

    await api.responder('i1', 'Outro texto').catch(() => undefined);
    const ultima = chamadasDe(f, 'POST /contact-center/items/i1/reply').at(-1)?.corpo;
    expect(ultima?.text).toBe('Outro texto');
  });

  it('um texto diferente é outra ação e usa outro requestId, mesmo depois de uma falha', async () => {
    const f = criarFetch({ 'POST /contact-center/items/i1/reply': new Response(null, { status: 500 }) });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    await api.responder('i1', 'um').catch(() => undefined);
    await api.responder('i1', 'dois').catch(() => undefined);

    const [a, b] = chamadasDe(f, 'POST /contact-center/items/i1/reply').map((x) => x.corpo?.requestId);
    expect(b).not.toBe(a);
  });

  it('close: manda { requestId, reasonCode: "resolved" }', async () => {
    const f = criarFetch({ 'POST /contact-center/items/i1/close': json({}) });
    vi.stubGlobal('fetch', f);

    await createApi().encerrar('i1');

    expect(chamadasDe(f, 'POST /contact-center/items/i1/close')[0]?.corpo).toEqual({
      requestId: expect.stringMatching(GUID),
      reasonCode: 'resolved',
    });
  });
});
