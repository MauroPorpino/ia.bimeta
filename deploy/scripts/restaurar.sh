#!/usr/bin/env bash
# Restaura um backup (banco + anexos) do ia.bimeta numa VM NOVA, em volumes VAZIOS. ADR 0032 secao 6, ADR 0033.
#
# Uso: restaurar.sh <arquivo.tar.gpg> [--resultado <arquivo>]      Saida: 0 ou 1.
#   --resultado <arquivo>   (uso do ensaiar-restauracao.sh) grava chave=valor com o resultado do "attachments check" e nao imprime os proximos passos.
#
# So restaura em volumes que ainda NAO existem (ou, no caso dos anexos, existem e estao VAZIOS). NUNCA apaga nem sobrescreve volume existente:
# se <projeto>_pgdata ja existe, ou <projeto>_anexos nao esta vazio, ou api/worker/web estao rodando, RECUSA antes de qualquer alteracao.
# Pre-requisitos na VM nova: deploy/.env e deploy/app.env (a chave de cifra dos anexos e a das credenciais TEM DE SER AS MESMAS da origem),
# deploy/backup.env com a MESMA BACKUP_PASSPHRASE, o backup (.tar.gpg e .sha256) e a imagem da api disponivel.
# O texto claro do dump e dos anexos passa por uma pasta temporaria 0700 em ${TMPDIR:-/tmp}: use TMPDIR para escolher um disco com espaco.
# Sequencia: recusas -> verificar-backup.sh (completo) -> compose up -d db e esperar a saude -> pg_restore -> tar dos anexos para o volume
#   -> "attachments check" (so leitura) -> resumo e proximos passos.
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
umask 077

SCRIPTS_DIR="$DEPLOY_DIR/scripts"
INICIO=$SECONDS
ARQUIVO=""
RESULTADO=""
while [ $# -gt 0 ]; do
  case "$1" in
    --resultado)
      [ $# -ge 2 ] || fail "falta o valor de --resultado"
      RESULTADO="$2"
      shift
      ;;
    -h|--help) sed -n '2,13p' "${BASH_SOURCE[0]}" >&2; exit 0 ;;
    -*) fail "opcao desconhecida: $1 (uso: restaurar.sh <arquivo.tar.gpg>)" ;;
    *)
      [ -z "$ARQUIVO" ] || fail "informe um unico arquivo de backup"
      ARQUIVO="$1"
      ;;
  esac
  shift
done
[ -n "$ARQUIVO" ] || fail "uso: restaurar.sh <arquivo.tar.gpg>"
[ -f "$ARQUIVO" ] || fail "arquivo de backup nao encontrado: $ARQUIVO"
case "$ARQUIVO" in /*|./*|../*) ;; *) ARQUIVO="./$ARQUIVO" ;; esac

TMP=""
limpar() {
  local rc=$?
  trap - EXIT
  [ -z "$TMP" ] || rm -rf "$TMP"
  exit "$rc"
}
trap limpar EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

for c in gpg tar sha256sum "$DOCKER"; do need "$c"; done
PROJETO="${COMPOSE_PROJECT:-iabimeta}"
ANEXOS_NO_CONTEINER="/var/lib/iabimeta/anexos"

# 1. As RECUSAS (antes de qualquer alteracao).
require_private_file "$DEPLOY_DIR/.env"
require_private_file "$DEPLOY_DIR/app.env"
log "Projeto Compose: $PROJETO"
log "ATENCAO: a chave de cifra dos anexos (Conversations__Attachments__Encryption__Keys__*) e a das credenciais dos canais e da IA em app.env"
log "         TEM DE SER AS MESMAS da maquina de origem. Com outra chave, os arquivos e os segredos restaurados nao abrem."

volumes="$("$DOCKER" volume ls -q)" || fail "nao consegui listar os volumes do docker (o docker esta no ar?)"
if grep -qxF -- "${PROJETO}_pgdata" <<<"$volumes"; then
  fail "o volume ${PROJETO}_pgdata JA EXISTE: o restaurar.sh so restaura em volumes novos e nunca apaga nem sobrescreve. Se for um volume de teste que voce sabe descartar, remova-o voce mesmo (docker volume rm ${PROJETO}_pgdata) e rode de novo."
fi
if grep -qxF -- "${PROJETO}_anexos" <<<"$volumes"; then
  if ! lista="$(compose run --rm --no-deps -T --entrypoint tar api -C "$ANEXOS_NO_CONTEINER" -cf - . </dev/null | tar -tf -)"; then
    fail "o volume ${PROJETO}_anexos existe e nao consegui conferir se esta vazio (a imagem da api existe?); por seguranca nada foi restaurado"
  fi
  lista="$(printf '%s\n' "$lista" | grep -vE '^\./?$' || true)"
  if [ -n "$lista" ]; then
    fail "o volume ${PROJETO}_anexos JA EXISTE e NAO esta vazio: o restaurar.sh nunca sobrescreve arquivos. Nada foi alterado."
  fi
  log "o volume ${PROJETO}_anexos existe e esta vazio: sera usado."
fi
em_uso="$(compose ps -q api worker web 2>/dev/null || true)"
[ -z "$em_uso" ] || fail "ha servico api/worker/web rodando neste projeto ($PROJETO): pare-os antes (restaurar so em ambiente parado). Nada foi alterado."

# 2. Verificacao COMPLETA do backup, que ja deixa o conteudo verificado numa pasta temporaria.
TMP="$(mktemp -d "${TMPDIR:-/tmp}/iabimeta-restaurar.XXXXXX")" || fail "nao consegui criar a pasta temporaria em ${TMPDIR:-/tmp}"
chmod 700 "$TMP"
mkdir "$TMP/conteudo"
chmod 700 "$TMP/conteudo"
log "verificando o backup (completo) ..."
if ! bash "$SCRIPTS_DIR/verificar-backup.sh" "$ARQUIVO" --extrair-para "$TMP/conteudo"; then
  fail "o backup foi REPROVADO pela verificacao: nada foi restaurado"
fi
manifesto="$TMP/conteudo/manifesto.txt"
mget() { grep -m1 "^$1=" "$manifesto" | cut -d= -f2- || true; }
objetos="$(mget anexos_objetos)"
log "backup de $(mget criado_utc) (host $(mget host)); imagens na origem: $(mget imagens); $objetos objeto(s) de anexo."

# 3. Sobe SO o banco e espera ele ficar pronto (saude + banco iabimeta acessivel e estavel).
log "subindo o servico db ..."
compose up -d db </dev/null || fail "o compose up -d db falhou"
ESPERA="${DB_WAIT_TIMEOUT:-180}"
inicio_espera=$SECONDS
anterior=""
while :; do
  db_id="$(compose ps -q db 2>/dev/null | head -n1 || true)"
  situacao=""
  if [ -n "$db_id" ]; then
    situacao="$("$DOCKER" inspect -f '{{.State.Health.Status}}' "$db_id" 2>/dev/null || true)"
  fi
  if [ "$situacao" = "healthy" ]; then
    # O healthcheck pode passar com o servidor temporario da inicializacao: so segue quando o banco responde e o mesmo processo
    # responde duas vezes seguidas (o servidor definitivo).
    atual="$(compose exec -T db psql -U postgres -d iabimeta -tAc 'select pg_postmaster_start_time()' </dev/null 2>/dev/null || true)"
    if [ -n "$atual" ] && [ "$atual" = "$anterior" ]; then
      break
    fi
    anterior="$atual"
  fi
  [ $((SECONDS - inicio_espera)) -lt "$ESPERA" ] || fail "o servico db nao ficou pronto em ${ESPERA}s (situacao: ${situacao:-desconhecida}). Veja: docker compose logs db. Nada alem do volume novo foi criado."
  sleep "${DB_WAIT_INTERVAL:-3}"
done
log "o servico db esta pronto."

falha_no_meio() {
  log "A restauracao parou no meio. NADA foi apagado por este script. O banco e os anexos podem estar parcialmente restaurados."
  log "Para tentar de novo e preciso descartar, por decisao sua, os volumes novos (docker compose -p $PROJETO down -v) e rodar este script outra vez."
}

# 4. O banco.
log "restaurando o banco (pg_restore) ..."
if ! compose exec -T db pg_restore -U postgres -d iabimeta --no-owner --no-privileges --role=iabimeta --exit-on-error <"$TMP/conteudo/banco.dump"; then
  falha_no_meio
  fail "o pg_restore falhou"
fi

# 5. Os anexos, para o volume, pelo conteiner da api (o mesmo usuario do servico).
log "restaurando os anexos no volume ${PROJETO}_anexos ..."
if ! compose run --rm --no-deps -T --entrypoint tar api -C "$ANEXOS_NO_CONTEINER" -xf - <"$TMP/conteudo/anexos.tar"; then
  falha_no_meio
  fail "a extracao dos anexos falhou"
fi

# 6. O check (so leitura): cada linha disponivel tem o seu objeto, cada objeto tem a sua linha.
log "conferindo o banco contra os anexos (attachments check, so leitura) ..."
set +e
compose run --rm --no-deps -T api attachments check </dev/null 2>&1 | tee "$TMP/check.txt" >&2
check_rc="${PIPESTATUS[0]}"
set -e
conferidas="$(grep -E 'conferidas *: *[0-9]+' "$TMP/check.txt" | grep -oE '[0-9]+' | tail -n1 || true)"
duracao=$((SECONDS - INICIO))

if [ -n "$RESULTADO" ]; then
  {
    printf 'check_saida=%s\n' "$check_rc"
    printf 'check_linhas_conferidas=%s\n' "${conferidas:-desconhecido}"
    printf 'manifesto_anexos_objetos=%s\n' "$objetos"
    printf 'duracao_segundos=%s\n' "$duracao"
  } >"$RESULTADO"
fi

case "$check_rc" in
  0)
    log "OK: o attachments check nao achou divergencia."
    ;;
  1)
    log "AVISO: o attachments check apontou divergencias. Objetos apagados pela retencao entre o dump e a copia dos anexos aparecem assim (linha sem objeto)."
    log "       Revise a lista acima; o reparo ('attachments check --repair --confirm') so deve rodar depois dessa revisao."
    ;;
  *)
    log "ERRO: o attachments check FALHOU (codigo $check_rc): nao foi possivel conferir os anexos. O banco e os anexos foram restaurados, mas NAO conferidos."
    fail "restauracao feita, conferencia falhou"
    ;;
esac

log "Restauracao concluida em ${duracao}s (projeto $PROJETO): banco restaurado, $objetos objeto(s) de anexo restaurado(s)."
if [ -z "$RESULTADO" ]; then
  log "PROXIMOS PASSOS:"
  log "  1. Se o codigo (a versao das imagens) for mais novo que o do backup, rode: scripts/cli.sh migrate"
  log "  2. Suba o sistema: scripts/subir.sh"
  log "  3. Entre na interface e teste: login, uma conversa e a abertura de um anexo antigo."
fi
exit 0
