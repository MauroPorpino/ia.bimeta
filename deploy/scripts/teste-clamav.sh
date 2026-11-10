#!/usr/bin/env bash
# Prova a integracao com o ClamAV REAL: espera o daemon ficar saudavel e roda o comando "attachments test-scanner", que fala direto com o motor:
# um arquivo benigno pequeno deve voltar LIMPO, um benigno do tamanho maximo de documento deve voltar limpo (prova o StreamMaxLength do daemon) e a
# amostra de teste EICAR (inofensiva por desenho) deve voltar INFECTADA. Sai 0 so se os tres derem o resultado esperado.
# Uso: teste-clamav.sh [tamanho-em-MB]    (padrao 20, o maior limite de documento)
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

megas="${1:-20}"
case "$megas" in ''|*[!0-9]*) fail "o tamanho deve ser um numero de MB (1 a 100)" ;; esac

log "esperando o ClamAV ficar saudavel (na primeira vez ele baixa as assinaturas: pode levar varios minutos)"
inicio="$(date +%s)"
while :; do
  id="$(compose ps -q clamav 2>/dev/null | head -n1)"
  estado=""
  [ -z "$id" ] || estado="$("$DOCKER" inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$id" 2>/dev/null || true)"
  [ "$estado" = "healthy" ] && break
  [ $(( $(date +%s) - inicio )) -lt 1800 ] || fail "o ClamAV nao ficou saudavel em 30 minutos (estado: ${estado:-ausente}); veja: docker compose --project-directory $DEPLOY_DIR logs clamav"
  sleep 10
done
log "ClamAV saudavel; rodando o teste direto com o motor"
exec "$DEPLOY_DIR/scripts/cli.sh" attachments test-scanner --size-mb "$megas"
