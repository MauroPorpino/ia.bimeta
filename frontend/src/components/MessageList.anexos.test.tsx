import { render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { anexo, mensagem } from '../test/helpers';
import { MessageList } from './MessageList';

beforeEach(() => {
  Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: vi.fn(() => 'blob:x') });
  Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: vi.fn() });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('MessageList: mídia com arquivo', () => {
  it('mostra o arquivo no lugar do aviso genérico de "mídia sem conteúdo" e pede o arquivo da mensagem certa', async () => {
    const imagem = mensagem({ kind: 'Image', contentState: 'Pending', text: null, attachment: anexo({ caption: 'Requisição' }) });
    const obter = vi.fn().mockResolvedValue(new Blob([new Uint8Array([1])], { type: 'image/png' }));

    render(<MessageList mensagens={[imagem]} temMaisAntigas={false} obterAnexo={obter} />);

    expect(await screen.findByRole('img')).toBeTruthy();
    expect(obter).toHaveBeenCalledWith(imagem.id);
    expect(screen.queryByText(/conteúdo ainda em processamento/)).toBeNull();
    expect(screen.queryByText(/mídia recebida/)).toBeNull();
  });

  it('mídia sem arquivo registrado (mensagem antiga) continua com o aviso de arquivo não disponível', () => {
    render(<MessageList mensagens={[mensagem({ kind: 'Document', contentState: 'Pending', text: null })]} temMaisAntigas={false} />);

    expect(screen.getByText('conteúdo ainda em processamento')).toBeTruthy();
  });

  it('arquivo pendente, indisponível e recusado aparecem no histórico com o estado de cada um', () => {
    const mensagens = [
      mensagem({ kind: 'Image', contentState: 'Pending', text: null, attachment: anexo({ status: 'Pending', mimeType: null, fileName: null, sizeBytes: null }) }),
      mensagem({ kind: 'Audio', contentState: 'Pending', text: null, attachment: anexo({ status: 'Unavailable', failureCode: 'expired', mimeType: null, fileName: null, sizeBytes: null }) }),
      mensagem({ kind: 'Document', contentState: 'Pending', text: null, attachment: anexo({ status: 'Rejected', failureCode: 'content-mismatch', mimeType: null, fileName: null, sizeBytes: null }) }),
    ];

    render(<MessageList mensagens={mensagens} temMaisAntigas={false} />);

    expect(screen.getByText('Recebendo imagem...')).toBeTruthy();
    expect(screen.getByText(/expirou no WhatsApp/)).toBeTruthy();
    expect(screen.getByText(/não corresponde ao tipo/)).toBeTruthy();
  });

  it('o texto que já existe (por exemplo, uma transcrição futura) continua aparecendo junto do arquivo', () => {
    render(
      <MessageList
        mensagens={[mensagem({ kind: 'Audio', contentState: 'Ready', text: 'Preciso remarcar o exame', attachment: anexo({ mimeType: 'audio/ogg' }) })]}
        temMaisAntigas={false}
      />,
    );

    expect(screen.getByText('Preciso remarcar o exame')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Carregar áudio' })).toBeTruthy();
  });
});
