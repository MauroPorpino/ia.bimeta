import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import { definirSessao } from '../api/session';
import type { FaqSuggestion, SemanticMetrics, SemanticSettings } from '../api/types';
import { chamadasDe, criarFetch, json, problema } from '../test/helpers';
import { ConhecimentoHibrido } from './ConhecimentoHibrido';

type Rotas = Parameters<typeof criarFetch>[0];

const CONFIG: SemanticSettings = { mode: 'Off', minScore: 0.9, minMargin: 0.05, updatedAt: null, isDefault: true };

const METRICAS: SemanticMetrics = {
  windowDays: 7,
  decisions: 12,
  answered: 6,
  wouldAnswer: 0,
  noCandidates: 3,
  belowThreshold: 1,
  ambiguous: 1,
  notAutoReply: 0,
  guardRejected: 1,
  stale: 0,
  unavailable: 0,
  faqHits: 10,
  semanticHits: 6,
  modelReplies: 4,
  handoffs: 2,
  commercialCallsAvoided: 16,
  semanticShareOfAiReplies: 30,
  latencyAverageMilliseconds: 55,
  latencyP95Milliseconds: 100,
};

let sequencia = 0;
function sugestao(sobrescrever: Partial<FaqSuggestion> = {}, payload: Partial<FaqSuggestion['payload']> = {}): FaqSuggestion {
  sequencia += 1;
  return {
    id: `bbbb0000-0000-4000-8000-${String(sequencia).padStart(12, '0')}`,
    kind: 'NewFaq',
    status: 'Pending',
    gapId: 'aaaa0000-0000-4000-8000-000000000001',
    payload: { question: 'Posso tomar café antes do exame?', answer: null, aliases: ['café antes da coleta'], targetQuestion: null, otherQuestion: null, gapIds: null, rationale: 'dúvida recorrente', ...payload },
    modelId: 'qwen-teste',
    createdAt: new Date().toISOString(),
    decidedAt: null,
    appliedDraftVersion: null,
    edits: 0,
    revision: 1,
    ...sobrescrever,
  };
}

function renderizar(rotas: Rotas, lacunaSelecionada: string | null = null) {
  definirSessao('token-sintetico', new Date(Date.now() + 3_600_000).toISOString());
  const f = criarFetch({
    'GET /agents/semantic/settings': json(CONFIG),
    'GET /agents/semantic/metrics': json(METRICAS),
    'GET /agents/faq-suggestions': json({ items: [], total: 0 }),
    ...rotas,
  });
  vi.stubGlobal('fetch', f);
  const onRascunhoAlterado = vi.fn();
  const onLacunasAlteradas = vi.fn();
  render(<ConhecimentoHibrido api={createApi()} lacunaSelecionada={lacunaSelecionada} onRascunhoAlterado={onRascunhoAlterado} onLacunasAlteradas={onLacunasAlteradas} />);
  return { f, onRascunhoAlterado, onLacunasAlteradas };
}

const config = () => within(screen.getByRole('region', { name: 'Busca semântica' }));
const sugestoes = () => within(screen.getByRole('region', { name: 'Sugestões da IA local' }));

async function abrir(usuario: ReturnType<typeof userEvent.setup>) {
  await usuario.click(screen.getByRole('button', { name: /Mostrar busca semântica e sugestões da IA/ }));
}

describe('ConhecimentoHibrido', () => {
  it('fica recolhido e só consulta o servidor depois de aberto', async () => {
    const { f } = renderizar({});
    expect(f).not.toHaveBeenCalled();
    expect(screen.queryByRole('region', { name: 'Busca semântica' })).toBeNull();

    await abrir(userEvent.setup());
    await config().findByLabelText('Modo');
    expect(chamadasDe(f, 'GET /agents/semantic/settings')).toHaveLength(1);
  });

  it('mostra o estado desligado e as métricas exatas, com a ressalva de que tokens e custo são estimativa', async () => {
    renderizar({});
    await abrir(userEvent.setup());

    expect(await config().findByText(/ainda não configurou a busca semântica: ela está desligada/)).toBeTruthy();
    expect((config().getByLabelText('Modo') as HTMLSelectElement).value).toBe('Off');
    expect(await config().findByText('16')).toBeTruthy(); // chamadas evitadas (soma exata)
    expect(config().getByText(/6 \(30% das respostas da IA\)/)).toBeTruthy();
    expect(config().getByText(/média 55 ms, p95 100 ms/)).toBeTruthy();
    expect(config().getByText(/estimativas/)).toBeTruthy();
  });

  it('o modo ativo exige a confirmação consciente e salva modo e limiares; desligar vale já', async () => {
    const salvar = vi.fn((req: { corpo: Record<string, unknown> | null }) => json({ ...CONFIG, mode: req.corpo!.mode, minScore: req.corpo!.minScore, minMargin: req.corpo!.minMargin, isDefault: false }));
    const { f } = renderizar({ 'PUT /agents/semantic/settings': (req) => salvar(req) });
    const usuario = userEvent.setup();
    await abrir(usuario);
    await config().findByLabelText('Modo');

    await usuario.selectOptions(config().getByLabelText('Modo'), 'Active');
    const botao = config().getByRole('button', { name: 'Salvar configuração' }) as HTMLButtonElement;
    expect(botao.disabled).toBe(true); // sem confirmar, não salva

    await usuario.click(config().getByRole('checkbox'));
    expect(botao.disabled).toBe(false);
    await usuario.clear(config().getByLabelText(/Limiar de similaridade/));
    await usuario.type(config().getByLabelText(/Limiar de similaridade/), '0,93');
    await usuario.click(botao);

    await waitFor(() => expect(chamadasDe(f, 'PUT /agents/semantic/settings')).toHaveLength(1));
    expect(chamadasDe(f, 'PUT /agents/semantic/settings')[0]!.corpo).toEqual({ mode: 'Active', minScore: 0.93, minMargin: 0.05 });
    expect(await config().findByText(/Configuração salva \(Ativo/)).toBeTruthy();
  });

  it('limiar fora do intervalo seguro impede salvar', async () => {
    renderizar({});
    const usuario = userEvent.setup();
    await abrir(usuario);
    await config().findByLabelText('Modo');
    await usuario.clear(config().getByLabelText(/Limiar de similaridade/));
    await usuario.type(config().getByLabelText(/Limiar de similaridade/), '0.3');
    expect((config().getByRole('button', { name: 'Salvar configuração' }) as HTMLButtonElement).disabled).toBe(true);
    expect(config().getByText(/Informe o limiar entre 0,60 e 1/)).toBeTruthy();
  });

  it('403 na configuração mostra a mensagem de permissão, sem derrubar a tela', async () => {
    renderizar({ 'GET /agents/semantic/settings': problema(403, 'Acesso negado'), 'GET /agents/semantic/metrics': problema(403, 'Acesso negado') });
    await abrir(userEvent.setup());
    expect((await config().findAllByRole('alert'))[0]!.textContent).toMatch(/não tem permissão/);
  });
});

describe('SugestoesDaIa', () => {
  it('lista as pendentes e mostra a revisão; entrada nova sem resposta não pode ser aceita até escrever a resposta', async () => {
    const s = sugestao();
    const aceita = { ...s, status: 'Approved', appliedDraftVersion: 4, revision: 3 };
    const { f, onRascunhoAlterado } = renderizar({
      'GET /agents/faq-suggestions': json({ items: [s], total: 1 }),
      [`PUT /agents/faq-suggestions/${s.id}`]: json({ ...s, payload: { ...s.payload, answer: 'Pode, sem açúcar.' }, edits: 1, revision: 2 }),
      [`POST /agents/faq-suggestions/${s.id}/approve`]: json(aceita),
    });
    const usuario = userEvent.setup();
    await abrir(usuario);

    await usuario.click(await sugestoes().findByRole('button', { name: /Entrada nova/ }));
    const revisao = within(screen.getByRole('region', { name: 'Revisão da sugestão' }));
    expect(revisao.getByText(/Motivo apontado pela IA: dúvida recorrente/)).toBeTruthy();
    const aceitar = revisao.getByRole('button', { name: 'Aceitar e enviar ao rascunho' }) as HTMLButtonElement;
    expect(aceitar.disabled).toBe(true);
    expect(revisao.getByText(/A IA não escreve respostas por conta própria/)).toBeTruthy();

    await usuario.type(revisao.getByLabelText(/Resposta aprovada/), 'Pode, sem açúcar.');
    expect(aceitar.disabled).toBe(false);
    await usuario.click(aceitar);

    await waitFor(() => expect(chamadasDe(f, `POST /agents/faq-suggestions/${s.id}/approve`)).toHaveLength(1));
    // A edição pendente é salva ANTES de aceitar: o que se vê é o que se aceita.
    expect(chamadasDe(f, `PUT /agents/faq-suggestions/${s.id}`)[0]!.corpo).toMatchObject({ answer: 'Pode, sem açúcar.', question: 'Posso tomar café antes do exame?' });
    expect(await sugestoes().findByText(/Enviada ao rascunho \(versão 4\). Nada foi publicado./)).toBeTruthy();
    expect(onRascunhoAlterado).toHaveBeenCalled();
  });

  it('rejeitar não altera o rascunho', async () => {
    const s = sugestao({}, { answer: 'Pode.' });
    const { f, onRascunhoAlterado } = renderizar({
      'GET /agents/faq-suggestions': json({ items: [s], total: 1 }),
      [`POST /agents/faq-suggestions/${s.id}/reject`]: json({ ...s, status: 'Rejected' }),
    });
    const usuario = userEvent.setup();
    await abrir(usuario);
    await usuario.click(await sugestoes().findByRole('button', { name: /Entrada nova/ }));
    await usuario.click(within(screen.getByRole('region', { name: 'Revisão da sugestão' })).getByRole('button', { name: 'Rejeitar' }));

    await waitFor(() => expect(chamadasDe(f, `POST /agents/faq-suggestions/${s.id}/reject`)).toHaveLength(1));
    expect(await sugestoes().findByText('Sugestão rejeitada.')).toBeTruthy();
    expect(onRascunhoAlterado).not.toHaveBeenCalled();
  });

  it('duplicata é só um achado: reconhecer não altera o rascunho e mostra as duas entradas', async () => {
    const s = sugestao({ kind: 'Duplicate', gapId: null }, { question: null, aliases: null, targetQuestion: 'Preciso de jejum?', otherQuestion: 'Tenho que fazer jejum?' });
    const { onRascunhoAlterado } = renderizar({
      'GET /agents/faq-suggestions': json({ items: [s], total: 1 }),
      [`POST /agents/faq-suggestions/${s.id}/approve`]: json({ ...s, status: 'Approved' }),
    });
    const usuario = userEvent.setup();
    await abrir(usuario);
    await usuario.click(await sugestoes().findByRole('button', { name: /Possível duplicata/ }));
    const revisao = within(screen.getByRole('region', { name: 'Revisão da sugestão' }));
    expect(revisao.getByText('Preciso de jejum?')).toBeTruthy();
    expect(revisao.getByText('Tenho que fazer jejum?')).toBeTruthy();

    await usuario.click(revisao.getByRole('button', { name: 'Reconhecer' }));
    expect(await sugestoes().findByText(/O FAQ não foi alterado/)).toBeTruthy();
    expect(onRascunhoAlterado).not.toHaveBeenCalled();
  });

  it('pedir sugestão: o botão da lacuna só vale com uma lacuna selecionada; o resultado vazio explica o motivo sem inventar sugestão', async () => {
    const { f } = renderizar({ 'POST /agents/faq-suggestions/generate/cluster': json({ suggestions: [], skipped: 0, skipReason: 'not-enough-gaps' }) });
    const usuario = userEvent.setup();
    await abrir(usuario);
    expect((await sugestoes().findByRole('button', { name: 'Sugerir para a lacuna selecionada' }) as HTMLButtonElement).disabled).toBe(true);

    await usuario.click(sugestoes().getByRole('button', { name: 'Agrupar lacunas parecidas' }));
    expect(await sugestoes().findByText(/pelo menos duas lacunas abertas/)).toBeTruthy();
    expect(chamadasDe(f, 'POST /agents/faq-suggestions/generate/cluster')).toHaveLength(1);
  });

  it('gerar para a lacuna selecionada chama a rota dela e abre a sugestão criada', async () => {
    const lacunaId = 'aaaa0000-0000-4000-8000-0000000000aa';
    const nova = sugestao({ gapId: lacunaId });
    const { f } = renderizar(
      {
        [`POST /agents/faq-suggestions/generate/gap/${lacunaId}`]: json({ suggestions: [nova], skipped: 0, skipReason: null }),
        'GET /agents/faq-suggestions': json({ items: [nova], total: 1 }),
      },
      lacunaId,
    );
    const usuario = userEvent.setup();
    await abrir(usuario);
    await usuario.click(await sugestoes().findByRole('button', { name: 'Sugerir para a lacuna selecionada' }));

    await waitFor(() => expect(chamadasDe(f, `POST /agents/faq-suggestions/generate/gap/${lacunaId}`)).toHaveLength(1));
    expect(await sugestoes().findByText('1 sugestão(ões) para revisar.')).toBeTruthy();
    expect(await screen.findByRole('region', { name: 'Revisão da sugestão' })).toBeTruthy();
  });

  it('IA local indisponível (503) vira mensagem clara e nada muda', async () => {
    renderizar({ 'POST /agents/faq-suggestions/generate/analysis': problema(503, 'IA local indisponível', 'x', 'unreachable') });
    const usuario = userEvent.setup();
    await abrir(usuario);
    await usuario.click(await sugestoes().findByRole('button', { name: 'Procurar duplicatas e contradições' }));
    expect((await sugestoes().findByRole('alert')).textContent).toMatch(/A IA local não respondeu agora\. Nada foi alterado/);
  });

  it('IA local desligada neste ambiente é dita com clareza', async () => {
    renderizar({ 'POST /agents/faq-suggestions/generate/analysis': problema(503, 'IA local indisponível', 'x', 'disabled') });
    const usuario = userEvent.setup();
    await abrir(usuario);
    await usuario.click(await sugestoes().findByRole('button', { name: 'Procurar duplicatas e contradições' }));
    expect((await sugestoes().findByRole('alert')).textContent).toMatch(/desligadas neste ambiente/);
  });

  it('conflito ao aceitar (decidida por outra pessoa) avisa e relê a lista', async () => {
    const s = sugestao({}, { answer: 'Pode.' });
    const { f } = renderizar({
      'GET /agents/faq-suggestions': json({ items: [s], total: 1 }),
      [`POST /agents/faq-suggestions/${s.id}/approve`]: problema(409, 'Conflito', 'x', 'suggestion-decided'),
    });
    const usuario = userEvent.setup();
    await abrir(usuario);
    await usuario.click(await sugestoes().findByRole('button', { name: /Entrada nova/ }));
    await usuario.click(within(screen.getByRole('region', { name: 'Revisão da sugestão' })).getByRole('button', { name: 'Aceitar e enviar ao rascunho' }));
    expect((await within(screen.getByRole('region', { name: 'Revisão da sugestão' })).findByRole('alert')).textContent).toMatch(/já foi decidida/);
    await waitFor(() => expect(chamadasDe(f, 'GET /agents/faq-suggestions').length).toBeGreaterThan(1));
  });
});
