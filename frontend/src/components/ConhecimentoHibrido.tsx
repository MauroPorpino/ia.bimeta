import { useState } from 'react';
import type { ApiClient } from '../api/client';
import { SemanticaConfig } from './SemanticaConfig';
import { SugestoesDaIa } from './SugestoesDaIa';

interface Props {
  api: ApiClient;
  lacunaSelecionada: string | null;
  onRascunhoAlterado: () => void;
  onLacunasAlteradas: () => void;
}

/**
 * O conhecimento HÍBRIDO do tenant (ADR 0034): busca semântica (configuração e métricas) e sugestões da IA local. Fica recolhido até o administrador abrir: só então a tela consulta o servidor,
 * e um ambiente sem a camada semântica (desligada ou sem o serviço de IA) não incomoda quem só revisa lacunas.
 */
export function ConhecimentoHibrido({ api, lacunaSelecionada, onRascunhoAlterado, onLacunasAlteradas }: Props) {
  const [aberto, setAberto] = useState(false);

  return (
    <section className="hibrido" aria-label="Conhecimento híbrido">
      <button type="button" className="btn" aria-expanded={aberto} onClick={() => setAberto((a) => !a)}>
        {aberto ? 'Esconder' : 'Mostrar'} busca semântica e sugestões da IA
      </button>
      {aberto && (
        <>
          <SemanticaConfig api={api} />
          <SugestoesDaIa api={api} lacunaSelecionada={lacunaSelecionada} onRascunhoAlterado={onRascunhoAlterado} onLacunasAlteradas={onLacunasAlteradas} />
        </>
      )}
    </section>
  );
}
