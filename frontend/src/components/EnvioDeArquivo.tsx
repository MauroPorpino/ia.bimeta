import { useEffect, useRef, useState, type ChangeEvent } from 'react';
import { formatarTamanho } from '../format';
import { ACCEPT_DE_ARQUIVOS, LIMITE_DA_LEGENDA, LIMITES_RESUMO, ROTULO_DO_TIPO_DE_ARQUIVO, tipoDoArquivo, validarArquivo } from '../arquivos';

/**
 * Enviar um ARQUIVO ao cliente (ADR 0031): o atendente escolhe uma imagem, um áudio ou um documento, vê o cartão com nome, tipo e tamanho, escreve a legenda (imagem e
 * documento; o áudio não tem) e envia. O texto do campo de resposta é outra ação: nunca vai junto, nem se perde. A conferência de tipo, tamanho e arquivo vazio é só um
 * feedback antecipado (sem enviar nada); o servidor descobre o tipo real pelo conteúdo, repete as verificações e decide.
 */

interface Props {
  /** Há uma ação em andamento no painel (esta ou outra): o formulário fica travado. */
  ocupado: boolean;
  /** Por que o SERVIDOR recusou o último envio do arquivo escolhido (422 `MediaRejected` ou 413), já em português; o cartão o mostra ao lado do arquivo. */
  recusa: string | null;
  /** Envia o arquivo com a legenda (ou `null`). Devolve verdadeiro se o envio foi concluído (o cartão é limpo); a recusa chega por `recusa`. */
  aoEnviar: (arquivo: File, legenda: string | null) => Promise<boolean>;
  /** O atendente mudou o arquivo ou a legenda: a recusa anterior deixa de valer. */
  aoEditar?: () => void;
}

export function EnvioDeArquivo({ ocupado, recusa, aoEnviar, aoEditar }: Props) {
  const [arquivo, setArquivo] = useState<File | null>(null);
  const [legenda, setLegenda] = useState('');
  const [enviando, setEnviando] = useState(false);
  const montado = useRef(true);
  useEffect(() => {
    montado.current = true;
    return () => {
      montado.current = false;
    };
  }, []);

  const tipo = arquivo ? tipoDoArquivo(arquivo) : null;
  const validacao = arquivo ? validarArquivo(arquivo, legenda) : null;
  const semLegenda = tipo === 'Audio';
  const legendaDoEnvio = semLegenda ? null : legenda.trim() || null;

  function escolher(evento: ChangeEvent<HTMLInputElement>) {
    const escolhido = evento.target.files?.[0] ?? null;
    // O campo é esvaziado para que escolher o MESMO arquivo outra vez (depois de removê-lo) dispare a escolha de novo.
    evento.target.value = '';
    if (escolhido) {
      setArquivo(escolhido);
      aoEditar?.();
    }
  }

  function remover() {
    setArquivo(null);
    setLegenda('');
    aoEditar?.();
  }

  function mudarLegenda(nova: string) {
    setLegenda(nova);
    aoEditar?.();
  }

  async function enviar() {
    if (!arquivo || ocupado || enviando || validacao?.valido !== true) {
      return;
    }
    setEnviando(true);
    try {
      const ok = await aoEnviar(arquivo, legendaDoEnvio);
      if (ok && montado.current) {
        setArquivo(null);
        setLegenda('');
      }
    } finally {
      if (montado.current) {
        setEnviando(false);
      }
    }
  }

  return (
    <div className="envio-arquivo">
      <div className="envio-arquivo-barra">
        <label className={ocupado ? 'btn btn-pequeno btn-anexar desativado' : 'btn btn-pequeno btn-anexar'} htmlFor="arquivo-resposta">
          Anexar arquivo
        </label>
        <input
          id="arquivo-resposta"
          className="sr-only"
          type="file"
          accept={ACCEPT_DE_ARQUIVOS}
          disabled={ocupado}
          aria-describedby="arquivo-resposta-dica"
          onChange={escolher}
        />
        <span className="discreto" id="arquivo-resposta-dica">
          {LIMITES_RESUMO}
        </span>
      </div>

      {arquivo && (
        <section className="arquivo-escolhido" role="group" aria-label="Arquivo a enviar">
          <div className="arquivo-escolhido-dados">
            <span className="anexo-nome">{arquivo.name}</span>
            <span className="anexo-detalhe">
              {tipo ? ROTULO_DO_TIPO_DE_ARQUIVO[tipo] : 'Arquivo'} · {formatarTamanho(arquivo.size)}
            </span>
          </div>

          {validacao && !validacao.valido && (
            <p className="erro-campo" role="alert">
              {validacao.texto}
            </p>
          )}
          {recusa && (
            <p className="erro-campo" role="alert">
              {recusa}
            </p>
          )}

          {semLegenda ? (
            <p className="discreto">Áudio não aceita legenda.</p>
          ) : (
            <div className="campo legenda-arquivo">
              <label htmlFor="legenda-arquivo">Legenda (opcional)</label>
              <textarea
                id="legenda-arquivo"
                value={legenda}
                maxLength={LIMITE_DA_LEGENDA}
                rows={2}
                disabled={ocupado}
                aria-describedby="legenda-arquivo-contador"
                onChange={(e) => mudarLegenda(e.target.value)}
              />
              <span className="discreto" id="legenda-arquivo-contador">
                {legenda.length}/{LIMITE_DA_LEGENDA}
              </span>
            </div>
          )}

          <div className="acoes">
            <button type="button" className="btn btn-primario" disabled={ocupado || enviando || validacao?.valido !== true} onClick={() => void enviar()}>
              {enviando ? 'Enviando arquivo...' : 'Enviar arquivo'}
            </button>
            <button type="button" className="btn" disabled={ocupado || enviando} onClick={remover}>
              Remover
            </button>
          </div>
        </section>
      )}
    </div>
  );
}
