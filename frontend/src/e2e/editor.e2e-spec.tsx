import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import { definirSessao } from '../api/session';
import type { CurrentUser } from '../api/types';
import { Fluxos } from '../components/Fluxos';

/**
 * O fluxo do administrador no editor, ponta a ponta, contra a API REAL e o PostgreSQL de teste: criar o fluxo, acrescentar um nó, validar (problemas vindos do servidor), remover o nó, editar, salvar, publicar,
 * simular e ativar (com o escopo conferido pelo servidor). O jsdom NÃO tem layout nem mouse: arrastar nós e CONECTAR saídas com o mouse ficam para o navegador (ver docs/operations/HOMOLOGACAO-V1.md).
 */
const variaveis = (globalThis as { process?: { env: Record<string, string | undefined> } }).process?.env ?? {};

const ambiente = (nome: string): string => {
  const valor = variaveis[nome];
  if (!valor) {
    throw new Error(`Variável ${nome} ausente: este teste só roda pelo EditorDeFluxosComApiRealTests (ou com as variáveis E2E_* definidas).`);
  }
  return valor;
};

describe('editor de fluxos com a API real', () => {
  let usuario: CurrentUser;
  const api = createApi({ baseUrl: variaveis.E2E_API_URL ?? '' });

  beforeAll(async () => {
    const entrada = await api.login({ tenant: ambiente('E2E_TENANT'), email: ambiente('E2E_EMAIL'), password: ambiente('E2E_PASSWORD') });
    definirSessao(entrada.token, entrada.expiresAt, ambiente('E2E_TENANT'));
    usuario = await api.me();
  });

  it('cria, edita, valida, publica, simula e ativa um fluxo', async () => {
    class RO {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    vi.stubGlobal('ResizeObserver', RO);
    const org = ambiente('E2E_ORG');
    render(<Fluxos api={api} me={usuario} />);

    // 1. Novo fluxo (a organização é a do tenant de teste).
    await userEvent.click(await screen.findByRole('button', { name: 'Novo fluxo' }));
    await userEvent.type(screen.getByLabelText('Nome'), 'Fluxo E2E');
    await userEvent.type(screen.getByLabelText(/Chave/), `e2e-${Date.now()}`);
    await userEvent.type(screen.getByLabelText('Organização'), org);
    await userEvent.click(screen.getByRole('button', { name: 'Criar e abrir o editor' }));
    const editor = await screen.findByRole('region', { name: /Editor do fluxo e2e-/ });

    // 2. Acrescenta uma pergunta SEM conectar: o servidor a aponta na validação.
    await userEvent.click(within(editor).getByRole('button', { name: '+ Pergunta' }));
    await within(editor).findByRole('region', { name: /Propriedades do nó question-1/ });
    await userEvent.click(within(editor).getByRole('button', { name: 'Validar' }));
    await within(editor).findByText(/problema\(s\) no fluxo/);
    await userEvent.click(within(editor).getByRole('button', { name: /^Problemas \(/ }));
    expect(within(editor).getAllByText(/question-1/).length).toBeGreaterThan(0);

    // 3. Remove o nó solto, edita o texto da mensagem inicial e salva (o rascunho fica válido).
    await userEvent.click(within(editor).getByRole('button', { name: 'Nó' }));
    await userEvent.click(within(editor).getByRole('button', { name: 'Remover nó' }));
    // fireEvent.click (sem mousedown): o d3-drag do React Flow não funciona no jsdom, mas a seleção por clique sim.
    fireEvent.click(within(editor).getByTestId('no-inicio'));
    const texto = await within(editor).findByLabelText('Texto');
    await userEvent.clear(texto);
    await userEvent.type(texto, 'Olá pelo teste funcional!');
    await userEvent.click(within(editor).getByRole('button', { name: 'Salvar rascunho' }));
    await within(editor).findByText(/Rascunho salvo\. O fluxo está pronto para publicar\./);

    // 4. Simula com o servidor real (sem nenhuma chamada externa).
    await userEvent.click(within(editor).getByRole('button', { name: 'Simular' }));
    await userEvent.click(within(editor).getByRole('button', { name: 'Simular conversa' }));
    const resultado = await within(editor).findByLabelText('Resultado da simulação');
    expect(resultado.textContent).toContain('Olá pelo teste funcional!');
    expect(resultado.textContent).toContain('Terminou o fluxo');

    // 5. Publica: a versão fica congelada e o editor passa a leitura.
    await userEvent.click(within(editor).getByRole('button', { name: 'Publicar' }));
    await within(editor).findByText(/publicada\. Ela não pode mais ser alterada/);
    expect(within(editor).queryByRole('button', { name: 'Publicar' })).toBeNull();
    await userEvent.click(within(editor).getByRole('button', { name: '← Fluxos' }));

    // 6. Ativação: o servidor confere organização, unidade e canal. Um canal inventado é recusado; o real passa e nasce DESLIGADA.
    await userEvent.click(await screen.findByRole('button', { name: 'Ativações' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Nova ativação' }));
    const form = screen.getByRole('form', { name: 'Nova ativação' });
    await userEvent.type(within(form).getByLabelText(/Canal/), '11111111-2222-4333-8444-555555555555');
    await userEvent.click(within(form).getByRole('button', { name: 'Criar ativação' }));
    await within(form).findByText(/channel-not-in-organization|escopo/);

    await userEvent.clear(within(form).getByLabelText(/Canal/));
    await userEvent.type(within(form).getByLabelText(/Canal/), ambiente('E2E_CHANNEL'));
    await userEvent.type(within(form).getByLabelText(/Unidade/), ambiente('E2E_UNIT'));
    await userEvent.click(within(form).getByRole('button', { name: 'Criar ativação' }));
    await screen.findByText(/Ativação criada, DESLIGADA/);
    await userEvent.click((await screen.findAllByRole('button', { name: /^Ligar a ativação de e2e-/ }))[0]!);
    await screen.findByText(/Ativação ligada/);
    await waitFor(() => expect(screen.getAllByText('Ligada').length).toBeGreaterThan(0));
  });
});
