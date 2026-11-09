import { describe, expect, it } from 'vitest';
import type { QueueItem } from './api/types';
import { ArmazemLocal } from './armazem';
import { CacheDeConversas, aplicarPaginaAnterior, aplicarPaginaRecente, mesclarMensagens, type EntradaDeConversa } from './cache';
import { anexo, item, mensagem } from './test/helpers';

const ANA = { tenant: 'demo-a', usuarioId: '11111111-1111-4111-8111-111111111111' };
const PEDRO = { tenant: 'demo-a', usuarioId: '22222222-2222-4222-8222-222222222222' };

// As mesmas mensagens (mesmo id) em todas as leituras, como na API: só a janela muda.
const TODAS = Array.from({ length: 400 }, (_, i) => mensagem({ messageSeq: i + 1, text: `m${i + 1}` }));
function msgs(de: number, ate: number) {
  return TODAS.filter((m) => m.messageSeq >= de && m.messageSeq <= ate);
}

describe('páginas do histórico', () => {
  it('a primeira leitura guarda a página e o cursor das mais antigas', () => {
    const entrada = aplicarPaginaRecente(undefined, item(), { items: msgs(151, 250), nextCursor: 'c150' });

    expect(entrada.mensagens).toHaveLength(100);
    expect(entrada.cursorAnterior).toBe('c150');
    expect(entrada.carregouAnteriores).toBe(false);
  });

  it('antes de pedir páginas antigas, a releitura acompanha o cursor da página recente', () => {
    const primeira = aplicarPaginaRecente(undefined, item(), { items: msgs(151, 250), nextCursor: 'c150' });

    const depois = aplicarPaginaRecente(primeira, item(), { items: msgs(161, 260), nextCursor: 'c160' });

    expect(depois.mensagens.map((m) => m.messageSeq)).toEqual(Array.from({ length: 110 }, (_, i) => 151 + i));
    expect(depois.cursorAnterior).toBe('c160');
  });

  it('a página antiga entra no começo, sem repetir, e avança o cursor', () => {
    const recente = aplicarPaginaRecente(undefined, item(), { items: msgs(151, 250), nextCursor: 'c150' });

    const comAntigas = aplicarPaginaAnterior(recente, { items: msgs(101, 150), nextCursor: 'c100' });

    expect(comAntigas.mensagens).toHaveLength(150);
    expect(comAntigas.mensagens[0]?.messageSeq).toBe(101);
    expect(new Set(comAntigas.mensagens.map((m) => m.id)).size).toBe(150);
    expect(comAntigas.cursorAnterior).toBe('c100');
    expect(comAntigas.carregouAnteriores).toBe(true);
  });

  it('depois de pedir páginas antigas, a releitura periódica NÃO mexe no cursor nem perde o que foi carregado', () => {
    const recente = aplicarPaginaRecente(undefined, item(), { items: msgs(151, 250), nextCursor: 'c150' });
    const comAntigas = aplicarPaginaAnterior(recente, { items: msgs(101, 150), nextCursor: 'c100' });

    const releitura = aplicarPaginaRecente(comAntigas, item(), { items: msgs(152, 251), nextCursor: 'c151' });

    expect(releitura.cursorAnterior).toBe('c100');
    expect(releitura.mensagens[0]?.messageSeq).toBe(101);
    expect(releitura.mensagens.at(-1)?.messageSeq).toBe(251);
    expect(releitura.mensagens).toHaveLength(151);
  });

  it('última página antiga: o cursor vira nulo e acaba o botão', () => {
    const recente = aplicarPaginaRecente(undefined, item(), { items: msgs(51, 150), nextCursor: 'c50' });

    const tudo = aplicarPaginaAnterior(recente, { items: msgs(1, 50), nextCursor: null });

    expect(tudo.cursorAnterior).toBeNull();
    expect(tudo.mensagens).toHaveLength(150);
  });

  it('janela recente que não encosta no que a tela tinha (chegou mais que uma página): recomeça dela, sem buraco no meio', () => {
    const antiga = aplicarPaginaRecente(undefined, item(), { items: msgs(1, 100), nextCursor: null });

    const depois = aplicarPaginaRecente(antiga, item(), { items: msgs(301, 400), nextCursor: 'c300' });

    expect(depois.mensagens[0]?.messageSeq).toBe(301);
    expect(depois.mensagens).toHaveLength(100);
    expect(depois.cursorAnterior).toBe('c300');
    expect(depois.carregouAnteriores).toBe(false);
  });
});

describe('CacheDeConversas com armazenamento local', () => {
  function armazemEmMemoria() {
    const dados = new Map<string, string>();
    const storage = {
      get length() {
        return dados.size;
      },
      clear: () => dados.clear(),
      getItem: (k: string) => dados.get(k) ?? null,
      key: (i: number) => [...dados.keys()][i] ?? null,
      removeItem: (k: string) => void dados.delete(k),
      setItem: (k: string, v: string) => void dados.set(k, v),
    } as Storage;
    return storage;
  }

  it('o rascunho sobrevive a um cache novo (a página recarregada) com o mesmo escopo', () => {
    const storage = armazemEmMemoria();
    new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) }).gravarRascunho('item-1', 'Texto digitado');

    const recarregado = new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) });

    expect(recarregado.lerRascunho('item-1')).toBe('Texto digitado');
  });

  it('outro usuário no mesmo navegador não vê o rascunho', () => {
    const storage = armazemEmMemoria();
    new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) }).gravarRascunho('item-1', 'Texto da Ana');

    expect(new CacheDeConversas({ armazem: new ArmazemLocal(PEDRO, { storage }) }).lerRascunho('item-1')).toBe('');
  });

  it('o pedido pendente (requestId e texto) sobrevive junto do rascunho e sai quando é limpo', () => {
    const storage = armazemEmMemoria();
    const cache = new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) });
    cache.gravarRascunho('item-1', 'Resposta');
    cache.gravarPendente('item-1', { requestId: 'req-9', texto: 'Resposta' });

    const recarregado = new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) });
    expect(recarregado.lerPendente('item-1')).toEqual({ requestId: 'req-9', texto: 'Resposta' });
    expect(recarregado.lerRascunho('item-1')).toBe('Resposta');

    recarregado.gravarPendente('item-1', null);
    recarregado.gravarRascunho('item-1', '');
    expect(new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) }).lerRascunho('item-1')).toBe('');
    expect(storage.length).toBe(0);
  });

  it('o "visto até" sobrevive e só avança', () => {
    const storage = armazemEmMemoria();
    const cache = new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) });
    cache.gravarVisto('item-1', 9);
    cache.gravarVisto('item-1', 3);

    expect(new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) }).lerVisto('item-1')).toBe(9);
  });

  it('descartarConversa (encerrada por ele) apaga tudo da conversa; limparTudo (saída pedida) apaga tudo do usuário', () => {
    const storage = armazemEmMemoria();
    const cache = new CacheDeConversas({ armazem: new ArmazemLocal(ANA, { storage }) });
    const entrada: EntradaDeConversa = { item: item({ id: 'item-1' }), mensagens: [], cursorAnterior: null, carregouAnteriores: false };
    cache.gravar('item-1', entrada);
    cache.gravarRascunho('item-1', 'a');
    cache.gravarRascunho('item-2', 'b');
    cache.gravarVisto('item-1', 2);

    cache.descartarConversa('item-1');
    expect(cache.ler('item-1')).toBeUndefined();
    expect(cache.lerRascunho('item-1')).toBe('');
    expect(cache.lerVisto('item-1')).toBeUndefined();
    expect(cache.lerRascunho('item-2')).toBe('b');

    cache.limparTudo();
    expect(cache.lerRascunho('item-2')).toBe('');
    expect(storage.length).toBe(0);
  });

  it('a posição de rolagem é guardada por conversa (só em memória) e esquecer a apaga', () => {
    const cache = new CacheDeConversas();
    cache.gravarRolagem('item-1', { noFim: false, distanciaDoFim: 320 });

    expect(cache.lerRolagem('item-1')).toEqual({ noFim: false, distanciaDoFim: 320 });
    cache.esquecer('item-1');
    expect(cache.lerRolagem('item-1')).toBeUndefined();
  });
});

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
  const entrada = (it: QueueItem) => ({ item: it, mensagens: [], cursorAnterior: null, carregouAnteriores: false });

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

describe('mídia no histórico: o estado do arquivo chega pela releitura periódica', () => {
  it('a mesma mensagem com o arquivo pendente e depois disponível é uma mudança (a tela se atualiza)', () => {
    const pendente = mensagem({ messageSeq: 1, kind: 'Image', text: null, contentState: 'Pending', attachment: anexo({ status: 'Pending', mimeType: null, fileName: null, sizeBytes: null }) });
    const disponivel = { ...pendente, attachment: { ...pendente.attachment!, status: 'Available' as const, mimeType: 'image/png', fileName: 'a.png', sizeBytes: 10 } };

    const atuais = [pendente];
    const resultado = mesclarMensagens(atuais, [disponivel]);

    expect(resultado).not.toBe(atuais);
    expect(resultado[0]?.attachment?.status).toBe('Available');
  });

  it('sem mudança no arquivo, devolve o mesmo array (a tela não se redesenha nem rola)', () => {
    const m = mensagem({ messageSeq: 1, kind: 'Image', text: null, attachment: anexo() });
    const atuais = [m];

    expect(mesclarMensagens(atuais, [{ ...m }])).toBe(atuais);
  });

  it('o motivo da falha também conta como mudança', () => {
    const a = mensagem({ messageSeq: 1, kind: 'Image', text: null, attachment: anexo({ status: 'Unavailable', failureCode: 'expired' }) });
    const b = { ...a, attachment: { ...a.attachment!, failureCode: 'download-failed' } };
    const atuais = [a];

    expect(mesclarMensagens(atuais, [b])).not.toBe(atuais);
  });
});
