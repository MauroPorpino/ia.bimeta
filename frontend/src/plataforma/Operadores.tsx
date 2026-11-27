import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Dialogo } from '../components/Dialogo';
import { descreverErroPlataforma, type PlataformaApi } from './api';
import { TokenUnico } from './TokenUnico';
import type { OperatorDetail, OperatorItem, OperatorPermission, OperatorToken, Pagina, PlatformMe, RoleInfo } from './types';
import { POR_PAGINA, ROTULO_PERFIL_OPERADOR, ROTULO_SITUACAO_OPERADOR, formatarData, rotuloDoPerfilOperador, temPermissao } from './util';

interface Props {
  api: PlataformaApi;
  me: PlatformMe;
}

const PERFIS = Object.keys(ROTULO_PERFIL_OPERADOR);

/** Operadores da plataforma: lista com filtros e, ao abrir um, o detalhe com ações e permissões. */
export function Operadores({ api, me }: Props) {
  const [aberto, setAberto] = useState<string | null>(null);
  if (aberto) {
    return <OperadorDetalhe api={api} me={me} id={aberto} onVoltar={() => setAberto(null)} />;
  }
  return <ListaDeOperadores api={api} me={me} onAbrir={setAberto} />;
}

function ListaDeOperadores({ api, me, onAbrir }: Props & { onAbrir: (id: string) => void }) {
  const [busca, setBusca] = useState('');
  const [situacao, setSituacao] = useState('');
  const [filtros, setFiltros] = useState({ busca: '', situacao: '' });
  const [pagina, setPagina] = useState(1);
  const [dados, setDados] = useState<Pagina<OperatorItem> | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [novo, setNovo] = useState(false);
  const [recarga, setRecarga] = useState(0);
  const [token, setToken] = useState<OperatorToken | null>(null);

  useEffect(() => {
    let ativo = true;
    setErro(null);
    api
      .listarOperadores({ search: filtros.busca, status: filtros.situacao, page: pagina, pageSize: POR_PAGINA })
      .then((d) => ativo && setDados(d))
      .catch((f) => ativo && setErro(descreverErroPlataforma(f)));
    return () => {
      ativo = false;
    };
  }, [api, filtros, pagina, recarga]);

  const totalDePaginas = dados ? Math.max(1, Math.ceil(dados.total / POR_PAGINA)) : 1;

  return (
    <div className="pl-pagina">
      <div className="pl-titulo-linha">
        <h2>Operadores</h2>
        {temPermissao(me, 'PlatformOperatorManage') && (
          <button type="button" className="btn btn-primario" onClick={() => setNovo(true)}>
            Cadastrar operador
          </button>
        )}
      </div>
      <form
        className="filtros pl-filtros"
        role="search"
        aria-label="Filtrar operadores"
        onSubmit={(e) => {
          e.preventDefault();
          setPagina(1);
          setFiltros({ busca: busca.trim(), situacao });
        }}
      >
        <label>
          Busca
          <input value={busca} onChange={(e) => setBusca(e.target.value)} placeholder="Nome ou e-mail" />
        </label>
        <label>
          Situação
          <select value={situacao} onChange={(e) => setSituacao(e.target.value)}>
            <option value="">Todas</option>
            {Object.entries(ROTULO_SITUACAO_OPERADOR).map(([valor, rotulo]) => (
              <option key={valor} value={valor}>
                {rotulo}
              </option>
            ))}
          </select>
        </label>
        <button type="submit" className="btn">
          Filtrar
        </button>
      </form>
      {erro && (
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      )}
      {!dados && !erro && <p className="discreto">Carregando os operadores...</p>}
      {dados && dados.items.length === 0 && <p className="discreto">Nenhum operador encontrado.</p>}
      {dados && dados.items.length > 0 && (
        <div className="pl-tabela-rolagem">
          <table className="tabela-execucoes">
            <thead>
              <tr>
                <th>Operador</th>
                <th>Perfil</th>
                <th>Situação</th>
                <th>Cadastrado em</th>
              </tr>
            </thead>
            <tbody>
              {dados.items.map((o) => (
                <tr key={o.id}>
                  <td>
                    <button type="button" className="btn-link" onClick={() => onAbrir(o.id)} aria-label={`Abrir ${o.displayName}`}>
                      {o.displayName}
                    </button>
                    <br />
                    <span className="discreto">{o.email}</span>
                  </td>
                  <td>{rotuloDoPerfilOperador(o.role)}</td>
                  <td>
                    <span className={o.status === 'Active' ? 'etiqueta' : 'etiqueta etiqueta-aviso'}>{ROTULO_SITUACAO_OPERADOR[o.status] ?? o.status}</span>
                  </td>
                  <td>{formatarData(o.createdAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {dados && dados.total > 0 && (
        <nav className="acoes" aria-label="Paginação">
          <button type="button" className="btn btn-pequeno" disabled={pagina <= 1} onClick={() => setPagina((p) => p - 1)}>
            Anterior
          </button>
          <span className="discreto">
            Página {pagina} de {totalDePaginas} ({dados.total} operadores)
          </span>
          <button type="button" className="btn btn-pequeno" disabled={pagina >= totalDePaginas} onClick={() => setPagina((p) => p + 1)}>
            Próxima
          </button>
        </nav>
      )}
      {novo && (
        <NovoOperador
          api={api}
          podeEscolherPerfil={temPermissao(me, 'PlatformPermissionManage')}
          onCancelar={() => setNovo(false)}
          onCriado={(t) => {
            setNovo(false);
            setToken(t);
            setRecarga((r) => r + 1);
          }}
        />
      )}
      {token && (
        <TokenUnico
          titulo="Operador cadastrado"
          paraQuem={token.operator.email}
          token={token.token}
          expiresAt={token.expiresAt}
          instrucao="O operador usa este token na tela de primeiro acesso para definir a própria senha."
          onFechar={() => setToken(null)}
        />
      )}
    </div>
  );
}

function NovoOperador({ api, podeEscolherPerfil, onCancelar, onCriado }: { api: PlataformaApi; podeEscolherPerfil: boolean; onCancelar: () => void; onCriado: (t: OperatorToken) => void }) {
  const [nome, setNome] = useState('');
  const [email, setEmail] = useState('');
  const [perfil, setPerfil] = useState('PlatformViewer');
  const [erro, setErro] = useState<string | null>(null);
  const [enviando, setEnviando] = useState(false);
  const emAndamento = useRef(false);

  async function enviar(e: FormEvent) {
    e.preventDefault();
    if (emAndamento.current) {
      return;
    }
    emAndamento.current = true;
    setEnviando(true);
    setErro(null);
    try {
      onCriado(await api.criarOperador({ email: email.trim(), displayName: nome.trim(), role: perfil }));
    } catch (f) {
      setErro(descreverErroPlataforma(f));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  return (
    <Dialogo titulo="Cadastrar operador" onFechar={onCancelar}>
      <form className="revisao-formulario" onSubmit={(e) => void enviar(e)}>
        <label>
          Nome completo
          <input value={nome} onChange={(e) => setNome(e.target.value)} required maxLength={200} />
        </label>
        <label>
          E-mail
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required maxLength={320} autoComplete="off" />
        </label>
        <label>
          Perfil
          <select value={perfil} onChange={(e) => setPerfil(e.target.value)}>
            {PERFIS.map((p) => (
              <option key={p} value={p} disabled={p === 'PlatformAdministrator' && !podeEscolherPerfil}>
                {rotuloDoPerfilOperador(p)}
              </option>
            ))}
          </select>
        </label>
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        <div className="acoes">
          <button type="submit" className="btn btn-primario" disabled={enviando}>
            {enviando ? 'Cadastrando...' : 'Cadastrar operador'}
          </button>
          <button type="button" className="btn" onClick={onCancelar}>
            Cancelar
          </button>
        </div>
      </form>
    </Dialogo>
  );
}

export function situacaoDaPermissao(p: Pick<OperatorPermission, 'fromRole' | 'override'>): string {
  if (p.override === 'Grant') {
    return 'Concedida';
  }
  if (p.override === 'Deny') {
    return 'Retirada';
  }
  return p.fromRole ? 'Herdada do perfil' : 'Não concedida pelo perfil';
}

function OperadorDetalhe({ api, me, id, onVoltar }: Props & { id: string; onVoltar: () => void }) {
  const [detalhe, setDetalhe] = useState<OperatorDetail | null>(null);
  const [perfis, setPerfis] = useState<RoleInfo[]>([]);
  const [erro, setErro] = useState<string | null>(null);
  const [erroDeAcao, setErroDeAcao] = useState<string | null>(null);
  const [aviso, setAviso] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [token, setToken] = useState<OperatorToken | null>(null);
  const [editando, setEditando] = useState(false);

  const podeGerir = temPermissao(me, 'PlatformOperatorManage');
  const podePermissoes = temPermissao(me, 'PlatformPermissionManage');

  useEffect(() => {
    let ativo = true;
    api
      .operador(id)
      .then((d) => ativo && setDetalhe(d))
      .catch((f) => ativo && setErro(descreverErroPlataforma(f)));
    return () => {
      ativo = false;
    };
  }, [api, id]);

  useEffect(() => {
    if (!podePermissoes) {
      return;
    }
    let ativo = true;
    api
      .perfis()
      .then((p) => ativo && setPerfis(p))
      .catch(() => undefined);
    return () => {
      ativo = false;
    };
  }, [api, podePermissoes]);

  const executar = useCallback(async (acao: () => Promise<OperatorDetail | void>, mensagem?: string): Promise<boolean> => {
    setOcupado(true);
    setErroDeAcao(null);
    setAviso(null);
    try {
      const r = await acao();
      if (r) {
        setDetalhe(r);
      }
      if (mensagem) {
        setAviso(mensagem);
      }
      return true;
    } catch (f) {
      setErroDeAcao(descreverErroPlataforma(f));
      return false;
    } finally {
      setOcupado(false);
    }
  }, []);

  async function emitirAcesso() {
    setOcupado(true);
    setErroDeAcao(null);
    try {
      const t = await api.emitirAcesso(id);
      setToken(t);
      setDetalhe(await api.operador(id));
    } catch (f) {
      setErroDeAcao(descreverErroPlataforma(f));
    } finally {
      setOcupado(false);
    }
  }

  const voltar = (
    <button type="button" className="btn-link" onClick={onVoltar}>
      Voltar para os operadores
    </button>
  );
  if (erro) {
    return (
      <div className="pl-pagina">
        {voltar}
        <p className="erro-campo" role="alert">
          {erro}
        </p>
      </div>
    );
  }
  if (!detalhe) {
    return (
      <div className="pl-pagina">
        {voltar}
        <p className="discreto">Carregando o operador...</p>
      </div>
    );
  }

  const { operator, permissions } = detalhe;
  const pendente = operator.status === 'Pending' || operator.status === 'Expired';
  const inativo = operator.status === 'Inactive';
  const sou = operator.id === me.id;
  const grupos = [...new Set(permissions.map((p) => p.group))];
  const algumaPersonalizada = permissions.some((p) => p.override !== null);

  return (
    <div className="pl-pagina">
      {voltar}
      <div className="pl-titulo-linha">
        <h2>{operator.displayName}</h2>
        <span className={operator.status === 'Active' ? 'etiqueta' : 'etiqueta etiqueta-aviso'}>{ROTULO_SITUACAO_OPERADOR[operator.status] ?? operator.status}</span>
      </div>
      {aviso && (
        <p className="aviso" role="status">
          {aviso}
        </p>
      )}
      {erroDeAcao && (
        <p className="erro-campo" role="alert">
          {erroDeAcao}
        </p>
      )}
      <dl className="pl-dl">
        <dt>E-mail</dt>
        <dd>{operator.email}</dd>
        <dt>Perfil</dt>
        <dd>{rotuloDoPerfilOperador(operator.role)}</dd>
        <dt>Cadastrado em</dt>
        <dd>{formatarData(operator.createdAt)}</dd>
        <dt>Token de primeiro acesso vigente</dt>
        <dd>{operator.hasLiveFirstAccessToken ? 'Sim' : 'Não'}</dd>
      </dl>

      {podeGerir && (
        <div className="acoes">
          <button type="button" className="btn btn-pequeno" disabled={ocupado} onClick={() => setEditando(true)}>
            Editar dados
          </button>
          {!inativo && operator.status !== 'Cancelled' && (
            <button type="button" className="btn btn-pequeno" disabled={ocupado} onClick={() => void emitirAcesso()}>
              {pendente ? 'Emitir primeiro acesso' : 'Emitir redefinição de acesso'}
            </button>
          )}
          {operator.status === 'Active' && (
            <button type="button" className="btn btn-pequeno" disabled={ocupado} onClick={() => void executar(() => api.revogarSessoes(id), 'Sessões revogadas.')}>
              Revogar sessões
            </button>
          )}
          {operator.status === 'Active' && !sou && (
            <button type="button" className="btn btn-pequeno btn-perigo" disabled={ocupado} onClick={() => void executar(() => api.desativarOperador(id))}>
              Desativar
            </button>
          )}
          {inativo && (
            <button type="button" className="btn btn-pequeno" disabled={ocupado} onClick={() => void executar(() => api.reativarOperador(id))}>
              Reativar
            </button>
          )}
        </div>
      )}

      {podePermissoes && (
        <div className="revisao-formulario">
          <label>
            Perfil do operador
            <select
              value={operator.role}
              disabled={ocupado}
              onChange={(e) => void executar(() => api.definirPerfil(id, e.target.value), 'Perfil alterado.')}
            >
              {(perfis.length > 0 ? perfis.map((p) => ({ valor: p.role, rotulo: p.label })) : PERFIS.map((p) => ({ valor: p, rotulo: rotuloDoPerfilOperador(p) }))).map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.rotulo}
                </option>
              ))}
            </select>
          </label>
        </div>
      )}

      <section aria-labelledby="pl-op-perm">
        <h3 id="pl-op-perm">Permissões</h3>
        {grupos.map((grupo) => (
          <div key={grupo}>
            <strong>{grupo}</strong>
            <ul className="lista-permissoes">
              {permissions
                .filter((p) => p.group === grupo)
                .map((p) => (
                  <li key={p.code}>
                    <span className="permissao-nome">{p.label}</span>
                    <span className="etiqueta">{situacaoDaPermissao(p)}</span>
                    <span className={p.effective ? 'etiqueta' : 'etiqueta etiqueta-aviso'}>{p.effective ? 'Efetiva' : 'Não efetiva'}</span>
                    {podePermissoes && (
                      <span className="acoes">
                        {p.effective ? (
                          <button type="button" className="btn btn-pequeno" disabled={ocupado} aria-label={`Retirar ${p.label}`} onClick={() => void executar(() => api.definirPermissao(id, p.code, 'Deny'))}>
                            Retirar
                          </button>
                        ) : (
                          <button type="button" className="btn btn-pequeno" disabled={ocupado} aria-label={`Conceder ${p.label}`} onClick={() => void executar(() => api.definirPermissao(id, p.code, 'Grant'))}>
                            Conceder
                          </button>
                        )}
                        {p.override !== null && (
                          <button type="button" className="btn btn-pequeno" disabled={ocupado} aria-label={`Restaurar padrão do perfil para ${p.label}`} onClick={() => void executar(() => api.removerPermissao(id, p.code))}>
                            Restaurar padrão
                          </button>
                        )}
                      </span>
                    )}
                  </li>
                ))}
            </ul>
          </div>
        ))}
        {podePermissoes && algumaPersonalizada && (
          <div className="acoes">
            <button type="button" className="btn btn-pequeno" disabled={ocupado} onClick={() => void executar(() => api.restaurarPermissoes(id), 'Permissões restauradas ao padrão do perfil.')}>
              Restaurar todas as permissões ao padrão do perfil
            </button>
          </div>
        )}
      </section>

      {editando && (
        <EditarOperador
          operador={operator}
          ocupado={ocupado}
          erro={erroDeAcao}
          onCancelar={() => setEditando(false)}
          onSalvar={async (p) => {
            if (await executar(() => api.atualizarOperador(id, p))) {
              setEditando(false);
            }
          }}
        />
      )}
      {token && (
        <TokenUnico
          titulo={token.kind === 'first-access' ? 'Primeiro acesso emitido' : 'Redefinição de acesso emitida'}
          paraQuem={token.operator.email}
          token={token.token}
          expiresAt={token.expiresAt}
          instrucao="O operador usa este token na tela de primeiro acesso para definir a própria senha."
          onFechar={() => setToken(null)}
        />
      )}
    </div>
  );
}

function EditarOperador({ operador, ocupado, erro, onCancelar, onSalvar }: { operador: OperatorItem; ocupado: boolean; erro: string | null; onCancelar: () => void; onSalvar: (p: { email: string; displayName: string }) => Promise<void> }) {
  const [nome, setNome] = useState(operador.displayName);
  const [email, setEmail] = useState(operador.email);
  return (
    <Dialogo titulo="Editar operador" onFechar={onCancelar}>
      <form
        className="revisao-formulario"
        onSubmit={(e) => {
          e.preventDefault();
          void onSalvar({ email: email.trim(), displayName: nome.trim() });
        }}
      >
        <label>
          Nome completo
          <input value={nome} onChange={(e) => setNome(e.target.value)} required maxLength={200} />
        </label>
        <label>
          E-mail
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required maxLength={320} />
        </label>
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        <div className="acoes">
          <button type="submit" className="btn btn-primario" disabled={ocupado}>
            Salvar
          </button>
          <button type="button" className="btn" onClick={onCancelar}>
            Cancelar
          </button>
        </div>
      </form>
    </Dialogo>
  );
}
