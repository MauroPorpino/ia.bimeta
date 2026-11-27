import { useState } from 'react';
import { Dialogo } from '../components/Dialogo';
import { formatarData } from './util';

interface Props {
  titulo: string;
  paraQuem: string;
  token: string;
  expiresAt: string;
  /** Explica o que a pessoa faz com o token. */
  instrucao: string;
  onFechar: () => void;
}

/**
 * O token exibido UMA ÚNICA VEZ. Vive só no estado de quem o abriu: não vai a localStorage, sessionStorage, URL nem console,
 * e some ao fechar o diálogo.
 */
export function TokenUnico({ titulo, paraQuem, token, expiresAt, instrucao, onFechar }: Props) {
  const [copiado, setCopiado] = useState<'sim' | 'falhou' | null>(null);

  async function copiar() {
    try {
      await navigator.clipboard.writeText(token);
      setCopiado('sim');
    } catch {
      setCopiado('falhou');
    }
  }

  return (
    <Dialogo titulo={titulo} onFechar={onFechar}>
      <p>
        Token para <strong>{paraQuem}</strong>. Validade: {formatarData(expiresAt)}.
      </p>
      <p className="token-emitido" aria-label="Token emitido">
        <code>{token}</code>
      </p>
      <p className="aviso" role="note">
        Este token só é exibido agora e não será mostrado de novo. Entregue-o por um canal seguro e não o guarde. {instrucao}
      </p>
      {copiado === 'sim' && (
        <p role="status" className="discreto">
          Token copiado.
        </p>
      )}
      {copiado === 'falhou' && (
        <p role="alert" className="erro-campo">
          Não foi possível copiar automaticamente. Selecione o token e copie manualmente.
        </p>
      )}
      <div className="acoes">
        <button type="button" className="btn btn-primario" onClick={() => void copiar()}>
          Copiar
        </button>
        <button type="button" className="btn" onClick={onFechar}>
          Fechar
        </button>
      </div>
    </Dialogo>
  );
}
