# ADR 0012 — Numeração por tenant e associação de mensagens a atendimentos (chat e e-mail)

- **Status:** **Aceito parcialmente (03/11/2026), escopo de chat.** Nada deste ADR está implementado.
- **Registro de aprovação (03/11/2026, decisor):** aprovado o **escopo de chat**: a unicidade de sessão aberta do chat (seção A, índice de chat), o modo de associação do chat, `Contact` (seção B), a **numeração por tenant** com o backfill documentado, sem renumerar, nos dados de desenvolvimento (seção C), o `Down` (seção E) e, da seção F, as migrações 3 e 4 e **a parte de chat da migração 5** (limites alargados, `association_mode`, `thread_key` anulável e o índice de chat, com a regressão da etapa 4); critérios B1 (parte de chat: concorrência de chat, mutações M-1a e M-1d) e B2. O corte exato da migração 5 é definido no plano da 5.1b. **Permanecem Propostos e fora da implementação inicial (e-mail):** a associação de e-mail por cabeçalhos (seção D), o modo `Thread`, o índice de thread, `session_links`, `sender_contact_id`, a migração 7, o critério C7 e as mutações M-1b, M-1c e M-1e. Nenhuma migração ou backfill foi executado: cada aplicação exige autorização específica (DP-07). A aprovação de uma parte não aprova o restante.
- **Data:** 30/10/2026 (aprovação parcial: 03/11/2026)
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` v0.4.6 (seções 5, 6 e 12), `docs/architecture/CONVERSATION-LIFECYCLE.md`, `docs/architecture/ETAPA-5.md`, ADRs 0006, 0007, 0008 e 0011, `CLAUDE.md`
- **Relacionados:** 0011 (Channels, aceito parcialmente), 0013 (colaboração, Proposto), 0014 (resposta humana externa, aceito parcialmente), 0015 (e-mail e SMS, Proposto)
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**), os ADRs 0001 a 0010 e todas as garantias da etapa 4 (`HandlingPeriod`, `PlacementEpoch`, `ScopeProof`, cerca, idempotência, obrigação de ressincronização). A suíte de sessão da etapa 4 e as mutações existentes **devem continuar verdes**.
- **Registra:** mudança no **modelo de dados do `Conversations`** (autoridade do estado e do contexto): troca da unicidade de sessão aberta, número de atendimento por tenant, modo de associação e chave de thread. Mudança de dono de dados não há: tudo permanece no `Conversations`.
- **Revisão (03/11/2026):** incorpora a decisão funcional G2 do proprietário (associação adiada, recuperação limitada e reprocessamento antes de separar sessões; vínculo explícito e leitura de sessões vinculadas com autorização própria). Essa decisão não aprova este ADR.

## Contexto

Três fatos do código atual condicionam a etapa 5 (evidências lidas em 30/10/2026):

1. **Uma sessão aberta por contato.** O índice único parcial é `(tenant_id, contact_id) WHERE status = 1` (`SessionAndContactConfigurations.cs`, `OneOpenPerContactIndex`), e a criação usa `ON CONFLICT (tenant_id, contact_id) WHERE status = 1 DO NOTHING` por **SQL cru** (`ConversationStore.cs`). `ServiceSession.Create` só é usado em testes unitários. O e-mail precisa de **várias** sessões abertas por remetente (uma por thread).
2. **Não há número de atendimento.** O `CLAUDE.md` fala em "atendimento #1001", mas a sessão só tem identificador técnico.
3. **Limites estreitos.** `ContactRefMaxLength = 128` e `ProviderEventIdMaxLength = 200` (`ConversationLimits.cs`) não comportam endereço de e-mail (até 254) nem alguns identificadores de provedor.

O plano inicial errou em dois pontos que a auditoria independente apontou e este ADR corrige: o índice `(tenant_id, contact_id, thread_key)` **quebraria a unicidade nos canais de chat** (no PostgreSQL, `NULL` é distinto por padrão num índice único) e **prenderia a thread de e-mail ao remetente**; e a alocação do número antes do `INSERT … ON CONFLICT DO NOTHING` **queimaria um número a cada conflito**.

## Decisão

### A. Unicidade de sessão aberta: chat e e-mail

1. **Dois tipos de sessão, dois índices parciais explícitos:**

```sql
-- chat (WhatsApp, Instagram, Messenger, SMS): preserva o invariante atual
CREATE UNIQUE INDEX ux_service_sessions_open_chat
  ON conversations.service_sessions (tenant_id, contact_id)
  WHERE status = 1 AND thread_key IS NULL;

-- e-mail: caixa + thread, INDEPENDENTE do remetente
CREATE UNIQUE INDEX ux_service_sessions_open_thread
  ON conversations.service_sessions (tenant_id, channel_instance_id, thread_key)
  WHERE status = 1 AND thread_key IS NOT NULL;
```

   O índice antigo é removido **na mesma migração**. As sessões existentes têm `thread_key` nulo, então o comportamento delas não muda.
2. **Por que não `NULLS NOT DISTINCT`** (PostgreSQL 15 ou superior; o projeto usa o 17): um único índice `(tenant_id, contact_id, thread_key) NULLS NOT DISTINCT` resolve o chat, mas **mantém o contato na chave do e-mail**, que é o defeito. Dois índices resolvem os dois casos. `NULLS NOT DISTINCT` fica só como alternativa ao índice de chat.
3. **`ON CONFLICT`:** dois comandos de inserção, escolhidos por `threadKey is null`, cada um com **alvo e predicado explícitos** que casam com o índice. **É proibido `ON CONFLICT DO NOTHING` sem alvo**: ele engoliria violações de outros índices únicos, como o de `session_number`.
4. **Modelo EF:** `OneOpenPerContactIndex` é substituído por dois `HasIndex(...).IsUnique().HasFilter(...)`. `thread_key` é texto com `CHECK (thread_key IS NULL OR length(thread_key) > 0)`.
5. **Contato da sessão de e-mail.** `contact_id` passa a ser o **contato iniciador** e **nunca entra na chave de associação**. Cada mensagem recebida guarda o remetente real em `messages.sender_contact_id` (anulável; nulo em saída e em nota). Quem responde numa thread existente é só mais um contato.
6. **Limites alargados** (`ContactRefMaxLength`, `ProviderEventIdMaxLength`) na mesma fatia, com teste de limite.

### B. Modo de associação explícito (contrato da ingestão)

```text
IngestInboundMessageCommand += (
  AssociationMode: Contact | Thread,
  ThreadKey?,                          // obrigatório se e somente se Mode = Thread
  SenderContact,                       // remetente real da mensagem
  Attachments[], Category = Private, ChannelData?)
```

- Validação: `(Mode == Thread) ⇔ (ThreadKey != null)`. Mensagem em modo `Thread` sem chave é **rejeitada**; **nunca** é rebaixada para modo `Contact` (rebaixar trataria e-mail como chat).
- Invariante de banco: `service_sessions.association_mode` (`NOT NULL`), com `CHECK ((association_mode = Thread) = (thread_key IS NOT NULL))`.
- Chave de associação por canal: WhatsApp, Instagram, Messenger e SMS usam **instância + contato**; e-mail usa **caixa + thread**. **Nunca** se associa por assunto ou endereço apenas.
- A concorrência de duas mensagens do mesmo contato novo continua resolvida pela lane de ingestão (`BOUNDARIES.md` §12.3) e, como proteção de última instância, pelos índices únicos.

### C. Numeração por tenant: protocolo transacional

**Onde:** na `ServiceSession` (o número identifica o atendimento; a sessão nasce na primeira mensagem, ainda sem item de fila). **Nunca é chave técnica**: a chave segue `(tenant_id, id)`, e nenhum contrato interno passa a usar o número como identidade. A exibição (`#000184`) é só formatação.

**Protocolo**, dentro da transação de ingestão (a mesma da mensagem e do evento):

1. **Caminho rápido, sem trava:** procurar a sessão aberta pela chave de associação. Se existe, **reutilizar. Nenhum número é consumido.**
2. **Garantir e travar o contador:** `INSERT INTO conversations.tenant_counters(tenant_id, last_number) VALUES (@t, 0) ON CONFLICT DO NOTHING`, seguido de `SELECT last_number … FOR UPDATE`.
3. **Reconferir sob a trava.** Se outra transação criou a sessão enquanto esperávamos, reutilizar e **sair sem alterar o contador**.
4. Se não existe: `UPDATE tenant_counters SET last_number = last_number + 1`, usar o novo valor e fazer o **`INSERT` da sessão já com `session_number`**. Como a trava serializa os criadores do tenant, o conflito de chave não ocorre; se ocorrer (0 linhas), a transação **aborta** em vez de seguir.
5. **Rollback desfaz as duas escritas juntas.**

**Regras:**
- **Ordem de trava:** o contador é sempre travado **antes** da linha da sessão (`LockSessionAsync`), e só no caminho de criação. Não há ciclo de espera.
- **Isolamento:** `tenant_counters` ganha filtro de tenant e guarda de `CrossTenantAccess`, como as demais tabelas.
- **Modelo:** `session_number bigint NOT NULL`, `ValueGeneratedNever`, índice único `(tenant_id, session_number)`. A fábrica `ServiceSession.Create` ganha o parâmetro (os testes unitários a usam); a rota de produção, por SQL cru, é a única que persiste.
- **Alternativas rejeitadas:** `SEQUENCE` por tenant (DDL por tenant e lacunas), `MAX()+1` (corrida), sequência global (não é por tenant e expõe volume). **Faixas reservadas** ficam como plano B **se** a medição mostrar o contador como gargalo, aceitando lacunas.

**Garantia qualificada** (a versão anterior, "sem lacunas", era mais forte do que o protocolo sustenta):
- **Contiguidade transacional:** rollback, conflito e reutilização **não consomem** número.
- **Lacunas históricas aceitas:** exclusão e retenção (LGPD, expurgo) podem criar lacunas. O número **nunca é reemitido**.
- **Invariantes verificáveis por consulta:** sem expurgo, `last_number = max(session_number)` e a sequência é contígua; com expurgo, `last_number ≥ max(session_number)`, o número é único e nunca reemitido, e a lacuna só existe no ponto do expurgo.

**Backfill sem renumerar** (única migração de dados; só há dados de desenvolvimento, e a aprovação é do decisor, DP-07): adicionar a coluna anulável; preencher **somente** `session_number IS NULL`, em ordem `(created_at, id)`, a partir de `COALESCE(max(session_number), 0)` do tenant; semear o contador com `GREATEST(contador atual, máximo)`; `ALTER COLUMN SET NOT NULL`; criar o índice único. Rodar duas vezes atualiza **zero** linhas, e as linhas já numeradas **não mudam**. **Expansão e contração:** se um dia houver produção sem parada, o caminho é o de três passos (escrever o número com a coluna anulável, preencher, contrair).

**Reabertura.** Mensagem sobre atendimento já encerrado (ou thread encerrada): o padrão proposto é **atendimento novo ligado ao anterior**, como no comportamento atual, com janela configurável (DP-09).

### D. Associação de e-mail por cabeçalhos

**Identificadores** normalizados (sem `<>`, domínio em minúsculas): `Message-ID` próprio, `In-Reply-To`, `References[]`. Os identificadores de thread do provedor (Graph `conversationId`, Gmail `threadId`) são **só dica**.

**Índice** `channels.email_message_index(tenant, mailbox, message_id) → thread_key`: cada entrada é **imutável** (o primeiro a escrever vence, `ON CONFLICT DO NOTHING`) e tem tipo **vista** (a mensagem foi ingerida) ou **provisória** (identificador apenas referenciado por outra mensagem). **Nenhuma sessão muda de `thread_key`.**

**Algoritmo**, por mensagem, dentro da lane da caixa:

1. Conjunto de consulta `R = [In-Reply-To] ++ References` mais o `Message-ID` próprio, numa única consulta ao índice.
2. **Uma thread conhecida** entre as encontradas → usa essa.
3. **Nenhuma conhecida, mas há referências ainda não vistas** → **associação adiada**: o evento fica `PendingAssociation(ReferenciaPendente)` no Inbox e **não cria sessão nem ponteiro** ainda. Enquanto o limite não se esgota, aplicam-se: (i) **reprocessamento** a cada mensagem ingerida (ou ponteiro registrado) que corresponda a um identificador pendente; (ii) **recuperação limitada**: busca ativa dos identificadores referenciados, pelo adaptador, dentro do `mailbox_sync_scope`, até a profundidade máxima, **sem ampliar o escopo**; as mensagens buscadas entram pelo Inbox normal (deduplicadas), nunca direto na sessão; (iii) ao resolver (thread conhecida, ou cadeia até a raiz), a mensagem entra na **sessão única** da cadeia. **Esgotado o limite sem resolução**, materializa-se como hoje: `thread_key = primeiro(References)`, senão `In-Reply-To` (qualidade `Provisional`), com ponteiros provisórios para **todos** os identificadores referenciados e o próprio (quando o pai chegar com um identificador já apontado, entra na mesma sessão), e aplica-se o G2.
4. **Várias threads distintas (conflito)** → **sem fusão automática e sem re-chaveamento de sessão existente** (nada de conteúdo, histórico, número, responsável ou `ScopeProof` é movido). Regra determinística, em ordem: thread do `In-Reply-To`, se conhecida; senão a da referência conhecida **mais próxima** (a última de `References`); senão a thread da sessão de **menor** `session_number`. Grava-se `ConflictNote` (em `ChannelData`), evento de auditoria e alerta ao atendente.
5. **Sem referências, com `Message-ID`** → thread nova, `thread_key` = o próprio identificador (qualidade `Known`).
6. **Sem nenhum identificador** (cabeçalho malformado) → `thread_key` **sintético**, derivado de caixa e identificador do provedor (qualidade `Synthetic`). Nunca se associa por assunto ou endereço.
7. **Associação impossível de determinar** (índice indisponível, dados inconsistentes) → o evento fica `PendingAssociation` no Inbox, com retentativa, recuo e alerta; passado o limite, vai para `channels.inbox_quarantine`, visível ao administrador. **Nunca** vira sessão de chat nem é anexado por palpite.

Qualidade da associação: `EmailAssociationResult(ThreadKey, Quality: Known | Provisional | Synthetic | Indeterminate | Conflicted, ConflictNote?)`.

**Serialização por caixa.** A lane de associação do e-mail é a **caixa** (não caixa mais thread candidata), com `pg_advisory_xact_lock` sobre a caixa dentro da transação de ingestão. Sem isso, mensagens da mesma conversa em lanes diferentes disputariam as entradas do índice, e a divergência dependeria de corrida, não da ordem de chegada. O volume de e-mail por caixa é de escala humana, então o custo é aceitável. Os índices únicos de sessão continuam como proteção de última instância.

**Limites da associação adiada** (por caixa, configuráveis): tempo máximo de espera, tentativas, profundidade da recuperação e escopo contratado. **Os valores padrão são decisão da 5.5**; este ADR não fixa números. A recuperação ativa é capacidade **declarada** do adaptador (buscar mensagem por identificador), sujeita à PRV-2 estendida; adaptador sem ela só usa o reprocessamento. Eventos em `PendingAssociation` são **duráveis** (nada se perde), visíveis ao administrador com contagem e idade, e geram alerta quando passam do limite. Mensagem **sem referências** (raiz) nunca é adiada.

**Garantia G1 (provada).** *Premissa P\*:* toda mensagem de uma conversa lista a **raiz** `r` em `References` (cadeia completa, como recomenda a RFC 5322), ou é a própria raiz. *Afirmação:* para **qualquer** ordem de chegada, todas caem na **mesma sessão**. *Prova:* com P\*, o conjunto de consulta `S(m)` contém `r` para toda mensagem `m`. A primeira a ser materializada (por resolução ou ao esgotar o limite) cria a thread com chave `r` e registra `r → T`; toda seguinte encontra `r` no índice e entra em `T`. Só haveria outra thread em `S(m)` se `m` referenciasse **outra conversa**, que é o caso de conflito, fora desta garantia. Cadeia A, B, C com `References` completo: todas as 6 permutações resultam em **1 sessão**.

**Garantia G2 (restrita), decisão do proprietário de 30/10/2026.** A associação converge para **uma sessão** sempre que as evidências dos cabeçalhos forem suficientes para determinar a identidade da conversa **e** a cadeia for resolvida **dentro dos limites definidos** (tempo, tentativas, profundidade e escopo contratado; parágrafo "Limites da associação adiada"). A chegada fora de ordem, por si só, não cria sessões separadas definitivas: enquanto houver resolução segura possível dentro dos limites, usam-se a associação provisória (adiada), a recuperação limitada e o reprocessamento.

**Qualificação.** A convergência **não é garantida** quando (i) as referências são insuficientes para determinar a identidade da conversa (por exemplo, `References` truncado sem mensagem-ponte disponível, ou referência a mensagem fora do escopo contratado) ou (ii) os limites se esgotam antes da resolução. Nesses casos vale o **G2 já aprovado**: sessões separadas, **sem fusão automática e sem re-chaveamento**. Uma mensagem-ponte que chegue depois aciona a regra determinística, `ConflictNote`, auditoria, alerta e `conversations.session_links(tenant, session_id, related_session_id, kind = EmailReferenceConflict, caused_by_message_id)` (único por `(tenant, session_id, related_session_id, kind)`); o vínculo só nasce quando uma mensagem-ponte prova a relação. **Nunca se infere vínculo por assunto ou remetente.** A fusão manual de sessões é futura.

A garantia **G1** (referências completas, premissa P\*) **não depende dos limites**: esgotado o limite, a materialização usa `primeiro(References)`, que sob P\* é a raiz, e a convergência segue provada.

**Sessões vinculadas na leitura e na interface.**
- **O vínculo não concede nada.** Mostrar o número, o link e a navegação para a sessão vinculada exige que o usuário esteja **autorizado a ler a sessão-alvo** pelo caminho normal (tenant, fila ou participação, `ScopeProof`, `HandlingPeriod`, `PlacementEpoch` e **os próprios limites históricos dele**, ADR 0013).
- Quem lê só uma das sessões **não vê** número, contato ou conteúdo da outra. A interface (5.8) exibe, para qualquer leitor da sessão, o aviso de **limitação de associação** dela, sem identificador algum da outra.
- Para quem lê as duas, a interface sinaliza "sessões vinculadas" e permite navegar, cada sessão com os limites do leitor.

**Melhoria sem garantia:** dentro de uma página de sincronização, processar primeiro as mensagens com menos referências (e `Date` mais antiga) reduz divisões no caso comum. Não altera as cláusulas acima.

### E. `Down` do índice antigo

Depois que existir um contato com **várias threads abertas**, recriar `UNIQUE (tenant_id, contact_id) WHERE status = 1` é **inviável**. O `Down` fica **condicional**: verifica antes e **aborta com a lista dos contatos em conflito, sem alterar nada**. O rollback **operacional** é desativar o canal de e-mail e encerrar ou consolidar as threads excedentes **antes** de qualquer `Down`.

### F. Migrações propostas (nenhuma criada nem aplicada)

| # | Migração | Risco |
|---|---|---|
| 3 | `tenant_counters` e `session_number` anulável | baixo |
| 4 | **backfill** do número (sem renumerar) e `NOT NULL`, mais o índice único | médio; **exige aprovação** (dado) |
| 5 | `association_mode`, `thread_key`, `sender_contact_id`, troca do índice único parcial pelos dois índices, limites alargados, `session_links` | **médio-alto** (regressão da etapa 4) |
| 7 | tipo e estado de verificação do `Contact` | baixo |

Todas aditivas em dados, com `Down` (a do item 5 é condicional) e plano de rollback; só aplicadas com autorização expressa. A migração 5 precisa estar verde, com regressão da etapa 4 e as cinco mutações, **antes** de qualquer outra coisa da 5.1b.

## Alternativas descartadas

- **Índice único `(tenant_id, contact_id, thread_key)` com `thread_key` nulo no chat.** Permite várias sessões abertas por contato (o `NULL` é distinto) e prende o e-mail ao remetente.
- **`NULLS NOT DISTINCT` único para os dois casos.** Mantém o contato na chave do e-mail.
- **Alocar o número antes do `INSERT … ON CONFLICT DO NOTHING`, ou a cada mensagem.** Queima número em conflito e em reutilização.
- **`ON CONFLICT DO NOTHING` sem alvo.** Esconde violação de `session_number`.
- **Associar por assunto ou endereço, ou fundir sessões automaticamente no conflito.** Associa ao atendimento errado e move histórico, responsável e `ScopeProof` (bloqueador 6).
- **Re-chaveamento de sessão existente quando o pai chega.** Invalida a identidade da sessão e o que foi concedido sobre ela.

## Consequências

- Muda o índice que sustenta uma invariante central da etapa 4. Por isso a migração 5 é isolada, reversível de forma condicional e protegida por regressão e mutações.
- O contador por tenant é uma **linha quente de curta duração**, restrita à criação de sessão. O limite vem do plano de carga; se doer, faixas reservadas (com lacunas).
- Em e-mail, o `contact_id` da sessão deixa de dizer "quem é o cliente da sessão": é o iniciador. A exibição usa o remetente real de cada mensagem.
- A heurística por cabeçalhos só divide uma conversa em duas sessões (G2) **sem evidências suficientes ou depois de esgotados os limites**, depois de tentar a associação adiada, a recuperação limitada e o reprocessamento. Isso é **seguro** (nada é perdido nem misturado) e **visível** (aviso de limitação de associação e, quando uma mensagem-ponte prova a relação, vínculo e alerta).
- A associação adiada atrasa a chegada ao atendente de uma mensagem que referencia outra ainda não vista, até a resolução ou o limite. Por isso o limite é curto, configurável por caixa e visível ao administrador (valores padrão: decisão da 5.5).

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Troca do índice quebra a unicidade do chat | dois índices explícitos; teste concorrente de chat; mutações M-1a e M-1b |
| `ON CONFLICT` esconder violação de outro índice | alvo e predicado explícitos; mutação M-1d |
| E-mail tratado como chat | `Thread` sem chave rejeitado e `CHECK` de banco; mutação M-1e |
| Número duplicado ou reemitido | protocolo sob trava, índice único, teste de estresse e de reutilização |
| `Down` apagar a capacidade do e-mail | `Down` condicional e rollback operacional |
| Conversa de e-mail dividida | só sem evidências suficientes ou depois de esgotados os limites (G2); associação adiada, recuperação limitada e reprocessamento antes; vínculo, nota de conflito e alerta quando uma mensagem-ponte prova a relação; sem fusão automática |
| Atraso na chegada de mensagem com referência pendente | limite curto e configurável por caixa; `PendingAssociation` visível ao administrador, com contagem, idade e alerta; mensagem raiz nunca é adiada |
| Corrida entre mensagens da mesma conversa | serialização por caixa; índices únicos de última instância |
| Vazamento da existência de outra sessão pelo vínculo | vínculo visível só a quem lê as duas sessões |

## Critérios de aceitação

**B1 — unicidade:** os dois índices existem como especificado e o antigo foi removido; teste concorrente de chat (N ingestões do mesmo contato geram **exatamente 1** sessão aberta); teste concorrente de e-mail (N threads do mesmo remetente geram N sessões; a mesma thread com 2 remetentes gera 1); a suíte de sessão da etapa 4 e as mutações existentes continuam verdes. **As cinco mutações deixam vermelhos exatamente os testes indicados**, uma proteção por vez:

| Mutação | Teste que precisa ficar vermelho |
|---|---|
| **M-1a:** remover o índice de chat, ou colocar `thread_key` na chave **sem** `NULLS NOT DISTINCT` | concorrência de chat: N ingestões geram mais de 1 sessão aberta |
| **M-1b:** remover `AND thread_key IS NULL` do índice de chat | e-mail: duas threads do mesmo remetente deixam de ser permitidas |
| **M-1c:** remover o índice de thread | e-mail: a mesma thread duplica a sessão |
| **M-1d:** `ON CONFLICT DO NOTHING` sem alvo | violação induzida de `session_number` é engolida |
| **M-1e:** rebaixar `Thread` sem `ThreadKey` para `Contact` | a mensagem passa a ser tratada como chat |

**B2 — numeração:** estresse concorrente por tenant sem lacuna (sem expurgo); rollback forçado depois da alocação reaproveita o número; reutilização de sessão não move o contador; dois tenants com contadores independentes; expurgo não reemite; backfill idempotente e sem renumerar linhas já numeradas (teste com linhas mistas); a mutação "alocar antes de reconferir" deixa o teste de reutilização vermelho.

**C7 — associação:**
- **Referências completas (P\*):** todas as **24 permutações** de uma cadeia de 4 mensagens resultam em **1 sessão**, independentemente dos limites (G1).
- **Cadeia truncada** (A raiz; B `In-Reply-To: A`; C `References: [B]`; D `References: [C]`), **com evidências suficientes e a cadeia resolvida dentro dos limites** (as quatro mensagens chegam, ou são recuperadas, antes do esgotamento): todas as 24 permutações resultam em **1 sessão**, inclusive D, A, C, B.
- **Sem evidências suficientes** (B nunca disponível): [A] e [C, D] ficam em **2 sessões**, sem vínculo (nada prova a relação), sem fusão, sem inferência por assunto ou remetente e com as mensagens preservadas.
- **Limites esgotados** (relógio controlado) e mensagem-ponte chegando depois: **2 sessões com vínculo**, `ConflictNote`, auditoria e escolha determinística, sem fusão.
- Mensagem adiada **nunca se perde** e é materializada ao esgotar o limite.
- B e C ingeridas ao mesmo tempo resultam num dos resultados sequenciais possíveis, nunca num terceiro; sem identificadores gera thread sintética nova, nunca chat; associação indeterminável passa por `PendingAssociation` e quarentena; mensagem duplicada é inofensiva; vínculo invisível a quem lê só uma das sessões; a navegação para a sessão vinculada exige a autorização da sessão-alvo, com os limites do leitor.
- Mutações, cada uma deixando vermelho o teste indicado: sem serialização por caixa; criar a sessão sem adiar (teste das permutações); adiar sem limite (teste de expiração); recuperar fora do escopo contratado; associar por assunto ou remetente; fusão automática no conflito; re-chaveamento de sessão existente; escolha pela última referência em vez do `In-Reply-To`; mostrar o vínculo a quem lê só uma das sessões; navegar sem autorização da sessão-alvo.

**`Down`:** recusa, com a lista de conflitos, quando há várias threads por contato, sem alterar nada.

**Geral:** suíte completa e mutações da etapa 4 verdes; nenhuma garantia aprovada enfraquecida.

## Pendências

- **DP-07** e **DP-09** foram decididas em 03/11/2026: o planejamento das migrações por fatia (com `Down`, rollback e backfill documentado) e o backfill de numeração nos dados de desenvolvimento, sem renumerar, estão aprovados **documentalmente**, **sem execução**; a reabertura é um **novo atendimento vinculado ao anterior**, sem reabrir automaticamente a sessão encerrada (a janela configurável fica para evolução posterior). As migrações 5 (parte de e-mail) e 7 seguem Propostas.
- Fusão manual de sessões de e-mail: **futura**, fora da etapa 5.
- **Podem permanecer para a fatia:** valores padrão dos limites da associação adiada (tempo, tentativas, profundidade) e a capacidade de busca por identificador de cada adaptador, que depende da PRV-2 estendida (5.5).
- A sincronização e a ressincronização de caixa estão no ADR 0015; a retenção por tipo de dado, antes de gravar dado real.
