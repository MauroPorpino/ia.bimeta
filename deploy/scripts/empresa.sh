#!/usr/bin/env bash
# Coloca uma empresa (tenant) em funcionamento com o comando provision do Api (docs/operations/NOVA-EMPRESA.md), a partir de um arquivo JSON.
# O JSON NAO leva segredo: os campos terminados em "Env" sao o NOME de uma variavel; os valores vem do arquivo .env da empresa (opcional), que este script
# repassa ao conteiner SEM colocar valor em linha de comando. Por seguranca, o padrao e SO VALIDAR (nada e gravado); para aplicar use --aplicar.
# Uso: empresa.sh <empresa.json> [<empresa.env>] [--aplicar]
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

json=""
envfile=""
aplicar=0
for argumento in "$@"; do
  case "$argumento" in
    --aplicar) aplicar=1 ;;
    *.json) json="$argumento" ;;
    *) envfile="$argumento" ;;
  esac
done
[ -n "$json" ] || fail "uso: empresa.sh <empresa.json> [<empresa.env>] [--aplicar]"
[ -f "$json" ] || fail "arquivo nao encontrado: $json"
json="$(cd "$(dirname "$json")" && pwd)/$(basename "$json")"

envargs=()
if [ -n "$envfile" ]; then
  require_private_file "$envfile"
  while IFS= read -r linha || [ -n "$linha" ]; do
    case "$linha" in ''|'#'*) continue ;; esac
    nome="${linha%%=*}"
    case "$nome" in [A-Za-z_]*) ;; *) continue ;; esac
    case "$nome" in *[!A-Za-z0-9_]*) continue ;; esac
    envargs+=(-e "$nome")
  done < "$envfile"
  read_env_file "$envfile"
fi

tty_flag=()
[ -t 0 ] || tty_flag=(-T)
rodar() {
  compose run --rm --no-deps "${tty_flag[@]}" -v "$json:/config/empresa.json:ro" "${envargs[@]}" api provision --file /config/empresa.json "$@"
}

log "validando o arquivo da empresa (nada e gravado)"
rodar --validate-only
if [ "$aplicar" -eq 1 ]; then
  log "aplicando (idempotente: repetir completa o que faltou e nunca duplica)"
  rodar
else
  log "somente validado. Para provisionar de verdade: repita com --aplicar"
fi
