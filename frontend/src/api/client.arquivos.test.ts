import { describe, expect, it, vi } from 'vitest';
import { GUID, criarFetch, formulariosDe, json, problema } from '../test/helpers';
import { ApiError, createApi, ehRejeicaoDefinitiva } from './client';
import { definirSessao } from './session';

const ROTA = 'POST /contact-center/items/i1/reply-media';

function arquivo(nome = 'requisicao.png', tipo = 'image/png', modificado = 1_700_000_000_000): File {
  return new File([new Uint8Array([1, 2, 3])], nome, { type: tipo, lastModified: modificado });
}

describe('cliente da API: envio de arquivo do atendente (reply-media)', () => {
  it('envia multipart com requestId, file e caption (só com legenda), com Authorization e SEM fixar o Content-Type', async () => {
    const f = criarFetch({ [ROTA]: json({ outcome: 'Applied', messageId: 'm1', messageSeq: 7 }) });
    vi.stubGlobal('fetch', f);
    definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
    const imagem = arquivo();

    const resultado = await createApi().responderComArquivo('i1', imagem, 'Requisição do exame');

    expect(resultado).toEqual({ outcome: 'Applied', messageId: 'm1', messageSeq: 7 });
    expect(f.mock.calls[0]?.[0]).toBe('/api/contact-center/items/i1/reply-media');
    expect(f.mock.calls[0]?.[1]?.method).toBe('POST');
    const [chamada] = formulariosDe(f, ROTA);
    expect([...chamada!.formulario.keys()]).toEqual(['requestId', 'file', 'caption']);
    expect(chamada!.formulario.get('requestId')).toMatch(GUID);
    const enviado = chamada!.formulario.get('file') as File;
    expect(enviado.name).toBe('requisicao.png');
    expect(enviado.type).toBe('image/png');
    expect(enviado.size).toBe(3);
    expect(chamada!.formulario.get('caption')).toBe('Requisição do exame');
    expect(chamada!.cabecalhos.get('Authorization')).toBe('Bearer token-sintetico');
    // O navegador gera o Content-Type com o boundary: a tela não pode defini-lo.
    expect(chamada!.cabecalhos.has('Content-Type')).toBe(false);
  });

  it('sem legenda, o campo caption nem é enviado', async () => {
    const f = criarFetch({ [ROTA]: json({ outcome: 'Applied' }) });
    vi.stubGlobal('fetch', f);

    await createApi().responderComArquivo('i1', arquivo('nota.m4a', 'audio/mp4'), null);
    await createApi().responderComArquivo('i1', arquivo('laudo.pdf', 'application/pdf'), '');

    const [primeira, segunda] = formulariosDe(f, ROTA);
    expect([...primeira!.formulario.keys()]).toEqual(['requestId', 'file']);
    expect(primeira!.formulario.has('caption')).toBe(false);
    expect(segunda!.formulario.has('caption')).toBe(false);
  });

  it('o 422 MediaRejected chega ao ApiError com o código, o motivo curto e o texto em português do servidor', async () => {
    const f = criarFetch({
      [ROTA]: json({ title: 'Regra não atendida', status: 422, code: 'MediaRejected', reason: 'too-large', detail: 'O arquivo passa do tamanho máximo.' }, 422),
    });
    vi.stubGlobal('fetch', f);

    const erro = await createApi().responderComArquivo('i1', arquivo(), null).catch((e: unknown) => e as ApiError);

    expect(erro).toBeInstanceOf(ApiError);
    expect(erro).toMatchObject({ status: 422, code: 'MediaRejected', motivo: 'too-large', detail: 'O arquivo passa do tamanho máximo.' });
  });

  it('os demais erros ficam sem motivo (o campo é aditivo)', async () => {
    vi.stubGlobal('fetch', criarFetch({ [ROTA]: problema(422, 'Regra não atendida', 'A janela fechou.', 'ReplyWindowClosed') }));

    const erro = await createApi().responderComArquivo('i1', arquivo(), null).catch((e: unknown) => e as ApiError);

    expect(erro).toMatchObject({ status: 422, code: 'ReplyWindowClosed', motivo: null });
  });

  it('413 é rejeição definitiva (o pedido não teve efeito) e 5xx não', () => {
    expect(ehRejeicaoDefinitiva(new ApiError(413, 'Erro HTTP 413'))).toBe(true);
    expect(ehRejeicaoDefinitiva(new ApiError(422, 'Regra não atendida', null, 'MediaRejected', false, [], 'empty'))).toBe(true);
    expect(ehRejeicaoDefinitiva(new ApiError(500, 'Erro no servidor'))).toBe(false);
    expect(ehRejeicaoDefinitiva(new ApiError(0, 'Sem conexão com a API'))).toBe(false);
  });

  it('a MESMA ação repetida depois de um resultado incerto reaproveita o requestId; depois de uma rejeição definitiva, usa outro', async () => {
    let tentativas = 0;
    const f = criarFetch({
      [ROTA]: () => {
        tentativas += 1;
        if (tentativas === 1) {
          throw new TypeError('rede caiu');
        }
        if (tentativas === 3) {
          return json({ title: 'Regra não atendida', status: 422, code: 'MediaRejected', reason: 'empty' }, 422);
        }
        return json({ outcome: 'Applied' });
      },
    });
    vi.stubGlobal('fetch', f);
    const api = createApi();
    const imagem = arquivo();

    await api.responderComArquivo('i1', imagem, 'legenda').catch(() => undefined); // 1: rede caiu (incerto)
    await api.responderComArquivo('i1', imagem, 'legenda'); // 2: mesmo requestId
    await api.responderComArquivo('i1', imagem, 'legenda').catch(() => undefined); // 3: 422 (a resposta anterior deu certo: outro requestId)
    await api.responderComArquivo('i1', imagem, 'legenda'); // 4: depois da rejeição definitiva, outro requestId

    const ids = formulariosDe(f, ROTA).map((c) => c.formulario.get('requestId'));
    expect(ids).toHaveLength(4);
    expect(ids[0]).toBe(ids[1]);
    expect(ids[2]).not.toBe(ids[1]);
    expect(ids[3]).not.toBe(ids[2]);
  });

  it('o requestId sugerido (guardado pela tela) é o usado no primeiro pedido', async () => {
    const f = criarFetch({ [ROTA]: json({ outcome: 'Applied' }) });
    vi.stubGlobal('fetch', f);
    const sugerido = '0197a1b2-0000-7000-8000-00000000abcd';

    await createApi().responderComArquivo('i1', arquivo(), null, sugerido);

    expect(formulariosDe(f, ROTA)[0]!.formulario.get('requestId')).toBe(sugerido);
  });
});
