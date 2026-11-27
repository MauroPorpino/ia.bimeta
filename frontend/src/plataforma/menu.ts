// Itens de menu do painel da plataforma. Para um novo módulo, acrescente um item aqui e a tela em PlataformaApp.

export type ChaveDeMenu = 'visao-geral' | 'empresas' | 'operadores' | 'auditoria' | 'conta';

export interface ItemDeMenu {
  chave: ChaveDeMenu;
  rotulo: string;
  /** Permissão exigida para ver o item; ausente = qualquer operador autenticado. */
  permissao?: string;
  caminho: string;
}

export const ITENS_DE_MENU: ItemDeMenu[] = [
  { chave: 'visao-geral', rotulo: 'Visão geral', permissao: 'PlatformOverviewRead', caminho: '/plataforma' },
  { chave: 'empresas', rotulo: 'Empresas', permissao: 'PlatformTenantRead', caminho: '/plataforma/empresas' },
  { chave: 'operadores', rotulo: 'Operadores', permissao: 'PlatformOperatorManage', caminho: '/plataforma/operadores' },
  { chave: 'auditoria', rotulo: 'Auditoria', permissao: 'PlatformAuditRead', caminho: '/plataforma/auditoria' },
  { chave: 'conta', rotulo: 'Minha conta', caminho: '/plataforma/conta' },
];

export function chaveDoCaminho(caminho: string): ChaveDeMenu {
  const item = [...ITENS_DE_MENU].reverse().find((i) => i.caminho !== '/plataforma' && (caminho === i.caminho || caminho.startsWith(`${i.caminho}/`)));
  return item?.chave ?? 'visao-geral';
}
