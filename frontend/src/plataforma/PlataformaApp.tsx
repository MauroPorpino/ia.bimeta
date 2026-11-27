import { useEffect, useMemo, useState } from 'react';
import { Auditoria } from './Auditoria';
import { EmpresaDetalhe } from './EmpresaDetalhe';
import { Empresas } from './Empresas';
import { MinhaConta } from './MinhaConta';
import { Operadores } from './Operadores';
import { PlataformaLogin } from './PlataformaLogin';
import { VisaoGeral } from './VisaoGeral';
import { aoSairDaPlataforma, createPlataformaApi, descreverErroPlataforma, encerrarSessaoPlataforma, restaurarSessaoPlataforma, type PlataformaApi } from './api';
import { ITENS_DE_MENU, chaveDoCaminho, type ChaveDeMenu } from './menu';
import type { PlatformMe } from './types';
import { temPermissao } from './util';

interface Props {
  /** Para testes. */
  api?: PlataformaApi;
}

function caminhoAtual(): string {
  return typeof window === 'undefined' ? '/plataforma' : window.location.pathname;
}

/** O painel de administração da Plataforma BImeta: aplicação separada, com sessão própria. */
export function PlataformaApp({ api: apiDada }: Props) {
  const api = useMemo(() => apiDada ?? createPlataformaApi(), [apiDada]);
  const [autenticado, setAutenticado] = useState(() => restaurarSessaoPlataforma() !== null);
  const [aviso, setAviso] = useState<string | null>(null);

  useEffect(
    () =>
      aoSairDaPlataforma(() => {
        setAutenticado(false);
        setAviso('Sua sessão expirou. Entre novamente.');
      }),
    [],
  );

  if (!autenticado) {
    return (
      <PlataformaLogin
        api={api}
        aviso={aviso}
        onEntrou={() => {
          setAviso(null);
          setAutenticado(true);
        }}
      />
    );
  }
  return <Painel api={api} onSair={() => setAutenticado(false)} />;
}

function Painel({ api, onSair }: { api: PlataformaApi; onSair: () => void }) {
  const [me, setMe] = useState<PlatformMe | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [tela, setTela] = useState<ChaveDeMenu>(() => chaveDoCaminho(caminhoAtual()));
  const [slug, setSlug] = useState<string | null>(null);

  useEffect(() => {
    let ativo = true;
    api
      .eu()
      .then((d) => ativo && setMe(d))
      .catch((f) => ativo && setErro(descreverErroPlataforma(f)));
    return () => {
      ativo = false;
    };
  }, [api]);

  async function sair() {
    try {
      await api.logout();
    } catch {
      // a sessão local é encerrada de qualquer forma
    }
    encerrarSessaoPlataforma();
    onSair();
  }

  if (erro) {
    return (
      <main className="tela-entrada">
        <div className="cartao-entrada">
          <p className="erro-campo" role="alert">
            {erro}
          </p>
          <button type="button" className="btn" onClick={() => void sair()}>
            Voltar à entrada
          </button>
        </div>
      </main>
    );
  }
  if (!me) {
    return (
      <main className="tela-entrada">
        <p className="discreto">Carregando...</p>
      </main>
    );
  }

  const itens = ITENS_DE_MENU.filter((i) => !i.permissao || temPermissao(me, i.permissao));
  // Se a tela pedida não está disponível ao operador (ex.: recarregou em /plataforma/auditoria sem permissão), cai na primeira permitida.
  const atual = itens.find((i) => i.chave === tela) ?? itens[0];

  function ir(chave: ChaveDeMenu) {
    const item = ITENS_DE_MENU.find((i) => i.chave === chave);
    setTela(chave);
    setSlug(null);
    if (item) {
      try {
        window.history.pushState(null, '', item.caminho);
      } catch {
        // ignora
      }
    }
  }

  return (
    <div className="pl-shell">
      <header className="pl-topo">
        <strong className="pl-marca">Plataforma BImeta</strong>
        <span className="discreto">{me.displayName}</span>
        <button type="button" className="btn btn-pequeno" onClick={() => void sair()}>
          Sair
        </button>
      </header>
      <div className="pl-corpo">
        <nav className="pl-menu" aria-label="Menu da plataforma">
          {itens.map((i) => (
            <button key={i.chave} type="button" className={atual?.chave === i.chave ? 'pl-menu-item ativo' : 'pl-menu-item'} aria-current={atual?.chave === i.chave ? 'page' : undefined} onClick={() => ir(i.chave)}>
              {i.rotulo}
            </button>
          ))}
        </nav>
        <main className="pl-conteudo">
          {atual?.chave === 'visao-geral' && <VisaoGeral api={api} />}
          {atual?.chave === 'empresas' &&
            (slug ? <EmpresaDetalhe api={api} me={me} slug={slug} onVoltar={() => setSlug(null)} /> : <Empresas api={api} me={me} onAbrir={setSlug} />)}
          {atual?.chave === 'operadores' && <Operadores api={api} me={me} />}
          {atual?.chave === 'auditoria' && <Auditoria api={api} />}
          {atual?.chave === 'conta' && <MinhaConta api={api} me={me} onSair={() => void sair()} />}
          {!atual && <p className="discreto">Este operador não tem acesso a nenhuma área do painel.</p>}
        </main>
      </div>
    </div>
  );
}
