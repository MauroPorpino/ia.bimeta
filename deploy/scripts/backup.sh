#!/usr/bin/env bash
# Backup COMPLETO e cifrado do ia.bimeta: banco (pg_dump -Fc) + pasta de anexos privados, num unico arquivo .tar.gpg. ADR 0032 secao 6, ADR 0033.
#
# Uso: backup.sh [--sem-externo]      Saida: 0 (backup local valido) ou 1 (falha; ha alerta em BACKUP_DIR/alertas.log).
#   --sem-externo   nao faz a copia externa (so para testes; em producao a copia externa e obrigatoria).
#
# A ORDEM e uma regra do ADR 0032: primeiro o dump do banco, DEPOIS o tar dos anexos. Assim toda linha de message_attachments do dump
# encontra o seu objeto na copia (salvo o que a retencao apagar no meio); objetos criados depois do dump sao orfaos inofensivos.
#
# Passos: lock -> dump -> tar dos anexos -> manifesto -> tar + gpg simetrico AES256 (frase por descritor de arquivo, nunca em argumento)
#   -> verificacao COMPLETA (verificar-backup.sh) -> ultimo-backup-ok -> copia externa -> retencao local.
# Configuracao no backup.env (permissao 600): BACKUP_PASSPHRASE (obrigatoria), BACKUP_DIR, BACKUP_KEEP_DAYS, BACKUP_EXTERNAL_DEST.
# Nenhum segredo vai a log, manifesto, alerta ou mensagem de erro.
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
umask 077

SCRIPTS_DIR="$DEPLOY_DIR/scripts"
INICIO=$SECONDS
PASSO="iniciar"
ALERTADO=0
LOCK_MEU=0
TMP=""
PARCIAIS=()
FINAL=""

# Qualquer saida (sucesso, erro, sinal) apaga a pasta temporaria (que tem texto claro) e os .partial, e solta o lock.
# Se o script morrer sem alerta proprio, o ultimo recurso avisa com o passo em que estava.
limpar() {
  local rc=$?
  trap - EXIT
  local p
  [ -z "$TMP" ] || rm -rf "$TMP"
  for p in "${PARCIAIS[@]+"${PARCIAIS[@]}"}"; do rm -f "$p"; done
  [ "$LOCK_MEU" = 0 ] || lock_release "$BACKUP_DIR/.lock"
  if [ "$rc" -ne 0 ] && [ "$ALERTADO" = 0 ]; then
    alerta "backup FALHOU no passo '$PASSO' (veja o log do agendador)"
  fi
  exit "$rc"
}
trap limpar EXIT
trap 'exit 143' TERM
trap 'exit 130' INT
trap 'exit 129' HUP

# Falha com mensagem, alerta (sem segredo) e saida 1.
falhar() {
  log "ERRO: $*"
  alerta "backup FALHOU no passo '$PASSO': $*"
  ALERTADO=1
  exit 1
}

SEM_EXTERNO=0
while [ $# -gt 0 ]; do
  case "$1" in
    --sem-externo) SEM_EXTERNO=1 ;;
    -h|--help) sed -n '2,13p' "${BASH_SOURCE[0]}" >&2; exit 0 ;;
    *) fail "opcao desconhecida: $1 (uso: backup.sh [--sem-externo])" ;;
  esac
  shift
done

# 1. Configuracao, ferramentas e lock.
PASSO="ler a configuracao"
load_backup_env
[ -n "${BACKUP_PASSPHRASE:-}" ] || falhar "BACKUP_PASSPHRASE nao esta definida no backup.env"
FRASE="$BACKUP_PASSPHRASE"
unset BACKUP_PASSPHRASE
case "$BACKUP_KEEP_DAYS" in
  ''|*[!0-9]*) falhar "BACKUP_KEEP_DAYS invalido (deve ser um numero inteiro de dias)" ;;
esac
case "$BACKUP_DIR" in
  ''|/) falhar "BACKUP_DIR invalido" ;;
esac

PASSO="verificar as ferramentas"
for c in gpg tar sha256sum "$DOCKER"; do
  command -v "$c" >/dev/null 2>&1 || falhar "comando necessario nao encontrado: $c"
done

PASSO="travar contra execucao simultanea"
if [ ! -d "$BACKUP_DIR" ]; then
  mkdir -p "$BACKUP_DIR" || falhar "nao consegui criar $BACKUP_DIR"
  chmod 700 "$BACKUP_DIR"
fi
if ! lock_acquire "$BACKUP_DIR/.lock"; then
  falhar "outra execucao do backup esta em andamento (lock em $BACKUP_DIR/.lock); esta execucao foi recusada"
fi
LOCK_MEU=1

# Sobras de uma execucao anterior morta a forca (a pasta temporaria tem texto claro): com o lock em maos, ninguem mais as usa.
PASSO="limpar sobras de execucoes interrompidas"
find "$BACKUP_DIR" -maxdepth 1 -type d -name '.tmp.*' -exec rm -rf {} + 2>/dev/null || true
find "$BACKUP_DIR" -maxdepth 1 -type f \( -name 'iabimeta-*.tar.gpg.partial' -o -name 'iabimeta-*.tar.gpg.sha256.partial' -o -name '.ultimo-backup-ok.partial' \) -delete 2>/dev/null || true

PASSO="criar a pasta temporaria"
TMP="$(mktemp -d "$BACKUP_DIR/.tmp.XXXXXX")" || falhar "nao consegui criar a pasta temporaria em $BACKUP_DIR"
chmod 700 "$TMP"

# 2. O DUMP do banco, primeiro.
PASSO="dump do banco (pg_dump)"
log "dump do banco..."
if ! compose exec -T db pg_dump -U postgres -Fc iabimeta </dev/null >"$TMP/banco.dump"; then
  falhar "o pg_dump falhou (o servico db esta no ar? veja o log acima)"
fi
[ -s "$TMP/banco.dump" ] || falhar "o pg_dump nao gerou dados"
[ "$(head -c 5 "$TMP/banco.dump")" = "PGDMP" ] || falhar "a saida do pg_dump nao tem o cabecalho de um dump -Fc (PGDMP)"

# 3. Depois, o tar dos anexos. O tar sai 1 quando um arquivo muda ou some durante a leitura (esperado com o sistema em uso: aceito com aviso,
#    desde que o tar resultante seja legivel); qualquer outro codigo e falha.
PASSO="tar dos anexos"
log "tar dos anexos..."
tar_rc=0
compose run --rm --no-deps -T --entrypoint tar api -C /var/lib/iabimeta/anexos -cf - . </dev/null >"$TMP/anexos.tar" || tar_rc=$?
case "$tar_rc" in
  0) ;;
  1) log "AVISO: o tar dos anexos avisou que arquivos mudaram ou sumiram durante a leitura (codigo 1); a legibilidade do tar e conferida a seguir." ;;
  *) falhar "o tar dos anexos falhou (codigo $tar_rc; o servico api/imagem existe? o volume de anexos esta montado?)" ;;
esac
tar -tf "$TMP/anexos.tar" >"$TMP/anexos.lista" 2>/dev/null || falhar "o tar dos anexos nao e legivel"
objetos="$(grep -c '\.bin$' "$TMP/anexos.lista" || true)"
rm -f "$TMP/anexos.lista"

# 4. O manifesto (chave=valor, ASCII, sem segredo).
PASSO="manifesto"
banco_sha="$(sha256_of "$TMP/banco.dump")"
banco_bytes="$(wc -c <"$TMP/banco.dump" | tr -d ' ')"
anexos_sha="$(sha256_of "$TMP/anexos.tar")"
anexos_bytes="$(wc -c <"$TMP/anexos.tar" | tr -d ' ')"
imagens="desconhecidas"
if saida_imagens="$(compose config --images 2>/dev/null)" && [ -n "$saida_imagens" ]; then
  imagens="$(printf '%s\n' "$saida_imagens" | tr -cd 'A-Za-z0-9._:/@\n-' | LC_ALL=C sort -u | tr '\n' ' ' | sed 's/ *$//')"
  [ -n "$imagens" ] || imagens="desconhecidas"
fi
estacao="$(hostname 2>/dev/null | tr -cd 'A-Za-z0-9._-' || true)"
[ -n "$estacao" ] || estacao="desconhecido"
{
  printf 'versao_formato=1\n'
  printf 'criado_utc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'banco_dump_sha256=%s\n' "$banco_sha"
  printf 'banco_dump_bytes=%s\n' "$banco_bytes"
  printf 'anexos_tar_sha256=%s\n' "$anexos_sha"
  printf 'anexos_tar_bytes=%s\n' "$anexos_bytes"
  printf 'anexos_objetos=%s\n' "$objetos"
  printf 'imagens=%s\n' "$imagens"
  printf 'host=%s\n' "$estacao"
} >"$TMP/manifesto.txt"

# 5. Empacota e cifra em fluxo (gpg simetrico AES256, sem compressao, frase pelo descritor 3), por arquivo .partial e troca atomica.
PASSO="cifrar o backup"
carimbo="$(date -u +%Y%m%dT%H%M%SZ)"
FINAL="$BACKUP_DIR/iabimeta-$carimbo.tar.gpg"
[ ! -e "$FINAL" ] && [ ! -e "$FINAL.INVALIDO" ] || falhar "o arquivo de destino ja existe ($(basename "$FINAL")); aguarde um segundo e rode de novo"
PARCIAL="$FINAL.partial"
PARCIAIS+=("$PARCIAL" "$FINAL.sha256.partial")
cifrar_para() {
  gpg --batch --yes --quiet --no-tty --pinentry-mode loopback --passphrase-fd 3 \
    --symmetric --cipher-algo AES256 --compress-algo none \
    --s2k-mode 3 --s2k-digest-algo SHA512 --s2k-count 65011712 \
    --output "$1" 3<<<"$FRASE"
}
log "cifrando (gpg AES256)..."
if ! tar -cf - -C "$TMP" manifesto.txt banco.dump anexos.tar | cifrar_para "$PARCIAL"; then
  falhar "a cifra do backup falhou"
fi
[ -s "$PARCIAL" ] || falhar "a cifra do backup nao gerou arquivo"
mv -f "$PARCIAL" "$FINAL"
chmod 600 "$FINAL"
cifrado_sha="$(sha256_of "$FINAL")"
cifrado_bytes="$(wc -c <"$FINAL" | tr -d ' ')"
printf '%s  %s\n' "$cifrado_sha" "$(basename "$FINAL")" >"$FINAL.sha256.partial"
mv -f "$FINAL.sha256.partial" "$FINAL.sha256"
chmod 600 "$FINAL.sha256"
# O texto claro ja nao e preciso (a verificacao decifra de novo): libera o espaco antes.
rm -f "$TMP/banco.dump" "$TMP/anexos.tar"

# 6. Verificacao COMPLETA do arquivo recem-criado.
PASSO="verificar o backup recem-criado"
log "verificando o backup recem-criado (completo)..."
if ! TMPDIR="$TMP" bash "$SCRIPTS_DIR/verificar-backup.sh" "$FINAL"; then
  mv -f "$FINAL" "$FINAL.INVALIDO"
  chmod 600 "$FINAL.INVALIDO"
  rm -f "$FINAL.sha256"
  falhar "a verificacao do backup recem-criado REPROVOU; o arquivo ficou como $(basename "$FINAL").INVALIDO"
fi
printf '%s\n%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$(basename "$FINAL")" >"$BACKUP_DIR/.ultimo-backup-ok.partial"
PARCIAIS+=("$BACKUP_DIR/.ultimo-backup-ok.partial")
mv -f "$BACKUP_DIR/.ultimo-backup-ok.partial" "$BACKUP_DIR/ultimo-backup-ok"
chmod 600 "$BACKUP_DIR/ultimo-backup-ok"
log "backup local valido: $(basename "$FINAL") ($cifrado_bytes bytes, $objetos objeto(s) de anexo)."

# 7. Copia externa. Falha: alerta e saida 1 no fim (o arquivo local fica; a retencao local ainda roda).
EXTERNO_RC=0
copiar_externo() {
  PASSO="copia externa"
  local dest="${BACKUP_EXTERNAL_DEST:-}" src nome parc
  if [ -z "$dest" ]; then
    log "AVISO: sem copia externa configurada (BACKUP_EXTERNAL_DEST vazio)."
    alerta "backup $(basename "$FINAL") valido, mas SEM copia externa configurada (BACKUP_EXTERNAL_DEST vazio): o disco desta maquina nao e um backup"
    return 0
  fi
  case "$dest" in
    *:*)
      if ! command -v rsync >/dev/null 2>&1; then
        alerta "backup $(basename "$FINAL") valido, mas a COPIA EXTERNA falhou: rsync nao esta instalado"
        return 1
      fi
      log "copia externa por rsync para $dest ..."
      if ! rsync -a --partial --partial-dir=.parcial --timeout=300 -e "ssh -o BatchMode=yes -o ConnectTimeout=30" -- "$FINAL" "$FINAL.sha256" "${dest%/}/"; then
        alerta "backup $(basename "$FINAL") valido, mas a COPIA EXTERNA por rsync falhou (destino $dest)"
        return 1
      fi
      ;;
    *)
      if [ ! -d "$dest" ]; then
        alerta "backup $(basename "$FINAL") valido, mas a COPIA EXTERNA falhou: o destino $dest nao existe (disco montado?)"
        return 1
      fi
      if [ "$(cd "$dest" && pwd -P)" = "$(cd "$BACKUP_DIR" && pwd -P)" ]; then
        alerta "backup $(basename "$FINAL") valido, mas a COPIA EXTERNA falhou: o destino e a propria pasta de backup"
        return 1
      fi
      log "copia externa para a pasta $dest ..."
      # O arquivo primeiro, o .sha256 por ultimo: a presenca do .sha256 indica copia completa.
      for src in "$FINAL" "$FINAL.sha256"; do
        nome="$(basename "$src")"
        parc="$dest/.$nome.partial"
        if ! cp -- "$src" "$parc"; then
          rm -f "$parc"
          alerta "backup $(basename "$FINAL") valido, mas a COPIA EXTERNA falhou ao copiar $nome para $dest"
          return 1
        fi
        if [ "$(sha256_of "$parc")" != "$(sha256_of "$src")" ]; then
          rm -f "$parc"
          alerta "backup $(basename "$FINAL") valido, mas a COPIA EXTERNA de $nome nao confere (SHA-256 diferente); a copia foi descartada"
          return 1
        fi
        if ! mv -f "$parc" "$dest/$nome"; then
          rm -f "$parc"
          alerta "backup $(basename "$FINAL") valido, mas a COPIA EXTERNA falhou ao finalizar $nome em $dest"
          return 1
        fi
      done
      if [ "$(stat -c %d "$dest" 2>/dev/null || echo a)" = "$(stat -c %d "$BACKUP_DIR" 2>/dev/null || echo b)" ]; then
        alerta "AVISO: a copia externa ($dest) esta no MESMO disco da pasta de backup: isso nao protege contra a perda do disco"
      fi
      ;;
  esac
  log "copia externa concluida."
  return 0
}
if [ "$SEM_EXTERNO" = 1 ]; then
  log "copia externa pulada (--sem-externo)."
else
  copiar_externo || EXTERNO_RC=1
fi

# 8. Retencao local: so BACKUP_DIR, so iabimeta-<carimbo>.tar.gpg (e o .sha256), so o que passa de BACKUP_KEEP_DAYS dias, nunca os 2 validos
#    mais recentes. Os arquivos .INVALIDO velhos tambem saem. Nunca toca a copia externa nem nome fora do padrao.
PASSO="retencao local"
aplicar_retencao() {
  local limite f nome epoch apagados=0 mantidos=0
  local -a todos=() invalidos=()
  local -A manter=()
  local padrao='^iabimeta-([0-9]{4})([0-9]{2})([0-9]{2})T([0-9]{2})([0-9]{2})([0-9]{2})Z\.tar\.gpg$'
  limite=$(( $(date -u +%s) - BACKUP_KEEP_DAYS * 86400 ))
  while IFS= read -r f; do
    [ -n "$f" ] && todos+=("$f")
  done < <(find "$BACKUP_DIR" -maxdepth 1 -type f -name 'iabimeta-*.tar.gpg' | LC_ALL=C sort -r)
  while IFS= read -r f; do
    [ -n "$f" ] && invalidos+=("$f")
  done < <(find "$BACKUP_DIR" -maxdepth 1 -type f -name 'iabimeta-*.tar.gpg.INVALIDO' | LC_ALL=C sort -r)
  # Os 2 mais recentes VALIDOS (com .sha256 ao lado e nome no padrao) nunca saem.
  for f in "${todos[@]+"${todos[@]}"}"; do
    nome="$(basename "$f")"
    [[ "$nome" =~ $padrao ]] || continue
    [ -f "$f.sha256" ] || continue
    if [ "$mantidos" -lt 2 ]; then
      manter["$f"]=1
      mantidos=$((mantidos + 1))
    fi
  done
  for f in "${todos[@]+"${todos[@]}"}"; do
    [ -z "${manter[$f]:-}" ] || continue
    nome="$(basename "$f")"
    [[ "$nome" =~ $padrao ]] || continue
    epoch="$(date -u -d "${BASH_REMATCH[1]}-${BASH_REMATCH[2]}-${BASH_REMATCH[3]}T${BASH_REMATCH[4]}:${BASH_REMATCH[5]}:${BASH_REMATCH[6]}Z" +%s 2>/dev/null || true)"
    [ -n "$epoch" ] || continue
    if [ "$epoch" -lt "$limite" ]; then
      rm -f -- "$f" "$f.sha256" || return 1
      apagados=$((apagados + 1))
    fi
  done
  for f in "${invalidos[@]+"${invalidos[@]}"}"; do
    nome="$(basename "$f")"
    [[ "${nome%.INVALIDO}" =~ $padrao ]] || continue
    epoch="$(date -u -d "${BASH_REMATCH[1]}-${BASH_REMATCH[2]}-${BASH_REMATCH[3]}T${BASH_REMATCH[4]}:${BASH_REMATCH[5]}:${BASH_REMATCH[6]}Z" +%s 2>/dev/null || true)"
    [ -n "$epoch" ] || continue
    if [ "$epoch" -lt "$limite" ]; then
      rm -f -- "$f" || return 1
      apagados=$((apagados + 1))
    fi
  done
  log "retencao local: $apagados arquivo(s) apagado(s) (mais velhos que $BACKUP_KEEP_DAYS dia(s); os 2 backups validos mais recentes sao sempre mantidos)."
}
aplicar_retencao || falhar "a retencao local falhou ao apagar arquivos antigos"

# 9. Fim.
duracao=$((SECONDS - INICIO))
if [ "$EXTERNO_RC" != 0 ]; then
  log "backup local VALIDO, mas a copia externa FALHOU (alerta registrado). Duracao: ${duracao}s."
  ALERTADO=1
  exit 1
fi
log "backup concluido em ${duracao}s: $(basename "$FINAL")."
exit 0
