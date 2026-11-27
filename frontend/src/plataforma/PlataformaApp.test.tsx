import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { chamadasDe, criarFetch, json, problema } from '../test/helpers';
import { CHAVE_SESSAO_PLATAFORMA, createPlataformaApi, definirSessaoPlataforma, reiniciarSessaoPlataformaParaTeste } from './api';
import { PlataformaApp } from './PlataformaApp';
import type { CompanyDetail, CompanyListItem, OperatorDetail, OperatorItem, Overview, PlatformMe, SeatsView } from './types';

// Sem jest-dom neste projeto: três verificações mínimas (por `dom(...)`).
interface MatchersDoDom<R = unknown> {
  toHaveTextContent(texto: string): R;
  toBeDisabled(): R;
  toBeEnabled(): R;
}
const dom = (valor: unknown) => expect(valor) as unknown as MatchersDoDom;
expect.extend({
  toHaveTextContent(recebido: Element, texto: string) {
    const conteudo = recebido.textContent ?? '';
    return { pass: conteudo.includes(texto), message: () => `esperava que "${conteudo}" contivesse "${texto}"` };
  },
  toBeDisabled(recebido: HTMLButtonElement) {
    return { pass: recebido.disabled === true, message: () => 'esperava o elemento desabilitado' };
  },
  toBeEnabled(recebido: HTMLButtonElement) {
    return { pass: recebido.disabled === false, message: () => 'esperava o elemento habilitado' };
  },
});

const TOKEN_DE_USO = 'ibr_TOKEN-SINTETICO-XYZ';
const DEPOIS = new Date(Date.now() + 3_600_000).toISOString();

afterEach(() => {
  reiniciarSessaoPlataformaParaTeste();
});

function eu(permissoes: string[]): PlatformMe {
  return {
    id: 'op000000-0000-4000-8000-000000000001',
    email: 'ana@bimeta.local',
    displayName: 'Ana Operadora',
    grants: [],
    preferredLocale: null,
    permissions: permissoes.map((permission) => ({ permission, organizationId: null })),
  };
}

const TUDO = [
  'PlatformOverviewRead',
  'PlatformTenantRead',
  'PlatformTenantCreate',
  'PlatformTenantUpdate',
  'PlatformTenantQuotaManage',
  'PlatformFirstAdminManage',
  'PlatformTenantSuspend',
  'PlatformOperatorManage',
  'PlatformPermissionManage',
  'PlatformAuditRead',
];

function vagas(sobrescrever: Partial<SeatsView> = {}): SeatsView {
  return { limit: 10, registrationEnabled: true, isExplicit: true, active: 3, pendingReserved: 1, pendingExpired: 0, inactive: 0, used: 4, available: 6, excess: 0, ...sobrescrever };
}

function empresa(sobrescrever: Partial<CompanyDetail> = {}): CompanyDetail {
  return {
    company: {
      tenantId: 'te000000-0000-4000-8000-000000000001',
      slug: 'lab-exemplo',
      name: 'Laboratório Exemplo',
      status: 'Active',
      legalName: 'Lab Exemplo Ltda',
      document: '00.000.000/0001-00',
      contactName: 'Carla',
      contactEmail: 'carla@lab.local',
      createdAt: '2026-11-01T10:00:00Z',
      updatedAt: '2026-11-01T10:00:00Z',
      suspendedAt: null,
    },
    seats: vagas(),
    initialAdministrator: { email: 'admin@lab.local', displayName: 'Admin', status: 'Pending', hasLiveToken: true, createdAt: '2026-11-01T10:00:00Z' },
    statusHistory: [],
    ...sobrescrever,
  };
}

function itemDeLista(d: CompanyDetail): CompanyListItem {
  return { company: d.company, seats: d.seats };
}

const VISAO: Overview = {
  totalCompanies: 12,
  activeCompanies: 9,
  suspendedCompanies: 2,
  provisioningCompanies: 1,
  seats: { contracted: 100, active: 60, reserved: 5, available: 35, companiesWithLimit: 8 },
  companiesOverLimit: 1,
  companiesRegistrationDisabled: 2,
  legacy: { companies: 4, activeUsers: 37 },
  generatedAt: '2026-11-25T12:00:00Z',
};

function operador(sobrescrever: Partial<OperatorItem> = {}): OperatorItem {
  return { id: 'op000000-0000-4000-8000-000000000009', email: 'bia@bimeta.local', displayName: 'Bia Comercial', status: 'Active', role: 'PlatformCommercial', hasLiveFirstAccessToken: false, createdAt: '2026-11-02T10:00:00Z', ...sobrescrever };
}

function renderizar(permissoes: string[], rotas: Parameters<typeof criarFetch>[0] = {}, caminho = '/plataforma') {
  window.history.replaceState(null, '', caminho);
  definirSessaoPlataforma('tok-sintetico', DEPOIS);
  const fetchSimulado = criarFetch({
    'GET /auth/me': json(eu(permissoes)),
    'GET /platform/overview': json(VISAO),
    'GET /platform/companies': json({ items: [itemDeLista(empresa())], total: 1, page: 1, pageSize: 20 }),
    ...rotas,
  });
  const api = createPlataformaApi({ fetchFn: fetchSimulado });
  render(<PlataformaApp api={api} />);
  return fetchSimulado;
}

describe('login e primeiro acesso', () => {
  it('entra com e-mail e senha, guarda a sessão na chave própria e mostra o painel', async () => {
    window.history.replaceState(null, '', '/plataforma');
    const f = criarFetch({
      'POST /platform/auth/login': json({ token: 'tok-novo', expiresAt: DEPOIS }),
      'GET /auth/me': json(eu(['PlatformOverviewRead'])),
      'GET /platform/overview': json(VISAO),
    });
    render(<PlataformaApp api={createPlataformaApi({ fetchFn: f })} />);
    const u = userEvent.setup();
    await u.type(screen.getByLabelText('E-mail'), 'ana@bimeta.local');
    await u.type(screen.getByLabelText('Senha'), 'senha-sintetica-123');
    await u.click(screen.getByRole('button', { name: 'Entrar' }));

    expect(await screen.findByRole('heading', { name: 'Visão geral' })).toBeTruthy();
    expect(chamadasDe(f, 'POST /platform/auth/login')[0]?.corpo).toEqual({ email: 'ana@bimeta.local', password: 'senha-sintetica-123' });
    expect(sessionStorage.getItem(CHAVE_SESSAO_PLATAFORMA)).toContain('tok-novo');
    expect(sessionStorage.getItem('ia-bimeta.sessao')).toBeNull();
  });

  it('mostra o erro do login sem entrar', async () => {
    window.history.replaceState(null, '', '/plataforma');
    const f = criarFetch({ 'POST /platform/auth/login': problema(401, 'Credenciais inválidas', 'E-mail ou senha incorretos.') });
    render(<PlataformaApp api={createPlataformaApi({ fetchFn: f })} />);
    const u = userEvent.setup();
    await u.type(screen.getByLabelText('E-mail'), 'x@y.z');
    await u.type(screen.getByLabelText('Senha'), 'qualquer');
    await u.click(screen.getByRole('button', { name: 'Entrar' }));
    dom(await screen.findByRole('alert')).toHaveTextContent('E-mail ou senha incorretos.');
  });

  it('primeiro acesso: redime o token no tenant da plataforma e volta à entrada', async () => {
    window.history.replaceState(null, '', '/plataforma');
    const f = criarFetch({ 'POST /auth/password-reset/redeem': new Response(null, { status: 204 }) });
    render(<PlataformaApp api={createPlataformaApi({ fetchFn: f })} />);
    const u = userEvent.setup();
    await u.click(screen.getByRole('button', { name: /Primeiro acesso/ }));
    await u.type(screen.getByLabelText('E-mail'), 'ana@bimeta.local');
    await u.type(screen.getByLabelText('Token de primeiro acesso'), TOKEN_DE_USO);
    await u.type(screen.getByLabelText('Nova senha'), 'nova-senha-sintetica-1');
    await u.type(screen.getByLabelText('Confirmar a nova senha'), 'nova-senha-sintetica-1');
    await u.click(screen.getByRole('button', { name: 'Definir senha' }));

    expect(await screen.findByText('Senha definida. Entre com a nova senha.')).toBeTruthy();
    expect(chamadasDe(f, 'POST /auth/password-reset/redeem')[0]?.corpo).toEqual({
      tenant: 'bimeta-platform',
      email: 'ana@bimeta.local',
      token: TOKEN_DE_USO,
      newPassword: 'nova-senha-sintetica-1',
    });
  });

  it('primeiro acesso: senha curta é recusada antes de enviar', async () => {
    window.history.replaceState(null, '', '/plataforma');
    const f = criarFetch({});
    render(<PlataformaApp api={createPlataformaApi({ fetchFn: f })} />);
    const u = userEvent.setup();
    await u.click(screen.getByRole('button', { name: /Primeiro acesso/ }));
    await u.type(screen.getByLabelText('E-mail'), 'ana@bimeta.local');
    await u.type(screen.getByLabelText('Token de primeiro acesso'), 'tk');
    await u.type(screen.getByLabelText('Nova senha'), 'curta');
    await u.type(screen.getByLabelText('Confirmar a nova senha'), 'curta');
    await u.click(screen.getByRole('button', { name: 'Definir senha' }));
    dom(await screen.findByRole('alert')).toHaveTextContent('ao menos 12');
    expect(f).not.toHaveBeenCalled();
  });

  it('401 numa chamada volta ao login', async () => {
    renderizar(['PlatformOverviewRead'], { 'GET /auth/me': problema(401, 'Sessão expirada') });
    expect(await screen.findByText('Sua sessão expirou. Entre novamente.')).toBeTruthy();
  });
});

describe('menus por permissão', () => {
  it('consulta (só leitura) não vê Operadores nem Auditoria', async () => {
    renderizar(['PlatformOverviewRead', 'PlatformTenantRead']);
    const menu = await screen.findByRole('navigation', { name: 'Menu da plataforma' });
    expect(within(menu).getByRole('button', { name: 'Visão geral' })).toBeTruthy();
    expect(within(menu).getByRole('button', { name: 'Empresas' })).toBeTruthy();
    expect(within(menu).getByRole('button', { name: 'Minha conta' })).toBeTruthy();
    expect(within(menu).queryByRole('button', { name: 'Operadores' })).toBeNull();
    expect(within(menu).queryByRole('button', { name: 'Auditoria' })).toBeNull();
  });

  it('comercial não vê suspender; administrador vê', async () => {
    const rota = { 'GET /platform/companies/lab-exemplo': json(empresa()) };
    renderizar(['PlatformOverviewRead', 'PlatformTenantRead', 'PlatformTenantUpdate'], rota);
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Empresas' }));
    await u.click(await screen.findByRole('button', { name: 'Abrir Laboratório Exemplo' }));
    expect(await screen.findByRole('heading', { name: /Laboratório Exemplo/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Suspender empresa' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Editar dados' })).toBeTruthy();
  });
});

describe('visão geral', () => {
  it('mostra os cartões e o bloco separado de empresas sem limite', async () => {
    renderizar(['PlatformOverviewRead']);
    const separado = await screen.findByRole('region', { name: 'Empresas sem limite definido (anteriores ou exceção)' });
    dom(within(separado).getByRole('group', { name: 'Empresas sem limite' })).toHaveTextContent('4');
    dom(within(separado).getByRole('group', { name: 'Usuários ativos (sem limite)' })).toHaveTextContent('37');
    expect(within(separado).getByText(/fora dos totais de vagas/)).toBeTruthy();
    dom(screen.getByRole('group', { name: 'Vagas contratadas' })).toHaveTextContent('100');
    dom(screen.getByRole('group', { name: 'Vagas utilizadas' })).toHaveTextContent('60');
    dom(screen.getByRole('group', { name: 'Empresas com limite excedido' })).toHaveTextContent('1');
    dom(screen.getByRole('group', { name: 'Suspensas' })).toHaveTextContent('2');
  });
});

describe('empresas', () => {
  it('lista com colunas de vagas e filtra por situação', async () => {
    const f = renderizar(['PlatformTenantRead'], {}, '/plataforma/empresas');
    expect(await screen.findByText('lab-exemplo')).toBeTruthy();
    const linha = screen.getByRole('row', { name: /Laboratório Exemplo/ });
    expect(within(linha).getByText('Habilitado')).toBeTruthy();
    const u = userEvent.setup();
    await u.selectOptions(screen.getByLabelText('Situação'), 'Suspended');
    await u.click(screen.getByRole('button', { name: 'Filtrar' }));
    await waitFor(() => expect(chamadasDe(f, 'GET /platform/companies').some((c) => c.consulta.get('status') === 'Suspended')).toBe(true));
  });

  it('cadastra a empresa exigindo limite e mostra o token uma única vez', async () => {
    const f = renderizar(
      TUDO,
      {
        'POST /platform/companies': json(
          { detail: empresa(), firstAccess: { email: 'admin@lab.local', token: TOKEN_DE_USO, expiresAt: DEPOIS, reissued: false }, created: true },
          201,
        ),
        'GET /platform/companies/lab-exemplo': json(empresa()),
      },
      '/plataforma/empresas',
    );
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Cadastrar empresa' }));
    const dlg = await screen.findByRole('dialog', { name: 'Cadastrar empresa' });
    await u.type(within(dlg).getByLabelText(/Identificador técnico/), 'Lab_Ruim');
    await u.type(within(dlg).getByLabelText('Nome da empresa'), 'Laboratório Exemplo');
    await u.type(within(dlg).getByLabelText('Nome do administrador inicial'), 'Admin');
    await u.type(within(dlg).getByLabelText('E-mail do administrador inicial'), 'admin@lab.local');

    // limite obrigatório: o campo é required; sem ele nada é enviado
    await u.click(within(dlg).getByRole('button', { name: 'Cadastrar empresa' }));
    expect(chamadasDe(f, 'POST /platform/companies')).toHaveLength(0);

    await u.type(within(dlg).getByLabelText('Limite de usuários contratados'), '10');
    await u.click(within(dlg).getByRole('button', { name: 'Cadastrar empresa' }));
    dom(await within(dlg).findByRole('alert')).toHaveTextContent('letras minúsculas, números e hífens');
    expect(chamadasDe(f, 'POST /platform/companies')).toHaveLength(0);

    const slug = within(dlg).getByLabelText(/Identificador técnico/);
    await u.clear(slug);
    await u.type(slug, 'lab-exemplo');
    await u.click(within(dlg).getByRole('button', { name: 'Cadastrar empresa' }));

    const corpo = chamadasDe(f, 'POST /platform/companies')[0]?.corpo;
    expect(corpo).toMatchObject({ slug: 'lab-exemplo', maxUsers: 10, registrationEnabled: true, adminEmail: 'admin@lab.local' });

    expect(await screen.findByText(TOKEN_DE_USO)).toBeTruthy();
    expect(screen.getByText(/não será mostrado de novo/)).toBeTruthy();
    expect(sessionStorage.getItem(CHAVE_SESSAO_PLATAFORMA)).not.toContain(TOKEN_DE_USO);
    expect(JSON.stringify({ ...localStorage })).not.toContain(TOKEN_DE_USO);

    await u.click(screen.getByRole('button', { name: 'Fechar' }));
    await screen.findByRole('heading', { name: /Laboratório Exemplo/ });
    expect(screen.queryByText(TOKEN_DE_USO)).toBeNull();
  });

  it('mostra o conflito de identificador já em uso', async () => {
    renderizar(TUDO, { 'POST /platform/companies': problema(409, 'Conflito', 'O identificador já está em uso.', 'slug-in-use') }, '/plataforma/empresas');
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Cadastrar empresa' }));
    const dlg = await screen.findByRole('dialog', { name: 'Cadastrar empresa' });
    await u.type(within(dlg).getByLabelText(/Identificador técnico/), 'lab-exemplo');
    await u.type(within(dlg).getByLabelText('Nome da empresa'), 'X');
    await u.type(within(dlg).getByLabelText('Limite de usuários contratados'), '5');
    await u.type(within(dlg).getByLabelText('Nome do administrador inicial'), 'A');
    await u.type(within(dlg).getByLabelText('E-mail do administrador inicial'), 'a@b.co');
    await u.click(within(dlg).getByRole('button', { name: 'Cadastrar empresa' }));
    dom(await within(dlg).findByRole('alert')).toHaveTextContent('O identificador já está em uso.');
  });

  it('detalhe destaca o excesso de utilização', async () => {
    renderizar(['PlatformTenantRead'], { 'GET /platform/companies/lab-exemplo': json(empresa({ seats: vagas({ limit: 3, active: 5, used: 5, available: 0, excess: 2 }) })) }, '/plataforma/empresas');
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Abrir Laboratório Exemplo' }));
    dom(await screen.findByText(/Excesso de utilização/)).toHaveTextContent('ninguém é desativado; novos ingressos ficam bloqueados');
    expect(screen.getByText('Aguardando primeiro acesso')).toBeTruthy();
  });

  it('suspensão exige motivo e o slug digitado', async () => {
    const f = renderizar(
      TUDO,
      {
        'GET /platform/companies/lab-exemplo': json(empresa()),
        'POST /platform/companies/lab-exemplo/suspend': json(empresa({ company: { ...empresa().company, status: 'Suspended', suspendedAt: '2026-11-25T12:00:00Z' } })),
      },
      '/plataforma/empresas',
    );
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Abrir Laboratório Exemplo' }));
    await u.click(await screen.findByRole('button', { name: 'Suspender empresa' }));
    const dlg = await screen.findByRole('dialog', { name: 'Suspender empresa' });
    expect(within(dlg).getByText(/derruba as sessões/)).toBeTruthy();
    const confirmar = within(dlg).getByRole('button', { name: 'Suspender empresa' });
    dom(confirmar).toBeDisabled();

    await u.type(within(dlg).getByLabelText(/Motivo/), 'Inadimplência confirmada');
    dom(confirmar).toBeDisabled();
    await u.type(within(dlg).getByLabelText(/Digite o identificador/), 'lab-errado');
    dom(confirmar).toBeDisabled();
    await u.clear(within(dlg).getByLabelText(/Digite o identificador/));
    await u.type(within(dlg).getByLabelText(/Digite o identificador/), 'lab-exemplo');
    dom(confirmar).toBeEnabled();
    await u.click(confirmar);

    await waitFor(() => expect(chamadasDe(f, 'POST /platform/companies/lab-exemplo/suspend')[0]?.corpo).toEqual({ reason: 'Inadimplência confirmada', confirm: 'lab-exemplo' }));
    expect(await screen.findByRole('button', { name: 'Reativar empresa' })).toBeTruthy();
  });

  it('reemissão do primeiro acesso só aparece com o administrador pendente', async () => {
    const f = renderizar(
      TUDO,
      {
        'GET /platform/companies/lab-exemplo': json(empresa()),
        'POST /platform/companies/lab-exemplo/first-administrator/reissue': json({ email: 'admin@lab.local', token: TOKEN_DE_USO, expiresAt: DEPOIS, reissued: true }),
      },
      '/plataforma/empresas',
    );
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Abrir Laboratório Exemplo' }));
    await u.click(await screen.findByRole('button', { name: 'Reemitir primeiro acesso' }));
    expect(await screen.findByText(TOKEN_DE_USO)).toBeTruthy();
    expect(chamadasDe(f, 'POST /platform/companies/lab-exemplo/first-administrator/reissue')).toHaveLength(1);
  });

  it('sem reemissão quando o administrador já concluiu o acesso', async () => {
    renderizar(TUDO, { 'GET /platform/companies/lab-exemplo': json(empresa({ initialAdministrator: { email: 'admin@lab.local', displayName: 'Admin', status: 'Active', hasLiveToken: false, createdAt: '2026-11-01T10:00:00Z' } })) }, '/plataforma/empresas');
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Abrir Laboratório Exemplo' }));
    await screen.findByText('Administrador inicial');
    expect(screen.queryByRole('button', { name: 'Reemitir primeiro acesso' })).toBeNull();
  });
});

describe('operadores', () => {
  const detalheDoOperador = (sobrescrever: Partial<OperatorDetail> = {}): OperatorDetail => ({
    operator: operador(),
    permissions: [
      { code: 'PlatformTenantRead', group: 'Empresas', label: 'Consultar empresas', fromRole: true, override: null, effective: true },
      { code: 'PlatformTenantSuspend', group: 'Empresas', label: 'Suspender empresas', fromRole: false, override: 'Grant', effective: true },
      { code: 'PlatformTenantCreate', group: 'Empresas', label: 'Cadastrar empresas', fromRole: true, override: 'Deny', effective: false },
    ],
    ...sobrescrever,
  });

  it('mostra Herdada, Concedida, Retirada e Efetiva e permite restaurar o padrão', async () => {
    const f = renderizar(
      TUDO,
      {
        'GET /platform/operators': json({ items: [operador()], total: 1, page: 1, pageSize: 20 }),
        'GET /platform/operators/op000000-0000-4000-8000-000000000009': json(detalheDoOperador()),
        'GET /platform/roles': json([{ role: 'PlatformCommercial', label: 'Comercial', permissions: [] }]),
        'DELETE /platform/operators/op000000-0000-4000-8000-000000000009/permissions/PlatformTenantCreate': json(detalheDoOperador()),
      },
      '/plataforma/operadores',
    );
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Abrir Bia Comercial' }));
    const lista = await screen.findByRole('region', { name: 'Permissões' });
    const linhaHerdada = within(lista).getByText('Consultar empresas').closest('li')!;
    expect(within(linhaHerdada).getByText('Herdada do perfil')).toBeTruthy();
    expect(within(linhaHerdada).getByText('Efetiva')).toBeTruthy();
    const linhaConcedida = within(lista).getByText('Suspender empresas').closest('li')!;
    expect(within(linhaConcedida).getByText('Concedida')).toBeTruthy();
    const linhaRetirada = within(lista).getByText('Cadastrar empresas').closest('li')!;
    expect(within(linhaRetirada).getByText('Retirada')).toBeTruthy();
    expect(within(linhaRetirada).getByText('Não efetiva')).toBeTruthy();

    await u.click(within(linhaRetirada).getByRole('button', { name: /Restaurar padrão do perfil/ }));
    await waitFor(() => expect(f.mock.calls.some(([e, i]) => String(e).endsWith('/permissions/PlatformTenantCreate') && i?.method === 'DELETE')).toBe(true));
  });

  it('mostra o erro de último administrador', async () => {
    renderizar(
      TUDO,
      {
        'GET /platform/operators': json({ items: [operador({ role: 'PlatformAdministrator' })], total: 1, page: 1, pageSize: 20 }),
        'GET /platform/operators/op000000-0000-4000-8000-000000000009': json(detalheDoOperador({ operator: operador({ role: 'PlatformAdministrator' }) })),
        'GET /platform/roles': json([{ role: 'PlatformViewer', label: 'Consulta', permissions: [] }]),
        'POST /platform/operators/op000000-0000-4000-8000-000000000009/deactivate': problema(409, 'Conflito', 'Este é o único administrador ativo.', 'last-administrator'),
      },
      '/plataforma/operadores',
    );
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Abrir Bia Comercial' }));
    await u.click(await screen.findByRole('button', { name: 'Desativar' }));
    dom(await screen.findByRole('alert')).toHaveTextContent('ao menos um administrador ativo');
  });

  it('quem gerencia operadores mas não permissões não vê os controles de permissão', async () => {
    renderizar(
      ['PlatformOperatorManage'],
      {
        'GET /platform/operators': json({ items: [operador()], total: 1, page: 1, pageSize: 20 }),
        'GET /platform/operators/op000000-0000-4000-8000-000000000009': json(detalheDoOperador()),
      },
      '/plataforma/operadores',
    );
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Abrir Bia Comercial' }));
    await screen.findByRole('region', { name: 'Permissões' });
    expect(screen.queryByRole('button', { name: /Retirar|Conceder/ })).toBeNull();
    expect(screen.queryByLabelText('Perfil do operador')).toBeNull();
  });

  it('cadastra operador e mostra o token uma vez', async () => {
    renderizar(
      TUDO,
      {
        'GET /platform/operators': json({ items: [], total: 0, page: 1, pageSize: 20 }),
        'POST /platform/operators': json({ operator: operador(), token: TOKEN_DE_USO, expiresAt: DEPOIS, kind: 'first-access' }, 201),
      },
      '/plataforma/operadores',
    );
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Cadastrar operador' }));
    const dlg = await screen.findByRole('dialog', { name: 'Cadastrar operador' });
    await u.type(within(dlg).getByLabelText('Nome completo'), 'Bia Comercial');
    await u.type(within(dlg).getByLabelText('E-mail'), 'bia@bimeta.local');
    await u.click(within(dlg).getByRole('button', { name: 'Cadastrar operador' }));
    expect(await screen.findByText(TOKEN_DE_USO)).toBeTruthy();
    await u.click(screen.getByRole('button', { name: 'Fechar' }));
    expect(screen.queryByText(TOKEN_DE_USO)).toBeNull();
  });
});

describe('auditoria', () => {
  it('lista os registros com o nome amigável e o código técnico e aplica os filtros', async () => {
    const f = renderizar(
      ['PlatformAuditRead'],
      {
        'GET /platform/audit': json({
          items: [{ id: 'a1', occurredAt: '2026-11-25T12:00:00Z', actorUserId: 'u1', actorOrigin: 'platform-panel', operation: 'platform.tenant.suspend', resourceType: 'Tenant', resourceId: 'te000000-0000', outcome: 'Success' }],
          total: 1,
          page: 1,
          pageSize: 20,
        }),
      },
      '/plataforma/auditoria',
    );
    expect(await screen.findByText('Empresa suspensa')).toBeTruthy();
    expect(screen.getByText('platform.tenant.suspend')).toBeTruthy();
    expect(screen.getByText('te000000-0000')).toBeTruthy();

    const u = userEvent.setup();
    await u.type(screen.getByLabelText('Operação contém'), 'suspend');
    await u.type(screen.getByLabelText('Recurso contém'), 'te000000');
    await u.selectOptions(screen.getByLabelText('Resultado'), 'Denied');
    await u.type(screen.getByLabelText('De'), '2026-11-01');
    await u.click(screen.getByRole('button', { name: 'Filtrar' }));

    await waitFor(() => {
      const ultima = chamadasDe(f, 'GET /platform/audit').at(-1)!;
      expect(ultima.consulta.get('operation')).toBe('suspend');
      expect(ultima.consulta.get('resource')).toBe('te000000');
      expect(ultima.consulta.get('outcome')).toBe('Denied');
      expect(ultima.consulta.get('from')).toMatch(/^2026-11-0[1-2]T/);
      expect(ultima.consulta.get('page')).toBe('1');
    });
  });

  it('mostra a mensagem de falta de permissão em 403', async () => {
    renderizar(['PlatformAuditRead'], { 'GET /platform/audit': problema(403, 'Proibido') }, '/plataforma/auditoria');
    dom(await screen.findByRole('alert')).toHaveTextContent('Sem permissão');
  });
});

describe('minha conta', () => {
  it('mostra os dados, a nota sobre dois fatores e encerra a sessão', async () => {
    const f = renderizar(['PlatformOverviewRead'], { 'POST /auth/logout': new Response(null, { status: 204 }) }, '/plataforma/conta');
    expect(await screen.findByText('ana@bimeta.local')).toBeTruthy();
    expect(screen.getByText(/autenticação em dois fatores será exigida/)).toBeTruthy();
    const u = userEvent.setup();
    await u.click(screen.getAllByRole('button', { name: 'Encerrar sessão' })[0]!);
    expect(await screen.findByRole('heading', { name: 'Entrar na plataforma' })).toBeTruthy();
    expect(chamadasDe(f, 'POST /auth/logout')).toHaveLength(1);
    expect(sessionStorage.getItem(CHAVE_SESSAO_PLATAFORMA)).toBeNull();
  });

  it('troca a senha própria', async () => {
    const f = renderizar(['PlatformOverviewRead'], { 'PUT /auth/me/password': new Response(null, { status: 204 }) }, '/plataforma/conta');
    const u = userEvent.setup();
    await u.type(await screen.findByLabelText('Senha atual'), 'senha-atual-sintetica');
    await u.type(screen.getByLabelText('Nova senha'), 'senha-nova-sintetica-1');
    await u.type(screen.getByLabelText('Confirmar a nova senha'), 'senha-nova-sintetica-1');
    await u.click(screen.getByRole('button', { name: 'Trocar senha' }));
    expect(await screen.findByText('Senha alterada.')).toBeTruthy();
    expect(chamadasDe(f, 'PUT /auth/me/password')[0]?.corpo).toEqual({ currentPassword: 'senha-atual-sintetica', newPassword: 'senha-nova-sintetica-1' });
  });
});
