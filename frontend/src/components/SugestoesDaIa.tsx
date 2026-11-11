import { useEffect, useRef, useState } from 'react';
import { ApiError, type ApiClient } from '../api/client';
import type { FaqSuggestion, FaqSuggestionGeneration, FaqSuggestionStatus } from '../api/types';
import { LIMITE_ALIASES, LIMITE_PERGUNTA, LIMITE_RESPOSTA, ROTULO_SITUACAO_DA_SUGESTAO, ROTULO_TIPO_DA_SUGESTAO, aliasesDoTexto, dataHora, mensagemDeErroDaIa, motivoSemSugestao } from '../conhecimento';

interface Props {
  api: ApiClient;
  /** A lacuna aberta que o administrador está olhando (para pedir uma sugestão só para ela). */
  lacunaSelecionada: string | null;
  /** Aceitar uma sugestão alterou o rascunho da definição: o painel do rascunho relê. */
  onRascunhoAlterado: () => void;
  /** Aceitar ou agrupar pode ter decidido lacunas: a lista de lacunas relê. */
  onLacunasAlteradas: () => void;
}

type Filtro = FaqSuggestionStatus | 'Todas';

const POR_PAGINA = 20;

function ehAchado(s: FaqSuggestion): boolean {
  return s.kind === 'Duplicate' || s.kind === 'Contradiction';
}

/**
 * As sugestões da IA LOCAL para o FAQ (ADR 0034): a IA só propõe, e só quando o administrador pede. Cada sugestão é revisada, editada se preciso e aceita ou rejeitada por uma pessoa.
 * Aceitar leva ao RASCUNHO (nunca publica; a resposta direta nunca é ligada por aqui; uma entrada nova só vale com a resposta escrita ou conferida por quem revisa).
 */
export function SugestoesDaIa({ api, lacunaSelecionada, onRascunhoAlterado, onLacunasAlteradas }: Props) {
  const [filtro, setFiltro] = useState<Filtro>('Pending');
  const [lista, setLista] = useState<FaqSuggestion[] | null>(null);
  const [total, setTotal] = useState(0);
  const [erro, setErro] = useState<string | null>(null);
  const [aviso, setAviso] = useState<string | null>(null);
  const [recarga, setRecarga] = useState(0);
  const [pedindo, setPedindo] = useState(false);
  const [selecionada, setSelecionada] = useState<string | null>(null);
  const emAndamento = useRef(false);

  useEffect(() => {
    let vivo = true;
    api
      .listarSugestoesDaIa({ status: filtro === 'Todas' ? undefined : filtro, take: POR_PAGINA, skip: 0 })
      .then((pagina) => {
        if (vivo) {
          setLista(pagina.items);
          setTotal(pagina.total);
          setErro(null);
        }
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErro(mensagemDeErroDaIa(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, filtro, recarga]);

  async function pedir(acao: () => Promise<FaqSuggestionGeneration>) {
    if (emAndamento.current) {
      return;
    }
    emAndamento.current = true;
    setPedindo(true);
    setErro(null);
    setAviso(null);
    try {
      const resultado = await acao();
      if (resultado.suggestions.length === 0) {
        setAviso(motivoSemSugestao(resultado.skipReason));
      } else {
        setAviso(`${resultado.suggestions.length} sugestão(ões) para revisar.`);
        setSelecionada(resultado.suggestions[0]!.id);
        setFiltro('Pending');
      }
      setRecarga((r) => r + 1);
    } catch (falha) {
      setErro(mensagemDeErroDaIa(falha));
    } finally {
      emAndamento.current = false;
      setPedindo(false);
    }
  }

  const atual = lista?.find((s) => s.id === selecionada) ?? null;

  return (
    <section className="sugestoes" aria-label="Sugestões da IA local">
      <h3>Sugestões da IA local</h3>
      <p className="discreto">
        A IA local só propõe: nada é publicado por ela. O texto das conversas vai ao modelo sem dados pessoais (e-mail, CPF, telefone...), e você revisa, edita e decide cada sugestão. Aceitar leva ao
        rascunho, com a resposta direta desligada.
      </p>

      <div className="acoes">
        <button type="button" className="btn btn-pequeno" disabled={pedindo || lacunaSelecionada === null} onClick={() => void pedir(() => api.gerarSugestaoDaLacuna(lacunaSelecionada!))}>
          Sugerir para a lacuna selecionada
        </button>
        <button type="button" className="btn btn-pequeno" disabled={pedindo} onClick={() => void pedir(() => api.agruparLacunasComIa())}>
          Agrupar lacunas parecidas
        </button>
        <button type="button" className="btn btn-pequeno" disabled={pedindo} onClick={() => void pedir(() => api.analisarFaqComIa())}>
          Procurar duplicatas e contradições
        </button>
      </div>
      {pedindo && <p className="discreto">A IA local está trabalhando...</p>}
      {erro && (
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      )}
      {aviso && (
        <p className="aviso" role="status">
          {aviso}
        </p>
      )}

      <label className="filtro-fila">
        Situação
        <select value={filtro} onChange={(e) => setFiltro(e.target.value as Filtro)}>
          <option value="Pending">Pendentes</option>
          <option value="Approved">Aceitas</option>
          <option value="Rejected">Rejeitadas</option>
          <option value="Todas">Todas</option>
        </select>
      </label>

      <div className="lista-itens">
        {lista === null && !erro && <p className="discreto centro">Carregando...</p>}
        {lista !== null && lista.length === 0 && <p className="discreto centro">Nenhuma sugestão nesta situação.</p>}
        {lista !== null && lista.length > 0 && (
          <ul>
            {lista.map((s) => (
              <li key={s.id}>
                <button type="button" className={s.id === selecionada ? 'item ativo' : 'item'} aria-current={s.id === selecionada ? 'true' : undefined} onClick={() => setSelecionada(s.id)}>
                  <span className="item-nome">{ROTULO_TIPO_DA_SUGESTAO[s.kind]}</span>
                  <span className="item-meta">{s.payload.question ?? s.payload.targetQuestion ?? ''}</span>
                  <span className="item-meta">
                    {ROTULO_SITUACAO_DA_SUGESTAO[s.status]} · {dataHora(s.createdAt)}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
        {lista !== null && total > lista.length && <p className="discreto">Mostrando {lista.length} de {total}.</p>}
      </div>

      {atual && (
        <RevisaoDaSugestao
          key={`${atual.id}:${atual.revision}`}
          api={api}
          sugestao={atual}
          onDecidida={(resultado, rascunhoMudou) => {
            setAviso(resultado);
            setRecarga((r) => r + 1);
            onLacunasAlteradas();
            if (rascunhoMudou) {
              onRascunhoAlterado();
            }
          }}
          onMudou={() => setRecarga((r) => r + 1)}
        />
      )}
    </section>
  );
}

interface RevisaoProps {
  api: ApiClient;
  sugestao: FaqSuggestion;
  onDecidida: (mensagem: string, rascunhoMudou: boolean) => void;
  onMudou: () => void;
}

function RevisaoDaSugestao({ api, sugestao, onDecidida, onMudou }: RevisaoProps) {
  const [pergunta, setPergunta] = useState(sugestao.payload.question ?? '');
  const [resposta, setResposta] = useState(sugestao.payload.answer ?? '');
  const [aliases, setAliases] = useState((sugestao.payload.aliases ?? []).join('\n'));
  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const emAndamento = useRef(false);
  const pendente = sugestao.status === 'Pending';
  const achado = ehAchado(sugestao);
  const listaDeAliases = aliasesDoTexto(aliases);
  const precisaDeResposta = sugestao.kind === 'NewFaq';
  const valido =
    pergunta.length <= LIMITE_PERGUNTA && resposta.length <= LIMITE_RESPOSTA && listaDeAliases.length <= LIMITE_ALIASES && (!precisaDeResposta || (pergunta.trim() !== '' && resposta.trim() !== ''));

  async function executar(tarefa: () => Promise<string>, rascunhoMudou = false) {
    if (emAndamento.current) {
      return;
    }
    emAndamento.current = true;
    setOcupado(true);
    setErro(null);
    try {
      onDecidida(await tarefa(), rascunhoMudou);
    } catch (falha) {
      setErro(mensagemDeErroDaIa(falha));
      if (falha instanceof ApiError && falha.status === 409) {
        onMudou();
      }
    } finally {
      emAndamento.current = false;
      setOcupado(false);
    }
  }

  const editada = pergunta !== (sugestao.payload.question ?? '') || resposta !== (sugestao.payload.answer ?? '') || aliases !== (sugestao.payload.aliases ?? []).join('\n');

  async function salvarEdicao() {
    await executar(async () => {
      await api.editarSugestaoDaIa(sugestao.id, { question: pergunta, answer: resposta, aliases: listaDeAliases });
      return 'Edição salva.';
    });
  }

  async function aceitar() {
    await executar(async () => {
      // O que o administrador vê é o que é aceito: a edição pendente é salva antes.
      if (editada && !achado) {
        await api.editarSugestaoDaIa(sugestao.id, { question: pergunta, answer: resposta, aliases: listaDeAliases });
      }
      const aceita = await api.aceitarSugestaoDaIa(sugestao.id);
      return achado
        ? 'Achado reconhecido. O FAQ não foi alterado: ajuste as entradas no rascunho, se quiser.'
        : `Enviada ao rascunho (versão ${aceita.appliedDraftVersion ?? '?'}). Nada foi publicado.`;
    }, !achado);
  }

  async function rejeitar() {
    await executar(async () => {
      await api.rejeitarSugestaoDaIa(sugestao.id);
      return 'Sugestão rejeitada.';
    });
  }

  return (
    <section className="revisao" aria-label="Revisão da sugestão">
      <h3>{ROTULO_TIPO_DA_SUGESTAO[sugestao.kind]}</h3>
      {sugestao.payload.rationale && <p className="discreto">Motivo apontado pela IA: {sugestao.payload.rationale}</p>}
      <p className="discreto">
        Gerada por {sugestao.modelId} em {dataHora(sugestao.createdAt)}. {sugestao.edits > 0 ? `Editada ${sugestao.edits} vez(es). ` : ''}
        {sugestao.status !== 'Pending' ? `${ROTULO_SITUACAO_DA_SUGESTAO[sugestao.status]} em ${dataHora(sugestao.decidedAt)}.` : ''}
      </p>

      {achado ? (
        <div className="revisao-formulario">
          <p>
            <strong>Entrada A:</strong> {sugestao.payload.targetQuestion}
          </p>
          <p>
            <strong>Entrada B:</strong> {sugestao.payload.otherQuestion}
          </p>
        </div>
      ) : (
        <div className="revisao-formulario">
          {sugestao.kind === 'Alias' && (
            <p>
              <strong>Entrada que recebe os aliases:</strong> {sugestao.payload.targetQuestion}
            </p>
          )}
          {sugestao.kind === 'NewFaq' && (
            <div className="campo">
              <label htmlFor="sugestao-pergunta">Pergunta</label>
              <input id="sugestao-pergunta" type="text" value={pergunta} maxLength={LIMITE_PERGUNTA} disabled={!pendente} onChange={(e) => setPergunta(e.target.value)} />
            </div>
          )}
          {sugestao.kind === 'NewFaq' && (
            <div className="campo">
              <label htmlFor="sugestao-resposta">Resposta aprovada (escrita ou conferida por você)</label>
              <textarea id="sugestao-resposta" rows={4} value={resposta} maxLength={LIMITE_RESPOSTA} disabled={!pendente} onChange={(e) => setResposta(e.target.value)} />
              {resposta.trim() === '' && pendente && <p className="discreto">A IA não escreve respostas por conta própria: escreva a resposta antes de aceitar.</p>}
            </div>
          )}
          <div className="campo">
            <label htmlFor="sugestao-aliases">Outras formas de perguntar (uma por linha)</label>
            <textarea id="sugestao-aliases" rows={4} value={aliases} disabled={!pendente} onChange={(e) => setAliases(e.target.value)} />
          </div>
        </div>
      )}

      {erro && (
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      )}

      {pendente && (
        <div className="acoes">
          {!achado && (
            <button type="button" className="btn" disabled={ocupado || !editada || !valido} onClick={() => void salvarEdicao()}>
              Salvar edição
            </button>
          )}
          <button type="button" className="btn btn-primario" disabled={ocupado || (!achado && !valido)} onClick={() => void aceitar()}>
            {achado ? 'Reconhecer' : 'Aceitar e enviar ao rascunho'}
          </button>
          <button type="button" className="btn" disabled={ocupado} onClick={() => void rejeitar()}>
            {achado ? 'Descartar' : 'Rejeitar'}
          </button>
        </div>
      )}
    </section>
  );
}
