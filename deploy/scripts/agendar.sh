#!/usr/bin/env bash
# Instala (ou remove) os agendamentos do systemd: BACKUP diario (03:30) e MONITOR a cada 5 minutos. Rode como ROOT, DEPOIS de a pilha subir e de um
# backup manual funcionar. Uso: sudo ./agendar.sh   |   sudo ./agendar.sh --remover
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[ "$(id -u)" -eq 0 ] || fail "rode como root (sudo ./agendar.sh)"
unidades=(iabimeta-backup.service iabimeta-backup.timer iabimeta-monitor.service iabimeta-monitor.timer)

if [ "${1:-}" = "--remover" ]; then
  systemctl disable --now iabimeta-backup.timer iabimeta-monitor.timer 2>/dev/null || true
  for unidade in "${unidades[@]}"; do rm -f "/etc/systemd/system/$unidade"; done
  systemctl daemon-reload
  log "agendamentos removidos"
  exit 0
fi

command -v systemctl >/dev/null 2>&1 || fail "systemctl nao encontrado (isto e para uma maquina com systemd)"
for unidade in "${unidades[@]}"; do
  modelo="$DEPLOY_DIR/systemd/$unidade"
  [ -f "$modelo" ] || fail "modelo nao encontrado: $modelo"
  conteudo="$(cat "$modelo")"
  printf '%s\n' "${conteudo//@DEPLOY_DIR@/$DEPLOY_DIR}" > "/etc/systemd/system/$unidade"
  chmod 644 "/etc/systemd/system/$unidade"
done
systemctl daemon-reload
systemctl enable --now iabimeta-backup.timer iabimeta-monitor.timer
systemctl list-timers 'iabimeta-*' --no-pager >&2 || true
log "agendado. Acompanhe com: journalctl -u iabimeta-backup.service -u iabimeta-monitor.service"
