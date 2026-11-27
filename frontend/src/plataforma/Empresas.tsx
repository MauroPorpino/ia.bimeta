import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Dialogo } from '../components/Dialogo';
import { descreverErroPlataforma, type PlataformaApi } from './api';
import { TokenUnico } from './TokenUnico';
import type { CompanyStatus, CreateCompanyResult, Pagina, CompanyListItem, PlatformMe } from './types';
import { POR_PAGINA, ROTULO_SITUACAO_EMPRESA, temPermissao, validarSlug } from './util';

interface Props {
  api: PlataformaApi;
  me: PlatformMe;
  onAbrir: (slug: string) => void;
}

export function SeloDaEmpresa({ status }: { status: CompanyStatus }) {
  return <span className={status === 'Active' ? 'etiqueta' : 'etiqueta etiqueta-aviso'}>{ROTULO_SITUACAO_EMPRESA[status] ?? status}</span>;
}

/** Lista de empresas: busca, filtro de situação, paginação e cadastro. Nunca lista usuários das empresas. */
export function Empresas({ api, me, onAbrir }: Props) {
  const [busca, setBusca] = useState('');
  const [situacao, setSituacao] = useState('');
  const [pagina, setPagina] = useState(1);
  const [filtros, setFiltros] = useState({ busca: '', situacao: '' });
  const [dados, setDados] = useState<Pagina<CompanyListItem> | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [nova, setNova] = useState(false);
  const [recarga, setRecarga] = useState(0);

  useEffect(() => {
    let ativo = true;
    setErro(null);
    api
      .listarEmpresas({ search: filtros.busca, status: filtros.situacao, page: pagina, pageSize: POR_PAGINA })
      .then((d) => ativo && setDados(d))
      .catch((f) => ativo && setErro(descreverErroPlataforma(f)));
    return () => {
      ativo = false;
    };
  }, [api, filtros, pagina, recarga]);

  function aplicar(evento: FormEvent) {
    evento.preventDefault();
    setPagina(1);
    setFiltros({ busca: busca.trim(), situacao });
  }

  const totalDePaginas = dados ? Math.max(1, Math.ceil(dados.total / POR_PAGINA)) : 1;

  return (
    <div className="pl-pagina">
      <div className="pl-titulo-linha">
        <h2>Empresas</h2>
        {temPermissao(me, 'PlatformTenantCreate') && (
          <button type="button" className="btn btn-primario" onClick={() => setNova(true)}>
            Cadastrar empresa
          </button>
        )}
      </div>

      <form className="filtros pl-filtros" onSubmit={aplicar} role="search" aria-label="Filtrar empresas">
        <label>
          Busca
          <input value={busca} onChange={(e) => setBusca(e.target.value)} placeholder="Nome ou identificador" />
        </label>
        <label>
          Situação
          <select value={situacao} onChange={(e) => setSituacao(e.target.value)}>
            <option value="">Todas</option>
            <option value="Active">Ativa</option>
            <option value="Suspended">Suspensa</option>
            <option value="Provisioning">Aguardando provisionamento</option>
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
      {!dados && !erro && <p className="discreto">Carregando as empresas...</p>}
      {dados && dados.items.length === 0 && <p className="discreto">Nenhuma empresa encontrada.</p>}
      {dados && dados.items.length > 0 && (
        <div className="pl-tabela-rolagem">
          <table className="tabela-execucoes">
            <thead>
              <tr>
                <th>Empresa</th>
                <th>Situação</th>
                <th>Limite</th>
                <th>Ativos</th>
                <th>Reservas</th>
                <th>Disponíveis</th>
                <th>Excesso</th>
                <th>Cadastro</th>
              </tr>
            </thead>
            <tbody>
              {dados.items.map(({ company, seats }) => (
                <tr key={company.tenantId}>
                  <td>
                    <button type="button" className="btn-link" onClick={() => onAbrir(company.slug)} aria-label={`Abrir ${company.name}`}>
                      {company.name}
                    </button>
                    <br />
                    <span className="discreto">{company.slug}</span>
                  </td>
                  <td>
                    <SeloDaEmpresa status={company.status} />
                  </td>
                  <td>{seats.limit ?? 'Sem limite'}</td>
                  <td>{seats.active}</td>
                  <td>{seats.pendingReserved}</td>
                  <td>{seats.available ?? '-'}</td>
                  <td>{seats.excess > 0 ? <span className="etiqueta etiqueta-aviso">{seats.excess}</span> : 0}</td>
                  <td>{seats.registrationEnabled ? 'Habilitado' : 'Desabilitado'}</td>
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
            Página {pagina} de {totalDePaginas} ({dados.total} empresas)
          </span>
          <button type="button" className="btn btn-pequeno" disabled={pagina >= totalDePaginas} onClick={() => setPagina((p) => p + 1)}>
            Próxima
          </button>
        </nav>
      )}

      {nova && (
        <NovaEmpresa
          api={api}
          onCancelar={() => setNova(false)}
          onConcluida={() => {
            setNova(false);
            setRecarga((r) => r + 1);
          }}
          onAbrir={onAbrir}
        />
      )}
    </div>
  );
}

interface NovaProps {
  api: PlataformaApi;
  onCancelar: () => void;
  onConcluida: () => void;
  onAbrir: (slug: string) => void;
}

/** Cadastro de empresa: o limite de usuários é obrigatório; o token do administrador inicial aparece uma única vez. */
function NovaEmpresa({ api, onCancelar, onConcluida, onAbrir }: NovaProps) {
  const [slug, setSlug] = useState('');
  const [nome, setNome] = useState('');
  const [razao, setRazao] = useState('');
  const [documento, setDocumento] = useState('');
  const [contato, setContato] = useState('');
  const [emailContato, setEmailContato] = useState('');
  const [limite, setLimite] = useState('');
  const [cadastro, setCadastro] = useState(true);
  const [adminEmail, setAdminEmail] = useState('');
  const [adminNome, setAdminNome] = useState('');
  const [erro, setErro] = useState<string | null>(null);
  const [enviando, setEnviando] = useState(false);
  const [resultado, setResultado] = useState<CreateCompanyResult | null>(null);
  const emAndamento = useRef(false);

  const fechar = useCallback(() => {
    // O token some junto com o resultado.
    setResultado(null);
    onConcluida();
  }, [onConcluida]);

  async function enviar(evento: FormEvent) {
    evento.preventDefault();
    if (emAndamento.current) {
      return;
    }
    const slugLimpo = slug.trim();
    const problemaDoSlug = validarSlug(slugLimpo);
    if (problemaDoSlug) {
      setErro(problemaDoSlug);
      return;
    }
    const maxUsers = Number(limite);
    if (!Number.isInteger(maxUsers) || maxUsers < 1) {
      setErro('Informe o limite de usuários (inteiro, no mínimo 1).');
      return;
    }
    emAndamento.current = true;
    setEnviando(true);
    setErro(null);
    try {
      const r = await api.criarEmpresa({
        slug: slugLimpo,
        name: nome.trim(),
        legalName: razao.trim(),
        document: documento.trim(),
        contactName: contato.trim(),
        contactEmail: emailContato.trim(),
        maxUsers,
        registrationEnabled: cadastro,
        adminEmail: adminEmail.trim(),
        adminName: adminNome.trim(),
      });
      setResultado(r);
    } catch (falha) {
      setErro(descreverErroPlataforma(falha));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  if (resultado) {
    return (
      <TokenUnico
        titulo={resultado.created ? 'Empresa cadastrada' : 'Empresa já cadastrada: primeiro acesso reemitido'}
        paraQuem={resultado.firstAccess.email}
        token={resultado.firstAccess.token}
        expiresAt={resultado.firstAccess.expiresAt}
        instrucao="O administrador inicial usa este token na tela de primeiro acesso para definir a própria senha."
        onFechar={() => {
          const slugCriado = resultado.detail.company.slug;
          fechar();
          onAbrir(slugCriado);
        }}
      />
    );
  }

  return (
    <Dialogo titulo="Cadastrar empresa" onFechar={onCancelar}>
      <form className="revisao-formulario" onSubmit={(e) => void enviar(e)}>
        <label>
          Identificador técnico (slug)
          <input value={slug} onChange={(e) => setSlug(e.target.value)} required maxLength={40} autoCapitalize="none" spellCheck={false} placeholder="ex.: lab-exemplo" />
        </label>
        <p className="aviso" role="note">
          O identificador técnico é permanente: não poderá ser alterado depois. Use letras minúsculas, números e hífens (3 a 40 caracteres).
        </p>
        <label>
          Nome da empresa
          <input value={nome} onChange={(e) => setNome(e.target.value)} required maxLength={200} />
        </label>
        <label>
          Razão social
          <input value={razao} onChange={(e) => setRazao(e.target.value)} maxLength={200} />
        </label>
        <label>
          Documento (CNPJ)
          <input value={documento} onChange={(e) => setDocumento(e.target.value)} maxLength={32} />
        </label>
        <label>
          Nome do contato
          <input value={contato} onChange={(e) => setContato(e.target.value)} maxLength={200} />
        </label>
        <label>
          E-mail do contato
          <input type="email" value={emailContato} onChange={(e) => setEmailContato(e.target.value)} maxLength={320} />
        </label>
        <label>
          Limite de usuários contratados
          <input type="number" min={1} step={1} value={limite} onChange={(e) => setLimite(e.target.value)} required />
        </label>
        <label className="pl-linha-check">
          <input type="checkbox" checked={cadastro} onChange={(e) => setCadastro(e.target.checked)} />
          Novos cadastros de usuários habilitados
        </label>
        <label>
          Nome do administrador inicial
          <input value={adminNome} onChange={(e) => setAdminNome(e.target.value)} required maxLength={200} />
        </label>
        <label>
          E-mail do administrador inicial
          <input type="email" value={adminEmail} onChange={(e) => setAdminEmail(e.target.value)} required maxLength={320} autoComplete="off" />
        </label>
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        <div className="acoes">
          <button type="submit" className="btn btn-primario" disabled={enviando}>
            {enviando ? 'Cadastrando...' : 'Cadastrar empresa'}
          </button>
          <button type="button" className="btn" onClick={onCancelar}>
            Cancelar
          </button>
        </div>
      </form>
    </Dialogo>
  );
}
