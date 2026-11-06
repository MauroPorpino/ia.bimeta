import { useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { ApiError, descreverErro, type ApiClient } from '../api/client';
import type { CurrentUser, Message, QueueItem } from '../api/types';
import { rotuloSemNome, situacaoDoItem } from '../format';
import { usePolling } from '../hooks';
import { MessageList } from './MessageList';

interface Props {
  api: ApiClient;
  itemId: string;
  me: CurrentUser;
  nomeDoContato?: string;
  onErroDeRede: (origem: string, texto: string | null) => void;
  onErroDeAcao: (texto: string | null) => void;
  /** Avisa que uma ação mudou o item (a lista da fila é relida na hora). */
  onAlterado: () => void;
  /** Só para telas estreitas: volta à lista. */
  onVoltar?: () => void;
  intervaloMs?: number;
}

const TAMANHO_DA_PAGINA = 100;

type Acao = 'claim' | 'release' | 'return-to-ai' | 'reply' | 'close';

export function ConversationPanel({ api, itemId, me, nomeDoContato, onErroDeRede, onErroDeAcao, onAlterado, onVoltar, intervaloMs = 2000 }: Props) {
  const [item, setItem] = useState<QueueItem | null>(null);
  const [mensagens, setMensagens] = useState<Message[]>([]);
  const [temMais, setTemMais] = useState(false);
  const [indisponivel, setIndisponivel] = useState(false);
  const [versao, setVersao] = useState(0);

  const [texto, setTexto] = useState('');
  const [ocupado, setOcupado] = useState<Acao | null>(null);
  const [confirmandoEncerrar, setConfirmandoEncerrar] = useState(false);
  const [nota, setNota] = useState<string | null>(null);
  // O estado `ocupado` só muda no próximo render: este ref fecha a janela de um segundo clique no mesmo instante.
  const emAndamento = useRef(false);

  usePolling(
    async (ativo) => {
      try {
        const [itemAtual, pagina] = await Promise.all([
          api.obterItem(itemId),
          api.obterMensagens(itemId, { take: TAMANHO_DA_PAGINA, order: 'NewestFirst' }),
        ]);
        if (ativo()) {
          setItem(itemAtual);
          // A API devolve do mais novo para o mais antigo; a tela mostra em ordem cronológica.
          setMensagens([...pagina.items].sort((a, b) => a.messageSeq - b.messageSeq));
          setTemMais(pagina.nextCursor !== null);
          setIndisponivel(false);
          onErroDeRede('conversa', null);
        }
      } catch (erro) {
        if (!ativo()) {
          return;
        }
        if (erro instanceof ApiError && erro.status === 404) {
          // Fora do alcance, ou o atendimento terminou e o histórico deixou de valer para este usuário.
          setIndisponivel(true);
          onErroDeRede('conversa', null);
        } else {
          onErroDeRede('conversa', descreverErro(erro));
        }
      }
    },
    intervaloMs,
    `${itemId}|${versao}`,
  );

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
      onErroDeAcao(descreverErro(erro));
      if (erro instanceof ApiError && erro.status === 409) {
        // Conflito (por exemplo, outro atendente assumiu): reler o estado real.
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

  async function enviar(evento?: FormEvent) {
    evento?.preventDefault();
    const enviado = texto.trim();
    if (!enviado || !assumidoPorMim) {
      return;
    }
    const ok = await executar('reply', () => api.responder(itemId, enviado));
    if (ok) {
      // Só limpa depois do sucesso, e só se o atendente não editou o texto enquanto esperava.
      setTexto((atual) => (atual.trim() === enviado ? '' : atual));
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

      {indisponivel ? (
        <p className="discreto centro">Este atendimento não está mais disponível (encerrado ou fora do seu alcance).</p>
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
            onChange={(e) => setTexto(e.target.value)}
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
