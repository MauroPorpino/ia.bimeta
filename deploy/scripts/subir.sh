#!/usr/bin/env bash
# Sobe a pilha do ia.bimeta: confere o ambiente, baixa e constroi as imagens, sobe o banco e o verificador, APLICA AS MIGRATIONS e sobe o resto.
# Em producao o Api e o Worker nao aplicam migrations sozinhos: este script as aplica (primeira instalacao). Para ATUALIZAR uma pilha com dados, use
# atualizar.sh (faz backup antes). Uso: subir.sh [--sem-migrar]
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

migrar=1
[ "${1:-}" = "--sem-migrar" ] && migrar=0

"$DEPLOY_DIR/scripts/preflight.sh" || fail "o preflight reprovou: corrija os ERROs acima e rode de novo"

esperar_saudavel() {
  local servico="$1" limite="${2:-180}" inicio id estado
  inicio="$(date +%s)"
  while :; do
    id="$(compose ps -q "$servico" 2>/dev/null | head -n1)"
    if [ -n "$id" ]; then
      estado="$("$DOCKER" inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$id" 2>/dev/null || true)"
      case "$estado" in healthy|running) return 0 ;; esac
    fi
    [ $(( $(date +%s) - inicio )) -lt "$limite" ] || fail "o servico $servico nao ficou saudavel em ${limite}s (estado: ${estado:-desconhecido}); veja: docker compose --project-directory $DEPLOY_DIR logs $servico"
    sleep 3
  done
}

log "baixando as imagens de terceiros (PostgreSQL e ClamAV)"
compose pull db clamav
log "construindo as imagens do ia.bimeta (Api, Worker e proxy com a interface); a primeira vez demora"
compose build
log "subindo o banco e o verificador"
compose up -d db clamav
esperar_saudavel db 180
if [ "$migrar" -eq 1 ]; then
  log "aplicando as migrations (comando migrate)"
  "$DEPLOY_DIR/scripts/cli.sh" migrate
fi
log "subindo o Api, o Worker e o proxy"
compose up -d
esperar_saudavel api 240
esperar_saudavel web 120
compose ps >&2
log "pilha no ar. O ClamAV baixa as assinaturas na primeira vez (alguns minutos): ate la os arquivos recebidos ficam pendentes e se resolvem sozinhos."
log "proximos passos: scripts/teste-clamav.sh, scripts/status.sh e, quando o DNS e o firewall estiverem prontos, o teste do webhook (docs/operations/IMPLANTACAO-LINUX.md)"
