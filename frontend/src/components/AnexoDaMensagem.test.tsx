import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/client';
import { anexo } from '../test/helpers';
import { AnexoDaMensagem } from './AnexoDaMensagem';

let contador = 0;
let criar: ReturnType<typeof vi.fn>;
let revogar: ReturnType<typeof vi.fn>;

beforeEach(() => {
  contador = 0;
  // O jsdom não implementa URL.createObjectURL: o arquivo local é simulado.
  criar = vi.fn(() => `blob:arquivo-${(contador += 1)}`);
  revogar = vi.fn();
  Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: criar });
  Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: revogar });
});

afterEach(() => {
  vi.restoreAllMocks();
});

const blob = (tipo = 'image/png') => new Blob([new Uint8Array([1, 2, 3])], { type: tipo });

describe('AnexoDaMensagem: estados do arquivo', () => {
  it('pendente: mostra "recebendo" pelo tipo da mídia e não baixa nada', () => {
    const obter = vi.fn();

    render(<AnexoDaMensagem anexo={anexo({ status: 'Pending', mimeType: null, fileName: null, sizeBytes: null })} tipo="Audio" obter={obter} />);

    expect(screen.getByRole('status').textContent).toBe('Recebendo áudio...');
    expect(obter).not.toHaveBeenCalled();
  });

  it('indisponível: explica o motivo (expirada) sem oferecer arquivo', () => {
    const obter = vi.fn();

    render(<AnexoDaMensagem anexo={anexo({ status: 'Unavailable', failureCode: 'expired', mimeType: null, fileName: null, sizeBytes: null })} tipo="Image" obter={obter} />);

    expect(screen.getByRole('status').textContent).toContain('Arquivo indisponível.');
    expect(screen.getByRole('status').textContent).toContain('expirou no WhatsApp');
    expect(screen.queryByRole('img')).toBeNull();
    expect(screen.queryByRole('button')).toBeNull();
    expect(obter).not.toHaveBeenCalled();
  });

  it.each([
    ['too-large', 'maior que o limite'],
    ['type-not-allowed', 'não é permitido'],
    ['content-mismatch', 'não corresponde ao tipo'],
    ['hash-mismatch', 'integridade'],
    ['empty', 'vazio'],
    [null, 'recusado pela política de segurança'],
  ])('recusado (%s): diz que o arquivo foi bloqueado e nunca o abre', (codigo, trecho) => {
    const obter = vi.fn();

    render(<AnexoDaMensagem anexo={anexo({ status: 'Rejected', failureCode: codigo, mimeType: null, fileName: null, sizeBytes: null })} tipo="Document" obter={obter} />);

    expect(screen.getByRole('status').textContent).toContain('Arquivo bloqueado.');
    expect(screen.getByRole('status').textContent).toContain(trecho);
    expect(screen.queryByRole('button')).toBeNull();
    expect(obter).not.toHaveBeenCalled();
  });

  it('mostra a legenda do cliente em qualquer estado', () => {
    render(<AnexoDaMensagem anexo={anexo({ status: 'Pending', caption: 'Segue a requisição' })} tipo="Image" />);

    expect(screen.getByText('Segue a requisição')).toBeTruthy();
  });
});

describe('AnexoDaMensagem: imagem', () => {
  it('baixa sozinha, mostra a miniatura com o texto alternativo e libera o arquivo local ao sair', async () => {
    const obter = vi.fn().mockResolvedValue(blob());

    const { unmount } = render(<AnexoDaMensagem anexo={anexo({ caption: 'Foto da requisição', fileName: 'requisicao.png', sizeBytes: 48_213 })} tipo="Image" obter={obter} />);

    const imagem = await screen.findByRole('img');
    expect(imagem.getAttribute('src')).toBe('blob:arquivo-1');
    expect(imagem.getAttribute('alt')).toBe('Foto da requisição');
    expect(screen.getByText('requisicao.png · 47 KB')).toBeTruthy();
    expect(obter).toHaveBeenCalledTimes(1);

    unmount();
    expect(revogar).toHaveBeenCalledWith('blob:arquivo-1');
  });

  it('clicar na miniatura amplia e clicar de novo reduz, sem baixar outra vez', async () => {
    const obter = vi.fn().mockResolvedValue(blob());
    const usuario = userEvent.setup();
    render(<AnexoDaMensagem anexo={anexo()} tipo="Image" obter={obter} />);
    await screen.findByRole('img');

    await usuario.click(screen.getByRole('button', { name: 'Ampliar imagem' }));
    expect(screen.getByRole('button', { name: 'Reduzir imagem' }).getAttribute('aria-pressed')).toBe('true');
    await usuario.click(screen.getByRole('button', { name: 'Reduzir imagem' }));

    expect(screen.getByRole('button', { name: 'Ampliar imagem' })).toBeTruthy();
    expect(obter).toHaveBeenCalledTimes(1);
  });

  it('falha ao baixar: mostra o erro e "tentar de novo" baixa de novo', async () => {
    const obter = vi.fn().mockRejectedValueOnce(new ApiError(0, 'Sem conexão com a API', 'sem rede')).mockResolvedValue(blob());
    const usuario = userEvent.setup();
    render(<AnexoDaMensagem anexo={anexo()} tipo="Image" obter={obter} />);

    expect((await screen.findByRole('alert')).textContent).toContain('Não foi possível carregar a imagem.');
    await usuario.click(screen.getByRole('button', { name: 'Tentar de novo' }));

    expect((await screen.findByRole('img')).getAttribute('src')).toBe('blob:arquivo-1');
    expect(obter).toHaveBeenCalledTimes(2);
  });

  it('muitas imagens: só algumas baixam ao mesmo tempo e as outras esperam a vez', async () => {
    let simultaneas = 0;
    let maximo = 0;
    const liberacoes: Array<() => void> = [];
    const obter = vi.fn(
      () =>
        new Promise<Blob>((resolver) => {
          simultaneas += 1;
          maximo = Math.max(maximo, simultaneas);
          liberacoes.push(() => {
            simultaneas -= 1;
            resolver(blob());
          });
        }),
    );

    render(
      <>
        {Array.from({ length: 7 }, (_, i) => (
          <AnexoDaMensagem key={i} anexo={anexo()} tipo="Image" obter={obter} />
        ))}
      </>,
    );

    await waitFor(() => expect(liberacoes.length).toBe(4));
    expect(maximo).toBe(4);
    while (liberacoes.length > 0) {
      liberacoes.shift()?.();
      await new Promise((r) => setTimeout(r, 0));
    }
    await waitFor(() => expect(screen.getAllByRole('img')).toHaveLength(7));
    expect(maximo).toBeLessThanOrEqual(4);
  });
});

describe('AnexoDaMensagem: áudio', () => {
  it('só baixa quando o atendente pede e então mostra o player nativo', async () => {
    const obter = vi.fn().mockResolvedValue(blob('audio/ogg'));
    const usuario = userEvent.setup();
    const { container } = render(<AnexoDaMensagem anexo={anexo({ mimeType: 'audio/ogg', fileName: 'arquivo.ogg', sizeBytes: 31_000 })} tipo="Audio" obter={obter} />);

    expect(obter).not.toHaveBeenCalled();
    expect(container.querySelector('audio')).toBeNull();
    expect(screen.getByText('Áudio recebido (30 KB)')).toBeTruthy();

    await usuario.click(screen.getByRole('button', { name: 'Carregar áudio' }));

    await waitFor(() => expect(container.querySelector('audio')).not.toBeNull());
    const player = container.querySelector('audio')!;
    expect(player.getAttribute('src')).toBe('blob:arquivo-1');
    expect(player.hasAttribute('controls')).toBe(true);
    expect(obter).toHaveBeenCalledTimes(1);
  });

  it('falha ao carregar o áudio: avisa e permite tentar de novo', async () => {
    const obter = vi.fn().mockRejectedValueOnce(new ApiError(404, 'Não encontrado')).mockResolvedValue(blob('audio/ogg'));
    const usuario = userEvent.setup();
    const { container } = render(<AnexoDaMensagem anexo={anexo({ mimeType: 'audio/ogg', fileName: 'arquivo.ogg' })} tipo="Audio" obter={obter} />);

    await usuario.click(screen.getByRole('button', { name: 'Carregar áudio' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Não foi possível carregar o áudio.');
    await usuario.click(screen.getByRole('button', { name: 'Tentar carregar o áudio de novo' }));

    await waitFor(() => expect(container.querySelector('audio')).not.toBeNull());
  });

  it('sem como baixar (sem sessão), o botão fica desativado', () => {
    render(<AnexoDaMensagem anexo={anexo({ mimeType: 'audio/ogg' })} tipo="Audio" />);

    expect((screen.getByRole('button', { name: 'Carregar áudio' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('AnexoDaMensagem: documento', () => {
  it('mostra nome, tipo e tamanho e só baixa ao clicar, salvando com o nome do arquivo', async () => {
    const obter = vi.fn().mockResolvedValue(blob('application/pdf'));
    const usuario = userEvent.setup();
    const cliques: Array<{ nome: string; href: string }> = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      cliques.push({ nome: this.download, href: this.href });
    });
    render(<AnexoDaMensagem anexo={anexo({ mimeType: 'application/pdf', fileName: 'laudo anterior.pdf', sizeBytes: 3_500_000 })} tipo="Document" obter={obter} />);

    expect(screen.getByText('laudo anterior.pdf')).toBeTruthy();
    expect(screen.getByText('application/pdf · 3,3 MB')).toBeTruthy();
    expect(obter).not.toHaveBeenCalled();

    await usuario.click(screen.getByRole('button', { name: 'Baixar' }));

    await waitFor(() => expect(cliques).toHaveLength(1));
    expect(cliques[0]).toEqual({ nome: 'laudo anterior.pdf', href: 'blob:arquivo-1' });
    expect(obter).toHaveBeenCalledTimes(1);
    // O link temporário não fica na página.
    expect(document.querySelector('a[download]')).toBeNull();
  });

  it('falha ao baixar o documento: mostra o erro e o botão volta', async () => {
    const obter = vi.fn().mockRejectedValue(new ApiError(404, 'Não encontrado'));
    const usuario = userEvent.setup();
    render(<AnexoDaMensagem anexo={anexo({ mimeType: 'application/pdf', fileName: 'a.pdf' })} tipo="Document" obter={obter} />);

    await usuario.click(screen.getByRole('button', { name: 'Baixar' }));

    expect((await screen.findByRole('alert')).textContent).toContain('Não foi possível baixar o documento.');
    expect((screen.getByRole('button', { name: 'Baixar' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('um tipo que não é imagem nem áudio nunca é exibido na página, só baixado', async () => {
    const { container } = render(<AnexoDaMensagem anexo={anexo({ mimeType: 'text/plain', fileName: 'nota.txt' })} tipo="Document" obter={vi.fn()} />);

    expect(container.querySelector('img, audio, video, iframe, embed, object')).toBeNull();
    expect(screen.getByRole('button', { name: 'Baixar' })).toBeTruthy();
  });
});
