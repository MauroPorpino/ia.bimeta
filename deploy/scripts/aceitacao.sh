#!/usr/bin/env bash
# TESTE DE ACEITACAO da implantacao (ADR 0033): prova, numa VM de TESTE, que a pilha sobe, expoe so o que deve, fala com o ClamAV real, persiste dados,
# se recupera de falha de servico, faz backup, verifica, detecta adulteracao e RESTAURA num ambiente descartavel, e nao vaza segredo em log.
# E DESTRUTIVO para a disponibilidade: derruba conteineres de proposito. NUNCA rode numa pilha com atendimento real em andamento.
# Uso: aceitacao.sh --confirmo-que-e-teste
# Saida: 0 so se TODAS as verificacoes passarem. Nao chama a Meta nem a OpenAI. Nao grava nada de cliente.
set -uo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[ "${1:-}" = "--confirmo-que-e-teste" ] || fail "este teste derruba servicos de proposito. Rode so em ambiente de TESTE: aceitacao.sh --confirmo-que-e-teste"

ok_total=0
falhas=0
passou() { printf 'PASSOU  %s\n' "$*"; ok_total=$((ok_total + 1)); }
falhou() { printf 'FALHOU  %s\n' "$*"; falhas=$((falhas + 1)); }
secao() { printf '\n--- %s\n' "$*"; }
checa() { local descricao="$1"; shift; if "$@" >/dev/null 2>&1; then passou "$descricao"; else falhou "$descricao"; fi; }

id_de() { compose ps -q "$1" 2>/dev/null | head -n1; }
estado_de() { "$DOCKER" inspect -f '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}-{{end}}' "$(id_de "$1")" 2>/dev/null; }
bom() { case "$(estado_de "$1")" in running\|healthy|running\|-) return 0 ;; *) return 1 ;; esac; }
esperar_bom() { local servico="$1" limite="${2:-180}" inicio; inicio="$(date +%s)"; while ! bom "$servico"; do [ $(( $(date +%s) - inicio )) -lt "$limite" ] || return 1; sleep 3; done; }

read_env_file "$DEPLOY_DIR/.env"
versao="${IABIMETA_VERSAO:-local}"
curl_local() { local opcoes=(-sS -m 20 --resolve "${DOMAIN}:443:127.0.0.1"); case "${ACME_CA:-}" in *staging*) opcoes+=(-k) ;; esac; curl "${opcoes[@]}" "$@"; }

secao "1. Pre-requisitos e configuracao"
checa "preflight sem erros" "$DEPLOY_DIR/scripts/preflight.sh"
checa "docker compose config valido" compose config -q

secao "2. Subida"
if ! bom db || ! bom api || ! bom web; then
  checa "scripts/subir.sh conclui" "$DEPLOY_DIR/scripts/subir.sh"
fi
for servico in db clamav api worker web; do
  esperar_bom "$servico" 240 && passou "servico $servico no ar e saudavel" || falhou "servico $servico NAO ficou saudavel ($(estado_de "$servico"))"
done
checa "Caddyfile valido (caddy validate)" "$DOCKER" run --rm -e "DOMAIN=$DOMAIN" -e "ACME_EMAIL=${ACME_EMAIL}" -e "ACME_CA=${ACME_CA:-https://acme-v02.api.letsencrypt.org/directory}" "iabimeta/web:${versao}" caddy validate --config /etc/caddy/Caddyfile
checa "migrations em dia (migrate --list)" "$DEPLOY_DIR/scripts/cli.sh" migrate --list

secao "3. Exposicao: so o proxy publica portas (80 e 443)"
for servico in db clamav api worker; do
  publicadas="$("$DOCKER" port "$(id_de "$servico")" 2>/dev/null | tr '\n' ' ')"
  [ -z "${publicadas// /}" ] && passou "$servico nao publica porta alguma" || falhou "$servico publica portas: $publicadas"
done
extras="$("$DOCKER" port "$(id_de web)" 2>/dev/null | grep -vE '^(80|443)/tcp' || true)"
[ -z "$extras" ] && passou "web publica so 80 e 443" || falhou "web publica portas alem de 80 e 443: $extras"
if command -v ss >/dev/null 2>&1; then
  vazadas="$(ss -ltnH 2>/dev/null | awk '{print $4}' | grep -E '[:.](5432|3310|8080)$' || true)"
  [ -z "$vazadas" ] && passou "nenhuma porta de banco, ClamAV ou Api escutando no host" || falhou "portas internas escutando no host: $vazadas"
fi

secao "4. Proxy HTTPS e caminhos publicos (local, sem depender da rede publica)"
codigo="$(curl_local -o /dev/null -w '%{http_code}' "https://${DOMAIN}/api/health" || echo 000)"
[ "$codigo" = "200" ] && passou "GET /api/health responde 200 pelo HTTPS do Caddy" || falhou "GET /api/health respondeu $codigo (esperado 200)"
corpo="$(curl_local "https://${DOMAIN}/" || true)"
case "$corpo" in *'id="root"'*) passou "a interface React e servida em /" ;; *) falhou "a pagina inicial nao parece a interface React" ;; esac
codigo="$(curl_local -o /dev/null -w '%{http_code}' "https://${DOMAIN}/api/simulator/health" || echo 000)"
[ "$codigo" = "404" ] && passou "as rotas de desenvolvimento (/simulator) nao existem em producao" || falhou "/api/simulator respondeu $codigo (esperado 404)"
codigo="$(curl_local -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data '{}' "https://${DOMAIN}/webhooks/whatsapp/id-inexistente" || echo 000)"
case "$codigo" in 400|401|403|404) passou "o webhook chega ao Api e e recusado sem assinatura valida ($codigo)" ;; *) falhou "POST no webhook sem assinatura respondeu $codigo (esperado 4xx do Api, nunca 5xx)" ;; esac

secao "5. ClamAV real (benigno, tamanho maximo e amostra EICAR)"
checa "attachments test-scanner (20 MB) contra o clamd real" "$DEPLOY_DIR/scripts/teste-clamav.sh" 20

secao "6. Persistencia depois de derrubar e subir a pilha (sem apagar volumes)"
compose exec -T db psql -U postgres -d iabimeta -v ON_ERROR_STOP=1 -c "CREATE TABLE IF NOT EXISTS public.aceitacao_marcador (v text); INSERT INTO public.aceitacao_marcador VALUES ('persistiu');" >/dev/null 2>&1 \
  && passou "marcador gravado no banco" || falhou "nao foi possivel gravar o marcador no banco"
compose run --rm --no-deps -T --entrypoint sh api -c 'echo persistiu > /var/lib/iabimeta/anexos/.aceitacao' >/dev/null 2>&1 \
  && passou "marcador gravado no volume de anexos" || falhou "nao foi possivel gravar o marcador no volume de anexos"
compose down >/dev/null 2>&1 && passou "pilha derrubada (volumes preservados)" || falhou "docker compose down falhou"
compose up -d >/dev/null 2>&1 && passou "pilha recriada" || falhou "docker compose up falhou"
for servico in db api worker web; do esperar_bom "$servico" 240 || falhou "$servico nao voltou depois do down/up"; done
[ "$(compose exec -T db psql -U postgres -d iabimeta -Atc 'SELECT v FROM public.aceitacao_marcador LIMIT 1' 2>/dev/null)" = "persistiu" ] \
  && passou "o dado do banco sobreviveu" || falhou "o dado do banco NAO sobreviveu"
[ "$(compose run --rm --no-deps -T --entrypoint sh api -c 'cat /var/lib/iabimeta/anexos/.aceitacao' 2>/dev/null | tr -d '\r\n')" = "persistiu" ] \
  && passou "o arquivo do volume de anexos sobreviveu" || falhou "o arquivo do volume de anexos NAO sobreviveu"

secao "7. Falha de servico: o Docker reinicia sozinho e o ambiente se recupera"
for servico in worker api db; do
  id="$(id_de "$servico")"
  antes="$("$DOCKER" inspect -f '{{.State.StartedAt}}' "$id" 2>/dev/null)"
  "$DOCKER" kill "$id" >/dev/null 2>&1
  inicio="$(date +%s)"; voltou=0
  while [ $(( $(date +%s) - inicio )) -lt 150 ]; do
    depois="$("$DOCKER" inspect -f '{{.State.StartedAt}}' "$id" 2>/dev/null)"
    if [ "$depois" != "$antes" ] && bom "$servico"; then voltou=1; break; fi
    sleep 3
  done
  [ "$voltou" -eq 1 ] && passou "$servico morto a forca foi reiniciado sozinho e voltou saudavel" || falhou "$servico NAO voltou depois de morto a forca"
done
esperar_bom api 180 || true
checa "o ia.bimeta nao fica CRITICO depois das quedas (status)" bash -c "'$DEPLOY_DIR/scripts/cli.sh' status --api-url http://api:8080/health; test \$? -lt 2"

secao "8. ClamAV fora do ar: o arquivo nunca e liberado e o ambiente acusa"
compose stop clamav >/dev/null 2>&1
saida="$("$DEPLOY_DIR/scripts/cli.sh" attachments report 2>&1)"; codigo=$?
{ [ "$codigo" -eq 1 ] && printf '%s' "$saida" | grep -q 'responde agora: N'; } && passou "attachments report acusa o verificador fora do ar (saida 1)" || falhou "attachments report nao acusou o verificador fora do ar (saida $codigo)"
compose start clamav >/dev/null 2>&1
esperar_bom clamav 900 && passou "ClamAV voltou" || falhou "ClamAV nao voltou"

secao "9. Backup, verificacao, adulteracao e restauracao num ambiente descartavel"
checa "backup.sh --sem-externo conclui" "$DEPLOY_DIR/scripts/backup.sh" --sem-externo
load_backup_env
ultimo="$(ls -1t "$BACKUP_DIR"/iabimeta-*.tar.gpg 2>/dev/null | head -n1)"
if [ -n "$ultimo" ]; then
  checa "o backup cifrado passa na verificacao completa" "$DEPLOY_DIR/scripts/verificar-backup.sh" "$ultimo"
  adulterado="$BACKUP_DIR/adulterado-teste.tar.gpg"
  cp "$ultimo" "$adulterado" && cp "$ultimo.sha256" "$adulterado.sha256" 2>/dev/null || true
  printf 'X' | dd of="$adulterado" bs=1 seek=100 conv=notrunc 2>/dev/null
  "$DEPLOY_DIR/scripts/verificar-backup.sh" "$adulterado" >/dev/null 2>&1 && falhou "a verificacao ACEITOU um backup adulterado" || passou "a verificacao reprova um backup adulterado"
  rm -f "$adulterado" "$adulterado.sha256"
  checa "ensaio de restauracao num projeto descartavel conclui" "$DEPLOY_DIR/scripts/ensaiar-restauracao.sh" "$ultimo"
else
  falhou "nenhum backup foi criado"
fi

secao "10. Nenhum segredo em log"
segredos="$(mktemp)"; registros="$(mktemp)"
chmod 600 "$segredos" "$registros"
for arquivo in .env app.env backup.env; do
  while IFS= read -r linha || [ -n "$linha" ]; do
    nome="${linha%%=*}"; valor="${linha#*=}"
    case "$nome" in *PASSWORD*|*Keys__*|*KnowledgeGapHashKey*|BACKUP_PASSPHRASE) [ "${#valor}" -ge 16 ] && printf '%s\n' "$valor" >> "$segredos" ;; esac
  done < "$DEPLOY_DIR/$arquivo"
done
compose logs --no-color > "$registros" 2>&1
if grep -F -q -f "$segredos" "$registros"; then falhou "um SEGREDO apareceu nos logs dos conteineres"; else passou "nenhum segredo (senha, chave ou frase) aparece nos logs"; fi
rm -f "$segredos" "$registros"

secao "11. Limpeza dos marcadores"
compose exec -T db psql -U postgres -d iabimeta -c 'DROP TABLE IF EXISTS public.aceitacao_marcador' >/dev/null 2>&1 || true
compose run --rm --no-deps -T --entrypoint sh api -c 'rm -f /var/lib/iabimeta/anexos/.aceitacao' >/dev/null 2>&1 || true

printf '\nVerificacoes: %s passaram, %s falharam.\n' "$ok_total" "$falhas"
[ "$falhas" -eq 0 ] && { echo "ACEITACAO: TODAS AS VERIFICACOES PASSARAM."; exit 0; } || { echo "ACEITACAO: HA FALHAS (veja as linhas FALHOU)."; exit 1; }
