# ADR 0006 — Processamento assíncrono de mensagens: Receiver, Processing e Sender

- **Status:** Aceito
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `ARCHITECTURE.md` (seções 4.1, 4.4, 8 e 11), `docs/architecture/BOUNDARIES.md` v0.3 (seção 12), ADRs 0002, 0004 e 0005
- **Altera:** ADR 0003, item 5, **somente** quanto à etapa do `IaBimeta.Worker` (de 5 para 4). O restante do item permanece.
- **Preserva:** a matriz `docs/architecture/module-dependencies.json` (nenhuma aresta nova) e os ADRs 0001 a 0005.

## Contexto

O recebimento de mensagens de canais externos, o processamento por IA e humanos e o envio ao provedor têm ritmos, falhas e limites diferentes. Executá-los no mesmo caminho da requisição de webhook acoplaria a disponibilidade do recebimento à lentidão da IA e dos sistemas externos, e perderia mensagens quando algo falhasse no meio. A plataforma é multi-tenant, e um tenant com muito volume não pode degradar os demais. O `ARCHITECTURE.md` já exige controle de concorrência por atendimento, idempotência de eventos e ordem lógica (seção 4.1), e já posiciona o Workers como componente (seção 4).

## Decisão

1. **Três papéis lógicos, sem microsserviços.** O **Receiver** roda na `IaBimeta.Api`; os **Processing Workers** e os **Sender Workers** rodam no `IaBimeta.Worker`. A base de código é a mesma, cada papel liga ou desliga por configuração e escala por instância.
2. **Receiver.** Valida a autenticidade sobre o corpo bruto, antes de interpretar o conteúdo; resolve canal e tenant por **projeção local do `Channels`**, sem chamada a outro módulo nem a serviço externo lento; deduplica e grava em um **Inbox durável** antes de responder HTTP 200. Não executa processamento de negócio.
3. **Processing.** Consome o Inbox e os eventos duráveis pelos módulos (Conversation Engine, AI Agent Runtime, ContactCenter, Capability Execution Engine). Nenhum módulo orquestra outro: o `Agents`, no topo da matriz, orquestra o turno de IA.
4. **Sender.** Consome a fila de envio (tabela do `Channels`) respeitando limites e políticas do provedor, que são **parâmetros configuráveis**.
5. **PostgreSQL 17 como mecanismo inicial** de filas, com lane lease, fencing, retentativas, idempotência e tratamento de falhas permanentes (`dead`). O envelope dos eventos ganha `OrderingKey` e `CausationId`. Os contratos abstratos ficam no `SharedKernel` e a mecânica no bloco `IaBimeta.Messaging`, o que permite introduzir um broker depois **sem alterar os módulos de negócio**. O Inbox do webhook permanece no PostgreSQL.
6. **Ordem e paralelismo.** Processamento **sequencial por lane** (`ServiceSessionId` no turno; canal e contato na ingestão; conta e destinatário no envio) e **paralelo entre lanes**.
7. **Tempo.** O `provider_timestamp`, o `received_at` e o `ingest_seq` são preservados. Eventos atrasados e fora de ordem têm tratamento explícito e nunca são descartados por atraso. Status nunca regride.
8. **Efeitos externos.** Lease e `StateVersion` não bastam contra efeitos duplicados. Toda operação externa com efeito colateral declara **idempotência comprovada** ou **reconciliação** (intenção gravada antes da chamada). Sem nenhuma das duas, não há reenvio automático.
9. **Envio ambíguo.** Não é reenviado automaticamente sem confirmação segura ou mecanismo de idempotência comprovado. O estado é `SendUnknown`, visível na administração e ao atendente.
10. **Falha permanente de IA.** O `Agents` solicita a transferência automática para atendimento humano, **configurável por tenant** (`Handoff`, padrão, ou `AlertOnly`). Sem atendente disponível, o atendimento fica **pendente na fila** (`HumanAssisted` sem responsável), com o histórico preservado.
11. **Validade operacional e janela do WhatsApp.** O `expires_at` é a validade operacional da mensagem, definida pela nossa política, e **não** é a janela de atendimento de 24 horas, que é regra do provedor. Com a janela fechada, avalia-se o uso de templates aprovados conforme as políticas do provedor (avaliação da etapa 5; nenhuma decisão agora).
12. **Limites por tenant** desde o primeiro processamento concorrente. O **escalonamento justo avançado** só é construído se testes demonstrarem a necessidade.
13. **Etapas.** O `IaBimeta.Worker` e o `IaBimeta.Messaging` nascem na **etapa 4**, com o primeiro processamento assíncrono durável, e o `Messaging` é implementado **progressivamente**, sem antecipar funcionalidade sem necessidade comprovada.
14. **Parâmetros do provedor** (vazão, códigos de erro, assinatura de webhook, regras de retentativa) são **configuração sujeita a confirmação na documentação oficial** antes da implementação do `Channels`.
15. **Observabilidade** com `System.Diagnostics.Metrics` e `ActivitySource`, **sem pacote novo**; consumo por tenant em tabela agregada, e não como rótulo de métrica.

## Alternativas descartadas

- Processar no próprio request do webhook.
- Broker desde o início (adiado até necessidade demonstrada).
- Garantir a ordem só com `SKIP LOCKED` nos eventos, sem lane.
- Reenvio automático em timeout.
- Escalonador justo avançado antecipado.
- Rótulo de tenant em métricas.
- Uma única tabela de fila central, com dados de vários módulos.
- Confundir `expires_at` com a janela de 24 horas.

## Consequências

- **Amplificação de escrita:** cada mensagem trocada percorre vários saltos duráveis (Inbox, ingestão, outbox, inbox do consumidor, turno, resposta, fila de envio, status). O custo será medido nos testes de calibração.
- **O `Messaging` é o maior bloco de infraestrutura** do projeto até aqui; o crescimento progressivo e o critério de necessidade comprovada o limitam.
- **ADR 0003, item 5:** o texto permanece "etapa 5" nesse documento; este ADR declara a alteração para a etapa 4.
- **BOUNDARIES.md v0.3:** seção 12, regra DEP-6, regras ARQ-19, ARQ-20 e ARQ-21, e novo envelope de evento.
- **`ARCHITECTURE.md` v0.5 e `CLAUDE.md`:** referências ao ADR e novas regras.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Efeito externo duplicado por falha entre a chamada e a confirmação | Intenção gravada antes da chamada; sem reenvio automático em resultado ambíguo; `Unknown` visível. |
| Bloqueio de uma lane por evento veneno | Teto de tentativas, `dead` com motivo e continuação da lane. |
| Perda de mensagem se o Receiver ficar indisponível além da janela de reenvio do provedor | Alerta crítico de taxa de erro do webhook; disponibilidade do Receiver como requisito. |
| Parâmetros do provedor mudarem ou estarem errados | Parâmetros em configuração; confirmação documental como pré-requisito do `Channels`. |
| Inchaço das tabelas de fila | Purga por política e ajuste de autovacuum; teste de resistência. |
| Antecipar funcionalidade do `Messaging` sem necessidade | Revisão rejeita funcionalidade sem teste que demonstre a necessidade. |

## Critérios de aceitação

1. Assinatura inválida não grava; o mesmo evento repetido gera uma linha; o HTTP 200 só é enviado depois do commit (teste com falha injetada no commit).
2. O `Channels.Presentation` não referencia outro módulo (ARQ-19).
3. Sob concorrência, a ordem por lane é estrita e há no máximo uma lease ativa por lane.
4. Matar o worker no meio de um turno resulta em uma única resposta efetiva.
5. Com estratégia de idempotência ou de reconciliação, nenhum efeito externo se duplica; sem ela, nenhum reenvio automático acontece e o resultado ambíguo vira `SendUnknown`.
6. Falha permanente de IA gera handoff, ou atendimento pendente sem atendente, com o histórico preservado; a política `AlertOnly` é respeitada por tenant.
7. Evento atrasado entra no histórico pelo `provider_timestamp`; status nunca regride.
8. O teto de concorrência por tenant nunca é excedido em carga concorrente.
9. Não existe implementação do `Channels` sem a confirmação dos parâmetros do provedor registrada, com data e fonte.
10. O `Messaging` não contém funcionalidade sem teste que demonstre a necessidade.

## Pendências

- Confirmação, na documentação oficial da Meta, dos parâmetros listados na seção 12.11 do `BOUNDARIES.md`, antes da implementação do `Channels`.
- Política de uso de templates aprovados com a janela de 24 horas fechada.
- Retenção do Inbox, da fila de envio e do payload bruto (LGPD).
- Metas de latência e limiares para considerar um broker, a partir dos testes de carga.
- ADR do `Channels` (etapa 5), com a estratégia de idempotência ou reconciliação do envio.

## Revisão

Reavaliar quando a medição indicar que o PostgreSQL é gargalo da fila, quando houver mais de uma instância do Worker com limites de IA ou de conectores, quando o provedor confirmar idempotência no envio, ou se o custo da amplificação de escrita for inaceitável.
