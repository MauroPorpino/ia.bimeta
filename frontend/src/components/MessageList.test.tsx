import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Message } from '../api/types';
import { mensagem } from '../test/helpers';
import { MessageList } from './MessageList';

// O jsdom não calcula layout: a altura do conteúdo e da janela são simuladas, e a rolagem (scrollTop) é a do próprio elemento.
let alturaDoConteudo = 1000;
let alturaDaJanela = 400;

beforeEach(() => {
  alturaDoConteudo = 1000;
  alturaDaJanela = 400;
  vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockImplementation(() => alturaDoConteudo);
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockImplementation(() => alturaDaJanela);
});

afterEach(() => {
  vi.restoreAllMocks();
});

// As mesmas mensagens (mesmo id) em todas as renderizações, como na API.
const TODAS: Message[] = Array.from({ length: 200 }, (_, i) => mensagem({ messageSeq: i + 1, text: `m${i + 1}` }));
const entre = (de: number, ate: number) => TODAS.filter((m) => m.messageSeq >= de && m.messageSeq <= ate);

function lista(): HTMLElement {
  return screen.getByRole('log');
}

function rolarPara(el: HTMLElement, topo: number) {
  el.scrollTop = topo;
  fireEvent.scroll(el);
}

describe('MessageList: histórico em páginas', () => {
  it('com mensagens mais antigas, mostra o botão; clicar pede a página', async () => {
    const aoCarregar = vi.fn();
    const usuario = userEvent.setup();
    render(<MessageList mensagens={entre(101, 200)} temMaisAntigas aoCarregarAnteriores={aoCarregar} />);

    await usuario.click(screen.getByRole('button', { name: 'Carregar mensagens anteriores' }));

    expect(aoCarregar).toHaveBeenCalledTimes(1);
  });

  it('sem mensagens mais antigas, não há botão', () => {
    render(<MessageList mensagens={entre(1, 20)} temMaisAntigas={false} />);

    expect(screen.queryByRole('button', { name: /anteriores/ })).toBeNull();
  });

  it('carregando: mostra o estado e não oferece o botão (nada de pedido duplo)', () => {
    render(<MessageList mensagens={entre(101, 200)} temMaisAntigas carregandoAnteriores />);

    expect(screen.getByText('Carregando mensagens anteriores...')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /anteriores/ })).toBeNull();
  });

  it('erro ao carregar: mostra o motivo e o botão vira "tentar de novo"', async () => {
    const aoCarregar = vi.fn();
    const usuario = userEvent.setup();
    render(<MessageList mensagens={entre(101, 200)} temMaisAntigas erroAnteriores="Sem conexão: não foi possível carregar." aoCarregarAnteriores={aoCarregar} />);

    expect(screen.getByText('Sem conexão: não foi possível carregar.')).toBeTruthy();
    await usuario.click(screen.getByRole('button', { name: 'Tentar carregar de novo' }));

    expect(aoCarregar).toHaveBeenCalledTimes(1);
  });

  it('rolar até o topo pede a página antiga sozinho (uma vez, e não durante o carregamento nem depois de erro)', () => {
    const aoCarregar = vi.fn();
    const { rerender } = render(<MessageList mensagens={entre(101, 200)} temMaisAntigas aoCarregarAnteriores={aoCarregar} />);

    rolarPara(lista(), 20);
    expect(aoCarregar).toHaveBeenCalledTimes(1);

    rerender(<MessageList mensagens={entre(101, 200)} temMaisAntigas carregandoAnteriores aoCarregarAnteriores={aoCarregar} />);
    rolarPara(lista(), 10);
    expect(aoCarregar).toHaveBeenCalledTimes(1);

    rerender(<MessageList mensagens={entre(101, 200)} temMaisAntigas erroAnteriores="falhou" aoCarregarAnteriores={aoCarregar} />);
    rolarPara(lista(), 5);
    expect(aoCarregar).toHaveBeenCalledTimes(1);
  });

  it('rolar longe do topo, ou sem mensagens antigas, não pede nada', () => {
    const aoCarregar = vi.fn();
    const { rerender } = render(<MessageList mensagens={entre(101, 200)} temMaisAntigas aoCarregarAnteriores={aoCarregar} />);
    rolarPara(lista(), 500);

    rerender(<MessageList mensagens={entre(1, 200)} temMaisAntigas={false} aoCarregarAnteriores={aoCarregar} />);
    rolarPara(lista(), 0);

    expect(aoCarregar).not.toHaveBeenCalled();
  });

  it('as mensagens antigas que entram no começo NÃO empurram o que o atendente lê: a posição é compensada', () => {
    const { rerender } = render(<MessageList mensagens={entre(101, 200)} temMaisAntigas />);
    const ol = lista();
    expect(ol.scrollTop).toBe(1000); // abriu no fim
    rolarPara(ol, 0); // foi até o topo para ler o começo

    alturaDoConteudo = 2000; // a página antiga (100 mensagens) aumentou o conteúdo
    rerender(<MessageList mensagens={entre(1, 200)} temMaisAntigas={false} />);

    expect(ol.scrollTop).toBe(1000); // a mensagem que estava no topo continua no mesmo lugar da tela
  });

  it('com as mensagens antigas carregadas, cada mensagem aparece uma vez só', () => {
    render(<MessageList mensagens={entre(1, 200)} temMaisAntigas={false} />);

    expect(screen.getAllByRole('listitem')).toHaveLength(200);
    expect(screen.getAllByText('m1')).toHaveLength(1);
    expect(screen.getAllByText('m200')).toHaveLength(1);
  });
});

describe('MessageList: rolagem e mensagens novas', () => {
  it('abre no fim da conversa', () => {
    render(<MessageList mensagens={entre(101, 200)} temMaisAntigas={false} />);

    expect(lista().scrollTop).toBe(1000);
  });

  it('volta para onde o atendente estava ao sair da conversa (a distância do fim vale)', () => {
    alturaDoConteudo = 2000;
    render(<MessageList mensagens={entre(101, 200)} temMaisAntigas={false} rolagemInicial={{ noFim: false, distanciaDoFim: 300 }} />);

    expect(lista().scrollTop).toBe(2000 - 400 - 300);
  });

  it('avisa a posição de rolagem, para a conversa poder lembrá-la', () => {
    const aoMudar = vi.fn();
    render(<MessageList mensagens={entre(101, 200)} temMaisAntigas={false} aoMudarRolagem={aoMudar} />);

    rolarPara(lista(), 100);

    expect(aoMudar).toHaveBeenLastCalledWith({ noFim: false, distanciaDoFim: 500 });
    rolarPara(lista(), 600);
    expect(aoMudar).toHaveBeenLastCalledWith({ noFim: true, distanciaDoFim: 0 });
  });

  it('a posição guardada acompanha o conteúdo: chegou mensagem enquanto ele lia mais acima, a distância do fim cresce junto', () => {
    const aoMudar = vi.fn();
    const { rerender } = render(<MessageList mensagens={entre(101, 150)} temMaisAntigas={false} aoMudarRolagem={aoMudar} />);
    rolarPara(lista(), 100); // 1000 - 100 - 400 = 500 do fim

    alturaDoConteudo = 1100;
    rerender(<MessageList mensagens={entre(101, 151)} temMaisAntigas={false} aoMudarRolagem={aoMudar} />);

    expect(aoMudar).toHaveBeenLastCalledWith({ noFim: false, distanciaDoFim: 600 });
  });

  it('no fim da conversa, a mensagem nova acompanha a rolagem e não vira aviso', () => {
    const { rerender } = render(<MessageList mensagens={entre(101, 150)} temMaisAntigas={false} />);

    alturaDoConteudo = 1100;
    rerender(<MessageList mensagens={entre(101, 151)} temMaisAntigas={false} />);

    expect(lista().scrollTop).toBe(1100);
    expect(screen.queryByRole('button', { name: /nova/ })).toBeNull();
  });

  it('lendo mais acima, a mensagem nova do cliente NÃO puxa a tela: vira o aviso "1 mensagem nova", que leva ao fim', async () => {
    const usuario = userEvent.setup();
    const { rerender } = render(<MessageList mensagens={entre(101, 150)} temMaisAntigas={false} />);
    rolarPara(lista(), 100);

    alturaDoConteudo = 1100;
    rerender(<MessageList mensagens={entre(101, 151)} temMaisAntigas={false} />);

    expect(lista().scrollTop).toBe(100); // continua onde estava
    const aviso = screen.getByRole('button', { name: '1 mensagem nova ↓' });
    await usuario.click(aviso);
    expect(lista().scrollTop).toBe(1100);
    expect(screen.queryByRole('button', { name: /nova/ })).toBeNull();
  });

  it('o aviso soma as mensagens novas e some quando o atendente chega ao fim rolando', () => {
    const { rerender } = render(<MessageList mensagens={entre(101, 150)} temMaisAntigas={false} />);
    rolarPara(lista(), 100);

    alturaDoConteudo = 1200;
    rerender(<MessageList mensagens={entre(101, 153)} temMaisAntigas={false} />);
    expect(screen.getByRole('button', { name: '3 mensagens novas ↓' })).toBeTruthy();

    rolarPara(lista(), 800); // chegou ao fim (1200 - 800 - 400 = 0)
    expect(screen.queryByRole('button', { name: /nova/ })).toBeNull();
  });

  it('mensagem do próprio atendente ou da IA, com ele lendo mais acima, não conta como "nova"', () => {
    const { rerender } = render(<MessageList mensagens={entre(101, 150)} temMaisAntigas={false} />);
    rolarPara(lista(), 100);
    const minha = mensagem({ messageSeq: 151, direction: 'Outbound', author: 'Human', text: 'minha resposta' });

    alturaDoConteudo = 1100;
    rerender(<MessageList mensagens={[...entre(101, 150), minha]} temMaisAntigas={false} />);

    expect(screen.queryByRole('button', { name: /nova/ })).toBeNull();
  });

  it('o marcador "Novas mensagens" fica antes da primeira mensagem do cliente que ele não tinha visto', () => {
    render(<MessageList mensagens={entre(1, 5)} temMaisAntigas={false} vistoNaAbertura={3} />);

    const marcador = screen.getByRole('separator');
    expect(marcador.textContent).toBe('Novas mensagens');
    expect(marcador.nextElementSibling?.textContent).toContain('m4');
    expect(marcador.previousElementSibling?.textContent).toContain('m3');
  });

  it('sem "visto" anterior, ou com tudo visto, não há marcador', () => {
    const { rerender } = render(<MessageList mensagens={entre(1, 5)} temMaisAntigas={false} />);
    expect(screen.queryByRole('separator')).toBeNull();

    rerender(<MessageList mensagens={entre(1, 5)} temMaisAntigas={false} vistoNaAbertura={5} />);
    expect(screen.queryByRole('separator')).toBeNull();
  });
});

describe('MessageList: "visto até"', () => {
  it('no fim da conversa, com a aba à vista, informa a última mensagem vista', () => {
    const aoVisto = vi.fn();
    render(<MessageList mensagens={entre(1, 5)} temMaisAntigas={false} aoVisto={aoVisto} />);

    expect(aoVisto).toHaveBeenCalledWith(5);
  });

  it('lendo mais acima, não marca como visto; ao chegar ao fim, marca', () => {
    const aoVisto = vi.fn();
    render(<MessageList mensagens={entre(1, 50)} temMaisAntigas={false} aoVisto={aoVisto} />);
    aoVisto.mockClear();

    rolarPara(lista(), 100);
    expect(aoVisto).not.toHaveBeenCalled();

    rolarPara(lista(), 600);
    expect(aoVisto).toHaveBeenCalledWith(50);
  });

  it('com a aba escondida, não marca; quando ela volta a ser vista, marca', () => {
    const aoVisto = vi.fn();
    const escondida = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
    render(<MessageList mensagens={entre(1, 5)} temMaisAntigas={false} aoVisto={aoVisto} />);
    expect(aoVisto).not.toHaveBeenCalled();

    escondida.mockReturnValue(false);
    document.dispatchEvent(new Event('visibilitychange'));

    expect(aoVisto).toHaveBeenCalledWith(5);
  });
});
