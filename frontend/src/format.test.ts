import { describe, expect, it } from 'vitest';
import { formatarTamanho, motivoDoAnexo } from './format';

describe('formatarTamanho', () => {
  it.each([
    [0, '0 B'],
    [812, '812 B'],
    [1024, '1 KB'],
    [1536, '1,5 KB'],
    [48_213, '47 KB'],
    [1_258_291, '1,2 MB'],
    [20 * 1024 * 1024, '20 MB'],
    [3 * 1024 * 1024 * 1024, '3 GB'],
  ])('%i bytes viram "%s"', (bytes, esperado) => {
    expect(formatarTamanho(bytes)).toBe(esperado);
  });

  it('valor ausente ou inválido vira texto vazio', () => {
    expect(formatarTamanho(null)).toBe('');
    expect(formatarTamanho(undefined)).toBe('');
    expect(formatarTamanho(-1)).toBe('');
    expect(formatarTamanho(Number.NaN)).toBe('');
  });
});

describe('motivoDoAnexo', () => {
  it('cada motivo conhecido tem uma frase própria e o desconhecido tem a genérica do estado', () => {
    expect(motivoDoAnexo('Unavailable', 'expired')).toContain('expirou');
    expect(motivoDoAnexo('Unavailable', 'download-failed')).toContain('depois de várias tentativas');
    expect(motivoDoAnexo('Rejected', 'malware')).toContain('verificação de segurança');
    expect(motivoDoAnexo('Unavailable', 'algo-novo')).toBe('O arquivo não está disponível.');
    expect(motivoDoAnexo('Rejected', null)).toContain('política de segurança');
  });

  it('nunca repete texto do provedor: o código desconhecido não aparece na frase', () => {
    expect(motivoDoAnexo('Unavailable', 'texto-livre-do-provedor')).not.toContain('texto-livre');
  });
});
