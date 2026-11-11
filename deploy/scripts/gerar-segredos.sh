#!/usr/bin/env bash
# Gera os arquivos de segredos da implantacao a partir dos modelos: .env, app.env e backup.env (permissao 600), com senhas e chaves ALEATORIAS
# (/dev/urandom). NUNCA sobrescreve um arquivo que ja existe. NAO imprime nenhum segredo: voce os guarda copiando os arquivos (veja o final).
# Uso: gerar-segredos.sh --dominio atendimento.exemplo.com.br --email ops@exemplo.com.br
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

dominio=""
email=""
while [ $# -gt 0 ]; do
  case "$1" in
    --dominio) dominio="${2:-}"; shift 2 ;;
    --email) email="${2:-}"; shift 2 ;;
    *) fail "argumento desconhecido: $1 (uso: gerar-segredos.sh --dominio <dominio> --email <email>)" ;;
  esac
done
[ -n "$dominio" ] || fail "informe o dominio publico: --dominio atendimento.exemplo.com.br"
[ -n "$email" ] || fail "informe o e-mail do certificado: --email ops@exemplo.com.br"
case "$dominio" in
  *[!A-Za-z0-9.-]*|.*|*.|*..*|'') fail "dominio invalido (so letras, numeros, ponto e hifen): $dominio" ;;
esac
case "$dominio" in *.*) ;; *) fail "o dominio precisa ser completo (com ponto): $dominio" ;; esac
case "$email" in *@*.*) ;; *) fail "e-mail invalido: $email" ;; esac

# Aleatorios: Base64 de 32 bytes (chaves) e hexadecimal (senhas, so letras e numeros).
chave32() { head -c 32 /dev/urandom | base64 | tr -d '\n'; }
senha() { head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n'; }
frase() { head -c 36 /dev/urandom | base64 | tr -d '\n'; }

# Copia o modelo para o destino trocando o valor de cada "CHAVE=" que esta VAZIA no modelo. O valor nunca passa por argv de outro comando.
preencher() {
  local modelo="$1" destino="$2"; shift 2
  [ -f "$modelo" ] || fail "modelo nao encontrado: $modelo"
  if [ -e "$destino" ]; then
    log "JA EXISTE, nao foi tocado: $destino"
    return 0
  fi
  local linha nome par achou
  ( umask 077
    : > "$destino"
    while IFS= read -r linha || [ -n "$linha" ]; do
      nome="${linha%%=*}"
      achou=0
      if [ "$linha" != "$nome" ] && [ "${linha#*=}" = "" ]; then
        for par in "$@"; do
          if [ "${par%%=*}" = "$nome" ]; then
            printf '%s=%s\n' "$nome" "${par#*=}" >> "$destino"
            achou=1
            break
          fi
        done
      fi
      [ "$achou" -eq 1 ] || printf '%s\n' "$linha" >> "$destino"
    done < "$modelo"
  )
  chmod 600 "$destino"
  log "criado (permissao 600): $destino"
}

preencher "$DEPLOY_DIR/.env.example" "$DEPLOY_DIR/.env" \
  "DOMAIN=$dominio" "ACME_EMAIL=$email" "POSTGRES_PASSWORD=$(senha)" "APP_DB_PASSWORD=$(senha)"

preencher "$DEPLOY_DIR/app.env.example" "$DEPLOY_DIR/app.env" \
  "Channels__Secrets__Keys__ch1=$(chave32)" \
  "AiGateway__Secrets__Keys__ai1=$(chave32)" \
  "Capabilities__Secrets__Keys__cp1=$(chave32)" \
  "Conversations__Attachments__Encryption__Keys__an1=$(chave32)" \
  "Agents__Options__KnowledgeGapHashKey=$(chave32)"

preencher "$DEPLOY_DIR/backup.env.example" "$DEPLOY_DIR/backup.env" "BACKUP_PASSPHRASE=$(frase)"

cat >&2 <<'FIM'

PRONTO. Os valores NAO foram impressos. Proximos passos obrigatorios:
  1. GUARDE UMA COPIA FORA DESTA MAQUINA (cofre ou gerenciador de senhas) destes tres arquivos:
       deploy/app.env      (chaves: perder a dos anexos e perder os arquivos cifrados)
       deploy/backup.env   (a frase do backup: sem ela nenhum backup pode ser restaurado)
       deploy/.env         (senhas do banco)
  2. Preencha em deploy/backup.env o destino da copia externa (BACKUP_EXTERNAL_DEST) e, se quiser, o webhook de alerta (ALERT_WEBHOOK_URL).
  3. Rode scripts/preflight.sh e, se passar, scripts/subir.sh.
FIM
