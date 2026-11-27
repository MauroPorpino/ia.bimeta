import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { ConfiguracaoDeIa, CurrentUser, DiagnosticoDeIa, ModeloDeIa, ProvedorDeIa } from '../api/types';
import { ADMINISTRADOR, chamadasDe, criarFetch, json, problema } from '../test/helpers';
import { IaProvedores } from './IaProvedores';

const CHAVE = 'sk-ant-CHAVE-SINTETICA-0123456789';
const TODAS = ['AiSettingsRead', 'AiSettingsManage', 'AiCredentialManage', 'AiConnectivityTest', 'AiGenerationTest'];

function eu(permissoes: string[]): CurrentUser {
  return { ...ADMINISTRADOR, permissions: permissoes.map((permission) => ({ permission, organizationId: null })) };
}

function diag(sobrescrever: Partial<DiagnosticoDeIa> = {}): DiagnosticoDeIa {
  return {
    state: 'Verified',
    code: 'ok',
    checkedAt: '2026-11-27T12:00:00Z',
    stale: false,
    staleReason: null,
    latencyMs: 120,
    reachable: true,
    authenticated: true,
    modelAvailable: true,
    conversationCapable: null,
    inputTokens: null,
    outputTokens: null,
    usageCertainty: null,
    replyPreview: null,
    ...sobrescrever,
  };
}

function modelo(id: string, sobrescrever: Partial<ModeloDeIa> = {}): ModeloDeIa {
  return { modelId: id, profile: 'atendimento-rapido', contextWindowTokens: 200000, maxOutputTokens: 4096, isDefault: false, connectivity: null, generation: null, ...sobrescrever };
}

function provedor(id: string, sobrescrever: Partial<ProvedorDeIa> = {}): ProvedorDeIa {
  const ollama = id === 'ollama';
  return {
    providerId: id,
    implemented: true,
    authorized: true,
    supply: ollama ? 'Infrastructure' : 'Byok',
    credential: { required: !ollama, present: false, fingerprint: null, updatedAt: null },
    isDefault: false,
    connectivityCheckSupported: true,
    models: [modelo(ollama ? 'qwen3:4b' : id === 'openai' ? 'gpt-6-luna' : 'claude-haiku-5-5')],
    ...sobrescrever,
  };
}

function config(sobrescrever: Partial<ConfiguracaoDeIa> = {}): ConfiguracaoDeIa {
  return {
    configured: false,
    defaultProviderId: null,
    defaultModelId: null,
    status: null,
    updatedAt: null,
    warnings: ['no-default'],
    providers: [provedor('ollama'), provedor('anthropic'), provedor('openai')],
    ...sobrescrever,
  };
}

function renderizar(permissoes: string[], dados: ConfiguracaoDeIa, extras: Parameters<typeof criarFetch>[0] = {}) {
  const f = criarFetch({ 'GET /ai/settings': json(dados), ...extras });
  vi.stubGlobal('fetch', f);
  render(<IaProvedores api={createApi()} me={eu(permissoes)} />);
  return f;
}

function cartao(nome: string) {
  return screen.findByRole('article', { name: `Provedor ${nome}` });
}

afterEach(() => {
  vi.restoreAllMocks();
  window.localStorage.clear();
  window.sessionStorage.clear();
});

describe('Configurações → Inteligência artificial: provedores', () => {
  it('mostra OpenAI, Anthropic e Ollama sem apresentar nenhum como operacional só porque existe o adaptador', async () => {
    renderizar(TODAS, config());

    const openai = await cartao('OpenAI');
    const anthropic = await cartao('Anthropic');
    const ollama = await cartao('Ollama (IA local)');
    expect(within(openai).getByText(/Implementado; autorizado \(1 modelo/)).toBeTruthy();
    expect(within(openai).getByText('Não cadastrada')).toBeTruthy();
    expect(within(anthropic).getByText('Não: falta a credencial.')).toBeTruthy();
    expect(within(openai).getByText('Conectividade:').parentElement?.textContent).toMatch(/não verificada/);
    expect(within(openai).getByText('Geração:').parentElement?.textContent).toMatch(/não testada/);
    expect(within(ollama).getByText('Servidor da infraestrutura, sem chave')).toBeTruthy();
    expect(screen.getByRole('status', { name: '' }).textContent).toMatch(/ainda não escolheu um provedor/);
  });

  it('para o Ollama não há campo nem botão de chave, e nenhum campo de endereço de servidor existe em lugar nenhum', async () => {
    renderizar(TODAS, config());
    const ollama = await cartao('Ollama (IA local)');

    expect(within(ollama).queryByRole('button', { name: /credencial/i })).toBeNull();
    expect(within(ollama).getByText('Não se aplica: este provedor não usa chave.')).toBeTruthy();
    expect(screen.queryByLabelText(/endereço|url|servidor/i)).toBeNull();
    expect(screen.queryByRole('textbox')).toBeNull();
  });

  it('o Ollama mostra os seis estados separados, sem presumir um a partir do outro', async () => {
    const ollama = provedor('ollama', {
      models: [
        modelo('qwen3:4b', { connectivity: diag({ reachable: true, modelAvailable: true, conversationCapable: true }), generation: null, isDefault: true }),
      ],
    });
    renderizar(TODAS, config({ providers: [ollama] }));

    const estados = await screen.findByRole('list', { name: '' }).then(() => screen.getByLabelText('Estados do modelo qwen3:4b'));
    const valor = (rotulo: string) => within(estados).getByText(rotulo).nextElementSibling?.textContent;
    expect(valor('Modelo cadastrado no catálogo')).toBe('sim');
    expect(valor('Modelo habilitado')).toMatch(/^sim/);
    expect(valor('Servidor acessível')).toBe('sim');
    expect(valor('Modelo instalado')).toBe('sim');
    expect(valor('Capacidade conversacional declarada')).toBe('sim');
    expect(valor('Compatibilidade funcional validada')).toMatch(/não validada/);
  });

  it('um resultado desatualizado nunca aparece como confirmação', async () => {
    const dados = config({
      providers: [provedor('anthropic', { credential: { required: true, present: true, fingerprint: 'abc123abc123', updatedAt: '2026-11-27T10:00:00Z' }, models: [modelo('claude-haiku-5-5', { connectivity: diag({ stale: true, staleReason: 'credential-changed' }) })] })],
    });
    renderizar(TODAS, dados);

    const anthropic = await cartao('Anthropic');
    const linha = within(anthropic).getByText('Conectividade:').parentElement!;
    expect(linha.textContent).toMatch(/desatualizado: a credencial mudou/);
  });

  it('mostra o motivo de uma falha de autenticação e nunca "verificada"', async () => {
    const dados = config({ providers: [provedor('openai', { credential: { required: true, present: true, fingerprint: 'f', updatedAt: null }, models: [modelo('gpt-6-luna', { connectivity: diag({ state: 'AuthenticationFailed', code: 'unauthorized', authenticated: false }) })] })] });
    renderizar(TODAS, dados);
    const openai = await cartao('OpenAI');
    expect(within(openai).getByText('Conectividade:').parentElement?.textContent).toMatch(/Falha de autenticação \(credencial recusada pelo provedor\)/);
    expect(within(openai).queryByText(/Conectividade verificada/)).toBeNull();
  });
});

describe('Configurações → Inteligência artificial: permissões', () => {
  it('quem só consulta não vê nenhuma ação', async () => {
    renderizar(['AiSettingsRead'], config());
    await cartao('OpenAI');

    expect(screen.queryByRole('button', { name: /credencial|Testar/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Modelo padrão' })).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Modelo padrão' }));
    expect(screen.queryByRole('button', { name: 'Salvar padrão' })).toBeNull();
    expect(screen.getByText(/falta a permissão de gerir a configuração de IA/)).toBeTruthy();
  });

  it('cada ação aparece só com a sua permissão', async () => {
    renderizar(['AiSettingsRead', 'AiConnectivityTest'], config({ providers: [provedor('anthropic', { credential: { required: true, present: true, fingerprint: 'f', updatedAt: null } })] }));
    const c = await cartao('Anthropic');

    expect(within(c).getByRole('button', { name: 'Testar conexão' })).toBeTruthy();
    expect(within(c).queryByRole('button', { name: 'Testar geração' })).toBeNull();
    expect(within(c).queryByRole('button', { name: /credencial/i })).toBeNull();
  });

  it('a permissão ampla de configurar o tenant também vale', async () => {
    renderizar(['TenantSettingsManage'], config());
    const c = await cartao('OpenAI');
    expect(within(c).getByRole('button', { name: 'Cadastrar credencial' })).toBeTruthy();
  });

  it('a falha 403 do servidor vira "permissão insuficiente"', async () => {
    renderizar(TODAS, config(), { 'GET /ai/settings': problema(403, 'Acesso negado') });
    expect((await screen.findByRole('alert')).textContent).toMatch(/Permissão insuficiente/);
  });
});

describe('Configurações → Inteligência artificial: credenciais', () => {
  it('o campo é de senha, a chave é enviada uma vez, o campo some, nada é guardado no navegador e a tela recarrega', async () => {
    let presente = false;
    const f = renderizar(TODAS, config(), {
      'GET /ai/settings': () => json(config({ providers: [provedor('anthropic', { credential: { required: true, present: presente, fingerprint: presente ? 'aabbccddeeff' : null, updatedAt: presente ? '2026-11-27T12:00:00Z' : null } })] })),
      'PUT /ai/providers/anthropic/credential': () => {
        presente = true;
        return new Response(null, { status: 204 });
      },
    });
    const c = await cartao('Anthropic');
    await userEvent.click(within(c).getByRole('button', { name: 'Cadastrar credencial' }));
    const campo = within(c).getByLabelText('Chave de API da Anthropic') as HTMLInputElement;
    expect(campo.type).toBe('password');
    expect(campo.autocomplete).toBe('new-password');

    await userEvent.type(campo, CHAVE);
    await userEvent.click(within(c).getByRole('button', { name: 'Salvar credencial' }));

    await waitFor(() => expect(within(c).getByText(/Cadastrada \(identificador aabbccddeeff/)).toBeTruthy());
    expect(chamadasDe(f, 'PUT /ai/providers/anthropic/credential')).toEqual([{ corpo: { apiKey: CHAVE }, consulta: expect.any(URLSearchParams) }]);
    expect(within(c).queryByLabelText('Chave de API da Anthropic')).toBeNull();
    expect(document.body.innerHTML).not.toContain(CHAVE);
    expect(JSON.stringify({ ...window.localStorage })).not.toContain(CHAVE);
    expect(JSON.stringify({ ...window.sessionStorage })).not.toContain(CHAVE);
    expect(chamadasDe(f, 'GET /ai/settings').length).toBeGreaterThanOrEqual(2); // atualizou depois da operação
  });

  it('numa falha o campo é limpo, a chave não aparece na mensagem e o formulário continua para tentar de novo', async () => {
    renderizar(TODAS, config(), { 'PUT /ai/providers/openai/credential': problema(400, 'Dados inválidos', `O valor ${CHAVE} é inválido`) });
    const c = await cartao('OpenAI');
    await userEvent.click(within(c).getByRole('button', { name: 'Cadastrar credencial' }));
    await userEvent.type(within(c).getByLabelText('Chave de API da OpenAI'), CHAVE);
    await userEvent.click(within(c).getByRole('button', { name: 'Salvar credencial' }));

    const alerta = await within(c).findByRole('alert');
    expect(alerta.textContent).not.toContain(CHAVE);
    expect((within(c).getByLabelText('Chave de API da OpenAI') as HTMLInputElement).value).toBe('');
  });

  it('cancelar apaga o que foi digitado', async () => {
    renderizar(TODAS, config());
    const c = await cartao('OpenAI');
    await userEvent.click(within(c).getByRole('button', { name: 'Cadastrar credencial' }));
    await userEvent.type(within(c).getByLabelText('Chave de API da OpenAI'), CHAVE);
    await userEvent.click(within(c).getByRole('button', { name: 'Cancelar' }));
    await userEvent.click(within(c).getByRole('button', { name: 'Cadastrar credencial' }));
    expect((within(c).getByLabelText('Chave de API da OpenAI') as HTMLInputElement).value).toBe('');
  });

  it('para uma credencial existente oferece substituir e remover, e a remoção exige confirmação', async () => {
    const dados = config({ providers: [provedor('openai', { credential: { required: true, present: true, fingerprint: 'abc', updatedAt: null } })] });
    const f = renderizar(TODAS, dados, { 'DELETE /ai/providers/openai/credential': json({ revoked: true, wasDefaultProvider: false }) });
    const c = await cartao('OpenAI');

    expect(within(c).getByRole('button', { name: 'Substituir credencial' })).toBeTruthy();
    await userEvent.click(within(c).getByRole('button', { name: 'Remover credencial' }));
    expect(chamadasDe(f, 'DELETE /ai/providers/openai/credential')).toHaveLength(0); // ainda não removeu
    expect(within(c).getByRole('alertdialog').textContent).toMatch(/não pode ser recuperado/);

    await userEvent.click(within(c).getByRole('button', { name: 'Confirmar remoção' }));
    await waitFor(() => expect(chamadasDe(f, 'DELETE /ai/providers/openai/credential')).toHaveLength(1));
    expect((await within(c).findByRole('status')).textContent).toMatch(/Credencial removida/);
  });

  it('remover a credencial do provedor padrão avisa o impacto no atendimento, sem bloquear', async () => {
    const dados = config({ configured: true, defaultProviderId: 'openai', defaultModelId: 'gpt-6-luna', warnings: [], providers: [provedor('openai', { isDefault: true, credential: { required: true, present: true, fingerprint: 'abc', updatedAt: null } })] });
    renderizar(TODAS, dados);
    const c = await cartao('OpenAI');

    await userEvent.click(within(c).getByRole('button', { name: 'Remover credencial' }));
    expect(within(c).getByRole('alertdialog').textContent).toMatch(/provedor padrão.*deixa de responder.*sem troca automática.*nunca\s+é bloqueada/s);
    expect(within(c).getByRole('button', { name: 'Confirmar remoção' }).hasAttribute('disabled')).toBe(false);
  });
});

describe('Configurações → Inteligência artificial: testes', () => {
  const comChave = (extras: Partial<ProvedorDeIa> = {}) => config({ providers: [provedor('anthropic', { credential: { required: true, present: true, fingerprint: 'f', updatedAt: null }, ...extras })] });

  it('o teste de conexão mostra o resultado com cada item verificado e não é uma geração', async () => {
    const resultado = { providerId: 'anthropic', modelId: 'claude-haiku-5-5', supported: true, superseded: false, result: diag() };
    const f = renderizar(TODAS, comChave(), { 'POST /ai/providers/anthropic/connectivity-test': json(resultado) });
    const c = await cartao('Anthropic');

    await userEvent.click(within(c).getByRole('button', { name: 'Testar conexão' }));

    await waitFor(() => expect(chamadasDe(f, 'POST /ai/providers/anthropic/connectivity-test')).toHaveLength(1));
    expect((await within(c).findAllByRole('status')).map((s) => s.textContent).join(' ')).toMatch(/Conectividade verificada \(claude-haiku-5-5\): acessível sim, autenticação sim, modelo disponível sim, capacidade de conversa declarada não verificado/);
    expect(chamadasDe(f, 'POST /ai/providers/anthropic/generation-test')).toHaveLength(0);
  });

  it('quando o provedor não tem verificação própria informa e não a substitui por geração', async () => {
    const f = renderizar(TODAS, comChave(), { 'POST /ai/providers/anthropic/connectivity-test': json({ providerId: 'anthropic', modelId: 'm', supported: false, result: null, superseded: false }) });
    const c = await cartao('Anthropic');
    await userEvent.click(within(c).getByRole('button', { name: 'Testar conexão' }));
    expect((await within(c).findByText(/não oferece uma verificação sem geração de texto: nada foi verificado/)).textContent).toBeTruthy();
    expect(chamadasDe(f, 'POST /ai/providers/anthropic/generation-test')).toHaveLength(0);
  });

  it('o teste de geração exige a confirmação de cobrança e só então envia confirmed verdadeiro', async () => {
    const resultado = { providerId: 'anthropic', modelId: 'claude-haiku-5-5', succeeded: true, aiRequestId: 'r1', superseded: false, result: diag({ state: 'Succeeded', code: null, inputTokens: 40, outputTokens: 2, replyPreview: 'OK' }) };
    const f = renderizar(TODAS, comChave(), { 'POST /ai/providers/anthropic/generation-test': json(resultado) });
    const c = await cartao('Anthropic');

    await userEvent.click(within(c).getByRole('button', { name: 'Testar geração' }));
    const executar = within(c).getByRole('button', { name: 'Executar teste de geração' });
    expect(executar.hasAttribute('disabled')).toBe(true);
    expect(within(c).getByRole('alertdialog').textContent).toMatch(/podem cobrar por este teste/);
    expect(chamadasDe(f, 'POST /ai/providers/anthropic/generation-test')).toHaveLength(0);

    await userEvent.click(within(c).getByRole('checkbox'));
    await userEvent.click(executar);

    await waitFor(() => expect(chamadasDe(f, 'POST /ai/providers/anthropic/generation-test')).toEqual([{ corpo: { modelId: 'claude-haiku-5-5', confirmed: true }, consulta: expect.any(URLSearchParams) }]));
    expect((await within(c).findAllByRole('status')).map((s) => s.textContent).join(' ')).toMatch(/Geração testada com sucesso \(claude-haiku-5-5\).*Resposta: "OK"/s);
  });

  it('uma falha do teste de geração aparece como falha, com o motivo, e uma credencial trocada durante o teste é avisada', async () => {
    const falha = { providerId: 'anthropic', modelId: 'claude-haiku-5-5', succeeded: false, aiRequestId: 'r2', superseded: true, result: diag({ state: 'Failed', code: 'rate-limited' }) };
    renderizar(TODAS, comChave(), { 'POST /ai/providers/anthropic/generation-test': json(falha) });
    const c = await cartao('Anthropic');
    await userEvent.click(within(c).getByRole('button', { name: 'Testar geração' }));
    await userEvent.click(within(c).getByRole('checkbox'));
    await userEvent.click(within(c).getByRole('button', { name: 'Executar teste de geração' }));

    const alerta = (await within(c).findAllByRole('status')).map((s) => s.textContent).join(' ');
    expect(alerta).toMatch(/Falha no teste de geração \(claude-haiku-5-5\): limite de taxa do provedor/);
    expect(alerta).toMatch(/credencial foi alterada durante o teste/);
  });

  it('sem credencial os testes ficam desabilitados e a falha do servidor vira mensagem clara', async () => {
    renderizar(TODAS, config({ providers: [provedor('openai')] }), { 'POST /ai/providers/openai/connectivity-test': problema(503, 'Indisponível') });
    const c = await cartao('OpenAI');
    expect(within(c).getByRole('button', { name: 'Testar conexão' }).hasAttribute('disabled')).toBe(true);
    expect(within(c).getByRole('button', { name: 'Testar geração' }).hasAttribute('disabled')).toBe(true);
  });

  it('o excesso de testes é explicado (429)', async () => {
    renderizar(TODAS, comChave(), { 'POST /ai/providers/anthropic/connectivity-test': problema(429, 'Operação recusada', 'x', 'rate-limited') });
    const c = await cartao('Anthropic');
    await userEvent.click(within(c).getByRole('button', { name: 'Testar conexão' }));
    expect((await within(c).findByRole('alert')).textContent).toMatch(/Muitos testes em pouco tempo/);
  });
});

describe('Configurações → Inteligência artificial: modelo padrão', () => {
  async function abrirPadrao() {
    await userEvent.click(await screen.findByRole('button', { name: 'Modelo padrão' }));
    return screen.findByRole('region', { name: 'Modelo padrão' });
  }

  it('salva o padrão escolhido, mostra os avisos de não testado e atualiza a tela', async () => {
    const f = renderizar(TODAS, config({ providers: [provedor('ollama'), provedor('openai', { credential: { required: true, present: true, fingerprint: 'f', updatedAt: null } })] }), {
      'PUT /ai/settings/default': json({ warnings: ['connectivity-not-verified', 'generation-not-tested'] }),
    });
    const regiao = await abrirPadrao();

    await userEvent.selectOptions(within(regiao).getByLabelText('Provedor'), 'openai');
    await waitFor(() => expect((within(regiao).getByLabelText('Modelo') as HTMLSelectElement).value).toBe('gpt-6-luna'));
    expect(within(regiao).getByLabelText('Características do modelo').textContent).toMatch(/200\.000 tokens/);
    await userEvent.click(within(regiao).getByRole('button', { name: 'Salvar padrão' }));

    await waitFor(() => expect(chamadasDe(f, 'PUT /ai/settings/default')).toEqual([{ corpo: { providerId: 'openai', modelId: 'gpt-6-luna' }, consulta: expect.any(URLSearchParams) }]));
    const status = await within(regiao).findByRole('status');
    expect(status.textContent).toMatch(/Nenhuma chamada foi feita ao provedor/);
    expect(status.textContent).toMatch(/conectividade deste modelo ainda não foi verificada/);
    expect(status.textContent).toMatch(/geração com este modelo ainda não foi testada/);
    expect(chamadasDe(f, 'GET /ai/settings').length).toBeGreaterThanOrEqual(2);
  });

  it('um provedor sem credencial não pode ser salvo como padrão e a tela explica por quê', async () => {
    const f = renderizar(TODAS, config({ providers: [provedor('openai')] }));
    const regiao = await abrirPadrao();

    expect(within(regiao).getByRole('button', { name: 'Salvar padrão' }).hasAttribute('disabled')).toBe(true);
    expect(within(regiao).getByText(/Cadastre a credencial deste provedor/)).toBeTruthy();
    expect(chamadasDe(f, 'PUT /ai/settings/default')).toHaveLength(0);
  });

  it('a recusa do servidor (modelo não autorizado) aparece como mensagem, sem trocar nada em silêncio', async () => {
    renderizar(TODAS, config({ providers: [provedor('ollama')] }), { 'PUT /ai/settings/default': problema(400, 'Operação recusada', 'x', 'model-not-authorized') });
    const regiao = await abrirPadrao();
    await userEvent.click(within(regiao).getByRole('button', { name: 'Salvar padrão' }));
    expect((await within(regiao).findByRole('alert')).textContent).toMatch(/não está autorizado/);
  });

  it('mostra o padrão atual e os avisos de indisponibilidade da configuração', async () => {
    renderizar(TODAS, config({ configured: true, defaultProviderId: 'openai', defaultModelId: 'gpt-6-luna', warnings: ['default-credential-missing'], providers: [provedor('openai', { isDefault: true, models: [modelo('gpt-6-luna', { isDefault: true })] })] }));
    expect((await screen.findAllByRole('status'))[0]!.textContent).toMatch(/sem credencial: a IA não responde.*Não há troca automática/s);
    const regiao = await abrirPadrao();
    expect(within(regiao).getByText(/Padrão atual: OpenAI \/ gpt-6-luna/)).toBeTruthy();
  });
});

describe('Configurações → Inteligência artificial: estados da tela', () => {
  it('mostra carregamento e depois, se o serviço falha, uma mensagem de indisponibilidade com nova tentativa', async () => {
    let tentativa = 0;
    renderizar(TODAS, config(), {
      'GET /ai/settings': () => (++tentativa === 1 ? problema(503, 'Indisponível') : json(config())),
    });
    expect(screen.getByText(/Carregando a configuração de IA/)).toBeTruthy();
    const alerta = await screen.findByRole('alert');
    expect(alerta.textContent).toMatch(/Serviço indisponível/);

    await userEvent.click(within(alerta).getByRole('button', { name: 'Tentar de novo' }));
    expect(await cartao('OpenAI')).toBeTruthy();
  });

  it('sem configuração ainda mostra o aviso de ausência de padrão', async () => {
    renderizar(TODAS, config());
    await cartao('OpenAI');
    expect(screen.getAllByRole('status')[0]!.textContent).toMatch(/não responde aos clientes e a conversa vai para um atendente/);
  });
});
