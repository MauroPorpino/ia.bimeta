import type { EstadoDaConexao } from '../hooks';

interface Props {
  estado: EstadoDaConexao;
}

/** Faixa de conexão: avisa que a tela pode estar desatualizada e que ela tenta voltar sozinha; some quando a conexão volta. */
export function ConexaoBanner({ estado }: Props) {
  if (estado === 'online') {
    return null;
  }
  if (estado === 'restabelecida') {
    return (
      <div className="faixa-conexao restabelecida" role="status">
        Conexão restabelecida. A fila e as conversas foram atualizadas.
      </div>
    );
  }
  return (
    <div className="faixa-conexao offline" role="alert">
      Sem conexão com o servidor. O que você vê pode estar desatualizado; tentando reconectar sozinho. Suas respostas em rascunho continuam guardadas.
    </div>
  );
}
