import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import type { Message } from '../api/types';
import type { PosicaoDeRolagem } from '../cache';
import { ROTULO_AUTOR, ROTULO_ENTREGA, corpoDaMensagem, horaCurta } from '../format';

interface Props {
  /** Em ordem cronológica (a mais antiga primeiro). */
  mensagens: Message[];
  nomeDoCliente?: string | null;
  /** Há mensagens mais antigas que não foram carregadas. */
  temMaisAntigas: boolean;
  carregandoAnteriores?: boolean;
  /** Texto do erro do último pedido de mensagens antigas (o botão vira "tentar de novo"). */
  erroAnteriores?: string | null;
  /** Pede a página de mensagens mais antigas (botão ou rolagem até o topo). */
  aoCarregarAnteriores?: () => void;
  /** Até que mensagem o atendente já tinha visto quando abriu a conversa: a primeira do cliente depois dela ganha o marcador "Novas mensagens". */
  vistoNaAbertura?: number;
  /** A última mensagem que o atendente VIU (ele está no fim da conversa, com a aba à vista). */
  aoVisto?: (seq: number) => void;
  /** Onde ele estava quando saiu desta conversa. */
  rolagemInicial?: PosicaoDeRolagem;
  aoMudarRolagem?: (posicao: PosicaoDeRolagem) => void;
}

/** Perto do fim (em pixels): ainda conta como "no fim" e acompanha as mensagens novas. */
const MARGEM_DO_FIM = 48;
/** Perto do topo: a rolagem até aqui pede as mensagens mais antigas sozinha. */
const MARGEM_DO_TOPO = 80;

export function MessageList({
  mensagens,
  nomeDoCliente,
  temMaisAntigas,
  carregandoAnteriores = false,
  erroAnteriores = null,
  aoCarregarAnteriores,
  vistoNaAbertura,
  aoVisto,
  rolagemInicial,
  aoMudarRolagem,
}: Props) {
  const lista = useRef<HTMLOListElement>(null);
  const noFim = useRef(rolagemInicial?.noFim ?? true);
  const posicionou = useRef(false);
  const anterior = useRef<{ primeiro: string | undefined; ultimo: string | undefined; altura: number } | null>(null);
  const [novas, setNovas] = useState(0);

  const ultimaSeq = mensagens.length > 0 ? Math.max(...mensagens.map((m) => m.messageSeq)) : 0;

  const marcarVisto = useCallback(() => {
    if (aoVisto && ultimaSeq > 0 && noFim.current && !document.hidden) {
      aoVisto(ultimaSeq);
    }
  }, [aoVisto, ultimaSeq]);

  // A rolagem acompanha o que mudou na lista: abre onde o atendente parou (ou no fim); mensagens ANTIGAS que entram no
  // começo não empurram o que ele lê (a posição é compensada); mensagens NOVAS no fim só o puxam se ele já estava no fim.
  // Se ele estava lendo mais acima, contam como "novas" e ficam num aviso que o leva ao fim.
  useLayoutEffect(() => {
    const el = lista.current;
    if (!el) {
      return;
    }
    const primeiro = mensagens[0]?.id;
    const ultimo = mensagens[mensagens.length - 1]?.id;
    const antes = anterior.current;

    if (!posicionou.current) {
      if (mensagens.length > 0) {
        posicionou.current = true;
        if (rolagemInicial && !rolagemInicial.noFim) {
          el.scrollTop = Math.max(0, el.scrollHeight - el.clientHeight - rolagemInicial.distanciaDoFim);
        } else {
          el.scrollTop = el.scrollHeight;
        }
      }
    } else if (antes) {
      const indiceDoAntigoPrimeiro = antes.primeiro === undefined ? -1 : mensagens.findIndex((m) => m.id === antes.primeiro);
      if (indiceDoAntigoPrimeiro > 0) {
        el.scrollTop += el.scrollHeight - antes.altura;
      }
      const indiceDoAntigoUltimo = antes.ultimo === undefined ? -1 : mensagens.findIndex((m) => m.id === antes.ultimo);
      if (ultimo !== antes.ultimo && indiceDoAntigoUltimo >= 0) {
        if (noFim.current) {
          el.scrollTop = el.scrollHeight;
        } else {
          const chegaramDoCliente = mensagens.slice(indiceDoAntigoUltimo + 1).filter((m) => m.direction === 'Inbound').length;
          if (chegaramDoCliente > 0) {
            setNovas((n) => n + chegaramDoCliente);
          }
        }
      }
    }
    anterior.current = { primeiro, ultimo, altura: el.scrollHeight };
    // A posição guardada acompanha o conteúdo que mudou (mensagem nova no fim, antigas no começo), não só os gestos de rolagem.
    if (posicionou.current) {
      aoMudarRolagem?.({ noFim: noFim.current, distanciaDoFim: Math.max(0, el.scrollHeight - el.scrollTop - el.clientHeight) });
    }
    // `rolagemInicial` só vale na primeira vez que há mensagens, por isso não é dependência.
  }, [mensagens]);

  useEffect(() => {
    marcarVisto();
    const aoVoltarAVer = () => {
      if (!document.hidden) {
        marcarVisto();
      }
    };
    document.addEventListener('visibilitychange', aoVoltarAVer);
    return () => document.removeEventListener('visibilitychange', aoVoltarAVer);
  }, [marcarVisto]);

  function aoRolar() {
    const el = lista.current;
    if (!el) {
      return;
    }
    const distancia = el.scrollHeight - el.scrollTop - el.clientHeight;
    const fim = distancia < MARGEM_DO_FIM;
    noFim.current = fim;
    aoMudarRolagem?.({ noFim: fim, distanciaDoFim: Math.max(0, distancia) });
    if (fim) {
      setNovas(0);
      marcarVisto();
    }
    if (el.scrollTop < MARGEM_DO_TOPO && temMaisAntigas && !carregandoAnteriores && !erroAnteriores) {
      aoCarregarAnteriores?.();
    }
  }

  function irParaOFim() {
    const el = lista.current;
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
    noFim.current = true;
    setNovas(0);
    marcarVisto();
  }

  // O marcador "Novas mensagens" vai antes da primeira mensagem do cliente que o atendente ainda não tinha visto ao abrir.
  const idDoMarcador =
    vistoNaAbertura === undefined ? undefined : mensagens.find((m) => m.direction === 'Inbound' && m.messageSeq > vistoNaAbertura)?.id;

  return (
    <div className="mensagens">
      {temMaisAntigas && (
        <div className="mais-antigas">
          {carregandoAnteriores ? (
            <p className="discreto centro" role="status">
              Carregando mensagens anteriores...
            </p>
          ) : (
            <>
              {erroAnteriores && <p className="erro-anteriores">{erroAnteriores}</p>}
              <button type="button" className="btn btn-pequeno" onClick={() => aoCarregarAnteriores?.()}>
                {erroAnteriores ? 'Tentar carregar de novo' : 'Carregar mensagens anteriores'}
              </button>
            </>
          )}
        </div>
      )}
      {mensagens.length === 0 && <p className="discreto centro">Nenhuma mensagem neste atendimento ainda.</p>}
      <ol ref={lista} onScroll={aoRolar} role="log" aria-live="polite" aria-relevant="additions" aria-label="Mensagens da conversa">
        {mensagens.map((m) => {
          const corpo = corpoDaMensagem(m);
          const saida = m.direction === 'Outbound';
          const autor = m.author === 'Customer' && nomeDoCliente ? nomeDoCliente : ROTULO_AUTOR[m.author];
          const incerto = m.deliveryState === 'SendUnknown';
          const falhou = m.deliveryState === 'Failed' || m.deliveryState === 'Expired' || m.deliveryState === 'Dead';
          return (
            <MensagemNaLista key={m.id} marcador={m.id === idDoMarcador}>
              <li
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
            </MensagemNaLista>
          );
        })}
      </ol>
      {novas > 0 && (
        <button type="button" className="btn btn-primario aviso-novas" onClick={irParaOFim}>
          {novas === 1 ? '1 mensagem nova' : `${novas} mensagens novas`} ↓
        </button>
      )}
    </div>
  );
}

/** Uma mensagem, precedida do marcador "Novas mensagens" quando for a primeira que o atendente não tinha visto. */
function MensagemNaLista({ marcador, children }: { marcador: boolean; children: ReactNode }) {
  return (
    <>
      {marcador && (
        <li role="separator" className="marcador-novas">
          Novas mensagens
        </li>
      )}
      {children}
    </>
  );
}
