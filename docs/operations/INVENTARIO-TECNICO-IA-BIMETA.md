# Inventário técnico do ia.bimeta (para a equipe de infraestrutura)

**Documento A de quatro** (A inventário · B manual de instalação · C checklist · D pendências e decisões). Extraído do repositório em 23/11/2026, a partir do commit `09eae59` da branch `feature/v1-embedded`. Cada afirmação cita o arquivo que a comprova, em caminho relativo à raiz do pacote de código que você receberá. **Regra de leitura:** o que está marcado **PRONTO** existe como arquivo no pacote; **NÃO VALIDADO** significa que existe, mas **nunca rodou com Docker** (a máquina de desenvolvimento não tem Docker, WSL nem virtualização); **AUSENTE** significa que não existe no pacote e precisa ser configurado ou desenvolvido. Nada aqui afirma que um componente funciona só porque aparece no planejamento.

## 1. O que é o produto (o mínimo que a infraestrutura precisa saber)

Plataforma web de atendimento omnichannel com agentes de IA, multiempresa (multi-tenant). Uma pilha atende **várias empresas** (tenants) isoladas por dados e permissões. O primeiro canal é o **WhatsApp oficial da Meta (Cloud API)**. A IA de cada empresa é contratada pela própria empresa (chave do provedor cadastrada pela empresa e guardada cifrada no banco). A interface do atendente é uma aplicação web React. Existe também o modo **Embedded**: a mesma interface incorporada (iframe) em um sistema externo da empresa, com autenticação federada por assinatura HMAC feita pelo **servidor** desse sistema externo.

## 2. Arquitetura efetivamente implementada

Monólito modular em .NET 10 com **dois processos** (Api e Worker), um **PostgreSQL**, um **verificador de vírus (ClamAV)** e um **proxy HTTPS (Caddy)** que também serve a interface. Não há filas externas (RabbitMQ, Redis, Kafka): a mensageria entre módulos é uma *outbox* no próprio PostgreSQL entregue pelo Worker (`backend/building-blocks/IaBimeta.Messaging`).

```text
Internet ──80/443──► [web: Caddy + interface React estática] ──► [api :8080]
                                                                    │
                       rede interna "back" ◄───────────────────────┤
                         api ─► db (PostgreSQL 17)     worker ─► db
                         api ─► clamav (clamd :3310)   worker ─► clamav (mesma configuração do Api)
                         worker ─► Graph API da Meta (saída)   worker ─► provedor de IA (saída)
```

Comprovado em `deploy/docker-compose.yml` (serviços `db`, `clamav`, `api`, `worker`, `web`, opcional `ollama`), `deploy/web/Caddyfile`, `deploy/Dockerfile`, `deploy/web/Dockerfile`.

### 2.1 Módulos do backend (pasta `backend/modules/`)

| Módulo | Responsabilidade resumida |
|---|---|
| `tenancy` | empresas (tenants), organizações, unidades, setores |
| `identity` | usuários, papéis e permissões, sessões, troca/redefinição de senha, **Embedded** (conexões de integração, chaves HMAC, tickets, sessão embutida) |
| `conversations` | contatos, sessões de atendimento, mensagens, turnos de IA, **vínculo cliente-contato, consentimento e revogação** (E2), arquivos de mídia |
| `contact-center` | filas, atribuição, atendentes, histórico, transferência IA↔humano |
| `channels` | contas e números do WhatsApp, recebimento de webhook (Inbox), envio (Sender), modelos de mensagem, credenciais cifradas |
| `agents` | agente de IA: instruções, FAQ, turnos, handoff |
| `ai-gateway` | configuração de IA por empresa (BYOK), catálogo de modelos, limites e registro de consumo (ledger) |
| `knowledge` | conhecimento aprovado; busca semântica opcional (pgvector + Ollama) |
| `capabilities` | Motor de Integração Empresarial (chamadas a ERPs por operações tipadas) |
| `workflows` | Chatbot Visual (fluxos); **desligado por padrão** |
| `audit` | trilha de auditoria |

Adaptadores (`backend/adapters/`): `IaBimeta.Adapters.WhatsApp` (Meta Cloud API), `IaBimeta.Adapters.Ai.OpenAI`, `IaBimeta.Adapters.Ai.Ollama`. **Não existe adaptador do provedor Anthropic** no código, embora o catálogo de modelos o liste em `backend/host/IaBimeta.Api/appsettings.json` (ver Documento D).

### 2.2 Processos (hosts)

| Processo | Projeto | Papel |
|---|---|---|
| **Api** | `backend/host/IaBimeta.Api` | HTTP (porta 8080 no contêiner): API da interface, rotas do Embedded, webhook do WhatsApp, **comandos de operação** (veja §8). Em produção **não aplica migrations sozinho** |
| **Worker** | `backend/host/IaBimeta.Worker` | Segundo plano, sem porta HTTP: relay da outbox, executor de turnos de IA, reconciliadores, entrega da auditoria do Embedded, expurgo técnico agendado, indexador semântico (se ligado), executor de fluxos (se ligado) |

Serviços hospedados do Worker (`backend/host/IaBimeta.Worker/WorkerHost.cs`): `OutboxRelayService`, `AgentsTurnExecutorService`, `AgentsReconciliationService`, `ContactCenterReconciliationService`, `IdentityAuditDeliveryService`, `RetentionSchedulerService`, `SemanticIndexerService`, `AiConfigurationReport`, `FlowExecutorService` (só com `Workflows__Options__Enabled=true`).

## 3. Versões exatas

| Componente | Versão | Onde está fixada |
|---|---|---|
| .NET SDK (desenvolvimento) | 10.0.114, `rollForward: latestPatch` | `global.json` |
| .NET na imagem de build e execução | tag `10.0-noble` (SDK e `aspnet`, Ubuntu 24.04) | `deploy/Dockerfile` (`ARG DOTNET_TAG`) |
| Entity Framework Core | 10.0.14; Npgsql EF Core 10.0.3; `dotnet-ef` 10.0.14 | `backend/**/*.csproj`, `dotnet-tools.json` |
| PostgreSQL | **17** (imagem `pgvector/pgvector:pg17`, que é o PostgreSQL 17 oficial mais a extensão pgvector) | `deploy/docker-compose.yml` (`DB_IMAGE`) |
| ClamAV | `clamav/clamav:1.4_base-debian13-slim` | `deploy/docker-compose.yml`, `deploy/.env.example` |
| Caddy | `caddy:2` (**tag flutuante**) | `deploy/web/Dockerfile` |
| Node.js (build da interface) | `node:24-bookworm-slim` (tag flutuante da série 24) | `deploy/web/Dockerfile` |
| Frontend | React ^19.3.1, Vite ^8.3.6, TypeScript ^7.0.2, `@xyflow/react` 12.12.0, Vitest ^5.1.0 | `frontend/package.json`, `frontend/package-lock.json` (o build usa `npm ci`) |
| Ollama (opcional) | `ollama/ollama:latest` (**não fixada**) | `deploy/docker-compose.yml` (`OLLAMA_IMAGE`) |
| Ubuntu do host | 24.04 LTS (os scripts conferem) | `deploy/scripts/instalar-host.sh` |
| Docker Engine e Compose | pelo repositório oficial da Docker (versão corrente na instalação; **sem versão fixada**) | `deploy/scripts/instalar-host.sh` |
| Graph API da Meta (padrão do adaptador) | v26.0, trocável por configuração | `backend/adapters/IaBimeta.Adapters.WhatsApp/WhatsAppOptions.cs` |

Pacotes NuGet: só `nuget.org` (`nuget.config`, `<clear />` e uma única fonte). A construção das imagens exige saída para NuGet e npm.

## 4. O que já existe para implantação (PRONTO no pacote, NÃO VALIDADO em Docker)

Todo o material está em `deploy/` (resumo em `deploy/README.md`):

| Arquivo | Função |
|---|---|
| `docker-compose.yml` | a pilha completa (projeto Compose `iabimeta`) |
| `Dockerfile` | imagens `api` e `worker` (.NET 10, usuário sem root UID 1654, `DOTNET_EnableDiagnostics=0`) |
| `web/Dockerfile`, `web/Caddyfile` | proxy com a interface compilada |
| `postgres/init/01-app-role.sh` | cria o papel `iabimeta` (sem superusuário) e o banco `iabimeta`, e a extensão `vector` quando disponível |
| `.env.example`, `app.env.example`, `backup.env.example` | modelos sem valores |
| `scripts/*.sh` (18 executáveis + `lib.sh`) | `instalar-host`, `gerar-segredos`, `preflight`, `subir`, `atualizar`, `status`, `monitor`, `alerta`, `backup`, `verificar-backup`, `restaurar`, `ensaiar-restauracao`, `agendar`, `cli`, `empresa`, `teste-clamav`, `aceitacao`, `ia-local` |
| `systemd/` | timers de backup (diário 03:30) e de monitor (a cada 5 min, 3 min após o boot) |
| `ollama/` | proxy autenticado para IA local em **outra** máquina (opcional) |
| `tests/testar-scripts.sh` | testa os scripts de backup e restauração contra um `docker` falso (roda sem Docker) |

O que foi provado: sintaxe dos scripts, compilação (`dotnet publish`), build da interface, invariantes de texto do Compose/Caddyfile e o comportamento dos scripts de backup/restauração contra um Docker falso. **O que nunca rodou:** o Compose real, os Dockerfiles, o Caddy, o `systemd`, o ClamAV real, o Ollama/pgvector real. O primeiro uso real é `deploy/scripts/aceitacao.sh` na VM (`docs/operations/IMPLANTACAO-LINUX.md` §6, incluído no pacote).

## 5. Rede: portas, redes e exposição

| Serviço | Porta | Exposição | Onde |
|---|---|---|---|
| `web` (Caddy) | **80 e 443** (TCP) | **única exposição pública** | `docker-compose.yml` (`ports`) |
| `api` | 8080 | só rede interna (`edge` e `back`) | `docker-compose.yml` |
| `db` (PostgreSQL) | 5432 | só rede interna `back` | idem |
| `clamav` (clamd) | 3310 | só rede interna `back` | idem |
| `ollama` (opcional) | 11434 | só rede `back`, perfil `ia-local` | idem |
| proxy do Ollama em outra máquina | 11435 | rede privada | `deploy/ollama/Caddyfile` |
| SSH | 22 | **só** da rede de administração informada (`ufw`) | `deploy/scripts/instalar-host.sh` |

Redes Docker: `edge` (sub-rede **fixa** `172.29.89.0/24`, onde vivem o `web` em `172.29.89.2` e a `api`) e `back` (interna). O Api só confia nos cabeçalhos `X-Forwarded-*` vindos da sub-rede `edge` (`ForwardedHeaders__KnownNetworks__0`). Se a sub-rede colidir com a sua rede, mude `EDGE_SUBNET` e `EDGE_WEB_IP` em `.env`. **Atenção conhecida:** o Docker publica portas por fora do `ufw`; por isso só o `web` publica portas.

### 5.1 Rotas públicas do proxy (`deploy/web/Caddyfile`)
- `/webhooks/whatsapp/*` → `api:8080`, corpo máximo 2 MB, **sem alteração** (a aplicação valida a assinatura HMAC `X-Hub-Signature-256` sobre os bytes brutos).
- `/api/*` → `api:8080` **removendo o prefixo `/api`**, corpo máximo 30 MB. É por aqui que passam a interface, o Embedded e as chamadas assinadas do sistema externo (`/api/embed/v1/...`).
- qualquer outro caminho → interface React estática (`/srv/frontend`, com `try_files` para `index.html`).
- Cabeçalhos de resposta definidos: `Strict-Transport-Security max-age=31536000`, `X-Content-Type-Options nosniff`, `Referrer-Policy no-referrer`; remove `Server`. **Não há** `Content-Security-Policy`, `frame-ancestors` nem `X-Frame-Options` (ver §11, lacuna L1).
- Registro de acesso em JSON na saída padrão **sem nenhum cabeçalho de requisição** (por desenho: cabeçalhos de assinatura não devem ir a log).
- O `admin` do Caddy está desligado; o certificado (Let's Encrypt) é obtido e renovado sozinho e fica no volume `caddy_data`.

## 6. Volumes e diretórios persistentes

| Volume Compose | Conteúdo | Observação |
|---|---|---|
| `pgdata` | dados do PostgreSQL (`/var/lib/postgresql/data`, com `--data-checksums`) | **criticidade máxima** |
| `anexos` | arquivos de mídia **cifrados** (`/var/lib/iabimeta/anexos` no Api e no Worker, compartilhado) | entra no backup; a chave de cifra é indispensável |
| `clamdb` | assinaturas do ClamAV | recriável (baixa de novo) |
| `caddy_data`, `caddy_config` | certificados e chaves ACME | não apagar sem necessidade (limites do Let's Encrypt) |
| `ollamadata` | modelos do Ollama (só com o perfil `ia-local`) | opcional |

No host: código em `/opt/iabimeta` (sugestão do guia), segredos em `deploy/.env`, `deploy/app.env`, `deploy/backup.env` (permissão 600, **fora do repositório**), backups cifrados em `/var/backups/iabimeta` (`BACKUP_DIR`, criado 0700), alertas em `BACKUP_DIR/alertas.log`.

## 7. Dependências e ordem de inicialização

Ordem no Compose (`depends_on`): `db` (saudável) → `api` (saudável) e `worker` (ambos esperam só o `db`) → `web` (espera o `api` saudável). **O `clamav` não é dependência de ninguém**: a aplicação roda com o verificador obrigatório (`Conversations__Attachments__Scanner__Required=true`), então arquivos recebidos ficam **pendentes** até o ClamAV ficar saudável (a primeira subida baixa as assinaturas, `start_period` de 600 s). O Api **não sobe** se uma chave obrigatória faltar. **Migrations:** em produção o Api e o Worker não as aplicam; quem aplica é o comando `migrate` (chamado por `subir.sh` na primeira instalação e por `atualizar.sh` nas atualizações, este com backup antes). Todos os serviços têm `restart: unless-stopped` e logs `json-file` (10 MB × 5 arquivos).

## 8. Comandos de operação da aplicação

Todos são argumentos do ponto de entrada do Api e rodam por `deploy/scripts/cli.sh <comando>` (contêiner descartável com a mesma configuração). Fonte: `backend/host/IaBimeta.Api/Program.cs`.

| Comando | Para quê | Código de saída |
|---|---|---|
| `migrate` (`--list`) | aplica/lista as migrations de todos os módulos | 0 ok; 1 há o que olhar; 2 uso/configuração |
| `status` | estado do PostgreSQL, migrations, Api, Worker, filas, contas, arquivos, **consentimento (possíveis saídas pendentes)**, retenção | 0 / 1 atenção / 2 crítico |
| `provision` (via `empresa.sh`) | coloca uma empresa em funcionamento a partir de JSON (valida por padrão; `--aplicar` grava) | idem |
| `reset-password` | emite token de redefinição de senha | idem |
| `attachments report` / `check` | relatório e conferência dos arquivos de mídia | idem |
| `retention report` | o que o expurgo técnico removeria | idem |
| `workflows status` / `drain` | Chatbot Visual (só se ligado) | idem |

Os scripts PowerShell de `tools/ops/` (conexão da conta do WhatsApp, teste de prontidão, teste do webhook público, diagnóstico de envio) **são para Windows**; não há equivalente em shell no pacote (ver Documento D).

## 9. Verificação de saúde

| Alvo | Mecanismo | Comprova |
|---|---|---|
| Api | `GET /health` → `200 {"status":"ok"}` (por HTTPS: `https://<dominio>/api/health`) | **só que o processo responde**; **não verifica o banco** |
| db | `pg_isready -h 127.0.0.1 -U postgres -d iabimeta` (healthcheck do Compose) | servidor aceitando conexões de rede |
| clamav | protocolo `nPING` → `PONG` na 3310 (healthcheck) | clamd pronto |
| worker | **não tem endpoint**; a presença/atividade dele é lida pelo comando `status` (conexões dele no banco) e pelo `monitor.sh` | ver `status.sh` |
| web | nenhum healthcheck próprio; `status.sh` e `monitor.sh` testam o HTTPS local | — |
| Visão geral | `deploy/scripts/status.sh` (contêineres, `status` da aplicação, disco, idade do último backup) | saída 0 / 1 / 2 |

## 10. Integrações externas necessárias

| Integração | Direção | Obrigatória? | Detalhe |
|---|---|---|---|
| **Meta WhatsApp Cloud API** | entrada (webhook) e saída (Graph API) | sim, para o canal real | webhook em `https://<dominio>/webhooks/whatsapp/<id-opaco>`; saída HTTPS 443 para a Graph API; credenciais cifradas no banco. Detalhes no Documento B §14 |
| **Provedor de IA** | saída HTTPS | sim, para respostas de IA | adaptador real implementado: **OpenAI** (`https://api.openai.com/v1/`); chave por empresa (BYOK), cifrada |
| **Let's Encrypt** | saída (e validação pela porta 80) | sim | `ACME_CA` |
| **ClamAV (assinaturas)** | saída para `database.clamav.net` | sim | atualização diária |
| Docker Hub, MCR, NuGet, npm | saída | só na construção/atualização | |
| **Sistema externo da empresa (ERP/CRM)** — Embedded | **entrada**: o servidor dele chama `https://<dominio>/api/embed/v1/...` com assinatura HMAC; e o navegador carrega a interface em iframe | opcional (só se a empresa usar Embedded) | origem do iframe cadastrada por conexão; relógio sincronizado (janela de assinatura 2 min) |
| Motor de Integração (ERPs, chamadas de saída) | saída | opcional | política de destino: só endereços públicos HTTPS 443, a menos que o operador libere CIDR/porta (`app.env.example`) |
| Ollama (IA local) | interna ou rede privada | opcional, **desligada por padrão** | `Knowledge__Semantic__Enabled=false` |
| Destino de backup externo e webhook de alerta | saída | backup externo **obrigatório no piloto** | `backup.env` |

## 11. O que NÃO existe ou NÃO está validado (lacunas, com criticidade)

Criticidade: **Alta** = impede começar; **Média** = tratar antes de ligar clientes reais ou Embedded; **Baixa** = melhoria.

| Id | Lacuna | Criticidade | Quem trata |
|---|---|---|---|
| L1 | **Sem `Content-Security-Policy`/`frame-ancestors`/`X-Frame-Options` no Caddy** (verificado por busca em `deploy/`, `backend/` e `frontend/`). Hoje qualquer site pode incorporar a interface em iframe (risco de clickjacking); o Embedded exigirá restringir a origens autorizadas | **Média** (antes de usar Embedded com sistema externo real) | Desenvolvimento + infraestrutura |
| L2 | Compose, Dockerfiles, Caddy, systemd e scripts **nunca rodaram em Docker real** | **Alta** (é justamente a primeira prova a fazer) | Infraestrutura, com `aceitacao.sh` |
| L3 | `/health` do Api é **raso** (não consulta o banco); o Worker não tem endpoint | Média | monitoramento usa `status`; endpoint profundo é decisão de desenvolvimento |
| L4 | Imagens com tag flutuante: `caddy:2`, `node:24-bookworm-slim`, `ollama:latest`; Docker Engine sem versão fixada | Média (reprodutibilidade) | Infraestrutura fixa por digest/versão após a 1ª validação |
| L5 | Sem atualização automática de segurança do Ubuntu (`unattended-upgrades`), sem `fail2ban`, sem política de rotação de logs do host além do `json-file` | Média | Infraestrutura |
| L6 | Sem monitoramento externo (apenas `status.sh`, `monitor.sh`, `alertas.log` e webhook opcional); sem métricas/painéis | Média | Infraestrutura + decisão do proprietário |
| L7 | Scripts PowerShell de conexão do WhatsApp/prontidão/webhook exigem um PC Windows (PowerShell) chamando a API por HTTPS | Média | Desenvolvimento (equivalente) ou execução a partir de um PC |
| L8 | Adaptador do provedor Anthropic **não existe**; só OpenAI (e Ollama local) | Baixa para a homologação | Desenvolvimento |
| L9 | Nenhuma medição de desempenho, memória real da pilha ou tempo de restauração com dados reais | Média | Homologação |
| L10 | Preservação do IP real do cliente depende do NAT do roteador (se mascarar a origem, o limite de login por origem vale por um endereço único) | Média | Infraestrutura |
| L11 | Nenhuma chamada real à Meta ou ao provedor de IA foi feita pelo produto até hoje | **Alta** para o piloto | Homologação (H4/H5) |
| L12 | `deploy/.versao-anterior` e a reversão dependem de restaurar backup (não há reversão só de imagem após migrations) | Informativo | Procedimento no Documento B §19 |
| L13 | Política jurídica de retenção/consentimento não definida (consentimento é registro de referência, sem prazo e sem expurgo) | **Alta** antes de produção; **não** impede a homologação técnica | Proprietário/jurídico |

## 12. Configuração por variáveis de ambiente (resumo; lista completa nos modelos)

Três arquivos, todos permissão 600 e fora do repositório (gerados por `scripts/gerar-segredos.sh`):
- `.env` (Compose): `DOMAIN`, `ACME_EMAIL`, `ACME_CA`, `POSTGRES_PASSWORD`, `APP_DB_PASSWORD`, `IABIMETA_VERSAO`, `CLAMAV_IMAGE`, `EDGE_SUBNET`, `EDGE_WEB_IP` (e opcionais `DB_IMAGE`, `KNOWLEDGE_SEMANTIC_ENABLED`, `OLLAMA_*`).
- `app.env` (Api e Worker): chaves mestras de cifra Base64 de 32 bytes por módulo (`Channels__Secrets__*`, `AiGateway__Secrets__*`, `Capabilities__Secrets__*`, `Identity__Secrets__*`, `Workflows__Secrets__*`, `Conversations__Attachments__Encryption__*`), `Agents__Options__KnowledgeGapHashKey` e chaves opcionais comentadas.
- `backup.env` (só scripts do host): `BACKUP_PASSPHRASE`, `BACKUP_DIR`, `BACKUP_KEEP_DAYS`, `BACKUP_EXTERNAL_DEST`, `ALERT_WEBHOOK_URL`, `ALERT_BACKUP_MAX_AGE_HOURS`, `ALERT_DISK_PERCENT`.
- Fixados no próprio Compose: string de conexão do banco (`Host=db;...;Maximum Pool Size=30;Timeout=15`; Api e Worker 2 × 30 = 60 conexões, abaixo do `max_connections` de 100), `Channels__Webhook__PublicBaseUrl=https://${DOMAIN}`, `ForwardedHeaders__Enabled=true`, `Conversations__Attachments__RootPath`, escaneamento ClamAV obrigatório, cifra de anexos obrigatória, expurgo técnico diário ligado no Worker.

**Perder uma chave mestra de cifra é perder o que ela protege** (credenciais do WhatsApp, chaves de IA das empresas, arquivos de mídia, segredos das integrações). Guardar cópia **fora da VM** é obrigatório.

## 13. Requisitos de relógio e rede que a aplicação exige
- **Relógio sincronizado (NTP):** o Embedded rejeita assinaturas fora de uma janela de 2 minutos (`Identity__IntegrationSignatureWindow`), e o Let's Encrypt e o HTTPS dependem de hora correta. O `instalar-host.sh` liga o NTP e o `preflight.sh` confere.
- **Saída HTTPS (443)** liberada para os destinos do §10. **Entrada 80 e 443** do mundo.

## 14. Arquivos de referência no pacote
`deploy/README.md`, `docs/operations/IMPLANTACAO-LINUX.md` (guia que serviu de base ao Documento B), `docs/operations/BACKUP-E-RESTAURACAO.md`, `docs/operations/OPERACAO-E-SUPERVISAO.md`, `docs/operations/WHATSAPP-CONEXAO-REAL.md`, `docs/operations/HOMOLOGACAO-META.md`, `docs/operations/NOVA-EMPRESA.md`, `docs/adr/0033-*.md` (decisão da implantação em VM), `docs/adr/0039-*.md` e `0041-*.md` (Embedded, consentimento). **O pacote inicial (etapa H1) inclui** `deploy/README.md`, `IMPLANTACAO-LINUX.md`, `BACKUP-E-RESTAURACAO.md` e `OPERACAO-E-SUPERVISAO.md`; os documentos de WhatsApp, `NOVA-EMPRESA.md` e os ADRs citados **não** vão no pacote inicial e serão enviados quando a etapa correspondente começar. Os documentos A a D e a `ORIENTACAO-H1-IA-BIMETA.md` são **autocontidos** para a instalação.
