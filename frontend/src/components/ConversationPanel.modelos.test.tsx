import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { Message, ReplyOptions, ReplyTemplate } from '../api/types';
import { janelaDeResposta } from '../format';
import { GUID, USUARIO, chamadasDe, criarFetch, item, json, mensagem, problema } from '../test/helpers';
import { ConversationPanel } from './ConversationPanel';
import { montarPrevia, validarParametro } from './ModeloDeMensagem';

const ID = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const ROTA_ITEM = `GET /contact-center/items/${ID}`;
const ROTA_MENSAGENS = `GET /contact-center/items/${ID}/messages`;
const ROTA_OPCOES = `GET /contact-center/items/${ID}/reply-options`;
const ROTA_MODELO = `POST /contact-center/items/${ID}/reply-template`;
const ROTA_TEXTO = `POST /contact-center/items/${ID}/reply`;

const COMIGO = item({ status: 'Assigned', assignedUserId: USUARIO.id, assignedAt: new Date().toISOString() });

const HORA = 3_600_000;
const iso = (deslocamentoMs: number) => new Date(Date.now() + deslocamentoMs).toISOString();

const RESULTADO: ReplyTemplate = {
  id: '0197a1b2-0000-7000-8000-000000000001',
  name: 'resultado_pronto',
  language: 'pt_BR',
  category: 'Utility',
  headerText: null,
  bodyText: 'Olá {{1}}, o resultado do exame {{2}} já está disponível.',
  footerText: 'Laboratório Exemplo',
  parameters: [
    { key: 'body.1', component: 'body', label: 'Variável 1', example: 'Mauro', maxLength: 1024 },
    { key: 'body.2', component: 'body', label: 'Variável 2', example: 'hemograma', maxLength: 1024 },
  ],
};

const SIMPLES: ReplyTemplate = {
  id: '0197a1b2-0000-7000-8000-000000000002',
  name: 'retomar_atendimento',
  language: 'pt_BR',
  category: 'Utility',
  headerText: null,
  bodyText: 'Seu atendimento foi retomado.',
  footerText: null,
  parameters: [],
};

const FECHADA: ReplyOptions = {
  freeTextAllowed: false,
  windowOpen: false,
  windowHours: 24,
  lastInboundAt: iso(-26 * HORA),
  windowClosesAt: iso(-2 * HORA),
  templates: [RESULTADO, SIMPLES],
};

const ABERTA: ReplyOptions = {
  freeTextAllowed: true,
  windowOpen: true,
  windowHours: 24,
  lastInboundAt: iso(-1 * HORA),
  windowClosesAt: iso(23 * HORA),
  templates: [RESULTADO, SIMPLES],
};

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

function simular(rotas: Record<string, Parameters<typeof criarFetch>[0][string]>) {
  const falso = criarFetch({ [ROTA_ITEM]: json(COMIGO), [ROTA_MENSAGENS]: pagina(mensagem({ text: 'Quero meu resultado', messageSeq: 1 })), ...rotas });
  vi.stubGlobal('fetch', falso);
  return falso;
}

const ultimoErroDeAcao = (aoErroDeAcao: ReturnType<typeof vi.fn>) => aoErroDeAcao.mock.calls.at(-1)?.[0] ?? null;

describe('ConversationPanel: janela de 24 horas fechada', () => {
  it('mostra claramente que a janela fechou, bloqueia o texto livre e oferece os modelos aprovados', async () => {
    const falso = simular({ [ROTA_OPCOES]: json(FECHADA) });
    renderizar();

    const aviso = await screen.findByRole('status', { name: 'Janela de 24 horas fechada' });
    expect(within(aviso).getByText(/Janela de 24 horas fechada/)).toBeTruthy();
    expect(within(aviso).getByText(/A última mensagem do cliente foi há 1 d/)).toBeTruthy();
    expect(within(aviso).getByText(/texto livre fica bloqueado/)).toBeTruthy();

    const texto = screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement;
    expect(texto.disabled).toBe(true);
    expect(texto.placeholder).toMatch(/Texto livre indisponível/);
    expect((screen.getByRole('button', { name: 'Responder' }) as HTMLButtonElement).disabled).toBe(true);

    const escolha = screen.getByLabelText('Modelo de mensagem') as HTMLSelectElement;
    expect(Array.from(escolha.options).map((o) => o.textContent)).toEqual(['Escolha um modelo aprovado...', 'resultado_pronto (pt_BR)', 'retomar_atendimento (pt_BR)']);
    expect(chamadasDe(falso, ROTA_TEXTO)).toHaveLength(0);
  });

  it('escolher o modelo mostra os campos que a aprovação definiu e a prévia do que o cliente vai receber', async () => {
    simular({ [ROTA_OPCOES]: json(FECHADA) });
    const usuario = userEvent.setup();
    renderizar();

    await usuario.selectOptions(await screen.findByLabelText('Modelo de mensagem'), 'resultado_pronto (pt_BR)');

    const variavel1 = screen.getByLabelText('Variável 1') as HTMLInputElement;
    const variavel2 = screen.getByLabelText('Variável 2') as HTMLInputElement;
    expect(variavel1.placeholder).toBe('Ex.: Mauro');
    expect(variavel2.placeholder).toBe('Ex.: hemograma');
    const previa = screen.getByLabelText('Prévia do que o cliente vai receber');
    expect(previa.textContent).toBe('Olá «Variável 1», o resultado do exame «Variável 2» já está disponível.\nLaboratório Exemplo');

    await usuario.type(variavel1, 'Mauro');
    await usuario.type(variavel2, 'hemograma');

    expect(previa.textContent).toBe('Olá Mauro, o resultado do exame hemograma já está disponível.\nLaboratório Exemplo');
  });

  it('parâmetros vazios ou inválidos mostram o erro ao lado do campo e nada é enviado', async () => {
    const falso = simular({ [ROTA_OPCOES]: json(FECHADA), [ROTA_MODELO]: json({ outcome: 'Applied' }) });
    const usuario = userEvent.setup();
    renderizar();
    await usuario.selectOptions(await screen.findByLabelText('Modelo de mensagem'), 'resultado_pronto (pt_BR)');

    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));
    expect((await screen.findAllByRole('alert')).map((a) => a.textContent)).toEqual(['Preencha Variável 1.', 'Preencha Variável 2.']);
    expect(chamadasDe(falso, ROTA_MODELO)).toHaveLength(0);
    expect(screen.getByLabelText('Variável 1').getAttribute('aria-invalid')).toBe('true');

    await usuario.type(screen.getByLabelText('Variável 1'), 'quatro    espacos');
    expect(await screen.findByText(/não pode ter quebra de linha, tabulação nem mais de 3 espaços/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Enviar modelo' }) as HTMLButtonElement).disabled).toBe(true);
    expect(chamadasDe(falso, ROTA_MODELO)).toHaveLength(0);
  });

  it('com os campos certos, envia o modelo uma vez com o requestId e os parâmetros por chave, e o formulário é limpo', async () => {
    const falso = simular({ [ROTA_OPCOES]: json(FECHADA), [ROTA_MODELO]: json({ outcome: 'Applied', messageId: 'm1' }) });
    const usuario = userEvent.setup();
    const { aoAlterar, aoErroDeAcao } = renderizar();
    await usuario.selectOptions(await screen.findByLabelText('Modelo de mensagem'), 'resultado_pronto (pt_BR)');
    await usuario.type(screen.getByLabelText('Variável 1'), '  Mauro  ');
    await usuario.type(screen.getByLabelText('Variável 2'), 'hemograma');

    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));

    await waitFor(() => expect(chamadasDe(falso, ROTA_MODELO)).toHaveLength(1));
    const chamada = chamadasDe(falso, ROTA_MODELO)[0]!.corpo!;
    expect(chamada.templateId).toBe(RESULTADO.id);
    expect(chamada.requestId).toMatch(GUID);
    expect(chamada.parameters).toEqual({ 'body.1': 'Mauro', 'body.2': 'hemograma' });
    await waitFor(() => expect(aoAlterar).toHaveBeenCalled());
    expect(ultimoErroDeAcao(aoErroDeAcao)).toBeNull();
    // O formulário voltou ao começo.
    await waitFor(() => expect((screen.getByLabelText('Modelo de mensagem') as HTMLSelectElement).value).toBe(''));
    expect(screen.queryByLabelText('Variável 1')).toBeNull();
  });

  it('um modelo sem parâmetros envia direto', async () => {
    const falso = simular({ [ROTA_OPCOES]: json(FECHADA), [ROTA_MODELO]: json({ outcome: 'Applied' }) });
    const usuario = userEvent.setup();
    renderizar();
    await usuario.selectOptions(await screen.findByLabelText('Modelo de mensagem'), 'retomar_atendimento (pt_BR)');
    expect(screen.queryByRole('group', { name: 'Preencha os campos do modelo' })).toBeNull();
    expect(screen.getByLabelText('Prévia do que o cliente vai receber').textContent).toBe('Seu atendimento foi retomado.');

    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));

    await waitFor(() => expect(chamadasDe(falso, ROTA_MODELO)).toHaveLength(1));
    expect(chamadasDe(falso, ROTA_MODELO)[0]!.corpo!.parameters).toEqual({});
  });

  it('o servidor recusa os parâmetros (422): os erros aparecem nos campos, sem a faixa de erro vermelha, e o pedido seguinte usa outro requestId', async () => {
    let tentativas = 0;
    const falso = simular({
      [ROTA_OPCOES]: json(FECHADA),
      [ROTA_MODELO]: () => {
        tentativas += 1;
        if (tentativas === 1) {
          return json(
            {
              title: 'Parâmetros do modelo inválidos',
              status: 422,
              code: 'TemplateParametersInvalid',
              errors: [{ key: 'body.2', code: 'too-long', message: 'Variável 2 aceita no máximo 1024 caracteres.' }],
            },
            422,
          );
        }
        return json({ outcome: 'Applied' });
      },
    });
    const usuario = userEvent.setup();
    const { aoErroDeAcao } = renderizar();
    await usuario.selectOptions(await screen.findByLabelText('Modelo de mensagem'), 'resultado_pronto (pt_BR)');
    await usuario.type(screen.getByLabelText('Variável 1'), 'Mauro');
    await usuario.type(screen.getByLabelText('Variável 2'), 'hemograma');

    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));

    expect(await screen.findByText('Variável 2 aceita no máximo 1024 caracteres.')).toBeTruthy();
    expect(ultimoErroDeAcao(aoErroDeAcao)).toBeNull(); // nenhum texto de erro foi para a faixa vermelha

    // O atendente corrige (editar limpa o erro do servidor) e envia de novo: rejeição definitiva, então é um pedido novo.
    await usuario.type(screen.getByLabelText('Variável 2'), ' completo');
    expect(screen.queryByText('Variável 2 aceita no máximo 1024 caracteres.')).toBeNull();
    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));
    await waitFor(() => expect(chamadasDe(falso, ROTA_MODELO)).toHaveLength(2));
    const [primeira, segunda] = chamadasDe(falso, ROTA_MODELO).map((c) => c.corpo!.requestId);
    expect(primeira).not.toBe(segunda);
  });

  it('falha de rede no envio do modelo é resultado incerto: a faixa de erro aparece e repetir reaproveita o MESMO requestId', async () => {
    let tentativas = 0;
    const falso = simular({
      [ROTA_OPCOES]: json(FECHADA),
      [ROTA_MODELO]: () => {
        tentativas += 1;
        if (tentativas === 1) {
          throw new TypeError('rede caiu');
        }
        return json({ outcome: 'Applied' });
      },
    });
    const usuario = userEvent.setup();
    const { aoErroDeAcao } = renderizar();
    await usuario.selectOptions(await screen.findByLabelText('Modelo de mensagem'), 'retomar_atendimento (pt_BR)');

    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));
    await waitFor(() => expect(ultimoErroDeAcao(aoErroDeAcao)).toMatch(/Sem conexão/));
    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));

    await waitFor(() => expect(chamadasDe(falso, ROTA_MODELO)).toHaveLength(2));
    const [primeira, segunda] = chamadasDe(falso, ROTA_MODELO).map((c) => c.corpo!.requestId);
    expect(primeira).toBe(segunda);
  });

  it('o modelo escolhido sai da lista (pausado pela Meta): a escolha volta ao começo e a nota explica', async () => {
    let opcoes: ReplyOptions = FECHADA;
    simular({
      [ROTA_OPCOES]: () => json(opcoes),
      [ROTA_MODELO]: problema(422, 'Regra não atendida', 'O modelo de mensagem não está disponível para este número.', 'ReplyTemplateUnavailable'),
    });
    const usuario = userEvent.setup();
    renderizar();
    await usuario.selectOptions(await screen.findByLabelText('Modelo de mensagem'), 'retomar_atendimento (pt_BR)');
    opcoes = { ...FECHADA, templates: [RESULTADO] };

    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));

    expect(await screen.findByText(/não está mais disponível para este número/)).toBeTruthy();
    await waitFor(() => {
      const escolha = screen.getByLabelText('Modelo de mensagem') as HTMLSelectElement;
      expect(Array.from(escolha.options).map((o) => o.textContent)).toEqual(['Escolha um modelo aprovado...', 'resultado_pronto (pt_BR)']);
      expect(escolha.value).toBe('');
    });
  });

  it('sem nenhum modelo aprovado no número, avisa e orienta procurar o administrador', async () => {
    simular({ [ROTA_OPCOES]: json({ ...FECHADA, templates: [] }) });
    renderizar();

    expect(await screen.findByText(/Nenhum modelo aprovado está associado a este número/)).toBeTruthy();
    expect(screen.queryByLabelText('Modelo de mensagem')).toBeNull();
    expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).disabled).toBe(true);
  });
});

describe('ConversationPanel: janela aberta e opções indisponíveis', () => {
  it('com a janela aberta, o texto livre vale e os modelos ficam à mão num botão', async () => {
    const falso = simular({ [ROTA_OPCOES]: json(ABERTA), [ROTA_MODELO]: json({ outcome: 'Applied' }), [ROTA_TEXTO]: json({ outcome: 'Applied' }) });
    const usuario = userEvent.setup();
    renderizar();

    const botao = await screen.findByRole('button', { name: 'Enviar modelo de mensagem' });
    expect(screen.queryByRole('status', { name: 'Janela de 24 horas fechada' })).toBeNull();
    expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).disabled).toBe(false);
    expect(screen.getByText(/Resposta livre até/)).toBeTruthy();
    expect(screen.queryByLabelText('Modelo de mensagem')).toBeNull();

    await usuario.click(botao);
    expect(screen.getByLabelText('Modelo de mensagem')).toBeTruthy();
    await usuario.selectOptions(screen.getByLabelText('Modelo de mensagem'), 'retomar_atendimento (pt_BR)');
    await usuario.click(screen.getByRole('button', { name: 'Enviar modelo' }));
    await waitFor(() => expect(chamadasDe(falso, ROTA_MODELO)).toHaveLength(1));
    await waitFor(() => expect(screen.queryByLabelText('Modelo de mensagem')).toBeNull()); // o seletor se recolhe depois de enviar

    await usuario.type(screen.getByLabelText('Resposta ao cliente'), 'Olá!');
    await usuario.click(screen.getByRole('button', { name: 'Responder' }));
    await waitFor(() => expect(chamadasDe(falso, ROTA_TEXTO)).toHaveLength(1));
  });

  it('a janela fecha com o passar do tempo, sem esperar a próxima releitura', () => {
    const quase: ReplyOptions = { ...ABERTA, windowClosesAt: new Date(1_000_000 + 60_000).toISOString() };

    expect(janelaDeResposta(quase, 1_000_000).aberta).toBe(true);
    expect(janelaDeResposta(quase, 1_000_000 + 60_000).aberta).toBe(false);
    expect(janelaDeResposta({ ...quase, windowClosesAt: null }, 1_000_000).aberta).toBe(false); // o cliente nunca escreveu
  });

  it('sem janela no provedor, ou sem as opções, o texto livre vale como sempre', () => {
    expect(janelaDeResposta(null, Date.now())).toEqual({ aberta: true, fechaEm: null, horas: null });
    expect(janelaDeResposta({ ...ABERTA, windowHours: null, windowClosesAt: null }, Date.now()).aberta).toBe(true);
  });

  it('se o servidor recusa o texto livre porque a janela fechou há instantes, explica, relê as opções e passa a bloquear o campo', async () => {
    let opcoes: ReplyOptions = ABERTA;
    const falso = simular({
      [ROTA_OPCOES]: () => json(opcoes),
      [ROTA_TEXTO]: problema(422, 'Regra não atendida', 'A janela de 24 horas para responder livremente a este cliente está fechada.', 'ReplyWindowClosed'),
    });
    const usuario = userEvent.setup();
    const { aoErroDeAcao } = renderizar();
    await usuario.type(await screen.findByLabelText('Resposta ao cliente'), 'Posso ajudar?');
    opcoes = FECHADA;

    await usuario.click(screen.getByRole('button', { name: 'Responder' }));

    expect(await screen.findByText(/A janela de resposta livre deste cliente fechou/)).toBeTruthy();
    expect(ultimoErroDeAcao(aoErroDeAcao)).toBeNull();
    expect(await screen.findByRole('status', { name: 'Janela de 24 horas fechada' })).toBeTruthy();
    expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).disabled).toBe(true);
    expect((screen.getByLabelText('Resposta ao cliente') as HTMLTextAreaElement).value).toBe('Posso ajudar?'); // o texto digitado não se perde
    expect(chamadasDe(falso, ROTA_OPCOES).length).toBeGreaterThanOrEqual(2);
  });

  it('se as opções não carregam (404, sem conexão), a tela segue como antes: texto livre, sem modelos e sem erro', async () => {
    simular({});
    const { aoErroDeRede, aoErroDeAcao } = renderizar();

    const texto = (await screen.findByLabelText('Resposta ao cliente')) as HTMLTextAreaElement;
    await new Promise((resolve) => setTimeout(resolve, 60));

    expect(texto.disabled).toBe(false);
    expect(screen.queryByRole('button', { name: 'Enviar modelo de mensagem' })).toBeNull();
    expect(screen.queryByRole('status', { name: 'Janela de 24 horas fechada' })).toBeNull();
    expect(ultimoErroDeAcao(aoErroDeAcao)).toBeNull();
    expect(aoErroDeRede.mock.calls.every(([origem]) => origem !== 'opcoes')).toBe(true);
  });

  it('só quem assumiu a conversa consulta as opções de resposta', async () => {
    const falso = criarFetch({ [ROTA_ITEM]: json(item({ status: 'Waiting' })), [ROTA_MENSAGENS]: pagina(mensagem({ messageSeq: 1 })), [ROTA_OPCOES]: json(FECHADA) });
    vi.stubGlobal('fetch', falso);
    renderizar();

    await screen.findByText('Olá');

    expect(chamadasDe(falso, ROTA_OPCOES)).toHaveLength(0);
  });
});

describe('O histórico mostra o que saiu por modelo', () => {
  it('a mensagem enviada por modelo traz a etiqueta com o nome do modelo, e as demais não', async () => {
    simular({
      [ROTA_OPCOES]: json(ABERTA),
      [ROTA_MENSAGENS]: pagina(
        mensagem({ text: 'Quero meu resultado', messageSeq: 1 }),
        mensagem({ direction: 'Outbound', author: 'Human', text: 'Olá Mauro, o resultado do exame hemograma já está disponível.', messageSeq: 2, deliveryState: 'Sent', templateName: 'resultado_pronto' }),
        mensagem({ direction: 'Outbound', author: 'Human', text: 'Texto livre', messageSeq: 3, deliveryState: 'Sent' }),
      ),
    });
    renderizar();

    await screen.findByText(/o resultado do exame hemograma/);

    expect(screen.getAllByText('Modelo: resultado_pronto')).toHaveLength(1);
    expect(screen.queryAllByText(/^Modelo:/)).toHaveLength(1);
  });
});

describe('Parâmetros e prévia do modelo', () => {
  const [variavel1] = RESULTADO.parameters as [(typeof RESULTADO.parameters)[number]];

  it('valida como o servidor: vazio, grande demais, quebra de linha, tabulação e mais de 3 espaços', () => {
    expect(validarParametro(variavel1, '')).toBe('Preencha Variável 1.');
    expect(validarParametro(variavel1, '   ')).toBe('Preencha Variável 1.');
    expect(validarParametro(variavel1, 'x'.repeat(1025))).toBe('Variável 1 aceita no máximo 1024 caracteres.');
    expect(validarParametro(variavel1, 'a\nb')).toMatch(/quebra de linha/);
    expect(validarParametro(variavel1, 'a\tb')).toMatch(/quebra de linha/);
    expect(validarParametro(variavel1, 'a    b')).toMatch(/3 espaços/);
    expect(validarParametro(variavel1, 'a   b')).toBeNull();
    expect(validarParametro(variavel1, '  Mauro Porpino  ')).toBeNull();
  });

  it('a prévia troca cada marcador pelo valor, repete o mesmo valor e usa o rótulo onde o campo está vazio', () => {
    const repetido: ReplyTemplate = { ...RESULTADO, bodyText: '{{1}}, olá! {{1}}, tudo bem? Exame {{2}}.' };

    expect(montarPrevia(repetido, { 'body.1': 'Mauro' })).toBe('Mauro, olá! Mauro, tudo bem? Exame «Variável 2».\nLaboratório Exemplo');
    expect(montarPrevia({ ...RESULTADO, headerText: 'Aviso {{1}}', parameters: [{ key: 'header.1', component: 'header', label: 'Variável 1', example: null, maxLength: 60 }] }, { 'header.1': 'Importante' })).toBe(
      'Aviso Importante\nOlá {{1}}, o resultado do exame {{2}} já está disponível.\nLaboratório Exemplo',
    );
  });

  it('parâmetros nomeados entram pelo nome', () => {
    const nomeado: ReplyTemplate = {
      ...RESULTADO,
      bodyText: 'Olá {{nome}}!',
      footerText: null,
      parameters: [{ key: 'body.nome', component: 'body', label: 'nome', example: 'Mauro', maxLength: 1024 }],
    };

    expect(montarPrevia(nomeado, { 'body.nome': 'Mauro' })).toBe('Olá Mauro!');
    expect(montarPrevia(nomeado, {})).toBe('Olá «nome»!');
  });
});
