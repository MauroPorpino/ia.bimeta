# ADR 0009 — Autorização delegada às filas e elevação de contexto no `ElevatedConversationGateway`

- **Status:** Proposto (29/10/2026). Aguarda a aprovação do decisor e a nova auditoria independente; nenhum outro documento o trata como aceito.
- **Data:** 29/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` (seções 2, 3 e 11), ADRs 0005, 0007 e 0008, `CLAUDE.md` (regras de tenant e de autorização), terceira auditoria independente da etapa 4
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**) e os ADRs 0001 a 0008.
- **Registra:** uma **mudança no modelo de autorização** (`BOUNDARIES.md` §11): o ContactCenter passa a decidir o acesso do atendente por **participação e papel na fila**, e o Conversations executa a mudança de estado com o **contexto de sistema do mesmo tenant**, criado pelo host.

## Contexto

O atendente comum de uma fila não tem as permissões de conversa por organização (`ConversationRead`, `ConversationManage`): o acesso dele às conversas vem de **participar da fila** (`QueueMembership`, papel de atendente ou supervisor) e das regras do ContactCenter. O `Conversations` exige uma dessas permissões sobre a organização da sessão, ou um contexto de sistema (`RequestContext` sem `Principal`, `IsSystem`), para ler a sessão, ler o histórico, transitar o modo de atendimento e encaminhar a sessão.

Sem uma ponte, o atendente não consegue assumir, devolver nem transferir uma conversa. Conceder `ConversationRead`/`ConversationManage` por organização resolveria a mecânica, mas abriria **todas** as conversas da organização a quem só deveria ver as da fila dele.

O `RequestContext` só pode ser criado pelos hosts, a partir de fonte autenticada (TEN-1). A matriz permite `ContactCenter → Conversations.Contracts` e proíbe `Conversations → ContactCenter` (CIC-1).

## Decisão

1. **Quem decide o acesso é o ContactCenter, antes de chamar.** Cada caso de uso resolve, sobre o recurso carregado dentro do tenant (TEN-2): a organização visível ao usuário, a participação e o papel dele na fila (`QueueAccess`: atendente, supervisor, quem pode atuar sobre o item) e a regra de estado do item. O que o usuário não enxerga responde como **inexistente** (404); o que enxerga e não pode fazer é **negado e auditado** (403), com a negação guardada de forma durável (ADR 0010). O `Conversations` continua impondo a máquina de estados, a `StateVersion`, a autorização de unidade do número (ADR 0008) e a cerca do executor (ADR 0010). Como o comando é executado com o contexto de sistema (item 2), o `Conversations` não decide **quem** pode: por isso, nas operações que mudam o atendimento (inclusive a entrada na fila, cuja base é a conversa com a IA), a decisão do ContactCenter é tomada sobre o responsável **efetivo** (o do `Conversations`, não só o que a projeção do item mostra) e o estado sobre o qual ele autorizou vai junto do comando, como pré-condição que o `Conversations` confere de forma atômica, dentro da trava da sessão (ADR 0010, decisões D e E). O `Conversations` só confere a condição; não conhece filas nem papéis.
2. **Quem eleva é o host, não o módulo.** `ElevatedConversationGateway` (`IaBimeta.Api`, implementa `IConversationGateway`, a porta do ContactCenter) abre um **escopo de DI próprio e curto**, copia o tenant do contexto confiável da operação em curso (`IRequestContextAccessor.GetRequired()`, nunca de um parâmetro) e estabelece nele `new RequestContext(tenant, "contact-center")`, **sem `Principal`**, isto é, contexto de sistema do **mesmo tenant**. O contexto da requisição **não é alterado**: nada fica elevado depois da chamada nem em chamadas concorrentes.
3. **Superfície mínima e fechada.** A porta tem **seis** operações: ler a sessão, ler o histórico, transitar o modo, encaminhar a sessão, registrar a cerca do executor e, **somente leitura**, consultar se um efeito (transição ou encaminhamento) já foi aplicado (`GetEffectAsync`, pelo identificador que a intenção guardou; sem escrever, sem travar e sem cerca; emenda de 30/10/2026, ADR 0010, decisão I, item 51). A sexta serve a quem não tem (ou perdeu) a autoridade de escrever e só precisa reconhecer o que já aconteceu. Não há executor genérico de código privilegiado, e o ContactCenter não alcança outras operações do `Conversations` (por exemplo, `RequestResend`, que exige usuário autenticado, não passa por ela). Operação nova na porta exige aprovação (e ADR, se mudar o modelo).
4. **O que a elevação não muda.** O tenant é sempre o do contexto de origem; os filtros globais de tenant do `Conversations` continuam ativos; nenhum outro módulo é acessado; o modelo de IA e o `EmployeeAssistant` não chamam a porta.
5. **Autoria.** No `Conversations`, as mudanças pedidas pela porta têm **origem `contact-center` e nenhum usuário** (por exemplo, `SessionTransfer.RequestedBy` é nulo). O **autor humano fica no ContactCenter**: no histórico do item (`ActorUserId`) e na auditoria do ContactCenter, que carrega o ator, a origem e o instante originais (ADR 0010). A trilha do `Conversations` identifica o componente; quem quer saber **qual atendente** consulta o ContactCenter.
6. **Falha fechada.** Sem contexto confiável na operação em curso, a porta lança (não há tenant a elevar). Sem a implementação do host registrada, vale a porta padrão (`DirectConversationGateway`), que usa o contexto **de quem chamou**: o `Conversations` recusa o atendente comum.
7. **Worker.** O processo já roda em contexto de sistema do tenant do evento (`IMessagingScopeInitializer`, do host), então usa a porta padrão, sem elevação adicional.
8. **Leitura de conteúdo.** A leitura da sessão e do histórico pela porta serve ao caso de uso; **o acesso do usuário ao conteúdo é decidido no ContactCenter** (item visível e permissão de ler a conversa) e **limitado ao período do atendimento**, conforme a fronteira histórica do ADR 0010, que também vale depois de a conversa sair do escopo da fila (decisão G): o encerramento não a amplia. Nada disso muda a superfície da porta (as mesmas cinco operações): o consumidor dos eventos só lê a sessão por ela.

## Alternativas descartadas

- **Conceder `ConversationRead`/`ConversationManage` ao atendente por organização.** Amplia o acesso a todas as conversas da organização, contra o modelo de fila e de equipe compartilhada.
- **O `Conversations` consultar a participação na fila.** Exigiria `Conversations → ContactCenter`, que cria ciclo (CIC-1).
- **Passar o usuário ou o papel como parâmetro do comando do `Conversations`.** Confiaria em dado de entrada, contra o princípio de que a autorização vem do código e do contexto confiável.
- **O ContactCenter criar o contexto de sistema.** O `RequestContext` só nasce nos hosts (TEN-1).

## Consequências

- **O ContactCenter passa a ser o ponto de decisão do acesso a conversas de fila.** Um defeito nele pode expor conversas da organização. Por isso o acesso é testado nas duas direções (item de outra fila ou de outro tenant responde 404; sem papel, 403 auditado; conteúdo limitado ao período) e a superfície da porta é pequena e fechada.
- **Concentração de privilégio no gateway.** O contexto elevado vale para uma chamada, num escopo próprio. Toda operação nova na porta é uma ampliação de privilégio e passa por revisão.
- **Rastro dividido.** O `Conversations` registra o componente; o ContactCenter registra o atendente. A reconstrução do "quem" cruza os dois pelo identificador da operação e pelo item.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Elevação permanecer ativa depois da chamada ou vazar entre chamadas | Escopo de DI próprio por chamada; o contexto da requisição não é alterado; testes `ElevatedConversationGatewayTests` (a requisição não fica elevada; chamadas paralelas de tenants diferentes não se misturam) |
| Tenant vindo de parâmetro | O tenant é copiado do contexto confiável em curso; sem contexto, falha fechada |
| Operação privilegiada nova entrar na porta sem revisão | Superfície fechada de seis operações (a sexta só lê); mudança exige aprovação (e ADR, se alterar o modelo) |
| Atendente ler conteúdo fora do que lhe cabe | Decisão no ContactCenter (item visível, papel) e limite do período (ADR 0010) |
| Operação autorizada pelo ContactCenter sobre um estado que o `Conversations` já mudou (a porta executa com contexto de sistema e não decide quem pode) | Autorização sobre o responsável efetivo e pré-condição atômica no comando, inclusive na entrada na fila (ADR 0010, decisões D e E) |
| Autor humano não aparecer na trilha do `Conversations` | O autor fica no histórico do item e na auditoria do ContactCenter, com ator, origem e instante originais |

## Critérios de aceitação

Cobertos por testes de integração com PostgreSQL real (`tests/IaBimeta.IntegrationTests/ContactCenter`): `ElevatedConversationGatewayTests` (o atendente comum não lê nem muda a conversa direto no Conversations, mas a porta executa por ele; a requisição não fica elevada; sem contexto a porta falha fechada; chamadas paralelas de tenants diferentes não se misturam; a superfície da porta é fechada, com cinco operações), `QueueItemAccessTests` e `ServiceQueueAuthorizationTests` (404 e 403 auditado por papel e por fila), `ConversationHistoryPeriodTests` e `HistoryBoundaryTests` (conteúdo limitado ao período), `QueueHandoffTests` (o atendente comum conclui a operação pela porta elevada) e `AdministrativeAuditTests` (negações auditadas e recuperáveis).

## Pendências

- Este ADR descreve o que está implementado; a aprovação dele e a do ADR 0010 dependem do decisor e da nova auditoria independente.
