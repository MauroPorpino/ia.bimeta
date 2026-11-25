import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../api/client';
import type { CurrentUser, PermissionCatalog, UserDetail, UserListItem, UserUsage } from '../api/types';
import { ADMINISTRADOR, chamadasDe, criarFetch, json, problema } from '../test/helpers';
import { UsuariosConfig } from './UsuariosConfig';

const ORG = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const OUTRA_ORG = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const TOKEN = 'tok-sintetico-ABC123-xyz';

const CATALOGO: PermissionCatalog = {
  permissions: [
    { permission: 'UserRead', group: 'Usuários', label: 'Ver usuários', tenantOnly: false },
    { permission: 'QueueRead', group: 'Filas', label: 'Ver filas', tenantOnly: false },
    { permission: 'UserManage', group: 'Usuários', label: 'Gerir usuários', tenantOnly: false },
    { permission: 'ChannelRead', group: 'Canais', label: 'Ver canais', tenantOnly: false },
  ],
  roles: [
    { role: 'Attendant', profile: 'Atendente', organizationPermissions: ['QueueRead'], tenantPermissions: [] },
    { role: 'Manager', profile: 'Supervisor', organizationPermissions: ['QueueRead', 'UserRead'], tenantPermissions: [] },
    { role: 'OrganizationAdmin', profile: 'Administrador', organizationPermissions: ['QueueRead', 'UserRead'], tenantPermissions: [] },
    { role: 'User', profile: 'Usuário', organizationPermissions: [], tenantPermissions: [] },
  ],
};

function usoPadrao(sobrescrever: Partial<UserUsage> = {}): UserUsage {
  return { limit: 10, registrationEnabled: true, active: 3, inactive: 1, pending: 1, expired: 0, cancelled: 0, seatsUsed: 4, available: 6, overLimit: false, excess: 0, ...sobrescrever };
}

function usuario(sobrescrever: Partial<UserListItem> = {}): UserListItem {
  return {
    id: 'u0000000-0000-4000-8000-000000000001',
    email: 'paulo@demo-a.local',
    displayName: 'Paulo Prado',
    status: 'Active',
    authMode: 'Password',
    grants: [{ organizationId: ORG, role: 'Attendant' }],
    createdAt: '2026-11-01T10:00:00Z',
    customized: false,
    ...sobrescrever,
  };
}

function detalhe(sobrescrever: Partial<UserDetail> = {}): UserDetail {
  return {
    ...usuario(),
    hasLiveFirstAccessToken: false,
    permissions: [
      { permission: 'QueueRead', organizationId: ORG, inProfile: true, override: null, effective: true },
      { permission: 'UserRead', organizationId: ORG, inProfile: false, override: 'Grant', effective: true },
    ],
    ...sobrescrever,
  };
}

function eu(permissoes: string[], organizationId: string | null = null): CurrentUser {
  return { ...ADMINISTRADOR, permissions: permissoes.map((permission) => ({ permission, organizationId })) };
}

const TUDO = ['UserRead', 'UserManage', 'UserStatusManage', 'UserPermissionManage'];

function rotas(extras: Parameters<typeof criarFetch>[0] = {}) {
  return {
    'GET /users/usage': json(usoPadrao()),
    'GET /users/permission-catalog': json(CATALOGO),
    'GET /organizations': json([
      { id: ORG, name: 'Matriz' },
      { id: OUTRA_ORG, name: 'Filial' },
    ]),
    'GET /users': json({ items: [usuario()], total: 1, page: 1, pageSize: 20 }),
    ...extras,
  };
}

function renderizar(me: CurrentUser, extras: Parameters<typeof criarFetch>[0] = {}) {
  const f = criarFetch(rotas(extras));
  vi.stubGlobal('fetch', f);
  render(<UsuariosConfig api={createApi()} me={me} />);
  return f;
}

afterEach(() => {
  vi.restoreAllMocks();
  window.localStorage.clear();
  window.sessionStorage.clear();
});

describe('Configurações → Usuários: painel de uso', () => {
  it('mostra limite, ativos, inativos, pendentes, expirados, vagas e a situação do cadastro', async () => {
    renderizar(eu(TUDO));

    const painel = await screen.findByRole('region', { name: 'Uso de usuários' });
    const valor = (rotulo: string) => within(painel).getByText(rotulo).nextElementSibling?.textContent;
    expect(valor('Limite contratado')).toBe('10');
    expect(valor('Ativos')).toBe('3');
    expect(valor('Inativos')).toBe('1');
    expect(valor('Pendentes (reservam vaga)')).toBe('1');
    expect(valor('Expirados')).toBe('0');
    expect(valor('Vagas usadas')).toBe('4');
    expect(valor('Vagas disponíveis')).toBe('6');
    expect(valor('Cadastro de novos usuários')).toBe('Habilitado');
    expect(within(painel).queryByRole('status')).toBeNull();
  });

  it('sem limite mostra "Ilimitado"', async () => {
    renderizar(eu(TUDO), { 'GET /users/usage': json(usoPadrao({ limit: null, available: null })) });

    const painel = await screen.findByRole('region', { name: 'Uso de usuários' });
    expect(within(painel).getByText('Limite contratado').nextElementSibling?.textContent).toBe('Ilimitado');
  });

  it.each([
    ['próximo do limite (80% ou mais)', usoPadrao({ seatsUsed: 8, available: 2 }), /Próximo do limite: 8 de 10/],
    ['sem vagas', usoPadrao({ seatsUsed: 10, available: 0 }), /Sem vagas disponíveis/],
    ['acima do limite, com orientação à BImeta', usoPadrao({ seatsUsed: 12, available: 0, overLimit: true, excess: 2 }), /Acima do limite: 2 usuários a mais.*ampliação do limite à BImeta/],
    ['cadastro desabilitado pela BImeta', usoPadrao({ registrationEnabled: false }), /cadastro de novos usuários está desabilitado pela BImeta/],
  ])('avisa: %s', async (_nome, uso, texto) => {
    renderizar(eu(TUDO), { 'GET /users/usage': json(uso) });

    const painel = await screen.findByRole('region', { name: 'Uso de usuários' });
    expect(within(painel).getByRole('status').textContent).toMatch(texto);
  });

  it('o tenant não tem controle para alterar limite nem habilitação', async () => {
    renderizar(eu(TUDO));
    const painel = await screen.findByRole('region', { name: 'Uso de usuários' });

    expect(within(painel).queryAllByRole('button')).toHaveLength(0);
    expect(within(painel).queryAllByRole('textbox')).toHaveLength(0);
    expect(within(painel).queryAllByRole('spinbutton')).toHaveLength(0);
  });
});

describe('Configurações → Usuários: listagem', () => {
  it('lista nome, e-mail, perfil por organização, situação e personalizado', async () => {
    renderizar(eu(TUDO), { 'GET /users': json({ items: [usuario({ customized: true }), usuario({ id: 'u2', displayName: 'Rita', email: 'rita@x.local', status: 'Pending', grants: [] })], total: 2, page: 1, pageSize: 20 }) });

    const tabela = await screen.findByRole('table');
    const linhaPaulo = within(tabela).getByText('Paulo Prado').closest('tr')!;
    expect(within(linhaPaulo).getByText('paulo@demo-a.local')).toBeTruthy();
    expect(within(linhaPaulo).getByText('Atendente (Matriz)')).toBeTruthy();
    expect(within(linhaPaulo).getByText('Ativo')).toBeTruthy();
    expect(within(linhaPaulo).getByText('Sim')).toBeTruthy();
    const linhaRita = within(tabela).getByText('Rita').closest('tr')!;
    expect(within(linhaRita).getByText('Pendente')).toBeTruthy();
    expect(within(linhaRita).getByText('Não')).toBeTruthy();
  });

  it('busca e filtros vão à API e a paginação pede a página seguinte (20 por página)', async () => {
    const lista = vi.fn((req: { consulta: URLSearchParams }) =>
      json({ items: [usuario()], total: 45, page: Number(req.consulta.get('page') ?? 1), pageSize: 20 }),
    );
    const f = renderizar(eu(TUDO), { 'GET /users': lista });
    const u = userEvent.setup();

    await screen.findByText('45 usuários · página 1 de 3');
    expect(chamadasDe(f, 'GET /users')[0]!.consulta.get('pageSize')).toBe('20');

    await u.click(screen.getByRole('button', { name: 'Próxima' }));
    await screen.findByText('45 usuários · página 2 de 3');
    expect(chamadasDe(f, 'GET /users').at(-1)!.consulta.get('page')).toBe('2');

    await u.type(screen.getByLabelText('Buscar por nome ou e-mail'), 'pau');
    await u.selectOptions(screen.getByLabelText('Perfil'), 'Manager');
    await u.selectOptions(screen.getByLabelText('Organização'), ORG);
    await u.selectOptions(screen.getByLabelText('Situação'), 'pending');
    await u.click(screen.getByRole('button', { name: 'Filtrar' }));

    await waitFor(() => expect(chamadasDe(f, 'GET /users').at(-1)!.consulta.get('search')).toBe('pau'));
    const ultima = chamadasDe(f, 'GET /users').at(-1)!.consulta;
    expect(ultima.get('role')).toBe('Manager');
    expect(ultima.get('organizationId')).toBe(ORG);
    expect(ultima.get('status')).toBe('pending');
    expect(ultima.get('page')).toBe('1');
  });

  it('mostra a mensagem do 403 da listagem', async () => {
    renderizar(eu(TUDO), { 'GET /users': problema(403, 'Forbidden') });

    expect((await screen.findAllByRole('alert')).some((a) => a.textContent === 'Você não tem permissão para esta operação.')).toBe(true);
  });
});

describe('Configurações → Usuários: cadastro e token único', () => {
  async function preencher(u: ReturnType<typeof userEvent.setup>) {
    await u.click(await screen.findByRole('button', { name: 'Cadastrar usuário' }));
    const dialogo = await screen.findByRole('dialog', { name: 'Cadastrar usuário' });
    await u.type(within(dialogo).getByLabelText('Nome completo'), 'Nina Nova');
    await u.type(within(dialogo).getByLabelText('E-mail'), 'nina@demo-a.local');
    await u.selectOptions(within(dialogo).getByLabelText('Organização'), ORG);
    await u.selectOptions(within(dialogo).getByLabelText('Perfil'), 'Attendant');
    return dialogo;
  }

  it('cria o usuário pendente, mostra o token uma única vez, copia e o esquece ao fechar (sem storage nem console)', async () => {
    const logs = [vi.spyOn(console, 'log'), vi.spyOn(console, 'info'), vi.spyOn(console, 'warn'), vi.spyOn(console, 'error'), vi.spyOn(console, 'debug')];
    const f = renderizar(eu(TUDO), {
      [`POST /organizations/${ORG}/users/admissions`]: json({ user: usuario({ id: 'u9', displayName: 'Nina Nova', status: 'Pending' }), token: TOKEN, expiresAt: '2026-12-01T10:00:00Z' }, 201),
      'GET /users/u9': json(detalhe({ id: 'u9', displayName: 'Nina Nova', email: 'nina@demo-a.local', status: 'Pending', hasLiveFirstAccessToken: true })),
    });
    const u = userEvent.setup();
    const dialogo = await preencher(u);
    // A prévia das permissões padrão do perfil escolhido aparece por grupo funcional.
    expect(within(dialogo).getByText('Filas')).toBeTruthy();
    expect(within(dialogo).getByText('Ver filas')).toBeTruthy();

    await u.click(within(dialogo).getByRole('button', { name: 'Cadastrar' }));

    const aviso = await screen.findByRole('dialog', { name: 'Token de primeiro acesso' });
    expect(chamadasDe(f, `POST /organizations/${ORG}/users/admissions`)[0]!.corpo).toEqual({ email: 'nina@demo-a.local', displayName: 'Nina Nova', role: 'Attendant' });
    expect(within(aviso).getByText(TOKEN)).toBeTruthy();
    expect(within(aviso).getByText(/Validade:/)).toBeTruthy();
    expect(within(aviso).getByText(/só é exibido agora/)).toBeTruthy();

    await u.click(within(aviso).getByRole('button', { name: 'Copiar' }));
    expect(await within(aviso).findByText('Token copiado.')).toBeTruthy();
    expect(await navigator.clipboard.readText()).toBe(TOKEN);

    await u.click(within(aviso).getByRole('button', { name: 'Fechar' }));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByText(TOKEN)).toBeNull();

    const guardado = JSON.stringify([Object.entries(window.localStorage), Object.entries(window.sessionStorage)]);
    expect(guardado).not.toContain(TOKEN);
    expect(window.location.href).not.toContain(TOKEN);
    for (const espiao of logs) {
      expect(JSON.stringify(espiao.mock.calls)).not.toContain(TOKEN);
    }
    // O token também não volta numa nova leitura do detalhe.
    expect(document.body.textContent).not.toContain(TOKEN);
  });

  it.each([
    ['registration-disabled', problema(409, 'Conflict', 'x', 'registration-disabled'), /cadastro de novos usuários está desabilitado para esta empresa; solicite a habilitação à BImeta/],
    [
      'user-limit-reached',
      json({ title: 'Conflict', status: 409, code: 'user-limit-reached', limit: 10, used: 10 }, 409),
      /Limite de usuários atingido \(10 de 10 vagas em uso\)/,
    ],
    ['last-administrator', problema(409, 'Conflict', 'x', 'last-administrator'), /deixaria a empresa sem administrador ativo/],
    ['403', problema(403, 'Forbidden'), /não tem permissão/],
  ])('erro %s no cadastro vira mensagem específica, sem token', async (_nome, resposta, texto) => {
    renderizar(eu(TUDO), { [`POST /organizations/${ORG}/users/admissions`]: resposta });
    const u = userEvent.setup();
    const dialogo = await preencher(u);

    await u.click(within(dialogo).getByRole('button', { name: 'Cadastrar' }));

    expect((await within(dialogo).findByRole('alert')).textContent).toMatch(texto);
    expect(screen.queryByRole('dialog', { name: 'Token de primeiro acesso' })).toBeNull();
  });

  it('sem UserManage a pessoa não vê "Cadastrar usuário"', async () => {
    renderizar(eu(['UserRead']));
    await screen.findByRole('table');

    expect(screen.queryByRole('button', { name: 'Cadastrar usuário' })).toBeNull();
  });

  it('perfis acima de Atendente ficam indisponíveis sem UserPermissionManage', async () => {
    renderizar(eu(['UserRead', 'UserManage']));
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'Cadastrar usuário' }));
    const dialogo = await screen.findByRole('dialog');

    expect((within(dialogo).getByRole('option', { name: 'Administrador' }) as HTMLOptionElement).disabled).toBe(true);
    expect((within(dialogo).getByRole('option', { name: 'Supervisor' }) as HTMLOptionElement).disabled).toBe(true);
    expect((within(dialogo).getByRole('option', { name: 'Atendente' }) as HTMLOptionElement).disabled).toBe(false);
  });
});

describe('Configurações → Usuários: detalhe e ações', () => {
  async function abrir(u: ReturnType<typeof userEvent.setup>) {
    await u.click(await screen.findByRole('button', { name: 'Abrir Paulo Prado' }));
    return screen.findByRole('region', { name: 'Dados do usuário' });
  }

  it('desativar pede confirmação (histórico, vaga, sessões); cancelar não chama a API e confirmar desativa', async () => {
    const f = renderizar(eu(TUDO), {
      'GET /users/u0000000-0000-4000-8000-000000000001': json(detalhe()),
      'POST /users/u0000000-0000-4000-8000-000000000001/deactivate': new Response(null, { status: 204 }),
    });
    const u = userEvent.setup();
    await abrir(u);

    await u.click(await screen.findByRole('button', { name: 'Desativar' }));
    const dialogo = await screen.findByRole('dialog', { name: 'Desativar usuário' });
    expect(dialogo.textContent).toMatch(/preserva o histórico, libera a vaga e encerra as sessões/);
    await u.click(within(dialogo).getByRole('button', { name: 'Cancelar' }));
    expect(chamadasDe(f, 'POST /users/u0000000-0000-4000-8000-000000000001/deactivate')).toHaveLength(0);

    await u.click(screen.getByRole('button', { name: 'Desativar' }));
    await u.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Desativar' }));

    await screen.findByText('Usuário desativado.');
    expect(chamadasDe(f, 'POST /users/u0000000-0000-4000-8000-000000000001/deactivate')).toHaveLength(1);
  });

  it('último administrador: 409 mostra a mensagem específica', async () => {
    renderizar(eu(TUDO), {
      'GET /users/u0000000-0000-4000-8000-000000000001': json(detalhe()),
      'POST /users/u0000000-0000-4000-8000-000000000001/deactivate': problema(409, 'Conflict', undefined, 'last-administrator'),
    });
    const u = userEvent.setup();
    await abrir(u);
    await u.click(await screen.findByRole('button', { name: 'Desativar' }));
    await u.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Desativar' }));

    expect((await screen.findAllByRole('alert')).some((a) => /sem administrador ativo/.test(a.textContent ?? ''))).toBe(true);
  });

  it('sem UserStatusManage e sem UserManage não há desativar nem redefinir', async () => {
    renderizar(eu(['UserRead']), { 'GET /users/u0000000-0000-4000-8000-000000000001': json(detalhe()) });
    const u = userEvent.setup();
    await abrir(u);
    await screen.findByRole('region', { name: 'Situação e acesso' });

    expect(screen.queryByRole('button', { name: 'Desativar' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Redefinir acesso' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Salvar dados' })).toBeNull();
  });

  it('usuário pendente: emite/substitui o token (mostrado uma vez) e cancela o token', async () => {
    const id = 'u0000000-0000-4000-8000-000000000001';
    const f = renderizar(eu(TUDO), {
      [`GET /users/${id}`]: json(detalhe({ status: 'Pending', hasLiveFirstAccessToken: true })),
      [`POST /users/${id}/first-access`]: json({ token: TOKEN, expiresAt: '2026-12-01T10:00:00Z' }),
      [`DELETE /users/${id}/tokens`]: new Response(null, { status: 204 }),
    });
    const u = userEvent.setup();
    await abrir(u);

    await u.click(await screen.findByRole('button', { name: 'Substituir token de primeiro acesso' }));
    const aviso = await screen.findByRole('dialog', { name: 'Token de primeiro acesso' });
    expect(within(aviso).getByText(TOKEN)).toBeTruthy();
    await u.click(within(aviso).getByRole('button', { name: 'Fechar' }));
    expect(screen.queryByText(TOKEN)).toBeNull();

    await u.click(screen.getByRole('button', { name: 'Cancelar token' }));
    await screen.findByText('Token cancelado.');
    expect(chamadasDe(f, `DELETE /users/${id}/tokens`)).toHaveLength(1);
  });

  it('admissão expirada oferece novo token de primeiro acesso e cancelar admissão, e mostra a recusa por limite ou cadastro desabilitado', async () => {
    const id = 'u0000000-0000-4000-8000-000000000001';
    const f = renderizar(eu(TUDO), {
      [`GET /users/${id}`]: json(detalhe({ status: 'Expired', hasLiveFirstAccessToken: false })),
      [`POST /users/${id}/first-access`]: json({ title: 'Conflict', status: 409, code: 'user-limit-reached', limit: 5, used: 5 }, 409),
      [`POST /users/${id}/admission/cancel`]: new Response(null, { status: 204 }),
    });
    const u = userEvent.setup();
    await abrir(u);

    expect(await screen.findByRole('button', { name: 'Emitir token de primeiro acesso' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Cancelar token' })).toBeNull();
    await u.click(screen.getByRole('button', { name: 'Emitir token de primeiro acesso' }));
    await screen.findByText(/Limite de usuários atingido/);
    expect(chamadasDe(f, `POST /users/${id}/first-access`)).toHaveLength(1);

    await u.click(screen.getByRole('button', { name: 'Cancelar admissão' }));
    const dialogo = await screen.findByRole('dialog', { name: 'Cancelar admissão' });
    await u.click(within(dialogo).getByRole('button', { name: 'Cancelar admissão' }));
    await waitFor(() => expect(chamadasDe(f, `POST /users/${id}/admission/cancel`)).toHaveLength(1));
  });

  it('usuário ativo: redefinir acesso mostra o token de redefinição uma vez e revogar sessões informa a contagem', async () => {
    const id = 'u0000000-0000-4000-8000-000000000001';
    const f = renderizar(eu(TUDO), {
      [`GET /users/${id}`]: json(detalhe()),
      [`POST /organizations/${ORG}/users/${id}/password-reset`]: json({ token: TOKEN, expiresAt: '2026-11-25T10:30:00Z' }),
      [`POST /organizations/${ORG}/users/${id}/sessions/revoke`]: json({ revoked: 2 }),
    });
    const u = userEvent.setup();
    await abrir(u);

    await u.click(await screen.findByRole('button', { name: 'Redefinir acesso' }));
    const aviso = await screen.findByRole('dialog', { name: 'Token de redefinição de acesso' });
    expect(within(aviso).getByText(TOKEN)).toBeTruthy();
    await u.click(within(aviso).getByRole('button', { name: 'Fechar' }));
    expect(screen.queryByText(TOKEN)).toBeNull();

    await u.click(screen.getByRole('button', { name: 'Revogar sessões' }));
    await screen.findByText('2 sessões revogadas.');
    expect(chamadasDe(f, `POST /organizations/${ORG}/users/${id}/sessions/revoke`)).toHaveLength(1);
  });

  it('usuário cancelado pode ser readmitido (novo token mostrado uma vez)', async () => {
    const id = 'u0000000-0000-4000-8000-000000000001';
    renderizar(eu(TUDO), {
      [`GET /users/${id}`]: json(detalhe({ status: 'Cancelled', permissions: [] })),
      [`POST /users/${id}/admission/readmit`]: json({ user: usuario({ status: 'Pending' }), token: TOKEN, expiresAt: '2026-12-01T10:00:00Z' }),
    });
    const u = userEvent.setup();
    await abrir(u);

    await u.click(await screen.findByRole('button', { name: 'Readmitir' }));

    expect(within(await screen.findByRole('dialog')).getByText(TOKEN)).toBeTruthy();
  });

  it('altera o perfil na organização e retira o vínculo (com confirmação)', async () => {
    const id = 'u0000000-0000-4000-8000-000000000001';
    const f = renderizar(eu(TUDO), {
      [`GET /users/${id}`]: json(detalhe()),
      [`PUT /organizations/${ORG}/users/${id}/role`]: new Response(null, { status: 204 }),
      [`DELETE /organizations/${ORG}/users/${id}/role`]: new Response(null, { status: 204 }),
    });
    const u = userEvent.setup();
    await abrir(u);

    await u.selectOptions(await screen.findByLabelText('Perfil em Matriz'), 'Manager');
    await u.click(screen.getByRole('button', { name: 'Alterar perfil em Matriz' }));
    await screen.findByText('Perfil alterado.');
    expect(chamadasDe(f, `PUT /organizations/${ORG}/users/${id}/role`)[0]!.corpo).toEqual({ role: 'Manager' });

    await u.click(screen.getByRole('button', { name: 'Retirar vínculo com Matriz' }));
    await u.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Retirar vínculo' }));
    await screen.findByText('Vínculo retirado.');
    expect(chamadasDe(f, `DELETE /organizations/${ORG}/users/${id}/role`)).toHaveLength(1);
  });
});

describe('Configurações → Usuários: permissões do usuário', () => {
  const id = 'u0000000-0000-4000-8000-000000000001';

  async function abrir(u: ReturnType<typeof userEvent.setup>) {
    await u.click(await screen.findByRole('button', { name: 'Abrir Paulo Prado' }));
    return screen.findByRole('region', { name: 'Permissões em Matriz' });
  }

  it('mostra herdada/personalizada e efetiva por grupo; concede, retira, remove e restaura só com UserPermissionManage', async () => {
    const f = renderizar(eu(TUDO), {
      [`GET /users/${id}`]: json(
        detalhe({
          customized: true,
          permissions: [
            { permission: 'QueueRead', organizationId: ORG, inProfile: true, override: null, effective: true },
            { permission: 'UserRead', organizationId: ORG, inProfile: false, override: 'Grant', effective: true },
            { permission: 'Fantasma', organizationId: ORG, inProfile: true, override: null, effective: true },
          ],
        }),
      ),
      [`PUT /users/${id}/permissions`]: new Response(null, { status: 204 }),
      [`DELETE /users/${id}/permissions`]: new Response(null, { status: 204 }),
      [`POST /users/${id}/permissions/reset`]: new Response(null, { status: 204 }),
    });
    const u = userEvent.setup();
    const secao = await abrir(u);

    expect(within(secao).getByText('Filas')).toBeTruthy();
    expect(within(secao).getByText('Usuários')).toBeTruthy();
    expect(within(secao).getByText('Herdada do perfil')).toBeTruthy();
    expect(within(secao).getByText('Personalizada: concedida')).toBeTruthy();
    // Só as permissões do catálogo aparecem.
    expect(within(secao).queryByText('Fantasma')).toBeNull();

    await u.click(within(secao).getByRole('button', { name: 'Retirar Ver filas (Matriz)' }));
    await screen.findByText('Permissão atualizada.');
    expect(chamadasDe(f, `PUT /users/${id}/permissions`)[0]!.corpo).toEqual({ organizationId: ORG, permission: 'QueueRead', effect: 'Deny' });

    await u.click(within(secao).getByRole('button', { name: 'Remover personalização de Ver usuários (Matriz)' }));
    await waitFor(() => expect(chamadasDe(f, `DELETE /users/${id}/permissions`)).toHaveLength(1));
    const remocao = chamadasDe(f, `DELETE /users/${id}/permissions`)[0]!.consulta;
    expect(remocao.get('permission')).toBe('UserRead');
    expect(remocao.get('organizationId')).toBe(ORG);

    await u.click(within(secao).getByRole('button', { name: 'Restaurar padrão do perfil (Matriz)' }));
    await waitFor(() => expect(chamadasDe(f, `POST /users/${id}/permissions/reset`)).toHaveLength(1));
    expect(chamadasDe(f, `POST /users/${id}/permissions/reset`)[0]!.corpo).toEqual({ allScopes: false, organizationId: ORG });
  });

  it('permissão não efetiva oferece "Conceder" (Grant)', async () => {
    const f = renderizar(eu([...TUDO, 'QueueRead']), {
      [`GET /users/${id}`]: json(detalhe({ permissions: [{ permission: 'QueueRead', organizationId: ORG, inProfile: true, override: 'Deny', effective: false }] })),
      [`PUT /users/${id}/permissions`]: new Response(null, { status: 204 }),
    });
    const u = userEvent.setup();
    const secao = await abrir(u);

    expect(within(secao).getByText('Personalizada: retirada')).toBeTruthy();
    expect(within(secao).getByText('Não efetiva')).toBeTruthy();
    await u.click(within(secao).getByRole('button', { name: 'Conceder Ver filas (Matriz)' }));
    await waitFor(() => expect(chamadasDe(f, `PUT /users/${id}/permissions`)).toHaveLength(1));
    expect(chamadasDe(f, `PUT /users/${id}/permissions`)[0]!.corpo).toEqual({ organizationId: ORG, permission: 'QueueRead', effect: 'Grant' });
  });

  it('lista as permissões que o perfil NÃO dá e permite a concessão inédita, distinguindo as cinco situações', async () => {
    const f = renderizar(eu(TUDO), {
      [`GET /users/${id}`]: json(
        detalhe({
          permissions: [
            { permission: 'QueueRead', organizationId: ORG, inProfile: true, override: null, effective: true },
            { permission: 'UserManage', organizationId: ORG, inProfile: false, override: null, effective: false },
            { permission: 'UserRead', organizationId: ORG, inProfile: false, override: 'Grant', effective: true },
            { permission: 'ChannelRead', organizationId: ORG, inProfile: true, override: 'Deny', effective: false },
          ],
        }),
      ),
      [`PUT /users/${id}/permissions`]: new Response(null, { status: 204 }),
    });
    const u = userEvent.setup();
    const secao = await abrir(u);

    expect(within(secao).getByText('Herdada do perfil')).toBeTruthy();
    expect(within(secao).getByText('Não concedida pelo perfil')).toBeTruthy();
    expect(within(secao).getByText('Personalizada: concedida')).toBeTruthy();
    expect(within(secao).getByText('Personalizada: retirada')).toBeTruthy();
    expect(within(secao).getAllByText('Efetiva')).toHaveLength(2);
    expect(within(secao).getAllByText('Não efetiva')).toHaveLength(2);

    await u.click(within(secao).getByRole('button', { name: 'Conceder Gerir usuários (Matriz)' }));
    await waitFor(() => expect(chamadasDe(f, `PUT /users/${id}/permissions`)).toHaveLength(1));
    expect(chamadasDe(f, `PUT /users/${id}/permissions`)[0]!.corpo).toEqual({ organizationId: ORG, permission: 'UserManage', effect: 'Grant' });
  });

  it('quem não tem a permissão não vê "Conceder" para ela (superior à sua autoridade)', async () => {
    renderizar(eu(['UserRead', 'UserPermissionManage']), {
      [`GET /users/${id}`]: json(
        detalhe({ permissions: [{ permission: 'UserManage', organizationId: ORG, inProfile: false, override: null, effective: false }] }),
      ),
    });
    const u = userEvent.setup();
    const secao = await abrir(u);

    expect(within(secao).getByText('Não concedida pelo perfil')).toBeTruthy();
    expect(within(secao).queryByRole('button', { name: 'Conceder Gerir usuários (Matriz)' })).toBeNull();
    expect(within(secao).getByText('Sem autoridade para conceder')).toBeTruthy();
  });

  it('sem UserPermissionManage vê as permissões mas nenhum controle de personalização', async () => {
    renderizar(eu(['UserRead', 'UserManage', 'UserStatusManage']), { [`GET /users/${id}`]: json(detalhe({ customized: true })) });
    const u = userEvent.setup();
    const secao = await abrir(u);

    expect(within(secao).getByText('Personalizada: concedida')).toBeTruthy();
    expect(within(secao).queryAllByRole('button')).toHaveLength(0);
    expect(screen.queryByRole('button', { name: /Restaurar padrão/ })).toBeNull();
  });

  it('403 numa personalização mostra a mensagem de permissão', async () => {
    renderizar(eu(TUDO), {
      [`GET /users/${id}`]: json(detalhe()),
      [`PUT /users/${id}/permissions`]: problema(403, 'Forbidden'),
    });
    const u = userEvent.setup();
    const secao = await abrir(u);

    await u.click(within(secao).getByRole('button', { name: 'Retirar Ver filas (Matriz)' }));

    expect((await screen.findAllByRole('alert')).some((a) => /não tem permissão/.test(a.textContent ?? ''))).toBe(true);
  });
});
