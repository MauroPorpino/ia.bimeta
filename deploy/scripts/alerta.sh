#!/usr/bin/env bash
# Registra um alerta: sempre no arquivo BACKUP_DIR/alertas.log e, se ALERT_WEBHOOK_URL estiver definida, num POST JSON {"text": "..."}.
# Uso: alerta.sh "mensagem". Nunca falha por causa do webhook (a rede de alerta nao pode derrubar o backup). A mensagem NUNCA deve conter segredo.
set -uo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

mensagem="${1:-alerta sem texto}"
load_backup_env
mkdir -p "$BACKUP_DIR" 2>/dev/null || true
estacao="$(hostname 2>/dev/null || echo host)"
carimbo="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
printf '%s %s %s\n' "$carimbo" "$estacao" "$mensagem" >> "$BACKUP_DIR/alertas.log" 2>/dev/null || log "nao foi possivel gravar $BACKUP_DIR/alertas.log"
log "ALERTA: $mensagem"

if [ -n "${ALERT_WEBHOOK_URL:-}" ] && command -v curl >/dev/null 2>&1; then
  # Escapa para JSON: barra, aspas e quebras de linha; remove outros controles.
  texto="[ia.bimeta@${estacao}] ${mensagem}"
  texto="${texto//\\/\\\\}"
  texto="${texto//\"/\\\"}"
  texto="${texto//$'\n'/ }"
  texto="${texto//$'\r'/ }"
  texto="${texto//$'\t'/ }"
  curl -fsS -m 10 -X POST -H 'Content-Type: application/json' --data "{\"text\":\"${texto}\"}" "$ALERT_WEBHOOK_URL" >/dev/null 2>&1 \
    || log "o webhook de alerta nao respondeu (o alerta ficou so no arquivo)"
fi
exit 0
