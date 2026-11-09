import { describe, expect, it, vi } from 'vitest';
import { chamadasDe, criarFetch, definicao, entradaDoFaq, json, lacuna, problema } from '../test/helpers';
import { ApiError, createApi } from './client';
import { definirSessao } from './session';

function logar() {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
}

describe('cliente da API: conhecimento do agente (ADR 0030)', () => {
  it('lista as lacunas com situação, página e deslocamento; sem situação, traz todas', async () => {
    logar();
    const f = criarFetch({ 'GET /agents/knowledge-gaps': json({ items: [lacuna()], total: 1 }) });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    const pagina = await api.listarLacunasDoFaq({ status: 'Open', take: 20, skip: 0 });
    await api.listarLacunasDoFaq({ take: 50 });

    expect(pagina.total).toBe(1);
    expect(f.mock.calls[0]?.[0]).toBe('/api/agents/knowledge-gaps?status=Open&take=20&skip=0');
    expect(f.mock.calls[1]?.[0]).toBe('/api/agents/knowledge-gaps?take=50');
    expect(new Headers(f.mock.calls[0]?.[1]?.headers).get('Authorization')).toBe('Bearer token-sintetico');
  });

  it('lê o detalhe, descarta sem corpo e envia ao rascunho só a entrada montada (sem tenant nem usuário)', async () => {
    logar();
    const l = lacuna();
    const f = criarFetch({
      [`GET /agents/knowledge-gaps/${l.id}`]: json({ gap: l, occurrences: [], source: { state: 'Unavailable', question: null, humanReply: null } }),
      [`POST /agents/knowledge-gaps/${l.id}/dismiss`]: json({ ...l, status: 'Dismissed' }),
      [`POST /agents/knowledge-gaps/${l.id}/promote`]: json({ gap: { ...l, status: 'Promoted' }, draftVersion: 2, faqEntries: 3 }),
    });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    const detalhe = await api.obterLacunaDoFaq(l.id);
    const descartada = await api.descartarLacunaDoFaq(l.id);
    const promovida = await api.enviarLacunaAoRascunho(l.id, { question: 'P?', aliases: ['a'], answer: 'R.', autoReply: false });

    expect(detalhe.source.state).toBe('Unavailable');
    expect(descartada.status).toBe('Dismissed');
    expect(promovida.draftVersion).toBe(2);
    expect(chamadasDe(f, `POST /agents/knowledge-gaps/${l.id}/dismiss`)[0]!.corpo).toBeNull();
    expect(chamadasDe(f, `POST /agents/knowledge-gaps/${l.id}/promote`)[0]!.corpo).toEqual({ question: 'P?', aliases: ['a'], answer: 'R.', autoReply: false });
  });

  it('o 409 de uma decisão chega com o código para a tela traduzir', async () => {
    logar();
    const l = lacuna();
    vi.stubGlobal('fetch', criarFetch({ [`POST /agents/knowledge-gaps/${l.id}/promote`]: problema(409, 'Conflito', 'já existe', 'faq-conflict') }));

    const erro = await createApi()
      .enviarLacunaAoRascunho(l.id, { question: 'P?', aliases: [], answer: 'R.', autoReply: false })
      .catch((e: unknown) => e);

    expect(erro).toBeInstanceOf(ApiError);
    expect(erro).toMatchObject({ status: 409, code: 'faq-conflict' });
  });

  it('rascunho e publicada: o 404 vira nulo, outros erros continuam erro', async () => {
    logar();
    const f = criarFetch({
      'GET /agents/definition/draft': json(definicao([entradaDoFaq()])),
      'GET /agents/definition/published': new Response(null, { status: 404 }),
    });
    vi.stubGlobal('fetch', f);
    const api = createApi();

    expect((await api.obterRascunhoDoAgente())?.number).toBe(2);
    expect(await api.obterPublicadaDoAgente()).toBeNull();

    vi.stubGlobal('fetch', criarFetch({ 'GET /agents/definition/draft': problema(403, 'Acesso negado') }));
    await expect(createApi().obterRascunhoDoAgente()).rejects.toMatchObject({ status: 403 });
  });

  it('publicar é um POST sem corpo', async () => {
    logar();
    const f = criarFetch({ 'POST /agents/definition/publish': json(definicao([], { number: 4, status: 'Published' })) });
    vi.stubGlobal('fetch', f);

    const publicada = await createApi().publicarRascunhoDoAgente();

    expect(publicada.number).toBe(4);
    expect(chamadasDe(f, 'POST /agents/definition/publish')[0]!.corpo).toBeNull();
  });
});
