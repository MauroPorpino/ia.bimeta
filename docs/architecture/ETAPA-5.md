# Etapa 5 — Plano arquitetural consolidado (fase 5.0, documental)

Versão 0.4.1 — 03/11/2026 (criado em 30/10/2026). **Aprovação parcial, só para o escopo do MVP (03/11/2026, seção 8.0 e seção 11).** O que a seção 8.0 e os status dos ADRs não declaram aprovado **permanece Proposto**, e **nada está implementado**: a aprovação documental não autoriza código, projetos, migrações, commits nem push. Não é fonte normativa de fronteira: o `BOUNDARIES.md` (v0.4.7) e a matriz `module-dependencies.json` continuam sendo as fontes, e a **matriz não foi alterada**. Este documento reúne, num lugar só, o escopo, a situação dos gates, as dependências, a ordem de execução, os critérios de aceitação e as **decisões que dependem do decisor**.

## 1. Escopo e estado

A etapa 5 passa de "WhatsApp e webhooks" (`ARCHITECTURE.md` §11, item 5; `BOUNDARIES.md` §12.12) para **atendimento omnichannel unificado**:

- **cinco canais obrigatórios** (WhatsApp Cloud, Instagram Direct, Messenger, e-mail por Graph, Gmail API e IMAP/SMTP, e SMS), com núcleo comum no `Channels` e adaptadores em `backend/adapters/IaBimeta.Adapters.*`;
- **atendimento numerado por tenant**, com associação explícita de mensagens (chat por contato, e-mail por caixa e thread);
- **resposta humana externa** ao cliente, com autoria, idempotência e cerca;
- **colaboração humana** (participantes, políticas, limites de leitura) e **comunicação interna** estruturalmente separada do canal externo;
- **interface unificada** (React e TypeScript, ainda inexistente);
- **encerramento do atendimento** como capacidade geral do ContactCenter (5.1b), com a nona operação delegada do gateway;
- **WebChat nativo e continuidade WhatsApp → WebChat** (subetapa 5.2w): convite seguro, atendimento no ContactCenter existente, sessão satélite e retorno controlado;
- **extensibilidade futura** para agentes de IA como participantes, interação interempresarial e federação (nada implementado).

**Fora da etapa 5:** runtime completo de IA e orquestração autônoma; identidade operacional de agentes; rede interempresarial operacional e `IntercompanyInteraction`; federação e A2A; TikTok, Telegram, YouTube, Google Business Profile, LinkedIn, Discord e LINE; comentários, avaliações e leads como interação pública ou comercial (só **declarados como não suportados**); cliente comum e vinculação verificada de identidades; integração com ERP, CRM e demais sistemas; voz; e, no WebChat, aplicativos nativos, vídeo, voz em tempo real, migração automática sem ação do cliente, fusão física de sessões, convite iniciado por atendente, domínio próprio e WebChat iniciado sem convite.

**Premissas e limites desta fase.** O relatório integral do Codex sobre o planejamento nunca veio no texto colado, só listas de prioridades; os ajustes aqui incorporados são os dessas listas. Se o relatório trouxer outros achados, eles precisam ser reenviados para serem cobertos. Os fatos sobre APIs de terceiros vêm de buscas de 30/10/2026, são **indicativos** e parte vem de fonte secundária.

## 2. Mapa dos documentos da 5.0

| Documento | Conteúdo | Status |
|---|---|---|
| `docs/adr/0011-channels-receiver-inbox-sender-e-gates-da-etapa-5.md` | Receiver, Inbox, Sender, idempotência, cerca, `SendUnknown`, evidência, anexos, capacidades, conciliação da REC-2, situação de PRV-2 e REC-2, extensibilidade | **Aceito parcialmente** (03/11/2026, escopo do MVP; B.6, H, migrações 8 e 9 e os demais canais seguem Propostos) |
| `docs/adr/0012-numeracao-e-associacao-de-sessoes.md` | índices de sessão aberta (chat e e-mail), modo de associação, numeração por tenant, backfill, associação de e-mail (G1 e G2), `Down` condicional | **Aceito parcialmente** (03/11/2026, escopo de chat; e-mail segue Proposto) |
| `docs/adr/0013-colaboracao-humana-e-mensagens-internas.md` | participantes, limites de leitura, vazamentos laterais, mensagens e anexos internos, notificações | Proposto |
| `docs/adr/0014-resposta-humana-externa-intencao-e-reenvio-delegado.md` | `RegisterHumanReply`, `reply_intents`, cerca, serialização de permissão, anexos, `RequestDelegatedResend`, evidência | **Aceito parcialmente** (03/11/2026: 7ª operação, `reply_intents`, `ClientRequestId`; a 8ª e os anexos seguem Propostos) |
| `docs/adr/0015-email-e-sms-nativos-no-channels.md` | sincronização e recuperação de caixa, envio e segurança de e-mail, supressão e orçamento de SMS | Proposto |
| `docs/adr/0016-webchat-nativo-e-continuidade-entre-canais.md` | WebChat nativo, `ChannelHandoff`, convite, claim, sessão satélite, recuperação, continuidade no ContactCenter, encerramento (dependência), custos e indicadores | **Aceito parcialmente** (03/11/2026: só a seção M e AC-ENC01 a AC-ENC09; o restante segue Proposto) |
| `docs/adr/0009-…` (emendas de 30/10/2026 e 03/11/2026) | porta do gateway com **nove** operações previstas (seis implementadas); **oito no MVP**: as seis, a 7ª e a 9ª; a 8ª segue Proposta | **Aceito parcialmente** (03/11/2026) |
| `docs/adr/0010-…` | fronteira histórica, cerca de execução e auditoria durável | **Aceito** (03/11/2026, integral) |
| `docs/adr/0017-etapa-6a-fatia-minima-de-ia-aigateway-e-agents.md` | etapa 6a: AiGateway e Agents mínimos (OpenAI e Anthropic, BYOK), projetos, contrato comum, credenciais, FAQ por tenant, verificação da documentação dos provedores | **Aceito** (03/11/2026, incluindo DP-12 e DP-13, com o escopo exato no status do ADR; a aprovação não autoriza criar os projetos nem implementar) |

## 3. Situação dos gates (30/10/2026; atualizada em 03/11/2026)

| Gate | Situação | Evidência | O que falta |
|---|---|---|---|
| **REC-2** | **Conciliada e aprovada (03/11/2026, DP-03). Cumprido.** | ADR 0011, A.1; `BOUNDARIES.md` §12.2 ajustado (v0.4.7: REC-1, REC-2, REC-3, REC-4 e REC-6) | nada para o MVP. A pendência equivalente no ADR 0008 ainda não foi ajustada (precisa de autorização) |
| **PRV-2** | **Cumprido para o WhatsApp (03/11/2026, DP-02):** oito itens confirmados, itens 2 e 4 não documentados (tratamento interno), item 11 parcial (estratégia conservadora e `SendUnknown`). **Não cumprido para os demais canais.** | ADR 0011, A.2 (fontes e data); `BOUNDARIES.md` §12.11 | gate **por adaptador** (5.3 a 5.6), com autorização de acesso externo |

Consequência: a **5.1a** deixou de ser bloqueada pelos dois gates (resta a autorização de implementação, de projetos, migrações e dependências); a **5.1b** nunca dependeu deles, e depende de DP-01 (aprovado para o MVP), DP-05, DP-07, DP-08 e DP-09 (decididos em 03/11/2026).

**A REC-2 também cobre a resolução do tenant por domínio do WebChat** (ADR 0016, Proposto). O envio de convite e de retorno pelo WhatsApp depende de itens novos da PRV-2 (**PRV-2-WC**, seção 9.1), **pendente e fora do MVP**.

## 4. Dependências entre fatias

| Fatia | Conteúdo | Depende de |
|---|---|---|
| **5.0** | gates e ADRs (este documento) | — |
| **5.1a** | núcleo do `Channels`: generalização, `inbox_event`, `outbound_message`, `inbox_quarantine`, Receiver, ingestão por lane com adaptador fake, Sender, WhatsApp como primeiro provedor real | 5.0 aprovada (DP-01, DP-02, DP-03, DP-06, DP-07, DP-11) |
| **5.1b** | base do atendimento unificado: limites, índices, numeração, anexos, `RegisterHumanReply`, `reply_intents`, limites de leitura do fluxo externo, `RequestDelegatedResend`, **encerramento do atendimento (`CloseAttendance` e 9ª operação)** | 5.0 aprovada (DP-01, **incluída a emenda da 9ª operação ao ADR 0009**, DP-05, DP-07, DP-08, DP-09) |
| 5.2 WhatsApp | adaptador (assinatura, texto, mídia, status, templates, janela) | 5.1a e 5.1b |
| **6a** IA mínima (ADR 0017) | `AiGateway` e `Agents` mínimos, adaptadores OpenAI e Anthropic, `ContactCenter.Contracts` (só `RequestHandoff`), instruções e FAQ por tenant, consumo e limites | ADR 0017 (aceito em 03/11/2026, DP-12 e DP-13), plano da fatia e autorização de implementação (criação dos projetos). **Não depende** de 5.1a, 5.1b nem 5.2: começa contra o simulador de desenvolvimento e converge na integração ponta a ponta |
| **5.2w** WebChat e continuidade | portal do cliente, convite, claim, `ChannelHandoff`, sessão satélite, recuperação, continuidade no ContactCenter, custos e indicadores | `5.2w-a`: 5.1a, 5.1b e DP-04 (com WhatsApp fake); `5.2w-b`: 5.2 e **PRV-2-WC** |
| 5.3 Instagram, 5.4 Messenger | adaptadores sobre a infraestrutura Meta | 5.2 |
| 5.5 E-mail | três adaptadores, threads, anexos, sanitização, sincronização, múltiplas caixas | 5.1a e 5.1b |
| **5.6a** SMS transacional | envio, orçamento, supressão, status, auditoria (fluxo do `Channels`, sem sessão) | **só 5.1a** |
| **5.6b** SMS bidirecional e SMS de atendimento com resposta humana | recebimento e associação | 5.6a e 5.1b |
| 5.7 Colaboração | participantes, política colaborativa, notas, menções, notificações | 5.1b |
| 5.8 Interface | três painéis e compositores | 5.1b (mínimo); cresce com cada canal |
| 5.9 Validação integrada | segurança, isolamento, concorrência, recuperação, compatibilidade com a etapa 4 | todas |

**Caminho crítico (completo):** 5.0 → 5.1a ∥ 5.1b → 5.2 → 5.2w → (5.3, 5.4) → 5.9. **Caminho crítico do MVP (seção 11):** decisões → 5.1a (mecânica) → Sender e WhatsApp real → integração ponta a ponta, com 5.1b (subconjunto), 6a e a interface em paralelo. A 5.2w-a pode correr em paralelo à 5.2 (com WhatsApp fake). Independentes depois de 5.1: 5.5, 5.6, 5.7 e o esqueleto da 5.8. **Cada canal fecha sozinho**, com adaptador fake e payloads sintéticos, sem depender dos demais **e sem dispensar os gates técnicos da seção 9.1**; a habilitação operacional (credenciais, aprovações e contas reais) é registrada **separadamente** da conclusão técnica.

## 5. Ordem de execução

### 5.1a — núcleo do `Channels` (só depois da aprovação)

1. Generalizar instância, provedor e restrições; identificador opaco de webhook na conta; novos papéis de segredo (migração 1).
2. `inbox_event`, `outbound_message`, `inbox_quarantine`, lanes e o estado `PendingAssociation` (migração 2).
3. Receiver e ingestão por lane, com adaptador **fake**; **medição** da consulta sob a trava da sessão (ADR 0008) **antes** do worker de ingestão.
4. Sender com lane, limites, estados de efeito e a regra de `SendUnknown` sem reenvio automático, mais a ingestão de evidência (`EvidenceSource`).
5. WhatsApp como primeiro provedor real, **depois** da mecânica validada.

### 5.1b — base do atendimento unificado (nesta ordem, por dependência e risco)

1. Alargar os limites (`ContactRefMaxLength`, `ProviderEventIdMaxLength`) e acrescentar `sender_contact_id`.
2. **`association_mode`, `thread_key` e os dois índices** (migração 5), com as **cinco mutações** (M-1a a M-1e) e a regressão da etapa 4, **antes de qualquer outra coisa**.
3. **Numeração** (migrações 3 e 4), com o backfill sem renumerar.
4. Anexos com escopo e origem, `ChannelData` e `outbound_request_hash`.
5. **`RegisterHumanReply`** e `reply_intents` com `authz_version`, trava, emissão de token e **chave de idempotência do cliente** (`ClientRequestId`), em modo exclusivo.
6. Limites de leitura do fluxo externo.
7. `RequestDelegatedResend` e a ingestão de `EvidenceSource`.
8. **Encerramento do atendimento:** `CloseAttendance` (autorização do ADR 0010, D.19; operação pendente `Close`; recibo; reconciliação), 9ª operação `CloseSession` delegada e consulta de efeito `SessionClose`. Só depois da aprovação da segunda emenda ao ADR 0009 (DP-01).

**Por quê:** os passos 1 a 3 mexem em invariantes da etapa 4 (sessão e índice) e precisam estar verdes antes de construir em cima; o passo 5 depende dos passos 2 e 3.

**Subconjunto aprovado para o MVP (03/11/2026, seção 11):** passos 1, 2 (só a parte de chat), 3, 4 (reduzido: `ChannelData` com metadados e placeholders de mídia, `outbound_request_hash` e `outbound_operation_id`, **sem** armazenamento nem anexos), 5, 6 e 8. **Fora do MVP:** o `RequestDelegatedResend` do passo 7 (8ª operação) e as partes de e-mail do passo 2. **Fica no MVP** a parte do passo 7 que é a ingestão de `EvidenceSource` em `RecordDeliveryStatus`, necessária ao Sender (5.1a, passo 4) e ao `ConfirmedSent` manual (DP-08). O corte exato da migração 5 é definido no plano da 5.1b.

### 6a — IA mínima (ADR 0017; só depois da autorização de implementação)

1. `ContactCenter.Contracts` (só `RequestHandoff`) e atualização do teste de arquitetura de ausência do projeto.
2. `AiGateway`: contrato `IAiProvider`, catálogo de modelos, configuração por tenant, credencial BYOK cifrada (solução mínima e isolada), limites, `AiRequest` e dublê de provedor (migração 15).
3. `Agents`: `AiTurn`, agrupamento, prompt com instruções e FAQ publicados, decisão estruturada validada, política de mídia (MED-2), retentativa, handoff, executor no Worker e reconciliador (migração 16).
4. Adaptador **Anthropic** e adaptador **OpenAI**, com a mesma suíte de contrato.
5. Endpoints de configuração e de consumo; tela mínima de configuração na interface, ou chamadas de API até ela existir.
6. Teste manual com chaves reais de cada provedor (só com autorização expressa de consumo pago), primeiro pelo simulador e depois pelo WhatsApp.

### 5.2w — WebChat e continuidade (só depois da aprovação)

1. `ChannelHandoff`, `role` de sessão (satélite) e `MessageAuthor.System` no Conversations, com as regras fixas do satélite.
2. Canal `WebChat` no Channels: configuração, rota por domínio, claim, sessão do navegador, métodos de recuperação, Receiver, Sender e marcas de despacho.
3. Portal do cliente (app React separado) e hub SignalR, com CSP e verificador em memória.
4. Continuidade no ContactCenter: entrada por continuação, `queue_order_at`, indicadores por cadeia, ocorrências e reconhecimento do satélite.
5. Extensão do encerramento para o WebChat: `SatelliteDecision` e `SatelliteAckThroughSeq` (usa a 9ª operação da 5.1b).
6. Convite real pela 5.2 (`5.2w-b`).
7. Razão de custos e indicadores.

**Pré-requisitos de cada fatia de implementação** (CLAUDE.md): plano com arquivos, testes e riscos; aprovação dos projetos `.csproj` novos (BOUNDARIES §10), de **migrações** e de **dependências novas**; nenhum commit, push ou migração aplicada sem autorização expressa.

## 6. Critérios de aceitação consolidados

Detalhados nos ADRs; aqui, a condição objetiva de "resolvido":

| Tema | Resolvido quando | ADR |
|---|---|---|
| **B1** | os dois índices existem e o antigo foi removido; testes concorrentes de chat (1 sessão) e de e-mail (N threads do mesmo remetente; mesma thread com 2 remetentes = 1 sessão) passam; **as cinco mutações M-1a a M-1e deixam vermelhos exatamente os testes indicados**; a suíte de sessão e as mutações da etapa 4 seguem verdes | 0012 |
| **B2** | estresse concorrente sem lacuna (sem expurgo); rollback e reutilização não consomem; expurgo não reemite; backfill idempotente e sem renumerar; a mutação "alocar antes de reconferir" deixa vermelho o teste de reutilização | 0012 |
| **C3** | anexo de outra sessão, de nota ou consumido é rejeitado; repetição devolve o resultado original mesmo após mudança de autorização; `OperationIdReused`; `AlreadyApplied(Overtaken)` sem efeito novo; token emitido com `Assign` pendente; intenção vencida resolve por cerca e consulta; autor original preservado; queda entre mensagem e evento prova que um não existe sem o outro | 0014 |
| **C3-cliente** | duplo clique ou repetição da mesma intenção, inclusive concorrente ou após queda, resulta em **1** intenção, **1** `OperationId` e **1** mensagem; a repetição devolve o resultado original mesmo após mudança de autorização; mesma chave com conteúdo diferente = `ClientRequestIdReused`; chave ausente rejeitada; nova chave é novo envio; as mutações listadas ficam vermelhas | 0014 |
| **C4 e C5** | as já aceitas pelo Codex continuam, mais os limites de notas, anexos, citações, notificações e prévia, cada um com teste | 0013, 0014 |
| **C6** | `Manual` nunca produz `Failed`, `Expired` nem `Dead`; busca sem resultado, tempo esgotado e declaração humana deixam `SendUnknown`; `AcceptDuplicateRisk` exige permissão, justificativa, idempotência e auditoria; `RequestResend` direto segue recusando `SendUnknown`; o Sender nunca retenta `SendUnknown` | 0011, 0014 |
| **C7** | referências completas: as 24 permutações de uma cadeia = 1 sessão, independentemente dos limites; cadeia truncada: 1 sessão **se** houver evidências suficientes e a cadeia for resolvida dentro dos limites; sem evidências suficientes ou com limites esgotados vale o G2 aprovado (sessões separadas, vínculo quando uma mensagem-ponte prova a relação, sem fusão automática e sem inferência por assunto ou remetente); sem identificadores = thread sintética, nunca chat; `Thread` sem chave rejeitado; indeterminável passa por `PendingAssociation` e quarentena; cursor perdido dispara ressincronização paginada que só se declara completa no fim da enumeração | 0012, 0015 |
| **Orçamento de SMS** | N reservas paralelas nunca ultrapassam o limite; reserva idempotente; `SendUnknown` mantém a reserva; falha definitiva a libera | 0015 |
| **`Down`** | recusa com a lista de conflitos quando há várias threads por contato, sem alterar nada | 0012 |
| **Encerramento (5.1b)** | AC-ENC01 a AC-ENC09: autorização (D.19), idempotência por `RequestId` e por `OperationId`, operação pendente `Close`, pré-condição imutável com idempotência antes da cerca, aplicação atômica com fronteira certificada, auditoria durável, superfície do gateway com o número de operações **implementadas** (oito no MVP; nove previstas, com a 8ª Proposta) e um emissor, só atendimento humano com item, mutações vermelhas | 0009 (emenda), 0016 |
| **IA mínima (6a)** | os 12 critérios do ADR 0017: contrato único contra o dublê e os dois adaptadores, seleção por tenant sem fallback, credenciais isoladas por tenant, teste Mauro/Pedro com IA, cercas, decisão validada, falhas, mídia (MED-2), consumo e limites, isolamento, arquitetura, suíte geral | 0017 |
| **WebChat (5.2w)** | AC-WC01 a AC-WC60: isolamento entre tenants, idempotência, concorrência, segurança do convite e da sessão, CSP e XSS, continuidade de fila e de atendente (preferência, nunca garantia), indicadores por cadeia, satélite (nunca promovido por omissão), recuperação por método com limite de histórico por garantia (`L0A` insuficiente para operação sensível), indisponibilidade de recuperação só alerta, protocolo de ativação idempotente | 0016 |
| **Geral** | suíte completa e mutações da etapa 4 verdes; nenhuma garantia aprovada enfraquecida; matriz e grafo de projetos inalterados (ARQ-06) | todos |

**Bloqueadores objetivos da etapa** (qualquer um impede aprovar):
1. nota interna alcança o Sender, o Outbox de canais ou o contexto de IA;
2. participante novo lê histórico anterior sem ação explícita e auditada;
3. qualquer leitura, escrita ou evento atravessa tenant;
4. webhook processado sem verificação de autenticidade, ou Receiver chamando outro módulo;
5. envio repetido automaticamente com resultado incerto;
6. mensagem associada a atendimento errado (por assunto ou endereço), ou número duplicado;
7. regressão em qualquer garantia da etapa 4 (`ScopeProof`, `PlacementEpoch`, `HandlingPeriod`, cerca, idempotência, obrigação de ressincronização);
8. segredo ou token em log, resposta HTTP ou front-end;
9. resposta de IA enviada fora do fluxo autorizado;
10. HTML ou anexo ativo executado ou exibido sem sanitização e controle;
11. convite de WebChat consumido por pré-visualização ou presente em log, histórico ou tabela;
12. vínculo entre canais concede histórico sem autorização;
13. dado sensível sai pelo WebChat sem a verificação exigida;
14. ciclo de convites ou resposta automática repetida;
15. item de continuação perde a posição ou é reivindicado durante a continuidade;
16. convite consumido reutilizável ou sessão do navegador duplicada;
17. encerramento aceito sem autorização, sem pré-condição ou com ocorrências do satélite não reconhecidas;
18. satélite promovido por omissão;
19. sessão válida revogada por apresentação de credencial antiga;
20. recuperação administrativa sem a verificação de identidade da política do tenant, que concede histórico anterior, ou que trata `L0A` como suficiente para operação sensível;
21. método de recuperação indisponível que encerra, cancela ou promove atendimento automaticamente.

## 7. Migrações propostas (nenhuma criada nem aplicada)

Todas aditivas em dados, com `Down` e plano de rollback; **só aplicadas com autorização expressa** (DP-07).

| # | Módulo | Migração | Fatia | Risco |
|---|---|---|---|---|
| 1 | Channels | generalizar identificador e rótulo; relaxar `ck_*_provider`; novos papéis de segredo; identificador opaco de webhook | 5.1a | baixo |
| 2 | Channels | `inbox_event`, `outbound_message`, `inbox_quarantine`, lanes | 5.1a | médio |
| 3 | Conversations | `tenant_counters` e `session_number` anulável | 5.1b | baixo |
| 4 | Conversations | **backfill** sem renumerar e `NOT NULL` | 5.1b | médio, **dado** |
| 5 | Conversations | `association_mode`, `thread_key`, `sender_contact_id`, dois índices, limites, `session_links` (`Down` condicional) | 5.1b | **médio-alto** |
| 6 | Conversations | `message_attachments`, `channel_data`, `outbound_operation_id`, `outbound_request_hash` | 5.1b | médio |
| 7 | Conversations | tipo e verificação de `Contact` | 5.1b | baixo |
| 8 | Channels | estado de caixa, índice de e-mail | 5.5 | baixo |
| 9 | Channels | orçamento, reserva, razão e supressão de SMS | 5.6 | baixo |
| 10 | ContactCenter | participantes, notas, anexos internos, notificações; `reply_intents` e `authz_version` (5.1b), com `client_request_id`, `client_payload_hash`, `request_hash`, `hash_version` e `content_snapshot` (o encerramento é a migração 14) | 5.1b e 5.7 | médio |
| 11 | Channels | valor `WebChat`, `webchat_config`, `webchat_claim`, `webchat_session`, gerações de credencial, `webchat_recovery_grant`, marcas de despacho, `cost_entry`, `price_table` | 5.2w | médio |
| 12 | Conversations | `channel_handoffs`, `role` e `continuity_handoff_id` na sessão (reaproveita o `last_inbound_seq` existente), `MessageAuthor.System`, motivo de encerramento | 5.2w | médio |
| 13 | ContactCenter | `queue_order_at` (com novo índice de ordenação), colunas de continuidade, `satellite_base_seq` e `satellite_read_from_seq` no item, `satellite_acknowledgements` (por satélite), marcador `delivered_through_seq`, `satellite_closed_through_seq`, atributo de fila "oferta de WebChat"; `ck_queue_items_pending` admite atendente-alvo em `Enter` | 5.2w | médio (altera a ordenação da fila) |
| 14 | ContactCenter | valor `Close` na operação pendente e em `ck_queue_items_pending`, recibo `Close` | 5.1b | baixo |
| 15 | AiGateway | esquema `ai_gateway`: configuração por tenant, credencial cifrada, `ai_requests`, contadores e limites (ADR 0017) | 6a | baixo (esquema novo) |
| 16 | Agents | esquema `agents`: definições e versões de instruções e FAQ, `ai_turns` (índice único parcial de turno não terminal por sessão), inbox do módulo (ADR 0017) | 6a | médio |

**Migrações do MVP (DP-07, planejamento aprovado em 03/11/2026, nenhuma aplicada):** 1 e 2 (5.1a); 3, 4, 14, a parte de chat da 5 e a versão reduzida da 6 (5.1b); a parte de `reply_intents` da 10 (5.1b); 15 e 16 (6a). **Fora do MVP:** 7, 8, 9, 11, 12, 13 e as partes de e-mail e de colaboração das migrações 5 e 10. Cada migração tem `Down` quando tecnicamente possível, plano de rollback e backfill documentado (o da numeração, nos dados de desenvolvimento, sem renumerar), e **cada aplicação futura exige autorização específica**.

## 8. Decisões que dependem do usuário

### 8.0 Decisões tomadas em 03/11/2026 (aprovação do MVP)

Decisões do decisor, **só documentais**: não autorizam código, projetos `.csproj`, pacotes, migrações, backfill, banco, commits nem push.

| ID | Decisão (03/11/2026) | Onde está registrada |
|---|---|---|
| **DP-01** | Aprovados para o escopo do MVP: ADR 0009 (operações existentes, 7ª `RegisterHumanReply` e 9ª `CloseSession`; **8ª `RequestDelegatedResend` Proposta e fora do MVP**), ADR 0010 (integral), ADR 0011 (depois dos ajustes de PRV-2 e REC-2), ADR 0012 (escopo de chat), ADR 0014 (7ª operação, `reply_intents`, `ClientRequestId`) e ADR 0016 (**somente** a seção M e AC-ENC01 a AC-ENC09). ADRs 0013 e 0015 e as demais partes do 0016 **permanecem Propostos**. AC-ENC07 distingue operações previstas (nove) das implementadas no MVP (oito). Aprovação parcial registrada de forma explícita no status de cada ADR | status dos ADRs 0009, 0010, 0011, 0012, 0014 e 0016 |
| **DP-02** | PRV-2 do WhatsApp encerrada: itens 1, 3, 5, 6, 7, 8, 9 e 10 confirmados; itens 2 e 4 não documentados pela Meta, com tratamento interno; item 11 parcial, mantendo a estratégia conservadora e `SendUnknown`. Alcance: leitura literal | ADR 0011, A.2 |
| **DP-03** | Conciliação do ADR 0011, A.1 aprovada; REC-1, REC-2, REC-3, REC-4 e REC-6 ajustadas; o segredo de assinatura pertence ao aplicativo da Meta | `BOUNDARIES.md` §12.2 (v0.4.7) e histórico |
| **DP-05** | Adiar armazenamento e processamento de anexos; preservar metadados e placeholders (o atendente vê o registro, não o arquivo); MED-2 (imagem, documento e vídeo: handoff com `ContentUnavailable`; áudio: resposta informativa determinística com texto do tenant); mídia não processada nunca entra no prompt | ADR 0011, G.2; ADR 0017, H |
| **DP-06** | Retenção inicial configurável: payload bruto 7 dias após `Done`; metadados do Inbox 30 dias; quarentena até resolução manual; mensagens mortas até reenfileiramento ou descarte autorizado; sujeita a validação operacional e às obrigações legais | ADR 0011, C.6 |
| **DP-07** | Planejamento das migrações por fatia, com `Down` quando possível, rollback e backfill documentado; backfill de numeração aprovado **documentalmente** nos dados de desenvolvimento, sem renumerar. **Nada executado**; cada aplicação exige autorização específica | seção 7; ADRs 0011 (I), 0012 (F) |
| **DP-08** | `ConfirmedSent` manual com permissão específica, justificativa e auditoria; só avança estado, nunca provoca reenvio automático nem duplicidade. Na interface, `SendUnknown` aparece como **envio de resultado incerto**, sem se confundir com falha definitiva nem com entrega confirmada | ADR 0011, F; ADR 0014, Pendências |
| **DP-09** | Novo atendimento vinculado ao anterior, sem reabrir automaticamente a sessão encerrada; a janela configurável fica para evolução posterior | ADR 0012, Pendências |
| **DP-11** | Planejamento de `IaBimeta.Adapters.WhatsApp` e do seu projeto de testes aprovado; o adaptador fake fica na infraestrutura de testes; a criação efetiva depende da autorização de implementação | ADR 0011, Pendências |
| **DP-12** | ADR 0017 criado como Proposto e **aceito em 03/11/2026**, com o escopo exato no status do ADR (etapa 6a em paralelo à 5; OpenAI e Anthropic; contrato comum `IAiProvider`; provedor e modelo por tenant; credenciais BYOK cifradas e isoladas por tenant; instruções e FAQ publicados; consumo, custo estimado, latência e erros; limites; turnos assíncronos; integração com o motor de turnos existente; handoff pelo ContactCenter; proteção contra resposta de IA depois de o humano assumir; sem fallback; sem Knowledge, Capabilities, Workflows nem ERP no MVP) | ADR 0017 |
| **DP-13** | Projetos mínimos da 6a **aprovados como planejamento** (AiGateway ×4, Agents ×3, adaptadores OpenAI e Anthropic, `ContactCenter.Contracts`, testes); **nenhum criado**, e a criação depende da autorização de implementação. Credenciais: solução mínima e isolada no AiGateway, **sem** extração do `AesGcmSecretProtector` para o `SharedKernel` (refatoração compartilhada, só como evolução futura se demonstrada) | ADR 0017, B e E |
| **DP-10** | **Parcial:** o `ARCHITECTURE.md` §11 (item 6) e o `BOUNDARIES.md` §1 e §12.12 receberam a divisão 6a/6b. O item 5 do `ARCHITECTURE.md` §11 e a linha da etapa 5 do `BOUNDARIES.md` §12.12 **não foram alterados** | `ARCHITECTURE.md` §11; `BOUNDARIES.md` v0.4.7 |

**Ainda pendente:** DP-04 (capacidades do canal no ContactCenter: necessária ao compositor da 5.1b e à 5.8), aprovação do restante dos ADRs 0013, 0015 e 0016 e a autorização de implementação de cada fatia.

**Lacuna de desenho a fechar no plano da 5.1b (não bloqueia o início da 5.1a, da 6a nem da interface):** o **caminho de execução do `ConfirmedSent` manual** (DP-08). Nenhum ADR especifica por qual módulo e por qual operação a ação autorizada do atendente chega a `RecordDeliveryStatus` com origem `Manual` (que exige contexto de sistema): a 7ª e a 9ª operações do gateway não a cobrem. Se a solução exigir operação privilegiada nova no gateway ou aresta nova na matriz, **precisará de ADR e de aprovação**; o plano da 5.1b deve propor a via mínima antes de implementar essa operação.

As seções 8.1 e 8.2 abaixo guardam a redação original das decisões (a coluna "recomendação" era minha, não uma decisão); onde a seção 8.0 decidiu, vale a 8.0.

### 8.1 Precisam ser decididas antes da 5.1a ou da 5.1b

| ID | Decisão | Recomendação | Bloqueia |
|---|---|---|---|
| **DP-01** | Aprovar os ADRs 0011 a 0016 e as emendas do ADR 0009 (7ª e 8ª operações; **9ª, `CloseSession` delegada**); **(redação original de 30/10; em 03/11/2026 o ADR 0010 foi aceito e o 0009 aceito parcialmente: ver 8.0)**, e as operações novas da porta só entram no código depois da aprovação do 0009 e do ADR que as detalha (0014 e 0016) | revisar e aprovar, de preferência com nova auditoria independente dos pontos novos | 5.1a, 5.1b e 5.2w |
| **DP-02** | **PRV-2**: autorizar a confirmação dos parâmetros na documentação oficial (acesso externo) e decidir o alcance do gate: **literal** (confirmar os itens do WhatsApp antes da 5.1a; os demais por adaptador) ou **por adaptador** (emendar a PRV-2) | leitura literal; confirmar o WhatsApp antes da 5.1a | 5.1a |
| **DP-03** | **REC-2**: aprovar a conciliação do ADR 0011, A.1 e o ajuste de texto da REC-1 e da REC-2 no `BOUNDARIES.md` §12.2 | aprovar | 5.1a |
| **DP-04** | Capacidades do canal no ContactCenter: **porta do host** (sem aresta nova) ou aresta `ContactCenter → Channels` por ADR | porta do host, como no ADR 0008 | 5.1b (compositor) e 5.8 |
| **DP-05** | Armazenamento e varredura de anexos; corpo grande de e-mail; limites e tipos; **bibliotecas novas** (sanitização, varredura) | definir antes da 5.1b | 5.1b, 5.5 |
| **DP-06** | Retenção mínima do Inbox e do payload bruto (e, adiante, de notas, anexos e e-mail), à luz da LGPD | política mínima por tipo antes de dado real | 5.1a; 5.5 |
| **DP-07** | Aprovar as migrações da seção 7, **inclusive o backfill** do número (dado de desenvolvimento) | aprovar cada migração na fatia, o backfill agora | 5.1a e 5.1b |
| **DP-08** | `ConfirmedSent` manual: permitido, com origem `Manual`, justificativa e permissão (só avança estado, nunca habilita duplicidade) | permitir | 5.1b |
| **DP-09** | Reabertura de atendimento ou thread encerrados | atendimento novo ligado ao anterior, com janela configurável | 5.1b |
| **DP-10** | **Reconciliar o roadmap**: `ARCHITECTURE.md` §11, item 5 ("WhatsApp e webhooks com testes seguros"), e `BOUNDARIES.md` §12.12, linha da etapa 5, descrevem escopo menor. O cabeçalho do ADR 0003 **já** registra que o Worker foi para a etapa 4 | atualizar os dois **depois** da aprovação deste plano, com uma linha no histórico do `BOUNDARIES.md` | nenhuma fatia, mas evita documentos conflitantes |
| **DP-11** | Aprovar a criação dos projetos `.csproj` novos (por exemplo, `IaBimeta.Adapters.WhatsApp` e o adaptador fake de teste) e a ordem, por fatia | pedir na abertura de cada fatia (BOUNDARIES §10) | 5.1a |

### 8.2 Podem permanecer pendentes até a fatia

| Decisão | Fatia |
|---|---|
| Provedor de SMS (Twilio, Infobip, Zenvia ou outro) | 5.6 |
| Tempo real da interface (push ou polling) | 5.8 |
| Reenvio aos destinatários recusados no aceite parcial de e-mail | 5.5 |
| Suspensão das concessões de colaborador na mudança de colocação (padrão proposto: suspender) | 5.7 |
| Biblioteca compartilhada dos adaptadores Meta | 5.2 |
| Horizonte de espera do `SendUnknown` até exigir ação humana (padrão conservador, configurável por canal) | 5.1a |
| Reter ou descartar eventos de canal suspenso | 5.1a |
| Nomes definitivos das permissões novas | implementação |
| Valores padrão dos limites da associação adiada de e-mail (tempo, tentativas, profundidade) e capacidade de busca por identificador de cada adaptador | 5.5 |
| Fusão manual de sessões de e-mail | futura |
| Retenção por tipo de dado, além do Inbox | antes de dado real na 5.5 |
| Valores padrão do convite, do claim, da recuperação e da rotação de credencial (validade, usos, `max_offers`, intervalo entre ofertas, limites, limiares de alerta) | 5.2w |

### 8.3 Decisões do WebChat (ADR 0016)

A proposta do WebChat foi aprovada em bloco e condicionada aos ajustes; por isso as decisões sem aprovação individual aparecem como "depende de confirmação individual", e não como rejeitadas. **A aprovação conceitual não autoriza implementação.** Os ADRs seguem **Propostos**, e a implementação de cada fatia depende da aprovação formal (DP-01) e do plano da própria fatia.

| ID | Decisão | Situação |
|---|---|---|
| DP-WC01 | Continuidade por sessões distintas, origem encerrada na ativação, cadeia por `root_session_id` | direção aprovada, a formalizar |
| DP-WC02 | Número público do atendimento = número da sessão raiz | direção aprovada, a formalizar |
| DP-WC03 | `MessageAuthor.System` (enum aditivo) | depende de confirmação individual |
| DP-WC04 | Convite iniciado por atendente fora do MVP | depende de confirmação individual |
| DP-WC05 | Gatilhos do MVP: direcionada (Channels) e seletiva por fila e horário (ContactCenter); assunto, tipo e regra comercial não suportados | depende de confirmação individual |
| DP-WC06 | Políticas do WhatsApp durante o WebChat: `KeepWebChatActive` (padrão), `ReturnOnExplicitRequest`, `ReturnOnCustomerMessage` | aprovada conceitualmente |
| DP-WC07 | Convite como capacidade assinada no fragmento, com claim idempotente | direção aprovada; valores na 5.2w |
| DP-WC08 | SignalR no portal; marcas de despacho e polling Worker → hub | depende de confirmação individual |
| DP-WC09 | Portal como app React separado; PWA opcional; subdomínio da plataforma | depende de confirmação individual |
| DP-WC10 | Níveis de garantia (L0, L0A, L1, L2) e bloqueio de dado sensível; `L0A` insuficiente isoladamente | depende de confirmação individual; o ajuste do `L0A` foi aprovado conceitualmente |
| DP-WC11 | Razão genérico de custos e metodologia da economia efetiva | depende de confirmação individual |
| DP-WC12 | **PRV-2-WC** (política da Meta para convite e retorno; Web Push) | pendente; exige autorização de acesso externo |
| DP-WC13 | Recuperação padrão pelo WhatsApp | superada pela DP-WC26 |
| DP-WC14 | Retenção de sessões, transcrição e dados do WebChat (estende a DP-06) | depende de confirmação individual |
| DP-WC15 | Subetapa `5.2w`, com `5.2w-a` e `5.2w-b` | direção aprovada (nome usado pelo decisor) |
| DP-WC16 | Histórico da origem para o atendente da sessão destino: só pela autorização normal | depende de confirmação individual |
| DP-WC17 | Encerramento como caso de uso do ContactCenter e 9ª operação do gateway, na **5.1b** | aprovada conceitualmente; **depende da emenda ao ADR 0009** |
| DP-WC18 | Lacunas na sequência dos números públicos | depende de confirmação individual |
| DP-WC19 | Satélite: nunca promovido por omissão; reconhecimento antes do encerramento | aprovada conceitualmente |
| DP-WC20 | Um dispositivo por handoff no MVP | depende de confirmação individual |
| DP-WC21 | Valores a definir na 5.2w | pendente |
| DP-WC22 | Link de conversa do WhatsApp na PRV-2-WC | depende de confirmação individual |
| DP-WC23 | `queue_order_at` e continuidade de fila | direção aprovada; a migração segue na DP-07 |
| DP-WC24 | Políticas de retorno são autorizações explícitas de configuração do tenant | aprovada conceitualmente |
| DP-WC25 | Permissão própria e justificativa para `PromoteSatellite` | aprovada conceitualmente |
| DP-WC26 | Recuperação no MVP: WhatsApp e administrativa condicionada à política do tenant e à verificação de identidade | aprovada conceitualmente |
| DP-WC27 | Credencial antiga: recusar e auditar, sem revogar a sessão válida | aprovada conceitualmente |
| DP-WC28 | Verificador em memória; CSP e Trusted Types; endpoint de relatório | aprovada conceitualmente |
| DP-WC29 | Valores da 5.2w (claim, recuperação, rotação, limiares) | aprovada conceitualmente; valores na fatia |

## 9. Gates técnicos, dependências externas e aprovações operacionais

Há duas naturezas de condição, que não se confundem.

**Gates técnicos de arquitetura** (`BOUNDARIES.md` e ADR 0008) condicionam o **início da implementação** do `Channels`. **REC-2 e PRV-2 (WhatsApp) foram cumpridos em 03/11/2026** (seção 3); a PRV-2-WC e a PRV-2 dos demais canais **não**. **Adaptador fake não dispensa gate**: ele valida a mecânica, mas o Receiver e o Sender fazem parte do `Channels`, e o que implementam (assinatura, limites, retentativas, janela, idempotência) é o que a PRV-2 manda confirmar. Só uma confirmação na documentação oficial ou uma **emenda formal** aprovada pelo decisor (DP-02 e DP-03) altera isso.

**Credenciais, homologações e aprovações operacionais** não são gates de arquitetura. Não bloqueiam o desenvolvimento técnico contra adaptadores fake e payloads sintéticos **depois de cumpridos os gates técnicos aplicáveis**, e bloqueiam a **habilitação operacional** (tráfego real). A conclusão técnica de cada canal e a habilitação dele são registradas separadamente.

### 9.1 Gates técnicos

| Gate | Situação (atualizada em 03/11/2026) | Bloqueia | Decisão |
|---|---|---|---|
| **REC-2** | **cumprido** em 03/11/2026: conciliação do ADR 0011, A.1 aprovada; `BOUNDARIES.md` §12.2 ajustado (v0.4.7) | nada para o MVP | DP-03 |
| **PRV-2** | **cumprido para o WhatsApp** em 03/11/2026 (ADR 0011, A.2: oito itens confirmados, dois não documentados, um parcial); **não cumprido para os demais canais** | cada adaptador das fatias 5.3 a 5.6, quanto aos itens do seu canal | DP-02 |
| **PRV-2-WC** | **não confirmado; não cumprido** (ADR 0016, J.5: política da Meta para convite com link e retorno, templates, janela e custo; link de conversa do WhatsApp; Web Push em Android e iPhone); fora do MVP | a **5.2w-b** (convite real), **não** a 5.2w-a | DP-WC12 |

A 5.1b não é bloqueada por estes gates, porque só toca `Conversations` e `ContactCenter`.

### 9.2 Credenciais, homologações e aprovações operacionais

| Item | Dependência | Situação |
|---|---|---|
| WhatsApp | verificação da empresa, conta WABA, números, aprovação de templates | não obtida |
| Instagram Direct e Messenger | app Meta com permissões de mensagens, App Review, etiqueta *Human Agent* com revisão própria | não obtida |
| Microsoft 365 | registro de aplicativo (Entra ID), consentimento de administrador, permissões de correio | não obtida |
| Gmail | projeto Google Cloud, Pub/Sub, verificação OAuth para escopos restritos | não obtida |
| IMAP/SMTP | OAuth no Exchange Online; a descontinuação da autenticação básica do SMTP deve ser confirmada em fonte oficial | não obtida |
| SMS | contrato com provedor, remetentes e rotas, regras da Anatel e das operadoras, supressão e saída | não definido |
| E-mail enviado | SPF, DKIM e DMARC do domínio do tenant | responsabilidade do tenant, a documentar |
| Provedores de IA (6a) | chaves BYOK de cada tenant (e do decisor, para o teste manual, com autorização expressa de consumo pago); verificação jurídica das políticas de uso e de terceiros da OpenAI, da Anthropic e da Meta (ADR 0017, Pendências); aviso de IA ao cliente | não obtida; **não bloqueia** a versão para testes |
| URL pública HTTPS do webhook (teste com a Meta) | túnel ou hospedagem com certificado válido; a Meta não aceita certificado autoassinado (ADR 0011, A.2, item 3). Instalar ou contratar exige autorização | a decidir |
| WebChat | DNS e certificado do subdomínio do WebChat; aprovação de templates da Meta para convite e retorno; chaves VAPID (Web Push, opcional) | não obtida |
| Recuperação administrativa do WebChat | política e procedimento de verificação de identidade do cliente definidos pelo tenant (lista do `StaffChecklist`, segundo aprovador, canais de entrega); provedor de identidade do tenant (método futuro) | a definir pelo tenant |

Os valores técnicos desses provedores (janelas, limites, expirações) são **indicativos** e entram na PRV-2 estendida (ADR 0011, A.2); não são condições operacionais.

## 10. O que a fase 5.0 não fez

Nenhum código de produção ou de teste, projeto, pacote, migração, backfill, banco, build ou teste foi tocado, nem na 5.0 nem na consolidação documental de 03/11/2026. Nenhuma implementação (5.1a, 5.1b, 6a ou outra) foi iniciada, e nada foi commitado.

**Consolidação documental de 03/11/2026** (autorizada pelo decisor): alterados apenas o `BOUNDARIES.md` (v0.4.7), o `ARCHITECTURE.md` (§11, item 6, faseamento 6a/6b), os ADRs 0009, 0010, 0011, 0012, 0014 e 0016 (status e escopo de cada aprovação), este documento, e criado o ADR 0017 (Proposto e aceito no mesmo dia: ver §8.0, DP-12 e DP-13). **Não foram alterados:** a matriz `module-dependencies.json`, o ADR 0008, os ADRs 0013 e 0015, o `CONVERSATION-LIFECYCLE.md`, o `CLAUDE.md` e o código. Houve acesso externo, **somente leitura**, à documentação oficial da Meta (03/11/2026, ADR 0011, A.2), da OpenAI e da Anthropic (03/11/2026, ADR 0017, Anexo A), sem chave de API, sem conta e sem consumo pago.

## 11. Escopo do MVP para testes e marcos de planejamento (03/11/2026)

**Objetivo:** primeira versão operacional **para testes** até **15/02/2027** (meta de planejamento, não compromisso de entrega): WhatsApp oficial da Meta, atendimento inicial por IA com **OpenAI e Anthropic**, transferência para humano e interface React. WebChat, Instagram, Messenger, e-mail e SMS ficam fora.

**Fluxo obrigatório:** Cliente → WhatsApp → IA → resolução automática ou transferência a humano → React (visualizar, assumir, responder, transferir, encerrar) → encerramento.

**Trilhas** (dependências reais e paralelismo sem conflito de banco de testes e de contratos):

| Trilha | Conteúdo | Depende de |
|---|---|---|
| **A. Núcleo Conversations/ContactCenter** | subconjunto da 5.1b: limites, parte de chat do índice e da migração 5, numeração, `ChannelData` reduzido, `RegisterHumanReply` e `reply_intents`, `ClientRequestId`, limites de leitura, encerramento (9ª operação), `ConfirmedSent` manual | DP-01, 05, 07, 08, 09 (decididos) e autorização de implementação |
| **B. Channels e WhatsApp** | 5.1a (migrações 1 e 2, Receiver, Inbox, ingestão por lane com adaptador fake, Sender) e depois o adaptador WhatsApp (5.2) com o número de teste da Meta | REC-2 e PRV-2 (cumpridos), DP-06, DP-11; para o teste real, URL pública HTTPS |
| **C. Agents e AiGateway** | 6a (ADR 0017), com OpenAI e Anthropic; começa contra o simulador de desenvolvimento | plano da fatia (C1 e C2) e autorização de implementação; DP-12 e DP-13 já aprovados (ADR 0017 aceito em 03/11/2026) |
| **D. Interface React** | login, fila, conversa, assumir, transferir, devolver à IA, resposta, encerramento, `SendUnknown` como envio de resultado incerto e configuração de IA; depois de 5.8 mínima | endpoints existentes; resposta e encerramento esperam a trilha A; dependências npm exigem aprovação |

**Marcos de planejamento** (reavaliar somente com evidência técnica concreta de inviabilidade):

| Data | Marco |
|---|---|
| **18/12/2026** | IA real respondendo pelo simulador |
| **22/01/2027** | primeiro teste ponta a ponta WhatsApp + IA |
| **15/02/2027** | MVP para testes, com React e atendimento humano |

## Histórico de alterações

| Data | Versão | Alteração | Autor |
|---|---|---|---|
| 30/10/2026 | 0.1 | Criação do plano consolidado da fase 5.0 (documental): gates, dependências, ordem, critérios, migrações e decisões pendentes | Mauro Porpino (decisor); redação assistida |
| 03/11/2026 | 0.2 | Encerramento documental da 5.0: seção 9 separa gates técnicos (PRV-2 e REC-2, não cumpridos) de aprovações operacionais; critério C7 qualificado pela decisão G2 do proprietário; critério C3-cliente (chave de idempotência do cliente) na 5.1b; migração 10 e limites da associação adiada. Revisões correspondentes nos ADRs 0012 e 0014. Estados inalterados: tudo Proposto | Mauro Porpino (decisor); redação assistida |
| 03/11/2026 | 0.3 | WebChat nativo (ADR 0016) e subetapa 5.2w; encerramento do atendimento na 5.1b com a 9ª operação do gateway (segunda emenda Proposta ao ADR 0009); PRV-2-WC; decisões DP-WC; critérios AC-ENC e AC-WC; bloqueadores 11 a 21; migrações 11 a 14. Estados inalterados: tudo Proposto; PRV-2, PRV-2-WC e REC-2 pendentes | Mauro Porpino (decisor); redação assistida |
| 03/11/2026 | 0.3.1 | Correções da auditoria independente (F01, F02, I01, R01 a R06): encerramento direto e satélite, identidade de evento do WebChat, coluna existente, limites de leitura do satélite, ingestão vinculada, revalidação na ativação (limitação de concorrência entre módulos reconhecida), recibo da recusa de encerramento e reserva de capacidade na continuidade. Estados inalterados: tudo Proposto; PRV-2, PRV-2-WC e REC-2 pendentes | Mauro Porpino (decisor); redação assistida |
| 03/11/2026 | 0.4 | Consolidação documental do MVP (autorizada pelo decisor): PRV-2 do WhatsApp e REC-2 encerradas (ADR 0011, A.1 e A.2; `BOUNDARIES.md` v0.4.7); aprovação **parcial** dos ADRs 0009, 0011, 0012, 0014 e 0016 e integral do 0010, com o escopo de cada uma no status; 8ª operação e ADRs 0013 e 0015 permanecem Propostos; decisões DP-01 a DP-03, DP-05 a DP-09, DP-11 a DP-13 (seção 8.0); linha da etapa 6a, migrações 15 e 16, critérios e gates; escopo do MVP, trilhas e marcos (seção 11); ADR 0017 criado como Proposto; `ARCHITECTURE.md` §11, item 6. Nada implementado | Mauro Porpino (decisor); redação assistida |
| 03/11/2026 | 0.4.1 | Aprovação arquitetural do ADR 0017 (DP-12 e DP-13): estado **Aceito**, com o escopo exato no status do ADR; os projetos da 6a ficam aprovados como **planejamento**, sem criação. Referências atualizadas: seção 2, linha da 6a (seção 4), seção 8.0, seção 10 e seção 11. Nada implementado | Mauro Porpino (decisor); redação assistida |
