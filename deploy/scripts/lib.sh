#!/usr/bin/env bash
# Biblioteca comum dos scripts de operacao do ia.bimeta (ADR 0033). So e "sourced" pelos outros scripts; nao tem efeito sozinha.
# Tudo em ASCII e sem dependencia alem do que o Ubuntu Server 24.04 traz (bash, coreutils, gnupg, curl) e do docker.

# A pasta deploy/ (pai da pasta scripts/), qualquer que seja o diretorio de onde o script foi chamado.
DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DEPLOY_DIR

# O binario do docker pode ser trocado (os testes dos scripts usam um falso): DOCKER_BIN=/caminho/docker
DOCKER="${DOCKER_BIN:-docker}"

# Mensagens vao para o stderr: o stdout fica livre para dados (por exemplo, a saida de um dump).
log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }
fail() { log "ERRO: $*"; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || fail "comando necessario nao encontrado: $1"; }

# Le um arquivo KEY=VALUE para o ambiente DESTE processo SEM executar nada dele (nunca "source"): ignora comentarios e linhas em branco,
# aceita so nomes validos de variavel, e o valor e tudo depois do primeiro "=" (Base64 tem "=" no fim). Nao imprime valor algum.
read_env_file() {
  local arquivo="$1" linha nome valor
  [ -f "$arquivo" ] || fail "arquivo nao encontrado: $arquivo"
  while IFS= read -r linha || [ -n "$linha" ]; do
    linha="${linha%$'\r'}"
    case "$linha" in ''|'#'*) continue ;; esac
    nome="${linha%%=*}"
    valor="${linha#*=}"
    case "$nome" in
      [A-Za-z_]*) ;;
      *) continue ;;
    esac
    case "$nome" in
      *[!A-Za-z0-9_]*) continue ;;
    esac
    export "$nome=$valor"
  done < "$arquivo"
}

# Um arquivo de segredos so pode ser legivel pelo dono (permissao 600 ou 400).
require_private_file() {
  local arquivo="$1" modo
  [ -f "$arquivo" ] || fail "arquivo nao encontrado: $arquivo"
  modo="$(stat -c '%a' "$arquivo" 2>/dev/null || echo '?')"
  case "$modo" in
    600|400) ;;
    *) fail "permissao insegura em $arquivo (e $modo; deve ser 600): chmod 600 $arquivo" ;;
  esac
}

# A configuracao do backup e dos alertas (backup.env) com os padroes.
load_backup_env() {
  if [ -f "$DEPLOY_DIR/backup.env" ]; then
    require_private_file "$DEPLOY_DIR/backup.env"
    read_env_file "$DEPLOY_DIR/backup.env"
  fi
  : "${BACKUP_DIR:=/var/backups/iabimeta}"
  : "${BACKUP_KEEP_DAYS:=14}"
  : "${ALERT_BACKUP_MAX_AGE_HOURS:=30}"
  : "${ALERT_DISK_PERCENT:=85}"
  export BACKUP_DIR BACKUP_KEEP_DAYS ALERT_BACKUP_MAX_AGE_HOURS ALERT_DISK_PERCENT
}

# O docker compose do projeto (o nome do projeto vem de COMPOSE_PROJECT quando preciso, por exemplo no ensaio de restauracao).
compose() {
  "$DOCKER" compose --project-directory "$DEPLOY_DIR" -f "$DEPLOY_DIR/docker-compose.yml" ${COMPOSE_PROJECT:+-p "$COMPOSE_PROJECT"} "$@"
}

sha256_of() { sha256sum "$1" | cut -d' ' -f1; }

# Registra um alerta (arquivo e, se configurado, webhook). Nunca faz o script chamador falhar.
alerta() { bash "$DEPLOY_DIR/scripts/alerta.sh" "$*" || true; }

# Um lock simples e portavel (mkdir e atomico), com deteccao de lock velho (processo morto).
lock_acquire() {
  local pasta="$1"
  mkdir -p "$(dirname "$pasta")"
  if mkdir "$pasta" 2>/dev/null; then
    echo "$$" > "$pasta/pid"
    return 0
  fi
  local dono
  dono="$(cat "$pasta/pid" 2>/dev/null || true)"
  if [ -n "$dono" ] && ! kill -0 "$dono" 2>/dev/null; then
    rm -rf "$pasta"
    mkdir "$pasta" 2>/dev/null && echo "$$" > "$pasta/pid" && return 0
  fi
  return 1
}
lock_release() { rm -rf "$1"; }
