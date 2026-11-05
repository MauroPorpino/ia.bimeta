export interface Aviso {
  id: string;
  texto: string;
  /** Avisos de ação ficam até o usuário dispensar; os de rede somem sozinhos quando o polling se recupera. */
  dispensavel: boolean;
}

interface Props {
  avisos: Aviso[];
  onDispensar: (id: string) => void;
}

/** Faixa discreta de erros de rede e de API (nada de alert()). */
export function ErrorBanner({ avisos, onDispensar }: Props) {
  if (avisos.length === 0) {
    return null;
  }
  return (
    <div className="faixa-erros" role="alert">
      {avisos.map((aviso) => (
        <div className="faixa-erro" key={aviso.id}>
          <span>{aviso.texto}</span>
          {aviso.dispensavel && (
            <button type="button" className="btn-link" onClick={() => onDispensar(aviso.id)} aria-label="Dispensar aviso">
              Dispensar
            </button>
          )}
        </div>
      ))}
    </div>
  );
}
