# Fronteiras e camadas do ia.bimeta (BOUNDARIES.md)

- **Status:** Aprovado (v0.4)
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Formalizado por:** ADR 0005 (v0.2), ADR 0006 (v0.3, seção 12) e ADR 0007 (v0.4, seções 6 e 12)
- **Complementa:** `ARCHITECTURE.md` e os ADRs 0001 a 0004. Não revoga nenhuma decisão deles, exceto os itens 1 e 4 do ADR 0003, substituídos pelo ADR 0005.
- **Fonte única da matriz de dependências:** `docs/architecture/module-dependencies.json`.

**Vocabulário:** *módulo* é um contexto delimitado com dados próprios. *Contracts* é a API pública do módulo. *Core* é o Domain mais o Application do módulo. *Central de Atendimento* (`ARCHITECTURE.md`) é o módulo **ContactCenter**.

**Princípios:**

1. Monólito Modular em .NET 10, com um deploy (Api e Worker).
2. Clean Architecture e Ports & Adapters dentro de cada módulo.
3. Módulos se falam só por Contracts.
4. Cada módulo é dono dos seus dados.
5. Tenant e autorização vêm do código, nunca de argumento.
6. Fronteira sem teste automatizado não existe.

## 1. Estrutura inicial da solução e dos módulos

| Módulo | Componente (ARCH §4) | Dono de (entidades §10) | Etapa (§11) |
|---|---|---|---|
| Tenancy | hierarquia §3 | Tenant, Organization, BusinessUnit, Department | 2 |
| Identity | autorização §3 e §9 | User, UserOrganizationGrant, papéis | 2 |
| Audit | auditoria §2.7 | AuditEvent | 2 (quando o primeiro caso de uso precisar) |
| Conversations | Conversation Engine e Context Manager | Contact, ServiceSession, Conversation, Message, Attachment, **estado e modo de atendimento** | 3 |
| ContactCenter | Central de Atendimento e Routing/Escalation | ServiceQueue, QueueMembership, RoutingPolicy, SchedulePolicy, atribuição e histórico de handoff | 4 |
| Channels | Channel Gateway/Registry | Channel, ChannelAccount, eventos brutos de webhook | 5 |
| AiGateway | AI Provider Gateway | AiProviderCredentialReference, AiRequest | 6 |
| Knowledge | Knowledge Engine | KnowledgeSource, KnowledgeItem, KnowledgeRevision, KnowledgeApproval | 6 |
| Agents | AI Agent Runtime | AgentDefinition | 6 e 7 |
| Capabilities | Capability Execution Engine | IntegrationDefinition, Capability, CapabilityExecution | 7 |
| Workflows | Workflow Engine | WorkflowDefinition, WorkflowVersion, WorkflowInstance, WorkflowState | 7 |
| EmployeeAssistant | Employee AI Workspace | EmployeeAssistantSession | 9 |
| Voice | Voice Gateway | VoiceCall (**reservado**, sem projeto) | 11 |

**Projetos por módulo.** São quatro, e cada um só nasce quando necessário:

| Projeto | Nasce quando |
|---|---|
| `IaBimeta.<M>.Core` (pastas `Domain/` e `Application/`) | o módulo recebe o primeiro código |
| `IaBimeta.<M>.Contracts` | existe o **primeiro consumidor** (outro módulo ou adaptador) |
| `IaBimeta.<M>.Infrastructure` | o módulo persiste dados ou integra algo externo |
| `IaBimeta.<M>.Presentation` | surge o primeiro endpoint |

**Layout:**

```text
backend/
├── IaBimeta.slnx
├── host/            IaBimeta.Api · IaBimeta.Worker (etapa 4)
├── building-blocks/ IaBimeta.SharedKernel · IaBimeta.Messaging (etapa 4)
├── modules/<modulo>/IaBimeta.<M>.{Contracts,Core,Infrastructure,Presentation}
└── adapters/        IaBimeta.Adapters.{WhatsApp,Ai.<Provedor>,LabNet,BImeta}   # um projeto por adaptador, no primeiro uso
tests/
├── IaBimeta.ArchitectureTests/
├── IaBimeta.IntegrationTests/       # nasce quando houver PostgreSQL integrado
└── <modulo>/IaBimeta.<M>.Tests/
```

**Etapa 2a (primeira fatia), com 9 projetos de código e testes:**

| Projeto | Observação |
|---|---|
| `SharedKernel` | só `TenantId` e o que a 2a usar |
| `Tenancy.Core` e `Tenancy.Contracts` | o Contracts nasce porque o Identity o consome |
| `Identity.Core` | o `Identity.Contracts` ainda não nasce, porque ninguém o consome. Os contratos ficam no namespace `IaBimeta.Identity.Contracts` e passam para um projeto na etapa 3, sem renomear (§10) |
| `Api` | já existe |
| `SharedKernel.Tests`, `Tenancy.Tests`, `Identity.Tests` | |
| `ArchitectureTests` | começa só com testes do grafo de projetos, sem biblioteca |

`Audit` e os projetos `Infrastructure` entraram na 2b, e o `Conversations` (`Contracts`, `Core`, `Infrastructure` e `Presentation`) na etapa 3. Os projetos globais antigos (`Domain`, `Application`, `Infrastructure`) e o `Domain.Tests` já não fazem parte da solução: saíram nos commits `72e94e2` e `5b6c8ac`.

O `IaBimeta.Worker` e o bloco `IaBimeta.Messaging` nascem na etapa 4, com o primeiro processamento assíncrono durável. O `Messaging` é implementado progressivamente, sem antecipar funcionalidade sem necessidade comprovada (§12.12).

## 2. Responsabilidade de cada camada

| Camada | Contém | Pode usar | Proibido |
|---|---|---|---|
| **Domain** (no Core) | entidades, agregados, objetos de valor, invariantes, eventos de domínio | `SharedKernel` | EF, ASP.NET, HTTP, DI, I/O, `DateTime.Now` |
| **Application** (no Core) | casos de uso, portas, checagem de autorização, publicação de eventos | Domain, `SharedKernel`, Contracts permitidos, `Microsoft.Extensions.*` (abstrações) | EF, Npgsql, ASP.NET, SDKs, `HttpClient` concreto |
| **Infrastructure** | `DbContext` do módulo, repositórios, migrations, implementação das portas | Core, Contracts, pacotes de infraestrutura | regra de negócio; ser referenciada por qualquer um além dos hosts |
| **Presentation** | endpoints, DTOs HTTP, validação de formato, `ProblemDetails` | Core (Application) e Contracts do próprio módulo | regra de negócio, `DbContext`, `tenantId` vindo da entrada |
| **Contracts** | DTOs imutáveis, interfaces de consulta e comando, eventos, portas para adaptadores | `SharedKernel` e Contracts de módulos inferiores | lógica, entidades |
| **Host** | composição, configuração, middleware, criação do `RequestContext` | tudo, para montar | regra de negócio |

Dentro do módulo: `Presentation → Application → Domain` e `Infrastructure → Application → Domain`.

Contracts tem os namespaces `Queries`, `Commands`, `Events` e `Ports`. Os `Ports` são só para adaptadores e para o dono.

## 3. Direção permitida das dependências

**A fonte única é `docs/architecture/module-dependencies.json`, lido pelo teste ARQ-06. A tabela abaixo é cópia ilustrativa: em caso de divergência, o JSON prevalece.**

| Módulo | Pode usar Contracts de |
|---|---|
| Audit | — |
| Tenancy | Audit |
| Identity | Tenancy, Audit |
| Conversations, AiGateway, Knowledge, Capabilities | Identity, Tenancy, Audit |
| ContactCenter, Channels | Conversations, Identity, Tenancy, Audit |
| Workflows | Conversations, Capabilities, Identity, Tenancy, Audit |
| Agents | AiGateway, Knowledge, Capabilities, Workflows, Conversations, ContactCenter (só `RequestHandoff`), Identity, Tenancy, Audit |
| EmployeeAssistant | Agents, AiGateway, Knowledge, Capabilities, Conversations (**só `GetAuthorizedExcerpt`**), Identity, Tenancy, Audit |

- **DEP-1:** aresta nova na matriz exige ADR e edição do JSON na mesma alteração.
- **DEP-2:** adaptadores referenciam só o `.Contracts` do módulo dono da porta e o `SharedKernel`.
- **DEP-3:** só os hosts referenciam `Infrastructure`, `Presentation` e adaptadores.
- **DEP-4:** o `SharedKernel` não referencia módulo algum e só aceita tipos sem regra de negócio, e apenas o que algum módulo já usa.
- **DEP-5:** o Core e os Contracts não conhecem `LabNet`, `BImeta` nem a API da Meta.
- **DEP-6:** blocos de construção (`SharedKernel`, `Messaging`) não referenciam módulos. As abstrações de mensageria ficam no `SharedKernel`. A implementação (`Messaging`) só é referenciada pelas `Infrastructure` dos módulos e pelos hosts, nunca pelo Core (ARQ-20).
- **DEP-7 (evolução futura, fora do MVP):** se a exceção de resposta defasada for adotada (`CONVERSATION-LIFECYCLE.md` §6.3), o `Conversations` poderia usar `Audit.Contracts.Queries` para verificar, de forma independente do `Agents`, se um turno praticou atividade protegida. Seria uma consulta nova dentro da aresta `Conversations → Audit` que já existe na matriz. **No MVP o `Conversations` não consulta o `Audit` para aceitar respostas.**

## 4. Comunicação entre módulos

- **COM-1 (padrão):** chamada **síncrona** por `Contracts.Queries` e `Contracts.Commands`, com o `RequestContext` e resposta em DTO ou `Result`, nunca entidade nem `IQueryable`. Nada obriga comunicação assíncrona.
- **COM-2 (evento interno):** evento em processo, despachado depois do commit, **sem durabilidade**. Serve para avisar módulos superiores e para efeitos tolerantes a perda ou reconstruíveis (projeções, notificações). Tem nome no passado e versão.
- **COM-3 (evento durável, com outbox):** só quando a perda causaria inconsistência ou perda de ação do cliente. Critérios: (a) mensagem de cliente que deve gerar resposta; (b) mudança de estado que outro módulo precisa aplicar para manter invariante; (c) efeito em sistema externo. O envelope tem `EventId`, tipo e versão, `TenantId`, `CorrelationId`, `CausationId`, `OrderingKey` e `OccurredAt`. A entrega é pelo menos uma vez, com handler idempotente por `EventId`. O outbox próprio só é construído quando o primeiro evento durável entrar em implementação (etapa 4, §12.12), sem biblioteca de mediador ou barramento.
- **COM-4:** módulo inferior **não chama** superior; avisa por evento. O contrato do evento fica no módulo emissor.
- **COM-5:** não há transação entre módulos. A consistência é eventual, com compensação.
- **COM-6:** `CorrelationId` atravessa as chamadas, e `AiRequestId` correlaciona as respostas da IA.

**Fluxos de referência:**

- **Mensagem recebida** (detalhes na seção 12):
  1. O Receiver (`IaBimeta.Api`) verifica a assinatura, identifica canal e tenant pela projeção local do `Channels`, deduplica e grava no Inbox durável; só então responde 200.
  2. O worker de ingestão do `Channels` chama `Conversations.Commands.IngestInboundMessage`, preservando o timestamp do provedor e a ordem de ingestão.
  3. `Conversations` publica `InboundMessageRegisteredV1`, evento **durável** (COM-3a).
  4. `Agents` abre o turno (`BeginTurn`) e monta a resposta com `AiGateway`, `Knowledge` e `Capabilities`.
  5. `Agents` chama `Conversations.Commands.RegisterAiReply` com `StateVersion`, `basedOnInboundSeq`, `TurnId` e a época do turno (§6).
- **Mensagem enviada:** `Conversations` publica `OutboundMessageRequestedV1` (durável), `Channels` grava na fila de envio, o Sender envia respeitando os limites do provedor e o `Channels` chama `RecordDeliveryStatus` (seção 12).
- **Handoff e retorno à IA:** coordenados como na seção 6.

## 5. Isolamento multi-tenant e autorização

**TEN-1: o `RequestContext` só nasce de fonte autenticada e confiável.**

| Origem | Fonte confiável | Validação |
|---|---|---|
| Usuário (web/API) | token ou sessão emitido e validado pelo Identity (assinatura, validade, revogação) | tenant e ator vêm do token; concessões carregadas do Identity |
| Webhook de canal | assinatura verificada, depois `ChannelAccount` → tenant e unidade | sem assinatura válida, rejeitado antes de qualquer processamento |
| Evento interno | envelope publicado por código do próprio sistema | o ator original viaja no envelope; permissões revalidadas quando o efeito depender dele |
| Job agendado | `TenantId` gravado por código confiável ao agendar | sem tenant, falha fechada |

O `RequestContext` nunca vem de corpo, query string ou saída de modelo de IA. Ele é imutável e só os hosts o criam (construtor interno com `InternalsVisibleTo` para `Api` e `Worker`).

- **TEN-2 (permissão efetiva até o recurso):** em todo caso de uso:
  1. o recurso é buscado por `(tenant_id, id)`, e um recurso de outro tenant responde "não encontrado" para não vazar existência;
  2. o `ResourceScope` (organização, unidade, setor, fila) é derivado **do recurso carregado**, não de parâmetro da requisição;
  3. `IAccessPolicy` avalia a permissão para tenant, organização, unidade e recurso;
  4. nega por padrão;
  5. listas já saem filtradas pelo escopo efetivo.
- **TEN-3 (dados):** toda tabela do tenant tem `tenant_id NOT NULL`, índices e chaves compostas começando por ele, e filtro global no `DbContext`. RLS com `FORCE` continua **proposta a validar**, como defesa adicional (ARCH §3). Arquivos usam prefixo por tenant.
- **TEN-4 (contas técnicas):** o conector recebe um `ActorContext` com os direitos **efetivos do usuário**. A credencial técnica só autentica o canal e nunca amplia privilégios.
- **TEN-5 (contexto conversacional):** o Context Manager monta contexto de **uma** `ServiceSessionId`, e nenhuma API aceita várias.
- **TEN-6 (segredos BYOK):** referenciados por `AiProviderCredentialReference`, resolvidos na hora de usar e nunca em log, frontend ou texto puro.
- **TEN-7:** o decorador que avalia permissão antes do caso de uso só nasce quando houver pelo menos 3 casos de uso. Até lá, cada um chama `IAccessPolicy` explicitamente.

## 6. Conversations, ContactCenter e EmployeeAssistant

| | **Conversations** | **ContactCenter** | **EmployeeAssistant** |
|---|---|---|---|
| Autoridade sobre | o **estado e o contexto** da conversa, inclusive modo e responsável atual | filas, equipes, atribuição, prioridade, horários, **políticas** | sessões privadas do colaborador |
| Dados | Contact, ServiceSession, Conversation, Message, Attachment, `HandlingState` | ServiceQueue, QueueMembership, RoutingPolicy, SchedulePolicy, atribuição e histórico de handoff | `EmployeeAssistantSession` |
| Depende de | Identity, Tenancy, Audit | Conversations, Identity, Tenancy, Audit | Agents, AiGateway, Knowledge, Capabilities, Identity, Tenancy, Audit e Conversations (só `GetAuthorizedExcerpt`) |
| Proibido | decidir fila, equipe ou política de roteamento | ler tabelas de conversas; alterar o estado fora de `TransitionHandlingMode` | referenciar `Channels` ou `Conversations.Contracts.Commands`; gravar em sessão de cliente |

**Coordenação do modo IA/humano:**

- **Estado:** `HandlingState` = modo (`AiAutonomous`, `AiSupervised`, `HumanAssisted`), `ResponderKind`, `ResponderRef` e `StateVersion`. Vive no Conversations, junto das mensagens, porque a regra "só um responde por vez" tem de valer onde a mensagem é gravada.
- **Quem decide:** o ContactCenter decide *se* e *para quem* transferir (política, fila, operador) e então chama `Conversations.Commands.TransitionHandlingMode(serviceSessionId, para, responderRef, expectedStateVersion, transitionId, motivo)`. Agentes, operadores e supervisores pedem ao ContactCenter (`RequestHandoff`, `ReturnToAi`), nunca ao Conversations.
- **Quem valida:** o Conversations impõe a máquina de estados, incrementa a `StateVersion` (token de fencing) e publica `HandlingModeChangedV1` (durável, atômico com a mudança).
- **Sem respostas concorrentes:** toda resposta, de IA ou de operador, carrega a `StateVersion` sob a qual foi produzida. O Conversations rejeita versão ou responsável divergente (`StaleState`), e quem enviou descarta. A transição invalida o turno de IA em andamento. A resposta da **IA** carrega ainda `TurnId`, a época do turno ativo e `basedOnInboundSeq` (cerca de entrada), e a resposta defasada é **sempre rejeitada** no MVP, por regra fixa e sem chave de configuração (a exceção `allowStaleInput` é evolução futura e depende de novo ADR, `CONVERSATION-LIFECYCLE.md` §6).
- **Falha no meio:** o ContactCenter guarda a atribuição como `PendingHandoff` e repete o comando (idempotente por `transitionId`). A conversa permanece no modo anterior até a transição ter sucesso. Um job compara a projeção do ContactCenter com os eventos.
- **Aguardando atendente:** `HumanAssisted` pode ter `ResponderRef` vazio, o que significa atendimento **pendente na fila** do ContactCenter. A IA deixa de responder (a `StateVersion` rejeita as respostas dela), o histórico fica preservado no Conversations e as mensagens novas do cliente se acumulam até um atendente assumir. É o estado usado na falha permanente da IA (§12.3).
- **Turno logicamente ativo:** o Conversations guarda qual turno de IA está ativo na sessão (`BeginTurn`, `EndTurn` e `CancelTurn`, com época). Há no máximo um por sessão, e um turno substituído não registra resposta, mesmo que continue executando (`CONVERSATION-LIFECYCLE.md` §4).
- **Cobertura × entrega:** `AnsweredThroughSeq` é a cobertura **lógica** da resposta aceita. Os estados reais de envio e entrega são do `Channels`. Falha definitiva de envio é registrada, sinalizada ao `ContactCenter` e recuperada por ação autorizada do atendente, e **não dispara novo processamento de IA**. O reenvio (`RequestResend`) exige autorização explícita, vínculo auditável com a mensagem original e idempotência, deixa o `Channels` aplicar as regras e a janela, e nunca repete automaticamente um envio em `SendUnknown` (`CONVERSATION-LIFECYCLE.md` §11, COV-7).
- **Mídia pendente:** conteúdo que ainda não está disponível nunca é entrada do modelo. Ao esgotar o prazo, aplica-se uma política explícita por tipo de conteúdo (`CONVERSATION-LIFECYCLE.md` §10).
- **Propriedade das configurações:** o `Agents` controla agrupamento, interrupções, tentativas e o comportamento em rajada longa. O `Conversations` aplica regras fixas de consistência e de aceitação de respostas, sem configuração por tenant no MVP. Nenhum módulo lê a configuração de outro (`CONVERSATION-LIFECYCLE.md` §14).
- **Rajada longa (`OnChattyBurst`):** atingido o limite de mensagens ou de interrupções, o `Agents` pede ao `Conversations` que sinalize a sessão (`RaiseSessionAttention`, aresta existente), e o `ContactCenter` cria o item de atenção e permite a transferência para humano por ação do atendente. O padrão é `Alert`: **não há transferência automática** sem configuração explícita do tenant (`OnChattyBurst = Handoff`). O `Agents` continua chamando o `ContactCenter` somente por `RequestHandoff` (`CONVERSATION-LIFECYCLE.md` INT-6).
- **Leitura pelo Agents:** o modo vem no snapshot de contexto do Conversations. O Agents só chama o ContactCenter para pedir handoff.

**Assistente interno:**

- **SEP-1:** o `EmployeeAssistant` não tem referência a nada que envie mensagem ao cliente (ARQ-10).
- **SEP-2:** não existe acesso automático a histórico de cliente. Um trecho entra na sessão privada **somente** por ação explícita do colaborador na interface (endpoint em `Presentation`) que chama `GetAuthorizedExcerpt(serviceSessionId, intervalo, finalidade)`. A consulta valida a permissão efetiva sobre *aquele* atendimento, limita o tamanho, é auditada (quem, qual atendimento, qual intervalo) e o trecho é copiado com marca de origem.
- **SEP-3:** o Contracts não oferece listagem nem busca de conversas ao `EmployeeAssistant`, e **nenhuma ferramenta de IA** do assistente referencia o Conversations: o modelo não consegue chamar essa consulta.

## 7. Contratos e adaptadores

| Integração | Porta (onde) | Adaptador | Nasce em |
|---|---|---|---|
| **IA** | `IAiProvider` em `AiGateway.Contracts.Ports`: `CompleteAsync(AiCompletionRequest)` | `Adapters.Ai.<Provedor>`, um por provedor | 6 |
| **WhatsApp** | `IChannelAdapter` em `Channels.Contracts.Ports`: verificar e normalizar webhook, enviar | `Adapters.WhatsApp` (Meta Cloud API) | 5 |
| **LabNet e BImeta** | `ICapabilityConnector` em `Capabilities.Contracts.Ports`: catálogo de `CapabilityDescriptor` e `ExecuteAsync(invocação, ActorContext)` | `Adapters.LabNet`, `Adapters.BImeta` | 7 e 8 |
| **PostgreSQL** | repositórios e unidade de trabalho no Core de cada módulo | `.Infrastructure` do módulo | 2b |
| **Busca de conhecimento** | `IKnowledgeSearch` em `Knowledge` (ADR 0001, item 10) | `tsvector` agora; pgvector na homologação Linux | 6 |

Regras:

- **Conectores:** sem SQL de LLM. No MVP, capacidade com efeito colateral é recusada. Resultado vem marcado como não confiável (prompt injection) e é `Unconfirmed` se a origem não confirmar.
- **Sem abstração sem uso:** `ISecretStore`, `IFileStore` e o bloco `Persistence.PostgreSql` **não são definidos agora**. Nascem com o primeiro uso concreto: BYOK, anexos, segredo de assinatura de canal (etapa 5) ou código repetido em dois módulos.
- **Voz:** o módulo fica **reservado por documento**, sem projeto nem código. A interface mínima `IVoiceGateway` só nasce junto com o `Channels.Contracts` (etapa 5), para cumprir o critério 10 do ADR 0002 sem implementação.

## 8. Testes automatizados de arquitetura

**Porta de entrada do ArchUnitNET** (preferencial, **não instalado**). Verificado em 26/10/2026:

| Item | Resultado |
|---|---|
| Pacote | `TngTech.ArchUnitNET` 0.13.5, publicado em 26/10/2026, Apache-2.0, alvo `netstandard2.0` (e `net462`) |
| .NET 10 | os projetos de teste do próprio repositório (xUnit, xUnitV3, NUnit, MSTest, TUnit) alvejam `net10.0` na branch principal |
| xUnit | o adaptador `TngTech.ArchUnitNET.xUnit` depende de `xunit.assert 2.4.1` (xUnit 2, o nosso 2.9.3). Existe `...xUnitV3` para o xUnit 3. Dá para dispensar o adaptador e usar o pacote base com asserção própria |
| Dependências | Mono.Cecil 0.11.6 (publicado em out/2024, **antes do .NET 10**), Newtonsoft.Json 13.0.4, CycleDetection, JetBrains.Annotations |
| Atenção | a 0.13.5 corrige um defeito em que dependências em código `async` sumiam: regras negativas podiam passar sem checar nada. A versão é do mesmo dia; ler o "Upgrading" antes de adotá-la |
| **Não confirmado** | a **execução** contra assemblies .NET 10 do ia.bimeta. Compatibilidade declarada não é compatibilidade comprovada |

**Porta de entrada, em 3 passos**, antes de usar a biblioteca:

1. Aprovação da instalação pelo decisor.
2. Um *spike* no `ArchitectureTests` roda uma regra real contra os assemblies compilados em .NET 10.
3. Uma violação plantada de propósito (fixture) faz a regra falhar.

O resultado vai para o histórico deste documento. Se o Mono.Cecil não ler os metadados do .NET 10, o plano B são testes por reflexão e por XML dos `.csproj`.

**Regras.** As negativas devem afirmar que o conjunto avaliado não é vazio, para nunca passarem em vazio.

| ID | Regra | Ativa desde |
|---|---|---|
| ARQ-04 | Só os hosts referenciam `Infrastructure`, `Presentation` e adaptadores | 2a (sem biblioteca) |
| ARQ-05 | Referência entre módulos só para `.Contracts` | 2a |
| ARQ-06 | Grafo acíclico e igual ao JSON da matriz | 2a |
| ARQ-01 | Domain não referencia Application, Infrastructure, Presentation, ASP.NET nem EF | 2a, depois do spike |
| ARQ-02 | Application não referencia Infrastructure, Presentation, ASP.NET, EF, Npgsql nem SDKs | 2a, depois do spike |
| ARQ-12 | Todo `DbContext` aplica filtro de tenant e as entidades implementam `ITenantOwned` | 2b |
| ARQ-16 | SQL e migrations só no esquema do módulo; sem `FromSql*` fora da Infrastructure | 2b |
| ARQ-17 | Sem chave estrangeira entre esquemas (teste de integração, porta 5433) | 2b |
| ARQ-07 | Contracts só têm interfaces, `record`s imutáveis e enums | 3 |
| ARQ-11 | Todo caso de uso declara permissão ou `[Public(motivo)]` | 3 |
| ARQ-13 | Endpoint não recebe `tenantId` e só os hosts criam o `RequestContext` | 3 |
| ARQ-18 | Só `Conversations` altera o `HandlingState` (via `TransitionHandlingMode`) | 3 |
| ARQ-19 | `Channels.Presentation` não referencia outro módulo: o caminho do webhook só grava no Inbox | 5 |
| ARQ-20 | O Core não referencia a implementação do `Messaging`, só as abstrações do `SharedKernel` | 4 |
| ARQ-21 | Todo `CapabilityDescriptor` com efeito colateral declara `IdempotencySupport` | 7 |
| ARQ-22 | Só `Conversations` escreve `InboundSeq`, `AnsweredThroughSeq` e o turno ativo | 3 |
| ARQ-23 | Todo registro de resposta da IA exige `TurnId`, época e `basedOnInboundSeq` na assinatura do contrato | 3 |
| ARQ-24 | `Agents` não referencia `OutboundMessageFailedV1` nem os eventos de entrega: falha de envio não dispara IA | 5 |
| ARQ-08, 09, 14 | Adaptadores só referenciam o `.Contracts` do dono; `LabNet`, `BImeta` e `graph.facebook` só nos adaptadores; voz só como interface | 5 |
| ARQ-10 | `EmployeeAssistant` não referencia `Channels` nem `Conversations.Contracts.Commands`, e nenhuma ferramenta de IA referencia `Conversations` | 9 |

As regras ARQ-06 e ARQ-10 ganham fixture negativa ("teste do teste").

**Testes de comportamento** da coordenação de modo:

- resposta de IA durante uma transição é rejeitada;
- duas transições concorrentes: só uma vence;
- repetição de `transitionId` é idempotente.

Somam-se o teste Mauro/Pedro e o isolamento entre tenants.

## 9. Ciclos e acesso às tabelas de outros módulos

- **CIC-1:** o grafo é um DAG. O `dotnet build` recusa ciclo de projetos e o ARQ-06 cobre o resto.
- **CIC-2:** avisar "para cima" é por evento. Se dois módulos parecem precisar um do outro, a ordem é: mover o conceito comum para o módulo inferior, trocar a chamada por evento, ou fundir os módulos. Módulo utilitário com regra de negócio é proibido.
- **DAD-1:** **um esquema por módulo desde o início** (`tenancy`, `identity`, `conversations`, `contact_center`, …), com `DbContext` e migrations próprios.
- **DAD-2:** sem chave estrangeira, `JOIN`, view, trigger ou procedure entre esquemas. A referência a outro módulo é id opaco, com integridade eventual.
- **DAD-3:** quem precisa de dado alheio usa `Contracts.Queries` ou mantém uma projeção própria, alimentada por eventos e com reconciliação.
- **DAD-4:** **papéis de banco por módulo ficam para avaliação depois da 2b**, em ADR próprio. Os esquemas já existem desde o começo.

## 10. Quando criar `.csproj` e quando usar pastas

**Crie projeto** quando há dependências que as outras camadas não podem ter (`Infrastructure`, `Presentation`, adaptador), quando outros módulos precisam referenciá-lo sem ver o resto (`Contracts`), quando a fronteira de compilação é requisito de segurança, quando é unidade de execução distinta (`Api`, `Worker`), ou quando o teste exige ambiente distinto.

**Use pasta e namespace** para subdividir uma camada ou separar Domain de Application dentro do Core (a pureza vem dos ARQ-01 e ARQ-02, não do compilador).

- **PRJ-1:** o namespace é `IaBimeta.<Módulo>.<Camada>[.<Subpasta>]`, **independente do `.csproj`**. Mover um tipo para outro projeto depois não renomeia nada.
- **PRJ-2:** o projeto nasce quando surge o primeiro código que o justifica, nunca antes (tabela da seção 1).
- **PRJ-3 (sem abstração sem uso):** interface só nasce quando há duas implementações, ou uma implementação mais uma fronteira obrigatória (contrato entre módulos ou porta de adaptador externo), ou um teste de unidade que exige substituição. Caso contrário, classe concreta.
- **PRJ-4:** código compartilhado sobe para um bloco de construção só no segundo uso real.
- **PRJ-5:** `Domain` ganha projeto próprio se o ARQ-01 for violado de forma recorrente ou se o domínio for compartilhado por dois hosts.
- **PRJ-6:** extrair um módulo para outro processo só com medição que o justifique ou ciclo de release distinto. Hoje não há microsserviços (ARCH §8).

## 11. Governança da fronteira

| Tipo de mudança | Como registrar |
|---|---|
| **Relevante:** módulo novo; aresta nova na matriz; mudança de regra de camada; novo tipo de adaptador; mudança de dono de dados ou de modelo de comunicação; mudança no modelo de tenant ou autorização; exceção a uma regra; papéis de banco ou RLS | **ADR**, e o JSON da matriz quando houver aresta |
| **Menor:** esclarecimento de texto; método novo de Contracts dentro de aresta já permitida; regra de teste mais restritiva; criar projeto previsto na seção 1; atualizar as tabelas | **direto no BOUNDARIES.md**, com a aprovação do decisor, e uma linha no histórico |

## 12. Processamento de mensagens: recebimento, processamento e envio

Decisão do ADR 0006. Três **papéis lógicos** na mesma base de código, sem microsserviços. Cada papel liga ou desliga por configuração e escala por instância.

| Papel | Onde roda | Responsável | Faz | Não faz |
|---|---|---|---|---|
| **Receiver** | `IaBimeta.Api` (`Channels.Presentation`) | Channels | verifica a assinatura, identifica canal e tenant, deduplica e grava no Inbox; responde 200 só depois do commit | chamar outro módulo, IA ou provedor |
| **Processing** | `IaBimeta.Worker` | handlers de cada módulo | ingestão (Channels), turno de IA (Agents), projeções e roteamento (ContactCenter), capacidades (Capabilities) | enviar ao provedor |
| **Sender** | `IaBimeta.Worker` | Channels | consome a fila de envio e respeita os limites do provedor | decidir conteúdo |

### 12.1 Filas e donos

| Fila | Tabela | Chave de ordenação |
|---|---|---|
| Inbox de webhook | `channels.inbox_event` | canal + contato |
| Fila de envio (a "Outbox" de mensagens) | `channels.outbound_message` | canal + destinatário |
| Outbox de eventos duráveis | `<módulo>.outbox_message` | definida pelo evento (`OrderingKey`) |
| Inbox de eventos do consumidor | `<módulo>.inbox_message` | `ServiceSessionId` nos turnos |

Cada tabela pertence ao esquema do módulo dono (DAD-1 e DAD-2). O bloco `IaBimeta.Messaging` guarda só a mecânica compartilhada (aquisição, lease, retentativa), executada dentro da `Infrastructure` de cada módulo.

### 12.2 Receiver

- **REC-1 (ordem das etapas):** ler o corpo bruto → localizar o canal pelo identificador opaco da URL → verificar a assinatura sobre o corpo bruto, com comparação em tempo constante e **antes de interpretar o conteúdo** → identificar o tenant pelo `ChannelAccount` → separar o lote em eventos → gravar no Inbox (`INSERT … ON CONFLICT DO NOTHING`) numa transação → commit → HTTP 200.
- **REC-2 (resolução segura, sem chamada externa):** canal, tenant e unidade vêm de uma **projeção local do `Channels`** (tabela `channels.channel_account`, com cache em memória e invalidação). Ela guarda o identificador do provedor, o tenant, a unidade, o estado (ativa ou pausada) e a referência ao segredo de assinatura. É montada e validada **na administração do canal**, quando o `Channels` consulta `Tenancy.Contracts`, e nunca durante o webhook. O segredo é resolvido sem chamada remota lenta por requisição (cache com validade e invalidação).
- **REC-3 (respostas):** identificador de canal desconhecido ou inativo na URL: **404**, sem detalhe. Assinatura inválida: **401**, e nada é gravado. Assinatura válida, mas conta desconhecida dentro do payload: descarta o evento, conta na métrica e responde 200, para evitar tempestade de reenvios. Falha de gravação: **5xx**.
- **REC-4 (só gravar):** nenhum processamento de negócio e nenhuma chamada a outro módulo, à IA ou ao provedor (ARQ-19). Sem conteúdo de mensagem em log, e com limite de tamanho do corpo.
- **REC-5 (tempo):** cada evento grava `provider_timestamp` (do payload), `received_at` (relógio do servidor) e `ingest_seq` (sequência crescente de ingestão).
- **REC-6:** os parâmetros do provedor (assinatura, reenvios, limites) estão sujeitos à confirmação da seção 12.11.

### 12.3 Processing

- **PRO-1 (ingestão):** o worker de ingestão do `Channels` percorre o Inbox por lane (canal, contato) e chama `Conversations.Commands.IngestInboundMessage` ou `RecordDeliveryStatus`. A lane também evita criar duas sessões para o mesmo contato novo quando duas mensagens chegam juntas.
- **PRO-2 (turno de IA):** o `Agents` consome `InboundMessageRegisteredV1` por lane `ServiceSessionId` e orquestra: snapshot de contexto, `AiGateway`, `Knowledge`, `Capabilities` e `Workflows`. Abre o turno com `BeginTurn` e registra a resposta com as cercas do §6 (`StateVersion`, `basedOnInboundSeq`, `TurnId` e época do turno ativo).
- **PRO-3:** `ContactCenter` e outros consomem os eventos de que precisam nos próprios handlers (projeções, roteamento).
- **PRO-4:** nenhum módulo orquestra outro. O Worker só hospeda os handlers, e quem orquestra o turno é o `Agents`, no topo da matriz.
- **PRO-5 (falha permanente da IA):** quando um turno vira `dead`, o `Agents` aplica a política do tenant `OnAiPermanentFailure`: `Handoff` (padrão) ou `AlertOnly`. Em `Handoff`, chama `ContactCenter.RequestHandoff` com o motivo `AiProcessingFailed`. O ContactCenter transfere para um atendente disponível. **Se não houver atendente, o atendimento fica pendente na fila**, em `HumanAssisted` sem responsável (§6), com o histórico preservado no Conversations, e a IA deixa de responder nele. Se o próprio pedido de handoff falhar, vale a repetição idempotente do §6. A política fica nas configurações do agente do tenant, no `Agents`.

### 12.4 Sender

- **SND-1:** lane por (conta, destinatário), com envio sequencial: o próximo só sai depois da resposta da API do provedor (não da entrega).
- **SND-2:** os limites do provedor (vazão por conta e limite por par de destinatário) são **parâmetros configuráveis** (§12.11), aplicados por balde de fichas guardado no banco.
- **SND-3:** estados `Pending → Sending → Sent`, e ainda `Failed`, `Expired`, `SendUnknown` e `Dead`.
- **SND-4 (validade operacional):** `expires_at` é a **validade operacional** da mensagem, definida pela nossa política (por exemplo, uma resposta que deixou de fazer sentido). **Não é a janela de atendimento de 24 horas do WhatsApp**, que é regra do provedor e outra verificação.
- **SND-5 (janela de atendimento):** antes de enviar mensagem livre, o `Channels` verifica se a janela está aberta, a partir do `provider_timestamp` da última mensagem do cliente. Com a janela fechada, não envia mensagem livre e **avalia o uso de template aprovado**, conforme as políticas do provedor (categoria permitida, aprovação do template, consentimento e custo, a confirmar). Se não for possível, marca `Failed(WindowClosed)` e avisa o atendimento. Esse erro não é retentado automaticamente. A política de templates é item de avaliação da etapa 5.
- **SND-6 (erro de conta):** erro que atinge a conta (token expirado, conta restrita) pausa a conta e alerta, em vez de consumir as tentativas de cada mensagem.
- **SND-7 (falha definitiva):** `Failed`, `Expired` e `Dead` são falhas definitivas de envio. O `Channels` as informa ao `Conversations` (`RecordDeliveryStatus`), que publica `OutboundMessageFailedV1`. O `ContactCenter` sinaliza o atendimento, e a recuperação depende de ação autorizada pelo atendente. **Falha de envio não é entrada para a IA e não dispara novo turno** (`CONVERSATION-LIFECYCLE.md` §11).

### 12.5 Mecânica no PostgreSQL

- **Colunas padrão** de toda fila: `id`, `tenant_id`, `ordering_key`, `ingest_seq`, `state`, `attempts`, `next_attempt_at`, `lease_owner`, `lease_token`, `lease_expires_at`, `dedupe_key` (única por conta), `provider_timestamp`, `received_at`, `payload`, `last_error`, `dead_reason`, `created_at` e `expires_at`. Para efeitos externos, também `operation_id` e `effect_state` (§12.7).
- **Lane lease:** uma tabela `lane` por fila (`ordering_key`, `tenant_id`, `lease_owner`, `lease_token`, `lease_expires_at`, `last_served_at`). O worker adquire a lane com `SELECT … FOR UPDATE SKIP LOCKED` seguido de `UPDATE`, processa os eventos dela em ordem e a libera. O lease **não segura transação nem conexão** durante chamadas lentas, como as de IA. `SKIP LOCKED` sozinho nos eventos não garantiria a ordem dentro da sessão.
- **Lease e fencing:** o lease renova por heartbeat, e concluir um evento exige o `lease_token` vigente. **Isso impede processamento concorrente, mas não impede efeito externo duplicado (§12.7).**
- **Retentativa:** backoff exponencial com jitter. **Transitório** (rede, 5xx, 429, lease perdido) retenta. **Permanente** (validação, destinatário inválido, política) vai direto a `dead`. **Veneno** (exceção inesperada repetida) vai a `dead` depois do máximo de tentativas.
- **Falha permanente:** o evento vai a `dead` com motivo e a lane segue com o próximo. Uma ação de administrador reenfileira ou descarta, com auditoria.
- **Idempotência em camadas:** `dedupe_key` única no Inbox; `ProviderEventId` único no Conversations; `EventId` por consumidor nos handlers; `operation_id` nos efeitos externos.
- **Retenção e manutenção:** purga dos processados por política (período a definir, LGPD) e ajuste de autovacuum.
- **Acordar workers:** polling com intervalo configurável. `LISTEN/NOTIFY` só depois de medir.

### 12.6 Ordem, atraso e fora de ordem

- **ORD-1:** a lane é a unidade de exclusão: dentro dela o processamento é **sequencial**, e entre lanes é paralelo.
- **ORD-2:** há duas lanes encadeadas. A ingestão é por (canal, contato). O turno é por `ServiceSessionId`, que só existe depois da ingestão.
- **ORD-3:** teto de eventos por ciclo de lease, para uma sessão não monopolizar o worker.
- **ORD-4:** mensagens pendentes da mesma sessão viram um único turno, conforme o agrupamento (janela quieta e teto de espera) especificado em `docs/architecture/CONVERSATION-LIFECYCLE.md` §7, com a ordem preservada.
- **ORD-5 (três tempos preservados):** `ingest_seq` (ordem de ingestão: define a sequência de processamento e a auditoria), `provider_timestamp` (momento no provedor: ordena a exibição e o contexto) e `received_at` (relógio do servidor). O atraso do evento é `received_at − provider_timestamp`.
- **ORD-6 (atrasados e fora de ordem, tratados explicitamente):** nenhum evento é descartado por atraso. Uma mensagem atrasada entra no histórico na posição do `provider_timestamp`, mantendo o `ingest_seq`. Se a sessão já respondeu depois desse instante, o `Agents` recebe a marca `late_arrival` e decide por um novo turno com o contexto atualizado. A resposta anterior não é apagada nem reenviada. **Status** (enviada, entregue, lida, falha) segue máquina de estados monotônica e nunca regride. Status que chega antes de o id da mensagem estar gravado fica estacionado, com retentativa limitada.
- **ORD-7:** o atraso é medido (§12.10), e o limiar de alerta é configurável.

### 12.7 Efeitos externos

- **EFX-1:** lease e `StateVersion` evitam processamento concorrente e resposta obsoleta, mas **não garantem ausência de efeitos duplicados**: a chamada ao sistema externo pode ter ocorrido antes de a falha ser percebida.
- **EFX-2:** toda operação externa com efeito colateral (envio ao provedor, escrita no LabNet ou no BImeta, qualquer outra) segue **uma** destas estratégias, declarada na porta:
  - **(a) idempotência comprovada:** o destino aceita chave de idempotência, confirmada em documentação **e** em teste;
  - **(b) reconciliação:** a intenção é gravada **antes** da chamada (`operation_id`, com `effect_state` `Intent → Dispatching → Confirmed` ou `Unknown`) e confirmada depois por consulta ao sistema de origem ou por evento correlacionável;
  - **(c) nenhuma das duas:** não há reenvio automático, e o resultado ambíguo exige decisão humana.
- **EFX-3 (resultado ambíguo):** timeout depois de a requisição poder ter saído, ou queda entre o efeito e a confirmação, **nunca é reenviado automaticamente sem confirmação segura ou mecanismo de idempotência comprovado**. O estado é `SendUnknown` (envio) ou `Unknown` (outros), visível na administração e ao atendente.
- **EFX-4:** todo `CapabilityDescriptor` com efeito colateral declara `IdempotencySupport` (`Proven`, `Reconcilable` ou `None`). `None` não é reenviado automaticamente (ARQ-21).
- **EFX-5 (WhatsApp hoje):** a chave de idempotência e a correlação por `biz_opaque_callback_data` **não estão confirmadas** em documentação oficial. Até a confirmação (gate da etapa 5), o envio é tratado pela estratégia (c).

### 12.8 Limites e justiça

- **LIM-1 (desde o início):** a partir do primeiro processamento concorrente, há `max_in_flight` por tenant e por carga de trabalho (ingestão, turno, envio), contado no banco (vale entre instâncias) e configurável sem redeploy. Os limites de **IA** ficam no `AiGateway` (dependem da chave BYOK do tenant), os de **conectores** no `Capabilities` e os do **provedor de canal** no `Channels`.
- **LIM-2:** entre tenants abaixo do teto, a lane mais antiga vai primeiro (FIFO).
- **LIM-3 (escalonamento justo avançado, sob demanda):** o escalonador por tempo virtual e peso **só é construído se o teste de inundação (§12.13) demonstrar a necessidade**: a espera dos demais tenants excede a meta definida, ou algum tenant passa fome mesmo com os tetos.
- **LIM-4:** semáforos de IA e de conectores por instância. O limitador distribuído só entra com mais de uma instância e com medição.

### 12.9 Contratos abstratos e troca de transporte

- **O que os módulos conhecem** (no `SharedKernel`): o envelope (`EventId`, tipo e versão, `TenantId`, `CorrelationId`, `CausationId`, `OrderingKey`, `OccurredAt`); o publicador durável (grava no outbox **na transação do módulo**); e o handler, com garantia **pelo menos uma vez**, ordem por `OrderingKey`, idempotência por `EventId` e resultado `Success`, `Retry(após)` ou `Dead(motivo)`.
- **O que só o `Messaging` conhece:** o transporte. Hoje é PostgreSQL com lanes. Com RabbitMQ ou outro broker, o relay publicaria no broker, com ordem por partição ou *consistent-hash* e consumidor único por fila, e a fila de mortos viraria DLX.
- **Não muda com um broker:** o código dos módulos, os contratos dos eventos, o outbox transacional, as tabelas de idempotência e os nomes das métricas. **Muda:** a implementação do `Messaging`, a implantação e a configuração. O Inbox do webhook continua no PostgreSQL.
- **Critérios para considerar um broker**, a calibrar pelo teste de carga: a mecânica de filas dominar a carga do PostgreSQL; a latência de aquisição de lane acima da meta; consumidores em hosts separados; necessidade de roteamento e fan-out; vazão acima do medido. Sem limiares numéricos por ora.

### 12.10 Observabilidade

Usa `System.Diagnostics.Metrics` e `ActivitySource` do .NET, **sem pacote novo**. Exportadores (por exemplo OpenTelemetry) são pacote e entram depois, com aprovação.

- **Por fila:** profundidade por estado, idade do evento mais antigo, entrada e saída, espera na fila e tempo de processamento (histogramas), tentativas, retentativas por motivo, mortos por motivo, leases expirados, lanes ativas e eventos em andamento.
- **Receiver:** webhooks por resultado (`accepted`, `duplicate`, `invalid_signature`, `unknown_channel`, `error`) e latência.
- **Sender:** envios por resultado, estrangulamentos por tipo (vazão, par, HTTP 429), disjuntor aberto e total de `SendUnknown`.
- **Tempo e atraso:** atraso do evento (`received_at − provider_timestamp`) e eventos marcados `late_arrival`.
- **Falha de IA:** handoffs por falha permanente e atendimentos pendentes sem atendente.
- **Consumo por tenant:** agregados horários em tabela do `Messaging` (mensagens, turnos, chamadas de IA, tokens, chamadas de capacidade, espera na fila, uso do teto), **e não como rótulo de métrica**, para evitar cardinalidade explosiva. Alimenta também a observabilidade de custo do BYOK (ARCH §5).
- **Alertas:** idade do mais antigo acima do limite, qualquer `dead`, taxa de lease expirado, taxa de não 200 e de assinatura inválida, tenant no teto por muito tempo, estrangulamento prolongado, `SendUnknown` acumulado.
- **Logs** estruturados com `TenantId`, `CorrelationId`, `ServiceSessionId` e `EventId`, sem conteúdo de mensagem nem dado pessoal. Há uma visão administrativa dos `dead`.

### 12.11 Provedor de canal: duplicatas, status, indisponibilidade e parâmetros a confirmar

- **Duplicatas:** a chave de deduplicação é o id da mensagem do provedor por conta. Para status, que não têm id, é um hash de (id da mensagem, status, timestamp), e a máquina de estados idempotente torna a duplicata inofensiva.
- **Provedor indisponível:** a fila de envio acumula com backoff e disjuntor por conta. O Receiver não é afetado.
- **Nós indisponíveis:** se o Receiver falhar, o provedor reenvia conforme a política dele. Fora dessa janela a mensagem se perde, por isso o alerta de taxa de erro do webhook é crítico. Ao voltar, o acúmulo é absorvido pelos limites por tenant.

**Parâmetros sujeitos à confirmação** (leitura por extrator em 26/10/2026, não texto integral):

| Parâmetro | Valor indicado | Situação |
|---|---|---|
| Reenvio do webhook sem HTTP 200 | por até 7 dias, com frequência decrescente; pode duplicar | a confirmar |
| Tempo máximo para responder 200 | não consta na página lida | a confirmar |
| Assinatura `X-Hub-Signature-256` | não consta na página lida | a confirmar |
| Ordem de entrega dos eventos | não consta na página lida | a confirmar |
| Vazão por número | 80 mensagens por segundo por padrão | a confirmar |
| Limite por par (empresa e usuário) | 1 mensagem a cada 6 s, rajada de 45 em 6 s com empréstimo; erro 131056 | a confirmar |
| Erros a retentar com espera | 130429, 131056, 131000 | a confirmar |
| Erros sem retentativa automática | 131047, 131049, 131026, 131048 | a confirmar |
| Erros de conta | 190, 368 | a confirmar |
| Janela de 24 horas e uso de templates | não consta na página lida (o erro 131047 indica template) | a confirmar |
| Chave de idempotência no envio e `biz_opaque_callback_data` | não consta na página lida | a confirmar |

- **PRV-1:** esses valores são **parâmetros de configuração**, não constantes de código.
- **PRV-2 (gate):** nenhuma implementação do `Channels` (etapa 5) começa sem confirmar cada item na documentação oficial e registrar data e fonte no ADR do `Channels`.

### 12.12 Faseamento (só com necessidade comprovada)

| Etapa | O que nasce |
|---|---|
| 3 | contratos de eventos no `SharedKernel` e dublê em memória; nenhum outbox real |
| 4 | `Worker` e `Messaging` v0, com o primeiro processamento assíncrono durável: o `ContactCenter` consome `HandlingModeChangedV1` (outbox, relay e inbox do consumidor). Sem escalonador justo nem lanes avançadas |
| 5 | Receiver, Inbox, ingestão por lane e Sender; confirmação dos parâmetros do provedor; limites por tenant no primeiro processamento concorrente |
| 6 e 7 | turno de IA por lane, falha permanente com handoff, limites de IA e de conectores |
| sob demanda, comprovada por teste | escalonamento justo avançado, limitador distribuído, particionamento, `LISTEN/NOTIFY`, broker |

O `Messaging` cresce só até onde a necessidade foi comprovada. Funcionalidade antecipada é rejeitada na revisão.

### 12.13 Plano de testes

Todos usam o PostgreSQL 17 local (porta 5433) com dados sintéticos e dublês: um `IChannelAdapter` falso e um `IAiProvider` falso, com latência e falha injetáveis. Nenhuma biblioteca nova por ora; ferramentas de carga exigem instalação e aprovação. Os números absolutos desta máquina são só relativos.

**Invariantes** (verificáveis, sem números inventados), válidas em todos os testes:

- nada se perde: aceitos = processados + mortos;
- nenhum efeito externo duplicado quando a estratégia é (a) ou (b), e nenhum reenvio automático quando é (c);
- ordem estrita por lane;
- no máximo uma lease ativa por lane;
- o teto por tenant nunca é excedido;
- nenhum contexto cruza tenants.

| Grupo | Cenários |
|---|---|
| **Receiver** | rajada de webhooks concorrentes; 100 repetições do mesmo evento geram 1 linha; assinatura inválida (zero linhas); banco indisponível (5xx e nenhuma perda); falha injetada no commit (nenhum 200); lote com várias mensagens; corpo acima do limite; canal desconhecido (404); conta desconhecida no payload (200 e descarte); invalidação da projeção local do canal |
| **Concorrência** | N workers em disputa, sem processamento duplo; 1.000 sessões com 20 mensagens chegando embaralhadas, com ordem estrita por sessão e paralelismo entre sessões; worker "zumbi" que perdeu a lease não conclui nada |
| **Recuperação** | matar o worker no meio do turno (a lease expira e há uma só resposta efetiva); falha entre o efeito e a confirmação; reiniciar o PostgreSQL sob carga; backlog drenado depois de uma queda; evento veneno vai a `dead` e a lane continua |
| **Falha de IA** | falha permanente com `Handoff` e atendente disponível; sem atendente (atendimento pendente na fila, histórico preservado, IA não responde); política `AlertOnly` por tenant; falha do próprio pedido de handoff |
| **Efeitos externos** | provedor falso com timeout depois de aceitar a mensagem: o envio vira `SendUnknown` e **não** é reenviado; estratégia (a) com chave de idempotência: sem duplicata; estratégia (b): intenção gravada antes, reconciliação confirma; `CapabilityDescriptor` sem `IdempotencySupport` rejeitado (ARQ-21) |
| **Tempo e ordem** | mensagem atrasada entra no histórico pelo `provider_timestamp` e gera `late_arrival`; status fora de ordem não regride; status órfão estacionado e resolvido; medição do atraso do evento |
| **Janela e templates** | janela fechada: sem envio livre, `Failed(WindowClosed)` ou template avaliado; `expires_at` operacional independente da janela |
| **Justiça** | um tenant inunda 10 mil eventos enquanto outros enviam poucos: tetos por tenant respeitados e a espera dos demais medida; se exceder a meta, abre-se a necessidade do escalonador justo (LIM-3); concorrência de IA por tenant nunca excedida (contador no dublê) |
| **Isolamento** | canários entre tenants nos workers; Mauro/Pedro no pipeline assíncrono; evento sem tenant falha fechado; resposta com `StateVersion` antiga rejeitada |
| **Ciclo de vida** | substituição atômica de turnos, resposta defasada sensível, falha definitiva de envio e mídia indisponível, entre outros: ver `CONVERSATION-LIFECYCLE.md` §16 |
| **Provedor** | dublê com 5xx, 429 e timeout (backoff e balde de fichas); status duplicado; erro de conta pausa a conta; erro 131056 faz esperar só aquele destinatário |
| **Resistência** | 1 hora de carga contínua observando inchaço das tabelas de fila, autovacuum, memória e rotatividade de lease |
| **Calibração** | escritas por mensagem trocada (amplificação), latência de aquisição de lane e profundidade sustentável: dão os limiares para a decisão sobre o broker |

## Histórico de alterações

| Data | Versão | Resumo | Aprovador | ADR |
|---|---|---|---|---|
| 26/10/2026 | 0.2 | Versão inicial aprovada: monólito modular, quatro projetos por módulo, matriz verificável, coordenação do modo IA/humano, assistente interno com acesso explícito, governança | Mauro Porpino | 0005 |
| 26/10/2026 | 0.3 | Seção 12 (processamento de mensagens: Receiver, Processing e Sender); `Worker` e `Messaging` na etapa 4; DEP-6; ARQ-19 a ARQ-21; envelope com `OrderingKey` e `CausationId`; atendimento pendente sem responsável | Mauro Porpino | 0006 |
| 26/10/2026 | 0.4 | Ciclo de vida de mensagens e turnos de IA: cercas de resposta (`TurnId`, época, `basedOnInboundSeq`), turno logicamente ativo, cobertura lógica × entrega, mídia pendente; resposta defasada sempre rejeitada por regra fixa, sem `StaleReplyMode` (a exceção é evolução futura e depende de novo ADR); propriedade das configurações; `RequestResend` com garantias; comportamento inicial de `OnChattyBurst`; DEP-7 (futura); ARQ-22 a ARQ-24; SND-7; ORD-4 | Mauro Porpino | 0007 |
| 27/10/2026 | 0.4.1 | Ajuste de redação do §1: registra que os projetos globais antigos já saíram da solução e que o `Conversations` nasceu na etapa 3. Nenhuma decisão de fronteira mudou | Mauro Porpino | — |
