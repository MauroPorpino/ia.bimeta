import { useEffect, useState } from 'react';
import { type ApiClient } from '../api/client';
import type { IntegrationConnection, IntegrationConnectionStats, IntegrationExecution, IntegrationExecutionPage, IntegrationExecutionStatus, IntegrationResult } from '../api/types';
import { dataHora } from '../conhecimento';
import { ROTULO_ORIGEM, ROTULO_STATUS_DA_EXECUCAO, mensagemDeErroDaIntegracao, rotuloDoCodigo } from '../integracao';

interface Props {
  api: ApiClient;
}

type FiltroDeStatus = IntegrationExecutionStatus | 'Todas';
type FiltroDeTipo = 'todas' | 'producao' | 'testes';

const POR_PAGINA = 20;
const STATUS: FiltroDeStatus[] = ['Todas', 'Succeeded', 'Failed', 'Unknown', 'AwaitingConfirmation', 'Rejected', 'Cancelled', 'InProgress'];

function resultadoEmFrase(r: IntegrationResult): string {
  if (r.status === 'Succeeded') {
    return 'Executada com sucesso.';
  }
  if (r.status === 'Cancelled') {
    return 'Execução cancelada: nada foi enviado ao sistema externo.';
  }
  if (r.status === 'Unknown') {
    return 'O resultado é ambíguo: confira no sistema de origem antes de repetir. A plataforma nunca reenvia sozinha.';
  }
  return `Não concluiu: ${rotuloDoCodigo(r.error?.code)}`;
}

/**
 * O histórico de execuções do motor de integração (ADR 0035): quem pediu, o quê, quando, o resultado e a duração. O registro NÃO guarda parâmetros nem resposta, então a tela também não os mostra. As escritas
 * que esperam confirmação aparecem em destaque: só uma pessoa com permissão de escrita confirma ou cancela, e a confirmação vence em poucos minutos.
 */
export function HistoricoDeIntegracao({ api }: Props) {
  const [status, setStatus] = useState<FiltroDeStatus>('Todas');
  const [tipo, setTipo] = useState<FiltroDeTipo>('todas');
  const [conexaoId, setConexaoId] = useState('');
  const [deslocamento, setDeslocamento] = useState(0);
  const [pagina, setPagina] = useState<IntegrationExecutionPage | null>(null);
  const [pendentes, setPendentes] = useState<IntegrationExecution[]>([]);
  const [resumo, setResumo] = useState<IntegrationConnectionStats[]>([]);
  const [conexoes, setConexoes] = useState<IntegrationConnection[]>([]);
  const [erro, setErro] = useState<string | null>(null);
  // O erro de uma AÇÃO (confirmar, cancelar) é separado do erro da LISTA: recarregar a lista com sucesso não pode apagar o aviso de que a confirmação foi negada.
  const [erroDaAcao, setErroDaAcao] = useState<string | null>(null);
  const [mensagem, setMensagem] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState<string | null>(null);
  const [recarga, setRecarga] = useState(0);

  useEffect(() => {
    let vivo = true;
    api
      .listarExecucoes({
        status: status === 'Todas' ? undefined : status,
        isTest: tipo === 'todas' ? undefined : tipo === 'testes',
        connectionId: conexaoId === '' ? undefined : conexaoId,
        take: POR_PAGINA,
        skip: deslocamento,
      })
      .then((lida) => {
        if (vivo) {
          setPagina(lida);
          setErro(null);
        }
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErro(mensagemDeErroDaIntegracao(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, status, tipo, conexaoId, deslocamento, recarga]);

  useEffect(() => {
    let vivo = true;
    Promise.all([api.listarExecucoes({ status: 'AwaitingConfirmation', take: 50 }), api.resumoDasExecucoes(7), api.listarConexoes()])
      .then(([aguardando, stats, cons]) => {
        if (vivo) {
          setPendentes(aguardando.items);
          setResumo(stats);
          setConexoes(cons);
        }
      })
      .catch(() => undefined);
    return () => {
      vivo = false;
    };
  }, [api, recarga]);

  const nomeDaConexao = (id: string) => conexoes.find((c) => c.id === id)?.name ?? id.slice(0, 8);

  async function decidir(id: string, acao: 'confirmar' | 'cancelar') {
    setOcupado(id);
    setErroDaAcao(null);
    setMensagem(null);
    try {
      const r = acao === 'confirmar' ? await api.confirmarExecucao(id) : await api.cancelarExecucao(id);
      setMensagem(resultadoEmFrase(r));
    } catch (falha) {
      setErroDaAcao(mensagemDeErroDaIntegracao(falha));
    } finally {
      setOcupado(null);
      setRecarga((r) => r + 1);
    }
  }

  const itens = pagina?.items ?? null;
  const total = pagina?.total ?? 0;

  return (
    <div className="integracoes-historico" aria-label="Histórico de execuções" role="region">
      <h2>Execuções</h2>
      <p className="discreto">Cada chamada ao sistema externo fica registrada com quem pediu, a operação, o resultado e a duração. Parâmetros e respostas não são guardados.</p>

      {erro && (
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      )}
      {erroDaAcao && (
        <p className="erro-campo" role="alert">
          {erroDaAcao}
        </p>
      )}
      {mensagem && (
        <p className="aviso" role="status">
          {mensagem}
        </p>
      )}

      {resumo.length > 0 && (
        <div role="group" aria-label="Resumo dos últimos 7 dias">
          <h3>Últimos 7 dias</h3>
          <ul className="lista-itens">
            {resumo.map((s) => (
              <li key={s.connectionId} className="item-meta">
                {nomeDaConexao(s.connectionId)}: {s.total} execuções, {s.failed} com falha, {s.unknown} ambíguas{s.lastAt ? `, última em ${dataHora(s.lastAt)}` : ''}
              </li>
            ))}
          </ul>
        </div>
      )}

      {pendentes.length > 0 && (
        <div role="group" aria-label="Aguardando confirmação" className="revisao">
          <h3>Aguardando confirmação</h3>
          <p className="discreto">Nada foi enviado ao sistema externo. Confirmar executa a operação uma única vez; cancelar a descarta.</p>
          <ul className="lista-itens">
            {pendentes.map((p) => (
              <li key={p.id} className="pendente">
                <span>
                  <strong>{p.definitionKey}</strong> (v{p.definitionVersion}) em {nomeDaConexao(p.connectionId)}, pedida em {dataHora(p.createdAt)} por {p.caller.map((c) => ROTULO_ORIGEM[c]).join(', ')}
                  {p.confirmationExpiresAt ? `; vence em ${dataHora(p.confirmationExpiresAt)}` : ''}
                </span>
                <span className="acoes">
                  <button type="button" className="btn btn-pequeno btn-primario" disabled={ocupado === p.id} onClick={() => void decidir(p.id, 'confirmar')}>
                    Confirmar {p.definitionKey}
                  </button>
                  <button type="button" className="btn btn-pequeno btn-perigo" disabled={ocupado === p.id} onClick={() => void decidir(p.id, 'cancelar')}>
                    Cancelar {p.definitionKey}
                  </button>
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="acoes filtros">
        <label className="filtro-fila">
          Situação
          <select
            value={status}
            onChange={(e) => {
              setPagina(null);
              setDeslocamento(0);
              setStatus(e.target.value as FiltroDeStatus);
            }}
          >
            {STATUS.map((s) => (
              <option key={s} value={s}>
                {s === 'Todas' ? 'Todas' : ROTULO_STATUS_DA_EXECUCAO[s]}
              </option>
            ))}
          </select>
        </label>
        <label className="filtro-fila">
          Tipo
          <select
            value={tipo}
            onChange={(e) => {
              setPagina(null);
              setDeslocamento(0);
              setTipo(e.target.value as FiltroDeTipo);
            }}
          >
            <option value="todas">Todas</option>
            <option value="producao">Execuções reais</option>
            <option value="testes">Testes</option>
          </select>
        </label>
        <label className="filtro-fila">
          Conexão
          <select
            value={conexaoId}
            onChange={(e) => {
              setPagina(null);
              setDeslocamento(0);
              setConexaoId(e.target.value);
            }}
          >
            <option value="">Todas</option>
            {conexoes.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <button type="button" className="btn" onClick={() => setRecarga((r) => r + 1)}>
          Atualizar
        </button>
      </div>

      {itens === null && !erro && <p className="discreto">Carregando...</p>}
      {itens?.length === 0 && <p className="discreto">Nenhuma execução neste filtro.</p>}
      {itens && itens.length > 0 && (
        <table className="tabela-execucoes">
          <thead>
            <tr>
              <th scope="col">Quando</th>
              <th scope="col">Operação</th>
              <th scope="col">Origem</th>
              <th scope="col">Situação</th>
              <th scope="col">Detalhe</th>
              <th scope="col">Duração</th>
            </tr>
          </thead>
          <tbody>
            {itens.map((e) => (
              <tr key={e.id}>
                <td>{dataHora(e.createdAt)}</td>
                <td>
                  {e.definitionKey} (v{e.definitionVersion}){e.isTest ? ' · teste' : ''}
                </td>
                <td>{e.caller.map((c) => ROTULO_ORIGEM[c]).join(', ')}</td>
                <td>{ROTULO_STATUS_DA_EXECUCAO[e.status]}</td>
                <td>
                  {rotuloDoCodigo(e.errorCode)}
                  {e.upstreamStatus ? ` (HTTP ${e.upstreamStatus})` : ''}
                  {e.attempts > 1 ? ` · ${e.attempts} tentativas` : ''}
                </td>
                <td>{e.durationMs === null ? '—' : `${e.durationMs} ms`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {total > POR_PAGINA && (
        <div className="paginacao">
          <span className="discreto">
            {deslocamento + 1} a {Math.min(deslocamento + POR_PAGINA, total)} de {total}
          </span>
          <div className="acoes">
            <button type="button" className="btn btn-pequeno" disabled={deslocamento === 0} onClick={() => setDeslocamento(Math.max(0, deslocamento - POR_PAGINA))}>
              Anteriores
            </button>
            <button type="button" className="btn btn-pequeno" disabled={deslocamento + POR_PAGINA >= total} onClick={() => setDeslocamento(deslocamento + POR_PAGINA)}>
              Próximas
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
