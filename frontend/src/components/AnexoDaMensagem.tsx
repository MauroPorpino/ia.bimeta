import { useCallback, useEffect, useRef, useState } from 'react';
import { descreverErro } from '../api/client';
import type { Attachment, MessageKind } from '../api/types';
import { formatarTamanho, motivoDoAnexo } from '../format';

interface Props {
  anexo: Attachment;
  /** O tipo da mensagem (imagem, áudio, documento): dá o rótulo enquanto o arquivo não chegou. */
  tipo: MessageKind;
  /** Baixa o arquivo (com a sessão do atendente) como Blob. Sem isto, o anexo só mostra o estado. */
  obter?: () => Promise<Blob>;
}

const ROTULO_DO_TIPO: Partial<Record<MessageKind, string>> = {
  Image: 'imagem',
  Audio: 'áudio',
  Document: 'documento',
};

/** Quantas imagens ao mesmo tempo o navegador baixa para as miniaturas: o resto espera a sua vez (um histórico com muitas fotos não dispara todas de uma vez). */
const MAXIMO_DE_MINIATURAS_SIMULTANEAS = 4;
let miniaturasEmAndamento = 0;
const filaDeMiniaturas: Array<() => void> = [];

async function comVagaDeMiniatura<T>(trabalho: () => Promise<T>): Promise<T> {
  if (miniaturasEmAndamento >= MAXIMO_DE_MINIATURAS_SIMULTANEAS) {
    await new Promise<void>((liberar) => filaDeMiniaturas.push(liberar));
  } else {
    miniaturasEmAndamento += 1;
  }
  try {
    return await trabalho();
  } finally {
    const proximo = filaDeMiniaturas.shift();
    if (proximo) {
      proximo();
    } else {
      miniaturasEmAndamento -= 1;
    }
  }
}

/**
 * O arquivo de uma mensagem de mídia no histórico (ADR 0028): "recebendo" enquanto o servidor busca no WhatsApp, a miniatura da imagem, o player do áudio, o
 * documento para baixar, ou o motivo de o arquivo não estar disponível. O conteúdo vem pela rota autenticada do atendimento (nunca há URL pública) e é exibido
 * como Blob local, que a tela libera ao sair.
 */
export function AnexoDaMensagem({ anexo, tipo, obter }: Props) {
  const rotulo = ROTULO_DO_TIPO[tipo] ?? 'arquivo';
  const tipoDoArquivo = anexo.mimeType ?? '';

  return (
    <div className="anexo" data-anexo-estado={anexo.status} data-anexo-tipo={tipo}>
      {anexo.status === 'Pending' && (
        <p className="anexo-estado anexo-pendente" role="status">
          Recebendo {rotulo}...
        </p>
      )}

      {(anexo.status === 'Unavailable' || anexo.status === 'Rejected') && (
        <p className={anexo.status === 'Rejected' ? 'anexo-estado anexo-recusado' : 'anexo-estado anexo-indisponivel'} role="status">
          <strong>{anexo.status === 'Rejected' ? 'Arquivo bloqueado.' : 'Arquivo indisponível.'}</strong> {motivoDoAnexo(anexo.status, anexo.failureCode)}
        </p>
      )}

      {anexo.status === 'Available' && tipoDoArquivo.startsWith('image/') && <Imagem anexo={anexo} obter={obter} />}
      {anexo.status === 'Available' && tipoDoArquivo.startsWith('audio/') && <Audio anexo={anexo} obter={obter} />}
      {anexo.status === 'Available' && !tipoDoArquivo.startsWith('image/') && !tipoDoArquivo.startsWith('audio/') && <Documento anexo={anexo} obter={obter} />}

      {anexo.caption && <p className="anexo-legenda">{anexo.caption}</p>}
    </div>
  );
}

/** Baixa o arquivo e devolve o endereço local (blob:) para exibi-lo; libera o endereço ao sair ou ao trocar de arquivo. */
function useArquivoLocal(anexo: Attachment, obter: Props['obter'], automatico: boolean, limitar = false) {
  const [url, setUrl] = useState<string | null>(null);
  const [carregando, setCarregando] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const atual = useRef<string | null>(null);
  const montado = useRef(true);

  useEffect(() => {
    montado.current = true;
    return () => {
      montado.current = false;
    };
  }, []);

  const liberar = useCallback(() => {
    if (atual.current) {
      URL.revokeObjectURL(atual.current);
      atual.current = null;
    }
  }, []);

  const carregar = useCallback(async () => {
    if (!obter) {
      return;
    }
    setCarregando(true);
    setErro(null);
    try {
      const baixar = () => obter();
      const blob = limitar ? await comVagaDeMiniatura(baixar) : await baixar();
      if (!montado.current) {
        return;
      }
      liberar();
      atual.current = URL.createObjectURL(blob);
      setUrl(atual.current);
    } catch (e) {
      if (montado.current) {
        setErro(descreverErro(e));
      }
    } finally {
      if (montado.current) {
        setCarregando(false);
      }
    }
  }, [obter, liberar, limitar]);

  // Outro arquivo na mesma posição (a tela reaproveita o componente): o endereço antigo é descartado.
  useEffect(() => {
    setUrl(null);
    setErro(null);
    liberar();
    if (automatico) {
      void carregar();
    }
    return liberar;
    // `obter` muda a cada renderização do pai: o arquivo só é buscado de novo quando o ANEXO muda.
  }, [anexo.id, anexo.status, automatico]);

  return { url, carregando, erro, carregar };
}

function Imagem({ anexo, obter }: { anexo: Attachment; obter: Props['obter'] }) {
  const { url, carregando, erro, carregar } = useArquivoLocal(anexo, obter, true, true);
  const [ampliada, setAmpliada] = useState(false);
  const descricao = anexo.caption ?? anexo.fileName ?? 'Imagem recebida do cliente';

  if (erro) {
    return (
      <div className="anexo-estado anexo-erro" role="alert">
        <p>Não foi possível carregar a imagem. {erro}</p>
        <button type="button" className="btn btn-pequeno" onClick={() => void carregar()}>
          Tentar de novo
        </button>
      </div>
    );
  }
  if (!url) {
    return (
      <p className="anexo-estado anexo-pendente" role="status">
        {carregando ? 'Carregando imagem...' : 'Imagem recebida.'}
      </p>
    );
  }
  return (
    <figure className={ampliada ? 'anexo-figura ampliada' : 'anexo-figura'}>
      <button
        type="button"
        className="anexo-imagem-botao"
        onClick={() => setAmpliada((a) => !a)}
        aria-label={ampliada ? 'Reduzir imagem' : 'Ampliar imagem'}
        aria-pressed={ampliada}
      >
        <img className="anexo-imagem" src={url} alt={descricao} />
      </button>
      <figcaption className="anexo-detalhe">
        {anexo.fileName ?? 'imagem'}
        {anexo.sizeBytes !== null && ` · ${formatarTamanho(anexo.sizeBytes)}`}
      </figcaption>
    </figure>
  );
}

function Audio({ anexo, obter }: { anexo: Attachment; obter: Props['obter'] }) {
  // O áudio só é baixado quando o atendente pede (um histórico com muitos áudios não os baixa todos), e toca no player nativo.
  const { url, carregando, erro, carregar } = useArquivoLocal(anexo, obter, false);
  const descricao = `Áudio recebido${anexo.sizeBytes !== null ? ` (${formatarTamanho(anexo.sizeBytes)})` : ''}`;

  if (url) {
    return (
      <div className="anexo-audio">
        <audio controls preload="metadata" src={url} aria-label={descricao} />
      </div>
    );
  }
  return (
    <div className="anexo-audio">
      <button type="button" className="btn btn-pequeno" onClick={() => void carregar()} disabled={carregando || !obter}>
        {carregando ? 'Carregando áudio...' : erro ? 'Tentar carregar o áudio de novo' : 'Carregar áudio'}
      </button>
      <span className="anexo-detalhe">{descricao}</span>
      {erro && (
        <p className="anexo-estado anexo-erro" role="alert">
          Não foi possível carregar o áudio. {erro}
        </p>
      )}
    </div>
  );
}

function Documento({ anexo, obter }: { anexo: Attachment; obter: Props['obter'] }) {
  const [baixando, setBaixando] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const montado = useRef(true);

  useEffect(() => {
    montado.current = true;
    return () => {
      montado.current = false;
    };
  }, []);

  async function baixar() {
    if (!obter) {
      return;
    }
    setBaixando(true);
    setErro(null);
    try {
      const blob = await obter();
      const endereco = URL.createObjectURL(blob);
      // O download sai por um link temporário com o nome saneado; o endereço local é liberado logo depois.
      const ancora = document.createElement('a');
      ancora.href = endereco;
      ancora.download = anexo.fileName ?? 'arquivo';
      ancora.rel = 'noopener';
      document.body.appendChild(ancora);
      ancora.click();
      ancora.remove();
      setTimeout(() => URL.revokeObjectURL(endereco), 10_000);
    } catch (e) {
      if (montado.current) {
        setErro(descreverErro(e));
      }
    } finally {
      if (montado.current) {
        setBaixando(false);
      }
    }
  }

  return (
    <div className="anexo-documento">
      <span className="anexo-icone" aria-hidden="true">
        ▤
      </span>
      <div className="anexo-documento-dados">
        <span className="anexo-nome">{anexo.fileName ?? 'documento'}</span>
        <span className="anexo-detalhe">
          {anexo.mimeType}
          {anexo.sizeBytes !== null && ` · ${formatarTamanho(anexo.sizeBytes)}`}
        </span>
      </div>
      <button type="button" className="btn btn-pequeno" onClick={() => void baixar()} disabled={baixando || !obter}>
        {baixando ? 'Baixando...' : 'Baixar'}
      </button>
      {erro && (
        <p className="anexo-estado anexo-erro" role="alert">
          Não foi possível baixar o documento. {erro}
        </p>
      )}
    </div>
  );
}
