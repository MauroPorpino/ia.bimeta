#!/usr/bin/env bash
# Atualiza o ia.bimeta para o codigo que esta nesta pasta (por exemplo, depois de "git pull"): BACKUP primeiro (aborta se falhar), constroi as imagens
# novas, lista e aplica as migrations, e recria so o que mudou. Registra a versao anterior para reversao.
# Uso: atualizar.sh [--sem-backup]   (--sem-backup so em ambiente de teste)
# Reversao: se algo der errado depois das migrations, a volta SEGURA e restaurar o backup desta atualizacao (docs/operations/IMPLANTACAO-LINUX.md).
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

sem_backup=0
[ "${1:-}" = "--sem-backup" ] && sem_backup=1

"$DEPLOY_DIR/scripts/preflight.sh" || fail "o preflight reprovou: corrija os ERROs acima e rode de novo"

raiz="$(cd "$DEPLOY_DIR/.." && pwd)"
nova="local"
if command -v git >/dev/null 2>&1 && git -C "$raiz" rev-parse --short HEAD >/dev/null 2>&1; then
  nova="$(git -C "$raiz" rev-parse --short HEAD)"
  git -C "$raiz" diff --quiet || log "AVISO: ha alteracoes nao commitadas na pasta do codigo; a imagem leva o que esta na pasta"
fi
read_env_file "$DEPLOY_DIR/.env"
anterior="${IABIMETA_VERSAO:-local}"
log "versao atual: $anterior; nova: $nova"

if [ "$sem_backup" -eq 0 ]; then
  log "backup antes de atualizar (obrigatorio)"
  "$DEPLOY_DIR/scripts/backup.sh" || fail "o backup falhou: NADA foi alterado. Resolva o backup antes de atualizar"
else
  log "AVISO: atualizando SEM backup (--sem-backup)"
fi

export IABIMETA_VERSAO="$nova"
log "construindo as imagens novas"
compose build
log "subindo o banco (se preciso) e listando as migrations pendentes"
compose up -d db
"$DEPLOY_DIR/scripts/cli.sh" migrate --list || true
log "aplicando as migrations"
"$DEPLOY_DIR/scripts/cli.sh" migrate
log "recriando os servicos com a imagem nova"
compose up -d
sleep 5
"$DEPLOY_DIR/scripts/status.sh" || true

# Grava a versao no .env (para um reinicio da VM subir a mesma imagem) e guarda a anterior.
tmp="$(mktemp)"
( umask 077
  grep -v '^IABIMETA_VERSAO=' "$DEPLOY_DIR/.env" > "$tmp" || true
  printf 'IABIMETA_VERSAO=%s\n' "$nova" >> "$tmp"
  cat "$tmp" > "$DEPLOY_DIR/.env" )
rm -f "$tmp"
echo "$anterior" > "$DEPLOY_DIR/.versao-anterior" 2>/dev/null || true
log "atualizado de $anterior para $nova. Rode scripts/status.sh e confira o atendimento."
