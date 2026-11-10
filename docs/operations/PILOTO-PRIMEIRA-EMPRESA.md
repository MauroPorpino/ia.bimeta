# Preparação do primeiro piloto com uma empresa real (análise de 10/11/2026)

Análise objetiva do que falta para o ia.bimeta operar com **uma empresa real**, do caminho crítico, do que avança em paralelo e do que depende de ação externa. **Nada aqui presume que hospedagem, credenciais, créditos de IA ou aprovações da Meta já existam.**

**Metas de planejamento (diretriz de 10/11/2026), agressivas e condicionadas ao que não depende de nós:** 20/11/2026: MVP operacional em **ambiente de homologação**, pronto para testes integrados. 30/11/2026: início desejado do **primeiro piloto**, condicionado a infraestrutura, credenciais e homologação das integrações externas. A seção 7 avalia a viabilidade de cada uma e propõe ajustes.

Base desta análise: os diagnósticos, os documentos de prontidão e os testes que já existem (`PRONTIDAO-MVP.md`, `HOMOLOGACAO-META.md`, `WHATSAPP-CONEXAO-REAL.md`, `OPERACAO-E-SUPERVISAO.md`, `NOVA-EMPRESA.md`) mais a leitura pontual do código de hospedagem e das ferramentas de operação. Não foi feita auditoria geral.

## 1. Estado em uma frase

O produto do MVP (WhatsApp → IA → atendente, fila, modelos de mensagem, mídia nos dois sentidos, FAQ, provisionamento de empresa, retenção, backup, supervisão) está **completo e provado por testes com Api, Worker e PostgreSQL reais e a Meta e a OpenAI simuladas**. Com o ADR 0032 as lacunas de **segurança dos arquivos** (antivírus, cifra, cota, backup da pasta) deixaram de ser bloqueio de código. **O que impede o piloto não é código do produto: é hospedagem, credenciais e aprovações externas, e alguns ajustes de implantação** que só fazem sentido depois da decisão de hospedagem.

## 2. Classificação das pendências

Legenda de **dono**: **P** = proprietário, **E** = empresa piloto, **M** = Meta, **N** = nós (desenvolvimento), **I** = quem opera a infraestrutura.

### P0: bloqueiam o piloto

| # | Pendência | Dono | Estimativa (nossa parte) | Observação |
|---|---|---|---|---|
| P0-1 | **Hospedagem** (servidor, domínio, certificado HTTPS) e **execução permanente** do Api e do Worker, com reinício automático e logs persistentes | P decide e contrata; N entrega os artefatos; I executa | 2 a 3 dias depois da decisão | Hoje **não existe nenhum artefato de implantação** (seção 4). Sem endereço HTTPS público a Meta não entrega mensagens |
| P0-2 | **WhatsApp real**: conta WhatsApp Business verificada, número de produção, aplicativo da Meta em modo ativo, usuário do sistema com **token permanente**, App Secret; modelos de mensagem aprovados | E e P na Meta | 1 dia nosso (conectar e provar) depois de existir | **É o item de prazo menos controlável.** Começar já (seção 6) |
| P0-3 | **IA comercial**: chave do provedor (BYOK da empresa) com **crédito** e a **primeira chamada real validada** no ambiente de homologação | P ou E; N valida | 0,5 dia | O adaptador da OpenAI está pronto e provado contra simulação; a única chamada real feita foi recusada por saldo. O adaptador da Anthropic não existe (decisão do ADR 0017) |
| P0-4 | **`clamd` real** instalado e atualizado no servidor, e o cliente do ia.bimeta provado contra ele | I instala; N prova | 0,5 dia | Hoje provado só contra um daemon falso que fala o protocolo (ADR 0032). Com a verificação **exigida**, sem `clamd` nenhum arquivo é liberado |
| P0-5 | **Segredos do ambiente**: chave mestra dos canais, chave da IA, **chave dos anexos**, frase de cifra do backup; geração, cofre fora da máquina e entrega por variável de ambiente | P e I | 0,5 dia (roteiro) | Perder a chave dos anexos é perder os arquivos cifrados |
| P0-6 | **Backup com cópia externa funcionando** e **um ensaio de restauração completo no ambiente real** (banco + pasta de anexos + `attachments check`) | I e N | 1 dia | Os scripts estão provados (103 verificações), mas o ensaio composto, com dado real, ainda não foi feito |
| P0-7 | **Base legal e contrato com a empresa piloto** (LGPD: controlador e operador, finalidade, aviso aos clientes que escrevem pelo WhatsApp) | P e E | fora do código | Dado pessoal real só entra depois disso |

### P1: necessários para operação assistida

| # | Pendência | Dono | Estimativa |
|---|---|---|---|
| P1-1 | **Atrás de proxy reverso**: confiar nos cabeçalhos de encaminhamento (proxies conhecidos), porque o limite de login usa o IP de origem e, sem isso, todos os usuários compartilham o IP do proxy (padrão: 20 logins por minuto no total) | N | 0,5 dia, depois da decisão |
| P1-2 | **Ferramentas de operação no sistema operacional escolhido** (as de hoje são do Windows: PowerShell 5.1, `*.exe` do PostgreSQL, Agendador do Windows) | N | Windows: nada. Linux: 2 a 3 dias (PowerShell 7 verificado, ou equivalentes em shell para backup, restauração e `status`) |
| P1-3 | **Supervisão com destino do alerta** (webhook, e-mail ou canal que alguém lê) e **responsável por atender o alerta** (horário, escalonamento) | P | 0,5 dia nosso |
| P1-4 | **Ensaio de falhas no ambiente real**: Worker derrubado, `clamd` fora do ar, Meta fora do ar, OpenAI sem saldo, disco cheio, cota estourada: conferir o que o atendente vê e o que o `status` acusa | N | 1 dia |
| P1-5 | **Teste de carga e de permanência** com os simuladores (volume de mensagens, memória do Api e do Worker, tempo de varredura de arquivos grandes): hoje **nada de desempenho foi medido** | N | 1 dia |
| P1-6 | **Treinamento e roteiro de teste assistido** dos operadores reais; **canal de suporte** durante o piloto | N e E | 1 dia de material, mais o piloto |
| P1-7 | **Decisão do prazo de guarda dos arquivos recebidos** (hoje desligado: `InboundAttachments = 0`; recomendação técnica 90 dias) e de retenção das mensagens, a confirmar com a base legal | P | decisão |
| P1-8 | **Procedimento de suporte para o que não tem tela**: criar ou alterar atendentes, fila, agente e FAQ (hoje por arquivo e comando, idempotente), emitir token de senha (o administrador ainda não tem a tela) | N | 0,5 dia de procedimento |
| P1-9 | **Conferência no ambiente real dos limites da Meta** (vazão, status da subida de mídia, erros reais) e dos **modelos de mensagem** aprovados | N com M | junto com P0-2 |

### P2: adiáveis (depois do primeiro ciclo)

Painel e API administrativos (agente, usuários, horários), métricas exportadas e alertas ativos, tempo real (SSE), re-cifragem de objetos com a chave nova, quarentena com interface, Windows Defender ou segundo verificador, cota por usuário e prazo por tipo de arquivo, adaptador da Anthropic, Knowledge Engine e integrações LabNet e BImeta, voz, outros canais, PITR do PostgreSQL, alta disponibilidade do `clamd`.

## 3. Caminho crítico

A ordem é ditada pelas **esperas externas**, não pelo nosso esforço:

```text
        dia 0 (10 a 12/11)                           semana 1                    semana 2                  semana 3
P ── decide hospedagem ─┬─ contrata servidor, domínio ──► (N+I) ambiente de homologação no ar ──► testes integrados ──► teste assistido ──► piloto
                        └─ abre a Meta (WABA, número, app, usuário do sistema, modelos)  ══ espera da Meta ══►  conectar e provar (1 dia)
P ── conta de IA com crédito ──► primeira chamada real (0,5 dia)
N ── artefatos de implantação, ajustes P1-1, P1-2, ensaios de falha e carga  (em paralelo, sem esperar ninguém)
```

1. **Hoje a 12/11:** o proprietário decide a hospedagem (seção 4) e **abre o processo da Meta** (é o que tem prazo incontrolável). Sem isso o resto só adianta a homologação, nunca o piloto.
2. **Semana 1:** servidor e domínio no ar; Api e Worker instalados como serviço, `clamd` instalado, chaves entregues (P0-1, P0-4, P0-5); **primeira chamada real à IA** (P0-3); teste da Meta com o **número de teste** (não exige a verificação do negócio) pelo procedimento de `HOMOLOGACAO-META.md`, agora por HTTPS de verdade.
3. **Semana 2:** ensaio de restauração e de falhas, carga, ajustes de implantação (P0-6, P1-1, P1-2, P1-4, P1-5); se a Meta liberou o número real e os modelos: conectar a empresa piloto (`WHATSAPP-CONEXAO-REAL.md`) e **provisionar** a empresa (`NOVA-EMPRESA.md`).
4. **Semana 3:** teste assistido com operadores reais (P1-6), depois o piloto, com o suporte combinado.

## 4. Hospedagem mínima: o que existe, o que falta e as opções

### O que o código faz hoje (lido, não presumido)

- **O Api não serve a interface React.** Em desenvolvimento quem faz isso é o proxy do Vite (`/api/*` vira `http://localhost:5155/*`, com o prefixo `/api` removido). Em produção é preciso um **proxy reverso** que sirva `frontend/dist` (resultado de `npm run build`) e encaminhe `/api/*` (sem o prefixo) e `/webhooks/whatsapp/*` ao Api. Não há CORS (mesma origem).
- **Segurança em produção:** os recursos de desenvolvimento (`X-Dev-Tenant`, `/simulator`, preparação automática de empresa) só existem no ambiente `Development` e **impedem a partida** se ligados fora dele. Em produção a API é a autenticada de sempre.
- **O guarda do ingresso público (`PublicIngressGuard`) é para o túnel de desenvolvimento.** Com `Channels:Webhook:PublicListenerPort` definido, qualquer requisição com `X-Forwarded-For` (que todo proxy reverso acrescenta) ou chegada pela porta pública só alcança o webhook; **todo o resto responde 404**. Em produção atrás de proxy reverso **deixe `PublicListenerPort` sem valor** (o guarda fica desligado) e restrinja os caminhos no próprio proxy; senão a interface inteira some.
- **Limite de login por IP de origem** (padrão 20 por minuto), sem confiança em cabeçalho de encaminhamento (o próprio código diz que a configuração de proxies confiáveis não existe ainda): atrás de um proxy todos compartilham um só IP (P1-1). Há também o bloqueio por conta (`login_throttles`), que independe do IP.
- **Api e Worker usam a MESMA pasta de arquivos** (`Conversations:Attachments:RootPath`) e a mesma configuração de `Channels:*` e da chave mestra: no piloto, **os dois na mesma máquina**. Dois servidores exigiriam armazenamento compartilhado, que não existe.
- **Ferramentas de operação** (`Backup-Banco.ps1`, `Restaurar-*.ps1`, `Status-Ambiente.ps1`, `Registrar-Agendamento.ps1`...) são de **Windows**: PowerShell 5.1, `pg_dump.exe` e demais `*.exe`, Agendador do Windows. Os scripts que só chamam a API por HTTPS (`Conectar-ContaWhatsApp.ps1`, `Test-ProntidaoWhatsApp.ps1`) rodam do computador do operador contra qualquer servidor.

### As duas opções realistas

| | **A. Servidor Linux (recomendado)** | **B. Servidor Windows** |
|---|---|---|
| Aderência | é o alvo do projeto (CLAUDE.md: Linux e VPS depois do desenvolvimento no Windows); `clamd` vem em pacote da distribuição | reaproveita **tudo** o que foi testado nas ferramentas de operação |
| `clamd` (P0-4) | pacote oficial da distribuição (a página de pacotes do ClamAV lista Debian, Ubuntu, RHEL e derivados) | **suporte a Windows não confirmado** na documentação lida: exige confirmar, ou apontar `Scanner:Host` para um `clamd` em outra máquina |
| Api e Worker como serviço | `systemd` (exemplo na seção 8) | Agendador do Windows com reinício em falha, ou um empacotador de serviços de terceiros (a decidir; nada foi instalado) |
| Ferramentas de operação (P1-2) | **2 a 3 dias** para verificar com PowerShell 7 ou escrever equivalentes em shell (backup, restauração, `status`, agendamento por `systemd` ou `cron`) | nada a fazer |
| Banco | PostgreSQL 17 em pacote da distribuição ou do repositório oficial do PostgreSQL | PostgreSQL 17 já usado no desenvolvimento |
| Custo de operar | menor (servidor pequeno), mais barato de hospedar | exige licença e mais memória |

**Recomendação:** A, com um **servidor único** (Api, Worker, PostgreSQL, `clamd` e proxy no mesmo host) para o piloto de **uma** empresa. Dimensionamento a confirmar na instalação (nada foi medido): **4 GB de memória no mínimo** (o `clamd` carrega as assinaturas na memória e costuma ocupar mais de 1 GB), 2 vCPU e disco com folga para os arquivos e os backups locais. Alternativa se o prazo apertar: B para o piloto, com `clamd` em outra máquina Linux pequena.

Peça de decisão do proprietário: **qual das duas, quem contrata e administra o servidor, e o domínio do atendimento.**

## 5. Configuração do ambiente (nomes, nunca valores)

Variáveis de ambiente do **Api e do Worker** (`__` separa os níveis; **sem segredo em arquivo versionado**; use identificador de chave sem hífen). Procedimentos nos documentos citados.

| Para quê | Chaves | Onde está explicado |
|---|---|---|
| Banco | `ConnectionStrings__Default` (ou uma por módulo) | `NOVA-EMPRESA.md` |
| Credenciais dos canais cifradas | `Channels__Secrets__ActiveKeyId`, `Channels__Secrets__Keys__<id>` (**as mesmas** no Api e no Worker) | `WHATSAPP-CONEXAO-REAL.md` |
| Endereço público do webhook | `Channels__Webhook__PublicBaseUrl` (HTTPS válido) | `WHATSAPP-CONEXAO-REAL.md` |
| Chave da IA do tenant cifrada e catálogo de modelos e preços | `AiGateway__Secrets__ActiveKeyId`, `AiGateway__Secrets__Keys__<id>`, `AiGateway__Catalog__Models__<n>__...` | `NOVA-EMPRESA.md` |
| Arquivos: pasta, cota, verificador, cifra | `Conversations__Attachments__...` | `SEGURANCA-DOS-ARQUIVOS.md` |
| Retenção e expurgo agendado | `Retention__...` | `RETENCAO-DE-DADOS.md` |
| Esperas e tentativas do relay | `Messaging__...` | `OPERACAO-E-SUPERVISAO.md` |
| Ambiente | `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_URLS` só no laço local | seção 8 |

**Não definir em produção:** `Tenancy__Development__*`, `Channels__Webhook__PublicListenerPort`.

## 6. O que o proprietário faz agora (e que nós não conseguimos fazer por ele)

1. **Abrir a Meta hoje:** criar ou usar o aplicativo da Meta (tipo Empresa), iniciar a **verificação do negócio** da empresa piloto, registrar o **número de produção** na WABA (número que não esteja em uso no aplicativo do WhatsApp, ou o procedimento de migração), criar o **usuário do sistema** e o token permanente, e **submeter os modelos de mensagem** que o atendente usará fora da janela de 24 horas. Os prazos da Meta não são controlados por nós e não foram estimados. (Para os testes de homologação basta o **número de teste** gratuito do próprio aplicativo.)
2. **Decidir e contratar a hospedagem** (seção 4) e o **domínio**; informar quem terá acesso ao servidor. **O desenvolvimento não opera servidor remoto sem credencial entregue por você e sem sua autorização.**
3. **Conta da IA com crédito** na OpenAI (da empresa piloto, BYOK) e autorizar a **primeira chamada real** no ambiente de homologação (hoje não se faz chamada real sem autorização expressa).
4. **Definir a empresa piloto** (quem, número, janela do piloto, quantos operadores, horário de atendimento), o **contrato e a base legal** (P0-7) e o **prazo de guarda dos arquivos recebidos** (P1-7).
5. **Guardar fora da máquina** as chaves e a frase do backup (cofre ou gerenciador de senhas); **destino externo** do backup (outro disco, outro servidor ou armazenamento de objetos).

## 7. Viabilidade das metas (avaliação nossa, fundamentada, sem promessa)

| Meta | Parte técnica nossa | Depende de terceiros | Avaliação |
|---|---|---|---|
| **20/11: MVP operacional em homologação, pronto para testes integrados** | ~4 a 5 dias úteis: artefatos de implantação, P1-1, P1-2 (na opção B, quase nada), ensaios de falha e de carga, restauração real, `clamd` real, primeira chamada de IA | servidor, domínio e conta de IA **até 12/11**; número de teste da Meta | **Viável se a decisão de hospedagem sair até 12/11.** Com decisão em 13/11 ou depois, **em risco**. Observação: **20/11 é feriado nacional (Consciência Negra): na prática o prazo útil é 19/11**; restam 8 dias úteis desde hoje |
| **30/11: início do piloto com empresa real** | ~2 dias adicionais (conectar a empresa, provisionar, treinar, suporte) | **verificação do negócio, número de produção e modelos da Meta (prazo desconhecido)**, contrato e base legal (P0-7), disponibilidade dos operadores | **Possível apenas no melhor caso.** O caminho crítico real é a Meta, não o produto. Se o processo da Meta for aberto até 12/11 e andar sem retrabalho, 30/11 é plausível; **se qualquer etapa pedir correção, passa de 30/11** |

**Ajustes propostos:**

1. Tratar **19/11** como a data real da meta de homologação (a de 20/11 é feriado).
2. Tratar **30/11 como ponto de decisão** (go ou no-go) e não como compromisso: **início assistido com grupo reduzido** (uma empresa, poucos operadores, horário limitado) entre **30/11 e 11/12**, conforme a Meta e a disponibilidade dos operadores.
3. **Teste integrado com o número de teste da Meta** já na homologação (19/11), para que a única novidade do piloto seja o número real e os modelos aprovados.
4. Registrar 12/11 como data de corte para as decisões do proprietário (hospedagem, Meta aberta, conta de IA).

## 8. Pontos de partida de implantação (NÃO testados)

Nenhum dos arquivos abaixo foi executado: não há servidor Linux, `systemd` nem Caddy nesta máquina. São **ponto de partida** para quem opera o servidor; confira cada diretiva na documentação da versão instalada antes de usar.

`Caddyfile` (proxy reverso com HTTPS automático, interface e API na mesma origem; o webhook fica fora do prefixo `/api`):

```text
atendimento.exemplo.com.br {
    encode gzip

    handle /webhooks/whatsapp/* {
        reverse_proxy 127.0.0.1:5155
    }

    handle /api/* {
        uri strip_prefix /api
        reverse_proxy 127.0.0.1:5155
    }

    handle {
        root * /srv/iabimeta/frontend
        try_files {path} /index.html
        file_server
    }
}
```

`/etc/systemd/system/iabimeta-api.service` (o do Worker é igual, trocando o executável e sem `ASPNETCORE_URLS`):

```ini
[Unit]
Description=ia.bimeta Api
After=network-online.target postgresql.service

[Service]
User=iabimeta
WorkingDirectory=/opt/iabimeta/api
ExecStart=/usr/bin/dotnet /opt/iabimeta/api/IaBimeta.Api.dll
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5155
EnvironmentFile=/etc/iabimeta/ambiente.env

[Install]
WantedBy=multi-user.target
```

`/etc/iabimeta/ambiente.env`: um arquivo só do usuário do serviço (permissão `600`), **fora do repositório**, com as chaves da seção 5, igual para o Api e o Worker.

Antes do primeiro `systemctl start`: banco criado e migrado **com backup antes** (o Api aplica as migrações novas ao subir em banco existente), pasta de arquivos criada com dono e permissão só do serviço, `clamd` no ar, e `attachments report` mostrando o verificador respondendo.

## 9. O que esta entrega já deixou pronto para o piloto

- Segurança dos arquivos (ADR 0032, `SEGURANCA-DOS-ARQUIVOS.md`): verificação exigida, cifra em repouso, cota por empresa, prazo de guarda auditável, conferência banco × pasta, backup e restauração da pasta com perda da origem ensaiada.
- ADR 0031 (envio de arquivos pelo atendente) **aceito**.
- Checklist de ligação das proteções no ambiente (`SEGURANCA-DOS-ARQUIVOS.md`, seção 8).
- Esta análise com o caminho crítico, as decisões que dependem do proprietário e a avaliação das metas.
