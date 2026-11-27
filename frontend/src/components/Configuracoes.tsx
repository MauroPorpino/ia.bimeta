import { useState } from 'react';
import type { ApiClient } from '../api/client';
import type { CurrentUser } from '../api/types';
import { podeVerIa } from '../ia';
import { podeVerUsuarios } from '../usuarios';
import { IaProvedores } from './IaProvedores';
import { UsuariosConfig } from './UsuariosConfig';

interface Props {
  api: ApiClient;
  me: CurrentUser;
  /** A área está escondida (a pessoa está em outra): continua montada para não perder o que estava aberto. */
  oculto?: boolean;
}

type Secao = 'usuarios' | 'ia';

/** A área "Configurações": "Usuários" (só com a permissão UserRead) e "Inteligência artificial" (só com a permissão de consultar a configuração de IA). Outras entram aqui como novas abas. */
export function Configuracoes({ api, me, oculto = false }: Props) {
  const temUsuarios = podeVerUsuarios(me);
  const temIa = podeVerIa(me);
  const [escolhida, setEscolhida] = useState<Secao>(temUsuarios ? 'usuarios' : 'ia');
  const secao: Secao = escolhida === 'usuarios' && !temUsuarios ? 'ia' : escolhida === 'ia' && !temIa ? 'usuarios' : escolhida;

  return (
    <main className="integracoes" hidden={oculto}>
      <nav className="abas-integracoes" aria-label="Seções de configurações">
        {temUsuarios && (
          <button type="button" className="area-aba" aria-current={secao === 'usuarios' ? 'page' : undefined} onClick={() => setEscolhida('usuarios')}>
            Usuários
          </button>
        )}
        {temIa && (
          <button type="button" className="area-aba" aria-current={secao === 'ia' ? 'page' : undefined} onClick={() => setEscolhida('ia')}>
            Inteligência artificial
          </button>
        )}
      </nav>
      <div className="integracoes-aba">
        {temUsuarios && secao === 'usuarios' && <UsuariosConfig api={api} me={me} />}
        {temIa && secao === 'ia' && <IaProvedores api={api} me={me} />}
      </div>
    </main>
  );
}
