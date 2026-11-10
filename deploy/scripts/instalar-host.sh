#!/usr/bin/env bash
# Prepara uma VM Ubuntu Server 24.04 LTS para o ia.bimeta (rode como ROOT, UMA vez): pacotes de apoio, Docker Engine e o plugin Compose pelo
# repositorio OFICIAL da Docker, firewall (ufw), relogio e pastas. NAO sobe nada. Procedimento conforme a documentacao oficial da Docker para Ubuntu.
# Uso: sudo ./instalar-host.sh --ssh-de <rede de administracao em CIDR, por exemplo 192.168.1.0/24> [--forcar]
#
# ATENCAO (SSH): o firewall e ativado com SSH liberado SO para a rede informada. Se ela nao incluir o seu endereco, voce se tranca para fora.
# Mantenha esta sessao aberta e teste um SEGUNDO acesso SSH antes de fechar. O Docker publica portas por fora do ufw (por isso so o proxy publica
# portas, e so a 80 e a 443); isso e conhecido e documentado pela Docker.
set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ssh_de=""
forcar=0
while [ $# -gt 0 ]; do
  case "$1" in
    --ssh-de) ssh_de="${2:-}"; shift 2 ;;
    --forcar) forcar=1; shift ;;
    *) fail "argumento desconhecido: $1" ;;
  esac
done
[ "$(id -u)" -eq 0 ] || fail "rode como root (sudo ./instalar-host.sh ...)"
[ -n "$ssh_de" ] || fail "informe a rede de administracao: --ssh-de 192.168.1.0/24 (veja a ATENCAO no topo do script)"
case "$ssh_de" in */*) ;; *) fail "use CIDR em --ssh-de (por exemplo 192.168.1.0/24)" ;; esac

# shellcheck disable=SC1091
. /etc/os-release
if [ "${ID:-}" != "ubuntu" ] || [ "${VERSION_ID:-}" != "24.04" ]; then
  [ "$forcar" -eq 1 ] || fail "este script e para Ubuntu Server 24.04 (encontrado: ${PRETTY_NAME:-desconhecido}); use --forcar por sua conta e risco"
fi

# Pacotes nao oficiais que conflitam com o Docker Engine: a documentacao manda remove-los; aqui so se avisa (nada e removido sem voce saber).
conflitantes="$(dpkg --get-selections docker.io docker-compose docker-compose-v2 docker-doc podman-docker containerd runc 2>/dev/null | cut -f1 | tr '\n' ' ' || true)"
[ -z "${conflitantes// /}" ] || fail "pacotes conflitantes instalados: ${conflitantes}. Remova-os (apt remove ${conflitantes}) e rode de novo"

export DEBIAN_FRONTEND=noninteractive
log "instalando pacotes de apoio"
apt-get update
apt-get install -y ca-certificates curl gnupg rsync ufw

if ! command -v docker >/dev/null 2>&1; then
  log "configurando o repositorio oficial da Docker"
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  tee /etc/apt/sources.list.d/docker.sources >/dev/null <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: $(. /etc/os-release && echo "${UBUNTU_CODENAME:-$VERSION_CODENAME}")
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
  apt-get update
  log "instalando o Docker Engine e o plugin Compose"
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
else
  log "docker ja instalado: $(docker --version)"
fi
systemctl enable --now docker
docker compose version >&2

log "ajustando o relogio (NTP)"
timedatectl set-ntp true || log "AVISO: nao foi possivel ligar o NTP"

log "configurando o firewall (ufw): entrada negada, SSH so de $ssh_de, HTTP e HTTPS publicos"
ufw default deny incoming
ufw default allow outgoing
ufw allow from "$ssh_de" to any port 22 proto tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
ufw status verbose >&2

log "criando as pastas e protegendo os segredos"
install -d -m 0700 /var/backups/iabimeta
for arquivo in .env app.env backup.env; do
  [ ! -f "$DEPLOY_DIR/$arquivo" ] || chmod 600 "$DEPLOY_DIR/$arquivo"
done

cat >&2 <<'FIM'

HOST PRONTO. Proximos passos (nesta ordem):
  1. Em OUTRA sessao SSH, confirme que ainda entra (o firewall esta ativo).
  2. scripts/gerar-segredos.sh --dominio <dominio> --email <email>   e guarde os arquivos gerados FORA da maquina.
  3. Configure BACKUP_EXTERNAL_DEST em deploy/backup.env.
  4. scripts/preflight.sh  ->  scripts/subir.sh
  5. Depois que a pilha subir e o backup manual funcionar: sudo scripts/agendar.sh
FIM
