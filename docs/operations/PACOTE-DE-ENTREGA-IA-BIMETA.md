# Procedimento de geração e entrega do pacote versionado (para a equipe de desenvolvimento)

**Uso interno da equipe de desenvolvimento** (não faz parte do pacote entregue). Objetivo: entregar ao especialista de infraestrutura um pacote **reprodutível**, com **commit de origem**, **manifesto** e **SHA-256**, sem segredos e sem material interno desnecessário. **Só gerar o pacote definitivo depois da autorização do proprietário (decisão P5).** Nenhum comando abaixo faz push, merge ou publicação.

## 1. Pré-condições
1. Estar na branch e no commit aprovados, com a árvore **limpa** (`git status --short` vazio).
2. Anotar o **hash completo** (`git rev-parse HEAD`) e o curto (`git rev-parse --short HEAD`).
3. Conferir que não há segredo versionado: `git ls-files | grep -iE "(^|/)(\.env|app\.env|backup\.env)$|\.(pem|pfx|key)$"` não deve listar nada (só os modelos `*.example` podem existir).

## 2. O que entra no pacote (lista fechada, só o necessário à instalação)
`backend/`, `frontend/`, `deploy/`, `nuget.config`, `Directory.Build.props`, `global.json`, `dotnet-tools.json` (tudo que os Dockerfiles copiam) e **somente** estes documentos de `docs/operations/`: `INVENTARIO-TECNICO-IA-BIMETA.md`, `MANUAL-INSTALACAO-VM-IA-BIMETA.md`, `CHECKLIST-HOMOLOGACAO-IA-BIMETA.md`, `PENDENCIAS-E-DECISOES-INFRA.md`, `ORIENTACAO-H1-IA-BIMETA.md`, `IMPLANTACAO-LINUX.md`, `BACKUP-E-RESTAURACAO.md`, `OPERACAO-E-SUPERVISAO.md`. **Ficam de fora:** testes, ferramentas PowerShell, ADRs, instruções do agente de desenvolvimento, planos e auditorias internas, documentos comerciais e de piloto. (Os documentos citam ADRs e outros arquivos que **não** estão neste pacote inicial; o conjunto de documentos A a E é autocontido.)

## 3. Geração (executar na raiz do repositório, em Git Bash ou Linux)
```bash
set -euo pipefail
COMMIT="$(git rev-parse HEAD)"; CURTO="$(git rev-parse --short HEAD)"; RAMO="$(git rev-parse --abbrev-ref HEAD)"
SAIDA="<PASTA_DE_SAIDA_FORA_DO_REPOSITORIO>"; mkdir -p "$SAIDA"
NOME="iabimeta-${CURTO}"
test -z "$(git status --short)" || { echo "arvore suja: faca commit antes"; exit 1; }

# 1) o pacote: git archive so de arquivos versionados (sem bin/obj, .env ou arquivos locais), com o prefixo iabimeta/
git archive --format=tar.gz --prefix=iabimeta/ -o "$SAIDA/${NOME}.tar.gz" "$COMMIT" -- \
  backend frontend deploy nuget.config Directory.Build.props global.json dotnet-tools.json \
  docs/operations/INVENTARIO-TECNICO-IA-BIMETA.md docs/operations/MANUAL-INSTALACAO-VM-IA-BIMETA.md \
  docs/operations/CHECKLIST-HOMOLOGACAO-IA-BIMETA.md docs/operations/PENDENCIAS-E-DECISOES-INFRA.md \
  docs/operations/ORIENTACAO-H1-IA-BIMETA.md docs/operations/IMPLANTACAO-LINUX.md \
  docs/operations/BACKUP-E-RESTAURACAO.md docs/operations/OPERACAO-E-SUPERVISAO.md

# 2) SHA-256 do pacote (formato aceito por "sha256sum -c")
( cd "$SAIDA" && sha256sum "${NOME}.tar.gz" > "${NOME}.tar.gz.sha256" )

# 3) manifesto (texto) com commit de origem, quantidade de arquivos e hash de cada um
{
  echo "ia.bimeta - manifesto do pacote"
  echo "pacote:        ${NOME}.tar.gz"
  echo "commit:        ${COMMIT}"
  echo "branch:        ${RAMO}"
  echo "gerado em:     $(date -u +%Y-%m-%dT%H:%M:%SZ) (UTC)"
  echo "sha256 pacote: $(cut -d' ' -f1 "$SAIDA/${NOME}.tar.gz.sha256")"
  echo "arquivos:      $(tar -tzf "$SAIDA/${NOME}.tar.gz" | grep -vc '/$')"
  echo "--- sha256 por arquivo (conteudo do pacote) ---"
  T="$(mktemp -d)"; tar -xzf "$SAIDA/${NOME}.tar.gz" -C "$T"
  ( cd "$T/iabimeta" && find . -type f | LC_ALL=C sort | xargs sha256sum )
  rm -rf "$T"
} > "$SAIDA/${NOME}.manifesto.txt"
```
Resultado: três arquivos em `<PASTA_DE_SAIDA>`: `iabimeta-<hash curto>.tar.gz`, `iabimeta-<hash curto>.tar.gz.sha256` e `iabimeta-<hash curto>.manifesto.txt`.

## 4. Verificações antes de entregar
1. `cd <PASTA_DE_SAIDA> && sha256sum -c iabimeta-<hash curto>.tar.gz.sha256` deve imprimir `OK`.
2. O manifesto traz o **commit completo** e o mesmo SHA-256 do `.sha256`.
3. Conferir que **os arquivos que os Dockerfiles copiam** estão no pacote: `tar -tzf <pacote> | grep -E "iabimeta/(nuget.config|Directory.Build.props|backend/host/IaBimeta.Api/IaBimeta.Api.csproj|frontend/package-lock.json|deploy/web/Caddyfile|deploy/docker-compose.yml)$"` (seis linhas).
4. Varrer o pacote em busca de segredos: `tar -xzOf <pacote> | grep -aE "BEGIN (RSA|PRIVATE)|sk-[A-Za-z0-9]{20,}"` não deve encontrar nada.
5. Não deve haver `bin/`, `obj/`, `node_modules/`, `.env`, `app.env` nem `backup.env`: `tar -tzf <pacote> | grep -E "/(bin|obj|node_modules)/|/(\.env|app\.env|backup\.env)$"` deve sair vazio.
6. Os scripts de `deploy/scripts/` mantêm o bit de execução: `tar -tvzf <pacote> | grep "deploy/scripts/subir.sh"` mostra `-rwxr-xr-x`.

## 5. Entrega segura ao especialista
1. **Canal:** transferência por canal **cifrado e autenticado** (por exemplo `scp`/SFTP para a VM ou o servidor dele, ou um compartilhamento com acesso restrito e expiração). **Não** por e-mail comum, chat público nem repositório aberto.
2. **Hash por outro caminho:** envie o **SHA-256** e o **hash do commit** por **canal diferente** do usado para o arquivo (por exemplo, o pacote por SFTP e o hash por mensagem direta), para que adulteração no trânsito seja detectável. O especialista confere com `sha256sum -c` (Manual B §10) e compara o commit do manifesto.
3. **Conteúdo:** os três arquivos (pacote, `.sha256`, manifesto). **Nunca** inclua `.env`, `app.env`, `backup.env`, chaves, tokens ou senhas: esses são **gerados na VM** (`gerar-segredos.sh`) e guardados no cofre dela. As credenciais da Meta e do provedor de IA **não** viajam neste pacote nem em arquivo: entram pela aplicação, mais tarde (H4 e H5), por canal seguro.
4. **Registro:** anote data, destinatário, hash do commit e SHA-256 entregues. A cada atualização, gere novo pacote (novo hash no nome); nunca reutilize o nome.
5. **Retenção:** apague a cópia temporária do pacote do computador de desenvolvimento depois da confirmação de recebimento e conferência.

## 6. Atualizações
Cada novo pacote repete os passos 1 a 5. O especialista aplica com `atualizar.sh` (Manual B §20), que faz backup antes. Mantenha o registro dos hashes entregues para saber exatamente qual versão roda na VM (`deploy/.versao-anterior` guarda a anterior).
