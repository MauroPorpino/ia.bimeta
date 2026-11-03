# ADR 0015 — E-mail e SMS nativos no Channels: sincronização, segurança, supressão e orçamento

- **Status:** Proposto (30/10/2026). Aguarda a aprovação do decisor. Nada deste ADR está implementado, e nenhum outro documento o trata como aceito.
- **Data:** 30/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` v0.4.6 (seções 4, 7 e 12), `docs/architecture/ETAPA-5.md`, ADRs 0006, 0007, 0011, 0012 e 0014, `ARCHITECTURE.md` (§9), `CLAUDE.md`
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**), os ADRs 0001 a 0010 e a regra de que e-mail e SMS são **canais do mesmo atendimento**, sem central paralela de tickets.
- **Registra:** dois **novos tipos de adaptador** (`IaBimeta.Adapters.*`, `BOUNDARIES.md` §11), o estado de sincronização de caixa, a reserva de orçamento de SMS e a lista de supressão, todos dentro do `Channels`.
- **Dependências externas:** todos os valores de provedores citados são **indicativos e não confirmados**; passam pelo gate PRV-2 estendido (ADR 0011, A.2) antes de qualquer implementação do adaptador correspondente.

## Contexto

E-mail e SMS entram na etapa 5 como canais nativos, ao lado de WhatsApp, Instagram e Messenger. Diferem dos canais de chat em pontos que o núcleo precisa absorver sem que o `Conversations` conheça provedor:

- **E-mail** é por **sincronização** (pull) em três provedores (Microsoft Graph, Gmail API, IMAP/SMTP), tem **thread por cabeçalhos** (ADR 0012), corpo grande, anexos, vários destinatários (To, Cc, Bcc só em envio) e **ambiguidade de envio** em alguns provedores.
- **SMS** tem **custo por segmento**, **lista de supressão e consentimento** mantida pela empresa, remetente e rota (unidirecional ou bidirecional) e segmentação (GSM-7 ou UCS-2).

## Decisão

### A. E-mail

#### A.1 Provedores e entidades

1. **Três adaptadores distintos de um mesmo contrato de e-mail** (`IEmailProvider`): Graph, Gmail API e IMAP/SMTP. Cada um declara capacidades e a estratégia de efeito de envio (EFX-2).
2. **Caixa** = uma instância de canal com provedor de e-mail: endereço, tipo de autenticação, escopo contratado de sincronização, estado de sincronização e fila de destino padrão. Várias caixas por tenant, com fila por caixa.
3. **Credenciais** OAuth guardadas como `ChannelSecret` (novos papéis), com a mesma proteção AES-GCM e a mesma rotação; nunca em log, resposta ou front-end.
4. **Detalhes por mensagem** (assunto, remetente, To, Cc, Bcc só em envio, cabeçalhos): bloco opaco `ChannelData`, validado pelo adaptador. **O corpo grande** (HTML sanitizado e texto) vai para armazenamento de conteúdo (DP-05), porque o limite atual de 4096 caracteres de `Message.Text` não o comporta.
5. **Associação à sessão** por caixa e thread: ADR 0012 (seção D). Aqui valem a lane por **caixa** e a serialização por `pg_advisory_xact_lock`.

#### A.2 Sincronização, cursor e recuperação

```text
MailboxSyncState(Scope, Cursor, State: Synced | ResyncRequired | Resyncing, Progress)
```

- **Cursor por caixa e pasta ou rótulo:** `delta` do Graph, `historyId` do Gmail, `UIDVALIDITY + UID` do IMAP.
- **Escopo contratado** (`mailbox_sync_scope`): pastas ou rótulos e a profundidade escolhida no cadastro (por exemplo, "desde uma data" ou "tudo").
- **Avanço do cursor na mesma transação** que grava os eventos de Inbox da página. A mensagem que **não normaliza** vai para `channels.inbox_quarantine`, com motivo e alerta, **sem travar o cursor**. A entrega é pelo menos uma vez, com deduplicação por `dedupe_key` (identificador do provedor mais a caixa).
- **Perda de continuidade** (`historyId` expirado, retomada obrigatória do delta, `UIDVALIDITY` alterado) → `ResyncRequired`. A **ressincronização paginada** percorre o escopo contratado **inteiro**:
  1. obtém **primeiro** o novo cursor (`delta`, `historyId` ou `UIDNEXT`);
  2. enumera cada pasta ou rótulo até o **fim**, guardando o progresso (`pageToken` ou último UID);
  3. retoma o delta a partir do cursor obtido no passo 1.
- **Completude = fim da enumeração + retomada sem lacuna**, nunca "uma janela de N horas". Uma janela curta só serve como **otimização** quando o provedor garante a validade do cursor, e jamais como prova de completude. A reconciliação **nunca apaga** o que já existe.
- Ao concluir, grava-se um evento de auditoria com contagens (vistas, novas, duplicadas, em quarentena).
- **Notificação é acelerador, não fonte de verdade.** O polling periódico fica **sempre ligado**. A renovação das assinaturas (Graph, no máximo 4230 minutos; Gmail `watch`, no máximo 7 dias; ambos indicativos) é agendada com folga, e a renovação perdida é detectada pelo polling.
- **Itens parciais em lote** (Graph, SMTP com aceite parcial): tratamento por item, idempotente.

#### A.3 Envio de e-mail

- **Funcionalidades obrigatórias:** responder, responder a todos, encaminhar e e-mail novo; anexos nos dois sentidos; preservar remetente, destinatários, Cc, Bcc, assunto e encadeamento; autoria, estado e falhas registrados; comunicação interna no atendimento (ADR 0013).
- **Controle:** To, Cc e Bcc controlados; a conta remetente é autorizada por caixa e por usuário.
- **Identificador próprio.** O `Message-ID` é **gerado por nós**, para permitir a reconciliação (estratégia (b)) pela busca na pasta de enviados. No Graph, como o `sendMail` não devolveria identificador (indicativo), a mitigação proposta é **criar o rascunho, guardar o identificador e enviar o rascunho**, a confirmar na PRV-2.
- **Resultado incerto** é `SendUnknown` e **nunca** é reenviado automaticamente (ADR 0011, E e F).
- **Aceite parcial:** a mensagem fica `Sent` com o detalhe **por destinatário** em `ChannelData`, e a interface avisa. O reenvio aos recusados é decisão da 5.5.
- **SPF, DKIM e DMARC** do domínio do tenant são **responsabilidade do tenant**, a documentar.

#### A.4 Segurança do conteúdo

- **HTML sanitizado por lista de permissões**; conteúdo remoto bloqueado por padrão; HTML ou anexo ativo **nunca** é executado nem exibido sem sanitização e controle (**bloqueador 10**).
- **Anexos** com limite de tamanho, tipo verificado e varredura (DP-05); respostas automáticas e mensagens duplicadas tratadas.
- **Conteúdo recebido é dado, não instrução** (`ARCHITECTURE.md` §9): proteção contra injeção de comando em qualquer etapa que alcance IA.
- Bibliotecas de sanitização e de varredura são **dependências novas** e exigem **aprovação** (CLAUDE.md).

#### A.5 Fatos externos indicativos (PRV-2 estendido)

Graph: assinaturas de mensagens de no máximo 4230 minutos; `sendMail` com 202 sem identificador (fonte secundária). Gmail: `watch` de no máximo 7 dias, sincronização por `history.list`, verificação OAuth para escopos restritos (**não confirmada**). IMAP/SMTP: autenticação básica já desativada para IMAP e POP no Exchange Online, e a do SMTP AUTH programada, segundo fonte secundária, para o fim de dezembro de 2026; OAuth é o caminho para Microsoft 365. **Nenhum está confirmado** em documentação oficial; fontes e o que falta estão no ADR 0011, A.2.

### B. SMS

#### B.1 Provedor, remetente e rota

1. **Provedor não escolhido.** Twilio, Infobip e Zenvia são candidatos; a escolha depende de cobertura no Brasil, custo, número longo ou curto com suporte a resposta, autenticação e condições comerciais. **Decisão da 5.6** (não bloqueia as demais). O adaptador fica atrás de `ISmsProvider`.
2. **Remetente e conta** por tenant, com **tipo de rota** (unidirecional ou bidirecional) e **modalidade** (transacional, promocional, atendimento). **Falha de rota unidirecional é capacidade declarada, não erro.** Resposta (MO) só quando a rota permitir (indicativo, não confirmado por rota).
3. Número de telefone **não prova identidade**; não se associa a cliente por telefone não verificado (`ARCHITECTURE.md` §4.3).

#### B.2 Supressão e consentimento

- No Brasil, segundo fonte de fornecedor (não confirmada), **não há cadastro central de "não perturbe"**: a empresa mantém a **própria lista de supressão** e atende pedidos de saída como "SAIR" e "PARE". Há diretório centralizado de códigos curtos (ABR Telecom).
- `channels.suppression_entry`: lista por tenant e endereço, com origem e data, consultada pelo Sender **antes de cada envio**. Envio a endereço suprimido é recusado e auditado.

#### B.3 Orçamento de SMS: reserva concorrente

```text
channels.sms_budget(tenant_id, period, limit, spent, reserved)
channels.sms_reservation(operation_id UNIQUE, amount, state: Reserved | Committed | Released)
channels.sms_usage(...)            -- razão de custos: segmentos, custo estimado e confirmado, por tenant e período
```

```sql
UPDATE sms_budget SET reserved = reserved + @estimado
 WHERE tenant_id = @t AND period = @p AND spent + reserved + @estimado <= limit
RETURNING 1;                       -- 0 linhas = BudgetExceeded
```

- A reserva é **idempotente** por `operation_id`: reservar de novo é **sem efeito**. O mesmo `operation_id` do envio (ADR 0011, E).
- O **segmento é calculado no servidor** (GSM-7 ou UCS-2), com estimativa de custo no compositor.
- **Confirmação** com o custo real do provedor: `reserved -= estimado; spent += real`, com `overrun` registrado se passar da estimativa.
- **Falha definitiva ou expiração:** a reserva é **liberada**.
- **`SendUnknown`:** a reserva **continua** até a resolução (conservador).
- Limite configurável por tenant, com recusa ao exceder. A política exata além da recusa (alerta, aprovação) é decisão da 5.6.

#### B.4 Entrega incremental e dependências

- **5.6a, envio transacional:** depende **só da 5.1a**. É um fluxo do `Channels` (permissão própria, **sem sessão**), com orçamento, supressão, status e auditoria; sem inbound.
- **SMS de atendimento com resposta humana** e a **5.6b** (recebimento e bidirecional, associação por instância e contato) dependem da **5.1b**.

### C. Migrações propostas (nenhuma criada nem aplicada)

| # | Migração | Risco |
|---|---|---|
| 8 | estado de caixa (`MailboxSyncState`, `mailbox_sync_scope`), índice de mensagens de e-mail e `inbox_quarantine` (se não nasceu na 2) | baixo |
| 9 | `sms_budget`, `sms_reservation`, `sms_usage`, `suppression_entry` | baixo |

Aditivas, com `Down`; aplicação só com autorização expressa (DP-07).

## Alternativas descartadas

- **Central de tickets de e-mail paralela.** Contraria a decisão de atendimento unificado.
- **Janela de N horas como prova de completude da sincronização.** Perde mensagens quando o cursor expira.
- **Associar e-mail por assunto ou endereço.** Associa ao atendimento errado (ADR 0012).
- **Reenviar e-mail ou SMS ambíguo automaticamente.** Duplica efeito externo (EFX-3).
- **Checar o orçamento por leitura e depois gravar.** Corrida: reservas paralelas ultrapassariam o limite.
- **Escolher o provedor de SMS agora.** Falta cobertura, custo e condições comparáveis; não bloqueia as demais fatias.

## Consequências

- O Inbox e a fila de envio ganham estados e tabelas sem mecanismo paralelo.
- O e-mail depende de **três** adaptadores com ciclos de vida de credencial e de assinatura diferentes; cada um fecha sozinho (a entrega é incremental por provedor).
- O orçamento de SMS é uma **linha quente por tenant e período**, de curta duração; a medição dirá se exige outra estratégia.
- A segurança de conteúdo e de anexos traz **dependências novas**, sujeitas a aprovação.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Duplicidade ou perda na sincronização | `dedupe_key`; cursor na mesma transação; ressincronização paginada; polling sempre ligado |
| Renovação silenciosa de assinatura (Gmail) ou expiração (Graph) | renovação com folga e alerta; polling detecta a perda |
| Ambiguidade de envio (Graph sem identificador) | rascunho com identificador antes de enviar; `Message-ID` próprio; `SendUnknown` |
| HTML ou anexo ativo | sanitização por lista de permissões; conteúdo remoto bloqueado; varredura; **bloqueador 10** |
| SMS acima do orçamento por concorrência | reserva atômica idempotente por `operation_id` |
| SMS a quem pediu para sair | supressão consultada antes de cada envio; auditoria |
| Valores de provedores desatualizados | PRV-1 (configuração) e PRV-2 (confirmação com data e fonte) |

## Critérios de aceitação

**C7 — sincronização:**
- cursor perdido dispara ressincronização paginada que **só se declara completa** no fim da enumeração com retomada sem lacuna; a mutação "janela é completude" deixa o teste vermelho;
- mensagem inválida vai para quarentena **sem travar o cursor**; mensagem duplicada é inofensiva; o polling cobre a assinatura perdida;
- itens parciais em lote são idempotentes; a reconciliação nunca apaga o existente; o evento de auditoria traz as contagens.

**E-mail (5.5):** preservação de remetente, destinatários, Cc e Bcc; encadeamento por `Message-ID`, `In-Reply-To` e `References` (ADR 0012); aceite parcial com detalhe por destinatário; resultado incerto vira `SendUnknown` sem reenvio automático; sanitização de HTML e segurança de anexos; múltiplas caixas por tenant com fila por caixa.

**Orçamento de SMS (5.6a):** N reservas paralelas **nunca** ultrapassam o limite; reservar de novo a mesma operação não duplica; `SendUnknown` mantém a reserva; falha definitiva a libera; endereço suprimido é recusado e auditado; segmentação e estimativa corretas para GSM-7 e UCS-2.

**SMS bidirecional (5.6b):** restrições de rota respeitadas (rota unidirecional é capacidade, não erro); associação por instância e contato.

**Geral:** cada canal fecha sozinho, com adaptador fake e payloads sintéticos, **sem credencial real**; a habilitação operacional é registrada **separadamente** da conclusão técnica; suíte completa e mutações da etapa 4 verdes.

## Pendências

- **DP-05** (armazenamento e varredura de anexos, corpo grande, bibliotecas) e **DP-06** (retenção, **antes de dado real na 5.5**) dependem do decisor.
- **Podem permanecer para a fatia:** provedor de SMS (5.6); reenvio aos recusados no aceite parcial (5.5); fusão manual de sessões de e-mail (futura).
- **Habilitação operacional** (fora do desenvolvimento técnico): registro de aplicativo e consentimento de administrador na Microsoft; projeto Google Cloud, Pub/Sub e verificação OAuth para escopos restritos; contrato, remetentes e rotas de SMS; SPF, DKIM e DMARC do tenant.
