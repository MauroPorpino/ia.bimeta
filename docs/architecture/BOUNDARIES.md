# Fronteiras e camadas do ia.bimeta (BOUNDARIES.md)

- **Status:** Aprovado (v0.2)
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Formalizado por:** ADR 0005
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
├── host/            IaBimeta.Api · IaBimeta.Worker (etapa 5)
├── building-blocks/ IaBimeta.SharedKernel
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

`Audit` e os projetos `Infrastructure` entram na 2b. Os projetos globais atuais (`Domain`, `Application`, `Infrastructure`) e o `Domain.Tests` saem numa etapa de migração posterior, que também exige aprovação.

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

## 4. Comunicação entre módulos

- **COM-1 (padrão):** chamada **síncrona** por `Contracts.Queries` e `Contracts.Commands`, com o `RequestContext` e resposta em DTO ou `Result`, nunca entidade nem `IQueryable`. Nada obriga comunicação assíncrona.
- **COM-2 (evento interno):** evento em processo, despachado depois do commit, **sem durabilidade**. Serve para avisar módulos superiores e para efeitos tolerantes a perda ou reconstruíveis (projeções, notificações). Tem nome no passado e versão.
- **COM-3 (evento durável, com outbox):** só quando a perda causaria inconsistência ou perda de ação do cliente. Critérios: (a) mensagem de cliente que deve gerar resposta; (b) mudança de estado que outro módulo precisa aplicar para manter invariante; (c) efeito em sistema externo. O envelope tem `EventId`, `TenantId`, `CorrelationId` e `OccurredAt`. A entrega é pelo menos uma vez, com handler idempotente por `EventId`. O outbox próprio só é construído quando o primeiro evento durável entrar em implementação (provável na etapa 3), sem biblioteca de mediador ou barramento.
- **COM-4:** módulo inferior **não chama** superior; avisa por evento. O contrato do evento fica no módulo emissor.
- **COM-5:** não há transação entre módulos. A consistência é eventual, com compensação.
- **COM-6:** `CorrelationId` atravessa as chamadas, e `AiRequestId` correlaciona as respostas da IA.

**Fluxos de referência:**

- **Mensagem recebida:**
  1. `Channels` verifica a assinatura, resolve tenant e unidade e deduplica por `ProviderEventId`.
  2. `Channels` chama `Conversations.Commands.IngestInboundMessage`.
  3. `Conversations` publica `InboundMessageRegisteredV1`, evento **durável** (COM-3a).
  4. `Agents` monta a resposta com `AiGateway`, `Knowledge` e `Capabilities`.
  5. `Agents` chama `Conversations.Commands.RegisterAiReply` com a versão do estado (§6).
- **Mensagem enviada:** `Conversations` publica `OutboundMessageRequestedV1` (durável), `Channels` envia e chama `RecordDeliveryStatus`.
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
- **Sem respostas concorrentes:** toda resposta, de IA ou de operador, carrega a `StateVersion` sob a qual foi produzida. O Conversations rejeita versão ou responsável divergente (`StaleState`), e quem enviou descarta. A transição invalida o turno de IA em andamento.
- **Falha no meio:** o ContactCenter guarda a atribuição como `PendingHandoff` e repete o comando (idempotente por `transitionId`). A conversa permanece no modo anterior até a transição ter sucesso. Um job compara a projeção do ContactCenter com os eventos.
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
- **Sem abstração sem uso:** `ISecretStore`, `IFileStore` e o bloco `Persistence.PostgreSql` **não são definidos agora**. Nascem com o primeiro uso concreto: BYOK, anexos, ou código repetido em dois módulos.
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

## Histórico de alterações

| Data | Versão | Resumo | Aprovador | ADR |
|---|---|---|---|---|
| 26/10/2026 | 0.2 | Versão inicial aprovada: monólito modular, quatro projetos por módulo, matriz verificável, coordenação do modo IA/humano, assistente interno com acesso explícito, governança | Mauro Porpino | 0005 |
