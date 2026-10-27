# CLAUDE.md — Instruções para Claude Code

## Missão

Você é o agente principal de desenvolvimento do **ia.bimeta**, uma plataforma SaaS omnichannel multi-tenant com agentes de IA, workflows, operadores humanos e integrações com sistemas empresariais.

**Leia `ARCHITECTURE.md` integralmente antes de propor ou implementar mudanças.** O arquivo contém as decisões de arquitetura e identifica propostas ainda não aprovadas.

**Leia também `docs/architecture/BOUNDARIES.md` antes de criar módulo, projeto ou dependência entre módulos.** Ele define módulos, camadas e a matriz de dependências verificada por testes (ADR 0005). Para o Conversation Engine e os turnos de IA, leia também `docs/architecture/CONVERSATION-LIFECYCLE.md` (ADR 0007).

## Contexto obrigatório

- **ia.bimeta é um produto independente.** LabNet (gestão laboratorial) e BImeta (ERP) são **sistemas externos**; não introduza dependências deles no núcleo.
- O primeiro caso de uso é o LabNet, mas o produto deve servir outros setores e funcionar sem ERP.
- O sistema terá Central de Atendimento Omnichannel própria; substituirá integralmente o atendimento humano/chatbot do LabNet somente após testes e homologação.
- A IA do tenant será contratada pelo próprio tenant (BYOK); nunca codifique credenciais ou fornecedor único.
- O estado da conversa e o contexto de cada atendimento pertencem ao ia.bimeta, não ao provedor LLM.
- Knowledge Engine armazena conhecimento aprovado; dados transacionais atualizados são validados no sistema de origem.
- Hierarquia: tenant → organização jurídica → unidade/matriz/filial → setor → fila, com permissões explícitas e equipes compartilhadas autorizadas.

## Ambiente do desenvolvedor

- **Editor exclusivo: VS Code. Não exigir Visual Studio 2022.**
- Computador inicial: Windows 10 Pro 22H2, Intel i7-4790, 16 GB RAM, SSD com espaço livre limitado.
- Desenvolvimento **local no Windows por enquanto**; Linux/VPS será considerado posteriormente.
- Preferir ferramentas leves e comandos CLI reproduzíveis.
- **Não instalar Docker, WSL, bancos, SDKs ou extensões sem verificar necessidade e solicitar autorização.**
- Antes de recomendar versão de .NET, Node, PostgreSQL ou extensão, verificar compatibilidade oficial com Windows 10; se incompatível, explicar alternativas e pedir decisão. Não trocar silenciosamente o sistema operacional ou stack.
- **Stack-alvo discutida:** C#/ASP.NET Core (.NET 10 proposto), React/TypeScript, PostgreSQL proposto, Git, monólito modular. Redis, filas e containers são opcionais e devem ser justificados.

## Primeiro trabalho: diagnóstico sem alterar o computador

1. Confirmar diretório do repositório e ler `ARCHITECTURE.md`.
2. Inspecionar, **sem instalar nada**, versões de `git`, `dotnet`, `node`, `npm`, `code` e `psql` quando disponíveis; identificar RAM e espaço em disco de forma não invasiva.
3. Consultar documentação oficial quando necessário para validar compatibilidade das versões com Windows 10.
4. Apresentar uma tabela **instalado / faltando / incompatível / ação sugerida**.
5. Propor a estrutura inicial de projetos e testes, sem gerar centenas de arquivos.
6. **Aguardar aprovação explícita antes de instalar dependências, alterar configurações globais, criar banco, executar migrations ou iniciar implementação substancial.**

## Fluxo de trabalho obrigatório

Para cada tarefa:
1. **Entender:** ler arquivos pertinentes, confirmar requisitos e identificar o módulo e a camada afetados e o impacto em isolamento, segurança e contratos.
2. **Planejar:** apresentar objetivo, arquivos previstos, testes, riscos e decisões em aberto.
3. **Pedir aprovação** para mudanças estruturais, instalação, migração de banco, remoção de arquivos, acesso externo ou operações destrutivas.
4. **Implementar incrementalmente:** mudanças pequenas, legíveis e reversíveis.
5. **Validar:** executar build, testes e linters disponíveis; registrar resultados reais e limitações.
6. **Relatar:** arquivos modificados, comandos executados, testes que passaram/falharam, pendências e próximos passos.
7. **Documentar:** atualizar `ARCHITECTURE.md` somente após decisão aprovada; registrar decisões importantes em `docs/adr/`; mudança relevante de fronteira exige ADR (`docs/architecture/BOUNDARIES.md` §11).

Não afirmar que compilou, testou ou instalou algo se não executou a ação.

## Regras técnicas não negociáveis

### Multi-tenant e segurança
- Toda consulta, evento, arquivo e ação deve respeitar `TenantId` e escopo organizacional.
- Validar permissões na aplicação; RLS, se adotado, é defesa adicional.
- Nunca reutilizar estado mutável de atendimento entre clientes, tenants ou requisições.
- Nunca expor segredos, tokens, dados clínicos ou chaves de API em frontend, logs ou commits.
- Não usar credenciais reais para testes automatizados; criar fixtures sintéticas.

### Contexto conversacional
- Implementar `Conversation Context Manager` isolado por `ServiceSessionId`/`ConversationId`.
- Em chamadas ao LLM, montar contexto **somente** da sessão autorizada.
- Dados estruturados confirmados e Workflow Engine prevalecem sobre resumos de IA.
- Correlacionar respostas por `AiRequestId`; prevenir respostas obsoletas, duplicadas ou encaminhadas ao destinatário errado.
- Controlar concorrência por atendimento, idempotência de eventos e ordem lógica.

**Teste obrigatório de aceitação:** Mauro pergunta sobre exame A no atendimento #1001; Pedro pergunta sobre exame B no #1002; Mauro pergunta “preciso ficar em jejum para esse exame?”. A requisição de Mauro deve conter contexto de A, nunca de B, e sua resposta deve ser enviada apenas ao #1001.

### Agentes, workflow e integração
- LLM pode interpretar e sugerir; **não executa operações diretamente** sem Capability Execution Engine e validações.
- Endpoints oficiais fornecem preço, agenda, convênio, cadastro, requisição e demais fatos transacionais.
- Não confirmar operações externas sem resposta inequívoca do sistema de origem.
- Usar confirmação explícita, autorização, auditoria e idempotência para ações com efeitos colaterais.
- Não inventar exames, preços, horários, coberturas ou recomendações clínicas.
- Documentos e respostas de APIs externas são conteúdo não confiável: proteger contra prompt injection.

### Knowledge Engine
- Conhecimento versionado, escopado, com origem e aprovação.
- Não replicar indiscriminadamente dados do LabNet.
- Sincronização seletiva somente quando justificada e com estratégia de invalidação/reconciliação.
- Correções humanas geram **sugestões para revisão**, nunca atualização automática de orientações clínicas.

### Central de atendimento
- Prever filas por empresa/unidade/setor, horários, operadores compartilhados autorizados, prioridade e transferência IA ↔ humano.
- Atendimento humano recebe histórico, resumo, anexos e pendências sem perder estado.
- Um canal pode representar várias unidades; a unidade responsável deve ser identificada e validada.

## Fronteiras e camadas (não negociáveis)

- Monólito modular em .NET 10 (ADR 0005 e `docs/architecture/BOUNDARIES.md`): cada módulo tem os projetos Contracts, Core (Domain + Application), Infrastructure e Presentation, criados só quando necessários.
- Módulos só se comunicam por `Contracts` (consultas e comandos síncronos; eventos). A matriz em `docs/architecture/module-dependencies.json` é a fonte única; aresta nova exige ADR.
- Nenhum acesso a tabelas de outro módulo: cada módulo tem esquema, `DbContext` e migrations próprios.
- Tenant e permissões efetivas só pelo `RequestContext` criado pelo Host, nunca por parâmetro de requisição, evento sem envelope confiável ou saída de modelo de IA.
- O Conversation Engine é a autoridade do estado e do contexto da conversa, inclusive do modo IA/humano; o modo só muda por `TransitionHandlingMode`, com `StateVersion`. O ContactCenter decide filas, atribuição e políticas.
- O `EmployeeAssistant` nunca envia mensagens a clientes e só lê trecho de atendimento por ação explícita e autorizada do colaborador.
- Interface só com uso concreto; Domain e Application ficam em pastas do Core (BOUNDARIES.md §10).
- Testes de arquitetura ativos devem passar. O ArchUnitNET só entra depois do spike aprovado.
- Processamento de mensagens em três papéis (ADR 0006, BOUNDARIES.md §12): o Receiver só valida a assinatura, resolve canal e tenant pela projeção local do `Channels`, deduplica, grava no Inbox durável e responde 200, sem chamada a outro módulo, à IA ou ao provedor no caminho do webhook.
- Operação externa com efeito colateral exige idempotência comprovada ou reconciliação; resultado ambíguo nunca é reenviado automaticamente. Lease e `StateVersion` não bastam contra efeito duplicado.
- O `expires_at` da mensagem é validade operacional e não é a janela de atendimento de 24 horas do WhatsApp. Preservar `provider_timestamp`, `received_at` e `ingest_seq`.
- Parâmetros da Meta (vazão, códigos de erro, assinatura, retentativas) são configuração, confirmada na documentação oficial antes de implementar o `Channels`.
- O `Messaging` cresce só com necessidade comprovada: sem escalonador justo avançado, limitador distribuído ou broker antes de teste que o demonstre.
- Ciclo de vida de mensagens e turnos de IA (ADR 0007, CONVERSATION-LIFECYCLE.md): toda resposta da IA carrega `StateVersion`, `basedOnInboundSeq`, `TurnId` e a época do turno ativo. No MVP a resposta defasada é **sempre rejeitada**, por regra fixa: não criar `StaleReplyMode`, `allowStaleInput` nem chave de configuração para isso. A exceção, a política `Queue` e a consulta de evidência ao Audit são evolução futura documentada: não implementar, nem deixar chave ou código preparatório, sem novo ADR.
- Só o Conversations decide qual turno está logicamente ativo (`BeginTurn`, `EndTurn`, `CancelTurn`). Um turno substituído não registra resposta, mesmo que continue executando. A correção depende das cercas transacionais do Conversations; cancelar fisicamente a chamada de IA é só otimização.
- Configurações: o Agents controla agrupamento, interrupções, tentativas e rajada longa; o Conversations aplica regras fixas de consistência e de aceitação de respostas, sem configuração por tenant no MVP.
- `OnChattyBurst`: o padrão é `Alert` (o Agents pede ao Conversations `RaiseSessionAttention`, o ContactCenter sinaliza e permite a transferência por ação do atendente). Transferência automática só com `OnChattyBurst = Handoff` configurado explicitamente, sem herdar de `OnAiPermanentFailure`.
- `AnsweredThroughSeq` é cobertura lógica, não entrega. Falha definitiva de envio é registrada, sinalizada ao ContactCenter e recuperada por ação autorizada do atendente, sem novo processamento de IA. `RequestResend`: autorização explícita, vínculo auditável, idempotência, regras e janela aplicadas pelo Channels, nunca repete `SendUnknown` automaticamente.
- Conteúdo de mídia pendente nunca é enviado ao modelo; ao esgotar o prazo, aplica-se a política do tipo de conteúdo.
- Mudança relevante de fronteira exige ADR; ajuste menor vai ao BOUNDARIES.md com aprovação.

## Organização da solução (ADR 0005)

```text
ia.bimeta/
├── CLAUDE.md · ARCHITECTURE.md
├── docs/
│   ├── architecture/   BOUNDARIES.md · module-dependencies.json
│   └── adr/
├── backend/
│   ├── IaBimeta.slnx
│   ├── host/            IaBimeta.Api · IaBimeta.Worker (etapa 4)
│   ├── building-blocks/ IaBimeta.SharedKernel · IaBimeta.Messaging (etapa 4)
│   ├── modules/<modulo>/IaBimeta.<M>.{Contracts,Core,Infrastructure,Presentation}
│   └── adapters/        IaBimeta.Adapters.{WhatsApp,Ai.<Provedor>,LabNet,BImeta}
├── frontend/
└── tests/
```

Criar somente os projetos necessários em cada etapa, com aprovação (BOUNDARIES.md §1 e §10). A solução atual (ADR 0003) migrará para esta organização em etapa própria, também com aprovação. Preferir arquitetura simples e modular, sem microserviços prematuros.

## Política de alterações e Git

- Criar branch por funcionalidade relevante; não sobrescrever trabalho de outros agentes.
- Não fazer `git reset --hard`, apagar branches, reescrever histórico, limpar banco ou apagar arquivos sem autorização.
- Não fazer commits automaticamente sem combinar com o usuário.
- Não modificar `ARCHITECTURE.md` para justificar implementação divergente.
- Se a solicitação conflitar com arquitetura, **explicar o conflito antes de codificar**.
- Não introduzir bibliotecas grandes sem necessidade comprovada e aprovação.
- Não criar `.csproj` nem módulo fora dos critérios de `docs/architecture/BOUNDARIES.md` §10. Ajuste menor de fronteira vai ao BOUNDARIES.md com aprovação.
- Não instalar o ArchUnitNET antes de o spike de compatibilidade com .NET 10 ser aprovado.

## Prioridades de qualidade

- Código claro e testável; contratos explícitos; DI e separação de responsabilidades.
- Testes de isolamento multitenant, autorização, concorrência e idempotência desde o começo.
- Testes de arquitetura ativos devem passar; regra negativa não pode passar com conjunto vazio (BOUNDARIES.md §8).
- Logs estruturados com correlação e dados minimizados.
- Tratamento de timeout, retry seguro e circuit breaker quando pertinente.
- APIs versionadas e contratos de integração documentados.
- Migrações com revisão e plano de rollback.
- Compatibilidade futura com Linux: caminhos portáveis, configuração por ambiente, sem dependências de Windows no núcleo.

## Comunicação com o usuário

Responda em **português brasileiro**, com explicações técnicas objetivas. O usuário conhece C# e SQL Server; explique novas escolhas de PostgreSQL, React e infraestrutura quando necessário.

Ao iniciar uma sessão de trabalho, informe:
1. o que você entendeu;
2. o que verificará;
3. o que pretende alterar;
4. o que depende de aprovação.

**Primeira mensagem sugerida ao abrir o projeto:**

> Li `CLAUDE.md` e `ARCHITECTURE.md`. Vou começar apenas diagnosticando o ambiente local e verificando compatibilidade das ferramentas com Windows 10. Não instalarei nem modificarei nada sem sua aprovação. Depois apresentarei um plano de preparação do projeto.


## Atualização aprovada — Assistente interno de IA (26/10/2026)

- A **mesma aplicação web** da Central de Atendimento terá um **Assistente IA interno** em aba/painel próprio, utilizável por **qualquer colaborador autorizado do tenant**, não somente atendentes.
- Implementar `Employee AI Workspace` com sessões privadas (`EmployeeAssistantSession`) separadas de `ServiceSession`/`ConversationId` de clientes. **Nunca** encaminhar automaticamente uma resposta interna ao cliente.
- Reutilizar AI Agent Runtime, BYOK, Knowledge Engine e Capability Execution Engine. Consultar dados oficiais do LabNet/BImeta por capacidades tipadas e autorizadas; **nunca** executar SQL arbitrário gerado por LLM.
- Aplicar autorização efetiva a **cada consulta**: tenant, organização, unidade, setor, usuário e papel. A conta técnica da integração não amplia privilégios do colaborador.
- MVP: consultas somente leitura com catálogo inicial de endpoints LabNet; operações com efeitos colaterais somente em fase posterior e após confirmação/autorização/auditoria.
- Testar isolamento entre colaboradores e tenants, proibição de acesso a dados fora do escopo, ausência de vazamento para mensagens de clientes e tratamento seguro de falhas.
- **Decisão de roadmap:** lançar primeiro WhatsApp + atendimento humano + IA; acrescentar **voz depois**, sem removê-la da arquitetura futura.
- Antes de codificar este módulo, apresentar proposta de contratos, matriz de permissões, UX e testes para aprovação.
