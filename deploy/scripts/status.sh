#!/usr/bin/env bash
# O estado da pilha de uma vez: conteineres e saude, o comando "status" do ia.bimeta (PostgreSQL, migrations, Api, Worker, filas, contas, arquivos),
# espaco em disco e idade do ultimo backup. So leitura. Saida: 0 tudo em ordem; 1 ha o que olhar; 2 critico.
# Uso: status.sh [--tenant <slug>]
set -uo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
load_backup_env

tenant_args=()
if [ "${1:-}" = "--tenant" ] && [ -n "${2:-}" ]; then tenant_args=(--tenant "$2"); fi

pior=0
marca() { [ "$1" -gt "$pior" ] && pior="$1"; return 0; }

echo "== Conteineres" >&2
compose ps >&2 || { marca 2; echo "docker compose falhou" >&2; }

echo >&2
echo "== ia.bimeta (comando status)" >&2
"$DEPLOY_DIR/scripts/cli.sh" status --api-url http://api:8080/health "${tenant_args[@]}"
codigo=$?
marca "$codigo"

echo >&2
echo "== Disco e backup" >&2
pcent="$(df -P "$BACKUP_DIR" 2>/dev/null | awk 'NR==2 {gsub("%","",$5); print $5}')"
if [ -n "$pcent" ]; then
  echo "uso do disco dos backups: ${pcent}% (alerta em ${ALERT_DISK_PERCENT}%)" >&2
  [ "$pcent" -lt "$ALERT_DISK_PERCENT" ] || marca 1
fi
if [ -f "$BACKUP_DIR/ultimo-backup-ok" ]; then
  idade_h=$(( ( $(date +%s) - $(stat -c %Y "$BACKUP_DIR/ultimo-backup-ok") ) / 3600 ))
  echo "ultimo backup bom ha ${idade_h} h (alerta acima de ${ALERT_BACKUP_MAX_AGE_HOURS} h): $(sed -n 2p "$BACKUP_DIR/ultimo-backup-ok")" >&2
  [ "$idade_h" -le "$ALERT_BACKUP_MAX_AGE_HOURS" ] || marca 1
else
  echo "NENHUM backup bom registrado ainda (rode scripts/backup.sh)" >&2
  marca 1
fi
exit "$pior"
