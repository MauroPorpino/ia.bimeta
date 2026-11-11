import { describe, expect, it, vi } from 'vitest';
import { chamadasDe, criarFetch, json } from '../test/helpers';
import { ApiError, createApi } from './client';
import { definirSessao } from './session';

function iniciar(rotas: Parameters<typeof criarFetch>[0]) {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
  const f = criarFetch(rotas);
  vi.stubGlobal('fetch', f);
  return { f, api: createApi() };
}

const ID = '11111111-aaaa-4aaa-8aaa-111111111111';
const CONEXAO = { organizationId: null, key: 'erp', name: 'ERP', baseUrl: 'https://erp.exemplo.com', authType: 'None' as const, authSettings: {}, defaultHeaders: {}, allowedHeaders: [], healthPath: '/', timeoutSeconds: 10, maxResponseBytes: null };

describe('cliente: motor de integração (ADR 0035)', () => {
  it('as rotas de conexão usam os verbos e os corpos do servidor, sem tenant em lugar nenhum', async () => {
    const { f, api } = iniciar({
      'GET /capabilities/connections': json([]),
      'POST /capabilities/connections': json({ id: ID }, 201),
      [`PUT /capabilities/connections/${ID}`]: json({ id: ID }),
      [`PUT /capabilities/connections/${ID}/secret`]: json({ id: ID }),
      [`POST /capabilities/connections/${ID}/enable`]: json({ id: ID }),
      [`POST /capabilities/connections/${ID}/disable`]: json({ id: ID }),
      [`POST /capabilities/connections/${ID}/test`]: json({ ok: true, outcome: 'ok', upstreamStatus: 200, elapsedMs: 5 }),
    });

    await api.listarConexoes();
    await api.criarConexao(CONEXAO);
    await api.atualizarConexao(ID, { name: 'ERP', baseUrl: 'https://erp.exemplo.com', authType: 'None', authSettings: {}, defaultHeaders: {}, allowedHeaders: [], healthPath: '/', timeoutSeconds: 10, maxResponseBytes: null, expectedRevision: 3 });
    await api.definirSegredoDaConexao(ID, { apiKey: 'K' }, 4);
    await api.habilitarConexao(ID);
    await api.desabilitarConexao(ID);
    const teste = await api.testarConexao(ID);

    expect(teste.ok).toBe(true);
    expect(chamadasDe(f, `PUT /capabilities/connections/${ID}/secret`)[0]!.corpo).toEqual({ values: { apiKey: 'K' }, expectedRevision: 4 });
    expect(chamadasDe(f, `PUT /capabilities/connections/${ID}`)[0]!.corpo).toMatchObject({ expectedRevision: 3 });
    for (const [, init] of f.mock.calls) {
      expect(String(init?.body ?? '')).not.toMatch(/tenant/i);
    }
  });

  it('as rotas de operação, de teste e de execução', async () => {
    const { f, api } = iniciar({
      'GET /capabilities/definitions': json([]),
      'GET /capabilities/definitions/by-key/pedidos.consultar/versions': json([]),
      [`POST /capabilities/definitions/${ID}/publish`]: json({ id: ID }),
      [`POST /capabilities/definitions/${ID}/retire`]: json({ id: ID }),
      [`POST /capabilities/definitions/${ID}/enable`]: json({ id: ID }),
      [`POST /capabilities/definitions/${ID}/disable`]: json({ id: ID }),
      [`POST /capabilities/definitions/${ID}/test`]: json({ status: 'Succeeded' }),
      [`POST /capabilities/definitions/${ID}/validate`]: json({ valid: true, problems: [] }),
      'GET /capabilities/executions': json({ items: [], total: 0 }),
      'GET /capabilities/executions/stats': json([]),
      [`POST /capabilities/executions/${ID}/confirm`]: json({ status: 'Succeeded' }),
      [`POST /capabilities/executions/${ID}/cancel`]: json({ status: 'Cancelled' }),
    });

    await api.listarOperacoes(ID);
    await api.versoesDaOperacao('pedidos.consultar');
    await api.publicarOperacao(ID);
    await api.aposentarOperacao(ID);
    await api.ligarOperacao(ID);
    await api.desligarOperacao(ID);
    await api.testarOperacao(ID, { numero: 1 });
    await api.validarParametrosDaOperacao(ID, { numero: 1 });
    await api.listarExecucoes({ connectionId: ID, key: 'pedidos.consultar', status: 'Failed', isTest: true, take: 20, skip: 40 });
    await api.resumoDasExecucoes(30);
    await api.confirmarExecucao(ID);
    await api.cancelarExecucao(ID);

    expect(chamadasDe(f, 'GET /capabilities/definitions')[0]!.consulta.get('connectionId')).toBe(ID);
    expect(chamadasDe(f, `POST /capabilities/definitions/${ID}/test`)[0]!.corpo).toEqual({ parameters: { numero: 1 } });
    const consulta = chamadasDe(f, 'GET /capabilities/executions')[0]!.consulta;
    expect(Object.fromEntries(consulta)).toEqual({ connectionId: ID, key: 'pedidos.consultar', status: 'Failed', isTest: 'true', take: '20', skip: '40' });
    expect(chamadasDe(f, 'GET /capabilities/executions/stats')[0]!.consulta.get('days')).toBe('30');
  });

  it('o erro 400 traz os problemas de validação em lista e o 409 e o 503 trazem o código', async () => {
    const { api } = iniciar({
      'POST /capabilities/connections': json({ title: 'Dados inválidos', status: 400, problems: ['baseUrl: destino não permitido (private-address).', 42, 'name: de 1 a 100 caracteres.'] }, 400),
      [`POST /capabilities/connections/${ID}/enable`]: json({ title: 'Conflito', status: 409, code: 'changed' }, 409),
      [`PUT /capabilities/connections/${ID}/secret`]: json({ title: 'Recurso indisponível', status: 503, code: 'secrets-unavailable' }, 503),
    });

    const ruim = await api.criarConexao({ ...CONEXAO, name: '', baseUrl: 'http://127.0.0.1' }).catch((e: unknown) => e);
    expect(ruim).toBeInstanceOf(ApiError);
    expect((ruim as ApiError).status).toBe(400);
    expect((ruim as ApiError).problemas).toEqual(['baseUrl: destino não permitido (private-address).', 'name: de 1 a 100 caracteres.']);

    const conflito = await api.habilitarConexao(ID).catch((e: unknown) => e as ApiError);
    expect((conflito as ApiError).code).toBe('changed');
    expect((conflito as ApiError).problemas).toEqual([]);
    const indisponivel = await api.definirSegredoDaConexao(ID, { apiKey: 'k' }, 1).catch((e: unknown) => e as ApiError);
    expect((indisponivel as ApiError).status).toBe(503);
    expect((indisponivel as ApiError).code).toBe('secrets-unavailable');
  });
});
