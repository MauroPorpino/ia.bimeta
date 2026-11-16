#!/usr/bin/env bash
# Confere o ambiente ANTES de subir: arquivos de segredos (existencia, permissao e formato das chaves), dominio, ferramentas, hora, recursos da maquina
# e a configuracao do Compose. Sai 0 se nao ha ERRO (avisos nao reprovam) e 1 se houver. Nunca imprime valor de segredo.
# Uso: preflight.sh [--sem-docker]   (--sem-docker pula so as conferencias que precisam do docker)
set -uo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

sem_docker=0
[ "${1:-}" = "--sem-docker" ] && sem_docker=1

erros=0
avisos=0
ok() { printf 'OK     %s\n' "$*" >&2; }
erro() { printf 'ERRO   %s\n' "$*" >&2; erros=$((erros + 1)); }
aviso() { printf 'AVISO  %s\n' "$*" >&2; avisos=$((avisos + 1)); }

modo_ok() {
  local modo
  modo="$(stat -c '%a' "$1" 2>/dev/null || echo '?')"
  case "$modo" in 600|400) return 0 ;; *) return 1 ;; esac
}

# Uma chave Base64 que decifra para exatamente $2 bytes (ou mais, com "min").
chave_ok() {
  local valor="$1" bytes="$2" tamanho
  [ -n "$valor" ] || return 1
  tamanho="$(printf '%s' "$valor" | base64 -d 2>/dev/null | wc -c | tr -d ' ')" || return 1
  [ "$tamanho" -ge "$bytes" ]
}

# --- arquivos de segredos
for arquivo in .env app.env backup.env; do
  caminho="$DEPLOY_DIR/$arquivo"
  if [ ! -f "$caminho" ]; then
    erro "$arquivo nao existe (rode scripts/gerar-segredos.sh)"
  elif ! modo_ok "$caminho"; then
    erro "$arquivo com permissao insegura (chmod 600 $caminho)"
  else
    ok "$arquivo presente com permissao 600"
  fi
done

if [ -f "$DEPLOY_DIR/.env" ]; then
  ( read_env_file "$DEPLOY_DIR/.env"
    [ -n "${DOMAIN:-}" ] || exit 11
    case "$DOMAIN" in *[!A-Za-z0-9.-]*|*..*|.*|*.) exit 12 ;; esac
    case "$DOMAIN" in *.*) ;; *) exit 12 ;; esac
    [ -n "${ACME_EMAIL:-}" ] || exit 13
    case "${POSTGRES_PASSWORD:-}" in ''|*[!A-Za-z0-9]*) exit 14 ;; esac
    case "${APP_DB_PASSWORD:-}" in ''|*[!A-Za-z0-9]*) exit 15 ;; esac
    [ "${#POSTGRES_PASSWORD}" -ge 24 ] && [ "${#APP_DB_PASSWORD}" -ge 24 ] || exit 16
    exit 0 )
  case $? in
    0) ok ".env: dominio, e-mail e senhas do banco no formato esperado" ;;
    11) erro ".env: DOMAIN vazio" ;;
    12) erro ".env: DOMAIN invalido" ;;
    13) erro ".env: ACME_EMAIL vazio" ;;
    14|15) erro ".env: senha do banco vazia ou com caractere fora de letras e numeros" ;;
    16) erro ".env: senha do banco curta (use ao menos 24 caracteres)" ;;
    *) erro ".env: nao foi possivel conferir" ;;
  esac
fi

if [ -f "$DEPLOY_DIR/app.env" ]; then
  for par in "Channels__Secrets:32" "AiGateway__Secrets:32" "Capabilities__Secrets:32" "Identity__Secrets:32" "Workflows__Secrets:32" "Conversations__Attachments__Encryption:32"; do
    prefixo="${par%%:*}"
    ( read_env_file "$DEPLOY_DIR/app.env"
      ativo_nome="${prefixo}__ActiveKeyId"
      id="${!ativo_nome:-}"
      [ -n "$id" ] || exit 21
      valor_nome="${prefixo}__Keys__${id}"
      valor="${!valor_nome:-}"
      [ -n "$valor" ] || exit 22
      chave_ok "$valor" 32 || exit 23
      exit 0 )
    case $? in
      0) ok "app.env: ${prefixo//__/:} tem chave ativa valida (Base64 de 32 bytes)" ;;
      21) erro "app.env: ${prefixo}__ActiveKeyId vazio" ;;
      22) erro "app.env: a chave apontada por ${prefixo}__ActiveKeyId esta vazia ou ausente" ;;
      23) erro "app.env: a chave de ${prefixo//__/:} nao e Base64 de 32 bytes" ;;
      *) erro "app.env: nao foi possivel conferir ${prefixo//__/:}" ;;
    esac
  done
  ( read_env_file "$DEPLOY_DIR/app.env"
    chave_ok "${Agents__Options__KnowledgeGapHashKey:-}" 32 )
  if [ $? -eq 0 ]; then ok "app.env: chave do Knowledge Engine valida"; else erro "app.env: Agents__Options__KnowledgeGapHashKey vazia ou invalida (Base64, ao menos 32 bytes)"; fi
fi

if [ -f "$DEPLOY_DIR/backup.env" ]; then
  ( read_env_file "$DEPLOY_DIR/backup.env"
    [ "${#BACKUP_PASSPHRASE}" -ge 24 ] ) 2>/dev/null
  if [ $? -eq 0 ]; then ok "backup.env: frase de cifra presente"; else erro "backup.env: BACKUP_PASSPHRASE vazia ou curta (ao menos 24 caracteres)"; fi
  ( read_env_file "$DEPLOY_DIR/backup.env"; [ -n "${BACKUP_EXTERNAL_DEST:-}" ] )
  if [ $? -eq 0 ]; then ok "backup.env: copia externa configurada"; else aviso "backup.env: BACKUP_EXTERNAL_DEST vazio: o disco desta VM NAO e um backup. Configure a copia externa antes do piloto"; fi
fi

# --- ferramentas e maquina
for comando in bash gpg tar sha256sum base64 curl; do
  if command -v "$comando" >/dev/null 2>&1; then ok "comando $comando presente"; else erro "comando $comando ausente"; fi
done
if command -v rsync >/dev/null 2>&1; then ok "rsync presente"; else aviso "rsync ausente (so necessario para a copia externa por SSH)"; fi

if [ "$sem_docker" -eq 0 ]; then
  if command -v "$DOCKER" >/dev/null 2>&1 && "$DOCKER" compose version >/dev/null 2>&1; then
    ok "docker e o plugin compose presentes"
    if compose config -q >/dev/null 2>&1; then ok "docker-compose.yml valido com o .env atual"; else erro "docker compose config falhou: rode 'docker compose --project-directory deploy config' para ver o motivo"; fi
  else
    erro "docker ou o plugin compose ausentes (rode scripts/instalar-host.sh, como root)"
  fi
fi

if [ -r /proc/meminfo ]; then
  memoria_kb="$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)"
  if [ "${memoria_kb:-0}" -ge 7000000 ]; then ok "memoria: $((memoria_kb / 1024)) MB"; elif [ "${memoria_kb:-0}" -ge 5500000 ]; then aviso "memoria: $((memoria_kb / 1024)) MB. O ClamAV sozinho pede 3 a 4 GiB; recomendado 8 GB na VM"; else erro "memoria: $((memoria_kb / 1024)) MB e insuficiente (ClamAV 3 a 4 GiB + PostgreSQL + Api + Worker): use 8 GB"; fi
fi
livre_gb="$(df -Pk "$DEPLOY_DIR" 2>/dev/null | awk 'NR==2 {print int($4/1024/1024)}')"
if [ -n "${livre_gb:-}" ]; then
  if [ "$livre_gb" -ge 20 ]; then ok "disco livre: ${livre_gb} GB"; else aviso "disco livre: ${livre_gb} GB (recomendado 20 GB ou mais para imagens, banco, anexos e backups locais)"; fi
fi
if command -v timedatectl >/dev/null 2>&1; then
  if [ "$(timedatectl show -p NTPSynchronized --value 2>/dev/null)" = "yes" ]; then ok "relogio sincronizado (NTP)"; else aviso "relogio NAO sincronizado: certificados e assinaturas dependem da hora certa (timedatectl set-ntp true)"; fi
fi
if command -v ss >/dev/null 2>&1; then
  for porta in 80 443; do
    if ss -ltn 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]${porta}\$"; then aviso "a porta $porta ja esta em uso nesta maquina (o proxy precisa dela; ignore se for a propria pilha ja no ar)"; else ok "porta $porta livre"; fi
  done
fi
if [ -f "$DEPLOY_DIR/.env" ]; then
  dominio="$( ( read_env_file "$DEPLOY_DIR/.env"; printf '%s' "${DOMAIN:-}" ) )"
  if [ -n "$dominio" ] && command -v getent >/dev/null 2>&1; then
    if getent hosts "$dominio" >/dev/null 2>&1; then ok "o dominio $dominio resolve no DNS (confira que aponta para o IP publico fixo)"; else aviso "o dominio $dominio NAO resolve neste DNS: sem o registro A apontando para o IP publico o certificado nao sai"; fi
  fi
fi

printf '\n%s erro(s), %s aviso(s).\n' "$erros" "$avisos" >&2
[ "$erros" -eq 0 ]
