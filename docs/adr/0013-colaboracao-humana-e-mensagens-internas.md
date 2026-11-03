# ADR 0013 — Colaboração humana no atendimento, limites de leitura e mensagens internas

- **Status:** Proposto (30/10/2026). Aguarda a aprovação do decisor. Nada deste ADR está implementado, e nenhum outro documento o trata como aceito.
- **Data:** 30/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` v0.4.6 (seções 5 e 6), `docs/architecture/CONVERSATION-LIFECYCLE.md`, `docs/architecture/ETAPA-5.md`, ADRs 0005, 0007, 0009, 0010, 0011, 0012 e 0014, `CLAUDE.md`
- **Relacionados (Propostos):** 0014 (a autorização atômica de cada resposta externa e a intenção de resposta ficam lá)
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**; tudo no `ContactCenter`, que já depende de `Conversations`, `Identity`, `Tenancy` e `Audit`), os ADRs 0001 a 0010 e as garantias da etapa 4 de leitura do histórico (`ScopeProof`, `HandlingPeriod`, `PlacementEpoch`, fronteiras certificadas, `ReadAuthority`).
- **Registra:** mudança no **modelo de autorização** (`BOUNDARIES.md` §11): mais de uma pessoa passa a atuar sobre o mesmo atendimento, com concessões de leitura **próprias e limitadas**, e nasce um tipo de conteúdo (nota interna) **estruturalmente impedido** de chegar ao cliente e à IA.

## Contexto

Hoje o item de fila tem **um** responsável. O `Conversations` é dono do responsável e do modo (`ResponderRef`); o `ContactCenter` decide acesso, filas e políticas. A decisão de produto é permitir **colaboradores** no atendimento (responsável principal mais outros) e **comunicação interna** ligada ao atendimento, sem que nada interno saia para o cliente, e com extensibilidade para **agentes de IA** como participantes futuros.

Três riscos dominam: (1) **um participante novo ler o passado** que não lhe cabe; (2) **uma nota interna chegar ao cliente, ao Sender ou ao contexto de IA** (bloqueador); (3) **leituras por caminhos laterais** (citação, anexo, notificação, prévia da lista) vazarem o que a leitura principal protege.

## Decisão

### A. Participantes e política de resposta

1. **Tabela** `contact_center.item_participants` (fatia 5.7). Atributos: `ParticipantRef(Kind: HumanUser | AiAgent, Id)`, papel (`Owner`, `Collaborator`, `Observer`), indicação de **pode responder externamente**, quem adicionou, início e saída (**nunca apaga, só encerra**), `history_from_seq`, `internal_from_seq`, `granted_by`, `granted_at`, `ended_at`.
2. **Autoridade.** O `Conversations` continua dono do responsável e do modo; o `ContactCenter` decide acesso, participação e política. Adicionar e remover participantes e a entrada e a saída geram histórico próprio e auditoria com o autor original.
3. **Política de resposta externa**, por fila, com herança: **exclusiva** (só o `Owner`) ou **colaborativa** (o `Owner` e os participantes marcados). Na 5.1b vale só a **exclusiva**; a 5.7 acrescenta a colaborativa.
4. **Transferência de responsabilidade** reutiliza a transferência existente e a troca de responsável, com auditoria.
5. **Agentes de IA: só extensibilidade na etapa 5.** `ParticipantRef(Kind: HumanUser | AiAgent, Id)` entra nos contratos, e papéis e política **aceitam** `AiAgent` como tipo, **sem runtime**. Toda resposta externa, inclusive de IA, passa pelo **mesmo caminho autorizado** (`RegisterHumanReply` ou `RegisterAiReply` e o Sender); **resposta de IA nunca vai direto ao canal**. A participação **não** concede acesso ao histórico nem a ferramentas: cada operação verifica identidade, tenant, atendimento, contexto, permissão e política de autonomia. Identidade de agente, os quatro níveis de autonomia, ferramentas com contrato e colaboração entre agentes são **futuros** (módulo `Agents`), sem fixar fornecedor (BYOK).

### B. Limites de leitura do histórico (C5)

1. **Concessão presa à identidade do item.** Cada concessão fica presa ao `HandlingPeriod` e à `PlacementEpoch` do item, via `ScopeProof`.
2. **Limite inferior.** Padrão: `LastMessageSeq + 1` **no instante da concessão** (entrar não dá acesso ao passado). Uma ação **explícita e auditada** de "compartilhar histórico" pode baixar o limite, **nunca abaixo do limite de quem concede** (teto do concedente). O proprietário parte do começo da conversa, como hoje. Para notas internas, `internal_from_seq` segue a mesma regra; **uma menção expõe só a nota mencionada**, e só a quem já é participante na hora da escrita.
3. **Limite superior efetivo** = o menor entre a sequência vista na mesma resposta do `Conversations`, o limite de saída com origem comprovada (`HistoryLimitSeq` válido) e a fronteira certificada do item encerrado (garantias da etapa 4, **inalteradas**). A **remoção encerra a leitura** do ex-participante, sem acesso residual.
4. **Dois fluxos de paginação.** Mensagens externas e notas são lidas **separadamente**, cada uma com seus limites **aplicados na consulta, antes da paginação** (como `ThroughMessageSeq` hoje). A linha do tempo combinada é uma visão do host que mescla por `(OccurredAt, tipo, seq)`, com cursor da última entrada emitida, e exige as duas permissões. **As notas nunca aparecem nos endpoints de mensagens externas.**
5. **Revalidação.** O cursor carrega o `authz_version` e a versão da concessão. Depois da leitura, o que decide o acesso é comparado de novo, **estendendo o `ReadAuthority`** com esses dois campos (como a etapa 4 já faz com fila, responsável, período e fronteira). Se mudou, a página é descartada e o cursor expira (**409**), sem continuar em silêncio.
6. **Mudança de colocação** (nova `PlacementEpoch` por transferência de unidade ou setor): as concessões de colaborador ficam **suspensas** até uma nova concessão; a leitura do proprietário segue o `ScopeProof` como hoje (padrão proposto; a decisão final é da 5.7, entre as que podem permanecer pendentes).

### C. Vazamentos laterais (limites unificados)

- **Citação.** A nota ou resposta guarda só a **referência** (identificador e sequência), **sem cópia do trecho**. O trecho aparece apenas se a sequência está dentro dos limites do leitor; fora, mostra "mensagem fora do seu acesso".
- **Anexo.** O download usa os mesmos limites da mensagem de origem, com URL curta **revalidada a cada requisição**. Vale também para miniaturas e prévias.
- **Notificação.** Carrega só o identificador e o número do atendimento, **sem trecho de conteúdo**. A abertura revalida o acesso, e a notificação de quem foi removido é cancelada. Na etapa 5 só existe notificação **interna** (`contact_center.notifications`); o canal em tempo real é decisão da 5.8.
- **Prévia da lista de atendimentos** ("última interação"). Calculada **dentro dos limites do leitor**: um participante com limite inferior não vê, na lista, mensagem que não pode ler.
- Notas, anexos internos e citações ficam **fora** de qualquer endpoint de mensagens externas e do contexto de IA.

### D. Comunicação interna

1. **Entidade e tabela próprias** (`contact_center.item_internal_messages`), com identidade, autor, citação (resposta), menções, anexos internos e visibilidade. **Não tem** `ChannelInstanceId`, nem `DeliveryState`, nem referência a mensagem externa. Fica presa ao item e ao `HandlingPeriod`; a retenção é decisão pendente (DP-06).
2. **Garantias em camadas** (nenhuma depende só de uma):
   - **Domínio:** o agregado `InternalMessage` não possui caminho para produzir `OutboundMessageRequestedV1`; não existe método que gere evento de saída.
   - **Persistência:** tabela separada, **em outro módulo** (`ContactCenter`). O Sender só lê `channels.outbound_message`, que só se alimenta de `OutboundMessageRequestedV1`.
   - **API:** dois endpoints distintos ("responder externamente" e "nota interna"), com **permissões distintas**. Um corpo com indicador de modo não basta.
   - **Arquitetura:** teste impede que o `ContactCenter` referencie o Outbox de canais para notas.
   - **IA:** o contexto de IA é montado a partir do `Conversations` e **nunca** de notas internas. Notas só entram em contexto de agente por contrato explícito e opt-in futuro.
   - **Rótulo de origem:** conteúdo interno, conteúdo do cliente e conteúdo vindo de agente externo são tipos distintos na API e na interface.
3. **Anexos de nota** ficam em `contact_center.internal_attachments`, no módulo das notas, com **prefixo próprio no armazenamento**. O `Conversations` não tem como resolver esse identificador, e o Sender só lê anexos externos. A impossibilidade é **estrutural**, não uma regra de código sobre uma tabela compartilhada. **Nota interna nunca é exportável** como anexo de resposta (ADR 0014, autorização contextual de anexos).

### E. Autorização atômica e concorrência

A serialização entre mudança de permissão e intenção de resposta (`authz_version`, travas `FOR SHARE` e `FOR UPDATE`, cancelamento atômico das intenções `Pending`) e a reavaliação ao despachar estão no **ADR 0014**. Aqui vale a consequência de produto: a remoção de um colaborador impede a resposta dele **antes** do instante de autorização e não cancela a que já foi despachada; a mudança de responsável cancela as intenções `Pending` do autor afetado.

### F. Migração proposta (nenhuma criada nem aplicada)

| # | Migração | Risco |
|---|---|---|
| 10 | `item_participants`, `item_internal_messages`, `internal_attachments`, `notifications` (5.7); `authz_version` no item e `reply_intents` entram pelo ADR 0014 (5.1b) | médio |

Aditiva, com `Down`; aplicação só com autorização expressa (DP-07).

## Alternativas descartadas

- **Nota como mensagem com um indicador "interna".** Um bug de filtro a enviaria ao cliente. A separação estrutural (outro módulo, outra tabela, sem evento de saída) elimina a classe de erro.
- **Participante herdar o histórico anterior.** Contraria a proteção do histórico e a LGPD.
- **Ler as notas e as mensagens externas num só fluxo paginado.** Os limites seriam aplicados depois da paginação e vazariam por omissão ou duplicata.
- **Cópia do trecho citado na nota.** Vaza conteúdo fora do limite de leitura do leitor.
- **Colaborador com acesso residual depois de removido.** A remoção encerra a leitura.

## Consequências

- O `ContactCenter` concentra mais regras de acesso. Cada caminho lateral (citação, anexo, notificação, prévia) tem teste próprio.
- O cursor passa a carregar versão de autorização, e a leitura pode retornar **409** quando as regras mudam durante a paginação.
- A colaboração exige a 5.1b (resposta humana) pronta; a 5.7 só acrescenta os participantes e a política colaborativa.
- A interface (5.8) precisa distinguir visualmente conteúdo interno, do cliente e de agente.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Nota interna chega ao Sender, ao Outbox ou à IA (**bloqueador 1**) | separação estrutural em camadas; teste de arquitetura; teste de impossibilidade em todas as camadas |
| Participante novo lê o passado (**bloqueador 2**) | `history_from_seq` no instante da concessão; teto do concedente; ação explícita e auditada |
| Leitura por citação, anexo, notificação ou prévia | limites unificados; revalidação por requisição; teste por caminho |
| Remoção no meio da leitura | cursor com `authz_version`; 409 |
| Concessão atravessar mudança de colocação | concessões suspensas até nova concessão |
| Participante `AiAgent` ganhar acesso por ser participante | participação não concede acesso; cada operação verifica política de autonomia |

## Critérios de aceitação

Aceitos pela auditoria independente (C4 e C5) e **mantidos**, mais os limites laterais, cada um com teste:

1. **Proteção do histórico:** participante novo não lê o passado; a concessão **não passa o teto do concedente**; a ação de compartilhar é auditada; a remoção encerra a leitura.
2. **Paginação:** mesclagem dos dois fluxos sem duplicata nem omissão; limites aplicados **antes** da paginação; remoção no meio da leitura invalida o cursor (409).
3. **Nota interna:** nunca aparece nos endpoints externos, nem no contexto de IA, nem alcança o Sender ou o Outbox de canais (**bloqueador**); o `Conversations` não resolve o identificador de anexo de nota (teste estrutural); anexo de nota não é exportável.
4. **Laterais:** citação fora do limite mostra "fora do seu acesso" sem o trecho; download de anexo revalida a cada requisição; notificação sem trecho e cancelada na remoção; prévia da lista respeita os limites do leitor.
5. **Políticas:** exclusiva (só o responsável) e colaborativa (responsável e marcados); concorrência entre colaboradores; transferência de responsável auditada.
6. **Extensibilidade:** o tipo `AiAgent` é aceito nos contratos **sem runtime**; resposta de IA só pelo fluxo autorizado.
7. **Geral:** suíte completa e mutações da etapa 4 verdes; `ScopeProof`, `PlacementEpoch` e `HandlingPeriod` não enfraquecidos.

## Pendências

- **Podem permanecer para a fatia:** suspensão das concessões na mudança de colocação (5.7), tempo real da interface (5.8).
- **DP-06** (retenção de notas, anexos e do Inbox) e **DP-07** (migrações) dependem do decisor.
- Interação entre atendimentos de tenants distintos, federação e agentes de IA com runtime: **futuros**, fora da etapa 5 (ADR 0011, seção H); nada aqui assume identificador ou sessão globais.
