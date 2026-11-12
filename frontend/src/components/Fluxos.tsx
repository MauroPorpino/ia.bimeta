import { useCallback, useEffect, useState } from 'react';
import { descreverErro, type ApiClient } from '../api/client';
import type { CurrentUser, WorkflowSummary } from '../api/types';
import { EditorDeFluxo, type FluxoEmEdicao } from './EditorDeFluxo';

const ROTULO_DO_ESTADO: Record<WorkflowSummary['status'], string> = { Draft: 'Rascunho', Published: 'Publicada', Retired: 'Aposentada' };

const CHAVE = /^[a-z][a-z0-9_.-]{1,79}$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** As organizações às quais o usuário tem concessão. Quem só tem a concessão do tenant digita o identificador da organização (o servidor confere o alcance). */
function organizacoesDe(me: CurrentUser, fluxos: WorkflowSummary[]): string[] {
  return [...new Set([...me.grants.map((g) => g.organizationId).filter((o): o is string => o !== null), ...fluxos.map((f) => f.organizationId)])];
}

interface Props {
  api: ApiClient;
  me: CurrentUser;
  /** A área está escondida (a pessoa está em outra): continua montada para não perder o que ela estava editando. */
  oculto?: boolean;
}

/**
 * A área "Fluxos" (chatbot visual, ADR 0037): a lista dos fluxos da empresa, o histórico de versões e o editor. O servidor decide cada permissão (ler, editar, publicar e simular são distintas): quem só pode
 * ler abre os fluxos em modo de leitura e recebe a explicação do 403 nas ações. O chatbot visual nasce desligado: publicar um fluxo não o coloca em uso (a ativação vem depois, em outra tela).
 */
export function Fluxos({ api, me, oculto = false }: Props) {
  const [fluxos, setFluxos] = useState<WorkflowSummary[] | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [editando, setEditando] = useState<FluxoEmEdicao | null>(null);
  const [versoes, setVersoes] = useState<Record<string, WorkflowSummary[]>>({});
  const [criando, setCriando] = useState(false);
  const [chave, setChave] = useState('');
  const [nome, setNome] = useState('');
  const [organizacao, setOrganizacao] = useState('');
  const [ocupado, setOcupado] = useState(false);

  const carregar = useCallback(async () => {
    try {
      setFluxos(await api.listarFluxos());
      setErro(null);
    } catch (e) {
      setErro(descreverErro(e));
    }
  }, [api]);

  useEffect(() => {
    void carregar();
  }, [carregar]);

  const organizacoes = organizacoesDe(me, fluxos ?? []);

  async function abrir(resumo: WorkflowSummary) {
    setOcupado(true);
    try {
      const detalhe = await api.obterFluxo(resumo.id);
      setEditando({ existente: detalhe, organizationId: detalhe.organizationId, key: detalhe.key, name: detalhe.name });
      setErro(null);
    } catch (e) {
      setErro(descreverErro(e));
    } finally {
      setOcupado(false);
    }
  }

  async function mostrarVersoes(chaveDoFluxo: string) {
    if (versoes[chaveDoFluxo]) {
      setVersoes(({ [chaveDoFluxo]: _fechada, ...resto }) => resto);
      return;
    }
    try {
      setVersoes((atuais) => ({ ...atuais, [chaveDoFluxo]: [] }));
      const lista = await api.versoesDoFluxo(chaveDoFluxo);
      setVersoes((atuais) => ({ ...atuais, [chaveDoFluxo]: lista }));
    } catch (e) {
      setErro(descreverErro(e));
    }
  }

  async function aposentar(resumo: WorkflowSummary) {
    if (!window.confirm(`Aposentar a versão ${resumo.version} de "${resumo.name}"? Conversas em andamento continuam nela; novas conversas deixam de usá-la.`)) {
      return;
    }
    try {
      await api.aposentarFluxo(resumo.id);
      setVersoes({});
      await carregar();
    } catch (e) {
      setErro(descreverErro(e));
    }
  }

  const org = organizacao || organizacoes[0] || '';
  const podeCriar = CHAVE.test(chave) && nome.trim() !== '' && GUID.test(org);

  return (
    <main className="fluxos" hidden={oculto}>
      {editando ? (
        <EditorDeFluxo
          key={`${editando.key}-${editando.existente?.id ?? 'novo'}`}
          api={api}
          fluxo={editando}
          onFechar={() => setEditando(null)}
          onAlterado={() => {
            setVersoes({});
            void carregar();
          }}
        />
      ) : (
        <>
          <header className="fluxos-topo">
            <h2>Fluxos do chatbot</h2>
            <button type="button" className="btn btn-primario" onClick={() => setCriando((v) => !v)} aria-expanded={criando}>
              Novo fluxo
            </button>
          </header>
          <p className="discreto">
            Desenhe o atendimento automático em etapas (mensagens, perguntas, condições, consultas e transferência). Publicar congela a versão; o chatbot visual só entra em uso depois de ativado para um canal.
          </p>

          {criando && (
            <form
              className="fluxo-novo"
              aria-label="Novo fluxo"
              onSubmit={(e) => {
                e.preventDefault();
                if (podeCriar) {
                  setEditando({ existente: null, organizationId: org, key: chave, name: nome.trim() });
                  setCriando(false);
                  setChave('');
                  setNome('');
                }
              }}
            >
              <label htmlFor="novo-fluxo-nome">Nome</label>
              <input id="novo-fluxo-nome" value={nome} maxLength={100} onChange={(e) => setNome(e.target.value)} />
              <label htmlFor="novo-fluxo-chave">Chave (minúsculas, números, . _ -)</label>
              <input id="novo-fluxo-chave" value={chave} maxLength={80} onChange={(e) => setChave(e.target.value.toLowerCase())} />
              <label htmlFor="novo-fluxo-org">Organização</label>
              {organizacoes.length > 0 ? (
                <select id="novo-fluxo-org" value={org} onChange={(e) => setOrganizacao(e.target.value)}>
                  {organizacoes.map((o) => (
                    <option key={o} value={o}>
                      Organização {o.slice(0, 8)}
                    </option>
                  ))}
                </select>
              ) : (
                <input id="novo-fluxo-org" placeholder="identificador da organização" value={organizacao} onChange={(e) => setOrganizacao(e.target.value.trim())} />
              )}
              <button type="submit" className="btn btn-primario" disabled={!podeCriar}>
                Criar e abrir o editor
              </button>
            </form>
          )}

          {erro && (
            <p role="alert" className="erro">
              {erro}
            </p>
          )}
          {fluxos === null && !erro && <p className="discreto">Carregando...</p>}
          {fluxos !== null && fluxos.length === 0 && <p className="discreto">Nenhum fluxo ainda. Crie o primeiro com "Novo fluxo".</p>}

          {fluxos !== null && fluxos.length > 0 && (
            <table className="tabela-fluxos">
              <caption className="sr-only">Fluxos da empresa</caption>
              <thead>
                <tr>
                  <th scope="col">Nome</th>
                  <th scope="col">Chave</th>
                  <th scope="col">Situação</th>
                  <th scope="col">Atualizado</th>
                  <th scope="col">Ações</th>
                </tr>
              </thead>
              <tbody>
                {fluxos.map((f) => (
                  <FluxoLinha key={f.id} f={f} versoes={versoes[f.key]} ocupado={ocupado} onAbrir={abrir} onVersoes={mostrarVersoes} onAposentar={aposentar} />
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
    </main>
  );
}

function FluxoLinha({
  f,
  versoes,
  ocupado,
  onAbrir,
  onVersoes,
  onAposentar,
}: {
  f: WorkflowSummary;
  versoes: WorkflowSummary[] | undefined;
  ocupado: boolean;
  onAbrir: (f: WorkflowSummary) => Promise<void>;
  onVersoes: (chave: string) => Promise<void>;
  onAposentar: (f: WorkflowSummary) => Promise<void>;
}) {
  return (
    <>
      <tr>
        <td>{f.name}</td>
        <td>
          <code>{f.key}</code>
        </td>
        <td>
          {ROTULO_DO_ESTADO[f.status]} (v{f.version})
        </td>
        <td>{new Date(f.updatedAt).toLocaleString('pt-BR')}</td>
        <td>
          <button type="button" className="btn" disabled={ocupado} aria-label={`Abrir ${f.name}`} onClick={() => void onAbrir(f)}>
            Abrir
          </button>{' '}
          <button type="button" className="btn" aria-expanded={versoes !== undefined} aria-label={`Versões de ${f.name}`} onClick={() => void onVersoes(f.key)}>
            Versões
          </button>{' '}
          {f.status === 'Published' && (
            <button type="button" className="btn" aria-label={`Aposentar ${f.name}`} onClick={() => void onAposentar(f)}>
              Aposentar
            </button>
          )}
        </td>
      </tr>
      {versoes !== undefined && (
        <tr>
          <td colSpan={5}>
            <ul className="fluxo-versoes" aria-label={`Versões de ${f.name}`}>
              {versoes.map((v) => (
                <li key={v.id}>
                  v{v.version} — {ROTULO_DO_ESTADO[v.status]} — {new Date(v.updatedAt).toLocaleString('pt-BR')}{' '}
                  <button type="button" className="btn" aria-label={`Abrir a versão ${v.version} de ${f.name}`} onClick={() => void onAbrir(v)}>
                    Abrir
                  </button>
                </li>
              ))}
            </ul>
          </td>
        </tr>
      )}
    </>
  );
}
