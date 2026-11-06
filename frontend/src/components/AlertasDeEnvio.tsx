import type { SendingAlert } from '../api/types';

interface Props {
  alertas: SendingAlert[];
}

/**
 * Contas de WhatsApp com o envio pausado ou esperando o provedor, para o atendente entender por que a resposta demora.
 * Pausada é o caso grave (nada sai até o administrador retomar); esperando é só lentidão passageira.
 */
export function AlertasDeEnvio({ alertas }: Props) {
  if (alertas.length === 0) {
    return null;
  }
  return (
    <div className="faixa-envio" role="status" aria-label="Avisos de envio do WhatsApp">
      {alertas.map((alerta) => (
        <p key={alerta.accountId} className={alerta.paused ? 'faixa-envio-item pausada' : 'faixa-envio-item'}>
          {alerta.paused
            ? `O envio pelo WhatsApp "${alerta.accountName}" está pausado: as respostas ficam aguardando e podem expirar sem chegar ao cliente. Avise o administrador.`
            : `O WhatsApp "${alerta.accountName}" pediu para aguardar um pouco: as respostas estão saindo com atraso.`}
        </p>
      ))}
    </div>
  );
}

/** O aviso de uma conversa: o número que ela usa está pausado ou esperando? Pausada prevalece sobre esperando. */
export function alertaDoNumero(alertas: readonly SendingAlert[] | undefined, channelInstanceId: string | undefined): SendingAlert | null {
  if (!alertas || !channelInstanceId) {
    return null;
  }
  const doNumero = alertas.filter((a) => a.channelInstanceIds.includes(channelInstanceId));
  return doNumero.find((a) => a.paused) ?? doNumero[0] ?? null;
}
