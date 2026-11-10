#!/usr/bin/env bash
# Roda um comando de operacao do ia.bimeta (migrate, provision, attachments, retention, status, reset-password) num conteiner descartavel do Api,
# com a MESMA configuracao do Api e do Worker (app.env e variaveis do Compose). O banco precisa estar no ar.
# Uso: cli.sh <comando> [argumentos...]      por exemplo:  cli.sh attachments report   |   cli.sh status   |   cli.sh retention report
# A saida e o codigo de saida sao os do comando (0 ok; 1 ha o que olhar; 2 uso invalido ou configuracao que impede).
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[ $# -ge 1 ] || fail "uso: cli.sh <migrate|provision|attachments|retention|status|reset-password> [argumentos...]"
tty_flag=()
[ -t 0 ] || tty_flag=(-T)
exec "$DOCKER" compose --project-directory "$DEPLOY_DIR" -f "$DEPLOY_DIR/docker-compose.yml" ${COMPOSE_PROJECT:+-p "$COMPOSE_PROJECT"} \
  run --rm --no-deps "${tty_flag[@]}" api "$@"
