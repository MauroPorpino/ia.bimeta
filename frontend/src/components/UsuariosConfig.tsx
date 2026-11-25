import { useEffect, useState, type FormEvent } from 'react';
import type { ApiClient } from '../api/client';
import type { CurrentUser, IssuedToken, OrganizationSummary, PermissionCatalog, UserPage, UserUsage } from '../api/types';
import { PERFIS, ROTULO_SITUACAO, SITUACOES_DO_FILTRO, mensagemDeErroDeUsuario, organizacoesCom, perfisDoUsuario, pode, rotuloDoPerfil } from '../usuarios';
import { NovoUsuario } from './NovoUsuario';
import { TokenEmitido } from './TokenEmitido';
import { UsuarioDetalhe } from './UsuarioDetalhe';
import { UsoDeUsuarios } from './UsoDeUsuarios';

interface Props {
  api: ApiClient;
  me: CurrentUser;
}

const POR_PAGINA = 20;

interface Filtros {
  busca: string;
  perfil: string;
  organizacao: string;
  situacao: string;
}

const SEM_FILTROS: Filtros = { busca: '', perfil: '', organizacao: '', situacao: '' };

interface TokenAberto {
  titulo: string;
  paraQuem: string;
  emitido: IssuedToken;
}

/** Configurações → Usuários: painel de uso, listagem com filtros, admissão e detalhe. O servidor aplica cada permissão; aqui só se escolhe o que mostrar. */
export function UsuariosConfig({ api, me }: Props) {
  const [uso, setUso] = useState<UserUsage | null>(null);
  const [erroDoUso, setErroDoUso] = useState<string | null>(null);
  const [catalogo, setCatalogo] = useState<PermissionCatalog | null>(null);
  const [organizacoes, setOrganizacoes] = useState<OrganizationSummary[]>([]);
  const [pagina, setPagina] = useState<UserPage | null>(null);
  const [erroDaLista, setErroDaLista] = useState<string | null>(null);
  const [rascunho, setRascunho] = useState<Filtros>(SEM_FILTROS);
  const [filtros, setFiltros] = useState<Filtros>(SEM_FILTROS);
  const [numero, setNumero] = useState(1);
  const [recarga, setRecarga] = useState(0);
  const [selecionado, setSelecionado] = useState<string | null>(null);
  const [cadastrando, setCadastrando] = useState(false);
  // O token vive SÓ aqui, em memória, enquanto o diálogo está aberto: nunca em storage, URL ou log.
  const [token, setToken] = useState<TokenAberto | null>(null);

  useEffect(() => {
    let vivo = true;
    api
      .usoDeUsuarios()
      .then((lido) => {
        if (vivo) {
          setUso(lido);
          setErroDoUso(null);
        }
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErroDoUso(mensagemDeErroDeUsuario(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, recarga]);

  // O catálogo e as organizações são apoio: se falham, a tela segue (sem a prévia de permissões; sem nomes das organizações).
  useEffect(() => {
    let vivo = true;
    api
      .catalogoDePermissoes()
      .then((lido) => vivo && setCatalogo(lido))
      .catch(() => undefined);
    api
      .listarOrganizacoes()
      .then((lidas) => vivo && setOrganizacoes(Array.isArray(lidas) ? lidas : []))
      .catch(() => undefined);
    return () => {
      vivo = false;
    };
  }, [api]);

  useEffect(() => {
    let vivo = true;
    api
      .listarUsuarios({
        search: filtros.busca.trim(),
        role: filtros.perfil,
        organizationId: filtros.organizacao,
        status: filtros.situacao,
        page: numero,
        pageSize: POR_PAGINA,
      })
      .then((lida) => {
        if (vivo) {
          setPagina(lida);
          setErroDaLista(null);
        }
      })
      .catch((falha: unknown) => {
        if (vivo) {
          setErroDaLista(mensagemDeErroDeUsuario(falha));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, filtros, numero, recarga]);

  function aplicar(evento: FormEvent) {
    evento.preventDefault();
    setNumero(1);
    setFiltros(rascunho);
  }

  function limpar() {
    setRascunho(SEM_FILTROS);
    setFiltros(SEM_FILTROS);
    setNumero(1);
  }

  const podeCadastrar = organizacoesCom(me, 'UserManage', organizacoes).length > 0;
  const total = pagina?.total ?? 0;
  const paginas = Math.max(1, Math.ceil(total / POR_PAGINA));

  return (
    <div className="config-usuarios">
      <UsoDeUsuarios uso={uso} erro={erroDoUso} />

      <div className="integracoes-corpo">
        <section className="integracoes-lista" aria-label="Usuários">
          <h2>Usuários</h2>
          <div className="acoes">
            {pode(me, 'UserManage') && (
              <button type="button" className="btn btn-primario" disabled={!podeCadastrar} onClick={() => setCadastrando(true)}>
                Cadastrar usuário
              </button>
            )}
          </div>

          <form className="filtros acoes" role="search" aria-label="Filtros de usuários" onSubmit={aplicar}>
            <label>
              Buscar por nome ou e-mail
              <input type="search" value={rascunho.busca} onChange={(e) => setRascunho({ ...rascunho, busca: e.target.value })} />
            </label>
            <label>
              Perfil
              <select value={rascunho.perfil} onChange={(e) => setRascunho({ ...rascunho, perfil: e.target.value })}>
                <option value="">Todos</option>
                {PERFIS.map((p) => (
                  <option key={p} value={p}>
                    {rotuloDoPerfil(p)}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Organização
              <select value={rascunho.organizacao} onChange={(e) => setRascunho({ ...rascunho, organizacao: e.target.value })}>
                <option value="">Todas</option>
                {organizacoes.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.name}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Situação
              <select value={rascunho.situacao} onChange={(e) => setRascunho({ ...rascunho, situacao: e.target.value })}>
                <option value="">Todas</option>
                {SITUACOES_DO_FILTRO.map((s) => (
                  <option key={s.valor} value={s.valor}>
                    {s.rotulo}
                  </option>
                ))}
              </select>
            </label>
            <button type="submit" className="btn">
              Filtrar
            </button>
            <button type="button" className="btn" onClick={limpar}>
              Limpar filtros
            </button>
          </form>

          {erroDaLista && (
            <p className="erro-campo" role="alert">
              {erroDaLista}
            </p>
          )}
          {pagina === null && !erroDaLista && <p className="discreto">Carregando...</p>}
          {pagina?.items.length === 0 && <p className="discreto">Nenhum usuário encontrado.</p>}
          {pagina && pagina.items.length > 0 && (
            <table className="tabela-execucoes">
              <thead>
                <tr>
                  <th>Nome</th>
                  <th>E-mail</th>
                  <th>Perfil</th>
                  <th>Situação</th>
                  <th>Personalizado</th>
                </tr>
              </thead>
              <tbody>
                {pagina.items.map((u) => (
                  <tr key={u.id} aria-selected={selecionado === u.id}>
                    <td>
                      <button type="button" className="btn-link" aria-label={`Abrir ${u.displayName}`} onClick={() => setSelecionado(u.id)}>
                        {u.displayName}
                      </button>
                    </td>
                    <td>{u.email}</td>
                    <td>{perfisDoUsuario(u, organizacoes)}</td>
                    <td>
                      <span className="etiqueta">{ROTULO_SITUACAO[u.status]}</span>
                    </td>
                    <td>{u.customized ? 'Sim' : 'Não'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {pagina && (
            <nav className="paginacao" aria-label="Paginação">
              <span className="discreto">
                {total} {total === 1 ? 'usuário' : 'usuários'} · página {pagina.page} de {paginas}
              </span>
              <div className="acoes">
                <button type="button" className="btn btn-pequeno" disabled={numero <= 1} onClick={() => setNumero((n) => n - 1)}>
                  Anterior
                </button>
                <button type="button" className="btn btn-pequeno" disabled={numero >= paginas} onClick={() => setNumero((n) => n + 1)}>
                  Próxima
                </button>
              </div>
            </nav>
          )}
        </section>

        <section className="integracoes-detalhe" aria-label="Detalhe do usuário">
          {selecionado === null ? (
            <p className="discreto centro">Escolha um usuário para ver o detalhe.</p>
          ) : (
            <UsuarioDetalhe
              key={selecionado}
              api={api}
              me={me}
              usuarioId={selecionado}
              organizacoes={organizacoes}
              catalogo={catalogo}
              onAlterado={() => setRecarga((r) => r + 1)}
              onToken={(titulo, paraQuem, emitido) => setToken({ titulo, paraQuem, emitido })}
              onFechar={() => setSelecionado(null)}
            />
          )}
        </section>
      </div>

      {cadastrando && (
        <NovoUsuario
          api={api}
          me={me}
          organizacoes={organizacoes}
          catalogo={catalogo}
          onCancelar={() => setCadastrando(false)}
          onCriado={(resultado) => {
            setCadastrando(false);
            setSelecionado(resultado.user.id);
            setRecarga((r) => r + 1);
            setToken({ titulo: 'Token de primeiro acesso', paraQuem: `${resultado.user.displayName} (${resultado.user.email})`, emitido: { token: resultado.token, expiresAt: resultado.expiresAt } });
          }}
        />
      )}

      {token && <TokenEmitido titulo={token.titulo} paraQuem={token.paraQuem} token={token.emitido.token} expiresAt={token.emitido.expiresAt} onFechar={() => setToken(null)} />}
    </div>
  );
}
