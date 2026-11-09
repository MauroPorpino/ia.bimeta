import { render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Message } from '../api/types';
import { anexo, mensagem } from '../test/helpers';
import { AnexoDaMensagem } from './AnexoDaMensagem';
import { MessageList } from './MessageList';

beforeEach(() => {
  Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: vi.fn(() => 'blob:x') });
  Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: vi.fn() });
});

afterEach(() => {
  vi.restoreAllMocks();
});

/** Uma mensagem de arquivo do ATENDENTE (ADR 0031): saída, autor Human, sem `text`, com o arquivo e a legenda em `attachment`. */
function doAtendente(sobrescrever: Partial<Message> = {}): Message {
  return mensagem({ direction: 'Outbound', author: 'Human', contentState: 'Ready', text: null, deliveryState: 'Sent', ...sobrescrever });
}

const blob = (tipo: string) => new Blob([new Uint8Array([1, 2, 3])], { type: tipo });

describe('MessageList: arquivos enviados pelo atendente', () => {
  it('imagem do atendente aparece do lado do atendente, com a miniatura, a legenda e o estado de entrega, sem aviso de "sem conteúdo"', async () => {
    const imagem = doAtendente({ kind: 'Image', attachment: anexo({ mimeType: 'image/png', fileName: 'requisicao.png', sizeBytes: 48_213, caption: 'Requisição do exame' }) });
    const obter = vi.fn().mockResolvedValue(blob('image/png'));

    render(<MessageList mensagens={[imagem]} temMaisAntigas={false} obterAnexo={obter} />);

    const figura = await screen.findByRole('img');
    expect(figura.getAttribute('alt')).toBe('Requisição do exame');
    expect(obter).toHaveBeenCalledWith(imagem.id);
    const linha = screen.getByRole('listitem');
    expect(linha.getAttribute('data-autor')).toBe('Human');
    expect(linha.getAttribute('data-direcao')).toBe('Outbound');
    expect(linha.className).toContain('enviada');
    expect(within(linha).getByText('Atendente')).toBeTruthy();
    expect(within(linha).getByText('Requisição do exame', { selector: '.anexo-legenda' })).toBeTruthy();
    expect(within(linha).getByText('requisicao.png · 47 KB')).toBeTruthy();
    expect(within(linha).getByText('enviado')).toBeTruthy();
    expect(linha.querySelector('.sem-conteudo')).toBeNull();
    expect(screen.queryByText(/mídia (enviada|recebida)/)).toBeNull();
    expect(screen.queryByText(/conteúdo ainda em processamento|conteúdo não pôde ser lido|mensagem sem texto/)).toBeNull();
    expect(screen.queryByText('Envie o arquivo de novo.')).toBeNull();
  });

  it('áudio do atendente tem o player (carregado a pedido) e documento tem o botão Baixar, cada um com a legenda quando houver', () => {
    const mensagens = [
      doAtendente({ kind: 'Audio', attachment: anexo({ mimeType: 'audio/ogg', fileName: 'nota.ogg', sizeBytes: 31_000 }) }),
      doAtendente({ kind: 'Document', deliveryState: 'Delivered', attachment: anexo({ mimeType: 'application/pdf', fileName: 'laudo.pdf', sizeBytes: 3_500_000, caption: 'Segue o laudo' }) }),
    ];

    render(<MessageList mensagens={mensagens} temMaisAntigas={false} obterAnexo={vi.fn()} />);

    const [audio, documento] = screen.getAllByRole('listitem');
    expect(within(audio!).getByRole('button', { name: 'Carregar áudio' })).toBeTruthy();
    expect(within(audio!).getByText('Áudio enviado (30 KB)')).toBeTruthy(); // do atendente: "enviado", não "recebido"
    expect(within(audio!).getByText('enviado')).toBeTruthy();
    expect(within(documento!).getByText('laudo.pdf')).toBeTruthy();
    expect(within(documento!).getByText('application/pdf · 3,3 MB')).toBeTruthy();
    expect(within(documento!).getByRole('button', { name: 'Baixar' })).toBeTruthy();
    expect(within(documento!).getByText('Segue o laudo')).toBeTruthy();
    expect(within(documento!).getByText('entregue')).toBeTruthy();
    expect(screen.queryByText(/sem conteúdo|mídia (enviada|recebida)|mensagem sem texto/)).toBeNull();
  });

  it('o estado de entrega do arquivo acompanha a mensagem (Enviando, Enviado, Lido)', () => {
    const mensagens = (['Sending', 'Sent', 'Read'] as const).map((estado) =>
      doAtendente({ kind: 'Document', deliveryState: estado, attachment: anexo({ mimeType: 'application/pdf', fileName: `${estado}.pdf` }) }),
    );

    render(<MessageList mensagens={mensagens} temMaisAntigas={false} />);

    expect(screen.getAllByRole('listitem').map((l) => l.querySelector('.entrega')?.getAttribute('data-entrega'))).toEqual(['Sending', 'Sent', 'Read']);
    expect(screen.getByText('enviando')).toBeTruthy();
    expect(screen.getByText('lido')).toBeTruthy();
  });

  it.each([
    ['Failed', 'falha no envio', 'Envie o arquivo de novo.'],
    ['Dead', 'envio abandonado', 'Envie o arquivo de novo.'],
    ['Expired', 'expirado', 'Envie o arquivo de novo.'],
    ['SendUnknown', 'envio de resultado incerto', 'envie o arquivo de novo'],
  ] as const)('arquivo do atendente em %s mostra "%s" e orienta enviar o arquivo de novo', (estado, rotulo, orientacao) => {
    const falhou = doAtendente({ kind: 'Document', deliveryState: estado, attachment: anexo({ mimeType: 'application/pdf', fileName: 'laudo.pdf' }) });

    render(<MessageList mensagens={[falhou]} temMaisAntigas={false} />);

    const linha = screen.getByRole('listitem');
    expect(within(linha).getByText(rotulo)).toBeTruthy();
    expect(linha.querySelector('.entrega-orientacao')?.textContent).toContain(orientacao);
  });

  it('a orientação é só do arquivo: texto do atendente que falhou continua sem ela', () => {
    const texto = doAtendente({ kind: 'Text', text: 'Oi', deliveryState: 'Failed' });

    render(<MessageList mensagens={[texto]} temMaisAntigas={false} />);

    expect(screen.getByText('falha no envio')).toBeTruthy();
    expect(screen.queryByText(/envie o arquivo de novo/i)).toBeNull();
  });

  it('o arquivo do cliente não ganha a orientação de reenvio, mesmo indisponível', () => {
    render(<MessageList mensagens={[mensagem({ kind: 'Image', text: null, attachment: anexo({ status: 'Unavailable', failureCode: 'expired', mimeType: null, fileName: null, sizeBytes: null }) })]} temMaisAntigas={false} />);

    expect(screen.queryByText(/envie o arquivo de novo/i)).toBeNull();
  });

  it('depois do prazo de guarda (retention) mostra o motivo, o nome e a legenda, sem oferecer o arquivo', () => {
    const obter = vi.fn();
    const apagado = doAtendente({
      kind: 'Document',
      attachment: anexo({ status: 'Unavailable', failureCode: 'retention', mimeType: null, sizeBytes: null, fileName: 'laudo.pdf', caption: 'Segue o laudo' }),
    });

    render(<MessageList mensagens={[apagado]} temMaisAntigas={false} obterAnexo={obter} />);

    const linha = screen.getByRole('listitem');
    const estado = within(linha).getByRole('status');
    expect(estado.textContent).toContain('Arquivo indisponível.');
    expect(estado.textContent).toContain('O arquivo foi apagado do servidor depois do prazo de guarda; o nome e a legenda continuam aqui.');
    expect(within(linha).getByText('laudo.pdf')).toBeTruthy();
    expect(within(linha).getByText('Segue o laudo')).toBeTruthy();
    expect(within(linha).queryByRole('button')).toBeNull();
    expect(obter).not.toHaveBeenCalled();
    expect(linha.querySelector('.sem-conteudo')).toBeNull();
    expect(linha.querySelector('.entrega-orientacao')).toBeNull(); // foi entregue: não há o que reenviar
  });

  it('mídia de saída sem arquivo registrado diz "enviada" (e não "recebida")', () => {
    render(<MessageList mensagens={[doAtendente({ kind: 'Image' })]} temMaisAntigas={false} />);

    expect(screen.getByText('mídia enviada (arquivo não disponível)')).toBeTruthy();
    expect(screen.queryByText(/mídia recebida/)).toBeNull();
  });
});

describe('AnexoDaMensagem: arquivo enviado pelo atendente', () => {
  it('retention: o motivo aparece, o nome e a legenda continuam e nenhum arquivo é baixado', () => {
    const obter = vi.fn();

    render(
      <AnexoDaMensagem
        anexo={anexo({ status: 'Unavailable', failureCode: 'retention', mimeType: null, sizeBytes: null, fileName: 'requisicao.png', caption: 'Requisição' })}
        tipo="Image"
        obter={obter}
        enviada
      />,
    );

    expect(screen.getByRole('status').textContent).toBe('Arquivo indisponível. O arquivo foi apagado do servidor depois do prazo de guarda; o nome e a legenda continuam aqui.');
    expect(screen.getByText('requisicao.png')).toBeTruthy();
    expect(screen.getByText('Requisição')).toBeTruthy();
    expect(screen.queryByRole('img')).toBeNull();
    expect(obter).not.toHaveBeenCalled();
  });

  it('os motivos que já existiam não mudam (sem nome de arquivo, nada a mais aparece)', () => {
    render(<AnexoDaMensagem anexo={anexo({ status: 'Unavailable', failureCode: 'expired', mimeType: null, fileName: null, sizeBytes: null })} tipo="Image" />);

    expect(screen.getByRole('status').textContent).toContain('expirou no WhatsApp');
    expect(document.querySelector('.anexo-nome-indisponivel')).toBeNull();
  });

  it('imagem enviada usa "enviada" nos textos de apoio e baixa a miniatura sozinha', async () => {
    const obter = vi.fn().mockResolvedValue(blob('image/png'));

    render(<AnexoDaMensagem anexo={anexo({ fileName: null })} tipo="Image" obter={obter} enviada />);

    expect((await screen.findByRole('img')).getAttribute('alt')).toBe('Imagem enviada ao cliente');
  });
});
