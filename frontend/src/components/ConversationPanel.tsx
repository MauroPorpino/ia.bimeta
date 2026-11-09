import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { ApiError, descreverErro, ehFalhaDeConexao, ehRejeicaoDefinitiva, novoGuid, type ApiClient } from '../api/client';
import type { CurrentUser, Message, QueueItem, ReplyOptions, ReplyTemplate, SendingAlert, TemplateParameterError } from '../api/types';
import { CacheDeConversas, aplicarPaginaAnterior, aplicarPaginaRecente } from '../cache';
import { janelaDeResposta, rotuloSemNome, situacaoDoItem, tempoDecorrido } from '../format';
import { usePolling } from '../hooks';
import { alertaDoNumero } from './AlertasDeEnvio';
import { MessageList } from './MessageList';
import { ModeloDeMensagem } from './ModeloDeMensagem';

interface Props {
  api: ApiClient;
  itemId: string;
  me: CurrentUser;
  nomeDoContato?: string;
  /** Erro de carregamento periódico; `semConexao` = a API nem foi alcançada (a área logada mostra a faixa de reconexão). */
  onErroDeRede: (origem: string, texto: string | null, semConexao?: boolean) => void;
  /** Erro de uma ação do atendente; `semConexao`: a API nem foi alcançada (o aviso some sozinho quando a conexão volta). */
  onErroDeAcao: (texto: string | null, semConexao?: boolean) => void;
  /** Avisa que uma ação mudou o item (a lista da fila é relida na hora). */
  onAlterado: () => void;
  /** Só para telas estreitas: volta à lista. */
  onVoltar?: () => void;
  /**
   * Memória do histórico, da rolagem, do "visto até" e dos rascunhos, dona da área logada: trocar de conversa e voltar
   * encontra tudo como estava (e, com o armazenamento local, também depois de recarregar a página). Sem ela, o painel usa
   * uma memória própria (só dele). O chamador monta o painel com `key={itemId}`.
   */
  cache?: CacheDeConversas;
  /** Contas de WhatsApp com envio pausado ou esperando: avisa, ao lado do campo de resposta, quando o número desta conversa está afetado. */
  alertasDeEnvio?: SendingAlert[];
  /** Muda para forçar uma releitura imediata (a conexão voltou ou a aba reapareceu). */
  pulso?: number;
  intervaloMs?: number;
}

/** A releitura periódica traz só as mensagens mais recentes. */
const TAMANHO_DA_PAGINA = 100;
/** Cada pedido de mensagens anteriores traz esta quantidade. */
const TAMANHO_DA_PAGINA_ANTIGA = 50;
/** A janela de resposta e os modelos mudam devagar (a janela só reabre quando o cliente escreve, e isso já dispara uma releitura). */
const INTERVALO_DAS_OPCOES_MS = 30_000;

type Acao = 'claim' | 'release' | 'return-to-ai' | 'reply' | 'close';

/** O texto de um conflito (409), conforme o código que a API informa. O sistema às vezes está só terminando uma operação: isso NÃO é "outro atendente assumiu". */
function notaDoConflito(acao: Acao, codigo: string | null): string {
  switch (codigo) {
    case 'OperationInProgress':
      return 'A conversa ainda está sendo atualizada pelo sistema. Aguarde um instante e tente de novo.';
    case 'ItemClosed':
      return 'Este atendimento já foi encerrado. A conversa foi atualizada.';
    case 'AlreadyAssigned':
      return acao === 'claim'
        ? 'Outro atendente assumiu este atendimento antes de você. A conversa foi atualizada.'
        : 'Outro atendente é o responsável por este atendimento. A conversa foi atualizada.';
    default:
      return 'O atendimento mudou enquanto você agia. A conversa foi atualizada: confira antes de repetir a ação.';
  }
}

/** Como a ação terminou: provou que não teve efeito (`rejeitada`), concluiu, ou o resultado é incerto (rede, 5xx, ainda não confirmada). */
type Situacao = 'concluida' | 'rejeitada' | 'incerta';

export function ConversationPanel({
  api,
  itemId,
  me,
  nomeDoContato,
  onErroDeRede,
  onErroDeAcao,
  onAlterado,
  onVoltar,
  cache,
  alertasDeEnvio,
  pulso = 0,
  intervaloMs = 2000,
}: Props) {
  const memoriaPropria = useRef<CacheDeConversas | null>(null);
  memoriaPropria.current ??= new CacheDeConversas();
  const memoria = cache ?? memoriaPropria.current;

  // O que já se sabe desta conversa aparece na hora; o polling só atualiza.
  const [item, setItem] = useState<QueueItem | null>(() => memoria.ler(itemId)?.item ?? null);
  const [mensagens, setMensagens] = useState<Message[]>(() => memoria.ler(itemId)?.mensagens ?? []);
  const [cursorAnterior, setCursorAnterior] = useState<string | null>(() => memoria.ler(itemId)?.cursorAnterior ?? null);
  const [carregandoAnteriores, setCarregandoAnteriores] = useState(false);
  const [erroAnteriores, setErroAnteriores] = useState<string | null>(null);
  const [indisponivel, setIndisponivel] = useState(false);
  const [versao, setVersao] = useState(0);
  // Até onde o atendente tinha visto e onde estava na rolagem quando abriu esta conversa.
  const [vistoNaAbertura] = useState(() => memoria.lerVisto(itemId));
  const [rolagemInicial] = useState(() => memoria.lerRolagem(itemId));

  const [texto, setTexto] = useState(() => memoria.lerRascunho(itemId));
  const [ocupado, setOcupado] = useState<Acao | null>(null);
  const [confirmandoEncerrar, setConfirmandoEncerrar] = useState(false);
  const [nota, setNota] = useState<string | null>(null);
  const [copiado, setCopiado] = useState(false);
  // Resposta por modelo de mensagem aprovado (ADR 0027): a janela de 24 horas, os modelos do número e os erros de parâmetro do último envio.
  const [opcoes, setOpcoes] = useState<ReplyOptions | null>(null);
  const [versaoDasOpcoes, setVersaoDasOpcoes] = useState(0);
  const [agora, setAgora] = useState(() => Date.now());
  const [mostrandoModelos, setMostrandoModelos] = useState(false);
  const [errosDoModelo, setErrosDoModelo] = useState<TemplateParameterError[]>([]);
  // O estado `ocupado` só muda no próximo render: este ref fecha a janela de um segundo clique no mesmo instante.
  const emAndamento = useRef(false);
  const buscandoAnteriores = useRef(false);
  // Para avisar a fila UMA vez quando a conversa sai do alcance (e não a cada ciclo de polling).
  const indisponivelAntes = useRef(false);
  const montado = useRef(true);
  useEffect(() => {
    montado.current = true;
    return () => {
      montado.current = false;
    };
  }, []);

  usePolling(
    async (ativo) => {
      try {
        const [itemAtual, pagina] = await Promise.all([
          api.obterItem(itemId),
          api.obterMensagens(itemId, { take: TAMANHO_DA_PAGINA, order: 'NewestFirst' }),
        ]);
        if (ativo()) {
          // A API devolve do mais novo para o mais antigo; a tela mostra em ordem cronológica, sem duplicar o que já tinha.
          const nova = aplicarPaginaRecente(memoria.ler(itemId), itemAtual, pagina);
          memoria.gravar(itemId, nova);
          setItem(nova.item);
          setMensagens(nova.mensagens);
          setCursorAnterior(nova.cursorAnterior);
          indisponivelAntes.current = false;
          setIndisponivel(false);
          onErroDeRede('conversa', null);
        }
      } catch (erro) {
        if (!ativo()) {
          return;
        }
        if (erro instanceof ApiError && (erro.status === 404 || erro.status === 403)) {
          // Fora do alcance: o atendimento terminou, outro atendente o assumiu (para quem não é o responsável a leitura passa a
          // ser negada) ou o acesso mudou. Não mostra mais a conversa nem as ações, esquece o histórico guardado e deixa a
          // fila se atualizar uma vez. O texto que o atendente digitou NÃO é apagado (continua guardado e visível). Se a
          // conversa voltar a ficar disponível (devolvida à fila), a próxima leitura a traz de novo.
          memoria.esquecer(itemId);
          setItem(null);
          setMensagens([]);
          setCursorAnterior(null);
          onErroDeRede('conversa', null);
          if (!indisponivelAntes.current) {
            indisponivelAntes.current = true;
            setIndisponivel(true);
            onAlterado();
          }
        } else {
          onErroDeRede('conversa', descreverErro(erro), ehFalhaDeConexao(erro));
        }
      }
    },
    intervaloMs,
    `${itemId}|${versao}|${pulso}`,
  );

  /** Carrega a página de mensagens mais antigas (pelo cursor da API) e a junta ao histórico, sem repetir nenhuma. */
  async function carregarAnteriores() {
    const entrada = memoria.ler(itemId);
    const cursor = entrada?.cursorAnterior;
    if (!entrada || !cursor || buscandoAnteriores.current) {
      return;
    }
    buscandoAnteriores.current = true;
    setCarregandoAnteriores(true);
    setErroAnteriores(null);
    try {
      const pagina = await api.obterMensagens(itemId, { take: TAMANHO_DA_PAGINA_ANTIGA, order: 'NewestFirst', cursor });
      const atual = memoria.ler(itemId);
      if (atual) {
        const nova = aplicarPaginaAnterior(atual, pagina);
        memoria.gravar(itemId, nova);
        if (montado.current) {
          setMensagens(nova.mensagens);
          setCursorAnterior(nova.cursorAnterior);
        }
      }
    } catch (erro) {
      if (montado.current) {
        setErroAnteriores(
          ehFalhaDeConexao(erro) ? 'Sem conexão: não foi possível carregar as mensagens anteriores.' : `Não foi possível carregar as mensagens anteriores. ${descreverErro(erro)}`,
        );
      }
    } finally {
      buscandoAnteriores.current = false;
      if (montado.current) {
        setCarregandoAnteriores(false);
      }
    }
  }

  function mudarTexto(novo: string) {
    setTexto(novo);
    memoria.gravarRascunho(itemId, novo);
  }

  async function executar(
    acao: Acao,
    chamar: () => Promise<{ outcome?: string; item?: QueueItem } | undefined>,
    aoTerminar?: (situacao: Situacao) => void,
    /** Trata um erro que a própria tela explica (devolve verdadeiro): então não vai para a faixa de erro vermelha. */
    tratarErro?: (erro: unknown) => boolean,
  ): Promise<boolean> {
    if (emAndamento.current) {
      return false;
    }
    emAndamento.current = true;
    setOcupado(acao);
    setNota(null);
    onErroDeAcao(null);
    try {
      const resultado = await chamar();
      if (resultado?.outcome === 'Pending') {
        aoTerminar?.('incerta');
        setNota('A operação foi registrada, mas o servidor ainda não a confirmou. Repita a ação para concluir.');
        return false;
      }
      aoTerminar?.('concluida');
      if (resultado?.outcome === 'SessionEnded') {
        setNota('A sessão terminou: o atendimento foi encerrado.');
      }
      if (resultado?.item) {
        setItem(resultado.item);
      }
      setVersao((v) => v + 1);
      onAlterado();
      return true;
    } catch (erro) {
      aoTerminar?.(ehRejeicaoDefinitiva(erro) ? 'rejeitada' : 'incerta');
      const conflito = erro instanceof ApiError && erro.status === 409;
      // Um conflito que a nota abaixo explica não é falha: sem faixa de erro vermelha. Só quem de fato perdeu a corrida ao assumir ouve
      // "outro atendente assumiu"; os outros conflitos (a conversa ainda entrando na fila, operação em andamento, encerrada) têm texto próprio.
      const explicadoPelaNota = conflito && (erro.code === 'AlreadyAssigned' || erro.code === 'OperationInProgress' || erro.code === 'ItemClosed');
      if (explicadoPelaNota || tratarErro?.(erro) === true) {
        onErroDeAcao(null);
      } else {
        onErroDeAcao(descreverErro(erro), ehFalhaDeConexao(erro));
      }
      if (conflito) {
        // Conflito: outra operação (de outro atendente ou do sistema) agiu primeiro. Mostra o que aconteceu e relê o estado real.
        setNota(notaDoConflito(acao, erro.code));
        setVersao((v) => v + 1);
        onAlterado();
      } else if (erro instanceof ApiError && erro.status === 403) {
        // Sem permissão agora: em geral o atendimento passou para outra pessoa. Relê para a tela mostrar quem está com ele.
        setVersao((v) => v + 1);
        onAlterado();
      }
      return false;
    } finally {
      emAndamento.current = false;
      setOcupado(null);
    }
  }

  const assumidoPorMim = item?.status === 'Assigned' && item.assignedUserId === me.id;
  const aguardando = item?.status === 'Waiting';
  // A entrada na fila é operação do SISTEMA (a transferência da IA): nada para o atendente repetir, e assumir agora seria recusado.
  const entrandoNaFila = item?.pending?.kind === 'Enter';
  const nome = nomeDoContato ?? rotuloSemNome(itemId);
  const alertaDeEnvio = alertaDoNumero(alertasDeEnvio, mensagens[mensagens.length - 1]?.channelInstanceId);
  const temTextoGuardado = texto.trim() !== '';
  // Texto digitado numa conversa em que o atendente já não pode responder (outro a assumiu, foi encerrada, saiu do alcance):
  // continua guardado e visível, nunca some junto com o campo.
  const mostrarTextoGuardado = temTextoGuardado && !assumidoPorMim && (item !== null || indisponivel);

  // A janela de resposta livre do provedor (24 horas no WhatsApp, contada da última mensagem do CLIENTE). Sem as opções (a consulta falhou ou o canal não as
  // conhece), a tela segue como antes e o servidor continua decidindo.
  const janela = janelaDeResposta(opcoes, agora);
  const modelos = opcoes?.templates ?? [];
  const ultimaEntrada = mensagens.reduce((maior, m) => (m.direction === 'Inbound' && m.messageSeq > maior ? m.messageSeq : maior), 0);

  // As opções são relidas quando o cliente escreve (a janela reabre), quando o servidor recusa por causa dela e a cada 30 s. Aviso secundário: sem elas, a tela não mostra erro.
  usePolling(
    async (ativo) => {
      if (!assumidoPorMim) {
        return;
      }
      try {
        const lidas = await api.obterOpcoesDeResposta(itemId);
        if (ativo()) {
          setOpcoes(lidas);
          setAgora(Date.now());
        }
      } catch {
        // mantém o que já se sabia
      }
    },
    INTERVALO_DAS_OPCOES_MS,
    `${itemId}|${assumidoPorMim}|${ultimaEntrada}|${versaoDasOpcoes}|${pulso}`,
  );

  // A janela fecha com o passar do tempo, sem esperar a próxima releitura.
  useEffect(() => {
    const relogio = setInterval(() => setAgora(Date.now()), 30_000);
    return () => clearInterval(relogio);
  }, []);

  /** Erros que a tela explica ao lado do campo (ou numa nota), sem a faixa de erro vermelha: a janela fechou, o modelo saiu do ar ou os parâmetros são inválidos. */
  function tratarErroDeResposta(erro: unknown): boolean {
    if (!(erro instanceof ApiError) || erro.status !== 422) {
      return false;
    }
    switch (erro.code) {
      case 'ReplyWindowClosed':
        setNota('A janela de resposta livre deste cliente fechou. Envie um modelo de mensagem aprovado.');
        setVersaoDasOpcoes((v) => v + 1);
        return true;
      case 'ReplyTemplateUnavailable':
        setNota('O modelo escolhido não está mais disponível para este número. A lista foi atualizada: escolha outro.');
        setVersaoDasOpcoes((v) => v + 1);
        return true;
      case 'TemplateParametersInvalid':
        setErrosDoModelo(erro.erros.length > 0 ? erro.erros : [{ key: '*', code: 'invalid', message: erro.detail ?? 'Os campos do modelo são inválidos.' }]);
        return true;
      default:
        return false;
    }
  }

  async function enviarModelo(modelo: ReplyTemplate, parametros: Record<string, string>): Promise<boolean> {
    if (!assumidoPorMim) {
      return false;
    }
    setErrosDoModelo([]);
    const ok = await executar('reply', () => api.responderComModelo(itemId, modelo.id, parametros), undefined, tratarErroDeResposta);
    if (ok && montado.current) {
      setMostrandoModelos(false);
    }
    return ok;
  }

  async function enviar(evento?: FormEvent) {
    evento?.preventDefault();
    const enviado = texto.trim();
    if (!enviado || !assumidoPorMim || !janela.aberta) {
      return;
    }
    // O envio que ficou sem resultado (queda da conexão, página recarregada no meio) deixou o requestId guardado: o MESMO
    // texto reenviado o reaproveita, e o servidor reconhece o pedido em vez de mandar a mensagem duas vezes.
    const guardado = memoria.lerPendente(itemId);
    const requestId = guardado && guardado.texto === enviado ? guardado.requestId : novoGuid();
    memoria.gravarPendente(itemId, { requestId, texto: enviado });
    const ok = await executar(
      'reply',
      () => api.responder(itemId, enviado, requestId),
      (situacao) => {
        if (situacao !== 'incerta') {
          memoria.gravarPendente(itemId, null);
        }
      },
      tratarErroDeResposta,
    );
    if (ok && memoria.lerRascunho(itemId).trim() === enviado) {
      // Só limpa depois do sucesso, e só se o atendente não editou o texto enquanto esperava. O rascunho guardado é a
      // fonte da verdade: vale também quando o atendente trocou de conversa no meio do envio e este painel já saiu da tela.
      memoria.gravarRascunho(itemId, '');
      if (montado.current) {
        setTexto('');
      }
    }
  }

  function aoTeclar(evento: KeyboardEvent<HTMLTextAreaElement>) {
    if (evento.key === 'Enter' && !evento.shiftKey && !evento.nativeEvent.isComposing) {
      evento.preventDefault();
      void enviar();
    }
  }

  async function encerrar() {
    const ok = await executar('close', () => api.encerrar(itemId, 'resolved'));
    if (ok) {
      // Encerrado por ele mesmo: o rascunho (que a confirmação avisou que seria descartado) e o resto da memória da conversa saem.
      memoria.descartarConversa(itemId);
      setTexto('');
      setConfirmandoEncerrar(false);
    }
  }

  async function copiarTexto() {
    try {
      await navigator.clipboard.writeText(texto);
      setCopiado(true);
    } catch {
      setCopiado(false);
    }
  }

  return (
    <section className="painel-conversa" aria-label="Conversa">
      <header className="conversa-cabecalho">
        {onVoltar && (
          <button type="button" className="btn btn-voltar" onClick={onVoltar}>
            Fila
          </button>
        )}
        <div className="conversa-titulo">
          <h2>{nome}</h2>
          {item && <p className="discreto">{situacaoDoItem(item, me.id)}</p>}
          {item && item.deliveryFailures > 0 && (
            <p className="aviso-falha" role="alert">
              {item.deliveryFailures === 1 ? 'Uma mensagem não foi enviada ao cliente.' : `${item.deliveryFailures} mensagens não foram enviadas ao cliente.`} Responda de novo.
            </p>
          )}
        </div>
        <div className="acoes">
          {aguardando && (
            <button
              type="button"
              className="btn btn-primario"
              disabled={ocupado !== null || entrandoNaFila}
              title={entrandoNaFila ? 'A conversa ainda está entrando na fila.' : undefined}
              onClick={() => void executar('claim', () => api.assumir(itemId))}
            >
              {ocupado === 'claim' ? 'Assumindo...' : 'Assumir'}
            </button>
          )}
          {assumidoPorMim && !confirmandoEncerrar && (
            <>
              <button type="button" className="btn" disabled={ocupado !== null} onClick={() => void executar('release', () => api.devolverParaFila(itemId))}>
                {ocupado === 'release' ? 'Devolvendo...' : 'Devolver à fila'}
              </button>
              <button type="button" className="btn" disabled={ocupado !== null} onClick={() => void executar('return-to-ai', () => api.devolverParaIa(itemId))}>
                {ocupado === 'return-to-ai' ? 'Devolvendo...' : 'Devolver à IA'}
              </button>
              <button type="button" className="btn btn-perigo" disabled={ocupado !== null} onClick={() => setConfirmandoEncerrar(true)}>
                Encerrar
              </button>
            </>
          )}
          {assumidoPorMim && confirmandoEncerrar && (
            <span className="confirmacao" role="group" aria-label="Confirmar encerramento">
              <span>{temTextoGuardado ? 'Encerrar este atendimento? O texto não enviado será descartado.' : 'Encerrar este atendimento?'}</span>
              <button type="button" className="btn btn-perigo" disabled={ocupado !== null} onClick={() => void encerrar()}>
                {ocupado === 'close' ? 'Encerrando...' : 'Confirmar encerramento'}
              </button>
              <button type="button" className="btn" disabled={ocupado !== null} onClick={() => setConfirmandoEncerrar(false)}>
                Cancelar
              </button>
            </span>
          )}
        </div>
      </header>

      {item?.status === 'Assigned' && !assumidoPorMim && <p className="aviso">Este atendimento está com outro atendente.</p>}
      {item?.status === 'Closed' && <p className="aviso">Este atendimento foi encerrado.</p>}
      {item?.pending && (
        <p className="aviso" role="status">
          {entrandoNaFila
            ? 'A conversa está entrando na fila. Em instantes você poderá assumi-la.'
            : 'Há uma operação aguardando confirmação do servidor. Repita a ação para concluir.'}
        </p>
      )}
      {nota && (
        <p className="aviso" role="status">
          {nota}
        </p>
      )}
      {alertaDeEnvio && (
        <p className={alertaDeEnvio.paused ? 'aviso aviso-envio pausada' : 'aviso aviso-envio'} role="status">
          {alertaDeEnvio.paused
            ? 'O envio por este número do WhatsApp está pausado. Sua resposta ficará aguardando e pode expirar sem chegar ao cliente.'
            : 'Este número do WhatsApp está com o envio mais lento agora. Sua resposta pode demorar a chegar.'}
        </p>
      )}

      {indisponivel ? (
        <p className="discreto centro">Este atendimento não está mais disponível para você (encerrado, assumido por outro atendente ou fora do seu alcance).</p>
      ) : (
        <MessageList
          mensagens={mensagens}
          nomeDoCliente={nomeDoContato}
          temMaisAntigas={cursorAnterior !== null}
          carregandoAnteriores={carregandoAnteriores}
          erroAnteriores={erroAnteriores}
          aoCarregarAnteriores={() => void carregarAnteriores()}
          vistoNaAbertura={vistoNaAbertura}
          aoVisto={(seq) => memoria.gravarVisto(itemId, seq)}
          rolagemInicial={rolagemInicial}
          aoMudarRolagem={(posicao) => memoria.gravarRolagem(itemId, posicao)}
        />
      )}

      {mostrarTextoGuardado && (
        <section className="texto-guardado" aria-label="Texto não enviado">
          <p>Você tinha um texto não enviado nesta conversa. Ele continua guardado neste navegador; se ela voltar a ser sua, é só continuar.</p>
          <textarea readOnly value={texto} rows={3} aria-label="Texto guardado" />
          <div className="acoes">
            <button type="button" className="btn" onClick={() => void copiarTexto()}>
              {copiado ? 'Copiado' : 'Copiar texto'}
            </button>
            <button type="button" className="btn" onClick={() => mudarTexto('')}>
              Descartar texto
            </button>
          </div>
        </section>
      )}

      {assumidoPorMim && !janela.aberta && (
        <section className="janela-fechada" role="status" aria-label="Janela de 24 horas fechada">
          <strong>Janela de {janela.horas ?? 24} horas fechada.</strong>
          <p>
            A última mensagem do cliente foi {opcoes?.lastInboundAt ? tempoDecorrido(opcoes.lastInboundAt, agora) : 'há muito tempo'}. O WhatsApp só permite iniciar ou retomar a
            conversa com um <b>modelo de mensagem aprovado</b>: o texto livre fica bloqueado até o cliente escrever de novo.
          </p>
          {modelos.length === 0 && (
            <p className="aviso">
              Nenhum modelo aprovado está associado a este número. Peça ao administrador para associar os modelos da Meta à conta do WhatsApp.
            </p>
          )}
        </section>
      )}

      {assumidoPorMim && !janela.aberta && modelos.length > 0 && (
        <ModeloDeMensagem modelos={modelos} ocupado={ocupado === 'reply'} errosDoServidor={errosDoModelo} aoEnviar={enviarModelo} aoEditar={() => setErrosDoModelo([])} />
      )}

      {assumidoPorMim && (
        <form className="compositor" onSubmit={(e) => void enviar(e)}>
          <label className="sr-only" htmlFor="texto-resposta">
            Resposta ao cliente
          </label>
          <textarea
            id="texto-resposta"
            value={texto}
            onChange={(e) => mudarTexto(e.target.value)}
            onKeyDown={aoTeclar}
            readOnly={ocupado === 'reply'}
            disabled={!janela.aberta}
            rows={3}
            placeholder={
              janela.aberta ? 'Escreva a resposta (Enter envia, Shift+Enter quebra a linha)' : 'Texto livre indisponível: a janela de 24 horas está fechada. Use um modelo de mensagem.'
            }
          />
          <button type="submit" className="btn btn-primario" disabled={ocupado !== null || texto.trim() === '' || !janela.aberta}>
            {ocupado === 'reply' ? 'Enviando...' : 'Responder'}
          </button>
        </form>
      )}

      {assumidoPorMim && janela.aberta && modelos.length > 0 && (
        <div className="modelos-alternar">
          <button type="button" className="btn btn-pequeno" aria-expanded={mostrandoModelos} disabled={ocupado !== null} onClick={() => setMostrandoModelos((aberto) => !aberto)}>
            {mostrandoModelos ? 'Ocultar modelos de mensagem' : 'Enviar modelo de mensagem'}
          </button>
          {janela.fechaEm && (
            <span className="discreto">
              Resposta livre até {new Date(janela.fechaEm).toLocaleString('pt-BR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}.
            </span>
          )}
        </div>
      )}
      {assumidoPorMim && janela.aberta && mostrandoModelos && modelos.length > 0 && (
        <ModeloDeMensagem modelos={modelos} ocupado={ocupado === 'reply'} errosDoServidor={errosDoModelo} aoEnviar={enviarModelo} aoEditar={() => setErrosDoModelo([])} />
      )}
    </section>
  );
}
