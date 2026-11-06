import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { ApiError, descreverErro, ehFalhaDeConexao, ehRejeicaoDefinitiva, novoGuid, type ApiClient } from '../api/client';
import type { CurrentUser, Message, QueueItem, SendingAlert } from '../api/types';
import { CacheDeConversas, aplicarPaginaAnterior, aplicarPaginaRecente } from '../cache';
import { rotuloSemNome, situacaoDoItem } from '../format';
import { usePolling } from '../hooks';
import { alertaDoNumero } from './AlertasDeEnvio';
import { MessageList } from './MessageList';

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
      if (explicadoPelaNota) {
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

  async function enviar(evento?: FormEvent) {
    evento?.preventDefault();
    const enviado = texto.trim();
    if (!enviado || !assumidoPorMim) {
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
            rows={3}
            placeholder="Escreva a resposta (Enter envia, Shift+Enter quebra a linha)"
          />
          <button type="submit" className="btn btn-primario" disabled={ocupado !== null || texto.trim() === ''}>
            {ocupado === 'reply' ? 'Enviando...' : 'Responder'}
          </button>
        </form>
      )}
    </section>
  );
}
