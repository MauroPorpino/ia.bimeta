import { useState } from 'react';
import type { ApiClient } from '../api/client';
import type { CurrentUser } from '../api/types';
import { ConexoesDeIntegracao } from './ConexoesDeIntegracao';
import { HistoricoDeIntegracao } from './HistoricoDeIntegracao';
import { OperacoesDeIntegracao } from './OperacoesDeIntegracao';

type Aba = 'conexoes' | 'operacoes' | 'execucoes';

const ABAS: Array<{ id: Aba; rotulo: string }> = [
  { id: 'conexoes', rotulo: 'Conexões' },
  { id: 'operacoes', rotulo: 'Operações' },
  { id: 'execucoes', rotulo: 'Execuções' },
];

interface Props {
  api: ApiClient;
  me: CurrentUser;
  /** A área está escondida (a pessoa está no Atendimento): continua montada para não perder o que ela estava escrevendo. */
  oculto?: boolean;
}

/**
 * A área "Integrações" (ADR 0035): conexões com ERPs e sistemas legados, o catálogo de operações e o histórico. Cada aba só é montada quando aberta pela primeira vez e, depois, fica montada (escondida)
 * ao trocar de aba, para não perder um formulário em edição. O servidor decide cada permissão: quem só pode ler vê as telas e recebe a explicação do 403 nas ações.
 */
export function Integracoes({ api, me, oculto = false }: Props) {
  const [aba, setAba] = useState<Aba>('conexoes');
  const [abertas, setAbertas] = useState<Set<Aba>>(new Set<Aba>(['conexoes']));

  function abrir(destino: Aba) {
    setAba(destino);
    setAbertas((atuais) => (atuais.has(destino) ? atuais : new Set([...atuais, destino])));
  }

  return (
    <main className="integracoes" hidden={oculto}>
      <nav className="abas-integracoes" aria-label="Seções de integrações">
        {ABAS.map((a) => (
          <button key={a.id} type="button" className="area-aba" aria-current={aba === a.id ? 'page' : undefined} onClick={() => abrir(a.id)}>
            {a.rotulo}
          </button>
        ))}
      </nav>
      <div className="integracoes-aba" hidden={aba !== 'conexoes'}>
        {abertas.has('conexoes') && <ConexoesDeIntegracao api={api} me={me} />}
      </div>
      <div className="integracoes-aba" hidden={aba !== 'operacoes'}>
        {abertas.has('operacoes') && <OperacoesDeIntegracao api={api} />}
      </div>
      <div className="integracoes-aba" hidden={aba !== 'execucoes'}>
        {abertas.has('execucoes') && <HistoricoDeIntegracao api={api} />}
      </div>
    </main>
  );
}
