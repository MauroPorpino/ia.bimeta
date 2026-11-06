import { describe, expect, it } from 'vitest';
import type { QueueItem } from './api/types';
import { CacheDeConversas, mesclarMensagens } from './cache';
import { item, mensagem } from './test/helpers';

describe('mesclarMensagens', () => {
  it('junta sem duplicar, em ordem de messageSeq', () => {
    const m1 = mensagem({ messageSeq: 1 });
    const m2 = mensagem({ messageSeq: 2 });
    const m3 = mensagem({ messageSeq: 3 });

    const resultado = mesclarMensagens([m1, m2], [m3, m2]);

    expect(resultado.map((m) => m.id)).toEqual([m1.id, m2.id, m3.id]);
  });

  it('guarda as antigas que a página nova não traz mais', () => {
    const antiga = mensagem({ messageSeq: 1, text: 'antiga' });
    const nova = mensagem({ messageSeq: 150, text: 'nova' });

    expect(mesclarMensagens([antiga], [nova]).map((m) => m.text)).toEqual(['antiga', 'nova']);
  });

  it('a mensagem recebida agora prevalece: o estado de entrega avança no lugar', () => {
    const fila = mensagem({ messageSeq: 1, direction: 'Outbound', author: 'Human', deliveryState: 'Queued' });
    const entregue = { ...fila, deliveryState: 'Delivered' as const };

    const resultado = mesclarMensagens([fila], [entregue]);

    expect(resultado).toHaveLength(1);
    expect(resultado[0]?.deliveryState).toBe('Delivered');
  });

  it('sem novidade devolve o MESMO array (a tela não se redesenha a cada ciclo)', () => {
    const atuais = [mensagem({ messageSeq: 1 }), mensagem({ messageSeq: 2 })];
    const copiasDaApi = atuais.map((m) => ({ ...m }));

    expect(mesclarMensagens(atuais, copiasDaApi)).toBe(atuais);
  });

  it('começando do zero devolve as recebidas ordenadas', () => {
    const m1 = mensagem({ messageSeq: 1 });
    const m2 = mensagem({ messageSeq: 2 });

    expect(mesclarMensagens([], [m2, m1]).map((m) => m.messageSeq)).toEqual([1, 2]);
  });
});

describe('CacheDeConversas', () => {
  const entrada = (it: QueueItem) => ({ item: it, mensagens: [], temMais: false });

  it('guarda e devolve a entrada de cada conversa', () => {
    const cache = new CacheDeConversas();
    const a = item({ id: 'a' });

    cache.gravar('a', entrada(a));

    expect(cache.ler('a')?.item).toBe(a);
    expect(cache.ler('b')).toBeUndefined();
  });

  it('descarta a conversa menos recente quando passa do limite', () => {
    const cache = new CacheDeConversas(2);
    cache.gravar('a', entrada(item({ id: 'a' })));
    cache.gravar('b', entrada(item({ id: 'b' })));
    cache.gravar('a', entrada(item({ id: 'a' }))); // a voltou a ser a mais recente
    cache.gravar('c', entrada(item({ id: 'c' })));

    expect(cache.ler('b')).toBeUndefined();
    expect(cache.ler('a')).toBeDefined();
    expect(cache.ler('c')).toBeDefined();
  });

  it('esquecer apaga o histórico guardado e deixa o rascunho', () => {
    const cache = new CacheDeConversas();
    cache.gravar('a', entrada(item({ id: 'a' })));
    cache.gravarRascunho('a', 'ainda quero responder');

    cache.esquecer('a');

    expect(cache.ler('a')).toBeUndefined();
    expect(cache.lerRascunho('a')).toBe('ainda quero responder');
  });

  it('o rascunho é por conversa, e texto vazio o apaga', () => {
    const cache = new CacheDeConversas();

    cache.gravarRascunho('a', 'olá');
    cache.gravarRascunho('b', 'tchau');
    expect(cache.lerRascunho('a')).toBe('olá');
    expect(cache.lerRascunho('b')).toBe('tchau');

    cache.gravarRascunho('a', '');
    expect(cache.lerRascunho('a')).toBe('');
    expect(cache.lerRascunho('b')).toBe('tchau');
    expect(cache.lerRascunho('nunca-escrito')).toBe('');
  });

  it('o limite vale também para os rascunhos', () => {
    const cache = new CacheDeConversas(2);
    cache.gravarRascunho('a', '1');
    cache.gravarRascunho('b', '2');
    cache.gravarRascunho('c', '3');

    expect(cache.lerRascunho('a')).toBe('');
    expect(cache.lerRascunho('b')).toBe('2');
    expect(cache.lerRascunho('c')).toBe('3');
  });
});
