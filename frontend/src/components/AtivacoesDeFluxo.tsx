import { useCallback, useEffect, useState } from 'react';
import { ApiError, descreverErro, type ApiClient } from '../api/client';
import type { CurrentUser, FlowActivation, FlowActivationRequest, FlowOnFailure, WorkflowSummary } from '../api/types';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const ROTULO_FALHA: Record<FlowOnFailure, string> = {
  Handoff: 'Transferir para um atendente',
  Agent: 'Devolver ao agente de IA',
};

function organizacoesDe(me: CurrentUser, fluxos: WorkflowSummary[]): string[] {
  return [...new Set([...me.grants.map((g) => g.organizationId).filter((o): o is string => o !== null), ...fluxos.map((f) => f.organizationId)])];
}

function mensagemDeErro(erro: unknown): string {
  if (erro instanceof ApiError) {
    if (erro.status === 403) {
      return 'Você não tem permissão para ativar fluxos. Peça a um administrador da empresa.';
    }
    if (erro.status === 409 && erro.code === 'no-published-version') {
      return 'Este fluxo ainda não tem uma versão publicada nesta organização: publique antes de ativar.';
    }
    if (erro.status === 409 && erro.code === 'changed') {
      return 'Esta ativação foi alterada por outra pessoa. Recarregue a lista e tente de novo.';
    }
    if (erro.status === 400 && erro.problemas.length > 0) {
      return erro.problemas.join(' ');
    }
  }
  return descreverErro(erro);
}

interface Props {
  api: ApiClient;
  me: CurrentUser;
  fluxos: WorkflowSummary[];
}

/**
 * As ativações (ADR 0038 §2): em que organização, unidade e canal um fluxo PUBLICADO passa a atender, com porcentagem determinística opcional e a política de falha. Cada ativação nasce DESLIGADA; ligar
 * exige uma versão publicada. Uma sessão em andamento nunca muda de dono: só as conversas NOVAS são escolhidas. Com o chatbot visual desligado no ambiente, nada disto tem efeito.
 */
export function AtivacoesDeFluxo({ api, me, fluxos }: Props) {
  const [ativacoes, setAtivacoes] = useState<FlowActivation[] | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [aviso, setAviso] = useState<string | null>(null);
  const [criando, setCriando] = useState(false);
  const [editando, setEditando] = useState<FlowActivation | null>(null);
  const organizacoes = organizacoesDe(me, fluxos);
  const chavesPublicadas = [...new Set(fluxos.filter((f) => f.status === 'Published').map((f) => f.key))];

  const carregar = useCallback(async () => {
    try {
      setAtivacoes(await api.listarAtivacoesDeFluxo());
      setErro(null);
    } catch (e) {
      setErro(mensagemDeErro(e));
    }
  }, [api]);

  useEffect(() => {
    void carregar();
  }, [carregar]);

  async function alternar(a: FlowActivation) {
    setErro(null);
    setAviso(null);
    try {
      await (a.enabled ? api.desligarAtivacaoDeFluxo(a.id) : api.ligarAtivacaoDeFluxo(a.id));
      setAviso(a.enabled ? 'Ativação desligada: as conversas novas voltam ao agente de IA.' : 'Ativação ligada: as conversas novas que casarem com ela entram no fluxo.');
      await carregar();
    } catch (e) {
      setErro(mensagemDeErro(e));
    }
  }

  return (
    <section className="fluxo-ativacoes" aria-label="Ativações de fluxos">
      <header className="fluxos-topo">
        <h3>Ativações</h3>
        <button type="button" className="btn btn-primario" aria-expanded={criando} onClick={() => { setCriando((v) => !v); setEditando(null); }}>
          Nova ativação
        </button>
      </header>
      <p className="discreto">
        Uma ativação diz onde um fluxo publicado atende. O mais específico vence (unidade e canal); em empate vale a maior prioridade. Só conversas NOVAS entram: uma conversa em andamento nunca troca de dono.
      </p>

      {(criando || editando) && (
        <FormularioDeAtivacao
          key={editando?.id ?? 'nova'}
          api={api}
          organizacoes={organizacoes}
          chaves={chavesPublicadas}
          ativacao={editando}
          onFeito={async (mensagem) => {
            setCriando(false);
            setEditando(null);
            setAviso(mensagem);
            await carregar();
          }}
          onCancelar={() => {
            setCriando(false);
            setEditando(null);
          }}
        />
      )}

      {aviso && <p role="status" className="fluxo-mensagem">{aviso}</p>}
      {erro && <p role="alert" className="erro">{erro}</p>}
      {ativacoes === null && !erro && <p className="discreto">Carregando...</p>}
      {ativacoes !== null && ativacoes.length === 0 && <p className="discreto">Nenhuma ativação. Sem ativação, todas as conversas são do agente de IA.</p>}

      {ativacoes !== null && ativacoes.length > 0 && (
        <table className="tabela-fluxos">
          <caption className="sr-only">Ativações de fluxos</caption>
          <thead>
            <tr>
              <th scope="col">Fluxo</th>
              <th scope="col">Alcance</th>
              <th scope="col">Parcela</th>
              <th scope="col">Se falhar</th>
              <th scope="col">Situação</th>
              <th scope="col">Ações</th>
            </tr>
          </thead>
          <tbody>
            {ativacoes.map((a) => (
              <tr key={a.id}>
                <td>
                  <code>{a.workflowKey}</code>
                </td>
                <td>
                  Org. {a.organizationId.slice(0, 8)}
                  {a.businessUnitId ? ` · unidade ${a.businessUnitId.slice(0, 8)}` : ''}
                  {a.channelInstanceId ? ` · canal ${a.channelInstanceId.slice(0, 8)}` : ''}
                  {` · prioridade ${a.priority}`}
                </td>
                <td>{a.rolloutPercent}%</td>
                <td>{ROTULO_FALHA[a.onFailure]}</td>
                <td>{a.enabled ? 'Ligada' : 'Desligada'}</td>
                <td>
                  <button type="button" className="btn" aria-label={`${a.enabled ? 'Desligar' : 'Ligar'} a ativação de ${a.workflowKey}`} onClick={() => void alternar(a)}>
                    {a.enabled ? 'Desligar' : 'Ligar'}
                  </button>{' '}
                  <button type="button" className="btn" aria-label={`Editar a ativação de ${a.workflowKey}`} onClick={() => { setEditando(a); setCriando(false); }}>
                    Editar
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function FormularioDeAtivacao({
  api,
  organizacoes,
  chaves,
  ativacao,
  onFeito,
  onCancelar,
}: {
  api: ApiClient;
  organizacoes: string[];
  chaves: string[];
  ativacao: FlowActivation | null;
  onFeito: (mensagem: string) => Promise<void>;
  onCancelar: () => void;
}) {
  const [organizacao, setOrganizacao] = useState(ativacao?.organizationId ?? organizacoes[0] ?? '');
  const [chave, setChave] = useState(ativacao?.workflowKey ?? chaves[0] ?? '');
  const [unidade, setUnidade] = useState(ativacao?.businessUnitId ?? '');
  const [canal, setCanal] = useState(ativacao?.channelInstanceId ?? '');
  const [prioridade, setPrioridade] = useState(String(ativacao?.priority ?? 0));
  const [parcela, setParcela] = useState(String(ativacao?.rolloutPercent ?? 100));
  const [falha, setFalha] = useState<FlowOnFailure>(ativacao?.onFailure ?? 'Handoff');
  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);

  const prioridadeNumero = Number(prioridade);
  const parcelaNumero = Number(parcela);
  const valido =
    GUID.test(organizacao) &&
    chave !== '' &&
    (unidade === '' || GUID.test(unidade)) &&
    (canal === '' || GUID.test(canal)) &&
    Number.isInteger(prioridadeNumero) &&
    Number.isInteger(parcelaNumero) &&
    parcelaNumero >= 0 &&
    parcelaNumero <= 100;

  async function enviar(e: React.FormEvent) {
    e.preventDefault();
    if (!valido) {
      return;
    }
    setOcupado(true);
    setErro(null);
    const pedido: FlowActivationRequest = {
      organizationId: organizacao,
      businessUnitId: unidade === '' ? null : unidade,
      channelInstanceId: canal === '' ? null : canal,
      workflowKey: chave,
      priority: prioridadeNumero,
      rolloutPercent: parcelaNumero,
      onFailure: falha,
      expectedRevision: ativacao?.revision ?? null,
    };
    try {
      await (ativacao ? api.atualizarAtivacaoDeFluxo(ativacao.id, pedido) : api.criarAtivacaoDeFluxo(pedido));
      await onFeito(ativacao ? 'Ativação atualizada.' : 'Ativação criada, DESLIGADA. Ligue quando quiser que as conversas novas entrem no fluxo.');
    } catch (e2) {
      setErro(mensagemDeErro(e2));
    } finally {
      setOcupado(false);
    }
  }

  return (
    <form className="fluxo-novo" aria-label={ativacao ? 'Editar ativação' : 'Nova ativação'} onSubmit={(e) => void enviar(e)}>
      <label htmlFor="ativ-chave">Fluxo publicado</label>
      <select id="ativ-chave" value={chave} onChange={(e) => setChave(e.target.value)}>
        {chaves.length === 0 && <option value="">Nenhum fluxo publicado</option>}
        {chaves.map((c) => (
          <option key={c} value={c}>
            {c}
          </option>
        ))}
        {chave !== '' && !chaves.includes(chave) && <option value={chave}>{chave}</option>}
      </select>
      <label htmlFor="ativ-org">Organização</label>
      {organizacoes.length > 0 ? (
        <select id="ativ-org" value={organizacao} onChange={(e) => setOrganizacao(e.target.value)}>
          {organizacoes.map((o) => (
            <option key={o} value={o}>
              Organização {o.slice(0, 8)}
            </option>
          ))}
        </select>
      ) : (
        <input id="ativ-org" placeholder="identificador da organização" value={organizacao} onChange={(e) => setOrganizacao(e.target.value.trim())} />
      )}
      <label htmlFor="ativ-unidade">Unidade (vazio = todas)</label>
      <input id="ativ-unidade" placeholder="identificador da unidade" value={unidade} onChange={(e) => setUnidade(e.target.value.trim())} />
      <label htmlFor="ativ-canal">Canal / número (vazio = todos)</label>
      <input id="ativ-canal" placeholder="identificador do número" value={canal} onChange={(e) => setCanal(e.target.value.trim())} />
      <label htmlFor="ativ-prioridade">Prioridade (maior vence o empate)</label>
      <input id="ativ-prioridade" type="number" value={prioridade} onChange={(e) => setPrioridade(e.target.value)} />
      <label htmlFor="ativ-parcela">Parcela das conversas novas, em % (0 a 100; o mesmo cliente cai sempre do mesmo lado)</label>
      <input id="ativ-parcela" type="number" min={0} max={100} value={parcela} onChange={(e) => setParcela(e.target.value)} />
      <label htmlFor="ativ-falha">Se o fluxo falhar</label>
      <select id="ativ-falha" value={falha} onChange={(e) => setFalha(e.target.value as FlowOnFailure)}>
        <option value="Handoff">{ROTULO_FALHA.Handoff}</option>
        <option value="Agent">{ROTULO_FALHA.Agent}</option>
      </select>
      {erro && <p role="alert" className="erro">{erro}</p>}
      <div className="fluxo-linha">
        <button type="submit" className="btn btn-primario" disabled={!valido || ocupado}>
          {ativacao ? 'Salvar ativação' : 'Criar ativação'}
        </button>
        <button type="button" className="btn" onClick={onCancelar}>
          Cancelar
        </button>
      </div>
    </form>
  );
}
