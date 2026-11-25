import type { ApiClient } from '../api/client';
import type { CurrentUser } from '../api/types';
import { podeVerUsuarios } from '../usuarios';
import { UsuariosConfig } from './UsuariosConfig';

interface Props {
  api: ApiClient;
  me: CurrentUser;
  /** A área está escondida (a pessoa está em outra): continua montada para não perder o que estava aberto. */
  oculto?: boolean;
}

/** A área "Configurações". Hoje tem a subseção "Usuários" (só com a permissão UserRead); outras entram aqui como novas abas. */
export function Configuracoes({ api, me, oculto = false }: Props) {
  return (
    <main className="integracoes" hidden={oculto}>
      <nav className="abas-integracoes" aria-label="Seções de configurações">
        {podeVerUsuarios(me) && (
          <button type="button" className="area-aba" aria-current="page">
            Usuários
          </button>
        )}
      </nav>
      <div className="integracoes-aba">{podeVerUsuarios(me) && <UsuariosConfig api={api} me={me} />}</div>
    </main>
  );
}
