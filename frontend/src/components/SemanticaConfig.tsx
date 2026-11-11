import { useEffect, useState } from 'react';
import type { ApiClient } from '../api/client';
import type { SemanticAnswerMode, SemanticMetrics, SemanticSettings } from '../api/types';
import { ROTULO_MODO_SEMANTICO, mensagemDeErroDaIa, percentual } from '../conhecimento';

interface Props {
  api: ApiClient;
}

const MODOS: SemanticAnswerMode[] = ['Off', 'Evaluate', 'Active'];

const JANELAS = [
  { dias: 7, rotulo: 'Últimos 7 dias' },
  { dias: 30, rotulo: 'Últimos 30 dias' },
];

function ms(valor: number | null): string {
  return valor === null ? '—' : `${Math.round(valor).toLocaleString('pt-BR')} ms`;
}

/**
 * A configuração da resposta direta por similaridade e as métricas do tenant (ADR 0034). O modo é o interruptor de emergência: "Desligado" vale no próximo turno, sem publicar nada.
 * O modo ativo exige uma confirmação consciente. Os limiares iniciais são conservadores e ainda não calibrados com o modelo real: a avaliação serve para medir antes de ativar.
 */
export function SemanticaConfig({ api }: Props) {
  const [atual, setAtual] = useState<SemanticSettings | null>(null);
  const [modo, setModo] = useState<SemanticAnswerMode>('Off');
  const [minScore, setMinScore] = useState('0.90');
  const [minMargin, setMinMargin] = useState('0.05');
  const [confirmouAtivo, setConfirmouAtivo] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [aviso, setAviso] = useState<string | null>(null);
  const [salvando, setSalvando] = useState(false);
  const [dias, setDias] = useState(7);
  const [metricas, setMetricas] = useState<SemanticMetrics | null>(null);
  const [erroDasMetricas, setErroDasMetricas] = useState<string | null>(null);
  const [recarga, setRecarga] = useState(0);

  useEffect(() => {
    let vivo = true;
    api
      .obterConfiguracaoSemantica()
      .then((lida) => {
        if (!vivo) {
          return;
        }
        setAtual(lida);
        setModo(lida.mode);
        setMinScore(String(lida.minScore));
        setMinMargin(String(lida.minMargin));
        setConfirmouAtivo(false);
        setErro(null);
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErro(mensagemDeErroDaIa(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, recarga]);

  useEffect(() => {
    let vivo = true;
    setMetricas(null);
    api
      .obterMetricasSemanticas(dias)
      .then((lidas) => {
        if (vivo) {
          setMetricas(lidas);
          setErroDasMetricas(null);
        }
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErroDasMetricas(mensagemDeErroDaIa(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, dias, recarga]);

  const score = Number(minScore.replace(',', '.'));
  const margem = Number(minMargin.replace(',', '.'));
  const limiaresValidos = Number.isFinite(score) && score >= 0.6 && score <= 1 && Number.isFinite(margem) && margem >= 0 && margem <= 0.5;
  const ativandoAgora = modo === 'Active' && atual?.mode !== 'Active';
  const podeSalvar = atual !== null && limiaresValidos && !salvando && (!ativandoAgora || confirmouAtivo);

  async function salvar() {
    if (!podeSalvar) {
      return;
    }
    setSalvando(true);
    setErro(null);
    setAviso(null);
    try {
      const salva = await api.salvarConfiguracaoSemantica({ mode: modo, minScore: score, minMargin: margem });
      setAtual(salva);
      setConfirmouAtivo(false);
      setAviso(salva.mode === 'Off' ? 'Busca semântica desligada: vale já no próximo atendimento.' : `Configuração salva (${ROTULO_MODO_SEMANTICO[salva.mode]}).`);
      setRecarga((r) => r + 1);
    } catch (falha) {
      setErro(mensagemDeErroDaIa(falha));
    } finally {
      setSalvando(false);
    }
  }

  return (
    <section className="semantica" aria-label="Busca semântica">
      <h3>Busca semântica</h3>
      <p className="discreto">
        Quando a pergunta do cliente não é igual a nenhuma do FAQ, a busca semântica procura a entrada aprovada mais parecida. Só responde direto se a entrada tiver resposta direta ligada, a
        similaridade passar do limiar, nenhuma outra entrada ficar perto e a mensagem for simples (uma pergunta, sem negação nem números diferentes). A resposta é sempre o texto aprovado.
      </p>

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

      {atual && (
        <div className="revisao-formulario">
          {atual.isDefault && <p className="discreto">Esta empresa ainda não configurou a busca semântica: ela está desligada.</p>}
          <div className="campo">
            <label htmlFor="modo-semantico">Modo</label>
            <select id="modo-semantico" value={modo} onChange={(e) => setModo(e.target.value as SemanticAnswerMode)}>
              {MODOS.map((m) => (
                <option key={m} value={m}>
                  {ROTULO_MODO_SEMANTICO[m]}
                </option>
              ))}
            </select>
          </div>
          <div className="campo">
            <label htmlFor="limiar-semantico">Limiar de similaridade (0,60 a 1)</label>
            <input id="limiar-semantico" type="text" inputMode="decimal" value={minScore} onChange={(e) => setMinScore(e.target.value)} aria-invalid={!limiaresValidos} />
          </div>
          <div className="campo">
            <label htmlFor="margem-semantica">Margem para a segunda entrada (0 a 0,5)</label>
            <input id="margem-semantica" type="text" inputMode="decimal" value={minMargin} onChange={(e) => setMinMargin(e.target.value)} aria-invalid={!limiaresValidos} />
          </div>
          {!limiaresValidos && <p className="erro-campo">Informe o limiar entre 0,60 e 1 e a margem entre 0 e 0,5.</p>}
          {modo === 'Active' && (
            <label className="caixa">
              <input type="checkbox" checked={confirmouAtivo} onChange={(e) => setConfirmouAtivo(e.target.checked)} />
              Entendo que, no modo ativo, o cliente recebe a resposta direta sem passar pelo modelo. Já medi o desempenho no modo de avaliação.
            </label>
          )}
          <div className="acoes">
            <button type="button" className="btn btn-primario" disabled={!podeSalvar} onClick={() => void salvar()}>
              {salvando ? 'Salvando...' : 'Salvar configuração'}
            </button>
          </div>
        </div>
      )}

      <h3>Métricas da empresa</h3>
      <label className="filtro-fila">
        Período
        <select value={dias} onChange={(e) => setDias(Number(e.target.value))}>
          {JANELAS.map((j) => (
            <option key={j.dias} value={j.dias}>
              {j.rotulo}
            </option>
          ))}
        </select>
      </label>
      {erroDasMetricas && (
        <p className="erro-campo" role="alert">
          {erroDasMetricas}
        </p>
      )}
      {metricas === null && !erroDasMetricas && <p className="discreto">Carregando métricas...</p>}
      {metricas && (
        <dl className="metricas">
          <dt>Chamadas ao modelo comercial evitadas (contagem exata)</dt>
          <dd>{metricas.commercialCallsAvoided}</dd>
          <dt>Respondidas direto pelo FAQ</dt>
          <dd>{metricas.faqHits}</dd>
          <dt>Respondidas direto pela busca semântica</dt>
          <dd>
            {metricas.semanticHits} ({percentual(metricas.semanticShareOfAiReplies)} das respostas da IA)
          </dd>
          <dt>Respondidas pelo modelo</dt>
          <dd>{metricas.modelReplies}</dd>
          <dt>Transferidas para um atendente</dt>
          <dd>{metricas.handoffs}</dd>
          <dt>Decisões semânticas</dt>
          <dd>
            {metricas.decisions}: {metricas.answered} responderam, {metricas.wouldAnswer} teriam respondido (avaliação), {metricas.noCandidates} sem candidato, {metricas.belowThreshold} abaixo do
            limiar, {metricas.ambiguous} ambíguas, {metricas.notAutoReply} sem resposta direta, {metricas.guardRejected} recusadas por segurança, {metricas.stale} de versão antiga,{' '}
            {metricas.unavailable} com a busca indisponível
          </dd>
          <dt>Tempo da etapa semântica</dt>
          <dd>
            média {ms(metricas.latencyAverageMilliseconds)}, p95 {ms(metricas.latencyP95Milliseconds)}
          </dd>
        </dl>
      )}
      <p className="discreto">
        Tokens e custo evitados são estimativas e aparecem no relatório operacional (comando status), com a ressalva de que o custo da IA local não é descontado. Aqui só há contagens medidas.
      </p>
    </section>
  );
}
