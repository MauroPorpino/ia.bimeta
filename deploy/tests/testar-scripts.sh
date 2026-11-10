#!/usr/bin/env bash
# Teste automatizado dos scripts de BACKUP e RESTAURACAO da implantacao Linux (deploy/scripts: backup.sh, verificar-backup.sh, restaurar.sh,
# ensaiar-restauracao.sh). Roda com:  bash deploy/tests/testar-scripts.sh   (Git Bash no Windows ou bash no Linux).
#
# NAO usa Docker, PostgreSQL, rede nem o deploy/.env real: um docker FALSO (deploy/tests/falso/docker) imita so os comandos do contrato, com tar e
# sha256sum de verdade, e tudo roda em pastas temporarias (apagadas no fim). gpg, tar e sha256sum sao os DE VERDADE. Cada verificacao imprime PASSOU ou
# FALHOU; o codigo de saida e 0 so se todas passarem.
# O que este teste NAO prova (so uma VM com Docker de verdade prova): pg_dump/pg_restore reais, o volume real, o usuario do conteiner (UID),
# a saude real do PostgreSQL, rsync/ssh reais, permissoes do Ubuntu e o cron.
set -euo pipefail

AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_ORIG="$(cd "$AQUI/.." && pwd)"
FRASE="frase-de-teste-nao-secreta"
INICIO_TESTE=$SECONDS
unset COMPOSE_PROJECT EDGE_SUBNET EDGE_WEB_IP DOCKER_BIN BACKUP_PASSPHRASE BACKUP_DIR BACKUP_EXTERNAL_DEST || true

for c in gpg tar sha256sum find awk sed od dd; do
  command -v "$c" >/dev/null 2>&1 || { echo "FALHOU  ferramenta necessaria nao encontrada: $c"; exit 1; }
done

# ---------------------------------------------------------------------------------------------------------------------------------
# Preparo: pasta temporaria, GNUPGHOME proprio, falsos no PATH (e a emulacao de permissoes onde o sistema de arquivos nao as guarda).
# ---------------------------------------------------------------------------------------------------------------------------------
RAIZ="$(mktemp -d "${TMPDIR:-/tmp}/iabimeta-teste.XXXXXX")"
GPG_REAL="$(command -v gpg)"
REAL_CHMOD="$(command -v chmod)"
REAL_STAT="$(command -v stat)"
export GPG_REAL REAL_CHMOD REAL_STAT
cp -R "$AQUI/falso" "$RAIZ/falso"
FALSO="$RAIZ/falso"
"$REAL_CHMOD" +x "$FALSO/docker" "$FALSO/gpg" "$FALSO/rsync" "$FALSO/windows/chmod" "$FALSO/windows/stat"
export GNUPGHOME="$RAIZ/gnupg"
mkdir "$GNUPGHOME"
"$REAL_CHMOD" 700 "$GNUPGHOME"
export FALSO_MODOS="$RAIZ/modos"
mkdir "$FALSO_MODOS"

: >"$RAIZ/p"
"$REAL_CHMOD" 600 "$RAIZ/p"
if [ "$("$REAL_STAT" -c %a "$RAIZ/p")" = "600" ]; then
  PERM_REAL=1
  PATH="$FALSO:$PATH"
else
  PERM_REAL=0
  PATH="$FALSO/windows:$FALSO:$PATH"
fi
export PATH

limpar_teste() {
  gpgconf --kill gpg-agent >/dev/null 2>&1 || true
  rm -rf "$RAIZ" 2>/dev/null || true
}
trap limpar_teste EXIT

PASSOU=0
FALHOU=0
ok() { printf 'PASSOU  %s\n' "$1"; PASSOU=$((PASSOU + 1)); }
nok() { printf 'FALHOU  %s\n' "$1"; FALHOU=$((FALHOU + 1)); }
# t "descricao" comando args...  -> PASSOU se o comando sair 0.
t() {
  local d="$1"
  shift
  if "$@" >/dev/null 2>&1; then ok "$d"; else nok "$d"; fi
}
not() { ! "$@"; }
contem() { grep -qF -- "$2" "$1"; }
contem_bin() { grep -qaF -- "$2" "$1"; }
modo() { stat -c %a "$1"; }
igual() { [ "$1" = "$2" ]; }
existe() { [ -e "$1" ]; }
sha_do() { sha256sum "$1" | cut -d' ' -f1; }
sha_confere() { (cd "$(dirname "$1")" && sha256sum -c "$(basename "$1").sha256" >/dev/null); }
# O hash de uma arvore de arquivos (nome + conteudo).
arvore() {
  (cd "$1" && find . -type f | LC_ALL=C sort | while IFS= read -r f; do printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$f"; done | sha256sum | cut -d' ' -f1)
}
refazer_sha() { printf '%s  %s\n' "$(sha_do "$1")" "$(basename "$1")" >"$1.sha256"; }

# ---------------------------------------------------------------------------------------------------------------------------------
# Ambientes: cada cenario tem a sua copia de deploy/, o seu estado do docker falso e as suas pastas.
# ---------------------------------------------------------------------------------------------------------------------------------
A="" DEP="" BK="" EXT="" EST=""
OUT="" RC=0
N_SAIDA=0
VARS=()

escrever_backup_env() { # escrever_backup_env <frase> [destino-externo]
  {
    [ -z "$1" ] || printf 'BACKUP_PASSPHRASE=%s\n' "$1"
    printf 'BACKUP_DIR=%s\n' "$BK"
    printf 'BACKUP_KEEP_DAYS=14\n'
    printf 'BACKUP_EXTERNAL_DEST=%s\n' "${2:-}"
  } >"$DEP/backup.env"
  chmod 600 "$DEP/backup.env"
}

novo_ambiente() { # novo_ambiente <nome>
  A="$RAIZ/$1"
  DEP="$A/deploy"
  BK="$A/backups"
  EXT="$A/externo"
  EST="$A/estado"
  mkdir -p "$DEP/scripts" "$BK" "$EXT" "$EST" "$A/tmp"
  cp "$DEPLOY_ORIG"/scripts/*.sh "$DEP/scripts/"
  cp "$DEPLOY_ORIG/docker-compose.yml" "$DEP/"
  chmod +x "$DEP"/scripts/*.sh
  printf 'DOMAIN=teste.invalido\nPOSTGRES_PASSWORD=senha-pg-de-mentira\nAPP_DB_PASSWORD=senha-app-de-mentira\n' >"$DEP/.env"
  printf 'Conversations__Attachments__Encryption__Keys__an1=chave-de-mentira\n' >"$DEP/app.env"
  chmod 600 "$DEP/.env" "$DEP/app.env"
  escrever_backup_env "$FRASE" ""
  VARS=()
}

# Poe no estado do docker falso um ambiente de PRODUCAO: o "banco" (com um marcador em texto claro) e 4 objetos de anexo (um com marcador).
popular_producao() {
  local p="$EST/volumes/iabimeta_pgdata" a="$EST/volumes/iabimeta_anexos"
  local t1="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" t2="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  mkdir -p "$p" "$a/$t1" "$a/$t2" "$EST/rodando/iabimeta"
  { printf 'LINHA-SQL-MARCADOR-DO-DUMP-998877\n'; head -c 60000 /dev/urandom; } >"$p/dados.bin"
  { printf 'ANEXO-MARCADOR-EM-TEXTO-CLARO-554433\n'; head -c 20000 /dev/urandom; } >"$a/$t1/11111111111111111111111111111111.bin"
  head -c 30000 /dev/urandom >"$a/$t1/22222222222222222222222222222222.bin"
  head -c 5000 /dev/urandom >"$a/$t2/33333333333333333333333333333333.bin"
  head -c 1 /dev/urandom >"$a/$t2/44444444444444444444444444444444.bin"
  : >"$EST/rodando/iabimeta/db"
  : >"$EST/rodando/iabimeta/api"
  : >"$EST/rodando/iabimeta/worker"
  : >"$EST/rodando/iabimeta/web"
}

# Roda um script do ambiente atual (de outro diretorio, para provar que nao depende do cwd). Saida em $OUT, codigo em $RC.
rodar() { # rodar <script> [args...]
  local script="$1"
  shift
  N_SAIDA=$((N_SAIDA + 1))
  OUT="$A/saida.$N_SAIDA.txt"
  mkdir -p "$A/tmp"
  RC=0
  (
    cd "$RAIZ"
    env DOCKER_BIN="$FALSO/docker" FALSO_ESTADO="$EST" GPG_ARGV_LOG="$EST/gpg-argv.log" TMPDIR="$A/tmp" DB_WAIT_INTERVAL=0 DB_WAIT_TIMEOUT=20 \
      "${VARS[@]+"${VARS[@]}"}" bash "$DEP/scripts/$script" "$@"
  ) >"$OUT" 2>&1 || RC=$?
}
rodar_bg() { # rodar_bg <script> [args...]: em segundo plano; saida em $A/saida.bg.txt e codigo em $A/rc.bg
  local script="$1"
  shift
  rm -f "$A/rc.bg"
  (
    rc=0
    (
      cd "$RAIZ"
      env DOCKER_BIN="$FALSO/docker" FALSO_ESTADO="$EST" GPG_ARGV_LOG="$EST/gpg-argv.log" TMPDIR="$A/tmp" DB_WAIT_INTERVAL=0 DB_WAIT_TIMEOUT=20 \
        "${VARS[@]+"${VARS[@]}"}" bash "$DEP/scripts/$script" "$@"
    ) >"$A/saida.bg.txt" 2>&1 || rc=$?
    echo "$rc" >"$A/rc.bg"
  ) &
  BG_PID=$!
}
fazer_backup() { # fazer_backup [args...]  (espera 1 s: o nome do arquivo tem resolucao de segundos)
  sleep 1
  rodar backup.sh "$@"
}
ultimo_arquivo() { find "$BK" -maxdepth 1 -type f -name 'iabimeta-????????T??????Z.tar.gpg' | LC_ALL=C sort -r | head -n1 || true; }
n_linhas() { wc -l <"$1" | tr -d ' '; }
linha_de() { grep -n -- "$2" "$1" | head -n1 | cut -d: -f1 || true; }
sem_sobras() { # sem_sobras <pasta-de-backup> <pasta-tmp>: nenhuma pasta temporaria nem .partial
  [ -z "$(find "$1" -maxdepth 1 \( -name '.tmp.*' -o -name '*.partial' \) 2>/dev/null)" ] && [ -z "$(ls -A "$2" 2>/dev/null)" ]
}

# ---- Ferramentas para fabricar variantes de um backup (usam o gpg e a frase de verdade) ----
gpg_dec() { gpg --batch --quiet --no-tty --pinentry-mode loopback --passphrase-fd 3 --decrypt -- "$1" 3<<<"${2:-$FRASE}"; }
decifrar_dir() { gpg_dec "$1" | tar -xf - -C "$2"; }
cifrar_dir() { # cifrar_dir <dir> <saida> <arquivo...>
  local d="$1" saida="$2"
  shift 2
  tar -cf - -C "$d" "$@" | gpg --batch --yes --quiet --no-tty --pinentry-mode loopback --passphrase-fd 3 --symmetric --cipher-algo AES256 --compress-algo none --output "$saida" 3<<<"$FRASE"
  refazer_sha "$saida"
}
alterar_byte() { # alterar_byte <arquivo> <posicao>
  local b
  b="$(dd if="$1" bs=1 skip="$2" count=1 2>/dev/null | od -An -tu1 | tr -d ' ')"
  # shellcheck disable=SC2059
  printf "$(printf '\\%03o' $(((b + 1) % 256)))" | dd of="$1" bs=1 seek="$2" conv=notrunc 2>/dev/null
}
tamanho() { wc -c <"$1" | tr -d ' '; }

# =================================================================================================================================
echo "== Verificacoes estaticas dos arquivos (ASCII, LF, sem BOM, cabecalho, frase fora da linha de comando)"
# =================================================================================================================================
ASCII_NAO() { [ "$(LC_ALL=C tr -d '\000-\177' <"$1" | wc -c | tr -d ' ')" = "0" ]; }
sem_cr() { ! LC_ALL=C grep -q "$(printf '\r')" "$1"; }
sem_bom() { [ "$(head -c 3 "$1" | od -An -tx1 | tr -d ' \n')" != "efbbbf" ]; }
termina_em_nl() { [ "$(tail -c 1 "$1" | od -An -tx1 | tr -d ' \n')" = "0a" ]; }
shebang_ok() { [ "$(head -n1 "$1")" = '#!/usr/bin/env bash' ]; }
tem_set() { grep -qx 'set -euo pipefail' "$1"; }
carrega_lib() { grep -qF '. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"' "$1"; }
PROIBIDO='--pass''phrase([[:space:]=]|$)'
sem_frase_no_argv() { ! grep -nE -- "$PROIBIDO" "$1"; }
sintaxe_ok() { bash -n "$1"; }

TODOS=("$DEPLOY_ORIG/scripts/backup.sh" "$DEPLOY_ORIG/scripts/verificar-backup.sh" "$DEPLOY_ORIG/scripts/restaurar.sh" "$DEPLOY_ORIG/scripts/ensaiar-restauracao.sh"
  "$AQUI/testar-scripts.sh" "$AQUI/falso/docker" "$AQUI/falso/rsync" "$AQUI/falso/gpg" "$AQUI/falso/windows/chmod" "$AQUI/falso/windows/stat")
for f in "${TODOS[@]}"; do
  n="${f#"$DEPLOY_ORIG"/}"
  t "$n: somente ASCII (sem acento)" ASCII_NAO "$f"
  t "$n: fim de linha LF (sem CR)" sem_cr "$f"
  t "$n: sem BOM" sem_bom "$f"
  t "$n: termina com nova linha" termina_em_nl "$f"
  t "$n: #!/usr/bin/env bash na primeira linha" shebang_ok "$f"
  t "$n: set -euo pipefail" tem_set "$f"
  t "$n: sintaxe valida (bash -n)" sintaxe_ok "$f"
  t "$n: nenhuma frase na linha de comando (so o descritor de arquivo)" sem_frase_no_argv "$f"
done
for f in "${TODOS[@]:0:4}"; do
  t "${f#"$DEPLOY_ORIG"/}: carrega a lib.sh pela forma padrao" carrega_lib "$f"
done

# =================================================================================================================================
echo "== (a)(b) backup: arquivo cifrado 0600 + .sha256, ultimo-backup-ok, ordem dump antes dos anexos, manifesto, nada em texto claro"
# =================================================================================================================================
novo_ambiente basico
popular_producao
fazer_backup --sem-externo
t "backup: sai 0" igual "$RC" 0
ARQ="$(ultimo_arquivo)"
BASICO_A="$A"
BASICO_EST="$EST"
BASICO_ARQ="$ARQ"
t "backup: cria o iabimeta-<UTC>.tar.gpg" test -n "$ARQ" -a -f "$ARQ"
t "backup: o nome segue iabimeta-yyyymmddThhmmssZ.tar.gpg" bash -c "[[ \"\$(basename '$ARQ')\" =~ ^iabimeta-[0-9]{8}T[0-9]{6}Z\.tar\.gpg\$ ]]"
t "backup: cria o .sha256 ao lado, no formato do sha256sum, que confere" sha_confere "$ARQ"
t "backup: o .tar.gpg tem permissao 600" igual "$(modo "$ARQ")" 600
t "backup: o .sha256 tem permissao 600" igual "$(modo "$ARQ.sha256")" 600
t "backup: ultimo-backup-ok tem 2 linhas (carimbo UTC e nome do arquivo)" test "$(n_linhas "$BK/ultimo-backup-ok")" = 2
t "backup: ultimo-backup-ok aponta para o arquivo criado" igual "$(sed -n 2p "$BK/ultimo-backup-ok")" "$(basename "$ARQ")"
t "backup: ultimo-backup-ok tem o carimbo em ISO 8601 UTC" bash -c "[[ \"\$(sed -n 1p '$BK/ultimo-backup-ok')\" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\$ ]]"
t "backup: ultimo-backup-ok tem permissao 600" igual "$(modo "$BK/ultimo-backup-ok")" 600
t "backup: sem sobras (.tmp, .partial) na pasta de backup nem na pasta temporaria" sem_sobras "$BK" "$A/tmp"
t "backup: a pasta de backup so tem os arquivos esperados" igual "$(ls -A "$BK" | LC_ALL=C sort | tr '\n' ' ')" "$(basename "$ARQ") $(basename "$ARQ").sha256 ultimo-backup-ok "

L_DUMP="$(linha_de "$EST/chamadas.log" 'exec -T db pg_dump')"
L_TAR="$(linha_de "$EST/chamadas.log" '--entrypoint tar api -C /var/lib/iabimeta/anexos -cf -')"
ordem_ok() { [ -n "$L_DUMP" ] && [ -n "$L_TAR" ] && [ "$L_DUMP" -lt "$L_TAR" ]; }
t "backup: o pg_dump foi chamado ANTES do tar dos anexos (regra do ADR 0032)" ordem_ok
t "backup: pg_dump e tar usaram so o contrato (a unica chamada do pg_dump e a do contrato)" igual "$(grep -c 'exec -T db pg_dump -U postgres -Fc iabimeta' "$EST/chamadas.log" || true)" 1

mkdir "$A/conteudo"
decifrar_dir "$ARQ" "$A/conteudo"
t "backup: o pacote tem exatamente banco.dump, anexos.tar e manifesto.txt" igual "$(ls -A "$A/conteudo" | LC_ALL=C sort | tr '\n' ' ')" "anexos.tar banco.dump manifesto.txt "
M="$A/conteudo/manifesto.txt"
t "manifesto: versao_formato=1" grep -qx 'versao_formato=1' "$M"
t "manifesto: criado_utc em ISO 8601" grep -qE '^criado_utc=[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$' "$M"
t "manifesto: banco_dump_sha256 confere com o dump emitido pelo pg_dump" grep -qx "banco_dump_sha256=$(tail -n1 "$EST/registro-dump" | cut -d' ' -f1)" "$M"
t "manifesto: banco_dump_bytes confere" grep -qx "banco_dump_bytes=$(tail -n1 "$EST/registro-dump" | cut -d' ' -f2)" "$M"
t "manifesto: anexos_tar_sha256 confere com o anexos.tar" grep -qx "anexos_tar_sha256=$(sha_do "$A/conteudo/anexos.tar")" "$M"
t "manifesto: anexos_tar_bytes confere" grep -qx "anexos_tar_bytes=$(tamanho "$A/conteudo/anexos.tar")" "$M"
t "manifesto: anexos_objetos=4 (os .bin do tar)" grep -qx 'anexos_objetos=4' "$M"
t "manifesto: imagens (compose config --images), em ordem estavel" bash -c "grep -q '^imagens=clamav/clamav:1.4_base-debian13-slim iabimeta/api:local iabimeta/web:local iabimeta/worker:local postgres:17$' '$M'"
t "manifesto: host" grep -qE '^host=[A-Za-z0-9._-]+$' "$M"
t "manifesto: so linhas chave=valor em ASCII" bash -c "! grep -qvE '^[a-z0-9_]+=[ -~]*\$' '$M'"
t "manifesto: nao contem a frase nem segredo do .env/app.env" bash -c "! grep -qE 'frase-de-teste|senha-pg|senha-app|chave-de-mentira' '$M'"
t "controle: o dump decifrado TEM o marcador em texto claro (o teste nao e vazio)" contem_bin "$A/conteudo/banco.dump" "LINHA-SQL-MARCADOR-DO-DUMP-998877"
t "controle: o tar decifrado TEM o marcador do anexo em texto claro" contem_bin "$A/conteudo/anexos.tar" "ANEXO-MARCADOR-EM-TEXTO-CLARO-554433"
t "backup: o .tar.gpg NAO contem o texto claro do dump" not contem_bin "$ARQ" "LINHA-SQL-MARCADOR-DO-DUMP-998877"
t "backup: o .tar.gpg NAO contem o texto claro de um anexo" not contem_bin "$ARQ" "ANEXO-MARCADOR-EM-TEXTO-CLARO-554433"
t "backup: o .tar.gpg NAO contem a frase" not contem_bin "$ARQ" "$FRASE"
t "backup: o .tar.gpg nao tem o nome dos arquivos internos (cifrado de ponta a ponta)" not contem_bin "$ARQ" "banco.dump"

GL="$EST/gpg-argv.log"
t "gpg: usou --symmetric com AES256" bash -c "grep -q -- '--symmetric' '$GL' && grep -q -- '--cipher-algo AES256' '$GL'"
t "gpg: sem compressao (--compress-algo none)" contem "$GL" "--compress-algo none"
t "gpg: --s2k-mode 3 --s2k-digest-algo SHA512 --s2k-count 65011712" bash -c "grep -q -- '--s2k-mode 3 --s2k-digest-algo SHA512 --s2k-count 65011712' '$GL'"
t "gpg: --batch --yes --pinentry-mode loopback" bash -c "grep -q -- '--batch --yes' '$GL' && grep -q -- '--pinentry-mode loopback' '$GL'"
t "gpg: a frase vai por descritor (--passphrase-fd 3)" contem "$GL" "--passphrase-fd 3"
t "gpg: a frase NUNCA aparece nos argumentos" not contem "$GL" "$FRASE"
t "docker: a frase e nenhum segredo aparecem nas chamadas" bash -c "! grep -qE 'frase-de-teste|senha-pg|senha-app|chave-de-mentira' '$EST/chamadas.log'"
t "backup: a frase nao aparece na saida nem nos logs" bash -c "! grep -rqF -e '$FRASE' '$A'/saida.*.txt '$EST'/*.log"
t "backup --sem-externo: nao registra alerta" not existe "$BK/alertas.log"

# =================================================================================================================================
echo "== (c) verificar-backup: integro passa; byte alterado, truncado, frase errada, .sha256 ausente reprovam"
# =================================================================================================================================
V="$A/variantes"
mkdir "$V"
rodar verificar-backup.sh "$ARQ"
t "verificar: arquivo integro passa (completo)" igual "$RC" 0
t "verificar: a saida diz OK e que o pg_restore -l leu o dump" grep -q 'pg_restore -l leu o dump' "$OUT"
t "verificar: nada sobra na pasta temporaria" sem_sobras "$BK" "$A/tmp"
rodar verificar-backup.sh "$ARQ" --rapido
t "verificar --rapido: passa (so o SHA-256 do cifrado)" igual "$RC" 0
rodar verificar-backup.sh
t "verificar: sem argumento sai 2 (uso)" igual "$RC" 2
rodar verificar-backup.sh "$ARQ" --opcao-que-nao-existe
t "verificar: opcao desconhecida sai 2 (uso)" igual "$RC" 2
rodar verificar-backup.sh "$ARQ" --rapido --extrair-para "$A/x"
t "verificar: --rapido com --extrair-para sai 2 (uso)" igual "$RC" 2
rodar verificar-backup.sh "$V/nao-existe.tar.gpg"
t "verificar: arquivo inexistente reprova (1)" igual "$RC" 1

T="$(tamanho "$ARQ")"
cp "$ARQ" "$V/alterado-sha-velho.tar.gpg"
cp "$ARQ.sha256" "$V/alterado-sha-velho.tar.gpg.sha256"
alterar_byte "$V/alterado-sha-velho.tar.gpg" $((T / 2))
rodar verificar-backup.sh "$V/alterado-sha-velho.tar.gpg"
t "verificar: um byte alterado (com o .sha256 original) reprova" igual "$RC" 1
t "verificar: a mensagem aponta o SHA-256 do cifrado" grep -q 'SHA-256 do arquivo cifrado' "$OUT"
rodar verificar-backup.sh "$V/alterado-sha-velho.tar.gpg" --rapido
t "verificar --rapido: um byte alterado tambem reprova" igual "$RC" 1

cp "$ARQ" "$V/alterado-sha-novo.tar.gpg"
alterar_byte "$V/alterado-sha-novo.tar.gpg" $((T / 2))
refazer_sha "$V/alterado-sha-novo.tar.gpg"
rodar verificar-backup.sh "$V/alterado-sha-novo.tar.gpg"
t "verificar: byte alterado MESMO com o .sha256 refeito reprova (a integridade da cifra pega)" igual "$RC" 1
rodar verificar-backup.sh "$V/alterado-sha-novo.tar.gpg" --rapido
t "verificar --rapido: com o .sha256 refeito passa (limite conhecido do modo rapido)" igual "$RC" 0

cp "$ARQ" "$V/truncado-sha-velho.tar.gpg"
cp "$ARQ.sha256" "$V/truncado-sha-velho.tar.gpg.sha256"
head -c $((T / 2)) "$ARQ" >"$V/truncado-sha-velho.tar.gpg"
rodar verificar-backup.sh "$V/truncado-sha-velho.tar.gpg"
t "verificar: arquivo truncado (com o .sha256 original) reprova" igual "$RC" 1
head -c $((T / 2)) "$ARQ" >"$V/truncado-sha-novo.tar.gpg"
refazer_sha "$V/truncado-sha-novo.tar.gpg"
rodar verificar-backup.sh "$V/truncado-sha-novo.tar.gpg"
t "verificar: arquivo truncado MESMO com o .sha256 refeito reprova" igual "$RC" 1
head -c $((T - 40)) "$ARQ" >"$V/truncado-fim.tar.gpg"
refazer_sha "$V/truncado-fim.tar.gpg"
rodar verificar-backup.sh "$V/truncado-fim.tar.gpg"
t "verificar: arquivo cortado so no final (perde a verificacao de integridade da cifra) reprova" igual "$RC" 1

cp "$ARQ" "$V/sem-sha.tar.gpg"
rodar verificar-backup.sh "$V/sem-sha.tar.gpg"
t "verificar: .sha256 ausente reprova" igual "$RC" 1
t "verificar: a mensagem diz que falta o .sha256" grep -q 'falta o arquivo' "$OUT"

OUTRA="outra-frase-errada-de-teste"
escrever_backup_env "$OUTRA" ""
rodar verificar-backup.sh "$ARQ"
t "verificar: frase errada reprova" igual "$RC" 1
t "verificar: a saida nao imprime a frase errada nem a certa" bash -c "! grep -qF -e '$OUTRA' -e '$FRASE' '$OUT'"
escrever_backup_env "" ""
rodar verificar-backup.sh "$ARQ"
t "verificar: sem BACKUP_PASSPHRASE no backup.env reprova (nao ha como decifrar)" igual "$RC" 1
rodar verificar-backup.sh "$ARQ" --rapido
t "verificar --rapido: passa mesmo sem frase configurada" igual "$RC" 0
escrever_backup_env "$FRASE" ""

# Variantes feitas decifrando e reempacotando com a frase certa: so a verificacao completa pega.
D="$A/var-tmp"
mkdir "$D"
decifrar_dir "$ARQ" "$D"
echo "intruso" >"$D/extra.txt"
cifrar_dir "$D" "$V/arquivo-a-mais.tar.gpg" manifesto.txt banco.dump anexos.tar extra.txt
rodar verificar-backup.sh "$V/arquivo-a-mais.tar.gpg"
t "verificar: um arquivo A MAIS dentro do pacote reprova" igual "$RC" 1
cifrar_dir "$D" "$V/arquivo-a-menos.tar.gpg" manifesto.txt banco.dump
rodar verificar-backup.sh "$V/arquivo-a-menos.tar.gpg"
t "verificar: um arquivo A MENOS no pacote reprova" igual "$RC" 1
rm -f "$D/extra.txt"
sed 's/^banco_dump_sha256=.*/banco_dump_sha256=0000000000000000000000000000000000000000000000000000000000000000/' "$D/manifesto.txt" >"$D/manifesto.novo"
mv "$D/manifesto.novo" "$D/manifesto.txt"
cifrar_dir "$D" "$V/manifesto-adulterado.tar.gpg" manifesto.txt banco.dump anexos.tar
rodar verificar-backup.sh "$V/manifesto-adulterado.tar.gpg"
t "verificar: manifesto com SHA-256 do dump errado reprova" igual "$RC" 1
decifrar_dir "$ARQ" "$D" 2>/dev/null || true
printf 'x' >>"$D/banco.dump"
cifrar_dir "$D" "$V/dump-alterado.tar.gpg" manifesto.txt banco.dump anexos.tar
rodar verificar-backup.sh "$V/dump-alterado.tar.gpg"
t "verificar: dump alterado depois do manifesto reprova" igual "$RC" 1
rm -rf "$D"
mkdir "$D"
decifrar_dir "$ARQ" "$D"
printf 'x' >>"$D/anexos.tar"
cifrar_dir "$D" "$V/anexos-alterado.tar.gpg" manifesto.txt banco.dump anexos.tar
rodar verificar-backup.sh "$V/anexos-alterado.tar.gpg"
t "verificar: anexos.tar alterado depois do manifesto reprova" igual "$RC" 1
rm -rf "$D"
mkdir "$D"
decifrar_dir "$ARQ" "$D"
head -c 100 /dev/urandom >"$D/banco.dump"
sed -i "s/^banco_dump_sha256=.*/banco_dump_sha256=$(sha_do "$D/banco.dump")/; s/^banco_dump_bytes=.*/banco_dump_bytes=100/" "$D/manifesto.txt"
cifrar_dir "$D" "$V/dump-sem-cabecalho.tar.gpg" manifesto.txt banco.dump anexos.tar
rodar verificar-backup.sh "$V/dump-sem-cabecalho.tar.gpg"
t "verificar: banco.dump que nao e um dump -Fc (sem PGDMP) reprova mesmo com o manifesto coerente" igual "$RC" 1
rm -rf "$D"

VARS=(FALSO_FALHAR_LISTA=1)
rodar verificar-backup.sh "$ARQ"
t "verificar: se o pg_restore -l nao le o dump, reprova" igual "$RC" 1
VARS=()
rm -f "$EST/rodando/iabimeta/db"
rodar verificar-backup.sh "$ARQ"
t "verificar: com o db fora do ar PULA so o pg_restore -l (aviso) e passa" igual "$RC" 0
t "verificar: o aviso do item pulado aparece" grep -q 'PULADO' "$OUT"
: >"$EST/rodando/iabimeta/db"
mkdir "$A/extraido"
rodar verificar-backup.sh "$ARQ" --extrair-para "$A/extraido"
t "verificar --extrair-para: deixa os 3 arquivos verificados na pasta" igual "$(ls -A "$A/extraido" | LC_ALL=C sort | tr '\n' ' ')" "anexos.tar banco.dump manifesto.txt "
rodar verificar-backup.sh "$ARQ" --extrair-para "$A/extraido"
t "verificar --extrair-para: recusa pasta que nao esta vazia" igual "$RC" 1
t "verificar: nenhuma pasta temporaria sobrou depois de todos os cenarios" sem_sobras "$BK" "$A/tmp"
t "verificar: a frase nao aparece em nenhuma saida" bash -c "! grep -rqF -e '$FRASE' '$A'/saida.*.txt"

# =================================================================================================================================
echo "== (d) restaurar: recusa volumes existentes e restaura em volumes vazios com anexos byte-identicos"
# =================================================================================================================================
SRC_ARVORE="$(arvore "$BASICO_EST/volumes/iabimeta_anexos")"
SRC_DUMP_SHA="$(tail -n1 "$BASICO_EST/registro-dump" | cut -d' ' -f1)"

novo_ambiente nova
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar (VM nova): sai 0" igual "$RC" 0
t "restaurar: o dump restaurado e IDENTICO ao dumpado (SHA-256 lido pelo pg_restore)" igual "$(tail -n1 "$EST/registro-restore" | cut -d' ' -f1)" "$SRC_DUMP_SHA"
t "restaurar: os anexos restaurados sao byte-identicos (SHA-256 da arvore)" igual "$(arvore "$EST/volumes/iabimeta_anexos")" "$SRC_ARVORE"
t "restaurar: o pg_restore usou o contrato (--no-owner --no-privileges --role=iabimeta --exit-on-error)" contem "$EST/chamadas.log" "pg_restore -U postgres -d iabimeta --no-owner --no-privileges --role=iabimeta --exit-on-error"
L1="$(linha_de "$EST/chamadas.log" 'up -d db')"
L2="$(linha_de "$EST/chamadas.log" 'pg_restore -U postgres -d iabimeta')"
L3="$(linha_de "$EST/chamadas.log" '-xf -')"
L4="$(linha_de "$EST/chamadas.log" 'attachments check')"
ordem_restauracao() { [ -n "$L1" ] && [ -n "$L2" ] && [ -n "$L3" ] && [ -n "$L4" ] && [ "$L1" -lt "$L2" ] && [ "$L2" -lt "$L3" ] && [ "$L3" -lt "$L4" ]; }
t "restaurar: ordem db no ar -> pg_restore -> anexos -> attachments check" ordem_restauracao
t "restaurar: so o db sobe (um unico up, e e o up -d db)" igual "$(grep -c '|up ' "$EST/chamadas.log" || true)|$(grep -c '|up -d db' "$EST/chamadas.log" || true)" "1|1"
t "restaurar: roda o attachments check e mostra o resumo" contem "$OUT" "Linhas disponiveis conferidas : 4"
t "restaurar: imprime os proximos passos (migrate e subir.sh)" bash -c "grep -q 'scripts/cli.sh migrate' '$OUT' && grep -q 'scripts/subir.sh' '$OUT'"
t "restaurar: avisa que a chave dos anexos tem de ser a mesma da origem" grep -q 'MESMAS' "$OUT"
t "restaurar: nao sobra pasta temporaria (texto claro) em TMPDIR" sem_sobras "$BK" "$A/tmp"
t "restaurar: nunca chamou down (nao apaga nada)" bash -c "! grep -q 'down' '$EST/chamadas.log'"
t "restaurar: a frase nao aparece na saida" not contem "$OUT" "$FRASE"

# Recusa 1: o volume do banco ja existe.
novo_ambiente rec1
mkdir -p "$EST/volumes/iabimeta_pgdata"
echo "dados-existentes" >"$EST/volumes/iabimeta_pgdata/dados.bin"
ANTES="$(arvore "$EST/volumes")"
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: RECUSA se o volume pgdata ja existe (sai 1)" igual "$RC" 1
t "restaurar: a mensagem diz que o volume ja existe" grep -q 'JA EXISTE' "$OUT"
t "restaurar: na recusa nada foi alterado (volumes iguais)" igual "$(arvore "$EST/volumes")" "$ANTES"
t "restaurar: na recusa nao subiu nem restaurou nada" bash -c "! grep -qE 'up -d|pg_restore|-xf -' '$EST/chamadas.log'"

# Recusa 2: o volume de anexos existe e NAO esta vazio.
novo_ambiente rec2
mkdir -p "$EST/volumes/iabimeta_anexos/cccccccccccccccccccccccccccccccc"
echo "arquivo-existente" >"$EST/volumes/iabimeta_anexos/cccccccccccccccccccccccccccccccc/dddddddddddddddddddddddddddddddd.bin"
ANTES="$(arvore "$EST/volumes")"
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: RECUSA se o volume de anexos existe e nao esta vazio (sai 1)" igual "$RC" 1
t "restaurar: na recusa dos anexos nada foi alterado" igual "$(arvore "$EST/volumes")" "$ANTES"
t "restaurar: na recusa dos anexos nao subiu nem restaurou nada" bash -c "! grep -qE 'up -d|pg_restore|-xf -' '$EST/chamadas.log'"

# Aceita: o volume de anexos existe mas esta VAZIO.
novo_ambiente rec3
mkdir -p "$EST/volumes/iabimeta_anexos"
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: aceita volume de anexos existente e VAZIO (sai 0)" igual "$RC" 0
t "restaurar: no volume vazio os anexos tambem ficam identicos" igual "$(arvore "$EST/volumes/iabimeta_anexos")" "$SRC_ARVORE"

# Recusa 3: servico rodando.
novo_ambiente rec4
mkdir -p "$EST/rodando/iabimeta"
: >"$EST/rodando/iabimeta/api"
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: RECUSA com o api rodando (sai 1)" igual "$RC" 1
t "restaurar: a mensagem cita o servico rodando" grep -q 'rodando' "$OUT"
novo_ambiente rec5
mkdir -p "$EST/rodando/iabimeta"
: >"$EST/rodando/iabimeta/worker"
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: RECUSA com o worker rodando (sai 1)" igual "$RC" 1
novo_ambiente rec6
mkdir -p "$EST/rodando/iabimeta"
: >"$EST/rodando/iabimeta/web"
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: RECUSA com o web rodando (sai 1)" igual "$RC" 1

# Recusa 4: backup reprovado (byte alterado) ou .env ausente.
novo_ambiente rec7
rodar restaurar.sh "$V/alterado-sha-novo.tar.gpg"
t "restaurar: backup adulterado e REPROVADO e nada e restaurado (sai 1)" igual "$RC" 1
t "restaurar: com backup reprovado o db nem sobe" bash -c "! grep -qE 'up -d|pg_restore|-xf -' '$EST/chamadas.log'"
novo_ambiente rec8
rm -f "$DEP/app.env"
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: sem app.env recusa (sai 1)" igual "$RC" 1
novo_ambiente rec9
VARS=(FALSO_FALHAR_PGRESTORE=1)
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: falha do pg_restore sai 1" igual "$RC" 1
t "restaurar: a falha no meio avisa que nada foi apagado" grep -q 'NADA foi apagado' "$OUT"
t "restaurar: a falha no meio nao chamou down" bash -c "! grep -q 'down' '$EST/chamadas.log'"
novo_ambiente rec10
VARS=(FALSO_CHECK_SAIDA=1)
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: attachments check com divergencia (1) restaura, avisa e sai 0" igual "$RC" 0
t "restaurar: o aviso de divergencia aparece" grep -q 'divergencias' "$OUT"
novo_ambiente rec11
VARS=(FALSO_CHECK_SAIDA=2)
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: attachments check que FALHA (2) faz o script sair 1" igual "$RC" 1
novo_ambiente rec12
VARS=(FALSO_SAUDE_DEMORA=3)
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: espera o db ficar saudavel (starting -> healthy) e restaura" igual "$RC" 0
novo_ambiente rec13
VARS=(FALSO_SAUDE_DEMORA=100000 DB_WAIT_TIMEOUT=2 DB_WAIT_INTERVAL=1)
rodar restaurar.sh "$BASICO_ARQ"
t "restaurar: db que nunca fica saudavel estoura o prazo e sai 1" igual "$RC" 1
t "restaurar: o estouro do prazo nao restaurou nada" bash -c "! grep -qE 'pg_restore|-xf -' '$EST/chamadas.log'"

# =================================================================================================================================
echo "== (e) retencao: so o velho, nunca os 2 validos mais recentes, nunca o que foge do padrao ou esta fora de BACKUP_DIR"
# =================================================================================================================================
mk() { head -c 100 /dev/urandom >"$1"; }
mk_backup() { mk "$BK/iabimeta-$1.tar.gpg"; echo "x" >"$BK/iabimeta-$1.tar.gpg.sha256"; }

novo_ambiente ret1
popular_producao
escrever_backup_env "$FRASE" "$EXT"
for ts in 20200101T000000Z 20200201T000000Z 20200301T000000Z; do mk_backup "$ts"; done
mk "$BK/iabimeta-20200401T000000Z.tar.gpg.INVALIDO"
mk "$BK/outro.tar.gpg"
mk "$BK/iabimeta-notas.txt"
mk "$BK/iabimeta-20200101T000000Z.tar.gpg.bak"
mk "$BK/alertas.log"
mkdir -p "$BK/sub" "$A/fora"
mk "$BK/sub/iabimeta-20200101T000000Z.tar.gpg"
mk "$A/fora/iabimeta-20200101T000000Z.tar.gpg"
mk "$EXT/iabimeta-20200101T000000Z.tar.gpg"
echo "x" >"$EXT/iabimeta-20200101T000000Z.tar.gpg.sha256"
fazer_backup
t "retencao: o backup com copia externa em pasta local sai 0" igual "$RC" 0
NOVO="$(ultimo_arquivo)"
t "retencao: apagou o mais velho (2020-01) e o seu .sha256" bash -c "[ ! -e '$BK/iabimeta-20200101T000000Z.tar.gpg' ] && [ ! -e '$BK/iabimeta-20200101T000000Z.tar.gpg.sha256' ]"
t "retencao: apagou o 2020-02 e o seu .sha256" bash -c "[ ! -e '$BK/iabimeta-20200201T000000Z.tar.gpg' ] && [ ! -e '$BK/iabimeta-20200201T000000Z.tar.gpg.sha256' ]"
t "retencao: PRESERVOU o segundo valido mais recente (2020-03), mesmo velho" existe "$BK/iabimeta-20200301T000000Z.tar.gpg"
t "retencao: preservou o .sha256 do 2020-03" existe "$BK/iabimeta-20200301T000000Z.tar.gpg.sha256"
t "retencao: preservou o backup novo" test -n "$NOVO" -a -f "$NOVO"
t "retencao: apagou o .INVALIDO velho" not existe "$BK/iabimeta-20200401T000000Z.tar.gpg.INVALIDO"
t "retencao: nao apagou arquivo fora do padrao (outro.tar.gpg)" existe "$BK/outro.tar.gpg"
t "retencao: nao apagou iabimeta-notas.txt" existe "$BK/iabimeta-notas.txt"
t "retencao: nao apagou o .bak" existe "$BK/iabimeta-20200101T000000Z.tar.gpg.bak"
t "retencao: nao apagou o alertas.log" existe "$BK/alertas.log"
t "retencao: nao apagou nada em subpasta de BACKUP_DIR" existe "$BK/sub/iabimeta-20200101T000000Z.tar.gpg"
t "retencao: nao apagou nada FORA de BACKUP_DIR" existe "$A/fora/iabimeta-20200101T000000Z.tar.gpg"
t "retencao: nao tocou a copia externa antiga" existe "$EXT/iabimeta-20200101T000000Z.tar.gpg"
t "retencao: nao tocou o .sha256 da copia externa antiga" existe "$EXT/iabimeta-20200101T000000Z.tar.gpg.sha256"

novo_ambiente ret2
popular_producao
N1="$(date -u -d "1 day ago" +%Y%m%dT%H%M%SZ)"
N5="$(date -u -d "5 days ago" +%Y%m%dT%H%M%SZ)"
N13="$(date -u -d "13 days ago" +%Y%m%dT%H%M%SZ)"
N15="$(date -u -d "15 days ago" +%Y%m%dT%H%M%SZ)"
N20="$(date -u -d "20 days ago" +%Y%m%dT%H%M%SZ)"
for n in "$N1" "$N5" "$N13" "$N15" "$N20"; do mk_backup "$n"; done
t "retencao por idade (preparo): os 5 backups de mentira existem antes da execucao" bash -c "[ \"\$(ls '$BK'/iabimeta-*.tar.gpg | wc -l | tr -d ' ')\" = 5 ]"
fazer_backup --sem-externo
t "retencao por idade: sai 0" igual "$RC" 0
t "retencao por idade: apagou o de 20 dias" not existe "$BK/iabimeta-$N20.tar.gpg"
t "retencao por idade: apagou o de 15 dias" not existe "$BK/iabimeta-$N15.tar.gpg"
t "retencao por idade: manteve o de 13 dias (dentro do prazo de 14)" existe "$BK/iabimeta-$N13.tar.gpg"
t "retencao por idade: manteve o de 5 dias" existe "$BK/iabimeta-$N5.tar.gpg"
t "retencao por idade: manteve o de 1 dia" existe "$BK/iabimeta-$N1.tar.gpg"
t "retencao: o log conta quantos apagou" grep -q 'retencao local: 2 arquivo' "$OUT"

# =================================================================================================================================
echo "== (f) copia externa: pasta local confere o hash; destino inexistente falha com alerta; sem destino avisa; rsync"
# =================================================================================================================================
novo_ambiente ext1
popular_producao
escrever_backup_env "$FRASE" "$EXT"
fazer_backup
t "externa (pasta local): sai 0" igual "$RC" 0
ARQ="$(ultimo_arquivo)"
t "externa (pasta local): o arquivo foi copiado" existe "$EXT/$(basename "$ARQ")"
t "externa (pasta local): o .sha256 foi copiado" existe "$EXT/$(basename "$ARQ").sha256"
t "externa (pasta local): o SHA-256 da copia e o do original" igual "$(sha_do "$EXT/$(basename "$ARQ")")" "$(sha_do "$ARQ")"
t "externa (pasta local): a copia confere pelo seu .sha256" sha_confere "$EXT/$(basename "$ARQ")"
t "externa (pasta local): nao sobrou .partial no destino" bash -c "[ -z \"\$(find '$EXT' -name '*.partial')\" ]"
t "externa (pasta local): a copia externa e verificavel pelo verificar-backup" bash -c "cd '$RAIZ' && env DOCKER_BIN='$FALSO/docker' FALSO_ESTADO='$EST' TMPDIR='$A/tmp' bash '$DEP/scripts/verificar-backup.sh' '$EXT/$(basename "$ARQ")' --rapido"

novo_ambiente ext2
popular_producao
escrever_backup_env "$FRASE" "$A/pasta-que-nao-existe"
fazer_backup
t "externa (destino inexistente): sai 1" igual "$RC" 1
t "externa (destino inexistente): registra alerta em alertas.log" grep -q 'COPIA EXTERNA falhou' "$BK/alertas.log"
t "externa (destino inexistente): o alerta diz que o destino nao existe" grep -q 'nao existe' "$BK/alertas.log"
ARQ="$(ultimo_arquivo)"
t "externa (destino inexistente): o backup local FICA, valido" bash -c "[ -f '$ARQ' ] && [ -f '$ARQ.sha256' ]"
t "externa (destino inexistente): o ultimo-backup-ok foi gravado (o local e valido)" existe "$BK/ultimo-backup-ok"
t "externa (destino inexistente): nao criou o destino" not existe "$A/pasta-que-nao-existe"
t "externa (destino inexistente): sem sobras" sem_sobras "$BK" "$A/tmp"

novo_ambiente ext3
popular_producao
escrever_backup_env "$FRASE" ""
fazer_backup
t "externa (nao configurada): o backup local segue valido e sai 0" igual "$RC" 0
t "externa (nao configurada): alerta 'sem copia externa configurada'" grep -q 'SEM copia externa configurada' "$BK/alertas.log"
t "externa (nao configurada): o aviso aparece na saida" grep -q 'AVISO: sem copia externa' "$OUT"

novo_ambiente ext4
popular_producao
escrever_backup_env "$FRASE" "usuario@servidor.invalido:/pasta"
VARS=(FALSO_RSYNC_DEST="$A/rsync-destino")
fazer_backup
t "externa (rsync): sai 0" igual "$RC" 0
ARQ="$(ultimo_arquivo)"
t "externa (rsync): chamou rsync -a --partial" bash -c "grep -q -- '-a --partial' '$EST/rsync.log'"
t "externa (rsync): ssh nao interativo (BatchMode=yes)" grep -q 'BatchMode=yes' "$EST/rsync.log"
t "externa (rsync): o destino termina em /" grep -q 'usuario@servidor.invalido:/pasta/' "$EST/rsync.log"
t "externa (rsync): enviou o arquivo e o .sha256" bash -c "grep -q '$(basename "$ARQ")' '$EST/rsync.log' && grep -q '$(basename "$ARQ").sha256' '$EST/rsync.log'"
t "externa (rsync): os dois arquivos chegaram ao destino" bash -c "[ -f '$A/rsync-destino/$(basename "$ARQ")' ] && [ -f '$A/rsync-destino/$(basename "$ARQ").sha256' ]"
novo_ambiente ext5
popular_producao
escrever_backup_env "$FRASE" "usuario@servidor.invalido:/pasta"
VARS=(FALSO_RSYNC_DEST="$A/rsync-destino" FALSO_RSYNC_FALHA=1)
fazer_backup
t "externa (rsync com falha): sai 1" igual "$RC" 1
t "externa (rsync com falha): registra alerta" grep -q 'COPIA EXTERNA por rsync falhou' "$BK/alertas.log"
t "externa (rsync com falha): o backup local fica" bash -c "[ -f '$(ultimo_arquivo)' ]"

# =================================================================================================================================
echo "== (g) falha no meio: alerta, sai 1, sem .partial nem pasta temporaria"
# =================================================================================================================================
novo_ambiente falha1
popular_producao
VARS=(FALSO_FALHAR_PGDUMP=1)
fazer_backup --sem-externo
t "falha do pg_dump: sai 1" igual "$RC" 1
t "falha do pg_dump: registra alerta em alertas.log" grep -q "FALHOU no passo 'dump do banco" "$BK/alertas.log"
t "falha do pg_dump: o alerta nao tem segredo" bash -c "! grep -qE 'frase-de-teste|senha-pg|senha-app|chave-de-mentira' '$BK/alertas.log'"
t "falha do pg_dump: nao ficou .partial nem pasta temporaria" sem_sobras "$BK" "$A/tmp"
t "falha do pg_dump: nenhum .tar.gpg foi criado" bash -c "[ -z \"\$(ls '$BK' | grep 'tar.gpg' || true)\" ]"
t "falha do pg_dump: nao gravou ultimo-backup-ok" not existe "$BK/ultimo-backup-ok"
t "falha do pg_dump: nem chegou a chamar o tar dos anexos" bash -c "! grep -q -- '-cf -' '$EST/chamadas.log'"
t "falha do pg_dump: o lock foi solto" not existe "$BK/.lock"

novo_ambiente falha2
popular_producao
VARS=(FALSO_FALHAR_TAR=2)
fazer_backup --sem-externo
t "falha do tar dos anexos: sai 1" igual "$RC" 1
t "falha do tar dos anexos: registra alerta" grep -q "FALHOU no passo 'tar dos anexos" "$BK/alertas.log"
t "falha do tar dos anexos: sem .partial, pasta temporaria nem .tar.gpg" bash -c "[ -z \"\$(ls '$BK' | grep 'tar.gpg' || true)\" ]"
t "falha do tar dos anexos: sem sobras" sem_sobras "$BK" "$A/tmp"

novo_ambiente falha3
popular_producao
VARS=(FALSO_TAR_SAIDA_1=1)
fazer_backup --sem-externo
t "tar que avisa (codigo 1) com tar legivel: o backup segue e sai 0" igual "$RC" 0
t "tar que avisa (codigo 1): o aviso aparece" grep -q 'AVISO: o tar dos anexos avisou' "$OUT"

novo_ambiente falha4
popular_producao
VARS=(FALSO_FALHAR_LISTA=1)
fazer_backup --sem-externo
t "verificacao reprova o backup novo: sai 1" igual "$RC" 1
t "verificacao reprova: o arquivo vira .INVALIDO" bash -c "[ -n \"\$(ls '$BK'/iabimeta-*.tar.gpg.INVALIDO 2>/dev/null)\" ]"
t "verificacao reprova: nao sobra .tar.gpg com nome valido" igual "$(ultimo_arquivo)" ""
t "verificacao reprova: nao grava ultimo-backup-ok" not existe "$BK/ultimo-backup-ok"
t "verificacao reprova: registra alerta (REPROVOU)" grep -q 'REPROVOU' "$BK/alertas.log"
t "verificacao reprova: sem .partial nem pasta temporaria" sem_sobras "$BK" "$A/tmp"

novo_ambiente falha5
popular_producao
escrever_backup_env "" ""
fazer_backup --sem-externo
t "sem BACKUP_PASSPHRASE: sai 1" igual "$RC" 1
t "sem BACKUP_PASSPHRASE: alerta" grep -q 'BACKUP_PASSPHRASE nao esta definida' "$BK/alertas.log"
t "sem BACKUP_PASSPHRASE: nem chamou o docker" not existe "$EST/chamadas.log"

novo_ambiente falha6
popular_producao
chmod 644 "$DEP/backup.env"
fazer_backup --sem-externo
t "backup.env com permissao insegura (644): sai 1" igual "$RC" 1
t "backup.env inseguro: a mensagem pede chmod 600" grep -q 'permissao insegura' "$OUT"

novo_ambiente falha7
popular_producao
mkdir -p "$BK/.tmp.ANTIGA"
echo "texto claro de uma execucao morta" >"$BK/.tmp.ANTIGA/banco.dump"
echo "meio-arquivo" >"$BK/iabimeta-20200101T000000Z.tar.gpg.partial"
fazer_backup --sem-externo
t "sobras de execucao morta (.tmp.* e .partial) sao removidas no inicio" bash -c "[ ! -e '$BK/.tmp.ANTIGA' ] && [ ! -e '$BK/iabimeta-20200101T000000Z.tar.gpg.partial' ]"
t "sobras removidas: o backup novo sai 0" igual "$RC" 0

# =================================================================================================================================
echo "== (h) lock: a segunda execucao simultanea e recusada"
# =================================================================================================================================
novo_ambiente lock1
popular_producao
VARS=(FALSO_PGDUMP_SLEEP=6)
rodar_bg backup.sh --sem-externo
for _ in $(seq 1 60); do
  [ -d "$BK/.lock" ] && break
  sleep 0.5
done
VARS=()
rodar backup.sh --sem-externo
t "lock: a segunda execucao simultanea e recusada (sai 1)" igual "$RC" 1
t "lock: a mensagem e clara" grep -q 'outra execucao do backup esta em andamento' "$OUT"
wait "$BG_PID" || true
t "lock: a primeira execucao termina bem (sai 0)" igual "$(cat "$A/rc.bg")" 0
t "lock: o lock e solto no fim" not existe "$BK/.lock"
t "lock: a recusa nao apagou o lock da outra execucao (a primeira terminou e gerou o backup)" bash -c "[ -n \"\$(ls '$BK'/iabimeta-*.tar.gpg 2>/dev/null)\" ]"
novo_ambiente lock2
popular_producao
mkdir -p "$BK/.lock"
echo 99999999 >"$BK/.lock/pid"
fazer_backup --sem-externo
t "lock: um lock de processo morto e recuperado (sai 0)" igual "$RC" 0

# =================================================================================================================================
echo "== (i) ensaio de restauracao: so no projeto iabimeta-ensaio, nunca chama down no projeto iabimeta"
# =================================================================================================================================
novo_ambiente ens
popular_producao
fazer_backup --sem-externo
t "ensaio (preparo): o backup de producao saiu 0" igual "$RC" 0
ARQ_ENS="$(ultimo_arquivo)"
PROD_ANTES="$(arvore "$EST/volumes")"
L0="$(n_linhas "$EST/chamadas.log")"

VARS=(COMPOSE_PROJECT=iabimeta)
rodar ensaiar-restauracao.sh
t "ensaio: COMPOSE_PROJECT=iabimeta e RECUSADO (sai 1)" igual "$RC" 1
t "ensaio: a recusa explica o motivo" grep -q 'iabimeta-ensaio' "$OUT"
t "ensaio: a recusa nao fez nenhuma chamada ao docker" igual "$(n_linhas "$EST/chamadas.log")" "$L0"
VARS=(COMPOSE_PROJECT=outro)
rodar ensaiar-restauracao.sh
t "ensaio: COMPOSE_PROJECT=outro e RECUSADO (sai 1)" igual "$RC" 1
t "ensaio: a segunda recusa tambem nao chamou o docker" igual "$(n_linhas "$EST/chamadas.log")" "$L0"

# Resto de um ensaio anterior: e limpo antes de comecar.
mkdir -p "$EST/volumes/iabimeta-ensaio_pgdata" "$EST/volumes/iabimeta-ensaio_anexos"
echo "lixo" >"$EST/volumes/iabimeta-ensaio_pgdata/dados.bin"
echo "lixo" >"$EST/volumes/iabimeta-ensaio_anexos/lixo.bin"
VARS=()
rodar ensaiar-restauracao.sh
t "ensaio: sem argumento usa o ultimo backup valido e sai 0" igual "$RC" 0
t "ensaio: usou o backup apontado por ultimo-backup-ok" grep -q "$(basename "$ARQ_ENS")" "$OUT"
t "ensaio: imprime ENSAIO OK" grep -q 'ENSAIO OK' "$OUT"
t "ensaio: imprime a duracao" grep -q 'duracao total: [0-9]*s' "$OUT"
t "ensaio: imprime os objetos do manifesto (4) e as linhas conferidas pelo check (4)" bash -c "grep -q 'objetos de anexo no manifesto do backup: 4' '$OUT' && grep -q 'linhas disponiveis conferidas pelo attachments check: 4' '$OUT'"
LOG_ENS="$A/log-ensaio.txt"
tail -n +"$((L0 + 1))" "$EST/chamadas.log" >"$LOG_ENS"
t "ensaio: NENHUMA chamada ao projeto iabimeta (producao)" bash -c "! grep -q '^iabimeta|' '$LOG_ENS'"
t "ensaio: nunca chamou down no projeto iabimeta" bash -c "! grep -q '^iabimeta|down' '$EST/chamadas.log'"
t "ensaio: todo down foi 'down -v' no projeto iabimeta-ensaio" bash -c "grep '|down' '$LOG_ENS' | grep -vq '^iabimeta-ensaio|down -v '; [ \$? -ne 0 ]"
t "ensaio: limpou o resto do ensaio anterior e derrubou o novo (2 down -v)" igual "$(grep -c '^iabimeta-ensaio|down -v' "$LOG_ENS" || true)" 2
t "ensaio: o ultimo comando compose do ensaio foi o down -v" igual "$(grep '^iabimeta-ensaio|' "$LOG_ENS" | tail -n1 | cut -d' ' -f1)" "iabimeta-ensaio|down"
t "ensaio: usou a sub-rede propria (172.29.90.0/24 e 172.29.90.2)" bash -c "grep '^iabimeta-ensaio|up' '$LOG_ENS' | grep -q 'EDGE_SUBNET=172.29.90.0/24' && grep '^iabimeta-ensaio|run' '$LOG_ENS' | grep -q 'EDGE_WEB_IP=172.29.90.2'"
t "ensaio: os volumes do ensaio sumiram" bash -c "[ -z \"\$(ls '$EST/volumes' | grep '^iabimeta-ensaio_' || true)\" ]"
t "ensaio: os volumes de producao ficaram intactos (SHA-256 da arvore)" igual "$(arvore "$EST/volumes")" "$PROD_ANTES"
t "ensaio: nao sobrou pasta temporaria" sem_sobras "$BK" "$A/tmp"
t "ensaio: o lock do ensaio foi solto" not existe "$BK/.lock-ensaio"

L0="$(n_linhas "$EST/chamadas.log")"
VARS=(FALSO_CHECK_SAIDA=1)
rodar ensaiar-restauracao.sh "$ARQ_ENS"
t "ensaio: check com divergencia (1) e aceitavel: sai 0 com aviso" igual "$RC" 0
t "ensaio: o aviso de divergencia cita a retencao" grep -q 'retencao' "$OUT"
VARS=(FALSO_CHECK_SAIDA=2)
rodar ensaiar-restauracao.sh "$ARQ_ENS"
t "ensaio: check que falha (2) faz o ensaio sair 1" igual "$RC" 1
t "ensaio: mesmo em falha o projeto de ensaio foi derrubado (volumes sumiram)" bash -c "[ -z \"\$(ls '$EST/volumes' | grep '^iabimeta-ensaio_' || true)\" ]"
t "ensaio: a falha registra alerta" grep -q 'ensaio de restauracao FALHOU' "$BK/alertas.log"
VARS=()
rodar ensaiar-restauracao.sh "$V/alterado-sha-novo.tar.gpg"
t "ensaio: backup adulterado reprova o ensaio (sai 1)" igual "$RC" 1
t "ensaio: com backup adulterado tambem derruba so o ensaio" bash -c "! tail -n +$((L0 + 1)) '$EST/chamadas.log' | grep -q '^iabimeta|'"
t "ensaio: a producao continua intacta ate o fim" igual "$(arvore "$EST/volumes")" "$PROD_ANTES"
t "ensaio: nunca houve down no projeto iabimeta em nenhum cenario" bash -c "! grep -q '^iabimeta|down' '$EST/chamadas.log'"

novo_ambiente ens2
popular_producao
rodar ensaiar-restauracao.sh
t "ensaio: sem nenhum backup local sai 1 com mensagem clara" igual "$RC" 1
t "ensaio: a mensagem diz que nao ha backup" grep -q 'nao ha backup local' "$OUT"
t "ensaio: sem backup nao chamou o docker" bash -c "! grep -q 'down' '$EST/chamadas.log' 2>/dev/null"

t "geral: a frase nao aparece em nenhuma saida de nenhum cenario" bash -c "! grep -rqF -e '$FRASE' '$RAIZ'/*/saida.*.txt '$RAIZ'/*/estado/*.log '$RAIZ'/*/backups/alertas.log 2>/dev/null"

# =================================================================================================================================
DURACAO=$((SECONDS - INICIO_TESTE))
echo
echo "gpg: $(gpg --version | head -n1) | bash: ${BASH_VERSION} | permissoes POSIX reais: $PERM_REAL | duracao: ${DURACAO}s"
echo "Resultado: $PASSOU passaram, $FALHOU falharam."
[ "$FALHOU" -eq 0 ]
