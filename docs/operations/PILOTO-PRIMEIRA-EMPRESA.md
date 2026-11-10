# Preparação do primeiro piloto com uma empresa real (análise de 10/11/2026, atualizada com a decisão de hospedagem)

Análise objetiva do que falta para o ia.bimeta operar com **uma empresa real**, do caminho crítico, do que avança em paralelo e do que depende de ação externa. **Nada aqui presume que a VM, o domínio, as credenciais, os créditos de IA ou as aprovações da Meta já existam.**

**Decisão de infraestrutura (10/11/2026):** o primeiro piloto será instalado **numa máquina virtual Linux na infraestrutura própria**, com **IP público fixo** (o tráfego externo, inclusive os webhooks da Meta, é direcionado para a VM). **Não se contrata VPS.** A implantação (Ubuntu Server 24.04 + Docker Compose) está preparada em `deploy/` e descrita em `IMPLANTACAO-LINUX.md` (ADR 0033).

**Metas internas (diretriz de 10/11/2026), agressivas e condicionadas ao que não depende de nós:** **19/11/2026** (20/11 é feriado nacional) homologação pronta para testes integrados; **a partir de 30/11/2026** início assistido do piloto, se infraestrutura, credenciais e aprovações externas permitirem. A seção 6 avalia a viabilidade e propõe ajustes.

Base desta análise: os diagnósticos, os documentos de prontidão e os testes que já existem, mais a leitura pontual do código de hospedagem e das ferramentas de operação. Não foi feita auditoria geral.

## 1. Estado em uma frase

O produto do MVP está **completo e provado por testes com Api, Worker e PostgreSQL reais e a Meta e a OpenAI simuladas**; a segurança dos arquivos (ADR 0032) e a **implantação em VM Linux (ADR 0033)** estão **escritas e conferidas por texto e compilação, mas a implantação nunca rodou com Docker de verdade** (a máquina de desenvolvimento não tem Docker nem virtualização habilitada). **O que impede o piloto não é código do produto: é a VM com Docker, o domínio e o redirecionamento de portas, as credenciais e as aprovações externas, e o teste de aceitação que prova a implantação.**

## 2. Classificação das pendências

Legenda de **dono**: **P** = proprietário, **E** = empresa piloto, **M** = Meta, **N** = nós (desenvolvimento), **I** = quem opera a infraestrutura.

### P0: bloqueiam o piloto

| # | Pendência | Dono | Estado e o que falta |
|---|---|---|---|
| P0-1 | **VM Linux pronta**, domínio e HTTPS, e a pilha rodando | P e I (VM, DNS, NAT); N (artefatos: **prontos**) | **Artefatos entregues** (`deploy/`, ADR 0033). Falta: criar a VM (Ubuntu 24.04, 8 GB), apontar o **DNS A** para o IP fixo, **redirecionar 80 e 443** até a VM e rodar `instalar-host.sh`, `subir.sh` e **`aceitacao.sh`** (`IMPLANTACAO-LINUX.md`) |
| P0-2 | **WhatsApp real**: conta verificada, número de produção, app da Meta ativo, **token permanente**, App Secret; modelos aprovados | E e P na Meta | **Prazo menos controlável.** Começar já. 1 dia nosso para conectar e provar depois de existir |
| P0-3 | **IA comercial**: chave com **crédito** e a **primeira chamada real validada** no ambiente de homologação | P ou E; N valida | 0,5 dia. A única chamada real feita foi recusada por saldo. O adaptador da Anthropic não existe (ADR 0017) |
| P0-4 | **`clamd` real provado contra o cliente** | N (na VM) | **O ClamAV já está na pilha** (imagem oficial, LTS 1.4). Falta rodar `teste-clamav.sh` na VM (benigno, 20 MB e EICAR) |
| P0-5 | **Segredos do ambiente** guardados fora da máquina | P e I | `gerar-segredos.sh` gera tudo (3 arquivos, 600); **falta guardar uma cópia fora da VM** (cofre) |
| P0-6 | **Backup com cópia externa funcionando** e **ensaio de restauração completo na VM** | I e N | Scripts prontos e testados contra um docker falso; **falta o destino externo** (`BACKUP_EXTERNAL_DEST`) e rodar `ensaiar-restauracao.sh` na VM |
| P0-7 | **Base legal e contrato com a empresa piloto** (LGPD) | P e E | fora do código; dado pessoal real só entra depois |

### P1: necessários para operação assistida

| # | Pendência | Dono | Estado |
|---|---|---|---|
| P1-1 | ~~Confiar nos cabeçalhos de um proxy reverso (limite de login por origem)~~ | N | **Feito (ADR 0033):** `ForwardedHeaders`, só da rede do proxy, desligado por padrão, com testes |
| P1-2 | ~~Ferramentas de operação no sistema escolhido~~ | N | **Feito para Linux:** `deploy/scripts/` (Bash). Os scripts do Windows seguem para rodar do PC do operador contra a API |
| P1-3 | **Destino do alerta** (webhook de chat) e **responsável** por atendê-lo (horário, escalonamento) | P | o monitor e o backup já alertam (arquivo + webhook); falta definir quem recebe |
| P1-4 | **Ensaio de falhas no ambiente real** (Worker, Api, banco, ClamAV, Meta, OpenAI, disco cheio, cota) | N | queda de serviço e ClamAV estão no `aceitacao.sh`; Meta, OpenAI, disco e cota seguem como ensaio |
| P1-5 | **Teste de carga e de permanência** (volume de mensagens, memória da pilha, varredura de arquivo grande): **nada de desempenho foi medido** | N | 1 dia, na VM |
| P1-6 | **Treinamento e roteiro de teste assistido** dos operadores reais; **canal de suporte** | N e E | 1 dia de material |
| P1-7 | **Decisão do prazo de guarda dos arquivos recebidos** (hoje desligado; recomendação técnica 90 dias) | P | decisão |
| P1-8 | **Procedimento de suporte do que não tem tela** (agente, FAQ, usuários, filas) | N | `empresa.sh` e `cli.sh` cobrem; falta o roteiro escrito para o suporte |
| P1-9 | **Conferência dos limites reais da Meta** (vazão, status da subida de mídia, erros) e dos **modelos** aprovados | N com M | junto com P0-2 |
| P1-10 | **Reinício da VM e reconstrução em VM nova** (desastre) ensaiados | I e N | `aceitacao.sh` cobre a queda de serviços; o **reboot** e a **restauração em VM nova** são manuais (`IMPLANTACAO-LINUX.md`, seções 6 e 8) |

### P2: adiáveis (depois do primeiro ciclo)

Painel e API administrativos, métricas exportadas e alertas ativos, tempo real (SSE), re-cifragem de objetos com a chave nova, quarentena com interface, segundo verificador, cota por usuário, adaptador da Anthropic, Knowledge Engine e integrações LabNet e BImeta, voz, outros canais, PITR do PostgreSQL, alta disponibilidade, digests fixos das imagens, cofre dedicado de segredos, CSP no proxy.

## 3. Caminho crítico

A ordem é ditada pelas **esperas externas**, não pelo nosso esforço:

```text
  dia 0 (agora)                                    semana 1                       semana 2                semana 3
P ── abre a Meta (WABA, número, app, token, modelos) ═══════ espera da Meta ═══════► conectar e provar (1 dia)
P ── cria a VM + DNS + NAT 80/443 ──► N+I: instalar-host, subir, aceitacao.sh ──► ensaios (falhas, carga, restauracao) ──► teste assistido ──► piloto
P ── conta de IA com crédito ──► primeira chamada real (0,5 dia)
N ── (já feito) artefatos de implantação ; (em paralelo) roteiros, treinamento, ajustes
```

1. **Agora a 12/11:** o proprietário **cria a VM**, aponta o **DNS** e o **NAT 80/443**, e **abre o processo da Meta** (o que tem prazo incontrolável).
2. **Semana 1:** na VM, `instalar-host.sh`, `gerar-segredos.sh`, `subir.sh` com o certificado de homologação, `aceitacao.sh`, certificado real; **primeira chamada real à IA**; teste da Meta com o **número de teste** pelo procedimento de `HOMOLOGACAO-META.md`, agora por HTTPS de verdade.
3. **Semana 2:** ensaios de falha, carga e restauração (P0-6, P1-4, P1-5, P1-10); se a Meta liberou o número real e os modelos: conectar a empresa piloto e **provisionar** (`empresa.sh`).
4. **Semana 3:** teste assistido com operadores reais (P1-6), depois o piloto.

## 4. O que o proprietário faz agora (e que nós não conseguimos fazer por ele)

1. **Abrir a Meta hoje:** aplicativo da Meta (tipo Empresa), **verificação do negócio** da empresa piloto, **número de produção** na WABA (que não esteja em uso no aplicativo do WhatsApp, ou o procedimento de migração), **usuário do sistema** e token permanente, e **submeter os modelos de mensagem** para fora da janela de 24 horas. Os prazos da Meta não são controlados por nós e não foram estimados. (Para os testes de homologação basta o **número de teste** gratuito do próprio aplicativo.)
2. **Criar a VM** Ubuntu Server 24.04 (**8 GB de memória**, 2 a 4 vCPU, 60 a 100 GB), com um IP fixo na LAN, SSH por chave, e **liberar a saída** HTTPS; **apontar o DNS A** do domínio para o IP público fixo e **redirecionar as portas 80 e 443** do roteador/firewall até a VM (`IMPLANTACAO-LINUX.md`, seções 2 e 5). Confirmar com o provedor que 80 e 443 de entrada não são bloqueadas.
3. **Dar acesso à VM** (ou rodar você mesmo os comandos do guia): **o desenvolvimento não opera servidor remoto sem credencial entregue por você e sem sua autorização.**
4. **Conta da IA com crédito** na OpenAI (da empresa piloto, BYOK) e autorizar a **primeira chamada real** no ambiente de homologação.
5. **Definir a empresa piloto** (quem, número, janela, operadores, horário), o **contrato e a base legal** (P0-7) e o **prazo de guarda dos arquivos recebidos** (P1-7).
6. **Guardar fora da VM** as chaves e a frase do backup (cofre) e definir o **destino externo** dos backups.
7. **Opcional, para antecipar a prova do ClamAV real e da pilha antes de a VM existir:** habilitar a **virtualização por hardware na BIOS** desta máquina de desenvolvimento e autorizar a instalação do **WSL2 com Ubuntu 24.04 e do Docker Engine** (`IMPLANTACAO-LINUX.md`), ou **autorizar baixar um ClamAV portátil para Windows** só para provar o cliente contra um daemon real. Sem isso, a prova de tudo é feita na própria VM de teste.

## 5. Dependências externas

| Item | Quem fornece |
|---|---|
| Conta WhatsApp Business (WABA), número, aplicativo da Meta, token permanente, App Secret, verificação do negócio, modelos aprovados | a empresa e você, na Meta |
| **VM Linux**, IP público fixo, **domínio e DNS**, **redirecionamento 80/443** no roteador/firewall, saída HTTPS | você e a infraestrutura |
| Créditos e chave do provedor de IA (OpenAI) | você ou a empresa |
| Destino externo dos backups, cofre das chaves, destino dos alertas | você e a infraestrutura |
| Contrato e base legal; decisão do prazo de guarda | você e a empresa |
| Operadores reais para o teste assistido | a empresa |

## 6. Viabilidade das metas (avaliação nossa, fundamentada, sem promessa)

| Meta | O que já está feito | Depende de terceiros | Avaliação |
|---|---|---|---|
| **19/11: homologação pronta para testes integrados** | artefatos de implantação, `migrate`, confiança no proxy, scripts de operação e backup, guia e teste de aceitação | **a VM, o DNS e o NAT até ~13/11**; conta de IA; o **número de teste** da Meta | **Viável se a VM existir até 13/11.** Depois de a VM existir, o nosso trabalho é: instalar, `aceitacao.sh`, corrigir o que ela apontar (a implantação **ainda não rodou**: espere ajustes), primeira chamada de IA e teste com o número de teste. Restam 7 dias úteis desde amanhã (20/11 é feriado) |
| **30/11: início assistido do piloto** | tudo o que o produto precisa (MVP, segurança dos arquivos, implantação) | **verificação do negócio, número de produção e modelos da Meta (prazo desconhecido)**, contrato e base legal, operadores | **Possível apenas no melhor caso.** O caminho crítico real é a Meta. Se o processo for aberto até 12/11 e andar sem retrabalho, 30/11 é plausível; **se qualquer etapa pedir correção, passa de 30/11** |

**Ajustes propostos:** (1) tratar **19/11** como a meta real de homologação; (2) tratar **30/11 como ponto de decisão** (go ou no-go) e não como compromisso: **início assistido com grupo reduzido** entre 30/11 e 11/12, conforme a Meta; (3) fazer na homologação o **teste integrado com o número de teste da Meta**, para que a única novidade do piloto seja o número real e os modelos aprovados; (4) **12/11 como data de corte** das decisões e ações do proprietário (VM e DNS, Meta aberta, conta de IA).

## 7. O que esta entrega já deixou pronto

- **Segurança dos arquivos** (ADR 0032): verificação exigida, cifra, cota, prazo de guarda auditável, conferência banco × pasta, backup da pasta.
- **Implantação em VM Linux** (ADR 0033): Compose com proxy HTTPS e interface, Api, Worker, PostgreSQL 17 e ClamAV real; segredos fora do repositório; `migrate`; confiança no proxy; scripts de instalação, atualização, backup cifrado com cópia externa, restauração, monitor e alertas; guia passo a passo e checklist de rede e Meta (`IMPLANTACAO-LINUX.md`); **teste de aceitação** para a VM de teste (`aceitacao.sh`).
- **Não provado ainda:** a implantação com Docker de verdade (a máquina de desenvolvimento não tem Docker nem virtualização habilitada) e o ClamAV real. Isso é o primeiro trabalho na VM.
