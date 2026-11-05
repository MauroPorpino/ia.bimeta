import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createApi, descreverErro, type ApiClient } from './api/client';
import { aoSair, encerrarSessaoLocal, restaurarSessao } from './api/session';
import type { CurrentUser } from './api/types';
import { ConversationPanel } from './components/ConversationPanel';
import { ErrorBanner, type Aviso } from './components/ErrorBanner';
import { LoginScreen } from './components/LoginScreen';
import { QueuePanel } from './components/QueuePanel';
import { useNomesDosContatos } from './hooks';

interface AppProps {
  /** Para testes. Por padrão, o cliente real (via proxy `/api`). */
  api?: ApiClient;
  intervaloFilaMs?: number;
  intervaloConversaMs?: number;
}

export function App({ api: apiFornecida, intervaloFilaMs, intervaloConversaMs }: AppProps) {
  const api = useMemo(() => apiFornecida ?? createApi(), [apiFornecida]);
  const [autenticado, setAutenticado] = useState(() => restaurarSessao() !== null);
  const [avisoDeEntrada, setAvisoDeEntrada] = useState<string | null>(null);
  const saidaManual = useRef(false);

  // O evento de saída: 401 da API ou logout. Volta à entrada, com aviso só quando a saída não foi pedida pelo usuário.
  useEffect(
    () =>
      aoSair(() => {
        setAutenticado(false);
        setAvisoDeEntrada(saidaManual.current ? null : 'Sua sessão expirou ou não é mais válida. Entre novamente.');
        saidaManual.current = false;
      }),
    [],
  );

  if (!autenticado) {
    return (
      <LoginScreen
        api={api}
        aviso={avisoDeEntrada}
        onEntrou={() => {
          setAvisoDeEntrada(null);
          setAutenticado(true);
        }}
      />
    );
  }

  return <Atendimento api={api} saidaManual={saidaManual} intervaloFilaMs={intervaloFilaMs} intervaloConversaMs={intervaloConversaMs} />;
}

interface AtendimentoProps {
  api: ApiClient;
  saidaManual: { current: boolean };
  intervaloFilaMs?: number;
  intervaloConversaMs?: number;
}

/** A área logada: cabeçalho, faixa de erros e as duas colunas (fila à esquerda, conversa à direita). */
function Atendimento({ api, saidaManual, intervaloFilaMs, intervaloConversaMs }: AtendimentoProps) {
  const [me, setMe] = useState<CurrentUser | null>(null);
  const [erroAoCarregarUsuario, setErroAoCarregarUsuario] = useState<string | null>(null);
  const [tentativa, setTentativa] = useState(0);
  const [itemSelecionado, setItemSelecionado] = useState<string | null>(null);
  const [versaoDaLista, setVersaoDaLista] = useState(0);
  const [errosDeRede, setErrosDeRede] = useState<Record<string, string>>({});
  const [erroDeAcao, setErroDeAcao] = useState<string | null>(null);
  const { nomes, garantir } = useNomesDosContatos(api);

  useEffect(() => {
    let vivo = true;
    setErroAoCarregarUsuario(null);
    api
      .me()
      .then((usuario) => {
        if (vivo) {
          setMe(usuario);
        }
      })
      .catch((erro: unknown) => {
        if (vivo) {
          setErroAoCarregarUsuario(descreverErro(erro));
        }
      });
    return () => {
      vivo = false;
    };
  }, [api, tentativa]);

  const aoErroDeRede = useCallback((origem: string, texto: string | null) => {
    setErrosDeRede((atual) => {
      if (texto === null) {
        if (!(origem in atual)) {
          return atual;
        }
        const { [origem]: _removido, ...resto } = atual;
        return resto;
      }
      return atual[origem] === texto ? atual : { ...atual, [origem]: texto };
    });
  }, []);

  const aoErroDeAcao = useCallback((texto: string | null) => setErroDeAcao(texto), []);
  const aoAlterar = useCallback(() => setVersaoDaLista((v) => v + 1), []);

  async function sair() {
    saidaManual.current = true;
    try {
      await api.logout();
    } catch {
      // Sem resposta do servidor, a sessão local sai do mesmo jeito (o token vence sozinho no servidor).
    }
    encerrarSessaoLocal();
  }

  // Vários carregamentos periódicos falham juntos quando a API cai: a faixa mostra cada mensagem uma vez só.
  const avisos: Aviso[] = [
    ...[...new Set(Object.values(errosDeRede))].map((texto) => ({ id: `rede-${texto}`, texto, dispensavel: false })),
    ...(erroDeAcao ? [{ id: 'acao', texto: erroDeAcao, dispensavel: true }] : []),
    ...(erroAoCarregarUsuario ? [{ id: 'usuario', texto: erroAoCarregarUsuario, dispensavel: false }] : []),
  ];

  function dispensar(id: string) {
    if (id === 'acao') {
      setErroDeAcao(null);
    }
  }

  return (
    <div className="app">
      <header className="topo">
        <h1>ia.bimeta - Atendimento</h1>
        <div className="topo-usuario">
          {me && <span>{me.displayName}</span>}
          <button type="button" className="btn" onClick={() => void sair()}>
            Sair
          </button>
        </div>
      </header>

      <ErrorBanner avisos={avisos} onDispensar={dispensar} />

      {me === null ? (
        <main className="carregando">
          {erroAoCarregarUsuario ? (
            <button type="button" className="btn btn-primario" onClick={() => setTentativa((t) => t + 1)}>
              Tentar novamente
            </button>
          ) : (
            <p className="discreto">Carregando...</p>
          )}
        </main>
      ) : (
        <main className={itemSelecionado ? 'colunas com-selecao' : 'colunas'}>
          <QueuePanel
            api={api}
            me={me}
            itemSelecionado={itemSelecionado}
            onSelecionar={setItemSelecionado}
            nomes={nomes}
            garantirNomes={garantir}
            onErroDeRede={aoErroDeRede}
            onErroDeAcao={aoErroDeAcao}
            versaoDaLista={versaoDaLista}
            intervaloMs={intervaloFilaMs}
          />
          {itemSelecionado ? (
            <ConversationPanel
              key={itemSelecionado}
              api={api}
              itemId={itemSelecionado}
              me={me}
              nomeDoContato={nomes[itemSelecionado]}
              onErroDeRede={aoErroDeRede}
              onErroDeAcao={aoErroDeAcao}
              onAlterado={aoAlterar}
              onVoltar={() => setItemSelecionado(null)}
              intervaloMs={intervaloConversaMs}
            />
          ) : (
            <section className="painel-conversa vazio" aria-label="Conversa">
              <p className="discreto centro">Escolha um atendimento na fila para ver a conversa.</p>
            </section>
          )}
        </main>
      )}
    </div>
  );
}
