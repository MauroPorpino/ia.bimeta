import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { Message, ReplyOptions } from '../api/types';
import { GUID, OUTRO_USUARIO_ID, USUARIO, anexo, chamadasDe, criarFetch, formulariosDe, item, json, mensagem } from '../test/helpers';
import { ConversationPanel } from './ConversationPanel';

const ID = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const ROTA_ITEM = `GET /contact-center/items/${ID}`;
const ROTA_MENSAGENS = `GET /contact-center/items/${ID}/messages`;
const ROTA_OPCOES = `GET /contact-center/items/${ID}/reply-options`;
const ROTA_TEXTO = `POST /contact-center/items/${ID}/reply`;
const ROTA_ARQUIVO = `POST /contact-center/items/${ID}/reply-media`;

const MB = 1024 * 1024;
const HORA = 3_600_000;
const iso = (deslocamentoMs: number) => new Date(Date.now() + deslocamentoMs).toISOString();

const COMIGO = item({ status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });
const ABERTA: ReplyOptions = { freeTextAllowed: true, windowOpen: true, windowHours: 24, lastInboundAt: iso(-1 * HORA), windowClosesAt: iso(23 * HORA), templates: [] };
const FECHADA: ReplyOptions = { freeTextAllowed: false, windowOpen: false, windowHours: 24, lastInboundAt: iso(-26 * HORA), windowClosesAt: iso(-2 * HORA), templates: [] };

function pagina(...itens: Message[]) {
  return json({ items: [...itens].sort((a, b) => b.messageSeq - a.messageSeq), nextCursor: null });
}

function renderizar(propriedades: Partial<Parameters<typeof ConversationPanel>[0]> = {}) {
  const aoErroDeRede = vi.fn();
  const aoErroDeAcao = vi.fn();
  const aoAlterar = vi.fn();
  const resultado = render(
    <ConversationPanel
      api={createApi()}
      itemId={ID}
      me={USUARIO}
      nomeDoContato="Mauro"
      onErroDeRede={aoErroDeRede}
      onErroDeAcao={aoErroDeAcao}
      onAlterado={aoAlterar}
      intervaloMs={60_000}
      {...propriedades}
    />,
  );
  return { ...resultado, aoErroDeRede, aoErroDeAcao, aoAlterar };
}

function simular(rotas: Record<string, Parameters<typeof criarFetch>[0][string]> = {}, opcoes: ReplyOptions = ABERTA) {
  const falso = criarFetch({
    [ROTA_ITEM]: json(COMIGO),
    [ROTA_MENSAGENS]: pagina(mensagem({ text: 'Quero meu resultado', messageSeq: 1 })),
    [ROTA_OPCOES]: json(opcoes),
    ...rotas,
  });
  vi.stubGlobal('fetch', falso);
  return falso;
}

/** Um arquivo sintético; `tamanho` simula o tamanho sem alocar o conteúdo. */
function arquivo(nome: string, tipo: string, opcoes: { tamanho?: number; modificado?: number } = {}): File {
  const f = new File([new Uint8Array([1, 2, 3])], nome, { type: tipo, lastModified: opcoes.modificado ?? 1_700_000_000_000 });
  if (opcoes.tamanho !== undefined) {
    Object.defineProperty(f, 'size', { value: opcoes.tamanho });
  }
  return f;
}

// O filtro `accept` do seletor é do navegador; aqui o teste escolhe qualquer arquivo para provar a validação da própria tela.
const configurarUsuario = () => userEvent.setup({ applyAccept: false });
type Usuario = ReturnType<typeof configurarUsuario>;

async function escolher(usuario: Usuario, f: File) {
  await usuario.upload((await screen.findByLabelText('Anexar arquivo')) as HTMLInputElement, f);
}

const cartao = () => screen.getByRole('group', { name: 'Arquivo a enviar' });
const botaoEnviar = () => within(cartao()).getByRole('button', { name: 'Enviar arquivo' }) as HTMLButtonElement;
const ultimoErroDeAcao = (aoErroDeAcao: ReturnType<typeof vi.fn>) => aoErroDeAcao.mock.calls.at(-1)?.[0] ?? null;

function recusa(motivo: string, detalhe = 'Detalhe do servidor.') {
  return json({ title: 'Regra não atendida', detail: detalhe, status: 422, code: 'MediaRejected', reason: motivo }, 422);
}

describe('ConversationPanel: botão de anexar arquivo', () => {
  it('aparece com a conversa assumida por mim e a janela aberta, com o seletor de arquivo e a dica dos limites', async () => {
    simular();
    renderizar();

    const entrada = (await screen.findByLabelText('Anexar arquivo')) as HTMLInputElement;

    expect(entrada.type).toBe('file');
    const aceitos = entrada.accept.split(',');
    for (const trecho of ['.jpg', '.png', '.m4a', '.ogg', '.pdf', '.txt', '.docx', '.xlsx', '.pptx', 'image/png', 'application/pdf']) {
      expect(aceitos).toContain(trecho);
    }
    expect(entrada.multiple).toBe(false);
    expect(screen.getByText(/Imagem JPEG ou PNG até 5 MB, áudio até 16 MB \(sem legenda\), documento até 20 MB/)).toBeTruthy();
    expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull(); // sem arquivo escolhido, sem cartão
  });

  it('não aparece enquanto a conversa está aguardando, com outro atendente ou quando a janela de 24 horas fechou', async () => {
    simular({ [ROTA_ITEM]: json(item({ status: 'Waiting' })) });
    const aguardando = renderizar();
    await screen.findByRole('button', { name: 'Assumir' });
    expect(screen.queryByLabelText('Anexar arquivo')).toBeNull();
    aguardando.unmount();

    simular({ [ROTA_ITEM]: json(item({ status: 'Assigned', assignedUserId: OUTRO_USUARIO_ID })) });
    const comOutro = renderizar();
    await screen.findByText('Este atendimento está com outro atendente.');
    expect(screen.queryByLabelText('Anexar arquivo')).toBeNull();
    comOutro.unmount();

    simular({}, FECHADA);
    renderizar();
    await screen.findByRole('status', { name: 'Janela de 24 horas fechada' });
    expect(screen.queryByLabelText('Anexar arquivo')).toBeNull();
  });
});

describe('ConversationPanel: enviar um arquivo', () => {
  it('imagem com legenda: mostra o cartão (nome, tipo, tamanho), envia o arquivo e a legenda certos e limpa o cartão', async () => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'Applied', messageId: 'm9', messageSeq: 9 }) });
    const usuario = configurarUsuario();
    const { aoAlterar, aoErroDeAcao } = renderizar();

    await escolher(usuario, arquivo('requisicao.png', 'image/png', { tamanho: 48_213 }));

    expect(within(cartao()).getByText('requisicao.png')).toBeTruthy();
    expect(within(cartao()).getByText('Imagem · 47 KB')).toBeTruthy();
    await usuario.type(within(cartao()).getByLabelText('Legenda (opcional)'), 'Requisição do exame');
    expect(within(cartao()).getByText('19/1024')).toBeTruthy();
    expect((within(cartao()).getByLabelText('Legenda (opcional)') as HTMLTextAreaElement).maxLength).toBe(1024);

    await usuario.click(botaoEnviar());

    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1));
    const { formulario } = formulariosDe(falso, ROTA_ARQUIVO)[0]!;
    expect([...formulario.keys()]).toEqual(['requestId', 'file', 'caption']);
    expect(formulario.get('requestId')).toMatch(GUID);
    const enviado = formulario.get('file') as File;
    expect(enviado.name).toBe('requisicao.png');
    expect(enviado.type).toBe('image/png');
    expect(formulario.get('caption')).toBe('Requisição do exame');
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull());
    await waitFor(() => expect(aoAlterar).toHaveBeenCalled());
    expect(ultimoErroDeAcao(aoErroDeAcao)).toBeNull();
    expect(chamadasDe(falso, ROTA_TEXTO)).toHaveLength(0);
  });

  it('documento sem legenda: o campo caption nem é enviado', async () => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'AlreadyApplied', messageId: 'm9' }) });
    const usuario = configurarUsuario();
    renderizar();

    await escolher(usuario, arquivo('laudo.pdf', 'application/pdf', { tamanho: 3 * MB }));
    expect(within(cartao()).getByText('Documento · 3 MB')).toBeTruthy();
    await usuario.click(botaoEnviar());

    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1));
    expect(formulariosDe(falso, ROTA_ARQUIVO)[0]!.formulario.has('caption')).toBe(false);
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull());
  });

  it('áudio não mostra legenda, avisa que não aceita e nunca envia a legenda de um arquivo anterior', async () => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'Applied' }) });
    const usuario = configurarUsuario();
    renderizar();

    await escolher(usuario, arquivo('foto.png', 'image/png'));
    await usuario.type(within(cartao()).getByLabelText('Legenda (opcional)'), 'legenda da foto');
    await escolher(usuario, arquivo('nota.m4a', 'audio/mp4', { tamanho: 31_000 }));

    expect(within(cartao()).getByText('nota.m4a')).toBeTruthy();
    expect(within(cartao()).getByText('Áudio · 30 KB')).toBeTruthy();
    expect(within(cartao()).queryByLabelText('Legenda (opcional)')).toBeNull();
    expect(within(cartao()).getByText('Áudio não aceita legenda.')).toBeTruthy();

    await usuario.click(botaoEnviar());

    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1));
    const { formulario } = formulariosDe(falso, ROTA_ARQUIVO)[0]!;
    expect((formulario.get('file') as File).name).toBe('nota.m4a');
    expect(formulario.has('caption')).toBe(false);
  });

  it('escolher outro arquivo troca o cartão e "Remover" o esconde, sem enviar nada', async () => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'Applied' }) });
    const usuario = configurarUsuario();
    renderizar();

    await escolher(usuario, arquivo('primeiro.png', 'image/png'));
    await escolher(usuario, arquivo('segundo.pdf', 'application/pdf'));
    expect(within(cartao()).getByText('segundo.pdf')).toBeTruthy();
    expect(within(cartao()).queryByText('primeiro.png')).toBeNull();

    await usuario.click(within(cartao()).getByRole('button', { name: 'Remover' }));

    expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull();
    expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(0);
    // O mesmo arquivo pode ser escolhido de novo depois de removido.
    await escolher(usuario, arquivo('segundo.pdf', 'application/pdf'));
    expect(within(cartao()).getByText('segundo.pdf')).toBeTruthy();
  });

  it('o texto digitado no campo de resposta não se perde nem vai junto com o arquivo: são ações separadas', async () => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'Applied' }), [ROTA_TEXTO]: json({ outcome: 'Applied' }) });
    const usuario = configurarUsuario();
    renderizar();
    const campo = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;
    await usuario.type(campo, 'Olá, segue o resultado');

    await escolher(usuario, arquivo('resultado.pdf', 'application/pdf'));
    await usuario.click(botaoEnviar());

    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1));
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull());
    expect([...formulariosDe(falso, ROTA_ARQUIVO)[0]!.formulario.keys()]).toEqual(['requestId', 'file']); // nenhum campo de texto
    expect(chamadasDe(falso, ROTA_TEXTO)).toHaveLength(0);
    expect(campo.value).toBe('Olá, segue o resultado'); // continua no campo

    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    await waitFor(() => expect(chamadasDe(falso, ROTA_TEXTO)).toHaveLength(1));
    expect(chamadasDe(falso, ROTA_TEXTO)[0]!.corpo!.text).toBe('Olá, segue o resultado');
    expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1);
  });

  it('clique duplo em "Enviar arquivo" envia uma vez só, e o botão mostra o andamento', async () => {
    const resposta: { liberar: (() => void) | null } = { liberar: null };
    const falso = simular({
      [ROTA_ARQUIVO]: () =>
        new Promise<Response>((resolver) => {
          resposta.liberar = () => resolver(json({ outcome: 'Applied' }));
        }),
    });
    const usuario = configurarUsuario();
    renderizar();
    await escolher(usuario, arquivo('foto.png', 'image/png'));
    const botao = botaoEnviar();

    await usuario.dblClick(botao);

    expect(await screen.findByRole('button', { name: 'Enviando arquivo...' })).toBeTruthy();
    expect(botao.disabled).toBe(true);
    expect((within(cartao()).getByRole('button', { name: 'Remover' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByLabelText('Anexar arquivo') as HTMLInputElement).disabled).toBe(true);
    expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1);

    await waitFor(() => expect(resposta.liberar).not.toBeNull());
    resposta.liberar!();
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull());
    expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1);
  });
});

describe('ConversationPanel: validação antecipada (consultiva)', () => {
  it.each([
    ['foto-grande.png', 'image/png', 6 * MB, 'O arquivo tem 6 MB e passa do tamanho máximo para imagem (5 MB).'],
    ['audio-grande.mp3', 'audio/mpeg', 17 * MB, 'O arquivo tem 17 MB e passa do tamanho máximo para áudio (16 MB).'],
    ['laudo-grande.pdf', 'application/pdf', 21 * MB, 'O arquivo tem 21 MB e passa do tamanho máximo para documento (20 MB).'],
    ['programa.exe', 'application/x-msdownload', 100, 'Este tipo de arquivo não é aceito.'],
    ['filme.mp4', 'video/mp4', 100, 'Este tipo de arquivo não é aceito.'],
    ['vazio.png', 'image/png', 0, 'O arquivo está vazio.'],
  ])('%s (%s, %d bytes) bloqueia o envio e explica, sem chamar o servidor', async (nome, tipo, tamanho, explicacao) => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'Applied' }) });
    const usuario = configurarUsuario();
    renderizar();

    await escolher(usuario, arquivo(nome, tipo, { tamanho }));

    const alerta = within(cartao()).getByRole('alert');
    expect(alerta.textContent).toContain(explicacao);
    expect(botaoEnviar().disabled).toBe(true);
    await usuario.click(botaoEnviar());
    expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(0);
    // O arquivo continua escolhido para o atendente ver qual é o problema ou removê-lo.
    expect(within(cartao()).getByText(nome)).toBeTruthy();
  });

  it('tipo fora da lista lista os tipos aceitos', async () => {
    simular();
    const usuario = configurarUsuario();
    renderizar();

    await escolher(usuario, arquivo('programa.exe', 'application/x-msdownload'));

    expect(within(cartao()).getByRole('alert').textContent).toContain('Tipos aceitos: imagem JPEG ou PNG; áudio AAC, AMR, MP3, M4A ou Ogg Opus; documento PDF, TXT, Word, Excel ou PowerPoint.');
  });

  it('sem tipo informado pelo navegador, a extensão decide e o envio é permitido', async () => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'Applied' }) });
    const usuario = configurarUsuario();
    renderizar();

    await escolher(usuario, arquivo('LAUDO.PDF', ''));

    expect(within(cartao()).getByText('Documento · 3 B')).toBeTruthy();
    expect(within(cartao()).queryByRole('alert')).toBeNull();
    expect(botaoEnviar().disabled).toBe(false);
    await usuario.click(botaoEnviar());
    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1));
  });

  it('corrigir o problema (remover e escolher um arquivo válido) libera o envio', async () => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'Applied' }) });
    const usuario = configurarUsuario();
    renderizar();
    await escolher(usuario, arquivo('foto-grande.png', 'image/png', { tamanho: 6 * MB }));
    expect(botaoEnviar().disabled).toBe(true);

    await escolher(usuario, arquivo('foto-pequena.png', 'image/png', { tamanho: 100 * 1024 }));

    expect(within(cartao()).queryByRole('alert')).toBeNull();
    expect(botaoEnviar().disabled).toBe(false);
    await usuario.click(botaoEnviar());
    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1));
  });
});

describe('ConversationPanel: o servidor recusa o arquivo', () => {
  it.each([
    ['too-large', 'O arquivo passa do tamanho máximo para este tipo.'],
    ['type-not-allowed', 'Este tipo de arquivo não é aceito. Tipos aceitos: imagem JPEG ou PNG'],
    ['content-mismatch', 'O conteúdo do arquivo não corresponde ao tipo ou à extensão.'],
    ['audio-codec-unsupported', 'O áudio Ogg precisa ser Opus, em mono.'],
    ['caption-not-supported', 'Este tipo de arquivo não aceita legenda.'],
    ['caption-too-long', 'A legenda passa do limite de 1024 caracteres.'],
    ['empty', 'O arquivo está vazio.'],
    ['malware', 'O arquivo foi bloqueado pela verificação de segurança'],
    ['invalid-shape', 'não estão no formato esperado'],
    ['motivo-novo-do-servidor', 'Detalhe do servidor.'],
  ])('MediaRejected (%s) mostra o motivo ao lado do cartão, sem a faixa de erro, e mantém o arquivo', async (motivo, texto) => {
    simular({ [ROTA_ARQUIVO]: recusa(motivo) });
    const usuario = configurarUsuario();
    const { aoErroDeAcao } = renderizar();
    await escolher(usuario, arquivo('requisicao.png', 'image/png'));

    await usuario.click(botaoEnviar());

    await waitFor(() => expect(within(cartao()).getByRole('alert').textContent).toContain(texto));
    expect(ultimoErroDeAcao(aoErroDeAcao)).toBeNull(); // nada foi para a faixa de erro vermelha
    expect(within(cartao()).getByText('requisicao.png')).toBeTruthy(); // o arquivo continua escolhido
    expect(botaoEnviar().disabled).toBe(false);
  });

  it('a recusa some quando o atendente corrige a legenda, e o pedido seguinte usa outro requestId (rejeição definitiva)', async () => {
    let tentativas = 0;
    const falso = simular({
      [ROTA_ARQUIVO]: () => {
        tentativas += 1;
        return tentativas === 1 ? recusa('caption-too-long') : json({ outcome: 'Applied' });
      },
    });
    const usuario = configurarUsuario();
    renderizar();
    await escolher(usuario, arquivo('requisicao.png', 'image/png'));
    await usuario.type(within(cartao()).getByLabelText('Legenda (opcional)'), 'legenda longa');
    await usuario.click(botaoEnviar());
    await waitFor(() => expect(within(cartao()).getByRole('alert').textContent).toContain('A legenda passa do limite'));

    await usuario.clear(within(cartao()).getByLabelText('Legenda (opcional)'));

    expect(within(cartao()).queryByRole('alert')).toBeNull();
    await usuario.click(botaoEnviar());
    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(2));
    const [primeiro, segundo] = formulariosDe(falso, ROTA_ARQUIVO).map((c) => c.formulario.get('requestId'));
    expect(primeiro).not.toBe(segundo);
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull());
  });

  it('413 (arquivo grande demais para o pedido) explica ao lado do cartão e mantém o arquivo', async () => {
    simular({ [ROTA_ARQUIVO]: new Response(null, { status: 413 }) });
    const usuario = configurarUsuario();
    const { aoErroDeAcao } = renderizar();
    await escolher(usuario, arquivo('requisicao.pdf', 'application/pdf'));

    await usuario.click(botaoEnviar());

    await waitFor(() => expect(within(cartao()).getByRole('alert').textContent).toContain('grande demais'));
    expect(ultimoErroDeAcao(aoErroDeAcao)).toBeNull();
    expect(within(cartao()).getByText('requisicao.pdf')).toBeTruthy();
  });

  it('um erro que o cartão não explica (400) vai para a faixa de erro e o arquivo continua escolhido', async () => {
    simular({ [ROTA_ARQUIVO]: json({ title: 'Dados inválidos', detail: 'Formulário inválido.', status: 400 }, 400) });
    const usuario = configurarUsuario();
    const { aoErroDeAcao } = renderizar();
    await escolher(usuario, arquivo('requisicao.png', 'image/png'));

    await usuario.click(botaoEnviar());

    await waitFor(() => expect(ultimoErroDeAcao(aoErroDeAcao)).toBe('Dados inválidos: Formulário inválido.'));
    expect(within(cartao()).getByText('requisicao.png')).toBeTruthy();
    expect(within(cartao()).queryByRole('alert')).toBeNull();
  });

  it('o atendimento mudou (409): explica e relê, como no texto, e o arquivo continua escolhido', async () => {
    simular({ [ROTA_ARQUIVO]: json({ title: 'Conflito', detail: 'O atendimento mudou.', status: 409, code: 'ItemChanged' }, 409) });
    const usuario = configurarUsuario();
    renderizar();
    await escolher(usuario, arquivo('requisicao.png', 'image/png'));

    await usuario.click(botaoEnviar());

    expect(await screen.findByText(/O atendimento mudou enquanto você agia/)).toBeTruthy();
    expect(within(cartao()).getByText('requisicao.png')).toBeTruthy();
  });
});

describe('ConversationPanel: janela de 24 horas fechada durante o envio do arquivo', () => {
  it('ReplyWindowClosed (422) tem o mesmo tratamento do texto: nota, releitura das opções e o botão de anexar some', async () => {
    let opcoes: ReplyOptions = ABERTA;
    const falso = simular({
      [ROTA_OPCOES]: () => json(opcoes),
      [ROTA_ARQUIVO]: json({ title: 'Regra não atendida', detail: 'A janela de 24 horas para responder livremente a este cliente está fechada.', status: 422, code: 'ReplyWindowClosed' }, 422),
    });
    const usuario = configurarUsuario();
    const { aoErroDeAcao } = renderizar();
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Posso ajudar?');
    await escolher(usuario, arquivo('requisicao.png', 'image/png'));
    opcoes = FECHADA;

    await usuario.click(botaoEnviar());

    expect(await screen.findByText(/A janela de resposta livre deste cliente fechou/)).toBeTruthy();
    expect(ultimoErroDeAcao(aoErroDeAcao)).toBeNull();
    expect(await screen.findByRole('status', { name: 'Janela de 24 horas fechada' })).toBeTruthy();
    expect(screen.queryByLabelText('Anexar arquivo')).toBeNull();
    expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).value).toBe('Posso ajudar?'); // o texto digitado não se perde
    expect(chamadasDe(falso, ROTA_OPCOES).length).toBeGreaterThanOrEqual(2);
  });
});

describe('ConversationPanel: resultado incerto e requestId', () => {
  it('falha de rede: o mesmo arquivo reenviado reaproveita o MESMO requestId', async () => {
    let tentativas = 0;
    const falso = simular({
      [ROTA_ARQUIVO]: () => {
        tentativas += 1;
        if (tentativas === 1) {
          throw new TypeError('rede caiu');
        }
        return json({ outcome: 'Applied' });
      },
    });
    const usuario = configurarUsuario();
    const { aoErroDeAcao } = renderizar();
    await escolher(usuario, arquivo('requisicao.png', 'image/png'));
    await usuario.type(within(cartao()).getByLabelText('Legenda (opcional)'), 'Requisição');

    await usuario.click(botaoEnviar());
    await waitFor(() => expect(ultimoErroDeAcao(aoErroDeAcao)).toMatch(/Sem conexão/));
    expect(within(cartao()).getByText('requisicao.png')).toBeTruthy(); // o arquivo continua escolhido
    await usuario.click(botaoEnviar());

    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(2));
    const [primeiro, segundo] = formulariosDe(falso, ROTA_ARQUIVO).map((c) => c.formulario.get('requestId'));
    expect(primeiro).toMatch(GUID);
    expect(primeiro).toBe(segundo);
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull());
  });

  it('trocar a legenda ou o arquivo depois de um envio incerto gera outro requestId; repetir o mesmo mantém', async () => {
    let tentativas = 0;
    const falso = simular({
      [ROTA_ARQUIVO]: () => {
        tentativas += 1;
        if (tentativas < 4) {
          throw new TypeError('rede caiu');
        }
        return json({ outcome: 'Applied' });
      },
    });
    const usuario = configurarUsuario();
    renderizar();
    await escolher(usuario, arquivo('requisicao.png', 'image/png'));

    await usuario.click(botaoEnviar()); // 1: arquivo A, sem legenda
    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(1));
    await usuario.type(within(cartao()).getByLabelText('Legenda (opcional)'), 'x');
    await waitFor(() => expect(botaoEnviar().disabled).toBe(false));
    await usuario.click(botaoEnviar()); // 2: arquivo A, outra legenda
    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(2));
    await escolher(usuario, arquivo('outra.png', 'image/png'));
    await waitFor(() => expect(botaoEnviar().disabled).toBe(false));
    await usuario.click(botaoEnviar()); // 3: arquivo B, mesma legenda
    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(3));
    await waitFor(() => expect(botaoEnviar().disabled).toBe(false));
    await usuario.click(botaoEnviar()); // 4: arquivo B, mesma legenda de novo: o mesmo pedido

    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(4));
    const ids = formulariosDe(falso, ROTA_ARQUIVO).map((c) => c.formulario.get('requestId'));
    expect(new Set(ids.slice(0, 3)).size).toBe(3);
    expect(ids[3]).toBe(ids[2]);
  });

  it('resposta ainda não confirmada (202 Pending): avisa, mantém o arquivo e o reenvio reaproveita o requestId', async () => {
    let tentativas = 0;
    const falso = simular({
      [ROTA_ARQUIVO]: () => {
        tentativas += 1;
        return tentativas === 1 ? json({ outcome: 'Pending' }, 202) : json({ outcome: 'Applied' });
      },
    });
    const usuario = configurarUsuario();
    renderizar();
    await escolher(usuario, arquivo('requisicao.png', 'image/png'));

    await usuario.click(botaoEnviar());
    expect(await screen.findByText(/o servidor ainda não a confirmou/)).toBeTruthy();
    expect(within(cartao()).getByText('requisicao.png')).toBeTruthy();
    await waitFor(() => expect(botaoEnviar().disabled).toBe(false));
    await usuario.click(botaoEnviar());

    await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(2));
    const [primeiro, segundo] = formulariosDe(falso, ROTA_ARQUIVO).map((c) => c.formulario.get('requestId'));
    expect(primeiro).toBe(segundo);
  });

  it('depois de um envio concluído, mandar o mesmo arquivo de novo é um pedido novo', async () => {
    const falso = simular({ [ROTA_ARQUIVO]: json({ outcome: 'Applied' }) });
    const usuario = configurarUsuario();
    renderizar();

    for (let i = 1; i <= 2; i += 1) {
      await escolher(usuario, arquivo('requisicao.png', 'image/png'));
      await usuario.click(botaoEnviar());
      await waitFor(() => expect(formulariosDe(falso, ROTA_ARQUIVO)).toHaveLength(i));
      await waitFor(() => expect(screen.queryByRole('group', { name: 'Arquivo a enviar' })).toBeNull());
    }

    const [primeiro, segundo] = formulariosDe(falso, ROTA_ARQUIVO).map((c) => c.formulario.get('requestId'));
    expect(primeiro).not.toBe(segundo);
  });
});

describe('ConversationPanel: aviso de falha de envio com arquivo', () => {
  const comFalha = item({ status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString(), deliveryFailures: 1, lastDeliveryFailureAt: new Date().toISOString() });
  const cliente = mensagem({ text: 'Quero meu resultado', messageSeq: 1 });

  it('quando o arquivo do atendente falhou, o aviso também orienta enviar o arquivo de novo e mantém o texto existente', async () => {
    const documento = mensagem({
      direction: 'Outbound',
      author: 'Human',
      kind: 'Document',
      text: null,
      messageSeq: 2,
      deliveryState: 'Failed',
      attachment: anexo({ mimeType: 'application/pdf', fileName: 'laudo.pdf' }),
    });
    simular({ [ROTA_ITEM]: json(comFalha), [ROTA_MENSAGENS]: pagina(cliente, documento) });
    renderizar();

    const aviso = await screen.findByText(/Uma mensagem não foi enviada ao cliente\./);

    expect(aviso.textContent).toContain('Responda de novo.');
    expect(aviso.textContent).toContain('Se for um arquivo, envie o arquivo de novo.');
  });

  it('quando só um texto falhou, o aviso segue como antes, sem falar de arquivo', async () => {
    const texto = mensagem({ direction: 'Outbound', author: 'Human', text: 'Oi', messageSeq: 2, deliveryState: 'Failed' });
    simular({ [ROTA_ITEM]: json(comFalha), [ROTA_MENSAGENS]: pagina(cliente, texto) });
    renderizar();

    const aviso = await screen.findByText(/Uma mensagem não foi enviada ao cliente\./);

    expect(aviso.textContent).toContain('Responda de novo.');
    expect(aviso.textContent).not.toContain('arquivo');
  });
});
