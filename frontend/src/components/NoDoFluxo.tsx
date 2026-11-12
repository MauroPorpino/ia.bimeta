import { Handle, Position, type NodeProps } from '@xyflow/react';
import { ROTULO_DO_TIPO, rotuloDaPorta, type NoDoCanvas } from '../fluxo';
import type { FlowNode } from '../api/types';

/** Uma linha de resumo do que o nó faz (só texto de configuração do próprio fluxo, nunca conteúdo de cliente). */
export function resumoDoNo(no: FlowNode): string {
  const c = no.config ?? {};
  const texto = (t: string | null | undefined) => (t && t.length > 60 ? `${t.slice(0, 57)}...` : (t ?? ''));
  switch (no.type) {
    case 'message':
    case 'end':
      return texto(c.text);
    case 'question':
      return texto(c.prompt);
    case 'condition':
      return `${c.branches?.length ?? 0} ramo(s) + senão`;
    case 'knowledge':
      return 'Resposta aprovada da empresa';
    case 'integration':
      return c.operationKey ? c.operationKey : 'Escolha uma operação';
    case 'handoff':
      return texto(c.text) || 'Transferir para atendente';
    case 'ai':
      return 'Entrega ao agente de IA';
    default:
      return '';
  }
}

/** O nó do canvas: título do tipo, resumo e uma saída (com rótulo) por porta. As bordas dizem o estado: erro de validação, visitado na simulação, selecionado. */
export function NoDoFluxo({ data, selected }: NodeProps<NoDoCanvas>) {
  const { no, portas, comProblema, visitado, inicio } = data;
  const classes = ['fluxo-no', `fluxo-no-${no.type}`, selected ? 'fluxo-no-selecionado' : '', comProblema ? 'fluxo-no-problema' : '', visitado ? 'fluxo-no-visitado' : ''].filter(Boolean).join(' ');
  return (
    <div className={classes} data-testid={`no-${no.id}`}>
      <Handle type="target" position={Position.Left} aria-label="Entrada" />
      <div className="fluxo-no-titulo">
        <strong>{no.label || ROTULO_DO_TIPO[no.type]}</strong>
        {inicio && <span className="fluxo-no-inicio">início</span>}
      </div>
      <div className="fluxo-no-resumo">{resumoDoNo(no)}</div>
      {portas.length > 0 && (
        <ul className="fluxo-no-portas">
          {portas.map((porta) => (
            <li key={porta}>
              <span>{rotuloDaPorta(porta)}</span>
              <Handle type="source" position={Position.Right} id={porta} aria-label={`Saída ${rotuloDaPorta(porta)}`} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
