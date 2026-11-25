import { useState } from 'react';
import { validadeDoToken } from '../usuarios';
import { Dialogo } from './Dialogo';

interface Props {
  titulo: string;
  /** Quem vai receber o token (nome ou e-mail), só para a pessoa saber a quem entregar. */
  paraQuem: string;
  token: string;
  expiresAt: string;
  onFechar: () => void;
}

/**
 * O token entregue UMA ÚNICA VEZ. Vive só no estado de quem o abriu: não vai a localStorage, sessionStorage, URL nem console, e some ao fechar. O usuário o usa na tela
 * "Esqueci a senha (tenho um token)" para definir a própria senha.
 */
export function TokenEmitido({ titulo, paraQuem, token, expiresAt, onFechar }: Props) {
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
        Token para <strong>{paraQuem}</strong>. Validade: {validadeDoToken(expiresAt)}.
      </p>
      <p className="token-emitido" aria-label="Token emitido">
        <code>{token}</code>
      </p>
      <p className="aviso" role="note">
        Este token só é exibido agora. Entregue-o ao usuário por um canal seguro e não o guarde. Ele o usa na tela "Esqueci a senha (tenho um token)" para definir a própria senha.
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
