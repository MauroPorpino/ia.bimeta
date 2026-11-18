# Implantação do ia.bimeta em uma VM Linux (Ubuntu Server 24.04 + Docker Compose)

Guia passo a passo para instalar **uma pilha única** do ia.bimeta (proxy HTTPS com a interface, Api, Worker, PostgreSQL 17 e ClamAV) numa máquina virtual Linux na infraestrutura própria, com um IP público fixo. Decisão e justificativas: `docs/adr/0033-*.md`. Todos os arquivos estão em `deploy/`.

> **Papel desta VM:** a VM em servidor físico da empresa é o ambiente de **homologação e testes** (ADR 0033, aceito com a ressalva de homologar em Linux real); a **produção futura** será em nuvem com **os mesmos artefatos**. Nada é instalado no Windows de desenvolvimento.

> **Estado de validação (leia antes de usar):** os arquivos de implantação foram escritos e conferidos **por texto e por compilação** (o `dotnet publish` que os Dockerfiles executam, o build da interface, a sintaxe de todos os scripts, as invariantes de segurança do Compose, do Caddyfile e dos modelos, e os scripts de backup e restauração contra um `docker` falso). **Nada foi executado com Docker de verdade**: a máquina de desenvolvimento não tem Docker, WSL nem virtualização habilitada. **O primeiro uso REAL é o teste de aceitação na VM (seção 6)**, que prova o comportamento (subida, exposição, ClamAV real, persistência, queda de serviço, backup e restauração). Não coloque atendimento real antes de ele passar.

## 1. O que vai rodar e o que fica exposto

```text
Internet ──80/443──► roteador/firewall (NAT) ──► VM Ubuntu 24.04
                                                   │
                         ┌──────────── rede "edge" (172.29.89.0/24) ─────────────┐
                         │   web (Caddy: HTTPS + interface React) ──► api (:8080, interno)
                         └───────────────────────────────────────────────────────┘
                         ┌──────────── rede "back" (interna) ────────────────────┐
                         │   api, worker ──► db (PostgreSQL 17)                  │
                         │   api, worker ──► clamav (clamd :3310)                │
                         └───────────────────────────────────────────────────────┘
        Volumes: pgdata (banco) · anexos (arquivos de mídia, cifrados) · clamdb (assinaturas) · caddy_data (certificados)
```

- **Só o `web` publica portas: 80 e 443.** PostgreSQL, ClamAV, Api e Worker não têm porta publicada. O SSH é protegido pelo `ufw` (só da rede de administração).
- O Caddy obtém e **renova sozinho** o certificado do Let's Encrypt (precisa de DNS correto e das portas 80 e 443 chegando até ele).
- Caminhos públicos do proxy: `/` (interface), `/api/*` (a API da interface, sem o prefixo) e `/webhooks/whatsapp/*` (o webhook da Meta, passado inteiro e sem alteração para o Api, que valida a assinatura). Qualquer outro caminho cai na interface; as rotas de desenvolvimento (`/simulator`, `X-Dev-Tenant`) **não existem em produção**.
- O Api confia nos cabeçalhos `X-Forwarded-*` **só da rede `edge`** (a do proxy): o limite de login passa a valer por cliente. O Caddy ignora `X-Forwarded-*` que o próprio cliente mande.

### Requisitos da VM (a confirmar com o seu virtualizador)

| Recurso | Mínimo | Observação |
|---|---|---|
| Sistema | **Ubuntu Server 24.04 LTS** (64 bits) | os scripts conferem a versão |
| Memória | **8 GB** (o ClamAV sozinho pede 3 a 4 GiB; o `clamd` usa ~1,2 GiB e até ~2,4 GiB na recarga diária das assinaturas) | com menos de 6 GB o preflight reprova |
| CPU | 2 vCPU (recomendado 4) | a construção das imagens é a parte mais pesada |
| Disco | 60 GB (recomendado 100 GB) | imagens, banco, anexos, assinaturas e backups locais |
| Rede de saída | Docker Hub e MCR (imagens), NuGet e npm (construção), `database.clamav.net` (assinaturas), Let's Encrypt, Graph API da Meta e API da OpenAI | HTTPS (443) de saída liberado |
| Rede de entrada | **80 e 443** (TCP) do mundo; 22 só da rede de administração | ver a seção 5 |

## 2. Antes de começar: itens que dependem de você

1. **Domínio** do atendimento (por exemplo `atendimento.suaempresa.com.br`) e o **registro DNS A** apontando para o **IP público fixo**. Use TTL curto no início (300 s).
2. **Redirecionamento (NAT) no roteador/firewall:** TCP **80 e 443** do IP público para o IP da VM (reserve um IP fixo na LAN para a VM). A porta 80 também é necessária: é por ela que o Let's Encrypt valida o domínio.
3. Confirme com o provedor de internet que **80 e 443 de entrada não são bloqueadas** (alguns bloqueiam por padrão).
4. Um destino de **cópia externa dos backups** (outro servidor por SSH/rsync ou um disco/compartilhamento montado que **não** seja a própria VM) e, opcionalmente, a URL de um webhook de chat para os alertas.
5. Uma **conta de IA com crédito**, a **conta da Meta** e o **contrato com a empresa piloto** (`PILOTO-PRIMEIRA-EMPRESA.md`). Nada disto é necessário para instalar e passar no teste de aceitação.

## 3. Preparar a VM e levar o código

1. Instale o Ubuntu Server 24.04, com SSH por **chave** (sem senha) e as atualizações aplicadas. Crie um usuário administrador (`sudo`).
2. Leve o código para a VM, de um destes jeitos (a VM não precisa de acesso ao seu repositório):
   - **Git** (se a VM alcança o seu servidor Git): `git clone <url> /opt/iabimeta && cd /opt/iabimeta && git checkout feature/mvp-c-ia-minima`.
   - **Pacote**: na máquina de desenvolvimento, `git archive --format=tar.gz --prefix=iabimeta/ HEAD -o iabimeta.tar.gz`, `scp iabimeta.tar.gz usuario@vm:/tmp/`, e na VM `sudo mkdir -p /opt && sudo tar -xzf /tmp/iabimeta.tar.gz -C /opt && cd /opt/iabimeta`. (Para atualizar depois, repita com a versão nova.)
3. Os scripts rodam como **root** (`sudo`): eles usam o Docker e guardam backups em pasta protegida. Entre numa sessão de root (`sudo -i`) ou prefixe cada comando com `sudo`.

## 4. Instalar, configurar e subir (modo TESTE, certificado de homologação)

```bash
cd /opt/iabimeta/deploy

# 4.1 Prepara o host (Docker Engine e plugin Compose pelo repositorio OFICIAL da Docker, ufw, relogio, pastas). Nao sobe nada.
#     --ssh-de e a rede de onde voce administra por SSH. MANTENHA a sessao aberta e teste um SEGUNDO acesso antes de fechar.
sudo ./scripts/instalar-host.sh --ssh-de 192.168.1.0/24

# 4.2 Gera os segredos (senhas do banco, chaves de cifra, frase do backup). NAO imprime valor nenhum.
sudo ./scripts/gerar-segredos.sh --dominio atendimento.suaempresa.com.br --email ops@suaempresa.com.br
```

**Guarde agora uma cópia fora da máquina** (cofre ou gerenciador de senhas) de `deploy/app.env`, `deploy/backup.env` e `deploy/.env`. Perder a chave dos anexos é perder os arquivos cifrados; perder a frase do backup é não poder restaurar nenhum backup. Depois, em `deploy/backup.env`, preencha `BACKUP_EXTERNAL_DEST` (ver a seção 8).

**Teste primeiro com o certificado de HOMOLOGAÇÃO do Let's Encrypt** (para não esbarrar nos limites de emissão do ambiente real). Em `deploy/.env`, ponha:

```text
ACME_CA=https://acme-staging-v02.api.letsencrypt.org/directory
```

```bash
# 4.3 Confere tudo antes de subir (arquivos, permissoes, formato das chaves, recursos, DNS, compose).
sudo ./scripts/preflight.sh

# 4.4 Sobe: baixa as imagens, constroi as do ia.bimeta, sobe banco e ClamAV, APLICA AS MIGRATIONS, sobe o resto e espera ficar saudavel.
#     A primeira vez demora (construcao e download das assinaturas do ClamAV).
sudo ./scripts/subir.sh

# 4.5 Estado geral e a prova do ClamAV REAL (benigno, 20 MB e a amostra EICAR, inofensiva).
sudo ./scripts/status.sh
sudo ./scripts/teste-clamav.sh
```

Verifique no navegador `https://<dominio>/`: o certificado de homologação aparece como **não confiável** (esperado); a interface de login carrega. Quando tudo estiver certo, **passe para o certificado real**: em `deploy/.env` troque `ACME_CA` para `https://acme-v02.api.letsencrypt.org/directory` e rode `sudo docker compose --project-directory /opt/iabimeta/deploy up -d web`.

## 5. Rede, firewall e a Meta: checklist

| # | Item | Como conferir |
|---|---|---|
| 1 | O registro A do domínio aponta para o IP público fixo | `dig +short <dominio>` de fora da rede |
| 2 | NAT/redirecionamento 80 e 443 (TCP) do IP público para a VM | de um celular em 4G: `curl -I http://<dominio>` e `curl -I https://<dominio>` |
| 3 | Firewall do roteador/corporativo libera 80 e 443 de entrada | idem |
| 4 | `ufw` na VM: 22 só da rede de administração, 80 e 443 públicos | `sudo ufw status verbose` |
| 5 | Nenhuma porta interna exposta (5432, 3310, 8080) | de fora: `nmap -Pn -p 22,80,443,3310,5432,8080 <ip>` só deve mostrar 80 e 443 abertas |
| 6 | Certificado válido e renovação | `https://<dominio>` sem alerta; `docker compose logs web | grep -i certificate` |
| 7 | **Endereço real do cliente chega ao Api** | veja "Origem do cliente" abaixo |
| 8 | A URL do webhook: `https://<dominio>/webhooks/whatsapp/<id-opaco>` | o `<id-opaco>` sai do `Conectar-ContaWhatsApp.ps1` (`WHATSAPP-CONEXAO-REAL.md`) |
| 9 | O teste do webhook SEM a Meta | `Test-WebhookPublico.ps1` rodado do seu PC contra `https://<dominio>` (`HOMOLOGACAO-META.md`, seção 4.4) |
| 10 | Na Meta: URL de callback, **verify token** e campo `messages` assinado; o App Secret cadastrado no ia.bimeta | painel da Meta (feito por você; **nenhuma chamada real à Meta foi feita nesta etapa**) |

**Origem do cliente:** o Docker preserva o endereço de origem das conexões externas nas portas publicadas (modo padrão com iptables); se o roteador fizer NAT de origem (hairpin) o endereço visto será o do roteador. Confira: `docker compose --project-directory /opt/iabimeta/deploy logs web | grep remote_ip` deve mostrar o **IP público do cliente**, não o do Docker nem o do roteador. Se mostrar o do Docker/roteador, o limite de login por origem continua funcionando, mas vale por esse endereço único (ajuste o NAT para não mascarar a origem).

## 6. Teste de aceitação (na VM de TESTE, antes de qualquer atendimento real)

```bash
sudo ./scripts/aceitacao.sh --confirmo-que-e-teste
```

É **destrutivo para a disponibilidade** (derruba contêineres de propósito): rode numa pilha sem atendimento real. Ele prova, e imprime `PASSOU` ou `FALHOU` em cada item:

1. preflight e `docker compose config`; o `Caddyfile` válido (`caddy validate`);
2. a pilha sobe e cada serviço fica saudável; as migrations estão em dia;
3. **só o `web` publica portas (80 e 443)**; nada de banco, ClamAV ou Api escutando no host;
4. o HTTPS do Caddy responde `/api/health`, serve a interface, não expõe `/simulator`, e o webhook chega ao Api (4xx sem assinatura, nunca 5xx);
5. o **ClamAV real**: benigno limpo, benigno de 20 MB limpo (prova o `StreamMaxLength`) e a amostra EICAR detectada;
6. **persistência**: um marcador no banco e outro no volume de anexos sobrevivem a `down` e `up`;
7. **falha de serviço**: `worker`, `api` e `db` mortos à força (`docker kill`) são reiniciados sozinhos e voltam saudáveis; o ia.bimeta não fica crítico depois;
8. **ClamAV fora do ar**: `attachments report` acusa (saída 1) e volta quando ele volta;
9. **backup**: criado, cifrado, verificado por completo; um backup **adulterado é reprovado**; o **ensaio de restauração** num projeto descartável (`iabimeta-ensaio`) conclui e confere os anexos;
10. **nenhum segredo** (senha, chave ou frase) aparece nos logs dos contêineres.

Se algum item falhar, **não avance**: corrija e rode de novo. O teste de **reinício da VM** é manual: `sudo reboot` e, depois, `sudo ./scripts/status.sh` (a pilha deve voltar sozinha, porque todos os serviços têm `restart: unless-stopped` e o Docker sobe com o sistema).

## 7. Colocar a empresa piloto e agendar a operação

```bash
# 7.1 Empresa (tenant, administrador, fila, agente, IA, conta de WhatsApp, atendentes). SO valida por padrao; --aplicar grava.
#     O JSON nao leva segredo: os campos "...Env" sao NOMES de variaveis, cujos valores ficam num arquivo .env (chmod 600) so da empresa.
sudo ./scripts/empresa.sh minha-empresa.json minha-empresa.env
sudo ./scripts/empresa.sh minha-empresa.json minha-empresa.env --aplicar

# 7.2 Agenda o backup diario (03:30) e o monitor (a cada 5 min). Rode DEPOIS de um backup manual funcionar.
sudo ./scripts/backup.sh
sudo ./scripts/agendar.sh
```

A conexão do número de WhatsApp e o teste de prontidão usam os scripts PowerShell que já existem, rodados **do seu PC Windows** contra `https://<dominio>` (`Conectar-ContaWhatsApp.ps1`, `Test-ProntidaoWhatsApp.ps1`; `WHATSAPP-CONEXAO-REAL.md`). Eles só chamam a API por HTTPS.

## 8. Backup externo e restauração

- **Backup:** `scripts/backup.sh` gera `BACKUP_DIR/iabimeta-<data>.tar.gpg` (banco + anexos + manifesto, cifrado com AES-256 pela frase de `backup.env`), verifica por completo, mantém `BACKUP_KEEP_DAYS` dias (sempre os 2 mais recentes) e faz a **cópia externa**: `BACKUP_EXTERNAL_DEST=usuario@servidor:/pasta` (rsync por SSH; a chave SSH do root da VM precisa estar autorizada no servidor, sem senha) ou `/mnt/destino` (um disco ou compartilhamento já montado, **outro disco**). O disco da VM **não é** um backup.
- **Ensaio de restauração** (faça no início e depois periodicamente): `sudo ./scripts/ensaiar-restauracao.sh` restaura o backup mais recente num projeto descartável, confere com `attachments check` e derruba só o projeto de ensaio.
- **Desastre (VM nova):** instale o host (`instalar-host.sh`), copie o código e os arquivos `.env`, `app.env` e `backup.env` (a **mesma chave** dos anexos e a mesma frase) e leve o `.tar.gpg` **com o `.sha256` ao lado**. **Não rode o `subir.sh` antes**: o `restaurar.sh` só restaura em volumes **novos** (recusa se o volume do banco já existe, se o dos anexos não está vazio ou se há Api, Worker ou proxy no ar) e nunca apaga nem sobrescreve. Construa as imagens (`sudo docker compose --project-directory /opt/iabimeta/deploy build`) e rode `sudo ./scripts/restaurar.sh <arquivo>`. Depois: `sudo ./scripts/cli.sh migrate` (se o código for mais novo que o backup) e `sudo ./scripts/subir.sh`. O texto claro do dump passa por uma pasta temporária (`TMPDIR`, padrão `/tmp`) com espaço para o dump e os anexos: aponte `TMPDIR` para um disco folgado se preciso.
- Depois de restaurar, o `attachments check` lista as linhas "disponíveis" sem arquivo (por exemplo, apagadas pelo prazo de guarda depois do backup); veja `BACKUP-E-RESTAURACAO.md`.

## 9. Operação do dia a dia

| Para quê | Comando |
|---|---|
| Estado de tudo | `sudo ./scripts/status.sh [--tenant <slug>]` |
| Logs de um serviço | `sudo docker compose --project-directory /opt/iabimeta/deploy logs -f --tail 200 api` (ou `worker`, `web`, `db`, `clamav`) |
| Reiniciar um serviço | `sudo docker compose --project-directory /opt/iabimeta/deploy restart worker` |
| Qualquer comando do ia.bimeta | `sudo ./scripts/cli.sh <comando>`: `attachments report`, `attachments check`, `retention report`, `reset-password ...`, `migrate --list` |
| Atualizar para o código novo | levar o código novo e `sudo ./scripts/atualizar.sh` (faz **backup antes**, lista e aplica as migrations, recria só o que mudou) |
| Alertas | `BACKUP_DIR/alertas.log` e o webhook configurado; o monitor reinicia o Worker travado |

**Reversão de uma atualização:** se algo der errado **depois** das migrations, a volta segura é restaurar o backup feito pela própria atualização (seção 8) com a versão anterior do código (`deploy/.versao-anterior`). Reverter só a imagem pode não ser compatível com o esquema novo.

**Trocar uma chave:** faça um backup, crie a nova (por exemplo `an2`) em `app.env`, aponte o `...ActiveKeyId` para ela e **mantenha a antiga** na lista (as antigas só decifram; não há re-cifragem automática). Depois `sudo docker compose --project-directory /opt/iabimeta/deploy up -d api worker` recria os dois serviços com o `app.env` novo.

## 10. Solução de problemas

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| `preflight` reprova uma chave | formato errado | a chave é Base64 de 32 bytes (`head -c 32 /dev/urandom \| base64`); no `app.env`, `ActiveKeyId` aponta para a chave presente |
| O certificado não sai (log do `web`) | DNS errado, 80/443 bloqueadas ou limite do Let's Encrypt | confira os itens 1 a 3 da seção 5; teste com `ACME_CA` de homologação |
| Os arquivos recebidos ficam "pendentes" | o ClamAV ainda baixa as assinaturas, ou está fora do ar | `status.sh`; `docker compose logs clamav`; resolvem-se sozinhos quando ele fica saudável |
| `api` não fica saudável | migration pendente, banco fora, chave ausente (o Api não sobe com `Required` sem chave) | `docker compose logs api`; `cli.sh migrate --list` |
| 429 no login para todos | o proxy não é confiável (cabeçalho ignorado) | confira `EDGE_SUBNET` e `ForwardedHeaders__KnownNetworks__0` (devem ser a sub-rede real da rede `edge`) |
| Conflito de sub-rede ao subir | a sub-rede `172.29.89.0/24` colide com a sua rede | mude `EDGE_SUBNET` e `EDGE_WEB_IP` em `.env` |

## 11. O que NÃO foi provado (honestidade)

- **Docker Compose, os Dockerfiles, o Caddy, o `systemd` e a instalação do host nunca rodaram** (sem Docker nesta máquina). A prova é o `aceitacao.sh` na VM.
- O cliente do **ClamAV real**: provado só contra um daemon falso que fala o protocolo (o `teste-clamav.sh` na VM prova o real).
- A preservação do IP de origem do cliente depende do roteador (item 7 da seção 5).
- Desempenho, memória real da pilha e tempo de restauração com dados de verdade não foram medidos.
- **Nenhuma chamada real à Meta ou à OpenAI foi feita.**
- **Conhecimento híbrido (ADR 0034):** o banco agora usa a imagem `pgvector/pgvector:pg17` (`DB_IMAGE`), o serviço `ollama` é opcional (perfil `ia-local`) e tudo é desligado por padrão. **Banco já existente** (volume criado com `postgres:17`): crie a extensão antes de ligar o modo (`docker compose exec db psql -U postgres -d iabimeta -c "CREATE EXTENSION IF NOT EXISTS vector"`) e rode `migrate` depois de um backup. **Restauração** exige a mesma imagem com pgvector. Nada disso foi executado com Docker nem com Ollama/pgvector reais; veja `CONHECIMENTO-HIBRIDO.md` (validações pendentes e plano de homologação).
- **Motor de Integração Empresarial (ADR 0035):** nova chave mestra `Capabilities__Secrets__*` (gerada por `gerar-segredos.sh`, conferida por `preflight.sh`; **guarde cópia fora da máquina**: perdê-la é recadastrar as credenciais das integrações) e a política de destino de saída `Capabilities__Options__Outbound__*` (decisão do operador: um ERP em rede interna exige liberar o CIDR exato). `migrate` aplica o esquema `capabilities` (depois de backup). Veja `MOTOR-DE-INTEGRACAO.md`; nada foi exercitado contra um ERP real nem na VM.
- **Embedded, autenticação federada (ADR 0039, etapa E1; branch `feature/v1-embedded`, só depois da candidata `v1-rc1`):** nova chave mestra `Identity__Secrets__*` (gerada por `gerar-segredos.sh`, conferida por `preflight.sh`; **guarde cópia fora da máquina**: perdê-la é gerar de novo as chaves de assinatura dos ERPs) para o segredo das chaves de integração; `migrate` aplica a migration `EmbeddedFederation` do Identity (tabelas novas e colunas aditivas com padrão; depois de backup). Limites de taxa em `Embedded__RateLimit__*` (padrões: 120 chamadas assinadas por minuto por chave e 30 trocas de ticket por minuto por origem de rede) e parâmetros em `Identity__EmbeddedTicketLifetime` (60 s), `Identity__EmbeddedSessionLifetime` (30 min), `Identity__MaxEmbeddedSessionsPerUser` (5), `Identity__IntegrationSignatureWindow` (2 min). O ERP assina o caminho SEM o prefixo `/api`. Nada foi exercitado com Docker, Caddy nem um ERP real.
- **Embedded, E1-R (correções da auditoria; ADR 0039; commits `7a07d91`, `c33f1b9`, `011dac1`; gate verde, com uma falha intermitente de concorrência sem causa comprovada, ver `EMBEDDED-REAUDITORIA-E1R.md`):** `migrate` aplica também a migration `EmbeddedHardening` (cinco colunas aditivas; `Down` as remove). **Versões mistas e retrocesso:** o token da sessão embutida é `ibe_` com hash de domínio próprio, então uma API antiga (`v1-rc1`) não o acha nem o aceita; ao voltar para a candidata as sessões embutidas ficam inertes (ninguém entra por Embedded na versão antiga). **Limites de taxa:** a cota anterior à autenticação é só por origem de rede (`Embedded__RateLimit__IntegrationPermitLimit`, padrão 120 por minuto por IP; atrás do Caddy exige `ForwardedHeaders` ligado, ADR 0033, ou todos compartilham o IP do proxy); depois, `Identity__IntegrationPermitsPerKey` (30 falhas por minuto por chave conhecida) e `Identity__IntegrationPermitsPerConnection` (600 chamadas autenticadas por minuto por conexão). **Caddy:** o registro de acesso agora apaga todos os cabeçalhos da requisição; o `aceitacao.sh` envia um canário (`X-IaB-Signature`, `Authorization`, cabeçalho inventado) e procura nos logs. **Ao assinar:** o caminho é EXATAMENTE `/embed/v1/...` (sem `/api`, sem barra final, sem consulta); o ticket de contexto exige o `sessionId` da sessão que o resgatará. Nada disso foi exercitado com Docker, Caddy nem ERP real. **Segunda rodada (ADR 0039):** a string de conexão da aplicação fixa `Maximum Pool Size=30;Timeout=15` em `docker-compose.yml` (Api e Worker: 2 x 30 = 60, abaixo do `max_connections=100` do PostgreSQL, com folga para backup e psql); o padrão do Npgsql (100) igualava o limite do servidor e o esgotava sob rajada. As transações do Embedded esperam no máximo `Identity__EmbeddedLockTimeout` (padrão 5 s) por uma trava e, passado o tempo ou em deadlock, respondem 503 com `Retry-After` (o ERP deve repetir). Valores de partida do piloto, sem medição de produção: se o banco ou o número de processos mudar, ajuste o pool junto com o `max_connections`.
- **Consulta do agente ao Motor de Integração (ADR 0036):** desligada por padrão (`Agents__Options__IntegrationLookupsEnabled`); o Worker compõe o módulo e usa as mesmas chaves e política de saída do Api. Com ela ligada, `TurnMaxDuration` precisa passar o dobro de `ModelCallTimeout` mais `LookupBudget`. Veja `MOTOR-DE-INTEGRACAO.md` §13; nada foi exercitado com o modelo real nem contra um ERP real.
- **Chatbot Visual / Workflows (ADR 0037 e 0038):** desligado por padrão (`Workflows__Options__Enabled`, lido só pelo **Worker**, o único hospedeiro do executor). Nova chave mestra `Workflows__Secrets__*` (gerada por `gerar-segredos.sh`, conferida por `preflight.sh`; **guarde cópia fora da máquina**: sem ela as variáveis das instâncias existentes não abrem). `migrate` aplica o esquema `workflows` (tabelas, gatilho de imutabilidade das versões, **outbox da drenagem `FlowDrainOutbox`**) e a migration do Conversations com o autor `Bot`; depois de migrar, atualize o CLI e **todos** os Workers (a trava de execução e o relay de retomada são compartilhados). Mudou `app.env`? **Recrie** o Worker (`docker compose up -d --no-deps --force-recreate worker`): `restart` não relê o arquivo. Permissões novas: leitura, administração, publicação e teste de fluxos (conceda por papel). Retenção: categoria `FlowInstances` (`Retention__Days__FlowInstances`, 30 dias). Operação: `sudo ./scripts/cli.sh workflows status` e `workflows drain --confirm [--tenant <slug>]` (ver `CHATBOT-VISUAL.md` §5). A criação de uma ativação confere no servidor que organização, unidade e canal existem e pertencem ao tenant. Nada rodou na VM nem com a Meta real.
- **Embedded, E1-R quarta rodada (auditoria durável do consumo do ticket; ADR 0039):** `migrate` aplica também a migration `AuditOutbox` (tabela `identity.audit_outbox`, aditiva; o `Down` a remove). **O Worker passa a ser obrigatório para a trilha do Embedded:** o sucesso da troca do ticket e do resgate de contexto é guardado na transação do consumo e entregue à trilha pelo Worker (tentativa a cada 30 s por padrão, `Worker:ReconcileInterval`: intervalo de TENTATIVA, não prazo máximo garantido); sem o Worker no ar as linhas se acumulam em `identity.audit_outbox` (registro de referência, sem expurgo) e a trilha atrasa. Para conferir: `SELECT count(*) FROM identity.audit_outbox` deve tender a zero (a trilha só fica em dia com o Worker no ar e a trilha disponível). **Versões mistas e retrocesso:** uma API antiga (anterior à migration) não grava nessa tabela e audita o sucesso de forma síncrona; ao voltar para ela a tabela fica sem uso (linhas pendentes continuam sendo entregues por um Worker novo). **Resposta perdida depois do commit:** o ERP deve emitir um novo ticket e repetir a troca (o ticket de uso único não é reutilizável). Nada disso foi exercitado com Docker, Caddy nem ERP real.
- **Embedded, E1-R quinta rodada (outbox estendida e recuperação; ADR 0039):** `migrate` aplica também a migration `AuditOutboxRetry` (colunas `attempts`, `last_error`, `next_attempt_at` em `identity.audit_outbox`; aditiva, o `Down` as remove; aplique ANTES de subir o Worker novo). A auditoria de sucesso da criação, rotação e revogação de chaves, das conexões, dos vínculos, das revogações de sessão, da emissão de tickets e do provisionamento JIT agora também passa pela outbox e é entregue pelo Worker: sem o Worker no ar a trilha desses eventos atrasa, mas as operações respondem normalmente. **Entrada problemática:** se a trilha recusa uma linha, ela NÃO é descartada nem trava as demais; fica com `attempts`, o TIPO do erro em `last_error` e `next_attempt_at` com recuo de 30 s que dobra até 1 h. O Worker registra a cada ciclo os indicadores (pendentes, com falha, máximo de tentativas e idade da mais antiga): um AVISO "com falha de entrega" pede investigação da trilha. Consulta manual: `SELECT count(*), max(attempts), min(created_at) FROM identity.audit_outbox`. **Intervalo de 30 s é de TENTATIVA, não prazo máximo garantido.** Se a resposta de uma criação de chave se perder depois do commit, o segredo se perdeu: revogue a chave e crie outra. Versão mista: uma API anterior a esta rodada audita esses fluxos de forma síncrona; o Worker novo continua entregando as linhas existentes. Nada disso foi exercitado com Docker, Caddy nem ERP real.
