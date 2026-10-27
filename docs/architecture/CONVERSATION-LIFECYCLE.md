# Ciclo de vida de mensagens e turnos de IA (CONVERSATION-LIFECYCLE.md)

- **Status:** Aprovado (v1.0)
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Formalizado por:** ADR 0007
- **Complementa:** `BOUNDARIES.md` (seções 6 e 12) e `ARCHITECTURE.md` (seção 4.1). Não altera a matriz de dependências.
- **Valores numéricos:** são referência inicial, todos configuráveis e sujeitos a calibração com dados reais.

**Princípios:**

1. O Conversations é a autoridade sobre o estado da conversa e sobre a aceitação da resposta. O Agents propõe, e não decide.
2. Resposta defasada é **sempre rejeitada** pelo Conversations. É uma regra fixa do MVP, **sem chave de configuração**. Qualquer exceção é evolução futura documentada (seção 6.3) e depende de novo ADR.
3. A correção depende **somente das cercas transacionais do Conversations**. O cancelamento físico de uma chamada de IA é apenas otimização de custo e latência, e a declaração do Agents nunca é a base de uma decisão de aceite.
4. Há um só turno **logicamente ativo** por sessão.
5. Cobertura lógica da resposta não é entrega.
6. Conteúdo indisponível nunca chega ao modelo.
7. Efeito externo segue a EFX do `BOUNDARIES.md` §12.7: idempotência comprovada ou reconciliação, e nunca reenvio automático de resultado ambíguo.
8. A separação entre Conversations, Agents, Channels e ContactCenter é preservada.

## 1. Escopo e donos

| Módulo | Dono de |
|---|---|
| **Conversations** | mensagens, `InboundSeq`, `AnsweredThroughSeq`, `StateVersion` e `HandlingState`, turno logicamente ativo, snapshot de contexto, **aceite ou recusa da resposta** |
| **Agents** | o `AiTurn`: agrupamento, temporizador, política de interrupção, cancelamento cooperativo, tentativas |
| **Channels** | envio, status de entrega, janela do provedor |
| **ContactCenter** | transferências, filas, recuperação ou alerta de falha de entrega |

**Propriedade das configurações** (seção 14): o **Agents** controla agrupamento, interrupções, tentativas e o comportamento em rajada longa. O **Conversations** aplica as regras de consistência e de aceitação de respostas, que são **fixas no MVP** e não têm configuração por tenant. O **ContactCenter** controla o que é do atendimento humano. Nenhum módulo lê a configuração de outro.

**Fora do escopo desta especificação** (ver seção 17): quem processa mídia (download, transcrição, extração), o fluxo completo do modo `AiSupervised`, e a sessão com várias conversas de canais diferentes.

## 2. Identificadores e marcas

| Nome | Dono | Significado |
|---|---|---|
| `InboundSeq` | Conversations | contador por `ServiceSessionId`, incrementado de forma atômica na linha da sessão ao registrar cada mensagem do cliente. É a ordem de ingestão (ORD-5) |
| `LastInboundSeq` | Conversations | maior `InboundSeq` registrado |
| `AnsweredThroughSeq` | Conversations | maior `InboundSeq` **coberto logicamente** por uma resposta aceita. Nunca decresce (seção 11) |
| Pendentes | derivado | mensagens com `InboundSeq` acima de `AnsweredThroughSeq` e até `LastInboundSeq` |
| `StateVersion` | Conversations | versão do `HandlingState` (`BOUNDARIES.md` §6) |
| `TurnId` | Agents | um por turno; chave de idempotência do registro da resposta |
| `turn_epoch` | Conversations | inteiro crescente que identifica a "geração" do turno ativo |
| `active_turn_id` | Conversations | o turno logicamente ativo da sessão (no máximo um) |
| `last_lease_token` | Conversations | maior token de lane já visto, para rejeitar worker com lane antiga |
| `basedOnInboundSeq` | Agents, validado pelo Conversations | o `LastInboundSeq` do snapshot em que o turno se baseou |
| `AiRequestId` | AiGateway | uma por chamada ao modelo; um turno pode ter várias |

## 3. Ciclo de vida da mensagem

### 3.1 Mensagem do cliente

| Estado | Significado | Quem altera |
|---|---|---|
| `Registered` | gravada com `InboundSeq`; evento durável `InboundMessageRegisteredV1` publicado (OrderingKey = sessão) | Conversations, na ingestão |
| `ContentReady` | conteúdo interpretável (o texto nasce assim) | Conversations |
| `ContentPending` | mídia aguardando processamento | Conversations, na ingestão |
| `ContentFailed` | o processamento do conteúdo falhou, ou o prazo da política (seção 10) se esgotou | Conversations, por comando do processador |
| Pendente ou Respondida | derivado de `AnsweredThroughSeq`; não há estado por mensagem | derivado |

A marca `late_arrival` (ORD-6) identifica mensagem cujo `provider_timestamp` é anterior a uma resposta já aceita. Ela entra como pendente normal.

### 3.2 Mensagem de saída: cobertura lógica × envio

| Estado | Dono | Cobre logicamente? | Observação |
|---|---|---|---|
| `PendingApproval` | Conversations | ainda não | só no modo `AiSupervised`; tratamento detalhado em especificação própria |
| `Requested` | Conversations | **sim** | resposta aceita e liberada; `OutboundMessageRequestedV1` publicado |
| `Queued`, `Sending`, `Sent`, `Delivered`, `Read` | Channels | não altera | estados reais de envio e entrega (SND-3), monotônicos |
| `Failed(motivo)`, `Expired`, `Dead` | Channels | não desfaz | **falha definitiva**: seção 11 |
| `SendUnknown` | Channels | não altera | resultado ambíguo: nunca reenviado automaticamente (EFX-3) |
| `Rejected` | Conversations | não | rascunho rejeitado no `AiSupervised` |

- **MSG-1:** uma resposta por turno. Se tiver várias partes, saem em ordem pelo Sender (SND-1).
- **MSG-2:** `AiAutonomous` vai direto a `Requested`. `AiSupervised` passa por `PendingApproval`.
- **MSG-3 (adiado):** retratar resposta ainda não enviada quando chega mensagem nova. Só entra se a medição mostrar necessidade.

## 4. Turno logicamente ativo e substituição atômica

**4.1 Estado na linha da sessão.** O Conversations guarda `active_turn_id`, `turn_epoch` e `last_lease_token`.

**4.2 Comandos:**

- **`BeginTurn(serviceSessionId, turnId, leaseToken)`**: um **comparar-e-trocar atômico** que:
  1. exige responsável igual à IA e sessão aberta;
  2. exige `leaseToken` maior ou igual a `last_lease_token` (recusa worker com lane antiga);
  3. define `active_turn_id = turnId`, incrementa `turn_epoch` e grava `last_lease_token`;
  4. devolve o grant (`epoch`, `basedOnInboundSeq`, `StateVersion`) e o snapshot, tudo da mesma leitura consistente.

  **Qualquer turno ativo anterior deixa de ser ativo nesse mesmo instante.**
- **`EndTurn(serviceSessionId, turnId, epoch, resultado)`**: libera o turno ativo se ainda for este. Idempotente. Usado por `Superseded`, `Discarded`, `Cancelled` e `Dead`.
- **`CancelTurn(serviceSessionId, motivo)`**: limpa o turno ativo e incrementa a época (cancelamento manual, encerramento).

**4.3 Garantia.** No máximo **um turno logicamente ativo por `ServiceSessionId`, por construção** (é uma coluna). Um turno substituído, cancelado ou de época antiga **não registra resposta, mesmo que sua execução física continue**: `RegisterAiReply` exige turno ativo e época iguais, e responde `TurnNotActive` caso contrário. Essa garantia vem da transação do Conversations. O cancelamento físico da execução (seção 9) é só otimização.

**4.4 Substituição em duas camadas, sem transação entre módulos:**

1. No `Agents`, **uma transação** marca o turno `Superseded` e cria o sucessor `Scheduled`. Um índice único parcial garante um turno não terminal por sessão.
2. O sucessor, ao começar (T4), chama `BeginTurn`, que **assume** o lugar do anterior atomicamente no Conversations.

Entre as duas etapas o turno substituído já não consegue registrar: a cerca de entrada (a nova mensagem) e o `EndTurn` o impedem, e o `BeginTurn` do sucessor encerra qualquer dúvida. Se o Agents cair entre as etapas, o reconciliador (seção 12) recria o sucessor.

**4.5 Worker "zumbi".** Com a lease perdida, um novo `BeginTurn` com token maior assume. O zumbi, mesmo que termine, recebe `TurnNotActive`. Efeito externo continua sob a EFX.

## 5. Ciclo de vida do turno de IA

**Estados:** `Scheduled`, `Ready`, `Running`, e os terminais `Completed`, `Discarded`, `Superseded`, `Cancelled` e `Dead`. Falha transitória volta a `Ready`.

**Fases dentro de `Running`** (não são estados): P1 montar contexto, P2 chamar o modelo, P3 capacidades de leitura (P2 e P3 podem alternar), P4 efeito externo (pós-MVP, **não cancelável**), P5 compor a resposta, P6 registrar a resposta.

| ID | De | Gatilho | Condição | Para | Efeito |
|---|---|---|---|---|---|
| T1 | — | `InboundMessageRegisteredV1` | responsável é a IA e não há turno não terminal | `Scheduled` | calcula `fire_at` e `deadline` (seção 7) |
| T2 | `Scheduled` | nova entrada | — | `Scheduled` | recalcula `fire_at`, sem passar do `deadline` |
| T3 | `Scheduled` | `fire_at` atingido | todas as mensagens do grupo `ContentReady` | `Ready` | — |
| T3b | `Scheduled` | prazo de conteúdo esgotado | política `InformativeReply` (seção 10) | `Ready` | turno informativo, sem chamar o modelo |
| T3c | `Scheduled` | prazo de conteúdo esgotado | política `Handoff` (seção 10) | `Ready` | turno de encaminhamento, sem chamar o modelo |
| T4 | `Ready` | lane adquirida com `leaseToken` e teto do tenant livre | `BeginTurn` aceito | `Running` | recebe grant e snapshot; tentativa mais um |
| T4b | `Ready` | `BeginTurn` recusado (não é IA, sessão encerrada, token antigo) | — | `Cancelled` | sem sucessor |
| T5 | `Running` | `RegisterAiReply` aceito | — | `Completed` | cobre logicamente até `basedOnInboundSeq`; publica a saída; verifica pendentes (TRN-1) |
| T6 | `Running` | nova entrada | política `Supersede`, abaixo do limite, fase cancelável | `Superseded` | uma transação do Agents cria o sucessor; depois `EndTurn` (seção 4.4) |
| T7 | `Running` | resposta recusada `StaleInput` | — | `Discarded` | `EndTurn`; cria sucessor para as pendentes |
| T7b | `Running` | recusada `TurnNotActive`, `StaleState` ou `NotAiResponder` | — | `Discarded` | sem sucessor (quem substituiu ou cancelou cuida) |
| T8 | `Scheduled`, `Ready` ou `Running` | responsável deixou de ser a IA, sessão encerrada, ou `CancelTurn` | — | `Cancelled` | sem sucessor |
| T9 | `Running` | falha transitória (timeout, 5xx, 429, lease perdida) | tentativas abaixo do máximo | `Ready` | backoff; libera a lane; novo `BeginTurn` e snapshot |
| T10 | `Running` | falha permanente, ou tentativas esgotadas | — | `Dead` | `EndTurn`; aplica a PRO-5 (transferência ou alerta) |
| T11 | `Running` | encerramento limpo do worker | — | `Ready` | sem contar tentativa |
| T12 | `Running` | nova entrada | limite de interrupções atingido, ou fase não cancelável | `Running` | marca "entrada pendente"; ao terminar, o registro segue a cerca de entrada (seção 6) e, se a resposta estiver defasada, vale T7 |

- **TRN-1 (nenhuma mensagem fica para trás):** ao terminar T5, T6, T7 e T10, verifica-se se há pendentes e o sucessor existe.
- **TRN-2 (mensagem durante `Ready`):** não exige transição, porque o snapshot do T4 já pega tudo.
- **TRN-3 (`Dead` com `AlertOnly`):** a sessão fica sem novos turnos por `AiRetryCooldown`, para não repetir falha em laço. Com `Handoff`, vale a PRO-5.

**Invariantes:**

- **I1:** no máximo um turno **logicamente ativo** por sessão (coluna única no Conversations), e no máximo um turno não terminal por sessão no Agents (índice único parcial).
- **I2:** resposta aceita implica turno ativo igual a `TurnId`, época igual, `StateVersion` igual, responsável igual à IA e `basedOnInboundSeq = LastInboundSeq`. Não há exceção no MVP.
- **I3:** com a IA no comando, nenhuma mensagem pendente fica sem turno não terminal além do tempo do reconciliador.
- **I4:** `AnsweredThroughSeq` nunca decresce e é no máximo `LastInboundSeq`.
- **I5:** no máximo uma resposta aceita por `TurnId`.
- **I6:** nenhuma resposta sai para outra sessão ou contato (cenário Mauro/Pedro).
- **I7:** um turno `Scheduled` dispara até o `deadline`, mais o atraso de capacidade, ou resolve a política de conteúdo (seção 10).
- **I8:** efeito externo executado no máximo uma vez por `operation_id` (EFX).
- **I9:** conteúdo com estado diferente de `ContentReady` nunca é entrada do modelo.
- **I10:** falha de envio nunca cria turno de IA.
- **I11 (evolução futura, seção 6.3):** se a exceção defasada vier a existir, resposta defasada aceita implica ausência de atividade protegida e conteúdo só de texto simples. Não se aplica ao MVP.

## 6. Cercas de resposta e resposta defasada

### 6.1 Ordem de validação em `RegisterAiReply`

Tudo numa única transação do Conversations. **A decisão é do Conversations**, não do Agents.

1. **Idempotência:** `TurnId` já registrado devolve `AlreadyRegistered`.
2. **Turno ativo:** `active_turn_id = TurnId` e época igual à do grant; senão `TurnNotActive`.
3. **Responsável e versão:** responsável é a IA e `StateVersion` igual; senão `NotAiResponder` ou `StaleState`.
4. **Cerca de entrada:** `basedOnInboundSeq = LastInboundSeq`; senão `StaleInput`.
5. **Forma da resposta:** tipo e conteúdo permitidos, e `targetConversationId` pertence à sessão; senão `ShapeNotAllowed`.
6. **Commit atômico:** comparar-e-trocar na linha da sessão (turno ativo, época, `StateVersion` e `LastInboundSeq`) que grava `AnsweredThroughSeq = basedOnInboundSeq`, limpa o turno ativo, insere a mensagem de saída e publica `OutboundMessageRequestedV1`.

### 6.2 Resposta defasada no MVP: regra fixa

- **Toda resposta defasada é rejeitada** (`StaleInput`), inclusive a de turno que executou efeito colateral ou usou dado sensível. É uma **regra fixa do Conversations**, e não uma configuração: no MVP **não existe** `StaleReplyMode`, parâmetro `allowStaleInput`, chave por tenant, variável de ambiente nem opção de contrato que altere esse resultado. Nada na resposta, na declaração do Agents ou no tipo de turno o muda.
- O Conversations **não consulta o Audit** para decidir aceite.
- O resultado de uma ação executada por turno substituído ou descartado chega ao cliente pelo **turno sucessor**, que consulta as execuções recentes da sessão no `Capabilities` (a fonte da verdade), além do contexto da conversa.
- A correção depende apenas das cercas da seção 6.1, que são transacionais e **não configuráveis** por tenant. O cancelamento físico da execução (seção 9) é só otimização.

### 6.3 Evolução futura: exceção `allowStaleInput` (fora do MVP)

> **Não faz parte da primeira implementação.** Fica documentada para preservar a possibilidade, e só entra por **novo ADR**, com a aprovação do decisor. Nada acima depende dela, e o MVP **não contém chave, parâmetro nem código preparatório** para ela.

Se for adotada, o novo ADR definiria a configuração que a habilita (por exemplo, `StaleReplyMode = PlainTextOnly` por tenant, guardada no Conversations) e o parâmetro `allowStaleInput`, que permitiriam aceitar uma resposta defasada, **proibida para respostas sensíveis e para turnos com efeito colateral**, com verificações que não dependem apenas da declaração do Agents:

| # | Verificação | Quem fornece a evidência | Falha |
|---|---|---|---|
| E1 | exceção habilitada na configuração do tenant (chave criada por esse ADR), **guardada no Conversations** | Conversations | `StaleNotAllowed(Disabled)` |
| E2 | elegibilidade no `BeginTurn`: o grant seria emitido com `staleEligible = true` pelo próprio Conversations (só se E1 vale e o orçamento da rajada permite) | Conversations | `StaleNotAllowed(NotEligible)` |
| E3 | **forma:** conteúdo é texto simples, sem mídia, botões, lista, template, documento nem referência a resultado de ação | Conversations valida o conteúdo que recebe | `StaleNotAllowed(Shape)` |
| E4 | **evidência de auditoria:** nenhuma atividade protegida (efeito colateral ou dado sensível) com o `TurnId` nem na sessão desde a última resposta aceita | `Audit.Contracts.Queries.HasProtectedActivity`, sobre eventos gravados por **quem executou a operação** (Capabilities, Knowledge), e não pelo Agents | `StaleNotAllowed(ProtectedActivity)` |
| E5 | **falha fechada:** Audit indisponível ou resposta inconclusiva | — | `StaleNotAllowed(NoEvidence)` |
| E6 | no máximo uma resposta defasada aceita por rajada | Conversations (contador) | `StaleNotAllowed(Budget)` |

A consulta E4 seria uma consulta nova dentro da aresta `Conversations → Audit` já existente na matriz (DEP-7), e só se justifica nesse caso.

**Requisitos que tornariam a evidência E4 confiável** (a testar se a exceção for adotada):

- Toda execução de capacidade e toda leitura de conhecimento restrito carrega `CorrelationId = TurnId` e `ServiceSessionId`, vindos do `RequestContext`.
- O módulo que executa grava o evento de auditoria com classe `SideEffect` ou `SensitiveData` **na mesma transação do registro da execução e antes de devolver o resultado**. A classe vem do descritor da capacidade (ou do conhecimento), e não do Agents.
- O `Capabilities` recusa chamada sem `TurnId` num contexto conversacional.

**Auditoria da exceção:** todo aceite defasado gravaria o evento `StaleReplyAccepted` e uma métrica, com alerta se a taxa passasse de um limite.

**Política `Queue`:** também depende desta exceção e, portanto, também é evolução futura.

## 7. Agrupamento

- **GRP-1 (janela quieta com teto):** o turno espera `QuietWindow` desde a **última** mensagem, e nunca mais que `MaxWait` desde a **primeira** pendente.
- **GRP-2 (fórmula):** `fire_at = min(received_at da última + QuietWindow, received_at da primeira pendente + MaxWait)`. Usa o **relógio do servidor**, nunca o do provedor.
- **GRP-3 (sem espera):** tipos configuráveis disparam na hora (`BypassKinds`), como resposta a botão ou lista, e o retorno à IA com pendentes.
- **GRP-4 (conteúdo pronto):** o turno só passa a `Ready` quando todas as mensagens do grupo estão `ContentReady`. Se o `deadline` chegar com conteúdo pendente, vale a seção 10, e **o modelo nunca recebe conteúdo indisponível**.
- **GRP-5 (sem segurar recursos):** a espera é o campo `fire_at` do turno, avaliado por um laço de turnos vencidos. **Não segura lease, transação nem conexão.**
- **GRP-6 (tamanho):** `MaxInputMessages` limita o contexto do turno. O excedente entra no resumo do Context Manager, e os fatos estruturados não são resumidos (ARCH §4.1).
- **GRP-7 (ordem no contexto):** por `provider_timestamp`, desempatada por `InboundSeq`, com as marcas `late_arrival`.

## 8. Mensagens recebidas com a IA já processando

| Quando chega a mensagem | Política `Supersede` (padrão) |
|---|---|
| Turno `Scheduled` ou `Ready` | inclui no grupo (T2); nada é cancelado |
| `Running`, fases P1, P2, P3 ou P5 | **interrompe** (T6): tenta cancelar a chamada em curso (só otimização, CAN-2) e cria o sucessor com a janela quieta |
| `Running`, fase P4 (efeito externo) | **não cancela o efeito**: ele termina e fica registrado nas execuções do `Capabilities`. A resposta do turno é descartada (T7) e o sucessor a inclui |
| `Running`, fase P6 (registrando) | decide a ordem de commit na sessão (CON-1) |
| Turno já `Completed` | novo T1 |

- **INT-1 (limite de interrupções):** após `MaxSupersedesPerBurst` interrupções sem resposta aceita, o turno atual passa a ser **não interrompível** (T12). Ele **termina sem novas interrupções**.
- **INT-2 (ao terminar, MVP):** o registro da resposta segue a cerca de entrada. Se chegaram mensagens novas, a resposta está defasada e é **recusada** (`StaleInput`, T7), e o Agents cria um **novo turno** para as mensagens pendentes. Se não chegaram, a resposta é aceita (T5). A política `Queue`, que aceitaria a resposta defasada, é evolução futura (seção 6.3); `OnNewMessageWhileRunning` tem `Supersede` como único valor no MVP.
- **INT-3 (proteção contra ciclos infinitos):**
  - o contador de interrupções só reinicia quando uma resposta é aceita;
  - `MaxTurnAttempts` limita as tentativas do mesmo turno por falha transitória, e `TurnMaxDuration` limita cada execução;
  - `OnChattyBurst` (INT-6) é acionada quando a rajada atinge o limite configurável de mensagens ou de interrupções;
  - o ciclo é limitado pela entrada: **cada descarte exige ao menos uma mensagem nova do cliente**, e sem entrada nova não há novo turno;
  - as métricas de desperdício (seção 15) tornam o custo visível.
- **INT-4 (custo):** cancelar a chamada pode não anular os tokens já consumidos. Por isso as métricas medem o desperdício.
- **INT-5 (resposta já aceita):** o que já foi aceito e enfileirado continua. A retratação é MSG-3, adiada. O próximo turno enxerga a resposta no contexto.
- **INT-6 (rajada longa, `OnChattyBurst`):**
  - **Rajada** é a sequência de mensagens do cliente desde a última resposta aceita (`AnsweredThroughSeq`). Ela termina, e os contadores reiniciam, quando uma resposta é aceita.
  - **Limites configuráveis, do Agents:** `ChattyBurstMaxMessages` (mensagens do cliente na rajada) e `ChattyBurstMaxInterruptions` (interrupções somadas aos descartes por `StaleInput` na rajada). O Agents avalia a cada nova mensagem, interrupção ou descarte. Atingido **qualquer um** dos dois, a rajada é considerada longa.
  - **Comportamento inicial (`Alert`, o padrão):** o Agents pede ao Conversations `RaiseSessionAttention` com `motivo = ChattyBurst` e uma chave de idempotência (sessão e `AnsweredThroughSeq`), de modo que haja **um só sinal por rajada**. O Conversations marca a sessão e publica o evento durável `SessionAttentionRaisedV1`, que o ContactCenter consome (aresta existente): cria um item de atenção com alerta e **permite a transferência para humano por ação do atendente**, pelo fluxo normal de transferência (`TransitionHandlingMode`, que cancela o turno, T8). Enquanto isso a IA continua no comando e responde normalmente quando a rajada termina. **Nenhuma transferência automática acontece.**
  - **`Handoff` (opcional):** só vale se o tenant tiver configurado `OnChattyBurst = Handoff` **explicitamente**. Nesse caso, além do sinal, o Agents chama `ContactCenter.RequestHandoff` com o motivo `ChattyBurst` (e vale a PRO-5 se não houver atendente). **Sem configuração explícita, vale `Alert`**: o valor não é herdado de `OnAiPermanentFailure` (cujo padrão é `Handoff`), e valor ausente ou inválido é tratado como `Alert`, ou seja, falha para o lado de **não** transferir.
  - **Encerramento:** o item de atenção se encerra por ação do atendente (assumir ou dispensar) ou quando uma resposta é aceita (`AiReplyAcceptedV1`), o que termina a rajada.

## 9. Cancelamento e interrupção

| Causa | Tipo | Efeito no turno | Sucessor |
|---|---|---|---|
| Mensagem nova (T6) | suave | tenta cancelar operações de leitura em curso (otimização, CAN-2) | sim |
| Transição de modo ou handoff para humano (T8) | **dura** | cancela tudo | não (volta no `ReturnToAi`, CON-7) |
| Sessão encerrada | dura | cancela tudo | não |
| Cancelamento manual (`CancelTurn`) | dura | `Cancelled(Manual)` | não |
| Timeout do turno | transitória | T9 | o mesmo turno |
| Teto de IA do tenant | espera | continua `Ready` até haver capacidade | — |
| Encerramento limpo do worker | transitória | T11 | — |

- **CAN-1 (mecanismo):** pedido de cancelamento durável no turno (`cancel_requested` com motivo), lido no heartbeat. Dentro do mesmo processo, sinal imediato por token de cancelamento.
- **CAN-2 (o cancelamento físico é só otimização):** cancelar a chamada de IA (token de cancelamento, interrupção da requisição) reduz custo e latência, **mas nenhuma garantia de correção depende dele**. Se a chamada não for cancelada, ou terminar, a resposta é decidida pelas cercas transacionais do Conversations (`TurnNotActive`, `StaleInput`, `StaleState`, `NotAiResponder`). O sistema deve ser correto com o cancelamento físico totalmente desligado.
- **CAN-3 (efeitos externos):** a operação em P4 segue a EFX. Termina e registra o resultado; nunca é interrompida no meio.

## 10. Mídia com conteúdo pendente

- **MED-1:** conteúdo com estado diferente de `ContentReady` **nunca** entra no prompt, nem como texto substituto ou marcador que o modelo possa tratar como conteúdo. O turno **não começa** enquanto houver conteúdo pendente no grupo.
- **MED-2 (política por tipo, ao esgotar o prazo):** aplica-se ao **grupo** uma política explícita:

| Tipo de conteúdo | Primeiro prazo | Ao esgotar o prazo adicional | Observação |
|---|---|---|---|
| áudio | espera adicional (`ContentMaxExtraWait`) | resposta informativa | o cliente pode escrever |
| imagem | espera adicional | encaminhamento a humano | pode conter documento clínico (ARCH §7) |
| documento | espera adicional | encaminhamento a humano | idem |
| vídeo e outros | sem espera | encaminhamento a humano | sem processamento previsto |

Os padrões acima são **propostos e conservadores, a confirmar** na calibração. A configuração é por tenant e por tipo.

- **MED-3 (resposta informativa):** é **determinística**, um template aprovado do tenant, sem modelo. Entra como `RegisterAiReply` com `ReplyKind = Informative`, sujeita às mesmas cercas (turno ativo, versão, entrada). Cobre logicamente o grupo com `CoverageKind = Informative`. As mensagens continuam marcadas como `ContentFailed` e visíveis ao operador. Resposta defasada é recusada como qualquer outra e, se a exceção futura da seção 6.3 vier a existir, nunca se aplicará a ela.
- **MED-4 (encaminhamento):** `ContactCenter.RequestHandoff` com o motivo `ContentUnavailable`. Sem atendente, o atendimento fica pendente na fila, com o histórico preservado (PRO-5).
- **MED-5 (conteúdo pronto tarde):** se o conteúdo ficar pronto depois de a política ser aplicada, o turno **não é reaberto**. O conteúdo fica registrado e entra no contexto do próximo turno, ou é visto pelo atendente.
- **MED-6 (contratos, sem aresta nova):** o estado do conteúdo muda por `SetMessageContent` e `FailMessageContent` em `Conversations.Contracts.Commands`, chamados por quem processa a mídia (Channels ou Agents, ambos já com aresta para o Conversations). A mudança publica evento durável.

## 11. Cobertura lógica, envio e falha definitiva

- **COV-1:** `AnsweredThroughSeq` é a **cobertura lógica**. Avança na aceitação de uma resposta que cobre (`Generated` ou `Informative`), **nunca por envio ou entrega**, e nunca decresce.
- **COV-2:** os estados reais de envio e entrega pertencem ao Channels (SND-3) e chegam ao Conversations por `RecordDeliveryStatus`, de forma monotônica.
- **COV-3 (falha definitiva):** `Failed`, `Expired` e `Dead` **não alteram** `AnsweredThroughSeq`. O Conversations marca a sessão `ReplyDeliveryFailed` e publica o evento durável `OutboundMessageFailedV1`.
- **COV-4 (política inicial, única no MVP):**
  1. **Registrar** a falha: estado da mensagem de saída e marca `ReplyDeliveryFailed` na sessão.
  2. **Sinalizar** o atendimento ao ContactCenter, que consome `OutboundMessageFailedV1`, cria um item de pendência de entrega e dispara alerta.
  3. Permitir a **recuperação autorizada pelo atendente.** O atendente escolhe: reenviar a resposta (`RequestResend`, com as garantias da COV-7), enviar outra mensagem (inclusive template, se a janela estiver fechada e a política do provedor permitir), assumir o atendimento ou dispensar a pendência. Toda ação fica auditada com o autor.
  4. **Nenhum turno de IA é criado automaticamente.**

  A pendência se resolve quando uma nova saída que cubra a mesma sequência chega a `Sent`, ou por dispensa auditada. O handoff automático por falha de envio e a configuração por tenant (`OnReplyDeliveryFailure`) são evolução futura.
- **COV-5 (sem IA):** falha de envio **não é entrada** para a IA (I10). O Agents não consome `OutboundMessageFailedV1` (ARQ-24). O reconciliador olha só `AnsweredThroughSeq`. `SendUnknown` não é falha definitiva e segue a EFX-3.
- **COV-6:** quando o cliente escrever de novo, o snapshot traz o estado da última resposta (`Failed` ou `SendUnknown`), para o modelo não presumir que ela foi lida.
- **COV-7 (garantias do `RequestResend`):** é o contrato previsto para a recuperação autorizada, chamado pelo ContactCenter na ação do atendente.
  1. **Autorização explícita.** Só nasce de uma ação humana explícita. `autorizadoPor` vem da identidade autenticada no `RequestContext` (nunca do corpo da chamada), o ContactCenter verifica a permissão do atendente sobre aquele atendimento, e o Conversations recusa a chamada sem autor ou de outro tenant (`NotAuthorized`). Nenhum componente automático (Sender, reconciliador, Agents, nem regra do ContactCenter) chama `RequestResend`.
  2. **Vínculo auditável.** A nova solicitação de saída guarda `resendOfMessageId` (a mensagem que falhou), o autor e o horário, e a auditoria registra quem pediu, qual a mensagem original e qual a nova solicitação. O histórico mostra a cadeia original e reenvio.
  3. **Idempotência.** A chamada leva um `requestId`, a chave de idempotência da ação do atendente. Repetida com o mesmo `requestId`, devolve a mesma solicitação (`AlreadyRequested{newMessageId}`). Há ainda **no máximo uma solicitação de reenvio ativa** (não terminal) por mensagem original: outra chamada, mesmo com `requestId` diferente, é recusada (`ResendInProgress`) até a anterior terminar. O `operation_id` do envio deriva do `requestId` (EFX).
  4. **Regras e janela do canal.** O Conversations só aceita o reenvio de mensagem em **falha definitiva** (`Failed`, `Expired` ou `Dead`), de sessão aberta, com o mesmo conteúdo e o mesmo destino. A janela e as regras do provedor continuam sendo do **Channels**, que as aplica no envio como em qualquer saída: o reenvio **não as contorna**. Com a janela fechada, o resultado é nova falha definitiva (`Failed(WindowClosed)`), sinalizada ao atendente, que pode enviar outra mensagem (inclusive template, se a política do provedor permitir, COV-4). Não há laço: cada novo reenvio exige nova ação humana.
  5. **`SendUnknown`.** Resultado ambíguo **não** é falha definitiva: `RequestResend` o recusa (`NotDefinitiveFailure`) e **nenhum componente o repete automaticamente**. Ele só deixa de ser `SendUnknown` quando o Channels obtém **confirmação segura** (reconciliação com o provedor ou mecanismo de idempotência comprovado, EFX-3), que o leva a entregue ou a falha definitiva. O reenvio que também resultar em `SendUnknown` segue a mesma regra.
  6. **Sem IA.** O reenvio publica apenas `OutboundMessageRequestedV1`, consumido pelo Channels. Não cria entrada do cliente, não altera `AnsweredThroughSeq` (a cobertura já existe), não cria turno de IA e não é consumido pelo Agents (I10, ARQ-24).

## 12. Concorrência

Um turno ativo por sessão e sessões em paralelo, pelo lease da lane (`BOUNDARIES.md` §12.5).

| ID | Corrida | Resolução | Teste |
|---|---|---|---|
| CON-1 | mensagem nova × registro da resposta | comparar-e-trocar atômico na sessão. Se a resposta entra primeiro, a mensagem vira pendente (novo T1). Se a mensagem entra primeiro, a resposta é `StaleInput` (T7) | TST-R1 |
| CON-2 | duas mensagens simultâneas (canais diferentes da mesma sessão) | `InboundSeq` por incremento atômico: ordem total | TST-R2 |
| CON-3 | transição de modo × turno | `StateVersion` muda, resposta é `StaleState`, T8 cancela | TST-R3 |
| CON-4 | evento duplicado | idempotência por `EventId` e por (sessão, `InboundSeq`) | TST-R4 |
| CON-5 | worker "zumbi" que perdeu a lease | época, turno ativo e `last_lease_token` (seção 4) | TST-SUB |
| CON-6 | substituição de turno | seção 4.4: duas camadas, sem dois turnos ativos em instante algum | TST-SUB |
| CON-7 | retorno à IA com pendentes | T1 com disparo imediato | TST-R7 |
| CON-8 | turno agendado × mudança para humano | T8 | TST-R8 |
| CON-9 | evento de entrada reentregue depois de o snapshot já incluir a mensagem | T2 inócuo: o snapshot lê o banco | TST-R9 |
| CON-10 | encerramento da sessão × resposta | recusada (`NotAiResponder`) | TST-R10 |
| CON-11 | mensagem durante o backoff | entra no próximo snapshot | TST-R11 |
| CON-12 | relógio | espera e teto usam `received_at` do servidor | TST-R12 |
| CON-13 | cancelamento manual × registro da resposta | `CancelTurn` limpa o turno ativo: a resposta recebe `TurnNotActive` | TST-SUB |
| CON-14 | falha de envio × turno | falha de envio não cria turno (I10) | TST-ENV |

**Reconciliador (CON-15):** job periódico que, para cada sessão com a IA no comando e pendentes sem turno não terminal além de um limite, cria o turno. Cobre evento perdido e falha entre as duas etapas da seção 4.4. Não considera estados de envio.

## 13. Contratos (nomes e campos principais)

**`Conversations.Contracts.Commands`:**

- `IngestInboundMessage` (existente): atribui `InboundSeq` e publica `InboundMessageRegisteredV1` (durável).
- `BeginTurn(serviceSessionId, turnId, leaseToken)`, `EndTurn(serviceSessionId, turnId, epoch, resultado)`, `CancelTurn(serviceSessionId, motivo)` (seção 4).
- `RegisterAiReply(serviceSessionId, turnId, epoch, expectedStateVersion, basedOnInboundSeq, replyKind, targetConversationId, conteúdo, aiRequestIds)`, com os resultados `Accepted{messageId, answeredThroughSeq}`, `AlreadyRegistered{messageId}`, `TurnNotActive`, `StaleState`, `NotAiResponder`, `StaleInput{lastInboundSeq}` e `ShapeNotAllowed`. **O contrato do MVP não tem `allowStaleInput`.**
- `SetMessageContent`, `FailMessageContent`, `RecordDeliveryStatus` (existente), `TransitionHandlingMode` (existente).
- `RequestResend(messageId, requestId, autorizadoPor)`: ação **do atendente**, chamada pelo ContactCenter, que cria uma nova solicitação de saída **vinculada** à mensagem que falhou definitivamente, com as garantias da COV-7 (autorização explícita, vínculo auditável, idempotência, regras e janela do canal, nenhum reenvio automático de `SendUnknown`, nenhum turno de IA). Resultados: `Requested{newMessageId}`, `AlreadyRequested{newMessageId}`, `NotAuthorized`, `NotDefinitiveFailure`, `ResendInProgress` e `SessionClosed`. Usa a aresta `ContactCenter → Conversations`, que já existe.
- `RaiseSessionAttention(serviceSessionId, motivo, chaveDeIdempotencia, detalhes)`: chamado pelo Agents quando a rajada atinge o limite (INT-6, `motivo = ChattyBurst`). O Conversations marca a sessão e publica `SessionAttentionRaisedV1`, **sem alterar responsável nem modo**. Usa a aresta `Agents → Conversations`, que já existe.

**Eventos (duráveis):** `InboundMessageRegisteredV1`, `MessageContentReadyV1`, `MessageContentFailedV1`, `HandlingModeChangedV1`, `SessionClosedV1`, `OutboundMessageFailedV1` (`messageId`, `serviceSessionId`, `coveredThroughSeq`, motivo), `SessionAttentionRaisedV1` (`serviceSessionId`, motivo, contadores da rajada), `AiReplyAcceptedV1` (informativo).

**ContactCenter:** consome `OutboundMessageFailedV1` (COV-4), `SessionAttentionRaisedV1` (INT-6) e `AiReplyAcceptedV1` (encerra o item de atenção da rajada).

**Evolução futura, fora do MVP:** `Audit.Contracts.Queries.HasProtectedActivity(tenantId, serviceSessionId, turnId, desde)`, consulta nova dentro da aresta `Conversations → Audit` já existente, **só se** a exceção defasada (seção 6.3) for adotada. **No MVP o Conversations não consulta o Audit.**

**Agents:** persiste o `AiTurn` no próprio esquema e consome `InboundMessageRegisteredV1`, `MessageContentReadyV1`, `MessageContentFailedV1`, `HandlingModeChangedV1` e `SessionClosedV1`. Só chama o ContactCenter por `RequestHandoff`: para sinalizar a rajada longa usa o Conversations (`RaiseSessionAttention`), e não o ContactCenter. **Não consome** `OutboundMessageFailedV1`.

**Matriz de dependências:** nenhuma aresta nova.

## 14. Configuração

Os valores numéricos são a **referência inicial aprovada**, todos configuráveis e sujeitos a calibração.

**Propriedade:** o **Agents** controla agrupamento, interrupções, tentativas e o comportamento em rajada longa. O **Conversations** aplica as regras de consistência e de aceitação de respostas, que são **fixas** e não configuráveis no MVP. O **ContactCenter** controla o que é do atendimento humano. Nenhum módulo lê a configuração de outro: o Agents descobre o que o Conversations aceita pelas respostas dele (`TurnNotActive`, `StaleInput`, e assim por diante).

### Agents: agrupamento, interrupções, tentativas, rajada longa e conteúdo

| Parâmetro | Valor inicial |
|---|---|
| `QuietWindow` | 3 s |
| `MaxWait` | 10 s |
| `MaxSupersedesPerBurst` | 2 |
| `MaxTurnAttempts` | 3 |
| `OnNewMessageWhileRunning` | `Supersede` (único valor no MVP; `Queue` é evolução futura) |
| `BypassKinds` | resposta a botão ou lista |
| `ChattyBurstMaxMessages` | 10 mensagens do cliente na rajada (referência, a calibrar) |
| `ChattyBurstMaxInterruptions` | 3 interrupções somadas a descartes por `StaleInput` na rajada (referência: `MaxSupersedesPerBurst` + 1, a calibrar) |
| `OnChattyBurst` | `Alert`: sinaliza o atendimento ao ContactCenter e permite a transferência por ação do atendente (INT-6). `Handoff` **só se configurado explicitamente** pelo tenant; sem configuração, vale `Alert` |
| `OnContentUnavailable` (por tipo) e `ContentMaxExtraWait` | tabela da seção 10; prazo a calibrar |
| `OnAiPermanentFailure` | `Handoff` (já aprovado) |
| `TurnMaxDuration`, `RetryBackoff`, `AiRetryCooldown`, `MaxInputMessages` | a calibrar |

### Conversations: regras fixas, sem configuração de aceite

No MVP o Conversations **não tem parâmetro de aceitação de resposta**. Toda resposta defasada é rejeitada (seção 6.2), e as cercas (turno ativo, época, `StateVersion` e cerca de entrada) são **regras fixas**, não configuráveis nem por tenant. Uma configuração desse tipo só existiria se uma exceção futura (seção 6.3) fosse adotada por novo ADR.

### ContactCenter: atendimento humano

A política de falha definitiva de envio é **fixa no MVP** (seção 11). A configuração por tenant (`OnReplyDeliveryFailure`, com a alternativa de handoff automático) é evolução futura.

## 15. Observabilidade

- **Turnos:** contagem por estado; mensagens por turno; espera do agrupamento; espera por capacidade; duração; interrompidos, descartados (por motivo), cancelados (por causa), tentativas, `Dead`.
- **Substituição:** `TurnNotActive` recebidos (turnos substituídos que continuaram executando).
- **Custo:** tokens e chamadas desperdiçados por interrupção ou descarte, agregados por tenant (tabela, não rótulo).
- **Resposta ao cliente:** idade da mais antiga pendente; tempo da última mensagem até a resposta aceita e até `Sent`; órfãos achados pelo reconciliador.
- **Respostas defasadas:** recusas `StaleInput` por sessão e por tenant (MVP). `StaleReplyAccepted` e `StaleNotAllowed` só existem na evolução futura (seção 6.3).
- **Entrega:** falhas definitivas por motivo, itens abertos de `ReplyDeliveryFailed` e reenvios solicitados por resultado (`RequestResend`).
- **Rajada longa:** sinais `ChattyBurst` por tenant, itens de atenção abertos e transferências feitas a partir deles (por atendente e, se configurado, automáticas).
- **Mídia:** mensagens em `ContentPending` por idade, políticas de conteúdo aplicadas por tipo e conteúdo que ficou pronto tarde.
- **Alertas:** pendente antiga; taxa alta de interrupção (janela curta demais ou ciclo); taxa alta de descarte; qualquer `Dead`; órfão acima de zero; falha de entrega aberta além do prazo; item de atenção de rajada longa aberto além do prazo; mídia pendente além do prazo.
- **Para avaliar a hipótese do agrupamento:** a proporção de turnos com mais de uma mensagem mede o ganho, e a latência adicionada mede o custo.

## 16. Testes previstos

Locais conforme o `BOUNDARIES.md`: `Conversations.Tests` (estado e cercas), `Agents.Tests` (máquina do turno e agrupamento, com `IClock` falso) e `IntegrationTests` (PostgreSQL real, corridas). Sem pacote novo, com dublês de provedor de IA (latência e falha injetáveis), de canal e de auditoria.

| Grupo | Cenários |
|---|---|
| **Máquina de estados** | uma tabela de teste por transição (T1 a T12, incluindo T3b, T3c, T4b e T7b), positiva e negativa (transição ilegal recusada); sequências aleatórias com semente fixa verificando I1 a I10 (e I11 se a exceção defasada vier a existir) contra um modelo |
| **Agrupamento** | G1: 3 mensagens dentro da janela geram 1 turno com as 3. G2: fluxo contínuo dispara no `MaxWait`. G3: tipo de disparo imediato. G4: a janela reinicia a cada mensagem. G5: sessões intercaladas não se misturam. G6: mensagem atrasada (`late_arrival`) |
| **Interrupção** | S1: mensagem em P1 gera 1 resposta cobrindo as duas. S2: mensagem em P2 tenta cancelar a chamada (contada no dublê). S3: limite de interrupções torna o turno não interrompível; ao terminar com mensagens novas, a resposta é **recusada** e um novo turno cobre as pendentes, sem exceção defasada. S4: mensagem em P6, nas duas ordens possíveis, por barreiras determinísticas. S5: mensagem em P4 não cancela o efeito. S6: com entrada finita o número de turnos é finito: sem mensagem nova não nasce novo turno, e cada descarte exige entrada nova |
| **Cancelamento** | C1: handoff no meio do turno, **nenhuma resposta sai** mesmo que a IA termine. C2: cancelamento manual. C3: sessão encerrada. C4: encerramento limpo do worker sem contar tentativa. **C5: cancelamento físico desligado:** toda a suíte de interrupção e de substituição produz as mesmas respostas aceitas e recusadas. **C6:** o dublê do modelo ignora o token de cancelamento e conclui a chamada: a resposta é recusada pelas cercas e nada sai |
| **Substituição atômica de turnos (TST-SUB)** | SUB-1: dois `BeginTurn` concorrentes para a mesma sessão, só um fica ativo e a época cresce. SUB-2: turno substituído continua executando e chama `RegisterAiReply`: **recebe `TurnNotActive` e nenhuma resposta sai**. SUB-3: worker "zumbi" com token de lane antigo recebe `BeginTurn` recusado, e o zumbi que termina recebe `TurnNotActive`. SUB-4: falha injetada entre a transação do Agents e o `BeginTurn` do sucessor nunca produz dois turnos ativos, e o reconciliador recria o sucessor. SUB-5: `CancelTurn` manual concorrente com o registro, nas duas ordens. SUB-6: 1.000 intercalações com semente fixa, sempre com no máximo um turno ativo por sessão e no máximo uma resposta aceita por `TurnId` |
| **Resposta defasada sensível (TST-STL)** | STL-1: turno que executou efeito colateral termina defasado: **rejeitada** (`StaleInput`), e o resultado da ação chega ao cliente pelo sucessor. STL-2: turno que leu dado sensível termina defasado: rejeitada. STL-3: resposta defasada simples (texto): rejeitada, e o sucessor cobre as pendentes. STL-4: o contrato do MVP não tem `allowStaleInput`, e nenhuma declaração do Agents faz a resposta defasada passar. STL-5: **não existe configuração que altere a regra**: o modelo de opções do Conversations não contém chave de aceite defasado, e nenhuma chave de tenant, variável de ambiente ou parâmetro de contrato faz uma resposta defasada ser aceita. STL-6: o Conversations **não consulta o Audit** para aceitar respostas (o dublê do Audit não recebe nenhuma chamada). STL-7: turno não interrompível (limite atingido) cuja resposta fica defasada: recusada e novo turno criado |
| **Evolução futura: exceção defasada** (não bloqueia o MVP) | só serão implementados se a exceção da seção 6.3 for adotada por novo ADR: efeito colateral ou dado sensível rejeitados mesmo que o Agents declare o contrário; Audit indisponível rejeita (falha fechada); forma diferente de texto simples; orçamento por rajada; caso seguro aceito cobrindo só até `basedOnInboundSeq`, com o sucessor cobrindo o resto |
| **Falha definitiva de envio (TST-ENV)** | ENV-1: `Failed(WindowClosed)`, `Expired` e `Dead` **não criam turno de IA** (nenhuma chamada ao modelo no dublê). ENV-2: `AnsweredThroughSeq` permanece e a sessão fica `ReplyDeliveryFailed`. ENV-3: `OutboundMessageFailedV1` gera item de pendência e alerta no ContactCenter. ENV-4: o reconciliador não cria turno por falha de envio. ENV-5: `SendUnknown` não é tratado como falha definitiva e não é reenviado. ENV-6: nova mensagem do cliente cria turno normal cujo snapshot traz o estado `Failed` da última resposta. ENV-7: a pendência se resolve com nova saída `Sent` ou dispensa auditada. ENV-8: o Agents não referencia o evento de falha (ARQ-24). **ENV-9: a recuperação exige ação do atendente:** sem a ação, não há reenvio nem turno; com `RequestResend` autorizado, nasce uma nova solicitação de saída vinculada, auditada com o autor e com novo `operation_id` (EFX); as garantias estão em TST-RSD. ENV-10: o atendente pode, em vez disso, enviar outra mensagem, assumir o atendimento ou dispensar a pendência |
| **Reenvio autorizado (TST-RSD)** | RSD-1: chamada sem autor, ou de atendente sem permissão sobre o atendimento, é recusada (`NotAuthorized`) e nada é enviado. RSD-2: o reenvio guarda `resendOfMessageId`, autor e horário, e a auditoria liga a mensagem original à nova solicitação. RSD-3: o mesmo `requestId` repetido, inclusive em concorrência, gera uma só saída (`AlreadyRequested`); outra chamada com `requestId` diferente enquanto há reenvio ativo é `ResendInProgress`. RSD-4: mensagem que não está em falha definitiva é recusada (`NotDefinitiveFailure`). RSD-5: com a janela fechada, o Channels aplica a regra como em qualquer saída e o resultado é nova falha definitiva, sem laço e sem reenvio extra sem nova ação humana. RSD-6: mensagem em `SendUnknown` é recusada, e nem o Sender, nem o reconciliador, nem o ContactCenter, nem o Agents a repetem automaticamente; só depois da confirmação segura do Channels o estado muda e o reenvio pode ser pedido. RSD-7: o reenvio não cria turno de IA (o dublê do modelo não recebe chamada) e não altera `AnsweredThroughSeq`. RSD-8: jobs e consumidores de evento do ContactCenter não referenciam `RequestResend` |
| **Rajada longa (TST-CHT)** | CHT-1: o limite de mensagens sinaliza a sessão e **não transfere**. CHT-2: o limite de interrupções somadas a descartes faz o mesmo. CHT-3: um só sinal por rajada (idempotência), e novo sinal só depois de uma resposta aceita. CHT-4: o ContactCenter cria o item de atenção e permite ao atendente assumir, o que cancela o turno (T8). CHT-5: **sem configuração explícita nunca há `RequestHandoff`**, inclusive com `OnAiPermanentFailure = Handoff`. CHT-6: com `OnChattyBurst = Handoff` explícito, há um só `RequestHandoff(ChattyBurst)`, e sem atendente o atendimento fica pendente com o histórico (PRO-5). CHT-7: valor ausente ou inválido de `OnChattyBurst` vale `Alert`. CHT-8: sem ação do atendente, a IA responde normalmente quando a rajada termina, e o item se encerra com `AiReplyAcceptedV1`. CHT-9: o Agents chama o ContactCenter somente por `RequestHandoff` (a matriz e a restrição não mudam) |
| **Mídia indisponível (TST-MID)** | MID-1: com áudio pendente, **o dublê do modelo não recebe nenhuma chamada**. MID-2: o conteúdo fica pronto dentro do prazo e o turno roda com ele. MID-3: no prazo, `ExtendWait` mantém o turno agendado e ainda sem modelo. MID-4: esgotada a espera, o áudio gera resposta informativa determinística, sem modelo, com `CoverageKind = Informative` e mensagens marcadas `ContentFailed`. MID-5: imagem ou documento gera `RequestHandoff(ContentUnavailable)`, e sem atendente o atendimento fica pendente com o histórico. MID-6: grupo com texto pronto e áudio pendente: **o modelo nunca vê o conteúdo do áudio** e a política vale para o grupo. MID-7: transcrição que falha definitivamente segue a política por tipo. MID-8: o construtor do prompt nunca emite marcador que o modelo possa tratar como conteúdo. MID-9: conteúdo pronto depois da política não reabre o turno |
| **Concorrência** | TST-R1 a TST-R12, com N workers e PostgreSQL real |
| **Falhas** | retentativa com backoff; `Dead` com `Handoff` (com e sem atendente); `Dead` com `AlertOnly` e resfriamento (a rajada longa está em TST-CHT) |
| **Reconciliador** | evento perdido gera turno; sem falso positivo em sessão com humano |
| **Idempotência** | evento de entrada entregue 2 vezes gera 1 turno; `RegisterAiReply` repetido com o mesmo `TurnId` gera 1 mensagem |
| **Efeitos externos** | preservam a EFX: um turno substituído em P4 termina o efeito uma única vez, e o sucessor o conhece |
| **Aceitação Mauro/Pedro, ampliada** | Mauro manda 3 mensagens em 2 s e Pedro uma, em sessões paralelas: 1 turno de Mauro com as 3, 1 de Pedro, sem mistura. Mauro manda outra durante o processamento: interrupção e uma só resposta, **somente** no atendimento #1001 |
| **Carga e custo** | muitas sessões com temporizadores ativos (escala da busca de turnos vencidos); tráfego em rajadas medindo o desperdício de tokens e a taxa de interrupção |

## 17. Pontos em aberto

- **Evolução futura documentada, fora do MVP:** a exceção `allowStaleInput` e a configuração que a habilitaria (seção 6.3), a política `Queue`, a consulta de evidência ao Audit e o handoff automático por falha de envio (`OnReplyDeliveryFailure`). Cada uma exige novo ADR, e nenhuma deve deixar chave ou código preparatório no MVP.
- **Reenvio de mensagem em `SendUnknown`** por decisão humana, assumindo o risco de duplicidade: fora do MVP. Hoje só há reenvio depois da confirmação segura do Channels (COV-7).
- **Janela do canal na tela do atendente:** antecipar que o reenvio cairá em `WindowClosed` exigiria que o ContactCenter soubesse a situação da janela, e ele não tem aresta para o Channels. Decidir na especificação do ContactCenter (por exemplo, a partir de eventos do Conversations). Até lá, o atendente descobre pelo resultado do envio.
- **Quem processa a mídia** (download, transcrição, extração) e os prazos por tipo: este documento define só o estado do conteúdo e a política ao esgotar o prazo.
- **Templates das respostas informativas** (seção 10): onde ficam, quem os aprova, idioma e versão por tenant.
- **Modo `AiSupervised`:** fluxo de aprovação, edição e rejeição, e como a cobertura se comporta em `PendingApproval`. Especificação à parte.
- **Eventos de reação, edição e exclusão de mensagem** do cliente no WhatsApp: não verificados em documentação oficial. Hoje não afetam o turno.
- **Sinal de "digitando"** do cliente: não confirmado, e a especificação **não depende dele**.
- **Várias conversas na mesma sessão** (multicanal): o destino da resposta fica restrito à conversa da última mensagem até haver caso real.
- **Retratação** de resposta ainda não enviada (MSG-3) e **janela adaptativa**: adiadas, só com dados reais.
- **Calibração** de todos os valores da seção 14 com o piloto.

## Histórico de alterações

| Data | Versão | Resumo | Aprovador | ADR |
|---|---|---|---|---|
| 26/10/2026 | 1.0 | Versão inicial aprovada: agrupamento, política `Supersede`, cerca de entrada, turno logicamente ativo, resposta defasada sempre rejeitada por regra fixa, sem `StaleReplyMode` no MVP (a exceção controlada fica como evolução futura e depende de novo ADR), cobertura lógica separada de entrega, política inicial de falha definitiva de envio com `RequestResend` e suas garantias, comportamento inicial de `OnChattyBurst`, mídia pendente, propriedade das configurações, cancelamento físico só como otimização e testes | Mauro Porpino | 0007 |
