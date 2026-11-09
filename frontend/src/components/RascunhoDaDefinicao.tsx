import { useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, type ApiClient } from '../api/client';
import type { AgentDefinition } from '../api/types';
import { LIMITE_ENTRADAS_DO_FAQ, compararComPublicada, dataHora, mensagemDeErroDoConhecimento } from '../conhecimento';

interface Props {
  api: ApiClient;
  /** Muda quando algo alterou o rascunho (uma entrada enviada pela revisão): o painel relê. */
  versao: number;
}

interface Versoes {
  rascunho: AgentDefinition | null;
  publicada: AgentDefinition | null;
}

/**
 * O rascunho da definição do agente ao lado da versão publicada (ADR 0030): o administrador vê o que mudou no FAQ (entradas novas e com resposta
 * direta) e publica de propósito, com confirmação. Publicar substitui a versão que os clientes usam; enviar uma lacuna ao rascunho nunca publica.
 */
export function RascunhoDaDefinicao({ api, versao }: Props) {
  const [versoes, setVersoes] = useState<Versoes | null>(null);
  const [erroDeCarga, setErroDeCarga] = useState<string | null>(null);
  const [recarga, setRecarga] = useState(0);
  const [confirmando, setConfirmando] = useState(false);
  const [publicando, setPublicando] = useState(false);
  const [erroAoPublicar, setErroAoPublicar] = useState<string | null>(null);
  const [resultado, setResultado] = useState<string | null>(null);
  const emAndamento = useRef(false);
  const montado = useRef(true);

  useEffect(() => {
    montado.current = true;
    return () => {
      montado.current = false;
    };
  }, []);

  // As duas versões lidas juntas; "não existe" (404) chega como nulo. Uma releitura mantém a tela anterior até a nova chegar.
  // Quando o rascunho muda, uma confirmação que estava aberta perde o sentido (ela foi dada para o que a tela mostrava antes).
  useEffect(() => {
    let vivo = true;
    setConfirmando(false);
    Promise.all([api.obterRascunhoDoAgente(), api.obterPublicadaDoAgente()])
      .then(([rascunho, publicada]) => {
        if (vivo) {
          setVersoes({ rascunho, publicada });
          setErroDeCarga(null);
        }
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErroDeCarga(mensagemDeErroDoConhecimento(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, versao, recarga]);

  const rascunho = versoes?.rascunho ?? null;
  const publicada = versoes?.publicada ?? null;
  const entradas = useMemo(() => compararComPublicada(rascunho?.content.faq ?? [], publicada ? publicada.content.faq : null), [rascunho, publicada]);
  const novas = entradas.filter((e) => e.marca === 'nova').length;
  const respostasDiretas = entradas.filter((e) => e.entrada.autoReply).length;

  async function publicar() {
    if (emAndamento.current) {
      return;
    }
    emAndamento.current = true;
    setPublicando(true);
    setErroAoPublicar(null);
    setResultado(null);
    try {
      const nova = await api.publicarRascunhoDoAgente();
      setConfirmando(false);
      setResultado(`Versão ${nova.number} publicada: é a que o agente usa agora.`);
      setRecarga((r) => r + 1);
    } catch (falha) {
      setConfirmando(false);
      setErroAoPublicar(mensagemDeErroDoConhecimento(falha));
      // O rascunho mudou (ou já não existe): a tela relê para mostrar o que vale agora.
      if (falha instanceof ApiError && (falha.status === 400 || falha.status === 409)) {
        setRecarga((r) => r + 1);
      }
    } finally {
      emAndamento.current = false;
      if (montado.current) {
        setPublicando(false);
      }
    }
  }

  return (
    <section className="rascunho" aria-label="Rascunho da definição">
      <h2>Rascunho da definição</h2>

      {resultado && (
        <p className="aviso" role="status">
          {resultado}
        </p>
      )}
      {erroAoPublicar && (
        <p className="erro-campo" role="alert">
          {erroAoPublicar}
        </p>
      )}

      {versoes === null && !erroDeCarga && <p className="discreto">Carregando...</p>}
      {erroDeCarga && (
        <>
          <p className="erro-campo" role="alert">
            {erroDeCarga}
          </p>
          <div className="acoes">
            <button type="button" className="btn" onClick={() => setRecarga((r) => r + 1)}>
              Tentar novamente
            </button>
          </div>
        </>
      )}

      {versoes !== null && rascunho === null && (
        <p className="discreto">
          Não há rascunho.{publicada ? ` A versão publicada atual é a ${publicada.number}.` : ' Também não há versão publicada.'}
        </p>
      )}

      {rascunho && (
        <>
          <p className="discreto">
            Rascunho: versão {rascunho.number}, atualizado em {dataHora(rascunho.updatedAt)}.{' '}
            {publicada
              ? `Versão publicada: ${publicada.number}${publicada.publishedAt ? `, desde ${dataHora(publicada.publishedAt)}` : ''}.`
              : 'Ainda não há versão publicada para comparar: todas as entradas são novas.'}
          </p>

          <h3>
            Perguntas do FAQ no rascunho ({entradas.length} de {LIMITE_ENTRADAS_DO_FAQ})
          </h3>
          {entradas.length === 0 ? (
            <p className="discreto">O FAQ do rascunho está vazio.</p>
          ) : (
            <ul className="faq-lista">
              {entradas.map(({ entrada, marca }, indice) => (
                <li key={`${indice}|${entrada.question}`} className="faq-entrada">
                  <div className="faq-pergunta">
                    <span className="item-nome">{entrada.question}</span>
                    {marca === 'nova' && <span className="etiqueta etiqueta-nova">nova</span>}
                    {marca === 'alterada' && <span className="etiqueta etiqueta-aviso">alterada</span>}
                    {entrada.autoReply && <span className="etiqueta etiqueta-direta">resposta direta</span>}
                  </div>
                  <p className="faq-resposta">{entrada.answer}</p>
                  {(entrada.aliases ?? []).length > 0 && <p className="discreto">Também: {(entrada.aliases ?? []).join('; ')}</p>}
                </li>
              ))}
            </ul>
          )}

          <div className="acoes">
            {!confirmando ? (
              <button type="button" className="btn btn-primario" disabled={publicando} onClick={() => setConfirmando(true)}>
                Publicar rascunho
              </button>
            ) : (
              <span className="confirmacao" role="group" aria-label="Confirmar publicação">
                <span>
                  A versão atual será substituída; os clientes passam a receber estas respostas.
                  {respostasDiretas > 0 ? ` ${respostasDiretas === 1 ? '1 entrada responde' : `${respostasDiretas} entradas respondem`} direto, sem passar pela IA.` : ''}
                  {novas > 0 ? ` Entradas novas: ${novas}.` : ''}
                </span>
                <button type="button" className="btn btn-primario" disabled={publicando} onClick={() => void publicar()}>
                  {publicando ? 'Publicando...' : 'Confirmar publicação'}
                </button>
                <button type="button" className="btn" disabled={publicando} onClick={() => setConfirmando(false)}>
                  Cancelar
                </button>
              </span>
            )}
          </div>
        </>
      )}
    </section>
  );
}
