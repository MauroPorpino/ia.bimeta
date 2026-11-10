#!/usr/bin/env bash
# O monitor agendado (systemd timer, a cada 5 minutos; veja agendar.sh): confere os conteineres, o comando "status" do ia.bimeta, o disco, a idade do
# backup e o HTTPS local; ALERTA (arquivo + webhook) quando algo esta errado e reinicia o Worker se ele ficou TRAVADO (a retomada dele e segura: entrega
# pelo menos uma vez, com idempotencia). Nao repete o mesmo alerta a cada execucao: so quando muda ou depois de 6 horas. Nunca imprime segredo.
set -uo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
load_backup_env
[ -f "$DEPLOY_DIR/.env" ] && read_env_file "$DEPLOY_DIR/.env"

problemas=()
add() { problemas+=("$*"); }

# 1. Conteineres: rodando e, quando tem verificacao de saude, saudaveis.
for servico in db clamav api worker web; do
  id="$(compose ps -q "$servico" 2>/dev/null | head -n1)"
  if [ -z "$id" ]; then add "o servico $servico nao existe/nao esta no ar"; continue; fi
  estado="$("$DOCKER" inspect -f '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}-{{end}}' "$id" 2>/dev/null || echo 'erro|erro')"
  case "$estado" in
    running\|healthy|running\|-) ;;
    *) add "o servico $servico esta '$estado' (estado|saude)" ;;
  esac
done

# 2. O comando status do ia.bimeta (a saida fica num arquivo temporario; so as linhas de problema vao ao alerta).
saida="$(mktemp)"
trap 'rm -f "$saida"' EXIT
"$DEPLOY_DIR/scripts/cli.sh" status --api-url http://api:8080/health >"$saida" 2>&1
codigo=$?
if [ "$codigo" -ge 2 ]; then
  resumo="$(grep -E '^\[CRITICO\]|CRITICO' "$saida" | head -n 3 | tr '\n' ';' | cut -c1-300)"
  add "status CRITICO: ${resumo:-veja scripts/status.sh}"
fi
# O Worker travado (conexoes sem atividade): reinicia uma vez e alerta.
if grep -q 'travado?' "$saida"; then
  log "Worker travado: reiniciando (a retomada e segura)"
  compose restart worker >/dev/null 2>&1 || true
  add "o Worker estava travado (sem atividade) e foi reiniciado"
fi

# 3. Disco (backups e docker) e idade do ultimo backup.
pcent="$(df -P "$BACKUP_DIR" 2>/dev/null | awk 'NR==2 {gsub("%","",$5); print $5}')"
if [ -n "$pcent" ] && [ "$pcent" -ge "$ALERT_DISK_PERCENT" ]; then add "disco dos backups com ${pcent}% de uso (limite ${ALERT_DISK_PERCENT}%)"; fi
if [ -f "$BACKUP_DIR/ultimo-backup-ok" ]; then
  idade_h=$(( ( $(date +%s) - $(stat -c %Y "$BACKUP_DIR/ultimo-backup-ok") ) / 3600 ))
  if [ "$idade_h" -gt "$ALERT_BACKUP_MAX_AGE_HOURS" ]; then add "o ultimo backup bom tem ${idade_h} h (limite ${ALERT_BACKUP_MAX_AGE_HOURS} h)"; fi
else
  add "nenhum backup bom registrado ainda"
fi

# 4. O HTTPS local: o proprio Caddy atende o dominio com o certificado (sem depender da rede publica nem do roteador).
if [ -n "${DOMAIN:-}" ] && command -v curl >/dev/null 2>&1; then
  opcoes=(-fsS -m 15 -o /dev/null --resolve "${DOMAIN}:443:127.0.0.1")
  # Com a CA de homologacao do Let's Encrypt (testes) o certificado nao e confiavel: so entao se aceita sem verificar.
  case "${ACME_CA:-}" in *staging*) opcoes+=(-k) ;; esac
  curl "${opcoes[@]}" "https://${DOMAIN}/api/health" 2>/dev/null || add "o HTTPS local de ${DOMAIN} nao respondeu em /api/health (Caddy, certificado ou Api)"
fi

# Alerta com de-duplicacao (assinatura do conjunto de problemas; repete depois de 6 horas).
estado_arquivo="$BACKUP_DIR/monitor.estado"
mkdir -p "$BACKUP_DIR" 2>/dev/null || true
if [ "${#problemas[@]}" -eq 0 ]; then
  [ ! -f "$estado_arquivo" ] || { alerta "RECUPERADO: o ambiente voltou ao normal"; rm -f "$estado_arquivo"; }
  exit 0
fi
assinatura="$(printf '%s\n' "${problemas[@]}" | sha256sum | cut -d' ' -f1)"
agora="$(date +%s)"
if [ -f "$estado_arquivo" ]; then
  anterior="$(sed -n 1p "$estado_arquivo")"
  quando="$(sed -n 2p "$estado_arquivo")"
  if [ "$anterior" = "$assinatura" ] && [ $(( agora - ${quando:-0} )) -lt 21600 ]; then
    exit 1
  fi
fi
printf '%s\n%s\n' "$assinatura" "$agora" > "$estado_arquivo"
alerta "PROBLEMAS: $(printf '%s; ' "${problemas[@]}")"
exit 1
