#!/usr/bin/env bash
# Ensaio de restauracao: prova que o backup restaura, num projeto Compose DESCARTAVEL ("iabimeta-ensaio"), sem tocar o projeto "iabimeta". ADR 0032/0033.
#
# Uso: ensaiar-restauracao.sh [arquivo.tar.gpg]      Saida: 0 = ensaio ok; 1 = falhou.
#   Sem argumento usa o backup local mais recente valido (o apontado por BACKUP_DIR/ultimo-backup-ok, ou o mais novo no padrao do nome).
#   Roda o MESMO restaurar.sh, mas com COMPOSE_PROJECT=iabimeta-ensaio e outra sub-rede (para nao colidir com a rede de producao), e no FIM
#   (inclusive em falha) derruba SO o projeto de ensaio com "compose down -v".
#   Resultado do "attachments check": 0 = ok; 1 = divergencias (aceitavel no ensaio: objetos apagados pela retencao entre o dump e a copia
#   dos anexos aparecem assim; o ensaio lista e avisa); 2 ou outro = falha.
# TRAVAS: recusa rodar se COMPOSE_PROJECT vier definido com outro valor; antes do "down -v" confere de novo que o projeto e o de ensaio.
# O ensaio usa disco (os volumes do ensaio e o texto claro temporario) e CPU: rode fora do horario de pico. Use TMPDIR para escolher o disco.
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
umask 077

SCRIPTS_DIR="$DEPLOY_DIR/scripts"
ENSAIO="iabimeta-ensaio"
INICIO=$SECONDS

# Trava 1: o projeto efetivo so pode ser o de ensaio. Um COMPOSE_PROJECT vazio faria o compose usar o projeto de producao: por isso e imposto aqui.
if [ -n "${COMPOSE_PROJECT:-}" ] && [ "$COMPOSE_PROJECT" != "$ENSAIO" ]; then
  fail "COMPOSE_PROJECT='$COMPOSE_PROJECT' nao e '$ENSAIO': o ensaio recusa rodar fora do projeto descartavel (nada foi alterado)"
fi
export COMPOSE_PROJECT="$ENSAIO"
export EDGE_SUBNET="172.29.90.0/24"
export EDGE_WEB_IP="172.29.90.2"
readonly COMPOSE_PROJECT

ARQUIVO=""
while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" >&2; exit 0 ;;
    -*) fail "opcao desconhecida: $1 (uso: ensaiar-restauracao.sh [arquivo.tar.gpg])" ;;
    *)
      [ -z "$ARQUIVO" ] || fail "informe no maximo um arquivo de backup"
      ARQUIVO="$1"
      ;;
  esac
  shift
done

TMP=""
LOCK_MEU=0
DERRUBAR=0
# Trava 2: so derruba se o projeto efetivo e exatamente o de ensaio (compose usa -p "$COMPOSE_PROJECT").
derrubar_ensaio() {
  if [ "${COMPOSE_PROJECT:-}" != "$ENSAIO" ]; then
    log "ERRO: o projeto efetivo nao e '$ENSAIO': o down -v NAO sera executado"
    return 1
  fi
  log "derrubando o projeto de ensaio ($ENSAIO) e seus volumes ..."
  compose down -v </dev/null >/dev/null 2>&1 || log "AVISO: nao consegui derrubar o projeto de ensaio; confira com: docker compose -p $ENSAIO down -v"
}
limpar() {
  local rc=$?
  trap - EXIT
  [ "$DERRUBAR" = 0 ] || derrubar_ensaio || true
  [ -z "$TMP" ] || rm -rf "$TMP"
  [ "$LOCK_MEU" = 0 ] || lock_release "$BACKUP_DIR/.lock-ensaio"
  if [ "$rc" -ne 0 ]; then
    alerta "ensaio de restauracao FALHOU (veja o log do agendador)"
  fi
  exit "$rc"
}
trap limpar EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

for c in gpg tar sha256sum "$DOCKER"; do need "$c"; done
load_backup_env
[ -d "$BACKUP_DIR" ] || mkdir -p "$BACKUP_DIR"
if ! lock_acquire "$BACKUP_DIR/.lock-ensaio"; then
  fail "outro ensaio de restauracao esta em andamento (lock em $BACKUP_DIR/.lock-ensaio)"
fi
LOCK_MEU=1

# O backup a usar.
if [ -z "$ARQUIVO" ]; then
  if [ -f "$BACKUP_DIR/ultimo-backup-ok" ]; then
    nome="$(sed -n '2p' "$BACKUP_DIR/ultimo-backup-ok" | tr -d '\r')"
    if [[ "$nome" =~ ^iabimeta-[0-9]{8}T[0-9]{6}Z\.tar\.gpg$ ]] && [ -f "$BACKUP_DIR/$nome" ]; then
      ARQUIVO="$BACKUP_DIR/$nome"
    fi
  fi
  if [ -z "$ARQUIVO" ]; then
    ARQUIVO="$(find "$BACKUP_DIR" -maxdepth 1 -type f -name 'iabimeta-????????T??????Z.tar.gpg' | LC_ALL=C sort -r | head -n1 || true)"
  fi
  [ -n "$ARQUIVO" ] || fail "nao ha backup local em $BACKUP_DIR para ensaiar"
fi
[ -f "$ARQUIVO" ] || fail "arquivo de backup nao encontrado: $ARQUIVO"
log "ensaio de restauracao com $(basename "$ARQUIVO") no projeto $ENSAIO (sub-rede $EDGE_SUBNET)"

# A partir daqui o projeto de ensaio pode ter recursos: o fim (ou falha) sempre o derruba.
DERRUBAR=1
volumes="$("$DOCKER" volume ls -q)" || fail "nao consegui listar os volumes do docker (o docker esta no ar?)"
if grep -qE "^${ENSAIO}_" <<<"$volumes"; then
  log "restos de um ensaio anterior ($ENSAIO) encontrados: o projeto e descartavel, sera derrubado antes de comecar."
  derrubar_ensaio || fail "nao consegui limpar o ensaio anterior"
fi

TMP="$(mktemp -d "${TMPDIR:-/tmp}/iabimeta-ensaio.XXXXXX")" || fail "nao consegui criar a pasta temporaria"
chmod 700 "$TMP"
RESULTADO="$TMP/resultado.txt"
rc=0
bash "$SCRIPTS_DIR/restaurar.sh" "$ARQUIVO" --resultado "$RESULTADO" || rc=$?
if [ "$rc" -ne 0 ]; then
  fail "a restauracao do ensaio FALHOU (codigo $rc): o backup $(basename "$ARQUIVO") nao esta provado"
fi

rget() { grep -m1 "^$1=" "$RESULTADO" | cut -d= -f2- || true; }
check_rc="$(rget check_saida)"
linhas="$(rget check_linhas_conferidas)"
objetos="$(rget manifesto_anexos_objetos)"
duracao=$((SECONDS - INICIO))
log "RESUMO DO ENSAIO: backup $(basename "$ARQUIVO")"
log "  duracao total: ${duracao}s"
log "  objetos de anexo no manifesto do backup: ${objetos:-desconhecido}"
log "  linhas disponiveis conferidas pelo attachments check: ${linhas:-desconhecido}"
case "$check_rc" in
  0) log "  attachments check: sem divergencias" ;;
  1)
    log "  attachments check: DIVERGENCIAS (aceitavel no ensaio)"
    log "  AVISO: objetos apagados pela retencao entre o dump e a copia dos anexos aparecem assim. Se a lista acima mostrar muito mais"
    log "         que isso, investigue antes de confiar neste backup."
    ;;
  *) fail "o attachments check retornou '$check_rc' (esperado 0 ou 1)" ;;
esac
log "ENSAIO OK: o backup restaurou banco e anexos em ambiente descartavel."
exit 0
