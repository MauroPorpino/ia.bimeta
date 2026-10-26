# ADR 0005 — Monólito modular e fronteiras de módulo

- **Status:** Aceito
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `ARCHITECTURE.md` (seções 2, 4, 8 e 11), `docs/architecture/BOUNDARIES.md` v0.2, `docs/architecture/module-dependencies.json`, ADRs 0001 a 0004
- **Substitui em parte:** ADR 0003, itens 1 (quatro projetos globais) e 4 (`TenantId` no Domain). Os itens 2, 3 e 5 do ADR 0003 permanecem.

## Contexto

O ADR 0003 criou quatro projetos por camada, globais. Com os módulos previstos no `ARCHITECTURE.md` (Conversation Engine, Central de Atendimento, Employee AI Workspace e outros), camadas globais misturariam os módulos e não permitiriam verificar as fronteiras.

## Decisão

1. Monólito modular em .NET 10, com um deploy, com Clean Architecture e Ports & Adapters **dentro de cada módulo**.
2. Quatro projetos por módulo: Contracts, Core (Domain e Application), Infrastructure e Presentation, cada um criado só quando necessário. Hosts `Api` e `Worker`, `SharedKernel` mínimo e um projeto por adaptador de sistema externo.
3. Módulos se comunicam por Contracts (síncrono, padrão) e por eventos. Evento interno em processo; **outbox próprio só para eventos que exigem entrega confiável**. Sem biblioteca de mediador ou barramento.
4. Um esquema PostgreSQL por módulo desde o início. Papéis de banco por módulo são avaliados depois da fase 2b, em ADR próprio.
5. O Conversation Engine é a autoridade do estado e do contexto da conversa, inclusive do modo IA/humano. O ContactCenter decide filas, atribuição e política e solicita transições com versão de estado e identificador idempotente, o que impede respostas concorrentes.
6. O `RequestContext` só nasce de fontes autenticadas e confiáveis, com permissão efetiva validada para tenant, organização, unidade e recurso.
7. O `EmployeeAssistant` só lê trechos de atendimento por ação explícita e autorizada do colaborador.
8. A matriz de dependências é um arquivo versionado (`module-dependencies.json`) verificado por teste. Mudança relevante exige ADR; ajuste menor é registrado no `BOUNDARIES.md` com aprovação do decisor.
9. Testes de arquitetura com ArchUnitNET como biblioteca preferencial, **sujeita a spike aprovado**: a compatibilidade com .NET 10 está declarada, mas ainda não foi comprovada por execução.
10. A voz permanece **reservada por documento**, sem projeto nem código. A interface mínima `IVoiceGateway` nasce junto com o `Channels.Contracts` (etapa 5), para cumprir o critério 10 do ADR 0002 sem implementação.

## Alternativas descartadas

- Camadas globais (ADR 0003).
- Domain em projeto próprio já na origem (adiado; critério PRJ-5 do `BOUNDARIES.md`).
- Eventos assíncronos para toda interação entre módulos.
- ContactCenter como autoridade do modo de atendimento.
- Papéis de banco por módulo desde o início.
- Microsserviços.

## Consequências

- O `TenantId` e seus 3 testes vão para o `SharedKernel`. Os projetos globais `Domain`, `Application` e `Infrastructure` e o `Domain.Tests` saem numa migração posterior, que exige aprovação própria.
- O teste Mauro/Pedro (pulado) vai para `IntegrationTests` quando esse projeto nascer. Até lá, fica no projeto de testes do `SharedKernel`.
- Mais projetos exigem mais tempo de build. O impacto **não foi medido**; a criação sob demanda o limita.
- `CLAUDE.md` e `ARCHITECTURE.md` (v0.4) passam a referenciar o `BOUNDARIES.md`.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Violação silenciosa por regra negativa vazia | As regras afirmam que o conjunto avaliado não é vazio, e as críticas têm fixture negativa. |
| Mono.Cecil não ler os metadados do .NET 10 | Spike antes de adotar o ArchUnitNET, com plano B por reflexão e XML dos `.csproj`. |
| Divergência entre o `BOUNDARIES.md` e o JSON da matriz | O JSON prevalece e é testado. |
| Excesso de projetos nesta máquina | Criação sob demanda (PRJ-2). |

## Critérios de aceitação

1. O teste de grafo de projetos falha ao introduzir referência direta entre núcleos de módulos diferentes.
2. O teste da matriz falha ao introduzir ciclo ou aresta fora do JSON.
3. Resposta com `StateVersion` antiga é rejeitada.
4. O `EmployeeAssistant` não compila com referência a `Channels`.
5. Nenhum `RequestContext` é criado fora dos hosts.

## Pendências

- Resultado do spike do ArchUnitNET.
- Avaliação dos papéis de banco por módulo após a fase 2b.
- Escolha entre EF Core e SQL direto (`ARCHITECTURE.md` §13).
- Migração da solução atual para a organização por módulos.

## Revisão

Reavaliar se o custo de projetos superar o benefício, se o Domain precisar de projeto próprio (PRJ-5) ou se surgir necessidade de processo separado (PRJ-6).
