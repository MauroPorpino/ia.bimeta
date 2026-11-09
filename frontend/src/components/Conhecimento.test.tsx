import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import { definirSessao } from '../api/session';
import type { KnowledgeGapSummary } from '../api/types';
import { chamadasDe, criarFetch, definicao, detalheDaLacuna, entradaDoFaq, json, lacuna, problema } from '../test/helpers';
import { Conhecimento } from './Conhecimento';

type Rotas = Parameters<typeof criarFetch>[0];

function renderizar(rotas: Rotas) {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
  const f = criarFetch(rotas);
  vi.stubGlobal('fetch', f);
  render(<Conhecimento api={createApi()} />);
  return f;
}

const REVISAO = { name: 'Revisão da lacuna' };
const revisao = () => within(screen.getByRole('region', REVISAO));
const lista = () => within(screen.getByRole('region', { name: 'Lacunas do FAQ' }));
const rascunho = () => within(screen.getByRole('region', { name: 'Rascunho da definição' }));

const ROTA_LISTA = 'GET /agents/knowledge-gaps';
const rotaDetalhe = (id: string) => `GET /agents/knowledge-gaps/${id}`;
const rotaPromover = (id: string) => `POST /agents/knowledge-gaps/${id}/promote`;
const rotaDescartar = (id: string) => `POST /agents/knowledge-gaps/${id}/dismiss`;

const RESPOSTA_HUMANA = { text: 'Sim, é preciso jejum de 12 horas.', occurredAt: new Date().toISOString(), customerMessagesBetween: 0 };

/** Uma lacuna aberta, com a rota da lista e a do detalhe prontas. */
function rotasDeUmaLacuna(resumo: KnowledgeGapSummary, fonte: Parameters<typeof detalheDaLacuna>[1] = {}, extras: Rotas = {}): Rotas {
  return {
    [ROTA_LISTA]: json({ items: [resumo], total: 1 }),
    [rotaDetalhe(resumo.id)]: json(detalheDaLacuna(resumo, fonte)),
    ...extras,
  };
}

async function abrirPrimeiraLacuna(usuario: ReturnType<typeof userEvent.setup>) {
  await usuario.click(await screen.findByRole('button', { name: /ocorrência/ }));
  await revisao().findByLabelText('Pergunta');
}

function promovida(resumo: KnowledgeGapSummary, versao: number, entradas: number) {
  return { gap: { ...resumo, status: 'Promoted', promotedVersion: versao, decidedAt: new Date().toISOString() }, draftVersion: versao, faqEntries: entradas };
}

describe('Conhecimento: lista de lacunas', () => {
  it('lista as abertas por padrão, com tipo, ocorrências, última vez e situação, e filtra por situação', async () => {
    const a = lacuna({ kind: 'NoMatch', occurrences: 7 });
    const b = lacuna({ kind: 'Ambiguous', occurrences: 1 });
    const c = lacuna({ kind: 'NotAutoReply', occurrences: 2 });
    const descartada = lacuna({ status: 'Dismissed', occurrences: 4, decidedAt: new Date().toISOString() });
    const f = renderizar({
      [ROTA_LISTA]: (req) => {
        const situacao = req.consulta.get('status');
        if (situacao === 'Open') {
          return json({ items: [a, b, c], total: 3 });
        }
        return json(situacao === 'Dismissed' ? { items: [descartada], total: 1 } : { items: [a, b, c, descartada], total: 4 });
      },
    });
    const usuario = userEvent.setup();

    await lista().findByText('Sem correspondência no FAQ');
    expect(lista().getByText('Pergunta ambígua (entradas duplicadas)')).toBeTruthy();
    expect(lista().getByText('Entrada existente sem resposta direta')).toBeTruthy();
    expect(lista().getByText('7 ocorrências')).toBeTruthy();
    expect(lista().getByText('1 ocorrência')).toBeTruthy();
    expect(lista().getAllByText('Última vez: há 5 min')).toHaveLength(3);
    expect(lista().getAllByText('Aberta')).toHaveLength(3);
    expect((lista().getByLabelText('Situação') as HTMLSelectElement).value).toBe('Open');
    const primeira = chamadasDe(f, ROTA_LISTA)[0]!;
    expect(primeira.consulta.get('status')).toBe('Open');
    expect(primeira.consulta.get('take')).toBe('20');
    expect(primeira.consulta.get('skip')).toBe('0');

    await usuario.selectOptions(lista().getByLabelText('Situação'), 'Descartadas');
    await lista().findByText('4 ocorrências');
    expect(lista().getByText('Descartada')).toBeTruthy();
    expect(lista().queryByText('7 ocorrências')).toBeNull();
    expect(chamadasDe(f, ROTA_LISTA).at(-1)!.consulta.get('status')).toBe('Dismissed');

    await usuario.selectOptions(lista().getByLabelText('Situação'), 'Todas');
    await lista().findByText('7 ocorrências');
    expect(lista().getByText('4 ocorrências')).toBeTruthy();
    expect(chamadasDe(f, ROTA_LISTA).at(-1)!.consulta.has('status')).toBe(false);
  });

  it('mostra a versão do rascunho nas lacunas já enviadas e avisa quando não há lacunas', async () => {
    const enviada = lacuna({ status: 'Promoted', promotedVersion: 5, decidedAt: new Date().toISOString() });
    const usuario = userEvent.setup();
    renderizar({
      [ROTA_LISTA]: (req) => json(req.consulta.get('status') === 'Promoted' ? { items: [enviada], total: 1 } : { items: [], total: 0 }),
    });

    expect(await lista().findByText('Nenhuma lacuna aberta.')).toBeTruthy();

    await usuario.selectOptions(lista().getByLabelText('Situação'), 'Enviadas ao rascunho');
    expect(await lista().findByText('Enviada ao rascunho (versão 5)')).toBeTruthy();
  });

  it('pagina com o total e o deslocamento: próxima e anterior pedem o skip certo', async () => {
    const todas = Array.from({ length: 45 }, (_, i) => lacuna({ occurrences: 100 - i }));
    const f = renderizar({
      [ROTA_LISTA]: (req) => {
        const skip = Number(req.consulta.get('skip') ?? '0');
        const take = Number(req.consulta.get('take') ?? '50');
        return json({ items: todas.slice(skip, skip + take), total: todas.length });
      },
    });
    const usuario = userEvent.setup();

    await lista().findByText('1 a 20 de 45');
    expect((lista().getByRole('button', { name: 'Anterior' }) as HTMLButtonElement).disabled).toBe(true);

    await usuario.click(lista().getByRole('button', { name: 'Próxima' }));
    await lista().findByText('21 a 40 de 45');
    expect(chamadasDe(f, ROTA_LISTA).at(-1)!.consulta.get('skip')).toBe('20');

    await usuario.click(lista().getByRole('button', { name: 'Próxima' }));
    await lista().findByText('41 a 45 de 45');
    expect((lista().getByRole('button', { name: 'Próxima' }) as HTMLButtonElement).disabled).toBe(true);

    await usuario.click(lista().getByRole('button', { name: 'Anterior' }));
    await lista().findByText('21 a 40 de 45');
    expect(chamadasDe(f, ROTA_LISTA).at(-1)!.consulta.get('skip')).toBe('20');
  });

  it('quando a última página esvazia (as lacunas dela foram decididas), volta para a última que ainda tem itens', async () => {
    let todas = Array.from({ length: 21 }, (_, i) => lacuna({ occurrences: 100 - i }));
    renderizar({
      [ROTA_LISTA]: (req) => {
        const skip = Number(req.consulta.get('skip') ?? '0');
        return json({ items: todas.slice(skip, skip + 20), total: todas.length });
      },
    });
    const usuario = userEvent.setup();
    await lista().findByText('1 a 20 de 21');
    await usuario.click(lista().getByRole('button', { name: 'Próxima' }));
    await lista().findByText('21 a 21 de 21');

    todas = todas.slice(0, 20); // outra pessoa decidiu a lacuna que estava na segunda página
    await usuario.click(lista().getByRole('button', { name: 'Atualizar' }));

    expect(await lista().findByText('1 a 20 de 20')).toBeTruthy();
  });

  it('403 do servidor: mensagem clara, sem filtros e sem os painéis de revisão e de rascunho', async () => {
    renderizar({ [ROTA_LISTA]: problema(403, 'Acesso negado', 'Você não tem permissão para configurar o agente desta empresa.') });

    expect((await screen.findByRole('alert')).textContent).toContain('Você não tem permissão para administrar o conhecimento do agente desta empresa');
    expect(screen.queryByLabelText('Situação')).toBeNull();
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Rascunho da definição' })).toBeNull());
    expect(screen.queryByRole('region', REVISAO)).toBeNull();
  });

  it('erro de carregamento da lista aparece como alerta com o detalhe do servidor', async () => {
    renderizar({ [ROTA_LISTA]: problema(400, 'Dados inválidos', 'A página aceita de 1 a 100 itens.') });

    expect((await screen.findByRole('alert')).textContent).toBe('Dados inválidos: A página aceita de 1 a 100 itens.');
  });
});

describe('Conhecimento: revisão de uma lacuna', () => {
  it('abre o detalhe com a pergunta original e a resposta do atendente como SUGESTÃO, sem preencher a resposta sozinha', async () => {
    const l = lacuna();
    renderizar(rotasDeUmaLacuna(l, { pergunta: 'Preciso de jejum para o exame de colesterol?', respostaHumana: RESPOSTA_HUMANA }));
    const usuario = userEvent.setup();

    await abrirPrimeiraLacuna(usuario);

    expect(revisao().getByText('Preciso de jejum para o exame de colesterol?', { selector: 'blockquote' })).toBeTruthy();
    // A pergunta original vem como ponto de partida, editável.
    expect((revisao().getByLabelText('Pergunta') as HTMLTextAreaElement).value).toBe('Preciso de jejum para o exame de colesterol?');
    // A resposta humana está identificada como sugestão e NÃO foi copiada para o campo.
    expect(revisao().getByRole('region', { name: 'Resposta de um atendente' })).toBeTruthy();
    expect(revisao().getByText('Sim, é preciso jejum de 12 horas.', { selector: 'blockquote' })).toBeTruthy();
    expect(revisao().getByText(/Confira antes de usar/)).toBeTruthy();
    expect(revisao().getByText(/não é usada automaticamente/)).toBeTruthy();
    expect((revisao().getByLabelText('Resposta') as HTMLTextAreaElement).value).toBe('');
    expect(revisao().queryByText(/outras mensagens/)).toBeNull();
  });

  it('"Usar esta resposta" copia a sugestão para o campo editável, e só então', async () => {
    const l = lacuna();
    renderizar(rotasDeUmaLacuna(l, { pergunta: 'Preciso de jejum?', respostaHumana: RESPOSTA_HUMANA }));
    const usuario = userEvent.setup();
    await abrirPrimeiraLacuna(usuario);
    const campo = revisao().getByLabelText('Resposta') as HTMLTextAreaElement;
    expect(campo.value).toBe('');

    await usuario.click(revisao().getByRole('button', { name: 'Usar esta resposta' }));

    expect(campo.value).toBe('Sim, é preciso jejum de 12 horas.');
    await usuario.type(campo, ' Beba água.'); // continua editável
    expect(campo.value).toBe('Sim, é preciso jejum de 12 horas. Beba água.');
    expect((revisao().getByLabelText('Pergunta') as HTMLTextAreaElement).value).toBe('Preciso de jejum?');
  });

  it('fonte indisponível: avisa que a mensagem não pode ser lida e deixa a pergunta em branco para o administrador escrever', async () => {
    const l = lacuna();
    renderizar(rotasDeUmaLacuna(l, { indisponivel: true }));
    const usuario = userEvent.setup();

    await abrirPrimeiraLacuna(usuario);

    expect(revisao().getByText(/A mensagem original não está disponível \(foi removida ou você não tem permissão para vê-la\): escreva a pergunta\./)).toBeTruthy();
    expect((revisao().getByLabelText('Pergunta') as HTMLTextAreaElement).value).toBe('');
    expect(revisao().queryByText('Pergunta original do cliente')).toBeNull();
    expect(revisao().queryByRole('region', { name: 'Resposta de um atendente' })).toBeNull();
  });

  it('avisa quando o cliente enviou outras mensagens antes da resposta do atendente', async () => {
    const l = lacuna();
    renderizar(rotasDeUmaLacuna(l, { pergunta: 'Pergunta', respostaHumana: { ...RESPOSTA_HUMANA, customerMessagesBetween: 2 } }));
    const usuario = userEvent.setup();

    await abrirPrimeiraLacuna(usuario);

    expect(revisao().getByText('O cliente enviou outras mensagens (2) antes dessa resposta: ela pode ser de outra pergunta.')).toBeTruthy();
  });

  it('a resposta direta (autoReply) começa desmarcada, com o alerta, e o envio leva false', async () => {
    const l = lacuna();
    const f = renderizar(
      rotasDeUmaLacuna(l, { pergunta: 'Qual o prazo do resultado?' }, { [rotaPromover(l.id)]: json(promovida(l, 3, 4)) }),
    );
    const usuario = userEvent.setup();
    await abrirPrimeiraLacuna(usuario);

    const caixa = revisao().getByRole('checkbox', { name: 'Responder direto, sem usar a IA (autoReply)' }) as HTMLInputElement;
    expect(caixa.checked).toBe(false);
    expect(revisao().getByText('Só ligue em respostas que se sustentam sozinhas: sem preço, agenda, cobertura, orientação clínica nem referência ao que foi dito antes.')).toBeTruthy();

    await usuario.type(revisao().getByLabelText('Resposta'), 'O resultado sai em 2 dias úteis.');
    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));

    await revisao().findByText('Entrada enviada ao rascunho (versão 3). Nada foi publicado.');
    expect(chamadasDe(f, rotaPromover(l.id))).toEqual([
      { corpo: { question: 'Qual o prazo do resultado?', aliases: [], answer: 'O resultado sai em 2 dias úteis.', autoReply: false }, consulta: expect.anything() },
    ]);
  });

  it('só envia autoReply true quando o administrador marca a caixa; aliases saem um por linha, sem linhas vazias', async () => {
    const l = lacuna();
    const f = renderizar(rotasDeUmaLacuna(l, { pergunta: 'Horário de coleta?' }, { [rotaPromover(l.id)]: json(promovida(l, 4, 2)) }));
    const usuario = userEvent.setup();
    await abrirPrimeiraLacuna(usuario);

    await usuario.clear(revisao().getByLabelText('Pergunta'));
    await usuario.type(revisao().getByLabelText('Pergunta'), '  Que horas abre a coleta?  ');
    await usuario.type(revisao().getByLabelText('Aliases (um por linha)'), 'horário da coleta{Enter}{Enter}  coleta abre quando  {Enter}');
    await usuario.type(revisao().getByLabelText('Resposta'), 'A coleta abre às 7h.');
    await usuario.click(revisao().getByRole('checkbox', { name: /autoReply/ }));
    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));

    await revisao().findByText(/Nada foi publicado/);
    expect(chamadasDe(f, rotaPromover(l.id))[0]!.corpo).toEqual({
      question: 'Que horas abre a coleta?',
      aliases: ['horário da coleta', 'coleta abre quando'],
      answer: 'A coleta abre às 7h.',
      autoReply: true,
    });
  });

  it('envio com sucesso: mostra "Nada foi publicado", torna a lacuna somente leitura, relê a lista e o rascunho', async () => {
    const l = lacuna();
    let enviada = false;
    const nova = entradaDoFaq({ question: 'Pergunta nova', answer: 'Resposta nova' });
    const f = renderizar({
      [ROTA_LISTA]: () => json(enviada ? { items: [], total: 0 } : { items: [l], total: 1 }),
      [rotaDetalhe(l.id)]: json(detalheDaLacuna(l, { pergunta: 'Pergunta nova' })),
      'GET /agents/definition/draft': () => (enviada ? json(definicao([nova], { number: 6 })) : new Response(null, { status: 404 })),
      [rotaPromover(l.id)]: () => {
        enviada = true;
        return json(promovida(l, 6, 1));
      },
    });
    const usuario = userEvent.setup();
    await rascunho().findByText(/Não há rascunho/);
    await abrirPrimeiraLacuna(usuario);
    await usuario.type(revisao().getByLabelText('Resposta'), 'Resposta nova');

    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));

    expect((await revisao().findByRole('status')).textContent).toBe('Entrada enviada ao rascunho (versão 6). Nada foi publicado.');
    expect(revisao().queryByRole('form', { name: 'Entrada do FAQ' })).toBeNull();
    expect(revisao().getByText(/Situação: Enviada ao rascunho \(versão 6 do rascunho\)/)).toBeTruthy();
    await lista().findByText('Nenhuma lacuna aberta.');
    expect(await rascunho().findByText('Pergunta nova')).toBeTruthy(); // o rascunho foi relido
    expect(chamadasDe(f, 'GET /agents/definition/draft').length).toBeGreaterThanOrEqual(2);
    expect(chamadasDe(f, 'POST /agents/definition/publish')).toHaveLength(0);
  });

  it('409 faq-conflict: explica que a pergunta já existe, e o formulário continua com o que foi escrito', async () => {
    const l = lacuna();
    renderizar(
      rotasDeUmaLacuna(
        l,
        { pergunta: 'Qual o horário?' },
        { [rotaPromover(l.id)]: problema(409, 'Conflito', 'O FAQ já tem uma entrada com essa pergunta (ou um alias igual).', 'faq-conflict') },
      ),
    );
    const usuario = userEvent.setup();
    await abrirPrimeiraLacuna(usuario);
    await usuario.type(revisao().getByLabelText('Resposta'), 'Das 7h às 17h.');

    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));

    expect((await revisao().findByRole('alert')).textContent).toBe('O FAQ já tem uma entrada com essa pergunta ou alias: ajuste a existente em vez de criar outra.');
    expect((revisao().getByLabelText('Resposta') as HTMLTextAreaElement).value).toBe('Das 7h às 17h.');
    expect((revisao().getByRole('button', { name: 'Enviar ao rascunho' }) as HTMLButtonElement).disabled).toBe(false);
    expect(revisao().queryByText(/Nada foi publicado/)).toBeNull();
  });

  it('400: mostra o detalhe que o servidor explicou', async () => {
    const l = lacuna();
    renderizar(
      rotasDeUmaLacuna(l, { pergunta: 'Pergunta' }, { [rotaPromover(l.id)]: problema(400, 'Dados inválidos', 'A resposta excede 1500 caracteres.') }),
    );
    const usuario = userEvent.setup();
    await abrirPrimeiraLacuna(usuario);
    await usuario.type(revisao().getByLabelText('Resposta'), 'Algo');

    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));

    expect((await revisao().findByRole('alert')).textContent).toBe('Dados inválidos: A resposta excede 1500 caracteres.');
  });

  it('outros 409 (lacuna já decidida, sem definição, rascunho alterado) têm mensagem própria; "já decidida" relê a situação', async () => {
    const l = lacuna();
    let codigo = 'no-definition';
    let decidida = false;
    renderizar({
      [ROTA_LISTA]: json({ items: [l], total: 1 }),
      [rotaDetalhe(l.id)]: () => json(detalheDaLacuna(decidida ? { ...l, status: 'Dismissed', decidedAt: new Date().toISOString() } : l, { pergunta: 'Pergunta' })),
      [rotaPromover(l.id)]: () => problema(409, 'Conflito', 'detalhe do servidor', codigo),
    });
    const usuario = userEvent.setup();
    await abrirPrimeiraLacuna(usuario);
    await usuario.type(revisao().getByLabelText('Resposta'), 'Algo');

    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));
    expect((await revisao().findByRole('alert')).textContent).toContain('ainda não tem uma definição do agente');

    codigo = 'definition-changed';
    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));
    await waitFor(() => expect(revisao().getByRole('alert').textContent).toContain('O rascunho foi alterado por outra operação'));

    codigo = 'gap-not-open';
    decidida = true;
    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));
    await waitFor(() => expect(revisao().getByRole('alert').textContent).toContain('já foi decidida'));
    await waitFor(() => expect(revisao().queryByRole('form', { name: 'Entrada do FAQ' })).toBeNull()); // a situação real: somente leitura
  });

  it('não envia nada quando a pergunta ou a resposta estão vazias', async () => {
    const l = lacuna();
    const f = renderizar(rotasDeUmaLacuna(l, { indisponivel: true }, { [rotaPromover(l.id)]: json(promovida(l, 1, 1)) }));
    const usuario = userEvent.setup();
    await abrirPrimeiraLacuna(usuario);

    await usuario.click(revisao().getByRole('button', { name: 'Enviar ao rascunho' }));

    expect((await revisao().findByRole('alert')).textContent).toBe('Preencha a pergunta e a resposta da entrada.');
    expect(chamadasDe(f, rotaPromover(l.id))).toHaveLength(0);
  });

  it('descartar pede confirmação: cancelar não faz nada, confirmar descarta e relê a lista', async () => {
    const l = lacuna();
    let descartada = false;
    const f = renderizar({
      [ROTA_LISTA]: () => json(descartada ? { items: [], total: 0 } : { items: [l], total: 1 }),
      [rotaDetalhe(l.id)]: json(detalheDaLacuna(l, { pergunta: 'Pergunta' })),
      [rotaDescartar(l.id)]: () => {
        descartada = true;
        return json({ ...l, status: 'Dismissed', decidedAt: new Date().toISOString() });
      },
    });
    const usuario = userEvent.setup();
    await abrirPrimeiraLacuna(usuario);

    await usuario.click(revisao().getByRole('button', { name: 'Descartar' }));
    expect(revisao().getByRole('group', { name: 'Confirmar descarte' })).toBeTruthy();
    expect(chamadasDe(f, rotaDescartar(l.id))).toHaveLength(0);

    await usuario.click(revisao().getByRole('button', { name: 'Cancelar' }));
    expect(revisao().queryByRole('group', { name: 'Confirmar descarte' })).toBeNull();
    expect(chamadasDe(f, rotaDescartar(l.id))).toHaveLength(0);

    await usuario.click(revisao().getByRole('button', { name: 'Descartar' }));
    await usuario.click(revisao().getByRole('button', { name: 'Confirmar descarte' }));

    expect((await revisao().findByRole('status')).textContent).toBe('Lacuna descartada. Nada foi alterado no FAQ.');
    expect(chamadasDe(f, rotaDescartar(l.id))).toHaveLength(1);
    expect(revisao().queryByRole('form', { name: 'Entrada do FAQ' })).toBeNull();
    await lista().findByText('Nenhuma lacuna aberta.');
  });

  it('lacuna que não está aberta é só leitura: sem formulário, sem descartar e sem usar a resposta', async () => {
    const l = lacuna({ status: 'Promoted', promotedVersion: 4, decidedAt: new Date().toISOString() });
    renderizar({
      [ROTA_LISTA]: (req) => json(req.consulta.get('status') === 'Open' ? { items: [], total: 0 } : { items: [l], total: 1 }),
      [rotaDetalhe(l.id)]: json(detalheDaLacuna(l, { pergunta: 'Pergunta antiga', respostaHumana: RESPOSTA_HUMANA })),
    });
    const usuario = userEvent.setup();
    await usuario.selectOptions(await lista().findByLabelText('Situação'), 'Todas');
    await usuario.click(await screen.findByRole('button', { name: /ocorrência/ }));

    expect(await revisao().findByText('Pergunta antiga', { selector: 'blockquote' })).toBeTruthy();
    expect(revisao().getByText(/Situação: Enviada ao rascunho \(versão 4 do rascunho\)/)).toBeTruthy();
    expect(revisao().getByText('Sim, é preciso jejum de 12 horas.', { selector: 'blockquote' })).toBeTruthy();
    expect(revisao().queryByRole('form', { name: 'Entrada do FAQ' })).toBeNull();
    expect(revisao().queryByRole('button', { name: 'Descartar' })).toBeNull();
    expect(revisao().queryByRole('button', { name: 'Usar esta resposta' })).toBeNull();
    expect(revisao().queryByRole('button', { name: 'Enviar ao rascunho' })).toBeNull();
  });

  it('lacuna que não existe mais (404) mostra o aviso e deixa tentar de novo', async () => {
    const l = lacuna();
    renderizar({ [ROTA_LISTA]: json({ items: [l], total: 1 }) }); // a rota do detalhe não existe: 404 vazio
    const usuario = userEvent.setup();

    await usuario.click(await screen.findByRole('button', { name: /ocorrência/ }));

    expect((await revisao().findByRole('alert')).textContent).toBe('Esta lacuna não existe mais ou está fora do seu alcance.');
    expect(revisao().getByRole('button', { name: 'Tentar novamente' })).toBeTruthy();
  });
});

describe('Conhecimento: rascunho da definição', () => {
  it('sem rascunho, diz que não há', async () => {
    renderizar({ [ROTA_LISTA]: json({ items: [], total: 0 }), 'GET /agents/definition/published': json(definicao([], { number: 1, status: 'Published' })) });

    expect(await rascunho().findByText('Não há rascunho. A versão publicada atual é a 1.')).toBeTruthy();
    expect(rascunho().queryByRole('button', { name: 'Publicar rascunho' })).toBeNull();
  });

  it('marca as entradas novas, as alteradas e as com resposta direta em relação à publicada', async () => {
    const igual = entradaDoFaq({ question: 'Qual o horário?', answer: 'Das 7h às 17h.' });
    const antes = entradaDoFaq({ question: 'Aceitam convênio?', answer: 'Aceitamos alguns.' });
    renderizar({
      [ROTA_LISTA]: json({ items: [], total: 0 }),
      'GET /agents/definition/published': json(definicao([igual, antes], { number: 1, status: 'Published', publishedAt: new Date().toISOString() })),
      'GET /agents/definition/draft': json(
        definicao([
          igual,
          { ...antes, answer: 'Aceitamos vários.' },
          entradaDoFaq({ question: 'Como colher o exame?', answer: 'Em jejum.', autoReply: true }),
          entradaDoFaq({ question: 'Onde fica?', answer: 'Na rua A.', aliases: ['endereço', 'localização'] }),
        ]),
      ),
    });

    expect(await rascunho().findByText('Como colher o exame?')).toBeTruthy();
    expect(rascunho().getAllByText('nova')).toHaveLength(2);
    expect(rascunho().getAllByText('alterada')).toHaveLength(1);
    expect(rascunho().getAllByText('resposta direta')).toHaveLength(1);
    expect(rascunho().getByText('Também: endereço; localização')).toBeTruthy();
    expect(rascunho().getByText(/Rascunho: versão 2/)).toBeTruthy();
    expect(rascunho().getByText(/Versão publicada: 1/)).toBeTruthy();
    // A entrada igual à publicada não ganha marca: a primeira linha só tem a pergunta.
    const linhaIgual = rascunho().getByText('Qual o horário?').closest('li')!;
    expect(within(linhaIgual).queryByText('nova')).toBeNull();
    expect(within(linhaIgual).queryByText('alterada')).toBeNull();
  });

  it('sem versão publicada, diz que todas as entradas são novas em vez de marcar uma a uma', async () => {
    renderizar({ [ROTA_LISTA]: json({ items: [], total: 0 }), 'GET /agents/definition/draft': json(definicao([entradaDoFaq()])) });

    expect(await rascunho().findByText(/Ainda não há versão publicada para comparar: todas as entradas são novas\./)).toBeTruthy();
    expect(rascunho().queryByText('nova')).toBeNull();
  });

  it('publicar exige confirmação: cancelar não publica, confirmar publica e mostra o número da versão', async () => {
    let publicado = false;
    const f = renderizar({
      [ROTA_LISTA]: json({ items: [], total: 0 }),
      'GET /agents/definition/published': () => (publicado ? json(definicao([entradaDoFaq()], { number: 3, status: 'Published' })) : new Response(null, { status: 404 })),
      'GET /agents/definition/draft': () => (publicado ? new Response(null, { status: 404 }) : json(definicao([entradaDoFaq({ autoReply: true })], { number: 3 }))),
      'POST /agents/definition/publish': () => {
        publicado = true;
        return json(definicao([entradaDoFaq({ autoReply: true })], { number: 3, status: 'Published', publishedAt: new Date().toISOString() }));
      },
    });
    const usuario = userEvent.setup();
    await rascunho().findByText('Qual o horário de funcionamento?');

    await usuario.click(rascunho().getByRole('button', { name: 'Publicar rascunho' }));
    expect(rascunho().getByText(/A versão atual será substituída; os clientes passam a receber estas respostas\./)).toBeTruthy();
    expect(rascunho().getByText(/1 entrada responde direto, sem passar pela IA\./)).toBeTruthy();
    expect(chamadasDe(f, 'POST /agents/definition/publish')).toHaveLength(0);

    await usuario.click(rascunho().getByRole('button', { name: 'Cancelar' }));
    expect(rascunho().queryByRole('group', { name: 'Confirmar publicação' })).toBeNull();
    expect(chamadasDe(f, 'POST /agents/definition/publish')).toHaveLength(0);

    await usuario.click(rascunho().getByRole('button', { name: 'Publicar rascunho' }));
    await usuario.click(rascunho().getByRole('button', { name: 'Confirmar publicação' }));

    expect((await rascunho().findByRole('status')).textContent).toBe('Versão 3 publicada: é a que o agente usa agora.');
    expect(chamadasDe(f, 'POST /agents/definition/publish')).toHaveLength(1);
    expect(await rascunho().findByText(/Não há rascunho\. A versão publicada atual é a 3\./)).toBeTruthy();
  });

  it('publicar com erro (409 definition-changed) mostra a mensagem, relê e não diz que publicou', async () => {
    renderizar({
      [ROTA_LISTA]: json({ items: [], total: 0 }),
      'GET /agents/definition/draft': json(definicao([entradaDoFaq()])),
      'POST /agents/definition/publish': problema(409, 'Conflito', 'O rascunho foi alterado.', 'definition-changed'),
    });
    const usuario = userEvent.setup();
    await rascunho().findByText('Qual o horário de funcionamento?');

    await usuario.click(rascunho().getByRole('button', { name: 'Publicar rascunho' }));
    await usuario.click(rascunho().getByRole('button', { name: 'Confirmar publicação' }));

    expect((await rascunho().findByRole('alert')).textContent).toBe('O rascunho foi alterado por outra operação. Recarregue o rascunho e tente de novo.');
    expect(rascunho().queryByRole('status')).toBeNull();
    expect(rascunho().queryByRole('group', { name: 'Confirmar publicação' })).toBeNull();
  });

  it('403 ao publicar mostra a mensagem de permissão', async () => {
    renderizar({
      [ROTA_LISTA]: json({ items: [], total: 0 }),
      'GET /agents/definition/draft': json(definicao([entradaDoFaq()])),
      'POST /agents/definition/publish': problema(403, 'Acesso negado', 'Você não tem permissão para configurar o agente desta empresa.'),
    });
    const usuario = userEvent.setup();
    await rascunho().findByText('Qual o horário de funcionamento?');

    await usuario.click(rascunho().getByRole('button', { name: 'Publicar rascunho' }));
    await usuario.click(rascunho().getByRole('button', { name: 'Confirmar publicação' }));

    expect((await rascunho().findByRole('alert')).textContent).toContain('Você não tem permissão para administrar o conhecimento do agente desta empresa');
  });
});
