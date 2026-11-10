#!/usr/bin/env bash
# Verifica um backup cifrado do ia.bimeta (formato 1, criado por backup.sh). ADR 0032 secao 6 e ADR 0033.
#
# Uso: verificar-backup.sh <arquivo.tar.gpg> [--rapido] [--extrair-para <pasta>]
#   Saida: 0 = integro; 1 = reprovado (ou nao foi possivel verificar); 2 = uso incorreto.
#   Modo padrao (COMPLETO): confere o .sha256 ao lado, decifra em fluxo, confere que o conteudo e EXATAMENTE banco.dump,
#     anexos.tar e manifesto.txt (todos arquivos comuns), recalcula SHA-256 e tamanho de banco.dump e anexos.tar contra o
#     manifesto, confere a listagem do tar dos anexos e, se o servico db estiver no ar, que o pg_restore -l le o dump.
#   --rapido: so o SHA-256 do arquivo cifrado contra o .sha256 (nao decifra; nao prova que a frase serve).
#   --extrair-para <pasta>: (uso do restaurar.sh) deixa banco.dump, anexos.tar e manifesto.txt, ja verificados, numa pasta
#     VAZIA que o chamador criou (e que o chamador apaga). Sem esta opcao, tudo e apagado no fim.
# A frase vem do backup.env (BACKUP_PASSPHRASE) por descritor de arquivo: nunca aparece em argumento, log ou mensagem.
# O texto claro do dump passa por uma pasta temporaria 0700 em ${TMPDIR:-/tmp}; use TMPDIR para escolher um disco com espaco.
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
umask 077

uso() {
  printf '%s\n' \
    'Uso: verificar-backup.sh <arquivo.tar.gpg> [--rapido] [--extrair-para <pasta-vazia>]' \
    '  0 = integro; 1 = reprovado; 2 = uso incorreto.' >&2
}

ARQUIVO=""
RAPIDO=0
EXTRAIR=""
while [ $# -gt 0 ]; do
  case "$1" in
    --rapido) RAPIDO=1 ;;
    --extrair-para)
      [ $# -ge 2 ] || { uso; exit 2; }
      EXTRAIR="$2"
      shift
      ;;
    -h|--help) uso; exit 0 ;;
    -*) log "opcao desconhecida: $1"; uso; exit 2 ;;
    *)
      [ -z "$ARQUIVO" ] || { uso; exit 2; }
      ARQUIVO="$1"
      ;;
  esac
  shift
done
[ -n "$ARQUIVO" ] || { uso; exit 2; }
if [ "$RAPIDO" = 1 ] && [ -n "$EXTRAIR" ]; then
  log "--rapido nao decifra nada: nao combina com --extrair-para"
  exit 2
fi
case "$ARQUIVO" in /*|./*|../*) ;; *) ARQUIVO="./$ARQUIVO" ;; esac

CTRL=""
reprova() { log "REPROVADO: $*"; exit 1; }
limpar() {
  local rc=$?
  trap - EXIT
  [ -z "$CTRL" ] || rm -rf "$CTRL"
  exit "$rc"
}
trap limpar EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

need sha256sum

# 1. O arquivo e o .sha256 ao lado.
[ -f "$ARQUIVO" ] || reprova "arquivo nao encontrado: $ARQUIVO"
SHA_ARQ="$ARQUIVO.sha256"
[ -f "$SHA_ARQ" ] || reprova "falta o arquivo $(basename "$SHA_ARQ") ao lado do backup"
esperado="$(awk 'NR==1 {print $1}' "$SHA_ARQ" | tr 'A-F' 'a-f')"
[[ "$esperado" =~ ^[0-9a-f]{64}$ ]] || reprova "o conteudo de $(basename "$SHA_ARQ") nao e um SHA-256 valido"
real="$(sha256_of "$ARQUIVO")"
[ "$real" = "$esperado" ] || reprova "o SHA-256 do arquivo cifrado difere do registrado em $(basename "$SHA_ARQ") (arquivo alterado ou truncado)"
if [ "$RAPIDO" = 1 ]; then
  log "OK (rapido): o SHA-256 do arquivo cifrado confere ($(basename "$ARQUIVO")). A frase e o conteudo NAO foram conferidos."
  exit 0
fi

# 2. Ferramentas e frase.
need gpg
need tar
load_backup_env
[ -n "${BACKUP_PASSPHRASE:-}" ] || reprova "BACKUP_PASSPHRASE nao esta definida no backup.env: nao e possivel decifrar"
FRASE="$BACKUP_PASSPHRASE"
unset BACKUP_PASSPHRASE

# 3. Pastas de trabalho: CTRL (controle, sempre apagada) e W (onde o conteudo e extraido).
CTRL="$(mktemp -d "${TMPDIR:-/tmp}/iabimeta-verif.XXXXXX")" || fail "nao consegui criar a pasta temporaria em ${TMPDIR:-/tmp}"
chmod 700 "$CTRL"
if [ -n "$EXTRAIR" ]; then
  [ -d "$EXTRAIR" ] || fail "a pasta de --extrair-para nao existe: $EXTRAIR"
  [ -z "$(ls -A "$EXTRAIR")" ] || fail "a pasta de --extrair-para precisa estar vazia: $EXTRAIR"
  W="$EXTRAIR"
else
  W="$CTRL/conteudo"
  mkdir "$W"
fi
chmod 700 "$W"

# A frase vai pelo descritor 3 (nunca em argumento). A saida decifrada vai para o stdout.
decifrar() {
  gpg --batch --quiet --no-tty --pinentry-mode loopback --passphrase-fd 3 --decrypt -- "$ARQUIVO" 3<<<"$FRASE"
}

# 4. Passo 1: decifra em fluxo e LISTA o conteudo (nada e gravado fora a lista). O "cat" no fim esvazia o fluxo para o gpg nunca levar SIGPIPE.
if ! decifrar | { tar -tvf - >"$CTRL/lista"; rc=$?; cat >/dev/null; exit "$rc"; }; then
  reprova "nao foi possivel decifrar e ler o arquivo (frase errada, arquivo adulterado ou truncado)"
fi
nomes="$(awk '{print $NF}' "$CTRL/lista" | LC_ALL=C sort | tr '\n' ' ')"
tipos="$(cut -c1 "$CTRL/lista" | LC_ALL=C sort -u | tr -d '\n')"
[ "$nomes" = "anexos.tar banco.dump manifesto.txt " ] || reprova "o conteudo do backup nao e exatamente banco.dump, anexos.tar e manifesto.txt"
[ "$tipos" = "-" ] || reprova "o backup tem entradas que nao sao arquivos comuns"

# 5. Passo 2: decifra de novo e extrai SO os tres nomes ja conferidos.
if ! decifrar | { tar -xf - -C "$W" --no-same-owner --no-same-permissions manifesto.txt banco.dump anexos.tar; rc=$?; cat >/dev/null; exit "$rc"; }; then
  reprova "falha ao extrair o conteudo do backup"
fi
for f in manifesto.txt banco.dump anexos.tar; do
  if [ ! -f "$W/$f" ] || [ -L "$W/$f" ]; then reprova "$f nao foi extraido como arquivo comum"; fi
done

# 6. O manifesto.
mget() { grep -m1 "^$1=" "$W/manifesto.txt" | cut -d= -f2- || true; }
versao="$(mget versao_formato)"
[ "$versao" = "1" ] || reprova "versao_formato do manifesto nao suportada: '$versao'"
m_banco_sha="$(mget banco_dump_sha256)"
m_banco_bytes="$(mget banco_dump_bytes)"
m_anexos_sha="$(mget anexos_tar_sha256)"
m_anexos_bytes="$(mget anexos_tar_bytes)"
m_objetos="$(mget anexos_objetos)"
[[ "$m_banco_sha" =~ ^[0-9a-f]{64}$ ]] || reprova "banco_dump_sha256 ausente ou invalido no manifesto"
[[ "$m_anexos_sha" =~ ^[0-9a-f]{64}$ ]] || reprova "anexos_tar_sha256 ausente ou invalido no manifesto"
[[ "$m_banco_bytes" =~ ^[0-9]+$ ]] || reprova "banco_dump_bytes ausente ou invalido no manifesto"
[[ "$m_anexos_bytes" =~ ^[0-9]+$ ]] || reprova "anexos_tar_bytes ausente ou invalido no manifesto"
[[ "$m_objetos" =~ ^[0-9]+$ ]] || reprova "anexos_objetos ausente ou invalido no manifesto"

# 7. SHA-256 e tamanho de cada parte contra o manifesto.
r="$(sha256_of "$W/banco.dump")"
[ "$r" = "$m_banco_sha" ] || reprova "o SHA-256 de banco.dump difere do manifesto"
t="$(wc -c <"$W/banco.dump" | tr -d ' ')"
[ "$t" = "$m_banco_bytes" ] || reprova "o tamanho de banco.dump ($t) difere do manifesto ($m_banco_bytes)"
[ "$(head -c 5 "$W/banco.dump")" = "PGDMP" ] || reprova "banco.dump nao tem o cabecalho de um dump do pg_dump -Fc (PGDMP)"
r="$(sha256_of "$W/anexos.tar")"
[ "$r" = "$m_anexos_sha" ] || reprova "o SHA-256 de anexos.tar difere do manifesto"
t="$(wc -c <"$W/anexos.tar" | tr -d ' ')"
[ "$t" = "$m_anexos_bytes" ] || reprova "o tamanho de anexos.tar ($t) difere do manifesto ($m_anexos_bytes)"

# 8. A listagem do tar dos anexos: legivel, so arquivos comuns e pastas, nada fora da raiz, e o numero de objetos (.bin) do manifesto.
tar -tf "$W/anexos.tar" >"$CTRL/anexos.lista" 2>/dev/null || reprova "anexos.tar nao e um tar legivel"
tar -tvf "$W/anexos.tar" 2>/dev/null | cut -c1 | LC_ALL=C sort -u | tr -d '\n' >"$CTRL/anexos.tipos" || reprova "anexos.tar nao e um tar legivel"
case "$(cat "$CTRL/anexos.tipos")" in
  *[!d-]*) reprova "anexos.tar tem entradas que nao sao arquivos comuns nem pastas (link ou dispositivo)" ;;
esac
if grep -qE '(^/|(^|/)\.\.(/|$))' "$CTRL/anexos.lista"; then
  reprova "anexos.tar tem caminho absoluto ou com '..' (fora da pasta dos anexos)"
fi
n="$(grep -c '\.bin$' "$CTRL/anexos.lista" || true)"
[ "$n" = "$m_objetos" ] || reprova "anexos.tar tem $n objeto(s) .bin e o manifesto diz $m_objetos"

# 9. O pg_restore -l le o dump (so se o servico db estiver no ar; sem ele o item e pulado, nunca reprova).
db_id="$(compose ps -q db 2>/dev/null || true)"
if [ -n "$db_id" ]; then
  if ! compose exec -T db pg_restore -l <"$W/banco.dump" >/dev/null 2>"$CTRL/pgr.err"; then
    log "$(tail -n 3 "$CTRL/pgr.err" 2>/dev/null || true)"
    reprova "o pg_restore -l nao conseguiu ler banco.dump"
  fi
  pgr="pg_restore -l leu o dump"
else
  log "AVISO: o servico db nao esta no ar; o item 'pg_restore -l' foi PULADO (isto nao reprova o backup)."
  pgr="pg_restore -l PULADO (db fora do ar)"
fi

log "OK: backup integro ($(basename "$ARQUIVO")): frase confere, conteudo exato, SHA-256 e tamanhos conferem, $n objeto(s) de anexo, $pgr."
exit 0
