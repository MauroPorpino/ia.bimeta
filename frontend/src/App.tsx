import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createApi, descreverErro, ehFalhaDeConexao, type ApiClient } from './api/client';
import { aoSair, encerrarSessaoLocal, obterTenant, restaurarSessao } from './api/session';
import { gravarItemAberto, lerItemAberto } from './armazem';
import type { CurrentUser, SendingAlert } from './api/types';
import { AlertasDeEnvio } from './components/AlertasDeEnvio';
import { ConexaoBanner } from './components/ConexaoBanner';
import { Conhecimento } from './components/Conhecimento';
import { Integracoes } from './components/Integracoes';
import { ConversationPanel } from './components/ConversationPanel';
import { ErrorBanner, type Aviso } from './components/ErrorBanner';
import { LoginScreen } from './components/LoginScreen';
import { QueuePanel } from './components/QueuePanel';
import { TrocarSenha } from './components/TrocarSenha';
import { ehAdministradorDoTenant } from './conhecimento';
import { podeVerIntegracoes } from './integracao';
import { useCacheDeConversas, useEstadoDaConexao, useNomesDosContatos, usePolling } from './hooks';

type Area = 'atendimento' | 'conhecimento' | 'integracoes';

interface AppProps {
  /** Para testes. Por padrão, o cliente real (via proxy `/api`). */
  api?: ApiClient;
  intervaloFilaMs?: number;
  intervaloConversaMs?: number;
  intervaloAlertasMs?: number;
}

export function App({ api: apiFornecida, intervaloFilaMs, intervaloConversaMs, intervaloAlertasMs }: AppProps) {
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

  return (
    <Atendimento
      api={api}
      saidaManual={saidaManual}
      intervaloFilaMs={intervaloFilaMs}
      intervaloConversaMs={intervaloConversaMs}
      intervaloAlertasMs={intervaloAlertasMs}
    />
  );
}

interface AtendimentoProps {
  api: ApiClient;
  saidaManual: { current: boolean };
  intervaloFilaMs?: number;
  intervaloConversaMs?: number;
  intervaloAlertasMs?: number;
}

interface ErroDeRede {
  texto: string;
  /** A API nem foi alcançada: vira a faixa de conexão, não uma linha de erro. */
  semConexao: boolean;
}

/** A área logada: cabeçalho, faixa de erros e as duas colunas (fila à esquerda, conversa à direita). */
function Atendimento({ api, saidaManual, intervaloFilaMs, intervaloConversaMs, intervaloAlertasMs = 15_000 }: AtendimentoProps) {
  const [me, setMe] = useState<CurrentUser | null>(null);
  const [erroAoCarregarUsuario, setErroAoCarregarUsuario] = useState<string | null>(null);
  const [tentativa, setTentativa] = useState(0);
  const [trocandoSenha, setTrocandoSenha] = useState(false);
  // A área "Conhecimento" (ADR 0030) é só do administrador do tenant. Aberta uma vez, fica montada (escondida) ao voltar ao Atendimento, para não perder o que ele escrevia.
  const [area, setArea] = useState<Area>('atendimento');
  const [conhecimentoAberto, setConhecimentoAberto] = useState(false);
  // A área "Integrações" (ADR 0035) é de administradores e gerentes; também fica montada depois de aberta.
  const [integracoesAberta, setIntegracoesAberta] = useState(false);
  const [itemSelecionado, setItemSelecionado] = useState<string | null>(null);
  const [versaoDaLista, setVersaoDaLista] = useState(0);
  const [errosDeRede, setErrosDeRede] = useState<Record<string, ErroDeRede>>({});
  const [erroDeAcao, setErroDeAcao] = useState<ErroDeRede | null>(null);
  const [alertasDeEnvio, setAlertasDeEnvio] = useState<SendingAlert[]>([]);
  const { nomes, garantir } = useNomesDosContatos(api);
  // Histórico e rascunhos de todas as conversas abertas nesta sessão: trocar de atendimento e voltar não perde nada.
  const escopo = useMemo(() => (me ? { tenant: obterTenant(), usuarioId: me.id } : undefined), [me]);
  const cache = useCacheDeConversas(escopo);
  const abertaRestaurada = useRef(false);

  // Recarregar a página devolve o atendente à conversa em que estava (a lista a reabre se ainda estiver ao alcance dele).
  useEffect(() => {
    if (escopo && !abertaRestaurada.current) {
      abertaRestaurada.current = true;
      const guardada = lerItemAberto(escopo);
      if (guardada) {
        setItemSelecionado(guardada);
      }
    }
  }, [escopo]);

  useEffect(() => {
    if (escopo && abertaRestaurada.current) {
      gravarItemAberto(escopo, itemSelecionado);
    }
  }, [escopo, itemSelecionado]);
  const semConexao = Object.values(errosDeRede).some((e) => e.semConexao);
  const { estado: estadoDaConexao, pulso } = useEstadoDaConexao(semConexao);

  useEffect(() => {
    let vivo = true;
    let temporizador: ReturnType<typeof setTimeout> | undefined;
    api
      .me()
      .then((usuario) => {
        if (vivo) {
          setErroAoCarregarUsuario(null);
          setMe(usuario);
        }
      })
      .catch((erro: unknown) => {
        if (vivo) {
          setErroAoCarregarUsuario(descreverErro(erro));
          if (ehFalhaDeConexao(erro)) {
            // API fora ao abrir a tela: tenta de novo sozinho (o botão "Tentar novamente" continua valendo).
            temporizador = setTimeout(() => setTentativa((t) => t + 1), intervaloFilaMs ?? 3000);
          }
        }
      });
    return () => {
      vivo = false;
      if (temporizador !== undefined) {
        clearTimeout(temporizador);
      }
    };
  }, [api, tentativa, intervaloFilaMs]);

  const aoErroDeRede = useCallback((origem: string, texto: string | null, semConexaoComApi = false) => {
    setErrosDeRede((atual) => {
      if (texto === null) {
        if (!(origem in atual)) {
          return atual;
        }
        const { [origem]: _removido, ...resto } = atual;
        return resto;
      }
      const anterior = atual[origem];
      return anterior?.texto === texto && anterior.semConexao === semConexaoComApi
        ? atual
        : { ...atual, [origem]: { texto, semConexao: semConexaoComApi } };
    });
  }, []);

  const aoErroDeAcao = useCallback((texto: string | null, semConexaoComApi = false) => setErroDeAcao(texto === null ? null : { texto, semConexao: semConexaoComApi }), []);

  // O erro de uma ação que falhou por falta de conexão perde o sentido quando a conexão volta: some sozinho.
  useEffect(() => {
    if (estadoDaConexao === 'restabelecida') {
      setErroDeAcao((atual) => (atual?.semConexao ? null : atual));
    }
  }, [estadoDaConexao]);

  const aoAlterar = useCallback(() => setVersaoDaLista((v) => v + 1), []);

  async function sair() {
    saidaManual.current = true;
    try {
      await api.logout();
    } catch {
      // Sem resposta do servidor, a sessão local sai do mesmo jeito (o token vence sozinho no servidor).
    }
    // Saída pedida pelo atendente: o que ele deixou guardado neste navegador (rascunhos, "visto até") sai junto, para o
    // próximo operador da máquina não herdar nada. A sessão que expira sozinha NÃO apaga: os rascunhos voltam no próximo login.
    cache.limparTudo();
    if (escopo) {
      gravarItemAberto(escopo, null);
    }
    encerrarSessaoLocal();
  }

  // O aviso de contas de WhatsApp pausadas é secundário: se a consulta falha (API fora, versão antiga), a tela segue sem erro e com o último aviso.
  usePolling(
    async (ativo) => {
      try {
        const lista = await api.listarAlertasDeEnvio();
        if (ativo()) {
          setAlertasDeEnvio(Array.isArray(lista) ? lista : []);
        }
      } catch {
        // mantém o último aviso conhecido
      }
    },
    intervaloAlertasMs,
    pulso,
  );

  // Vários carregamentos periódicos falham juntos quando a API cai: a faixa mostra cada mensagem uma vez só. Falha de
  // conexão não entra aqui: ela tem a faixa própria de reconexão.
  const avisos: Aviso[] = [
    ...[...new Set(Object.values(errosDeRede).filter((e) => !e.semConexao).map((e) => e.texto))].map((texto) => ({
      id: `rede-${texto}`,
      texto,
      dispensavel: false,
    })),
    ...(erroDeAcao ? [{ id: 'acao', texto: erroDeAcao.texto, dispensavel: true }] : []),
    ...(erroAoCarregarUsuario ? [{ id: 'usuario', texto: erroAoCarregarUsuario, dispensavel: false }] : []),
  ];

  function dispensar(id: string) {
    if (id === 'acao') {
      setErroDeAcao(null);
    }
  }

  const podeVerConhecimento = me !== null && ehAdministradorDoTenant(me);
  const podeVerIntegracao = me !== null && podeVerIntegracoes(me);
  const naAreaDeConhecimento = podeVerConhecimento && area === 'conhecimento';
  const naAreaDeIntegracoes = podeVerIntegracao && area === 'integracoes';
  const foraDoAtendimento = naAreaDeConhecimento || naAreaDeIntegracoes;

  function irParaArea(destino: Area) {
    setArea(destino);
    if (destino === 'conhecimento') {
      setConhecimentoAberto(true);
    }
    if (destino === 'integracoes') {
      setIntegracoesAberta(true);
    }
  }

  return (
    <div className="app">
      <header className="topo">
        <h1>{naAreaDeConhecimento ? 'ia.bimeta - Conhecimento' : naAreaDeIntegracoes ? 'ia.bimeta - Integrações' : 'ia.bimeta - Atendimento'}</h1>
        {(podeVerConhecimento || podeVerIntegracao) && (
          <nav className="areas" aria-label="Áreas do sistema">
            <button type="button" className="area-aba" aria-current={!foraDoAtendimento ? 'page' : undefined} onClick={() => irParaArea('atendimento')}>
              Atendimento
            </button>
            {podeVerConhecimento && (
              <button type="button" className="area-aba" aria-current={naAreaDeConhecimento ? 'page' : undefined} onClick={() => irParaArea('conhecimento')}>
                Conhecimento
              </button>
            )}
            {podeVerIntegracao && (
              <button type="button" className="area-aba" aria-current={naAreaDeIntegracoes ? 'page' : undefined} onClick={() => irParaArea('integracoes')}>
                Integrações
              </button>
            )}
          </nav>
        )}
        <div className="topo-usuario">
          {me && <span>{me.displayName}</span>}
          <button type="button" className="btn" aria-expanded={trocandoSenha} onClick={() => setTrocandoSenha((aberto) => !aberto)}>
            Trocar senha
          </button>
          <button type="button" className="btn" onClick={() => void sair()}>
            Sair
          </button>
        </div>
      </header>

      {trocandoSenha && <TrocarSenha api={api} onFechar={() => setTrocandoSenha(false)} />}

      <ConexaoBanner estado={estadoDaConexao} />
      <ErrorBanner avisos={avisos} onDispensar={dispensar} />
      <AlertasDeEnvio alertas={alertasDeEnvio} />

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
        <main className={itemSelecionado ? 'colunas com-selecao' : 'colunas'} hidden={foraDoAtendimento}>
          <QueuePanel
            api={api}
            me={me}
            itemSelecionado={itemSelecionado}
            onSelecionar={setItemSelecionado}
            nomes={nomes}
            garantirNomes={garantir}
            onErroDeRede={aoErroDeRede}
            onErroDeAcao={aoErroDeAcao}
            versaoDaLista={versaoDaLista + pulso}
            cache={cache}
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
              cache={cache}
              alertasDeEnvio={alertasDeEnvio}
              pulso={pulso}
              intervaloMs={intervaloConversaMs}
            />
          ) : (
            <section className="painel-conversa vazio" aria-label="Conversa">
              <p className="discreto centro">Escolha um atendimento na fila para ver a conversa.</p>
            </section>
          )}
        </main>
      )}

      {podeVerConhecimento && conhecimentoAberto && <Conhecimento api={api} oculto={!naAreaDeConhecimento} />}
      {podeVerIntegracao && integracoesAberta && me && <Integracoes api={api} me={me} oculto={!naAreaDeIntegracoes} />}
    </div>
  );
}
