# Manual de instalação do ia.bimeta em VM Ubuntu Server (homologação)

**Documento B de quatro** (A inventário · B este manual · C checklist · D pendências e decisões). Público: especialista de infraestrutura. Objetivo: de uma VM Ubuntu **recém-instalada** até o ia.bimeta **funcionando em HTTPS**, com backup testado, para a homologação. Leia antes o Documento A (inventário e lacunas).

> **Estado de validação (leia primeiro).** Os arquivos de implantação (`deploy/`) foram conferidos por texto, compilação e testes de scripts contra um Docker falso, mas **nunca rodaram com Docker real**. Portanto **você é quem executa a primeira prova real**, por meio do teste de aceitação (`aceitacao.sh`, passo 13). Se algo falhar, pare e registre o erro exato (comando, saída, hora): não improvise correção no código nem nos scripts sem avisar a equipe de desenvolvimento.
>
> **Convenções.** `<PLACEHOLDER>` = valor que depende da sua VM, domínio ou credenciais. Comandos do ia.bimeta (`./scripts/...`) são os **existentes no pacote**; não há outros. Nada neste manual exige credenciais reais da Meta ou do provedor de IA: elas só entram na homologação funcional, depois. **Nunca** cole segredos em chats, e-mails, tickets ou no repositório.

## 0. Pré-requisitos (reúna antes de começar)

| Item | Detalhe |
|---|---|
| VM | Ubuntu Server **24.04 LTS** (64 bits), dedicada. Dimensionamento no §1 |
| IP público **fixo** e DNS | registro **A** de `<DOMINIO>` (por exemplo `homolog.<empresa>.com.br`) apontando para o IP público; TTL 300 s no início |
| Redirecionamento (NAT/firewall de borda) | TCP **80 e 443** do IP público para a VM (IP fixo na LAN, se houver NAT). A 80 é necessária para o Let's Encrypt |
| Rede de administração | CIDR de onde você acessa por SSH: `<REDE_ADMIN_CIDR>` |
| Pacote de código | o arquivo `iabimeta-<hash>.tar.gz` entregue pela equipe de desenvolvimento **e** o arquivo `.sha256` dele (ver §10) |
| Destino de backup externo | `<DESTINO_BACKUP>` (outro servidor por SSH/rsync **ou** um disco/compartilhamento que **não** seja a VM) |
| E-mail de avisos do certificado | `<EMAIL_ACME>` |
| (opcional) URL de webhook de chat para alertas | `<URL_ALERTA>` |
| Cofre/gerenciador de senhas | onde guardar as cópias dos segredos **fora da VM** (§12) |

## 1. Dimensionamento recomendado

| Recurso | Homologação | Observação |
|---|---|---|
| vCPU | **4** (mínimo 2) | a construção das imagens é o pico de CPU |
| Memória | **8 GB** (o `preflight.sh` reprova abaixo de ~5,5 GB e avisa abaixo de ~7 GB) | só o ClamAV usa ~1,2 GiB e até ~2,4 GiB na recarga diária de assinaturas; com o serviço de IA local (Ollama, opcional) co-localizado, **16 GB ou mais** e ≥ 6 vCPU |
| Disco | **100 GB SSD** (mínimo 60 GB), partição única simples | imagens, banco, anexos, assinaturas, backups locais (14 dias) |
| Rede | IP público fixo; entrada 80/443; saída 443 liberada (Docker Hub, MCR, NuGet, npm, `database.clamav.net`, Let's Encrypt, Graph API da Meta, API do provedor de IA) | latência baixa até a Meta/IA |
| Backup externo | outro disco/servidor | o disco da VM **não é** backup |
| Snapshot da VM | **recomendado** antes de cada atualização (complementa, **não substitui**, o backup cifrado do ia.bimeta) | |

Esses números são ponto de partida; a homologação mede o uso real (Checklist C, etapa de desempenho).

## 2. Preparação e atualização do Ubuntu

```bash
sudo apt-get update && sudo apt-get -y full-upgrade
sudo timedatectl set-timezone <FUSO>            # por exemplo America/Sao_Paulo (opcional; o NTP é ligado pelo script do passo 4)
sudo reboot                                     # se o kernel foi atualizado
```

## 3. Usuários, SSH e acesso administrativo
1. Crie um usuário administrador nominal (`<USUARIO_ADMIN>`) com `sudo`; **não** use `root` por SSH.
2. Autentique **somente por chave**: em `/etc/ssh/sshd_config.d/10-iabimeta.conf` defina `PasswordAuthentication no` e `PermitRootLogin no`; valide com `sudo sshd -t` e recarregue com `sudo systemctl reload ssh`. **Teste um segundo acesso SSH antes de fechar a sessão atual.**
3. Os scripts do ia.bimeta rodam como **root** (`sudo ./scripts/...`); use `sudo -i` ou prefixe com `sudo`.

## 4. Firewall e instalação do Docker (feitos por script do pacote)

O script `deploy/scripts/instalar-host.sh` (rode como root, **uma vez**) instala o **Docker Engine e o plugin Compose pelo repositório oficial da Docker**, instala `ca-certificates curl gnupg rsync ufw`, liga o NTP, configura o `ufw` (`deny incoming`; `allow outgoing`; **22 só de `<REDE_ADMIN_CIDR>`**; 80 e 443 públicos) e cria `/var/backups/iabimeta` (modo 0700). **Não sobe nada.** Ele será chamado no §11 depois que o código estiver em `/opt/iabimeta`.

Nota de segurança do Docker: o Docker publica portas **por fora** do `ufw`. Por isso, na pilha, **somente o contêiner `web` publica portas (80 e 443)**; banco, ClamAV, Api e Worker **não** têm porta publicada. **Não** adicione `ports:` a outros serviços.

## 5. Estrutura de diretórios e volumes

| Local | Uso |
|---|---|
| `/opt/iabimeta` | código (pacote descompactado); pasta de trabalho `deploy/` |
| `/opt/iabimeta/deploy/.env`, `app.env`, `backup.env` | segredos (600, gerados no passo 12; **nunca** versionados) |
| `/var/backups/iabimeta` | backups cifrados e `alertas.log` (0700) |
| Volumes Docker nomeados | `iabimeta_pgdata` (banco), `iabimeta_anexos` (arquivos cifrados), `iabimeta_clamdb`, `iabimeta_caddy_data`, `iabimeta_caddy_config` (e `iabimeta_ollamadata` se usar IA local) |

Os dados vivem **nos volumes** (em `/var/lib/docker/volumes`). Coloque `/var/lib/docker` e `/var/backups` em disco com espaço folgado. Não monte esses volumes em compartilhamentos de rede.

## 6. Redes internas
Definidas no `docker-compose.yml`: `edge` (sub-rede fixa `172.29.89.0/24`, com o `web` em `172.29.89.2` e o `api`) e `back` (interna, sem porta publicada: `db`, `clamav`, `api`, `worker`). **Se `172.29.89.0/24` colidir com a sua rede**, defina `EDGE_SUBNET` e `EDGE_WEB_IP` em `.env` (o `gerar-segredos.sh` já copia os padrões). O Api só confia nos cabeçalhos `X-Forwarded-*` vindos da sub-rede `edge`.

## 7. PostgreSQL
Não se instala no host: roda no contêiner `db` (imagem `pgvector/pgvector:pg17` = PostgreSQL 17 oficial + extensão pgvector), rede `back`, **sem porta publicada**, volume `pgdata`, `--data-checksums`. Na **primeira inicialização do volume**, `deploy/postgres/init/01-app-role.sh` cria o papel `iabimeta` (**sem** superusuário, sem criar banco/papel) como dono do banco `iabimeta`, e a extensão `vector` quando a imagem a traz. O superusuário `postgres` é usado apenas por backup e manutenção. Duas senhas distintas (`POSTGRES_PASSWORD`, `APP_DB_PASSWORD`) são geradas no passo 12. O pool de conexões da aplicação é fixado em 30 por processo (Api e Worker: 60 no total) para ficar abaixo do `max_connections` de 100.

## 8. ClamAV
Roda no contêiner `clamav` (`clamav/clamav:1.4_base-debian13-slim`), rede `back`, porta 3310 **interna**, volume `clamdb` para as assinaturas. A primeira subida baixa as assinaturas (pode levar vários minutos; `start_period` 600 s). Enquanto não estiver saudável, os arquivos de mídia recebidos ficam **pendentes** (a aplicação exige o verificador). A prova real é `./scripts/teste-clamav.sh` (benigno, benigno de 20 MB e a amostra EICAR, inofensiva).

## 9. Caddy, DNS e HTTPS
O contêiner `web` (Caddy) serve a interface e faz proxy para o Api (rotas em Documento A §5.1). Obtém e **renova sozinho** o certificado Let's Encrypt para `<DOMINIO>`; o estado fica no volume `caddy_data` (**não apague**).
1. Confirme o DNS **de fora da sua rede**: `dig +short <DOMINIO>` deve retornar `<IP_PUBLICO>`.
2. Confirme as portas **de fora** (por exemplo, de um celular em 4G): `curl -I http://<DOMINIO>` e `curl -I https://<DOMINIO>` devem alcançar a VM.
3. **Primeiro com o certificado de homologação do Let's Encrypt** (não conta nos limites do ambiente real): em `deploy/.env` defina `ACME_CA=https://acme-staging-v02.api.letsencrypt.org/directory`. O navegador mostrará o certificado como **não confiável** (esperado). Quando tudo estiver correto, troque para `ACME_CA=https://acme-v02.api.letsencrypt.org/directory` e rode `sudo docker compose --project-directory /opt/iabimeta/deploy up -d web`.
4. A aplicação **não** terá `Content-Security-Policy`/`frame-ancestors` (lacuna L1 do Documento A): **não** use a interface em iframe de sistema externo real antes de ela ser tratada pela equipe de desenvolvimento.

## 10. Preparação dos artefatos da aplicação
O código **não** é baixado de um repositório pela VM: a equipe de desenvolvimento entrega um pacote gerado do commit aprovado (`git archive`), com o hash do commit no nome e um `.sha256`.
```bash
sha256sum -c iabimeta-<HASH>.tar.gz.sha256               # deve imprimir OK
sudo mkdir -p /opt && sudo tar -xzf iabimeta-<HASH>.tar.gz -C /opt   # o pacote é gerado com o prefixo "iabimeta/", resultando em /opt/iabimeta/
ls /opt/iabimeta/deploy/docker-compose.yml
```
As imagens do ia.bimeta (`api`, `worker`, `web`) são **construídas na própria VM** pelo Compose (`docker compose build`, feito pelo `subir.sh`): exige saída para NuGet e npm. Não há registro de imagens privado no pacote.

## 11. Instalar o host (agora que o código está em `/opt/iabimeta`)
```bash
cd /opt/iabimeta/deploy
sudo ./scripts/instalar-host.sh --ssh-de <REDE_ADMIN_CIDR>
# MANTENHA a sessão aberta e teste um SEGUNDO acesso SSH antes de fechar: se <REDE_ADMIN_CIDR> não incluir o seu endereço, você se tranca para fora.
sudo ufw status verbose
```

## 12. Variáveis de ambiente e gestão de credenciais

```bash
cd /opt/iabimeta/deploy
sudo ./scripts/gerar-segredos.sh --dominio <DOMINIO> --email <EMAIL_ACME>
```
Gera `.env`, `app.env` e `backup.env` (permissão 600) com senhas e chaves **aleatórias**, **sem imprimir valor algum** e **sem sobrescrever** arquivo existente. Conteúdo (nomes; os valores são gerados):

| Arquivo | Variáveis principais |
|---|---|
| `.env` | `DOMAIN`, `ACME_EMAIL`, `ACME_CA`, `POSTGRES_PASSWORD`, `APP_DB_PASSWORD`, `IABIMETA_VERSAO`, `CLAMAV_IMAGE`, `EDGE_SUBNET`, `EDGE_WEB_IP` |
| `app.env` | `Channels__Secrets__*`, `AiGateway__Secrets__*`, `Capabilities__Secrets__*`, `Identity__Secrets__*`, `Workflows__Secrets__*`, `Conversations__Attachments__Encryption__*` (cada chave = Base64 de 32 bytes), `Agents__Options__KnowledgeGapHashKey` |
| `backup.env` | `BACKUP_PASSPHRASE`, `BACKUP_DIR`, `BACKUP_KEEP_DAYS`, `BACKUP_EXTERNAL_DEST`, `ALERT_WEBHOOK_URL`, `ALERT_BACKUP_MAX_AGE_HOURS`, `ALERT_DISK_PERCENT` |

**Gestão de credenciais (obrigatório):**
1. Logo após gerar, **copie `.env`, `app.env` e `backup.env` para o cofre fora da VM** (gerenciador de senhas ou cofre corporativo). Perder a chave dos anexos = perder os arquivos cifrados; perder a das credenciais dos canais = perder o token e os segredos da Meta cadastrados; perder a `BACKUP_PASSPHRASE` = nenhum backup restaurável.
2. Permissão 600, dono root; nunca em repositório, chat, e-mail ou ticket.
3. Edite `backup.env` e preencha `BACKUP_EXTERNAL_DEST=<DESTINO_BACKUP>` (e `ALERT_WEBHOOK_URL=<URL_ALERTA>`, se houver). Formatos: `usuario@servidor:/pasta` (rsync por SSH; a **chave SSH do root da VM** deve estar autorizada no servidor, sem senha) ou `/mnt/destino` (outro disco, já montado).
4. Rotação de chave: faça um backup, crie a nova chave (por exemplo `an2`) em `app.env`, aponte o `...ActiveKeyId` para ela e **mantenha a antiga na lista** (as antigas só decifram; não há re-cifragem automática); depois `sudo docker compose --project-directory /opt/iabimeta/deploy up -d api worker`. Mudou `app.env`? **recrie** os serviços; `restart` não relê o arquivo.
5. As chaves da **Meta** e do **provedor de IA** **não** vão em arquivo `.env`: são cadastradas pela aplicação (cifradas no banco) durante a homologação funcional (Documento C).

## 13. Conferir, subir (aplicando as migrations) e validar

```bash
cd /opt/iabimeta/deploy
# Para a 1ª subida, use o certificado de homologação do Let's Encrypt (ACME_CA em .env, §9).
sudo ./scripts/preflight.sh      # confere arquivos, permissões, formato das chaves, recursos, DNS e o compose; sai 1 se houver ERRO
sudo ./scripts/subir.sh          # baixa/constroi imagens, sobe db e clamav, APLICA AS MIGRATIONS, sobe o resto e espera a saúde
sudo ./scripts/status.sh         # contêineres, estado da aplicação, disco, idade do backup (saída 0 ok / 1 atenção / 2 crítico)
sudo ./scripts/teste-clamav.sh   # prova do ClamAV real
```
Notas: a primeira subida demora (construção + assinaturas do ClamAV). **Banco novo:** o `subir.sh` aplica as migrations; para **banco com dados** use sempre `atualizar.sh` (faz backup antes, §20). **Migrations:** `sudo ./scripts/cli.sh migrate --list` mostra o estado; a E2 do produto adicionou três migrations no módulo de conversas (`ContactLinksAndConsents`, `OptOutReviews`, `MessageOptOutCommandMarker`). **Não** execute reversões (`Down`): a de consentimento apaga dados.

**Teste de aceitação (VM de TESTE, antes de qualquer atendimento real):**
```bash
sudo ./scripts/aceitacao.sh --confirmo-que-e-teste
```
É destrutivo para a disponibilidade (derruba contêineres de propósito). Prova, com `PASSOU`/`FALHOU` por item: preflight e `compose config`; `Caddyfile` válido; pilha saudável e migrations em dia; **só 80 e 443 publicadas**; HTTPS responde `/api/health`, serve a interface e **não** expõe `/simulator`, webhook sem assinatura dá 4xx (nunca 5xx); ClamAV real; persistência após `down`/`up`; reinício automático após `docker kill` de `worker`, `api` e `db`; relatório de anexos acusa ClamAV fora do ar; backup criado/cifrado/verificado, **backup adulterado reprovado** e ensaio de restauração; **nenhum segredo nos logs**. **Se algum item falhar, não avance.**

## 14. Integração com a Meta, webhooks e IA (somente requisitos de infraestrutura)

O que o especialista de infraestrutura garante (a configuração funcional é da equipe de desenvolvimento, Documento D):
1. **URL pública do webhook**: `https://<DOMINIO>/webhooks/whatsapp/<id-opaco>` com **certificado válido** (a Meta não aceita autoassinado/staging). O `<id-opaco>` é gerado pela aplicação ao cadastrar a conta (passo da equipe de desenvolvimento).
2. A aplicação valida a assinatura `X-Hub-Signature-256` (HMAC-SHA256 com o App Secret da Meta) sobre os **bytes brutos**; assinatura inválida = 401; identificador desconhecido = 404. O Caddy **não** altera o corpo e limita a 2 MB. **Não** coloque WAF/proxy que modifique corpo ou cabeçalhos entre a Meta e o Caddy.
3. Saída HTTPS 443 da VM para a **Graph API da Meta** (padrão v26.0) e para a **API do provedor de IA** (adaptador implementado: OpenAI, `https://api.openai.com/v1/`).
4. Credenciais (token do usuário do sistema da Meta, App Secret, verify token, chave do provedor de IA) são cadastradas pela aplicação e ficam **cifradas no banco** com as chaves mestras do §12. **Nunca** as receba por canal inseguro nem as grave em arquivo do servidor.
5. **Controle de custos da IA:** limites por empresa configuráveis na aplicação (padrão do catálogo em `backend/host/IaBimeta.Api/appsettings.json`: até 2.000 chamadas e 5.000.000 tokens por período de 86.400 s, 4 chamadas simultâneas, 1.000 tokens de saída por chamada); o consumo é registrado em ledger. Defina também **limite de gasto no painel do provedor de IA**.
6. **Embedded** (sistema externo da empresa, opcional): o **servidor** do sistema externo chama `https://<DOMINIO>/api/embed/v1/...` com cabeçalhos de assinatura HMAC (a assinatura cobre o caminho **sem** o prefixo `/api`; ver orientação funcional ao desenvolvimento); o navegador do usuário carrega a interface por iframe. Para a infraestrutura: relógio sincronizado (janela de 2 min), ausência de intermediário que remova cabeçalhos `X-IaB-*`, e a lacuna L1 (CSP/frame-ancestors) tratada antes de uso real.

## 15. Empresa (tenant) de homologação
A criação da empresa de homologação (administrador, organização, fila, agente, IA e conta de WhatsApp) é **funcional** e feita com a equipe de desenvolvimento: o script `./scripts/empresa.sh <empresa.json> [<empresa.env>] [--aplicar]` **só valida por padrão** (nada é gravado sem `--aplicar`). O JSON **não leva segredo**: os campos terminados em `Env` são **nomes** de variáveis cujos valores ficam num arquivo `.env` (600) apenas da empresa. O especialista executa o script quando a equipe entregar o JSON.

## 16. Banco exclusivo de homologação
O banco `iabimeta` é criado vazio na primeira inicialização do volume (§7) numa VM **dedicada à homologação**: **nenhum dado de produção ou de desenvolvimento entra** nela. A mesma estrutura será usada em produção, com **outra VM, outro domínio, outros segredos e outro banco** (§22). Use apenas dados sintéticos/de teste até a decisão do proprietário.

## 17. Backup e teste de restauração (obrigatórios antes de qualquer dado de teste importante)
```bash
sudo ./scripts/backup.sh                 # banco (pg_dump -Fc) + anexos num único .tar.gpg (AES-256 por frase), verificado por completo, cópia externa
sudo ./scripts/verificar-backup.sh <arquivo.tar.gpg>
sudo ./scripts/ensaiar-restauracao.sh    # restaura o backup mais recente num projeto Compose DESCARTÁVEL (iabimeta-ensaio) e confere os anexos
sudo ./scripts/agendar.sh                # instala timers systemd: backup diário 03:30 e monitor a cada 5 min (rode DEPOIS de um backup manual funcionar)
```
- Cada backup gera `BACKUP_DIR/iabimeta-<data>.tar.gpg` + `.sha256`; mantém `BACKUP_KEEP_DAYS` dias (sempre os 2 mais recentes) e **copia para `BACKUP_EXTERNAL_DEST`**.
- **Aceite:** um backup **real** restaurado no ensaio, com a **cópia externa** conferida (abrir o arquivo no destino e verificar o `.sha256`), e o tempo da restauração **anotado**.
- **Desastre (VM nova):** instale o host (§11), leve o código, `.env`, `app.env` e `backup.env` (**as mesmas chaves e a mesma frase**) e o `.tar.gpg` com o `.sha256` ao lado. **Não rode `subir.sh` antes:** o `restaurar.sh` só restaura em volumes **novos** (recusa se o volume do banco já existe, se o de anexos não está vazio ou se há api/worker/web no ar). Construa as imagens (`sudo docker compose --project-directory /opt/iabimeta/deploy build`), rode `sudo ./scripts/restaurar.sh <arquivo.tar.gpg>` e depois `sudo ./scripts/cli.sh migrate` (se o código for mais novo que o backup) e `sudo ./scripts/subir.sh`. O dump em claro passa por uma pasta temporária (`TMPDIR`, padrão `/tmp`): aponte `TMPDIR` para um disco folgado se preciso.

## 18. Logs, health checks e monitoramento
```bash
sudo docker compose --project-directory /opt/iabimeta/deploy ps
sudo docker compose --project-directory /opt/iabimeta/deploy logs -f --tail 200 api       # ou worker, web, db, clamav
curl -sS https://<DOMINIO>/api/health                                                       # {"status":"ok"} (raso: só prova o processo)
sudo ./scripts/status.sh [--tenant <slug>]                                                  # visão completa (0/1/2)
```
- Logs dos contêineres: `json-file` com rotação (10 MB × 5). O registro de acesso do Caddy **não** inclui cabeçalhos nem corpo. A aplicação **não** deve registrar telefone, mensagem, evidência ou segredo (o teste de aceitação procura segredos nos logs).
- Alertas: `BACKUP_DIR/alertas.log` e, se configurado, `ALERT_WEBHOOK_URL`. O `monitor.sh` (timer de 5 min) confere contêineres, `status` da aplicação, disco, idade do backup e HTTPS local, reinicia o Worker **travado** e não repete o mesmo alerta (só quando muda ou após 6 h).
- O `status` inclui a sonda **`consentimento`**: quantidade e idade da pendência mais antiga de "possível saída" de clientes; alerta (aviso) acima de 24 h corridas. É um alerta **operacional provisório**.

## 19. Testes de conectividade, autenticação e reinício
1. **Conectividade externa:** `curl -I https://<DOMINIO>/` (200 e interface), `curl -sS https://<DOMINIO>/api/health`.
2. **Só 80/443 expostas** (de fora da rede): `nmap -Pn -p 22,80,443,3310,5432,8080 <IP_PUBLICO>`: **apenas 80 e 443 abertas** (22 só da rede de administração).
3. **Origem real do cliente chega ao Api:** `sudo docker compose --project-directory /opt/iabimeta/deploy logs web | grep remote_ip` deve mostrar o **IP público do cliente**, não o do Docker nem do roteador. Se mostrar o do roteador/Docker, o NAT está mascarando a origem (o limite de login por origem passa a valer por um único endereço): ajustar o NAT.
4. **Autenticação:** o login Standalone (empresa + e-mail + senha) e o Embedded dependem da empresa de homologação criada no §15; verifique 401 com senha errada e 200 com a correta, e que **trocar/redefinir senha** funciona. Sessão e Embedded: Checklist C (etapa H3).
5. **Reinício e queda:** `sudo reboot` → depois `sudo ./scripts/status.sh`: a pilha **volta sozinha** (`restart: unless-stopped`, Docker habilitado no boot). O `aceitacao.sh` já derruba `worker`, `api` e `db` e confere a volta.

## 20. Atualização e reversão
- **Atualizar:** leve o pacote novo (com `.sha256`), substitua o código em `/opt/iabimeta` **preservando** `deploy/.env`, `deploy/app.env`, `deploy/backup.env`, e rode `sudo ./scripts/atualizar.sh`: **faz backup antes (aborta se falhar)**, constrói as imagens novas, **lista e aplica as migrations** e recria só o que mudou; registra a versão anterior em `deploy/.versao-anterior`. `--sem-backup` só em ambiente de teste. Faça um **snapshot da VM** antes.
- **Reverter:** reverter só a imagem **pode não ser compatível** com o esquema novo do banco. A volta segura, se algo der errado **depois** das migrations, é **restaurar o backup feito pela própria atualização** (§17) com o código da versão anterior. **Nunca** execute `Down` de migration em ambiente com dados.
- **Mudou `app.env`:** `sudo docker compose --project-directory /opt/iabimeta/deploy up -d --no-deps --force-recreate api worker`.

## 21. Recuperação de falhas

| Sintoma | Causa provável | Ação |
|---|---|---|
| `preflight` reprova chave | formato incorreto (Base64 de 32 bytes) ou `ActiveKeyId` sem a chave | corrija em `app.env` (`head -c 32 /dev/urandom \| base64`); não reutilize chave de outro módulo |
| Certificado não sai (log do `web`) | DNS errado, 80/443 bloqueadas, limite do Let's Encrypt | confira DNS e portas de fora; use `ACME_CA` de homologação |
| Arquivos recebidos ficam "pendentes" | ClamAV baixando assinaturas/fora do ar | `status.sh`; `logs clamav`; resolvem sozinhos quando saudável |
| `api` não fica saudável | migration pendente, banco fora, chave obrigatória ausente | `logs api`; `./scripts/cli.sh migrate --list` |
| 429 no login para todos | proxy não confiável | confira `EDGE_SUBNET` e `ForwardedHeaders__KnownNetworks__0` (sub-rede real da rede `edge`) |
| Conflito de sub-rede ao subir | `172.29.89.0/24` colide | mude `EDGE_SUBNET` e `EDGE_WEB_IP` |
| Disco cheio | logs, backups locais, imagens | `docker system df`; ajuste `BACKUP_KEEP_DAYS`; **não** apague volumes |
| Worker parado/travado | processo caiu | o `monitor.sh` reinicia o travado; manual: `docker compose restart worker` (a retomada é segura: entrega pelo menos uma vez, idempotente) |
| Perda da VM | — | restauração em VM nova (§17, "Desastre") |

## 22. Segurança, atualização do servidor e separação de ambientes (orientações obrigatórias)
- **Isolamento do banco:** sem porta publicada, só na rede `back`; papel da aplicação sem superusuário; superusuário só para backup/manutenção; **nunca** exponha a 5432.
- **HTTPS:** obrigatório; HSTS ligado; certificados renovados pelo Caddy; nada de HTTP para dados.
- **Acesso administrativo:** SSH por chave, `ufw` restringindo a 22 à rede de administração, usuários nominais com `sudo`, `root` sem SSH. Recomendado adicionar `fail2ban` e MFA no acesso à VM/virtualizador (**não vêm no pacote**).
- **Segredos:** arquivos 600 fora do repositório; cópia em cofre fora da VM; nada em logs (o aceite verifica); chaves de Meta/IA cifradas no banco.
- **Persistência e backup:** volumes nomeados em disco confiável; backup cifrado diário com **cópia externa** e ensaio de restauração periódico.
- **Atualização do servidor:** aplique correções do Ubuntu e do Docker em janela combinada, **depois de backup/snapshot**; recomenda-se habilitar `unattended-upgrades` apenas para segurança (**não vem no pacote**); fixe versões de imagem após a 1ª validação (lacuna L4).
- **Separação homologação × produção:** VM, domínio, banco, chaves e contas Meta/IA **distintos**; nenhum dado nem segredo é copiado de um para o outro; a mesma versão de código, outros arquivos `.env`.
- **Multiempresa, consentimento, revogação e auditoria (já implementados no produto):** os dados de cada empresa são isolados por aplicação (nunca por infraestrutura); portanto **não** dê acesso direto ao banco a quem não deva ver dados de empresas. O consentimento de contato é **registro de referência**: somente acréscimo (o banco recusa UPDATE/DELETE), **sem prazo e sem expurgo** até decisão jurídica: **não** crie rotina de apagamento nem trunque essas tabelas (`conversations.contact_consents`, `conversations.optout_reviews`). A auditoria é gravada no banco e **entregue pelo Worker**: **o Worker precisa estar no ar** para a trilha ficar em dia (pendências em `identity.audit_outbox` e na outbox do Conversations). O backup cifrado **contém dados pessoais**: trate-o como dado sensível.
