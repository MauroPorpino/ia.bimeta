import { describe, expect, it } from 'vitest';
import { ArmazemLocal, PREFIXO_DO_ARMAZEM, VALIDADE_DOS_REGISTROS_MS } from './armazem';

/** Um Storage de mentira, para testar sem depender do navegador (e para simular cota cheia e armazenamento indisponível). */
class StorageDeTeste implements Storage {
  private readonly dados = new Map<string, string>();
  falharAoGravar = false;

  get length(): number {
    return this.dados.size;
  }
  clear(): void {
    this.dados.clear();
  }
  getItem(chave: string): string | null {
    return this.dados.get(chave) ?? null;
  }
  key(indice: number): string | null {
    return [...this.dados.keys()][indice] ?? null;
  }
  removeItem(chave: string): void {
    this.dados.delete(chave);
  }
  setItem(chave: string, valor: string): void {
    if (this.falharAoGravar) {
      throw new DOMException('cota cheia', 'QuotaExceededError');
    }
    this.dados.set(chave, valor);
  }
  chaves(): string[] {
    return [...this.dados.keys()];
  }
}

const ANA = { tenant: 'demo-a', usuarioId: '11111111-1111-4111-8111-111111111111' };
const PEDRO = { tenant: 'demo-a', usuarioId: '22222222-2222-4222-8222-222222222222' };
const ANA_EM_OUTRO_TENANT = { tenant: 'demo-b', usuarioId: ANA.usuarioId };

describe('ArmazemLocal: rascunhos', () => {
  it('grava e lê o rascunho de cada conversa, e texto vazio apaga', () => {
    const storage = new StorageDeTeste();
    const armazem = new ArmazemLocal(ANA, { storage });

    armazem.gravarRascunho('item-1', 'Bom dia');
    armazem.gravarRascunho('item-2', 'Boa tarde');

    expect(armazem.lerRascunho('item-1')?.texto).toBe('Bom dia');
    expect(armazem.lerRascunho('item-2')?.texto).toBe('Boa tarde');
    armazem.gravarRascunho('item-1', '');
    expect(armazem.lerRascunho('item-1')).toBeNull();
    expect(armazem.lerRascunho('item-2')?.texto).toBe('Boa tarde');
  });

  it('sobrevive a "recarregar": outra instância sobre o mesmo armazenamento e o mesmo escopo lê o rascunho', () => {
    const storage = new StorageDeTeste();
    new ArmazemLocal(ANA, { storage }).gravarRascunho('item-1', 'Texto digitado');

    expect(new ArmazemLocal(ANA, { storage }).lerRascunho('item-1')?.texto).toBe('Texto digitado');
  });

  it('o escopo é tenant + usuário: outro usuário, ou o mesmo usuário em outro tenant, nunca lê o rascunho', () => {
    const storage = new StorageDeTeste();
    new ArmazemLocal(ANA, { storage }).gravarRascunho('item-1', 'Só da Ana');

    expect(new ArmazemLocal(PEDRO, { storage }).lerRascunho('item-1')).toBeNull();
    expect(new ArmazemLocal(ANA_EM_OUTRO_TENANT, { storage }).lerRascunho('item-1')).toBeNull();
    expect(new ArmazemLocal(ANA, { storage }).lerRascunho('item-1')?.texto).toBe('Só da Ana');
  });

  it('guarda o pedido pendente junto do rascunho, e o texto vazio só apaga o registro se não houver pendência', () => {
    const storage = new StorageDeTeste();
    const armazem = new ArmazemLocal(ANA, { storage });

    armazem.gravarRascunho('item-1', 'Resposta', { requestId: 'req-1', texto: 'Resposta' });
    expect(armazem.lerRascunho('item-1')).toEqual({ texto: 'Resposta', pendente: { requestId: 'req-1', texto: 'Resposta' } });

    armazem.gravarRascunho('item-1', '', { requestId: 'req-1', texto: 'Resposta' });
    expect(armazem.lerRascunho('item-1')).toEqual({ texto: '', pendente: { requestId: 'req-1', texto: 'Resposta' } });

    armazem.gravarRascunho('item-1', '');
    expect(armazem.lerRascunho('item-1')).toBeNull();
  });

  it('a chave nunca leva token, e o conteúdo é só o texto, o requestId e a data', () => {
    const storage = new StorageDeTeste();
    new ArmazemLocal(ANA, { storage }).gravarRascunho('item-1', 'Olá');

    const [chave] = storage.chaves();
    expect(chave?.startsWith(`${PREFIXO_DO_ARMAZEM}:`)).toBe(true);
    expect(chave).toContain(ANA.usuarioId);
    expect(chave).toContain('demo-a');
    expect(Object.keys(JSON.parse(storage.getItem(chave ?? '') ?? '{}') as object).sort()).toEqual(['em', 't']);
  });
});

describe('ArmazemLocal: validade, limite e limpeza', () => {
  it('registro vencido (7 dias) não é lido e é apagado', () => {
    const storage = new StorageDeTeste();
    let agora = 1_000_000;
    const armazem = new ArmazemLocal(ANA, { storage, agora: () => agora });
    armazem.gravarRascunho('item-1', 'Antigo');

    agora += VALIDADE_DOS_REGISTROS_MS - 1;
    expect(armazem.lerRascunho('item-1')?.texto).toBe('Antigo');

    agora += 2;
    expect(armazem.lerRascunho('item-1')).toBeNull();
    expect(storage.length).toBe(0);
  });

  it('ao abrir, expurga o vencido de QUALQUER usuário (dado velho de outro operador não fica no navegador) e o que não é JSON', () => {
    const storage = new StorageDeTeste();
    let agora = 5_000_000;
    new ArmazemLocal(PEDRO, { storage, agora: () => agora }).gravarRascunho('item-9', 'Texto do Pedro');
    storage.setItem(`${PREFIXO_DO_ARMAZEM}:lixo`, 'isto nao e json');
    storage.setItem('outra-aplicacao', 'nao toca');

    agora += VALIDADE_DOS_REGISTROS_MS + 1;
    new ArmazemLocal(ANA, { storage, agora: () => agora });

    expect(storage.chaves()).toEqual(['outra-aplicacao']);
  });

  it('passou de 100 registros do usuário: os mais antigos saem', () => {
    const storage = new StorageDeTeste();
    let agora = 1_000;
    const armazem = new ArmazemLocal(ANA, { storage, agora: () => agora });
    for (let i = 0; i < 105; i += 1) {
      agora += 1;
      armazem.gravarRascunho(`item-${i}`, `texto ${i}`);
    }

    armazem.expurgar();

    expect(storage.length).toBe(100);
    expect(armazem.lerRascunho('item-0')).toBeNull();
    expect(armazem.lerRascunho('item-4')).toBeNull();
    expect(armazem.lerRascunho('item-5')?.texto).toBe('texto 5');
    expect(armazem.lerRascunho('item-104')?.texto).toBe('texto 104');
  });

  it('limparDoUsuario apaga só o que é dele; limparConversa apaga rascunho e visto de uma conversa', () => {
    const storage = new StorageDeTeste();
    const ana = new ArmazemLocal(ANA, { storage });
    const pedro = new ArmazemLocal(PEDRO, { storage });
    ana.gravarRascunho('item-1', 'Da Ana');
    ana.gravarVisto('item-1', 7);
    ana.gravarRascunho('item-2', 'Outro da Ana');
    pedro.gravarRascunho('item-1', 'Do Pedro');

    ana.limparConversa('item-1');
    expect(ana.lerRascunho('item-1')).toBeNull();
    expect(ana.lerVisto('item-1')).toBeUndefined();
    expect(ana.lerRascunho('item-2')?.texto).toBe('Outro da Ana');

    ana.limparDoUsuario();
    expect(ana.lerRascunho('item-2')).toBeNull();
    expect(pedro.lerRascunho('item-1')?.texto).toBe('Do Pedro');
  });
});

describe('ArmazemLocal: visto e falhas do armazenamento', () => {
  it('o "visto até" só avança', () => {
    const armazem = new ArmazemLocal(ANA, { storage: new StorageDeTeste() });

    armazem.gravarVisto('item-1', 10);
    armazem.gravarVisto('item-1', 4);
    expect(armazem.lerVisto('item-1')).toBe(10);
    armazem.gravarVisto('item-1', 12);
    expect(armazem.lerVisto('item-1')).toBe(12);
  });

  it('sem armazenamento (modo privado) nada quebra: não está disponível e só devolve vazio', () => {
    const armazem = new ArmazemLocal(ANA, { storage: null });

    expect(armazem.disponivel).toBe(false);
    expect(armazem.gravarRascunho('item-1', 'Texto')).toBe(false);
    expect(armazem.lerRascunho('item-1')).toBeNull();
    expect(armazem.lerVisto('item-1')).toBeUndefined();
    expect(() => armazem.limparDoUsuario()).not.toThrow();
  });

  it('cota cheia: a gravação devolve falso (a tela segue só com a memória), sem lançar', () => {
    const storage = new StorageDeTeste();
    const armazem = new ArmazemLocal(ANA, { storage });
    storage.falharAoGravar = true;

    expect(armazem.gravarRascunho('item-1', 'Texto')).toBe(false);
    expect(() => armazem.gravarVisto('item-1', 3)).not.toThrow();
  });

  it('texto gigante não é gravado (proteção da cota)', () => {
    const armazem = new ArmazemLocal(ANA, { storage: new StorageDeTeste() });

    expect(armazem.gravarRascunho('item-1', 'x'.repeat(20_001))).toBe(false);
    expect(armazem.lerRascunho('item-1')).toBeNull();
  });
});
