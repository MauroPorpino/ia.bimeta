import { describe, expect, it } from 'vitest';
import type { Attachment } from './api/types';
import { ACCEPT_DE_ARQUIVOS, LIMITE_POR_TIPO, chaveDoArquivo, temArquivoSemEnvio, textoDaRecusaDoArquivo, tipoDoArquivo, validarArquivo } from './arquivos';
import { anexo, mensagem } from './test/helpers';

const MB = 1024 * 1024;

describe('tipo do arquivo (feedback antecipado)', () => {
  it.each([
    ['foto.png', 'image/png', 'Image'],
    ['foto.jpg', 'image/jpeg', 'Image'],
    ['nota.m4a', 'audio/x-m4a', 'Audio'],
    ['nota.ogg', 'audio/ogg; codecs=opus', 'Audio'],
    ['laudo.pdf', 'application/pdf', 'Document'],
    ['planilha.xlsx', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 'Document'],
    ['texto.txt', 'text/plain', 'Document'],
  ])('%s (%s) é %s pelo tipo que o navegador informou', (nome, tipo, esperado) => {
    expect(tipoDoArquivo({ name: nome, type: tipo })).toBe(esperado);
  });

  it('sem tipo do navegador (ou com um tipo genérico), decide pela extensão, sem diferenciar maiúsculas', () => {
    expect(tipoDoArquivo({ name: 'FOTO.JPG', type: '' })).toBe('Image');
    expect(tipoDoArquivo({ name: 'nota.AMR', type: 'application/octet-stream' })).toBe('Audio');
    expect(tipoDoArquivo({ name: 'apresentacao.pptx', type: '' })).toBe('Document');
    expect(tipoDoArquivo({ name: 'relatorio.final.doc', type: '' })).toBe('Document');
  });

  it('tipos e extensões fora da lista não são aceitos', () => {
    expect(tipoDoArquivo({ name: 'filme.mp4', type: 'video/mp4' })).toBeNull();
    expect(tipoDoArquivo({ name: 'programa.exe', type: 'application/x-msdownload' })).toBeNull();
    expect(tipoDoArquivo({ name: 'figura.gif', type: 'image/gif' })).toBeNull();
    expect(tipoDoArquivo({ name: 'semextensao', type: '' })).toBeNull();
    expect(tipoDoArquivo({ name: 'termina.', type: '' })).toBeNull();
    expect(tipoDoArquivo({ name: 'constructor', type: '' })).toBeNull();
  });

  it('o accept do seletor traz as extensões e os tipos aceitos', () => {
    for (const trecho of ['.jpg', '.jpeg', '.png', '.m4a', '.ogg', '.pdf', '.txt', '.docx', '.xlsx', '.pptx', 'image/png', 'application/pdf']) {
      expect(ACCEPT_DE_ARQUIVOS.split(',')).toContain(trecho);
    }
  });
});

describe('validação antecipada do arquivo', () => {
  const imagem = (size: number) => ({ name: 'foto.png', type: 'image/png', size });

  it('arquivo vazio, tipo fora da lista e tamanho acima do máximo do tipo são recusados, nessa ordem, com a explicação', () => {
    expect(validarArquivo(imagem(0), '')).toMatchObject({ valido: false, motivo: 'empty' });
    expect(validarArquivo({ name: 'a.exe', type: '', size: 0 }, '')).toMatchObject({ valido: false, motivo: 'empty' });
    const tipo = validarArquivo({ name: 'a.exe', type: 'application/x-msdownload', size: 10 }, '');
    expect(tipo).toMatchObject({ valido: false, motivo: 'type-not-allowed' });
    expect(tipo.valido === false && tipo.texto).toContain('Tipos aceitos');
    const grande = validarArquivo(imagem(6 * MB), '');
    expect(grande).toMatchObject({ valido: false, motivo: 'too-large' });
    expect(grande.valido === false && grande.texto).toBe('O arquivo tem 6 MB e passa do tamanho máximo para imagem (5 MB).');
  });

  it('os limites são 5 MB (imagem), 16 MB (áudio) e 20 MB (documento), inclusive', () => {
    expect(LIMITE_POR_TIPO).toEqual({ Image: 5 * MB, Audio: 16 * MB, Document: 20 * MB });
    expect(validarArquivo(imagem(5 * MB), '')).toEqual({ valido: true, tipo: 'Image' });
    expect(validarArquivo({ name: 'a.mp3', type: 'audio/mpeg', size: 16 * MB + 1 }, '')).toMatchObject({ valido: false, motivo: 'too-large' });
    expect(validarArquivo({ name: 'a.pdf', type: 'application/pdf', size: 20 * MB }, '')).toEqual({ valido: true, tipo: 'Document' });
    expect(validarArquivo({ name: 'a.pdf', type: 'application/pdf', size: 20 * MB + 1 }, '')).toMatchObject({ valido: false, motivo: 'too-large' });
  });

  it('a legenda passa de 1024 caracteres só conta para imagem e documento (o áudio não tem legenda)', () => {
    const longa = 'x'.repeat(1025);

    expect(validarArquivo(imagem(10), 'x'.repeat(1024))).toEqual({ valido: true, tipo: 'Image' });
    expect(validarArquivo(imagem(10), longa)).toMatchObject({ valido: false, motivo: 'caption-too-long' });
    expect(validarArquivo({ name: 'a.mp3', type: 'audio/mpeg', size: 10 }, longa)).toEqual({ valido: true, tipo: 'Audio' });
  });
});

describe('texto da recusa do servidor', () => {
  it.each([
    ['too-large', 'passa do tamanho máximo para este tipo'],
    ['type-not-allowed', 'Tipos aceitos'],
    ['content-mismatch', 'não corresponde ao tipo ou à extensão'],
    ['audio-codec-unsupported', 'Opus, em mono'],
    ['caption-not-supported', 'não aceita legenda'],
    ['caption-too-long', '1024 caracteres'],
    ['empty', 'está vazio'],
    ['malware', 'verificação de segurança'],
    ['invalid-shape', 'formato esperado'],
  ])('%s explica o motivo em português', (motivo, trecho) => {
    expect(textoDaRecusaDoArquivo(motivo, 'detalhe do servidor')).toContain(trecho);
  });

  it('um motivo desconhecido usa o texto do servidor, e sem ele uma frase genérica', () => {
    expect(textoDaRecusaDoArquivo('novo-motivo', 'Texto do servidor.')).toBe('Texto do servidor.');
    expect(textoDaRecusaDoArquivo(null, null)).toBe('O servidor recusou o arquivo.');
  });
});

describe('identidade do envio e aviso de arquivo sem envio', () => {
  it('a chave junta nome, tamanho, data de modificação e legenda: trocar qualquer um é outro pedido', () => {
    const base = { name: 'a.png', size: 10, lastModified: 5 };

    expect(chaveDoArquivo(base, 'x')).toBe(chaveDoArquivo({ ...base }, 'x'));
    expect(chaveDoArquivo(base, 'x')).not.toBe(chaveDoArquivo(base, 'y'));
    expect(chaveDoArquivo(base, null)).toBe(chaveDoArquivo(base, ''));
    expect(chaveDoArquivo(base, 'x')).not.toBe(chaveDoArquivo({ ...base, name: 'b.png' }, 'x'));
    expect(chaveDoArquivo(base, 'x')).not.toBe(chaveDoArquivo({ ...base, size: 11 }, 'x'));
    expect(chaveDoArquivo(base, 'x')).not.toBe(chaveDoArquivo({ ...base, lastModified: 6 }, 'x'));
  });

  const arquivoDoAtendente = (estado: 'Sent' | 'Failed' | 'Dead' | 'Expired', seq: number, sobrescrever: Partial<Attachment> = {}) =>
    mensagem({ direction: 'Outbound', author: 'Human', kind: 'Image', text: null, messageSeq: seq, deliveryState: estado, attachment: anexo(sobrescrever) });

  it('só avisa quando um arquivo do atendente falhou e nada do atendente saiu depois dele', () => {
    expect(temArquivoSemEnvio([arquivoDoAtendente('Failed', 5)])).toBe(true);
    expect(temArquivoSemEnvio([arquivoDoAtendente('Dead', 5)])).toBe(true);
    expect(temArquivoSemEnvio([arquivoDoAtendente('Expired', 5)])).toBe(true);
    expect(temArquivoSemEnvio([arquivoDoAtendente('Sent', 5)])).toBe(false);
    // O atendente respondeu de novo e a resposta saiu: a falha antiga já não pede nada.
    expect(temArquivoSemEnvio([arquivoDoAtendente('Failed', 5), mensagem({ direction: 'Outbound', author: 'Human', text: 'de novo', messageSeq: 6, deliveryState: 'Delivered' })])).toBe(false);
    // Falha de texto, sem arquivo: o aviso continua só com "Responda de novo".
    expect(temArquivoSemEnvio([mensagem({ direction: 'Outbound', author: 'Human', text: 'oi', messageSeq: 7, deliveryState: 'Failed' })])).toBe(false);
    // Mensagem do cliente com arquivo não conta.
    expect(temArquivoSemEnvio([mensagem({ kind: 'Image', text: null, messageSeq: 8, attachment: anexo() })])).toBe(false);
  });
});
