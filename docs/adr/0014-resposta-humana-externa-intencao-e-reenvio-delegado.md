# ADR 0014 — Resposta humana externa: autorização, intenção de resposta, cerca e reenvio delegado

- **Status:** **Aceito parcialmente (03/11/2026), escopo da resposta humana.** Nada deste ADR está implementado.
- **Registro de aprovação (03/11/2026, decisor):** aprovados a **7ª operação**, `RegisterHumanReply` (seção A), `reply_intents` e a execução única (seção B), a serialização entre mudança de permissão e intenção (seção C), a **chave de idempotência do cliente** (`ClientRequestId`), a parte da seção F referente à 7ª operação e os critérios C3, C3-cliente, C4 e, do C6, o que trata de `Manual` e `SendUnknown` (ADR 0011, F; DP-08). **Permanecem Propostos e fora do MVP:** a **8ª operação**, `RequestDelegatedResend` (seção E), os critérios do C6 que a tratam (`AcceptDuplicateRisk`, reenvio delegado) e a seção D (autorização contextual de anexos), porque os anexos foram adiados (DP-05). A 8ª continua no planejamento futuro. A aprovação de uma parte não aprova o restante.
- **Data:** 30/10/2026 (aprovação parcial: 03/11/2026)
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` v0.4.6 (seções 2, 3, 6, 11 e 12), `docs/architecture/CONVERSATION-LIFECYCLE.md` (§11 e COV-7), `docs/architecture/ETAPA-5.md`, ADRs 0006, 0007, 0009, 0010, 0011, 0012 e 0013, `CLAUDE.md`
- **Emenda:** o ADR 0009 (aceito parcialmente em 03/11/2026) é emendado em 30/10/2026 para a porta de **oito** operações, e esta decisão é o que justifica a sétima e a oitava (a 7ª aprovada; a 8ª Proposta).
- **Revisão (03/11/2026):** a pré-condição passa a derivar da base persistida da intenção, imutável (só a `ExpectedStateVersion` é renovável); o `request_hash` passa a cobrir uma representação canônica de todo o efeito pedido; e entra a chave de idempotência do cliente (`ClientRequestId`) contra duplicidade por duplo clique ou repetição. Nada disso aprova este ADR.
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**: `ContactCenter → Conversations` já existe), os ADRs 0001 a 0010, o `TransitionHandlingMode` da etapa 4 (a cerca continua **antes** da idempotência nele), `RegisterAiReply`, `RequestResend` do `Conversations` (continua exigindo a permissão do `Conversations` e recusando `SendUnknown`) e todas as garantias da etapa 4.
- **Registra:** **ampliação da superfície privilegiada** do gateway elevado do ContactCenter (duas operações de escrita novas), um novo tipo de consulta de efeito (`HumanReply`) e a decisão de autorização das respostas externas dos atendentes (`BOUNDARIES.md` §11: mudança no modelo de autorização).

## Contexto

O `Conversations` só registra resposta de **IA** (`RegisterAiReply`) e reenvio (`RequestResend`, que exige usuário autenticado com permissão do `Conversations` e **recusa** `SendUnknown`, `HandlingService`). **Não existe comando de resposta humana**: o compositor do atendente não tem como enviar. O atendente comum de fila não tem as permissões de conversa por organização; o acesso dele vem da fila (ADR 0009). O gateway elevado tem hoje **seis** operações, sendo a sexta somente leitura.

Três propriedades precisam valer com processos que caem e executores concorrentes: (1) a resposta **não pode ser aplicada duas vezes** nem em atendimento que mudou; (2) o **autor humano original** precisa sobreviver à troca de processo; (3) a **remoção de uma permissão** não pode deixar uma janela em que o removido ainda responde, nem a falta de uma intenção por item (o item tem **um** slot de operação pendente, `RequireIdle`, que não comporta várias respostas concorrentes).

## Decisão

### A. `RegisterHumanReply` (7ª operação da porta)

```text
RegisterHumanReplyCommand(
  SessionId, OperationId,              // gerado pelo servidor na 1ª criação da intenção (id da intenção); reutilizado nas repetições do mesmo ClientRequestId
  AuthorUserId,                        // autor humano original, validado no Identity
  ReplyPrecondition Precondition,      // derivada da base persistida da intenção; imutável; atômica com a gravação
  ExpectedStateVersion,                // única parte renovável (ver "Pré-condição e renovação")
  FenceToken,                          // do reply_intents, validado na trava da sessão
  ReplyContent Content)

ReplyPrecondition(Mode = HumanAssisted, HandlingPeriod, PlacementEpoch,
                  ResponderRef?, ResponderMatch: Exact | Any)

ReplyContent(Text, Recipients?, InReplyToMessageId?, ChannelData?,
             AttachmentRef[] Attachments)
AttachmentRef(AttachmentId, Origin: Upload | SameSessionExternal | SameSessionOutbound)
```

- **`Exact`** vale para a política exclusiva (o autor é o responsável); **`Any`** vale para a colaborativa (o responsável pode mudar sem invalidar um colaborador autorizado). Isso corrige a `HandlingPrecondition` atual, que não distingue os dois casos.

**Princípio.** A cerca decide **quem pode escrever**. A idempotência decide **o que já foi escrito**. Reconhecer um efeito existente é uma **leitura** e não exige cerca vigente.

**Ordem de validação**, dentro da trava da sessão:

1. **Sintática e permanente:** forma e limites estruturais fixos (não dependem de configuração mutável, então uma repetição não quebra por mudança de limite).
2. **Trava da sessão** (como hoje).
3. **Idempotência, somente leitura,** por `(tenant, OperationId)`, sem filtro de sessão:
   - **mesma `request_hash`** → `AlreadyApplied` com o resultado **original**, **antes** de revalidar qualquer condição mutável (sessão aberta, pré-condição, versão, anexos, capacidades);
   - **`request_hash` diferente** (ele já cobre, entre outros, sessão e autor), ou sessão ou autor diferentes → `OperationIdReused`, sem expor os dados originais;
   - nos dois casos a **cerca não é consultada nem alterada**, nada é gravado e **nenhum evento é emitido** (o do commit original já existe); o `HandlingFenceToken` da sessão não muda.
   - A `request_hash` identifica **o efeito pedido** (ver "Identidade do pedido e `request_hash`"). **Não** cobre a pré-condição, a `ExpectedStateVersion`, o `FenceToken`, o `authz_version` nem dado volátil: eles dizem **em que condições** o efeito é aplicado, não **qual** é o efeito. Isso não os torna equivalentes: a pré-condição **não é renovável**; só a `ExpectedStateVersion` pode ser renovada; e o `FenceToken` é emitido por tentativa e não concede autoridade (ver "Pré-condição e renovação").
4. **Cerca, só se não houve efeito prévio:** token ≤ 0 ou menor que o maior visto retorna `FencedOut`. A cerca impede **novas** escritas.
5. **Semântica:** limites configuráveis, anexos (seção D) e capacidades.
6. Sessão aberta.
7. **Pré-condição** (atômica com a gravação e **não renovável**): divergência de modo, `HandlingPeriod`, `PlacementEpoch` ou do responsável exigido (`ResponderMatch = Exact`) retorna `HandlingChanged`, sem gravar. Vale depois da idempotência e da cerca, como a pré-condição do `TransitionHandlingMode` (ADR 0010, D.22).
8. **`ExpectedStateVersion`:** divergência retorna `StaleState`, **somente se a pré-condição ainda vale**; se as duas divergem, o resultado é `HandlingChanged`. O chamador relê a versão e repete com a **mesma** pré-condição.
9. **Gravação:** a mensagem (`Outbound`, `Author=Human`, `AuthorRef = AuthorUserId`, `outbound_operation_id`, `outbound_request_hash`) e o `OutboundMessageRequestedV1` entram **na mesma transação**, e só então o `AdvanceFence`. O `OperationId` do comando é a chave de idempotência do Sender (EFX-2) e **não muda** entre tentativas. Índice único `(tenant_id, outbound_operation_id)`.

**Pré-condição e renovação** (compatível com o ADR 0010, D.20 a D.23).

- A pré-condição **deriva da base persistida** da intenção original (`reply_intents.base`: modo, período, colocação e responsável), gravada na **mesma gravação** da intenção, no instante da autorização. `ResponderRef` só vai na pré-condição quando `ResponderMatch = Exact` (o responsável exigido); com `Any`, não se exige responsável, mas modo, período e colocação continuam exigidos.
- A base é **imutável**: não é regravada, recalculada nem preenchida a partir da sessão relida em retentativa, recuperação (`Dispatching`), reconciliação por outro processo ou renovação de versão. O executor monta o comando **a partir da linha persistida**, nunca do estado atual da sessão.
- **Só a `ExpectedStateVersion` é renovável.** Ela pode ser relida do `Conversations` (a versão muda por motivos alheios à autoridade, como idioma ou unidade) e usada na repetição **com a mesma pré-condição**, enquanto a intenção estiver `Pending` ou `Dispatching` e dentro de `expires_at`.
- **Mudança de período de atendimento, de colocação (`PlacementEpoch`) ou do responsável exigido resulta em `HandlingChanged`**, sem gravar. A rejeição é definitiva (`Cancelled(HandlingChanged)`) e a intenção não é "atualizada": quem ainda quiser responder cria **nova intenção**, com nova base e nova autorização.
- **Renovar a versão não substitui a autorização.** Validar uma vez e renovar a versão transformaria uma autorização dada sobre o estado antigo em permissão sobre o novo (ADR 0010, D.21). A reavaliação ao vivo (camada 2) pode negar ou cancelar, mas **nunca altera** a base.
- Base ausente ou sem geração de colocação deixa a intenção **inerte** (ADR 0010, D.23).
- O reconhecimento idempotente (item 3) vem antes e **não depende** da pré-condição.
- O `FenceToken` é emitido por tentativa, crescente (seção B), decide **quem pode escrever** e não integra a identidade nem a autorização do pedido.

**Identidade do pedido e `request_hash`.**

1. **O que identifica.** O `request_hash` é a impressão digital do **efeito pedido**: tudo o que determina o que será enviado, a quem e em nome de quem. Mesmo `OperationId` com o mesmo efeito é o mesmo pedido (`AlreadyApplied`); com efeito diferente em **qualquer** campo coberto, `OperationIdReused`, sem expor os dados originais.
2. **Campos cobertos**, na forma **efetiva**, isto é, depois de materializados:
   - `SessionId`, `AuthorUserId` e a instância de canal da sessão;
   - texto e conteúdo da resposta;
   - destinatários **efetivos**, por papel (To, Cc, Bcc, ou o destinatário do canal), inclusive os derivados do contato da sessão ou da mensagem respondida, materializados na intenção;
   - citação (`InReplyToMessageId`, identificador imutável da mensagem);
   - anexos, como sequência de `(AttachmentId, Origin, content_sha256)`: referência imutável mais a impressão do conteúdo;
   - `ChannelData` **de envio**: somente as chaves que o adaptador declara relevantes (`ChannelDataSendSchema`, versionado), por exemplo assunto, identidade remetente (caixa e endereço) e cabeçalhos permitidos. Chave não declarada é rejeitada (`ContentRejected`, passo 1), de modo que nada que influencie o envio fica fora do hash;
   - corpo guardado fora da mensagem: referência imutável e `body_sha256`.
3. **Forma canônica.** Função pura e versionada (`hash_version`), publicada em `Conversations.Contracts` e usada pelos dois módulos: campos em ordem fixa, cada um com tag e comprimento; texto em UTF-8 exatamente como persistido na intenção; destinatários como **conjunto** (ordenado por papel e endereço normalizado, sem duplicata); anexos como **sequência** (a ordem importa); `ChannelData` de envio em JSON canônico (chaves ordenadas, sem espaço insignificante, número em forma única); ausente e vazio distinguidos por tag; SHA-256. Mudar a forma canônica exige **nova `hash_version`**, e linhas antigas continuam comparáveis pela versão gravada.
4. **Excluídos (voláteis ou de condição):** `OperationId` (é a chave), `FenceToken`, `ExpectedStateVersion`, `Precondition`, `authz_version`, instantes (criação, `expires_at`, tentativa), `attempts`, estado de varredura do anexo, `MessageId`, `MessageSeq`, `Message-ID` de e-mail e qualquer identificador gerado no envio, e o estado de entrega.
5. **Estabilidade em retentativas e recuperação.** O conteúdo efetivo é materializado **uma vez**, na gravação da intenção (`content_snapshot`, ou `content_ref` imutável), e o `request_hash` é calculado nessa gravação. Toda tentativa (mesmo executor, outro processo ou reconciliação) monta o comando **a partir da linha persistida**, nunca de dado vivo (contato, cadastro, rascunho, configuração). O `Conversations` **recalcula** o hash a partir do conteúdo recebido e compara com `outbound_request_hash`; **nunca aceita hash informado** pelo chamador. O Sender envia **somente** o conteúdo da mensagem gravada, cujo hash foi validado.
6. **Corpo externo e anexos.** A referência deve ser imutável (escrita única, ou endereçável por conteúdo). Se o armazenamento escolhido (DP-05) não garantir imutabilidade, `body_sha256` e `content_sha256` identificam o conteúdo e o Sender os confere antes de enviar; divergência é falha definitiva, sem envio.
7. **Reenvio delegado.** O hash cobre `SessionId`, `AuthorUserId`, `MessageId` da mensagem original, o `outbound_request_hash` dela e `Kind` (a modalidade). `ReasonCode` e `Justification` ficam **fora**: são auditoria, valem os da primeira aceitação e a repetição devolve o resultado original. Mesmo `RequestId` com outra mensagem original ou outra modalidade resulta em `OperationIdReused`, porque o `RequestId` é a chave de idempotência dessa operação.

**Resultados:** `Applied`, `AlreadyApplied`, `OperationIdReused`, `SessionNotFound`, `SessionClosed`, `HandlingChanged`, `StaleState`, `FencedOut`, `ContentRejected`, `AttachmentNotAllowed`, `AuthorNotFound`. **Só os dois primeiros deixam efeito.** Operação rejeitada não deixa registro, e repetir reavalia.

**Resultado do reconhecimento:**

```text
AlreadyApplied(MessageId, MessageSeq, StateVersionAfter, RegisteredAt,
               FenceObservation: Current | Overtaken,   // informativo
               CurrentDeliveryState)                    // leitura, não parte do efeito original
```

**Reconhecer não é ter autoridade para escrever localmente.** A finalização da intenção (`Registered`) é um CAS por `(state, executor_fence_token)` (seção B). Um executor com cerca ultrapassado que recebe `AlreadyApplied` devolve `Superseded` sem gravar; o sucessor, com o token vigente, obtém o mesmo resultado e finaliza. `GetEffectAsync(HumanReply)` é a via de leitura pura equivalente, sem token.

**A ordem difere de propósito** do `TransitionHandlingMode` da etapa 4 (cerca primeiro), **que não é alterado**: ali a repetição por um executor ultrapassado devolve `FencedOut`. A mudança vale só para as operações novas.

**Autoria.** A origem `contact-center` e o contexto de sistema ficam só na auditoria (ADR 0009, item 5). O autor humano vai na **intenção persistida antes da chamada** (`reply_intents`) e é reaplicado por quem retomar, inclusive o job de reconciliação em outro processo, com a proveniência do pedido (`IDeferredAudit`). **Troca de processo nunca troca o autor.**

### B. `reply_intents` e a execução única

```text
contact_center.reply_intents(
  id /*= OperationId*/, tenant_id, item_id, author_user_id,
  client_request_id, client_payload_hash,
  request_hash, hash_version, content_snapshot /* ou content_ref imutável */,
  authz_version, base(period, epoch, responder, responder_match, mode),
  state: Pending | Dispatching | Registered | Cancelled | Expired,
  executor_fence_token, expires_at, version, provenance)
```

Tabela **própria**; não usa o slot `pending_operation_id` do item. **Base completa exigida:** base sem geração de colocação deixa a intenção **inerte**, como na etapa 4 (`ScopeProof` preservado).

**Chave de idempotência do cliente (duplo clique e repetição da mesma intenção).**

1. A interface gera um `ClientRequestId` (UUID) **uma vez por intenção de envio do usuário** e o reenvia sem mudança em todo duplo clique, nova tentativa de rede ou recarga da mesma ação. É **obrigatório** no endpoint "responder externamente": requisição sem chave é rejeitada (400), sem criar intenção, e o servidor **não gera chave substituta**.
2. A intenção guarda `client_request_id` e `client_payload_hash`, com índice único `(tenant_id, author_user_id, client_request_id)`. O `client_payload_hash` é a impressão (mesma forma canônica e `hash_version`) do conteúdo **como o cliente o submeteu**, antes da materialização (destinatários implícitos, remetente, `content_snapshot`), de modo que dado vivo que mude entre dois cliques não vira "pedido diferente". Ele se distingue do `request_hash`, que identifica o **efeito** já materializado.
3. **Na criação da intenção:**
   - busca por `(tenant, autor, ClientRequestId)`;
   - se existe com o mesmo `client_payload_hash` e o mesmo item, **reutiliza a intenção e o mesmo `OperationId`**, sem intenção nova, sem nova mensagem e sem novo evento, e devolve o estado dela: `Registered` → o resultado original; `Pending` ou `Dispatching` → aceito (202) com o mesmo `OperationId`; `Cancelled` ou `Expired` → o desfecho gravado;
   - se existe com `client_payload_hash` ou item diferentes → `ClientRequestIdReused` (409), sem efeito;
   - se não existe, segue a autorização completa (camada 1 sob `FOR SHARE`, seção C) e grava a intenção com `INSERT … ON CONFLICT (tenant_id, author_user_id, client_request_id) DO NOTHING` (alvo explícito); se o `INSERT` não grava (requisição concorrente com a mesma chave), relê e responde como no segundo caso.
4. **A autoridade da repetição é a posse da intenção** (mesmo tenant e mesmo autor), como na etapa 4 (ADR 0010, D.24): o reconhecimento vem **antes** de reavaliar permissão, vale mesmo depois de a autorização mudar e não revela nada além do que o autor já recebeu.
5. **A chave identifica a intenção, não o texto.** Nova intenção do usuário (nova chave), ainda com o mesmo texto, é um novo envio. A chave vale enquanto a intenção existir (retenção a definir, DP-06).
6. O `OperationId` continua **gerado pelo servidor**, na primeira criação, e é a chave de idempotência do `Conversations` e do Sender: a repetição do cliente nunca produz um segundo `OperationId`.

**Emissão do token**, independente do slot do item:

```sql
UPDATE reply_intents
   SET executor_fence_token = nextval('contact_center.executor_fence_seq'),
       state = 'Dispatching', version = version + 1
 WHERE id = @intent AND state IN ('Pending','Dispatching')
RETURNING executor_fence_token;
```

Um passo atômico de emissão e revalidação: intenção cancelada, registrada ou expirada **nunca** recebe token. Reaproveita a sequência global do executor, então um item com `Assign` pendente ainda aceita uma resposta. Uma tentativa posterior tem token maior e fecha a anterior (`FencedOut`).

**Garantia.** Pode haver **várias tentativas**, mas existe **no máximo um efeito** por intenção, em três camadas independentes:
- **L1, trava consultiva por intenção** (`pg_try_advisory_lock`, presa à conexão): evita trabalho repetido; uma queda a libera.
- **L2, token e CAS:** só o dono do token mais recente grava na intenção, e o `Conversations` recusa token menor.
- **L3, registro idempotente** por `OperationId` (índice único): é o limite final. Mesmo que L1 e L2 falhem, nunca existem duas mensagens.

**Estados e transições** (todas por CAS):

| De | Para | Condição | Prova exigida |
|---|---|---|---|
| `Pending` | `Dispatching` | `WHERE state = 'Pending'`, acesso ao vivo (camada 2) e `authz_version` conferidos; emite o token | nenhuma |
| `Pending` | `Cancelled` (permissão perdida, cancelamento, expiração) | `WHERE state = 'Pending'` | nada foi despachado |
| `Dispatching` | `Registered` | `(state = 'Dispatching', executor_fence_token = meu)` | `Applied` ou `AlreadyApplied` do `Conversations`, **ou** efeito `Applied` na consulta |
| `Dispatching` | `Cancelled` (rejeição) | mesmo CAS | rejeição definitiva: `HandlingChanged`, `SessionClosed`, `AttachmentNotAllowed`, `ContentRejected` (como a idempotência é avaliada **antes** da pré-condição, essa rejeição prova que **não** foi aplicada) |
| `Dispatching` | `Expired` | mesmo CAS | classificação `NotApplied-Fenced` |
| `Dispatching` | `Dispatching` | `attempts + 1`, novo token | tentativa sem desfecho |

**Efeito aplicado contra tentativa não aplicada**, em três classes:

| Classe | Como se prova | Consequência |
|---|---|---|
| **`Applied`** | existe a mensagem com aquele `outbound_operation_id` (resultado de `RegisterHumanReply` ou de `GetEffectAsync(HumanReply)`) | finaliza como `Registered`, **sem nova chamada** |
| **`NotApplied-Fenced`** | `FenceHandlingAsync(tokenNovo)` bem-sucedido **e depois** `GetEffectAsync = NotFound`: nenhuma tentativa de token anterior pode mais gravar | pode continuar a tentativa ou, se vencida, expirar |
| **`Unknown`** | erro, `Unavailable`, ou `NotFound` **sem** a cerca confirmada | permanece `Dispatching`, com recuo. **Nunca cancela** e nunca conta como "não aplicada" |

**Protocolo do executor:**

1. Escolhe intenções em `Pending` ou `Dispatching` com `next_attempt_at ≤ agora`.
2. Tenta a trava consultiva da intenção; se não conseguir, **sai** (outro executor trabalha nela).
3. Relê sob a trava; se o estado já é terminal, devolve o resultado gravado.
4. **`Pending`:** reavalia o acesso e faz o CAS `Pending → Dispatching`, emitindo o token. Esse é o **instante de autorização**. Se o CAS não afetar linha (cancelada no meio), relê.
5. **`Dispatching` (recuperação):** **não reautoriza** (o instante já passou) e **não presume** o desfecho da tentativa anterior. Emite um token novo, registra a cerca com `FenceHandlingAsync(tokenNovo)` e consulta `GetEffectAsync(HumanReply)`: `Applied` → passo 7; `NotFound` → passo 6; `Unavailable` ou erro → registra a falha, aplica recuo e sai.
6. Monta o comando a partir da linha persistida (`base` e `content_snapshot`) e chama `RegisterHumanReply` com o token vigente. Se vencida (`expires_at`) e `NotApplied-Fenced`, **não chama**: `Expired`.
7. **Desfecho local**, por CAS `(state, token)`, na **mesma gravação** do recibo e da auditoria diferida (padrão da etapa 4): `Applied` ou `AlreadyApplied` → `Registered`, com a identidade da mensagem; `FencedOut` → `Superseded`, sem escrita; `StaleState` → relê **somente a `StateVersion`** e repete com a **mesma** pré-condição e o **mesmo** conteúdo; rejeição definitiva → `Cancelled(motivo)`; exceção ou tempo esgotado → falha registrada com recuo, permanecendo `Dispatching`.

**Intenção vencida (`expires_at`).** Nunca se cancela uma intenção com chamada modificadora: emite-se um token maior (cerca a tentativa em voo), consulta-se o efeito por `GetEffectAsync(HumanReply)`, e `Applied` → `Registered`; `NotFound` **depois da cerca** → `Expired`. O `expires_at` é validade operacional e **não** é a janela de 24 h do provedor.

**Pontos de queda:**

| Queda | Retomada |
|---|---|
| depois do token, antes da chamada | consulta `NotFound` com cerca → chama o registro |
| depois do registro, antes de gravar localmente (ou resposta perdida) | consulta `Applied` → finaliza, **sem segunda mensagem** |
| dois executores ao mesmo tempo | a trava deixa um só; sem a trava, o token menor recebe `FencedOut` ou reconhece `AlreadyApplied`; no pior caso, a idempotência garante uma mensagem |
| intenção vencida em `Dispatching` | cerca, depois consulta, depois `Registered` ou `Expired`, **nunca** por chamada modificadora |

**Invariantes:** no máximo **uma** mensagem por intenção; `Cancelled` e `Expired` só com a prova de não aplicação; o token é monotônico por intenção; estado terminal não muda; toda gravação local é CAS por `(state, token)`.

**`GetEffectAsync` ganha o tipo `HumanReply`**, por `OperationId` (consulta somente leitura, sem token, na mesma classe da 6ª operação; ADR 0010, decisão I). É acréscimo aditivo e não altera o contrato aprovado da consulta.

### C. Serialização entre mudança de permissão e intenção (C4)

O plano anterior dizia que "a remoção concorrente vale depois da intenção gravada", o que deixava uma janela em que um colaborador removido ainda respondia. A correção:

- **Camada 1, no próprio item** (participantes, política, responsável): serializada por trava de linha de `queue_items`. A **mudança de permissão** faz `SELECT … FOR UPDATE`, incrementa `authz_version` e, **na mesma transação, cancela as intenções ainda `Pending` do autor afetado**. A **gravação de intenção** faz `SELECT … FOR SHARE`, reavalia o roster sob a trava e grava a intenção com o `authz_version` lido. Respostas não bloqueiam umas às outras; mudança de permissão bloqueia respostas. Qualquer das ordens é correta: quem vem depois enxerga o efeito da primeira.
- **Camada 2, acima do item** (filiação à fila, papel, usuário desativado): não dá para incrementar todos os itens. O executor **reavalia o acesso ao vivo imediatamente antes** da primeira emissão do token (CAS sobre a própria linha da intenção).
- **Instante de autorização definido:** o do CAS `Pending → Dispatching`. Depois dele a remoção **não** cancela a resposta em voo (a mensagem já pode estar no `Conversations`); antes dele, cancela. As retentativas seguem pela idempotência.
- **Residual documentado:** a janela entre a reavaliação da camada 2 e o commit no `Conversations` é da mesma classe aceita na etapa 4 para `CanWork`: a autorização vale para o estado em que foi dada.
- **Cobertura da 5.1b:** política exclusiva (só o responsável); `authz_version`, a trava e o cancelamento por mudança de responsável já entram, e a 5.7 só acrescenta os participantes (ADR 0013).

### D. Autorização contextual de anexos

O pertencimento ao tenant **não basta**. Um anexo só entra na resposta se, **ao mesmo tempo**: está `Clean` (varredura concluída); pertence à **mesma sessão**, ou é upload do próprio autor para esta intenção; tem origem permitida (`Upload`, de uso único e preso ao `OperationId`; `SameSessionExternal`, reencaminhar ao cliente algo que ele mesmo enviou; ou `SameSessionOutbound`); e está **dentro dos limites de leitura do autor** (a sequência da mensagem de origem no intervalo que ele pode ler, ADR 0013).

- **O `ContactCenter` decide a parte do usuário** (o autor pode ler a origem?).
- **O `Conversations` impõe o estrutural:** busca o anexo por `(tenant, sessão, id)`, **nunca só por id**; recusa origem de outra sessão; exige `Clean`; recusa reuso de upload já consumido.
- Anexo de nota interna é **inalcançável** (ADR 0013): outro módulo, outra tabela, outro prefixo de armazenamento.

### E. Reenvio delegado e evidência de entrega (8ª operação)

`RecordDeliveryStatus` ganha `EvidenceSource = ProviderStatus | ProviderLookup | ProviderRejection | Manual` (ADR 0011, F). **`Manual` nunca produz `Failed`, `Expired` nem `Dead`.**

```text
RequestDelegatedResendCommand(
  SessionId, MessageId, RequestId,       // RequestId: chave de idempotência durável (índice único), gerada pelo cliente uma vez por ação do usuário e reutilizada nas repetições
  AuthorUserId, Kind, ReasonCode, Justification?,
  ReplyPrecondition Precondition, FenceToken)

Kind = ResendAfterDefinitiveFailure     // só Failed/Expired/Dead: a semântica atual, delegada
     | AcceptDuplicateRisk              // só SendUnknown
```

- Segue a **mesma ordem de validação** da seção A (idempotência somente leitura antes da cerca).
- O `RequestResend` direto **não muda**: o atendente comum, sem `ConversationManage`, só alcança o reenvio **pelo ContactCenter**, que decide o acesso e chama o gateway elevado, no mesmo padrão do ADR 0009.
- **`AcceptDuplicateRisk`** exige **permissão própria** (nome provisório `ReplyAcceptDuplicateRisk`, a definir na implementação), justificativa obrigatória, idempotência por `RequestId` e auditoria com o autor original. Cria uma **nova** mensagem de saída ligada à original por `ResendOfMessageId`, com a marca `DuplicateRiskAccepted` e aviso na interface. A original **continua `SendUnknown`**, ou segue a evidência se ela aparecer depois.
- Aceite parcial de e-mail (alguns destinatários aceitos, outros não): a mensagem fica `Sent` com o detalhe **por destinatário** em `ChannelData`; o reenvio aos recusados é decisão da 5.5.

### F. Superfície da porta e inventário de emissores

- A porta `IConversationGateway` passa de **seis para oito** operações **previstas por este ADR**: as seis atuais (ler sessão, ler histórico, transitar o modo, encaminhar, registrar a cerca e consultar efeito, a sexta só leitura) mais `RegisterHumanReply` e `RequestDelegatedResend` (e, pela segunda emenda do ADR 0009, a 9ª, `CloseSession` delegada: total previsto, nove). **Não há executor genérico de código privilegiado**, e a porta continua fechada: operação nova exige aprovação (e ADR, se mudar o modelo).
- O **inventário dos emissores** dos comandos privilegiados (teste de arquitetura) inclui os comandos **implementados**, e o teste de superfície da porta espera o número de operações **implementadas**: no MVP, só a 7ª (`RegisterHumanReply`) entra por este ADR, e a 8ª (`RequestDelegatedResend`) permanece Proposta e fora do MVP.
- **Só entram no código depois da aprovação do ADR 0009** (e deste) **e da autorização de implementação**. Até lá, a porta implementada continua com seis.

## Alternativas descartadas

- **Reaproveitar o slot `pending_operation_id` do item.** Comporta uma operação, não várias respostas concorrentes, e bloquearia respostas atrás de um `Assign`.
- **Mudar o `RequestResend` do `Conversations` para aceitar `SendUnknown` ou o atendente comum.** Enfraquece uma garantia aprovada; o caminho delegado resolve sem tocar nela.
- **Cancelar uma intenção vencida com chamada modificadora.** Poderia cancelar o que já foi aplicado.
- **Cerca antes da idempotência nas operações novas.** Um executor ultrapassado não poderia reconhecer o que já foi escrito e o sucessor repetiria o trabalho.
- **Confiar só na trava consultiva.** Presa à conexão, que pode se perder sem aviso; por isso L2 e L3.
- **Aceitar a declaração humana como prova de não envio.** Ausência de confirmação não é prova (EFX-3).

## Consequências

- **Concentração de privilégio:** duas operações de escrita novas no gateway elevado. Cada uma passa por ADR, inventário de emissores e testes de mutação.
- **Rastro dividido, como no ADR 0009:** o `Conversations` registra o componente; o `ContactCenter` registra o autor original (item, intenção e auditoria).
- O compositor do atendente ganha **idempotência de ponta a ponta**: repetir o envio devolve a mesma mensagem, nunca duas.
- A remoção de permissão passa a ser **imediata** para novas respostas e **cancelável** para as `Pending`.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Resposta duplicada por queda entre o registro e a gravação local | consulta de efeito `HumanReply`; L3 por `OperationId` |
| Colaborador removido ainda responde | `authz_version`, trava `FOR SHARE`/`FOR UPDATE`, cancelamento atômico e reavaliação no despacho |
| Anexo de outra sessão, de nota ou já consumido | autorização contextual em duas camadas; busca por `(tenant, sessão, id)`; upload de uso único |
| `SendUnknown` virar `Failed` por palavra humana | `Manual` nunca produz falha definitiva; sem prova, permanece `SendUnknown` |
| Reenvio duplicado por engano | `AcceptDuplicateRisk` só com permissão, justificativa, idempotência e auditoria |
| Operação privilegiada nova entrar sem revisão | superfície fechada em oito; inventário de emissores e este ADR |

## Critérios de aceitação

**C3 — `RegisterHumanReply`:**
- anexo de outra sessão, de nota interna ou já consumido é rejeitado; o `Conversations` não resolve identificador de anexo de nota (teste estrutural);
- repetição devolve o resultado original **mesmo após mudança de autorização**; mesmo `OperationId` com conteúdo, sessão ou autor diferentes retorna `OperationIdReused`, **inclusive com cerca ultrapassada**;
- efeito aplicado com o token T, cerca da sessão avançada para T+k, repetição com T → `AlreadyApplied(Overtaken)`, sem mensagem nem evento novos e cerca inalterada; efeito **não** aplicado com token ultrapassado → `FencedOut`, nada gravado;
- `HandlingChanged`, `StaleState` e `FencedOut` não gravam; autor original preservado após recuperação por outro processo;
- um teste de queda entre a mensagem e o evento prova que um não existe sem o outro;
- mutações: "cerca antes da idempotência" e "repetição emite evento" deixam vermelhos os testes indicados.

**C3 — pré-condição não renovável:**
- intenção com base `(P, E, R, Exact)`: se o período, a `PlacementEpoch` ou o responsável mudarem, `RegisterHumanReply` retorna `HandlingChanged`, nada é gravado e a intenção vai a `Cancelled(HandlingChanged)`; com `Any`, trocar só o responsável não falha, e trocar período ou colocação falha;
- `StaleState` seguido de repetição com a versão renovada e a **mesma** pré-condição resulta em **uma** mensagem;
- versão e período divergindo juntos retornam `HandlingChanged`, não `StaleState`;
- recuperação em `Dispatching` por outro processo, depois de o responsável da sessão mudar: `HandlingChanged`, nunca escrita sobre a conversa do novo responsável;
- base sem geração de colocação deixa a intenção inerte;
- mutações vermelhas: "recalcular a pré-condição a partir da sessão relida na retentativa ou na recuperação"; "renovar a base junto com a versão no `StaleState`"; "`StaleState` avaliado antes de `HandlingChanged`";
- os testes de D.20 a D.23 da etapa 4 permanecem verdes e inalterados.

**C3 — hash de idempotência:**
- mesmo `OperationId` e mesmo efeito, com número de tentativas, processo, `StateVersion`, cerca, instante e autorização diferentes, resulta em `AlreadyApplied` com o resultado original;
- **um teste por campo coberto:** alterar texto, cada destinatário ou papel, citação, cada anexo (identificador, origem e `content_sha256`), cada chave de envio do `ChannelData` (assunto, remetente, cabeçalho permitido), referência ou `body_sha256` do corpo, ou o `Kind` ou a mensagem original no reenvio, resulta em `OperationIdReused`;
- reordenar destinatários do mesmo papel mantém o hash; reordenar anexos o altera;
- alterar `Precondition`, `ExpectedStateVersion`, `FenceToken`, `authz_version`, instantes ou estado de varredura **não** altera o hash;
- retomada por outro processo, depois de queda, usa o `content_snapshot` e produz o mesmo hash e **uma** mensagem;
- hash informado pelo chamador é ignorado (teste com hash errado ou forjado);
- chave de `ChannelData` não declarada pelo adaptador é rejeitada;
- linha com `hash_version` antiga continua comparável;
- mutações vermelhas: remover cada campo da forma canônica faz falhar o teste do respectivo campo; incluir um campo volátil faz falhar o teste de retentativa após `StaleState` (`OperationIdReused` indevido).

**C3-cliente — duplo clique e repetição:**
- duas requisições **concorrentes** com o mesmo `ClientRequestId` resultam em **1** intenção, **1** `OperationId`, **1** mensagem, **1** `OutboundMessageRequestedV1` e **1** linha em `channels.outbound_message`; as duas respostas trazem o mesmo `OperationId`;
- repetição depois do desfecho (`Registered`, `Cancelled` ou `Expired`) devolve o resultado original, sem intenção nem mensagem nova, **mesmo que a autorização do autor tenha mudado**;
- queda do servidor entre o commit da intenção e a resposta HTTP: a repetição com a mesma chave devolve o mesmo `OperationId` e resulta em **uma** mensagem;
- mesma chave com conteúdo diferente, ou com outro item ou sessão, retorna `ClientRequestIdReused`, sem efeito;
- chave ausente é rejeitada (400), sem intenção;
- a mesma chave enviada por **outro autor** ou **outro tenant** não colide nem devolve resultado alheio;
- nova chave com o mesmo texto cria **outro** envio;
- a repetição **não** muda o `client_payload_hash` quando o contato ou o cadastro mudou entre os cliques;
- no reenvio delegado, a repetição do mesmo `RequestId` produz **uma** nova mensagem;
- mutações vermelhas: remover o índice único (concorrência gera 2 intenções); gerar `OperationId` novo na repetição; reavaliar a autorização antes de reconhecer a repetição; aceitar chave ausente gerando uma no servidor; `ON CONFLICT` sem alvo; comparar a repetição pelo `request_hash` materializado em vez do `client_payload_hash`.

**C4 — intenções e permissões:**
- a emissão do token funciona com um `Assign` pendente no mesmo item;
- teste determinístico (`Interceptador`) das duas ordens (remoção antes e depois da intenção): remoção antes impede a intenção, depois cancela a que estava `Pending`; reavaliação no despacho nega a camada 2; a intenção em `Dispatching` **não** é cancelada; mutação sem a trava deixa o teste vermelho;
- queda em cada um dos quatro pontos produz **exatamente uma** mensagem; dois executores simultâneos produzem uma; `Unknown` nunca cancela; intenção vencida resolve por cerca e consulta, nunca por chamada modificadora;
- mutações: "cancelar em `Dispatching` sem a cerca" e "reautorizar em `Dispatching`" ficam vermelhas.

**C6 — reenvio e evidência:**
- `Manual` nunca produz `Failed`, `Expired` ou `Dead`; busca sem resultado, tempo esgotado e declaração humana deixam `SendUnknown`;
- `AcceptDuplicateRisk` exige permissão, justificativa, idempotência e auditoria;
- `RequestResend` direto continua recusando `SendUnknown`, e o atendente sem permissão no `Conversations` só reenvia pelo caminho delegado;
- o Sender nunca retenta `SendUnknown` (mutação).

**Superfície:** o inventário de emissores e o teste da porta refletem o número de operações **implementadas** (no MVP, a 7ª entra por este ADR; o total previsto por este ADR é oito, com a 8ª Proposta); a matriz e o grafo de projetos permanecem iguais (ARQ-06).

**Geral:** suíte completa e mutações da etapa 4 verdes; nenhuma garantia aprovada enfraquecida.

## Pendências

- **DP-01 (03/11/2026):** a aprovação parcial deste ADR e da emenda do ADR 0009 (aceito parcialmente; o 0010 foi aceito) libera, **depois da autorização de implementação**, a 7ª operação na 5.1b. **A 8ª operação permanece Proposta e fora do MVP.**
- **DP-08 (aprovado em 03/11/2026):** `ConfirmedSent` manual, com origem `Manual`, permissão específica, justificativa e auditoria; só avança o estado mediante a justificativa registrada, e nunca provoca reenvio automático nem duplicidade. Na interface, `SendUnknown` aparece como envio de resultado incerto.
- **Podem permanecer para a fatia:** reenvio aos recusados no aceite parcial de e-mail (5.5); nomes definitivos das permissões (na implementação).
