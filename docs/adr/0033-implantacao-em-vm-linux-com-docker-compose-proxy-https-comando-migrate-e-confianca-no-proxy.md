# ADR 0033 — Implantação do piloto em uma VM Linux com Docker Compose: proxy HTTPS (Caddy), comando `migrate`, confiança no proxy e operação em Bash

- **Status:** **Aceito para o MVP, com a ressalva de que a implantação Docker Compose ainda precisa ser homologada em Linux real** (ratificado pelo decisor em 10/11/2026, com os commits `cef8ced`, `371ffb3`, `e326ad9` e `b457c23`). **Infraestrutura decidida:** a VM Ubuntu Server 24.04 LTS em servidor físico da empresa (IP público fixo) é **exclusivamente de homologação e testes**; a **produção futura será em nuvem, com os mesmos artefatos de implantação**. Não se instala Docker, WSL nem ClamAV no Windows de desenvolvimento: a validação é feita diretamente na VM (implementado em 10/11/2026 sob a autorização do decisor, depois da ratificação do ADR 0032). **Nada foi executado com Docker** (a máquina de desenvolvimento não tem Docker, WSL nem virtualização habilitada): a implantação foi conferida por texto, por compilação e por testes automatizados do que roda aqui; a prova de comportamento é o teste de aceitação na VM. Nenhuma chamada real à Meta ou à OpenAI; nenhuma alteração em produção; `main` intocada; sem push.
- **Data:** 10/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md`, 0006 (Receiver, Inbox), 0011 (webhook assinado), 0025 e 0026 (retenção, backup e operação), 0032 (segurança dos arquivos), `docs/operations/IMPLANTACAO-LINUX.md`, `PILOTO-PRIMEIRA-EMPRESA.md`
- **Preserva:** nenhuma aresta nova na matriz de dependências (nem edição de `BOUNDARIES.md`, `ARCHITECTURE.md` ou `module-dependencies.json`); o monólito modular (Api e Worker continuam os dois hosts); a validação de assinatura do webhook no Api; tenant e permissões só pelo `RequestContext`; segredos fora do repositório, de log e de qualquer resposta.

## Contexto

O proprietário decidiu (10/11/2026) que o primeiro piloto roda em **uma máquina virtual Linux própria**, com IP público fixo e possibilidade de direcionar o tráfego externo (inclusive os webhooks da Meta) para ela, sem contratar VPS. O pedido: implantação **reproduzível, segura e simples** (Ubuntu Server 24.04 LTS e Docker Compose, sem Kubernetes), com HTTPS e renovação automática, volumes persistentes, reinício automático, verificações de saúde, ClamAV real, backup externo cifrado e restauração, e os scripts de operação em Linux. Achados do código que moldaram a decisão: o Api **não serve a interface React** (só o proxy do Vite o faz em desenvolvimento); o Worker não tem porta (a saúde dele se vê pelo banco); em produção **ninguém aplica as migrations** (só o ambiente de desenvolvimento e o `provision` o faziam); o limite de login usa o endereço da conexão e, atrás de um proxy, todos os usuários dividiriam o endereço dele; as ferramentas de operação existentes são de Windows.

**Fora do escopo (decidido):** Kubernetes ou qualquer orquestrador; alta disponibilidade e mais de uma VM (a pasta de arquivos é uma só, compartilhada por Api e Worker); registro de imagens privado e CI/CD (a construção é feita na própria VM); painel administrativo; métricas exportadas; segundo verificador; reescrever as ferramentas de operação do Windows (continuam servindo para rodar do PC do operador contra a API).

## Decisão

### 1. Uma pilha de Compose em um único host

`deploy/docker-compose.yml` (projeto `iabimeta`): **`web`** (Caddy 2 com a interface compilada), **`api`**, **`worker`** (a mesma base `mcr.microsoft.com/dotnet/aspnet:10.0-noble`, usuário não root, uma imagem por host), **`db`** (`postgres:17`, com `--data-checksums`) e **`clamav`** (`clamav/clamav:1.4_base-debian13-slim`, LTS 1.4, assinaturas num volume).

- **Só o `web` publica portas, 80 e 443.** Duas redes: `edge` (web e api; sub-rede **fixa** `172.29.89.0/24`) e `back` (db, clamav, api, worker). O banco e o verificador **nunca** ficam na rede do proxy. Um teste do repositório confere isto por texto (a regra é fácil de quebrar numa edição).
- Volumes **nomeados**: `pgdata`, `anexos`, `clamdb`, `caddy_data` (certificados: não apagar) e `caddy_config`.
- `restart: unless-stopped` em todos; **saúde** do PostgreSQL (`pg_isready`), do Api (`/health` por TCP, sem instalar `curl`) e do ClamAV (a verificação da própria imagem); `web` espera o Api e o Api espera o banco. O Api e o Worker **não esperam o ClamAV** (ele demora a baixar as assinaturas na primeira vez, e o desenho falha fechado: o arquivo recebido fica pendente e se resolve sozinho). A saúde do Worker é a do comando `status` (conexões do banco com atividade), que o monitor usa; o monitor reinicia o Worker travado (a retomada é segura).
- Logs `json-file` com rotação (10 MB × 5). Nada de segredo em imagem, em `docker-compose.yml` ou em argumento de linha de comando.
- **Segredos em três arquivos fora do repositório, permissão 600**, gerados pelo `gerar-segredos.sh` a partir de modelos *sem valores*: `.env` (variáveis do Compose: domínio, senhas do banco), `app.env` (só o Api e o Worker: chaves das credenciais dos canais, da IA e dos anexos, e a do Knowledge Engine) e `backup.env` (a frase do backup e os alertas: **nenhum contêiner o vê**). O papel do banco da aplicação **não é superusuário** (criado na primeira inicialização; o superusuário fica só para o backup).
- Produção **exige** a verificação de arquivos e a cifra (`Scanner:Required` e `Encryption:Required`, ADR 0032): sem chave ou sem `clamd`, o Api e o Worker não sobem ou não liberam arquivo.

### 2. Proxy HTTPS e o webhook da Meta

O Caddy obtém e **renova sozinho** o certificado do Let's Encrypt (precisa de DNS correto e das portas 80 e 443; o estado fica em `caddy_data`; a CA de **homologação** do Let's Encrypt serve para testar sem esgotar o limite do ambiente real). Rotas públicas: `/` (interface, com retorno à página inicial para as telas do React), `/api/*` (sem o prefixo; teto de 30 MB para o envio de arquivo) e `/webhooks/whatsapp/*` (teto de 2 MB, **corpo passado sem alteração**: o Api valida a assinatura HMAC, ADR 0011). Qualquer rota de desenvolvimento deixa de existir em produção (o Api já recusa ligá-las fora de `Development`). Cabeçalhos de segurança básicos (HSTS, `nosniff`, sem `Server`) e registro de acesso em JSON; admin do Caddy desligado.

### 3. Confiança no proxy (origem do cliente)

Novo **`ForwardedHeaders`** (`ForwardedHeadersSettings`, desligado por padrão): ligado, o Api confia em `X-Forwarded-For` e `X-Forwarded-Proto` **só dos proxies e redes listados** (`KnownProxies`, `KnownNetworks` em CIDR; nunca herda o laço local), e o middleware roda **antes** do guarda do ingresso e do limite de login, que passam a ver o endereço real do cliente. Ligado sem nenhum endereço confiável, **o Api não sobe**; valor inválido também não, sem repetir o valor. O Caddy **ignora** os `X-Forwarded-*` que o cliente mandar e escreve o endereço de origem verdadeiro; a sub-rede `edge` é fixa justamente para o Api confiar só nela. O `PublicIngressGuard` (túnel de desenvolvimento) **não é usado em produção** (`PublicListenerPort` fica sem valor); o Compose não o define e um teste confere.

### 4. Comando `migrate`

Em produção o Api e o Worker **não aplicam migrations ao subir**. Novo comando de operação **`migrate`** (e `migrate --list`, que só lista e sai com 1 se houver pendência): aplica as migrations de **todos os oito módulos** (a lista é a mesma do `provision`, e um teste fixa os nomes), só imprime identificadores de migrations e nunca a string de conexão. A implantação o roda **depois de um backup** (`atualizar.sh` faz o backup antes e aborta se ele falhar). Também: **`attachments test-scanner`**, que fala **direto** com o motor (a amostra EICAR é interceptada pela detecção embutida em qualquer outro caminho, então o `clamd` real nunca a veria): benigno pequeno limpo, benigno do tamanho máximo limpo (prova o `StreamMaxLength`) e EICAR infectada.

### 5. Operação em Bash (Ubuntu 24.04), sem dependência nova

`deploy/scripts/`: `instalar-host.sh` (Docker Engine e Compose pelo repositório **oficial** da Docker, `ufw` com SSH só da rede de administração e 80/443 públicos, NTP, pastas), `gerar-segredos.sh`, `preflight.sh`, `subir.sh` (constrói, sobe banco e ClamAV, **migra**, sobe o resto), `atualizar.sh` (**backup antes**, migrações listadas e aplicadas, versão anterior registrada), `cli.sh` (qualquer comando do Api num contêiner descartável com a mesma configuração), `empresa.sh` (`provision`: **só valida por padrão**, `--aplicar` grava; segredos por nome de variável, nunca em argv), `status.sh`, `monitor.sh` (contêineres, `status`, disco, idade do backup, HTTPS local; alerta com **de-duplicação**; reinicia o Worker travado), `teste-clamav.sh`, `agendar.sh` (timers do `systemd`: backup diário e monitor a cada 5 minutos), `aceitacao.sh` (veja a seção 7). **Backup e restauração** (`backup.sh`, `verificar-backup.sh`, `restaurar.sh`, `ensaiar-restauracao.sh`): um arquivo `.tar.gpg` com o dump do banco, o tar dos anexos (**o dump primeiro**, regra do ADR 0032) e um manifesto de SHA-256, cifrado com `gpg` simétrico AES-256 (a frase por descritor de arquivo, nunca em argv), `.sha256` ao lado, verificação completa logo após a criação, retenção local que nunca apaga os dois últimos, **cópia externa obrigatória no piloto** (rsync por SSH ou pasta montada) e restauração **só em volumes vazios**; o ensaio roda num projeto descartável (`iabimeta-ensaio`, sub-rede própria) e **só derruba esse projeto**. Os scripts do Windows continuam para quem opera do PC.

### 6. O que o host precisa

VM Ubuntu Server 24.04, **8 GB de memória** (o ClamAV pede 3 a 4 GiB; o `clamd` usa ~1,2 GiB e até ~2,4 GiB na recarga das assinaturas), 2 a 4 vCPU, 60 a 100 GB de disco, saída HTTPS para os registros de imagens, NuGet, npm, `database.clamav.net`, Let's Encrypt, Meta e OpenAI; entrada 80 e 443.

### 7. Como se prova (e o que não se provou)

- **Provado aqui:** `dotnet publish` do Api e do Worker (os mesmos comandos dos Dockerfiles; ambos usam o framework ASP.NET) e o build da interface; `bash -n` e as regras de ASCII, LF e modo estrito de todos os scripts; invariantes por texto do Compose, dos Dockerfiles, do Caddyfile, dos modelos e do `systemd` (`ArtefatosDeImplantacaoTests`); `ForwardedHeaders` (limite de login por cliente atrás do proxy confiável, ignorado para quem não é proxy, desligado por padrão, partida recusada com configuração contraditória: `ProxyReversoTests`); `migrate` e `test-scanner` com PostgreSQL real e um `clamd` falso; os scripts de backup, verificação e restauração contra um `docker` falso com `gpg`, `tar` e `sha256sum` reais.
- **Não provado (exige a VM):** a subida real do Compose, as imagens, o Caddy e a emissão do certificado, o `systemd`, a instalação do Docker, o ClamAV real, a preservação do IP de origem atrás do NAT do roteador, o desempenho. O **`aceitacao.sh`** na VM de teste prova tudo isso (exposição de portas, ClamAV real com EICAR e 20 MB, persistência, queda de serviço, backup, adulteração e restauração, ausência de segredo em log). **Não se coloca atendimento real antes de ele passar.**

## Consequências

- Uma instalação **reproduzível** em uma VM com um conjunto pequeno de comandos, e a mesma pilha serve à homologação e ao piloto.
- **Risco aceito:** servidor único (queda da VM é queda do atendimento; a recuperação é a restauração do backup externo numa VM nova) e a pasta de arquivos num só host.
- **Risco aceito:** as imagens são construídas na própria VM (precisa de acesso a NuGet e npm) e as bases (`10.0-noble`, `postgres:17`, `caddy:2`) flutuam dentro da sua versão maior: a reprodutibilidade fina (digests) fica para depois do piloto.
- **Risco aceito:** o Docker publica portas **por fora** do `ufw` (documentado pela Docker); por isso só o proxy publica portas.
- **Risco aceito:** os segredos ficam em arquivos 600 do host (visíveis a quem é root na VM e no `docker inspect`); cofre dedicado fica para depois.
- O primeiro `docker compose up` **baixa as assinaturas do ClamAV** (alguns minutos): até lá os arquivos recebidos ficam pendentes.

## Plano de reversão

Nada do que existe é alterado: `deploy/` é novo, o `migrate` e o `test-scanner` são comandos novos, e o `ForwardedHeaders` é desligado por padrão (sem a seção, o Api se comporta como antes). Desfazer a implantação é `docker compose down` (os volumes ficam) e, se for o caso, remover `deploy/`. A reversão de uma **atualização** com migrations é restaurar o backup feito por ela com a versão anterior do código.

## Testes

`ArtefatosDeImplantacaoTests` (invariantes por texto), `ProxyReversoTests` (6, por HTTP de verdade), `ComandoDeMigracaoTests` (4, PostgreSQL real), mais 2 de `test-scanner` em `ComandoDeAnexosTests`; os scripts de backup e restauração têm o seu teste em `deploy/tests/testar-scripts.sh` (docker falso). Gate completo e frontend: veja `PRONTIDAO-MVP.md`.
