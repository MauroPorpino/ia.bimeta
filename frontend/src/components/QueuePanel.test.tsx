import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { QueueItem } from '../api/types';
import { FILA, USUARIO, chamadasDe, criarFetch, item, json, problema } from '../test/helpers';
import { QueuePanel } from './QueuePanel';

const AGUARDANDO = item({ id: '10000000-0000-4000-8000-000000000001', status: 'Waiting', enteredAt: new Date(Date.now() - 7 * 60_000).toISOString() });
const MEU = item({
  id: '10000000-0000-4000-8000-000000000002',
  status: 'Assigned',
  assignedUserId: USUARIO.id,
  assignedAt: new Date(Date.now() - 2 * 3_600_000 - 10 * 60_000).toISOString(),
});

function rotasBase(extras: Parameters<typeof criarFetch>[0] = {}) {
  return criarFetch({
    'GET /contact-center/me/queues': json([FILA]),
    'GET /contact-center/me/presence': json({ userId: USUARIO.id, status: 'Offline', updatedAt: null }),
    'GET /contact-center/items': ({ consulta }) => json(consulta.get('status') === 'Assigned' ? [MEU] : [AGUARDANDO]),
    ...extras,
  });
}

function renderizar(propriedades: Partial<Parameters<typeof QueuePanel>[0]> = {}) {
  const aoSelecionar = vi.fn();
  const aoErroDeRede = vi.fn();
  const aoErroDeAcao = vi.fn();
  const resultado = render(
    <QueuePanel
      api={createApi()}
      me={USUARIO}
      itemSelecionado={null}
      onSelecionar={aoSelecionar}
      nomes={{ [AGUARDANDO.id]: 'Mauro', [MEU.id]: 'Pedro' }}
      garantirNomes={vi.fn()}
      onErroDeRede={aoErroDeRede}
      onErroDeAcao={aoErroDeAcao}
      versaoDaLista={0}
      intervaloMs={60_000}
      {...propriedades}
    />,
  );
  return { ...resultado, aoSelecionar, aoErroDeRede, aoErroDeAcao };
}

describe('QueuePanel', () => {
  it('lista os atendimentos aguardando com contato, fila, situação e tempo de espera', async () => {
    const f = rotasBase();
    vi.stubGlobal('fetch', f);
    renderizar();

    const botao = await screen.findByRole('button', { name: /Mauro/ });
    expect(botao.textContent).toContain('Atendimento geral');
    expect(botao.textContent).toContain('Aguardando');
    expect(botao.textContent).toContain('há 7 min');

    const consulta = chamadasDe(f, 'GET /contact-center/items')[0]?.consulta;
    expect(Object.fromEntries(consulta ?? [])).toEqual({ status: 'Waiting', take: '50' });
  });

  it('sem nome do contato, identifica o atendimento pelo fim do GUID', async () => {
    vi.stubGlobal('fetch', rotasBase());
    renderizar({ nomes: {} });

    expect(await screen.findByRole('button', { name: /Atendimento #000001/ })).toBeTruthy();
  });

  it('aba "Meus" pede assignedToMe=true e mostra o item com o tempo de atendimento', async () => {
    const f = rotasBase();
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    await screen.findByRole('button', { name: /Mauro/ });

    await usuario.click(screen.getByRole('tab', { name: 'Meus' }));

    const botao = await screen.findByRole('button', { name: /Pedro/ });
    expect(botao.textContent).toContain('Com você');
    expect(botao.textContent).toContain('há 2 h 10 min');
    expect(screen.queryByRole('button', { name: /Mauro/ })).toBeNull();
    const consultas = chamadasDe(f, 'GET /contact-center/items').map((c) => Object.fromEntries(c.consulta));
    expect(consultas.at(-1)).toEqual({ status: 'Assigned', assignedToMe: 'true', take: '50' });
    expect(screen.getByRole('tab', { name: 'Meus' }).getAttribute('aria-selected')).toBe('true');
  });

  it('filtra por fila usando as filas do atendente', async () => {
    const f = rotasBase();
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    await screen.findByRole('button', { name: /Mauro/ });

    await usuario.selectOptions(screen.getByLabelText('Fila'), FILA.id);

    await waitFor(() => expect(chamadasDe(f, 'GET /contact-center/items').at(-1)?.consulta.get('queueId')).toBe(FILA.id));
  });

  it('atualiza sozinha (polling) e mostra o item que chegou depois', async () => {
    let lista: QueueItem[] = [AGUARDANDO];
    const f = rotasBase({ 'GET /contact-center/items': () => json(lista) });
    vi.stubGlobal('fetch', f);
    renderizar({ intervaloMs: 20, nomes: { [AGUARDANDO.id]: 'Mauro', '10000000-0000-4000-8000-000000000003': 'Joana' } });
    await screen.findByRole('button', { name: /Mauro/ });
    expect(screen.queryByRole('button', { name: /Joana/ })).toBeNull();

    lista = [AGUARDANDO, item({ id: '10000000-0000-4000-8000-000000000003' })];

    expect(await screen.findByRole('button', { name: /Joana/ })).toBeTruthy();
    expect(chamadasDe(f, 'GET /contact-center/items').length).toBeGreaterThanOrEqual(2);
  });

  it('clicar no item avisa qual foi escolhido', async () => {
    vi.stubGlobal('fetch', rotasBase());
    const usuario = userEvent.setup();
    const { aoSelecionar } = renderizar();

    await usuario.click(await screen.findByRole('button', { name: /Mauro/ }));

    expect(aoSelecionar).toHaveBeenCalledWith(AGUARDANDO.id);
  });

  it('lista vazia mostra a mensagem da aba', async () => {
    vi.stubGlobal('fetch', rotasBase({ 'GET /contact-center/items': json([]) }));
    renderizar();

    expect(await screen.findByText('Nenhum atendimento aguardando.')).toBeTruthy();
  });

  it('a presença vem da API e a troca é enviada com PUT', async () => {
    const f = rotasBase({
      'PUT /contact-center/me/presence': ({ corpo }) => json({ userId: USUARIO.id, status: corpo?.status, updatedAt: new Date().toISOString() }),
    });
    vi.stubGlobal('fetch', f);
    const usuario = userEvent.setup();
    renderizar();
    const seletor = (await screen.findByLabelText('Minha presença')) as HTMLSelectElement;
    await waitFor(() => expect(seletor.value).toBe('Offline'));

    await usuario.selectOptions(seletor, 'Available');

    await waitFor(() => expect(seletor.value).toBe('Available'));
    expect(chamadasDe(f, 'PUT /contact-center/me/presence')[0]?.corpo).toEqual({ status: 'Available' });
    expect(within(seletor).getByRole('option', { name: 'Ausente' })).toBeTruthy();
  });

  it('erro ao carregar a lista vai para a faixa de erros, sem esvaziar nem quebrar a tela', async () => {
    vi.stubGlobal('fetch', rotasBase({ 'GET /contact-center/items': problema(403, 'Acesso negado', 'Você não tem permissão para esta operação.') }));
    const { aoErroDeRede } = renderizar();

    await waitFor(() => expect(aoErroDeRede).toHaveBeenCalledWith('fila', 'Acesso negado: Você não tem permissão para esta operação.'));
    expect(screen.getByRole('tab', { name: 'Aguardando' })).toBeTruthy();
  });

  it('com a API fora do ar, a faixa recebe o erro de rede', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
    const { aoErroDeRede } = renderizar();

    await waitFor(() => expect(aoErroDeRede).toHaveBeenCalledWith('fila', expect.stringContaining('Sem conexão com a API')));
  });
});
