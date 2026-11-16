# Roteiro único de homologação da V1 (13/11/2026)

> **Escopo da V1 alterado em 16/11/2026 (decisão do proprietário):** o **ia.bimeta Embedded** (painel incorporável, autenticação federada, vínculo cliente-contato, **início ativo de conversas**, histórico contextual e Capabilities no painel; ADR 0039 e 0040) é **parte obrigatória da V1**. A V1 **só é considerada concluída** depois da aprovação do Embedded, do início ativo, do histórico contextual e do Capabilities no painel (Fase 8 abaixo). As datas de 19/11 (homologação) e 30/11 (piloto) deixam de valer; o cronograma vigente está em `docs/architecture/EMBEDDED-PLANO.md`.
>
> **Candidata histórica `v1-rc1` (15/11/2026):** tag local no commit `32cf06d` da branch `feature/mvp-c-ia-minima`, código até `4eb19a7` (inclui `207ed6e`, `ed0719d` e a consolidação das correções da auditoria do Codex, aprovada tecnicamente). Gate do backend verde nesse commit; sem push e sem integração à `main`. Serve **só para preparar e validar a infraestrutura do Ubuntu em paralelo** (fases 0 a 1 e 7, sem caracterizar a homologação final). Implante `v1-rc1` com `git rev-parse HEAD` conferido e registre-o. O desenvolvimento do Embedded segue na branch `feature/v1-embedded`; a **candidata final** será definida depois dela e substitui `v1-rc1` para a homologação.

Sequência para sair do código até a V1 homologada numa **VM Ubuntu de teste**, cobrindo ContactCenter, Agents, Knowledge Engine, Capabilities, Workflows **e Embedded**. Cada fase aponta para o documento de detalhe; **não repete** o que já está lá. Regra geral: **não avance de fase com um item falhando**; registre o resultado de cada item (data, quem, evidência).

**O que já está provado (Windows, sem infraestrutura externa):** gate automático (backend e frontend verdes), testes de API com PostgreSQL real, teste funcional do editor de fluxos com a API real (`EditorDeFluxosComApiRealTests`). **O que nada disso prova:** Docker/Caddy/systemd, ClamAV real, pgvector/Ollama, Meta, OpenAI, ERP, navegador.

## Fase 0. Antes da VM (no PC de desenvolvimento)

| # | Item | Como |
|---|---|---|
| 0.1 | Gate verde | `dotnet test backend/IaBimeta.slnx`; no `frontend`: `npx vitest run` e `npm run build` |
| 0.2 | Editor com API real | incluído no gate (`EditorDeFluxosComApiRealTests`); à mão: `npm run test:e2e` com as variáveis `E2E_*` (ver `frontend/vitest.e2e.config.ts`) |
| 0.3 | Decisões do proprietário | domínio e DNS, IP público fixo, NAT 80/443, conta Meta, chave OpenAI/Anthropic, destino do backup externo, ratificação dos ADRs ainda Propostos (0033 a 0038) |

## Fase 1. A VM e a pilha (`IMPLANTACAO-LINUX.md`)

Ordem: `instalar-host.sh` (root) → levar o código → `gerar-segredos.sh --dominio ... --email ...` → **guardar `.env`, `app.env` e `backup.env` fora da máquina** → `preflight.sh` (0 erro; ele confere as chaves de Channels, AiGateway, Capabilities, **Workflows**, anexos e a do Knowledge) → `subir.sh` (aplica `migrate`) → `aceitacao.sh --confirmo-que-e-teste` (10 grupos, tudo `PASSOU`) → reinício da VM → `empresa.sh` (valida, depois `--aplicar`) → `backup.sh` e `agendar.sh`.

Conferir: `cli.sh migrate --list` sem pendência (inclui os esquemas `workflows` e `capabilities`, a coluna de autor `Bot` do Conversations e a extensão `vector` se o Knowledge híbrido for usado).

## Fase 2. ContactCenter e Conversations (sem IA)

Com a IA desligada na empresa piloto (`PILOTO-PRIMEIRA-EMPRESA.md`, `OPERACAO-E-SUPERVISAO.md`):

1. Login do administrador, do atendente e de um colaborador de outra unidade (permissões e escopo: cada um só vê o que lhe cabe).
2. Fila por unidade e setor, horário, atribuição, **assumir** (conflito entre dois atendentes: só um vence), transferir entre atendentes e filas, encerrar.
3. Histórico paginado, rascunhos persistentes, mensagens novas, anexos recebidos e enviados (imagem, documento, áudio) com o ClamAV real (`SEGURANCA-DOS-ARQUIVOS.md`, `MIDIA-DO-WHATSAPP.md`, `ENVIO-DE-ARQUIVOS.md`).
4. Modelos de mensagem fora da janela de 24 h (`MODELOS-DE-MENSAGEM.md`).
5. Senhas e sessões: troca, redefinição por token (`SENHAS-E-SESSOES.md`).
6. Isolamento: com **duas empresas**, nenhuma vê dado da outra (inclusive por URL direta).

## Fase 3. WhatsApp real (`HOMOLOGACAO-META.md`, `WHATSAPP-CONEXAO-REAL.md`)

Conectar o número pelos scripts PowerShell do PC contra `https://<dominio>`; `Test-ProntidaoWhatsApp.ps1`; mensagem de entrada → fila → resposta do atendente → entrega e status; assinatura inválida rejeitada; reenvio idempotente (webhook repetido não duplica). Falha de envio aparece como sinal na fila e é recuperável pelo atendente.

## Fase 4. Agents e Knowledge Engine

1. **FAQ direto** (`CONHECIMENTO-DO-FAQ.md`): entrada com `AutoReply` aprovada responde sem chamar a IA; entrada sem aprovação não responde; lacunas aparecem na área Conhecimento e viram rascunho revisável (`HOMOLOGACAO-DO-CONHECIMENTO.md`).
2. **IA com chave BYOK real da empresa**: uma pergunta responde com a última pergunta apenas; **teste de isolamento** do `CLAUDE.md` (Mauro #1001 x Pedro #1002: a resposta a Mauro usa só o contexto de #1001 e vai só a #1001); falha da IA → política `OnAiPermanentFailure`; rajada longa → alerta ao atendente; **transferência IA → humano** leva histórico e resumo.
3. **Knowledge híbrido (opcional)** (`CONHECIMENTO-HIBRIDO.md` §4 e §5): só depois de `pgvector` e Ollama reais passarem os testes do §4. Começa em `Evaluate`, sem responder ao cliente.

## Fase 5. Capabilities (`MOTOR-DE-INTEGRACAO.md` §11)

1. Chave `Capabilities__Secrets__*` guardada fora da máquina.
2. Um **ERP de homologação** (ou simulador na VM): `Testar conexão`, uma leitura, e uma escrita com a idempotência real do sistema (repetir a chave não duplica).
3. Política de destino de saída: só o CIDR exato liberado pelo operador; certificado inválido recusado; credencial nunca aparece em log nem na interface.
4. Consulta do agente (ADR 0036, desligada por padrão): ligar só com a operação revisada (`Proven`, quem pode usar) e `TurnMaxDuration` conferida.

## Fase 6. Workflows / Chatbot Visual (`CHATBOT-VISUAL.md`)

O runtime nasce **desligado**. Ligar só depois das fases 2 a 4.

1. **Editor no navegador** (o que o jsdom não cobre): arrastar nós, **conectar saídas com o mouse**, painel do nó, validação com nós em vermelho, simulador com o caminho destacado, navegação por teclado. Em seguida publicar (a versão fica imutável).
2. **Ativação**: criar com organização, unidade e canal reais (os do tenant); testar a recusa de identificador inventado e de canal de outra organização; nasce desligada; ligar para **um canal de teste com parcela pequena**.
3. `Workflows__Options__Enabled=true` **só no Worker**: ajustar em `deploy/app.env` e **recriar** o Worker (`docker compose up -d --no-deps --force-recreate worker`; um simples `restart` não relê o `app.env`). Conversa nova no canal de teste: entra no fluxo, lista numerada legível, resposta por número/rótulo, janela de 24 h, falha de envio registrada sem reprocessar o fluxo.
4. **Transferência** do fluxo para a fila real (aviso único, item na fila, atendente vê o histórico) e **entrega ao agente** (resposta única à última pergunta).
5. **Integração** de um nó com o ERP de homologação (operação pública; falha e timeout seguem a política de falha).
6. **Drenagem** (ensaio obrigatório antes do piloto): desligar a ativação; `cli.sh workflows status`; deixar uma instância parada esperando resposta; `cli.sh workflows drain --confirm --tenant <slug>`; conferir que a conversa voltou ao agente, que nada foi enviado ao cliente e que o status zerou; só então `Enabled=false` em `app.env` e recriação do Worker. Antes: `migrate` aplicou `FlowDrainOutbox` e o CLI e **todos** os Workers estão na versão nova. A drenagem é **recusada por inteiro** se houver execução ativa ou efeito em voo (repita depois); a retomada do Agents sai pelo relay do outbox do Workflows mesmo com o runtime desligado, e sobrevive a queda do CLI depois do commit.
7. Retenção (`Retention__Days__FlowInstances`) definida por empresa.

## Fase 7. Segurança, operação e encerramento

1. Backup manual, **ensaio de restauração** e copia externa conferida (`BACKUP-E-RESTAURACAO.md`); nenhum segredo nos logs.
2. `retention report`, `status.sh`, alertas do monitor; matar o Worker à força e vê-lo voltar.
3. Atualização simulada (`atualizar.sh`: backup antes, migrations, recriação) e o plano de reversão (restaurar o backup anterior com a versão anterior do código).
4. Relatório final: lista dos itens PASSOU/FALHOU/NÃO FEITO, com data e evidência.

## Fase 8. Embedded (obrigatória para concluir a V1; ADR 0039 e 0040)

**Estado:** a definir por etapa (E1 a E8) em `docs/architecture/EMBEDDED-PLANO.md`; os procedimentos detalhados (conexão de integração, chaves, origens, ERP fictício e BImeta) entram em `docs/operations/EMBEDDED.md` na etapa E8. **Nenhum envio real à Meta sem autorização expressa**; o primeiro envio real é um item desta fase, com número de teste.

| # | Item | Critério de aceite |
|---|---|---|
| 8.1 | Conexão de integração, chaves e origens | chave com rotação; segredo nunca em log nem na interface depois da criação |
| 8.2 | Painel no ERP fictício e no BImeta ERP (Edge, Chrome e **Safari**) | abre contextualizado no cliente; só conversas autorizadas; `FullAttendant` |
| 8.3 | Segurança da federação | ticket reutilizado, expirado, sem verifier ou de outra origem é recusado; logout do ERP revoga a sessão |
| 8.4 | Clickjacking | origem não autorizada não consegue embutir; o app independente também não pode ser embutido |
| 8.5 | Isolamento multi-tenant | duas empresas: nada cruza, inclusive por ticket, vínculo e rota direta |
| 8.6 | Contact Center incorporado | assumir, responder, transferir, encerrar, anexos e modelos como no app independente |
| 8.7 | Histórico do cliente | conversas do cliente no escopo autorizado, abertas e encerradas, paginadas, com mídia |
| 8.8 | Início ativo (ADR 0040) | sem conversa aberta, com consentimento e modelo aprovado; sem consentimento é recusado; sem duplicar sessão; retomada após falha; `SendUnknown` nunca reenviado; **primeiro envio real com número de teste, autorizado** |
| 8.9 | Consentimento e revogação | registro com evidência, origem, finalidade e data; PARAR, SAIR e CANCELAR revogam; falsos positivos não |
| 8.10 | Capabilities no painel | só operações liberadas, com o cliente em foco do ERP; auditoria; `CustomerData` conforme a classe |
| 8.11 | App independente | login por senha e início ativo funcionando como antes |

## Critério de conclusão da V1

A V1 só é considerada concluída quando as fases 0 a 8 estiverem registradas como PASSOU na VM Ubuntu, **inclusive** as aprovações do Embedded, do início ativo, do histórico contextual e do Capabilities no painel (itens 8.2 a 8.10), com a versão candidata final identificada por commit.

## Reversão rápida por funcionalidade

| O que | Como desligar |
|---|---|
| Fluxos | ativação desligada → drenagem → `Workflows__Options__Enabled=false` em `app.env` e recriar o Worker |
| FAQ automático | modo `Off` no Knowledge; `AutoReply` desligado por entrada |
| Consulta do agente ao ERP | `Agents__Options__IntegrationLookupsEnabled=false` |
| IA do tenant | desligar o agente da fila (conversas vão ao atendente) |
| Operação de ERP | desativar a definição da capacidade |

## O que continua sem prova (declarado, não escondido)

Docker Compose, Caddy, systemd, ClamAV real, pgvector, Ollama, Meta, OpenAI/Anthropic, ERP, desempenho e memória sob carga, restauração com dados reais, e o arraste/conexão de nós por mouse. Cada um tem sua fase acima; o resultado só vale depois de executado na VM.
