import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { ApiError, type ApiClient } from '../api/client';
import type { KnowledgeGapDetail, KnowledgeGapKind } from '../api/types';
import {
  LIMITE_ALIASES,
  LIMITE_PERGUNTA,
  LIMITE_RESPOSTA,
  ROTULO_SITUACAO_DA_LACUNA,
  ROTULO_TIPO_DA_LACUNA,
  aliasesDoTexto,
  dataHora,
  mensagemDeErroDoConhecimento,
} from '../conhecimento';

interface Props {
  api: ApiClient;
  lacunaId: string;
  /** A lacuna mudou de situação (enviada ao rascunho ou descartada): a lista relê. */
  onDecidida: () => void;
  /** Uma entrada foi para o rascunho (ou o rascunho mudou por outra operação): o painel do rascunho relê. */
  onRascunhoAlterado: () => void;
}

const EXPLICACAO_DO_TIPO: Record<KnowledgeGapKind, string> = {
  NoMatch: 'Nenhuma entrada do FAQ responde esta pergunta.',
  Ambiguous: 'Mais de uma entrada do FAQ corresponde a esta pergunta (duplicatas).',
  NotAutoReply: 'O FAQ já tem uma entrada para esta pergunta, mas sem resposta direta.',
};

function ocorrencias(quantidade: number): string {
  return quantidade === 1 ? '1 ocorrência' : `${quantidade} ocorrências`;
}

/**
 * A revisão de UMA lacuna do FAQ (ADR 0030): a pergunta original (lida pelo servidor com a autorização de quem pede), a resposta de um
 * atendente como SUGESTÃO e o formulário da entrada. Nada daqui é publicado: "Enviar ao rascunho" só põe a entrada no rascunho da definição, e
 * a resposta humana nunca preenche o formulário sozinha. Uma lacuna que não está aberta só pode ser lida.
 */
export function LacunaRevisao({ api, lacunaId, onDecidida, onRascunhoAlterado }: Props) {
  const idBase = useId();
  const [detalhe, setDetalhe] = useState<KnowledgeGapDetail | null>(null);
  const [erroDeCarga, setErroDeCarga] = useState<string | null>(null);
  const [tentativa, setTentativa] = useState(0);

  const [pergunta, setPergunta] = useState('');
  const [aliasesTexto, setAliasesTexto] = useState('');
  const [resposta, setResposta] = useState('');
  // Desligada por padrão e só vale se o administrador a liga de propósito, a cada entrada.
  const [autoReply, setAutoReply] = useState(false);

  const [ocupado, setOcupado] = useState<'promover' | 'descartar' | null>(null);
  const [confirmandoDescarte, setConfirmandoDescarte] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [resultado, setResultado] = useState<string | null>(null);
  const emAndamento = useRef(false);
  const montado = useRef(true);

  useEffect(() => {
    montado.current = true;
    return () => {
      montado.current = false;
    };
  }, []);

  // A leitura da lacuna. O formulário nasce aqui, uma vez: a pergunta original vem como ponto de partida (editável); a resposta humana NÃO entra.
  useEffect(() => {
    let vivo = true;
    api
      .obterLacunaDoFaq(lacunaId)
      .then((lido) => {
        if (vivo) {
          setDetalhe(lido);
          setErroDeCarga(null);
          setPergunta(lido.source.question ?? '');
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
  }, [api, lacunaId, tentativa]);

  /** Relê só a situação da lacuna (depois de um conflito), sem mexer no que o administrador já escreveu no formulário. */
  async function relerSituacao() {
    try {
      const lido = await api.obterLacunaDoFaq(lacunaId);
      if (montado.current) {
        setDetalhe(lido);
      }
    } catch {
      // mantém o que está na tela: o erro da ação já foi mostrado
    }
  }

  async function aoFalhar(falha: unknown) {
    setErro(mensagemDeErroDoConhecimento(falha));
    if (falha instanceof ApiError && falha.status === 409) {
      if (falha.code === 'gap-not-open' || falha.code === 'gap-changed') {
        await relerSituacao();
        onDecidida();
      } else if (falha.code === 'definition-changed') {
        onRascunhoAlterado();
      }
    }
  }

  async function enviarAoRascunho(evento: FormEvent) {
    evento.preventDefault();
    if (emAndamento.current || !detalhe) {
      return;
    }
    if (pergunta.trim() === '' || resposta.trim() === '') {
      setErro('Preencha a pergunta e a resposta da entrada.');
      return;
    }
    emAndamento.current = true;
    setOcupado('promover');
    setErro(null);
    setResultado(null);
    try {
      const enviada = await api.enviarLacunaAoRascunho(detalhe.gap.id, {
        question: pergunta.trim(),
        aliases: aliasesDoTexto(aliasesTexto),
        answer: resposta.trim(),
        autoReply,
      });
      setDetalhe((atual) => (atual ? { ...atual, gap: enviada.gap } : atual));
      setResultado(`Entrada enviada ao rascunho (versão ${enviada.draftVersion}). Nada foi publicado.`);
      onRascunhoAlterado();
      onDecidida();
    } catch (falha) {
      await aoFalhar(falha);
    } finally {
      emAndamento.current = false;
      if (montado.current) {
        setOcupado(null);
      }
    }
  }

  async function descartar() {
    if (emAndamento.current || !detalhe) {
      return;
    }
    emAndamento.current = true;
    setOcupado('descartar');
    setErro(null);
    setResultado(null);
    try {
      const descartada = await api.descartarLacunaDoFaq(detalhe.gap.id);
      setDetalhe((atual) => (atual ? { ...atual, gap: descartada } : atual));
      setConfirmandoDescarte(false);
      setResultado('Lacuna descartada. Nada foi alterado no FAQ.');
      onDecidida();
    } catch (falha) {
      setConfirmandoDescarte(false);
      await aoFalhar(falha);
    } finally {
      emAndamento.current = false;
      if (montado.current) {
        setOcupado(null);
      }
    }
  }

  if (detalhe === null) {
    return (
      <section className="revisao" aria-label="Revisão da lacuna">
        <h2>Revisão da lacuna</h2>
        {erroDeCarga ? (
          <>
            <p className="erro-campo" role="alert">
              {erroDeCarga}
            </p>
            <div className="acoes">
              <button type="button" className="btn" onClick={() => setTentativa((t) => t + 1)}>
                Tentar novamente
              </button>
            </div>
          </>
        ) : (
          <p className="discreto">Carregando...</p>
        )}
      </section>
    );
  }

  const { gap, source: fonte } = detalhe;
  const aberta = gap.status === 'Open';
  const respostaHumana = fonte.humanReply;
  const intercaladas = respostaHumana?.customerMessagesBetween ?? 0;
  const aliases = aliasesDoTexto(aliasesTexto);
  const idPergunta = `${idBase}-pergunta`;
  const idAliases = `${idBase}-aliases`;
  const idResposta = `${idBase}-resposta`;
  const idAlertaAutoReply = `${idBase}-alerta-auto-reply`;

  return (
    <section className="revisao" aria-label="Revisão da lacuna">
      <h2>Revisão da lacuna</h2>
      <p className="discreto">
        <strong>{ROTULO_TIPO_DA_LACUNA[gap.kind]}</strong>. {EXPLICACAO_DO_TIPO[gap.kind]}
      </p>
      <p className="discreto">
        {ocorrencias(gap.occurrences)} (a primeira em {dataHora(gap.firstSeenAt)}, a última em {dataHora(gap.lastSeenAt)}). Situação:{' '}
        {ROTULO_SITUACAO_DA_LACUNA[gap.status]}
        {gap.status === 'Promoted' && gap.promotedVersion !== null ? ` (versão ${gap.promotedVersion} do rascunho)` : ''}
        {gap.status !== 'Open' && gap.decidedAt ? ` em ${dataHora(gap.decidedAt)}` : ''}.
      </p>

      {resultado && (
        <p className="aviso" role="status">
          {resultado}
        </p>
      )}

      {fonte.state === 'Available' ? (
        <div className="pergunta-original">
          <h3>Pergunta original do cliente</h3>
          <blockquote className="texto-citado">{fonte.question}</blockquote>
        </div>
      ) : (
        <p className="aviso" role="note">
          A mensagem original não está disponível (foi removida ou você não tem permissão para vê-la){aberta ? ': escreva a pergunta.' : '.'}
        </p>
      )}

      {respostaHumana && (
        <section className="sugestao" aria-label="Resposta de um atendente">
          <h3>Resposta de um atendente</h3>
          <p className="discreto">Confira antes de usar: é só uma sugestão, não é usada automaticamente.</p>
          {intercaladas > 0 && (
            <p className="aviso" role="note">
              O cliente enviou outras mensagens ({intercaladas}) antes dessa resposta: ela pode ser de outra pergunta.
            </p>
          )}
          <blockquote className="texto-citado">{respostaHumana.text}</blockquote>
          {aberta && (
            <div className="acoes">
              <button type="button" className="btn" disabled={ocupado !== null} onClick={() => setResposta(respostaHumana.text)}>
                Usar esta resposta
              </button>
            </div>
          )}
        </section>
      )}

      {aberta && (
        <form className="revisao-formulario" aria-label="Entrada do FAQ" onSubmit={(e) => void enviarAoRascunho(e)}>
          <div className="campo">
            <label htmlFor={idPergunta}>Pergunta</label>
            <textarea id={idPergunta} rows={2} value={pergunta} disabled={ocupado !== null} aria-describedby={`${idPergunta}-contador`} onChange={(e) => setPergunta(e.target.value)} />
            <span id={`${idPergunta}-contador`} className={pergunta.length > LIMITE_PERGUNTA ? 'contador excedido' : 'contador'}>
              {pergunta.length} de {LIMITE_PERGUNTA} caracteres
            </span>
          </div>

          <div className="campo">
            <label htmlFor={idAliases}>Aliases (um por linha)</label>
            <textarea id={idAliases} rows={3} value={aliasesTexto} disabled={ocupado !== null} aria-describedby={`${idAliases}-contador`} onChange={(e) => setAliasesTexto(e.target.value)} />
            <span id={`${idAliases}-contador`} className={aliases.length > LIMITE_ALIASES ? 'contador excedido' : 'contador'}>
              {aliases.length} de {LIMITE_ALIASES} aliases (outras formas de fazer a mesma pergunta; cada um até {LIMITE_PERGUNTA} caracteres)
            </span>
          </div>

          <div className="campo">
            <label htmlFor={idResposta}>Resposta</label>
            <textarea id={idResposta} rows={5} value={resposta} disabled={ocupado !== null} aria-describedby={`${idResposta}-contador`} onChange={(e) => setResposta(e.target.value)} />
            <span id={`${idResposta}-contador`} className={resposta.length > LIMITE_RESPOSTA ? 'contador excedido' : 'contador'}>
              {resposta.length} de {LIMITE_RESPOSTA} caracteres
            </span>
          </div>

          <label className="caixa">
            <input type="checkbox" checked={autoReply} disabled={ocupado !== null} aria-describedby={idAlertaAutoReply} onChange={(e) => setAutoReply(e.target.checked)} />
            <span>Responder direto, sem usar a IA (autoReply)</span>
          </label>
          <p className="aviso" id={idAlertaAutoReply}>
            Só ligue em respostas que se sustentam sozinhas: sem preço, agenda, cobertura, orientação clínica nem referência ao que foi dito antes.
          </p>

          {erro && (
            <p className="erro-campo" role="alert">
              {erro}
            </p>
          )}

          <div className="acoes">
            {!confirmandoDescarte ? (
              <>
                <button type="submit" className="btn btn-primario" disabled={ocupado !== null}>
                  {ocupado === 'promover' ? 'Enviando...' : 'Enviar ao rascunho'}
                </button>
                <button type="button" className="btn btn-perigo" disabled={ocupado !== null} onClick={() => setConfirmandoDescarte(true)}>
                  Descartar
                </button>
              </>
            ) : (
              <span className="confirmacao" role="group" aria-label="Confirmar descarte">
                <span>Descartar esta lacuna? Ela sai da lista de abertas e nada é alterado no FAQ.</span>
                <button type="button" className="btn btn-perigo" disabled={ocupado !== null} onClick={() => void descartar()}>
                  {ocupado === 'descartar' ? 'Descartando...' : 'Confirmar descarte'}
                </button>
                <button type="button" className="btn" disabled={ocupado !== null} onClick={() => setConfirmandoDescarte(false)}>
                  Cancelar
                </button>
              </span>
            )}
          </div>
        </form>
      )}

      {!aberta && erro && (
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      )}
    </section>
  );
}
