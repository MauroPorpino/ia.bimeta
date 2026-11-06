import { useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { ApiError, descreverErro, ehFalhaDeConexao, type ApiClient } from '../api/client';
import type { CurrentUser, Message, QueueItem, SendingAlert } from '../api/types';
import { CacheDeConversas, mesclarMensagens } from '../cache';
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
   * Memória do histórico e dos rascunhos, dona da área logada: trocar de conversa e voltar encontra tudo como estava.
   * Sem ela, o painel usa uma memória própria (só dele). O chamador monta o painel com `key={itemId}`.
   */
  cache?: CacheDeConversas;
  /** Contas de WhatsApp com envio pausado ou esperando: avisa, ao lado do campo de resposta, quando o número desta conversa está afetado. */
  alertasDeEnvio?: SendingAlert[];
  /** Muda para forçar uma releitura imediata (a conexão voltou ou a aba reapareceu). */
  pulso?: number;
  intervaloMs?: number;
}

const TAMANHO_DA_PAGINA = 100;

type Acao = 'claim' | 'release' | 'return-to-ai' | 'reply' | 'close';

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
  const [temMais, setTemMais] = useState(() => memoria.ler(itemId)?.temMais ?? false);
  const [indisponivel, setIndisponivel] = useState(false);
  const [versao, setVersao] = useState(0);

  const [texto, setTexto] = useState(() => memoria.lerRascunho(itemId));
  const [ocupado, setOcupado] = useState<Acao | null>(null);
  const [confirmandoEncerrar, setConfirmandoEncerrar] = useState(false);
  const [nota, setNota] = useState<string | null>(null);
  // O estado `ocupado` só muda no próximo render: este ref fecha a janela de um segundo clique no mesmo instante.
  const emAndamento = useRef(false);
  // Para avisar a fila UMA vez quando a conversa sai do alcance (e não a cada ciclo de polling).
  const indisponivelAntes = useRef(false);

  usePolling(
    async (ativo) => {
      try {
        const [itemAtual, pagina] = await Promise.all([
          api.obterItem(itemId),
          api.obterMensagens(itemId, { take: TAMANHO_DA_PAGINA, order: 'NewestFirst' }),
        ]);
        if (ativo()) {
          // A API devolve do mais novo para o mais antigo; a tela mostra em ordem cronológica, sem duplicar o que já tinha.
          const anterior = memoria.ler(itemId);
          const todas = mesclarMensagens(anterior?.mensagens ?? [], pagina.items);
          const temMaisAntigas = pagina.nextCursor !== null;
          memoria.gravar(itemId, { item: itemAtual, mensagens: todas, temMais: temMaisAntigas });
          setItem(itemAtual);
          setMensagens(todas);
          setTemMais(temMaisAntigas);
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
          // fila se atualizar uma vez. Se ela voltar a ficar disponível (devolvida à fila), a próxima leitura a traz de novo.
          memoria.esquecer(itemId);
          setItem(null);
          setMensagens([]);
          setTemMais(false);
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

  function mudarTexto(novo: string) {
    setTexto(novo);
    memoria.gravarRascunho(itemId, novo);
  }

  async function executar(acao: Acao, chamar: () => Promise<{ outcome?: string; item?: QueueItem } | undefined>): Promise<boolean> {
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
        setNota('A operação foi registrada, mas o servidor ainda não a confirmou. Repita a ação para concluir.');
        return false;
      }
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
      const conflitoAoAssumir = acao === 'claim' && erro instanceof ApiError && erro.status === 409;
      // Perder a corrida ao assumir não é falha: a nota abaixo explica, sem faixa de erro vermelha.
      if (conflitoAoAssumir) {
        onErroDeAcao(null);
      } else {
        onErroDeAcao(descreverErro(erro), ehFalhaDeConexao(erro));
      }
      if (erro instanceof ApiError && erro.status === 409) {
        // Conflito: outro atendente (ou o sistema) agiu primeiro. Mostra o que aconteceu e relê o estado real.
        setNota(
          conflitoAoAssumir
            ? 'Outro atendente assumiu este atendimento antes de você. A conversa foi atualizada.'
            : 'O atendimento mudou enquanto você agia. A conversa foi atualizada: confira antes de repetir a ação.',
        );
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
  const nome = nomeDoContato ?? rotuloSemNome(itemId);
  const alertaDeEnvio = alertaDoNumero(alertasDeEnvio, mensagens[mensagens.length - 1]?.channelInstanceId);

  async function enviar(evento?: FormEvent) {
    evento?.preventDefault();
    const enviado = texto.trim();
    if (!enviado || !assumidoPorMim) {
      return;
    }
    const ok = await executar('reply', () => api.responder(itemId, enviado));
    if (ok && memoria.lerRascunho(itemId).trim() === enviado) {
      // Só limpa depois do sucesso, e só se o atendente não editou o texto enquanto esperava. O rascunho guardado é a
      // fonte da verdade: vale também quando o atendente trocou de conversa no meio do envio e este painel já saiu da tela.
      memoria.gravarRascunho(itemId, '');
      setTexto('');
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
      setConfirmandoEncerrar(false);
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
            <button type="button" className="btn btn-primario" disabled={ocupado !== null} onClick={() => void executar('claim', () => api.assumir(itemId))}>
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
              <span>Encerrar este atendimento?</span>
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
          Há uma operação aguardando confirmação do servidor. Repita a ação para concluir.
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
        <MessageList mensagens={mensagens} nomeDoCliente={nomeDoContato} temMaisAntigas={temMais} />
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
