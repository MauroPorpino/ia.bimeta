import { describe, expect, it } from 'vitest';
import { ApiError } from './api/client';
import type { CurrentUser } from './api/types';
import { enderecoParaExibir, lerParametrosDeExemplo, listaDoTexto, mensagemDeErroDaIntegracao, moldeVazio, numeroOuNulo, paresDoTexto, podeVerIntegracoes, rotuloDoCodigo, textoDosPares } from './integracao';

const usuario = (grants: CurrentUser['grants']): CurrentUser => ({ id: '1', email: 'a@b.c', displayName: 'A', grants, preferredLocale: null });

describe('integracao: helpers', () => {
  it('quem vê a área: administradores e gerentes, não o atendente comum', () => {
    expect(podeVerIntegracoes(usuario([]))).toBe(false);
    expect(podeVerIntegracoes(usuario([{ organizationId: 'o', role: 'User' }]))).toBe(false);
    expect(podeVerIntegracoes(usuario([{ organizationId: 'o', role: 'Manager' }]))).toBe(true);
    expect(podeVerIntegracoes(usuario([{ organizationId: 'o', role: 'OrganizationAdmin' }]))).toBe(true);
    expect(podeVerIntegracoes(usuario([{ organizationId: null, role: 'OrganizationAdmin' }]))).toBe(true);
  });

  it('o JSON de exemplo precisa ser um objeto', () => {
    expect(lerParametrosDeExemplo('')).toEqual({ valores: {} });
    expect(lerParametrosDeExemplo('{"numero": 1}')).toEqual({ valores: { numero: 1 } });
    expect(lerParametrosDeExemplo('[1]')).toHaveProperty('erro');
    expect(lerParametrosDeExemplo('1')).toHaveProperty('erro');
    expect(lerParametrosDeExemplo('null')).toHaveProperty('erro');
    expect(lerParametrosDeExemplo('{nao json')).toEqual({ erro: 'O texto não é um JSON válido.' });
  });

  it('listas, pares e números digitados', () => {
    expect(listaDoTexto(' a, b\n c ,, ')).toEqual(['a', 'b', 'c']);
    expect(paresDoTexto('X-Filial: 01\nsem dois pontos\nX-Origem:ia: bimeta')).toEqual({ 'X-Filial': '01', 'X-Origem': 'ia: bimeta' });
    expect(textoDosPares({ 'X-Filial': '01' })).toBe('X-Filial: 01');
    expect(numeroOuNulo('')).toBeNull();
    expect(numeroOuNulo('  ')).toBeNull();
    expect(numeroOuNulo('10,5')).toBe(10.5);
    expect(numeroOuNulo('abc')).toBeUndefined();
  });

  it('o endereço exibido nunca traz credencial nem consulta', () => {
    expect(enderecoParaExibir('https://user:senha@erp.exemplo.com/api?token=abc#x')).toBe('https://erp.exemplo.com/api');
    expect(enderecoParaExibir('https://erp.exemplo.com/')).toBe('https://erp.exemplo.com');
    expect(enderecoParaExibir('não é url')).toBe('não é url');
  });

  it('os códigos do motor têm frase em português e o código desconhecido não vaza texto', () => {
    expect(rotuloDoCodigo('outcome-unknown')).toMatch(/ambíguo/);
    expect(rotuloDoCodigo('codigo-novo')).toBe('Erro (codigo-novo).');
    expect(rotuloDoCodigo(null)).toBe('');
  });

  it('mensagens de erro por status', () => {
    expect(mensagemDeErroDaIntegracao(new ApiError(403, 'Acesso negado'))).toMatch(/permissão/);
    expect(mensagemDeErroDaIntegracao(new ApiError(404, 'x'))).toMatch(/fora do seu alcance/);
    expect(mensagemDeErroDaIntegracao(new ApiError(409, 'x', null, 'changed'))).toMatch(/alterado por outra pessoa/);
    expect(mensagemDeErroDaIntegracao(new ApiError(409, 'x', null, 'key-in-use'))).toMatch(/Já existe/);
    expect(mensagemDeErroDaIntegracao(new ApiError(503, 'x', null, 'secrets-unavailable'))).toMatch(/chave de cifra/);
    expect(mensagemDeErroDaIntegracao(new Error('falha'))).toBe('falha');
  });

  it('o molde vazio de escrita já nasce com a idempotência declarada e o de leitura sem', () => {
    expect(moldeVazio('Read')).toMatchObject({ method: 'GET', idempotency: 'Unspecified' });
    expect(moldeVazio('Write')).toMatchObject({ method: 'POST', idempotency: 'None' });
  });
});
