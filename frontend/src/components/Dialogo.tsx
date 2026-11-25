import { useEffect, useId, useRef, type ReactNode } from 'react';

interface Props {
  titulo: string;
  onFechar: () => void;
  children: ReactNode;
}

/** Diálogo modal simples e reutilizável: título, conteúdo, Esc fecha, o foco entra nele ao abrir. Não fecha ao clicar fora (evita perder, sem querer, o que está nele). */
export function Dialogo({ titulo, onFechar, children }: Props) {
  const idDoTitulo = useId();
  const caixa = useRef<HTMLDivElement>(null);

  useEffect(() => {
    caixa.current?.focus();
    function aoTeclar(evento: KeyboardEvent) {
      if (evento.key === 'Escape') {
        onFechar();
      }
    }
    document.addEventListener('keydown', aoTeclar);
    return () => document.removeEventListener('keydown', aoTeclar);
  }, [onFechar]);

  return (
    <div className="dialogo-fundo">
      <div ref={caixa} className="dialogo" role="dialog" aria-modal="true" aria-labelledby={idDoTitulo} tabIndex={-1}>
        <h2 id={idDoTitulo}>{titulo}</h2>
        {children}
      </div>
    </div>
  );
}

interface ConfirmarProps {
  titulo: string;
  texto: string;
  rotuloDeConfirmar: string;
  perigo?: boolean;
  ocupado?: boolean;
  onConfirmar: () => void;
  onCancelar: () => void;
}

/** Confirmação de uma ação com efeito: o texto diz o que acontece. */
export function ConfirmarAcao({ titulo, texto, rotuloDeConfirmar, perigo = false, ocupado = false, onConfirmar, onCancelar }: ConfirmarProps) {
  return (
    <Dialogo titulo={titulo} onFechar={onCancelar}>
      <p>{texto}</p>
      <div className="acoes">
        <button type="button" className={perigo ? 'btn btn-perigo' : 'btn btn-primario'} disabled={ocupado} onClick={onConfirmar}>
          {rotuloDeConfirmar}
        </button>
        <button type="button" className="btn" onClick={onCancelar}>
          Cancelar
        </button>
      </div>
    </Dialogo>
  );
}
