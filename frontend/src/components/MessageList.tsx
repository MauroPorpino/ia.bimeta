import { useEffect, useRef } from 'react';
import type { Message } from '../api/types';
import { ROTULO_AUTOR, ROTULO_ENTREGA, corpoDaMensagem, horaCurta } from '../format';

interface Props {
  /** Em ordem cronológica (a mais antiga primeiro). */
  mensagens: Message[];
  nomeDoCliente?: string | null;
  /** Há mensagens mais antigas que não foram carregadas. */
  temMaisAntigas: boolean;
}

export function MessageList({ mensagens, nomeDoCliente, temMaisAntigas }: Props) {
  const lista = useRef<HTMLOListElement>(null);
  const noFim = useRef(true);

  // Só acompanha as mensagens novas se o atendente já estava no fim: quem rolou para ler o histórico não é puxado de volta.
  useEffect(() => {
    const el = lista.current;
    if (el && noFim.current) {
      el.scrollTop = el.scrollHeight;
    }
  }, [mensagens]);

  function aoRolar() {
    const el = lista.current;
    if (el) {
      noFim.current = el.scrollHeight - el.scrollTop - el.clientHeight < 48;
    }
  }

  return (
    <div className="mensagens">
      {temMaisAntigas && <p className="discreto centro">Mostrando as mensagens mais recentes.</p>}
      {mensagens.length === 0 && <p className="discreto centro">Nenhuma mensagem neste atendimento ainda.</p>}
      <ol ref={lista} onScroll={aoRolar} role="log" aria-live="polite" aria-relevant="additions" aria-label="Mensagens da conversa">
        {mensagens.map((m) => {
          const corpo = corpoDaMensagem(m);
          const saida = m.direction === 'Outbound';
          const autor = m.author === 'Customer' && nomeDoCliente ? nomeDoCliente : ROTULO_AUTOR[m.author];
          const incerto = m.deliveryState === 'SendUnknown';
          const falhou = m.deliveryState === 'Failed' || m.deliveryState === 'Expired' || m.deliveryState === 'Dead';
          return (
            <li
              key={m.id}
              className={`mensagem ${saida ? 'enviada' : 'recebida'} autor-${m.author.toLowerCase()}`}
              data-autor={m.author}
              data-direcao={m.direction}
            >
              <div className="mensagem-cabecalho">
                <span className="mensagem-autor">{autor}</span>
                <time dateTime={m.occurredAt}>{horaCurta(m.occurredAt)}</time>
              </div>
              <p className={corpo.semConteudo ? 'mensagem-texto sem-conteudo' : 'mensagem-texto'}>{corpo.texto}</p>
              {saida && m.deliveryState && (
                <p
                  className={`entrega ${incerto ? 'entrega-incerta' : falhou ? 'entrega-falha' : ''}`}
                  data-entrega={m.deliveryState}
                  title={incerto ? 'O envio terminou sem confirmação do provedor; a mensagem não é reenviada automaticamente.' : undefined}
                >
                  {ROTULO_ENTREGA[m.deliveryState]}
                </p>
              )}
            </li>
          );
        })}
      </ol>
    </div>
  );
}
