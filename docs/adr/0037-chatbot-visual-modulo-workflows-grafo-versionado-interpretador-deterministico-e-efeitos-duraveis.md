# ADR 0037 — Chatbot Visual: módulo `Workflows`, grafo versionado, interpretador determinístico e efeitos duráveis

- **Status:** **Aceito** (decisões do proprietário em 12/11/2026, sobre a proposta técnica do Chatbot Visual).
- **Data:** 12/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md` (módulo `Workflows` já previsto), 0007 (turnos e cercas), 0035 (Motor de Integração), 0036 (Agents e operações públicas), 0034 (Knowledge), `CLAUDE.md`.
- **Complementa:** ADR 0038 (roteamento, convivência com o Agents e mensagens de fluxo).
- **Preserva:** o comportamento atual (a função nasce **desligada**: `Workflows:Options:Enabled = false`), o contrato do Agents, do ContactCenter e do Capabilities, e a matriz (nenhuma aresta nova).

## Contexto

Hoje o atendimento automatizado é só o Agents (FAQ, IA, transferência). Faltam fluxos **determinísticos e auditáveis** desenhados pela empresa (menus, coleta de dados, consulta a sistema, transferência), independentes de canal.

## Decisão

### 1. Módulo `Workflows` (Contracts, Core, Infrastructure, Presentation)
Ativa o módulo da matriz. Esquema `workflows`, `WorkflowsDbContext` e migrations próprios. Arestas permitidas (inalteradas): Conversations, Capabilities, Identity, Tenancy, Audit. **Não** depende de Agents, Knowledge, AiGateway nem ContactCenter: o que precisa deles passa por **portas** em `Workflows.Contracts`, implementadas pelo Agents (nós `knowledge` e `ai`) e por um adaptador do host (transferência).

### 2. Grafo
Documento JSON `{ schemaVersion, start, nodes[], edges[] }`; o editor visual (React Flow) e o servidor usam **o mesmo contrato**; o servidor valida. Nós da v1: `message`, `question`, `condition`, `knowledge`, `ai`, `integration`, `handoff`, `end`. Cada nó declara portas de saída nomeadas.
- `message`: texto com variáveis `{{nome}}` e opções (escolhas) abstratas; o canal renderiza.
- `question`: tipo esperado (texto, escolha, número, e-mail, telefone, data, padrão restrito), variável de destino, tentativas e prazo; portas `answered`, `invalid`, `timeout`.
- `condition`: **declarativa** (operadores fixos: igual, diferente, contém, vazio, maior, menor, dentro de lista, dentro do horário); **sem script, sem expressão arbitrária**.
- `knowledge`: porta `IFlowKnowledge` (reaproveita o FAQ e o Knowledge existentes; sem outro mecanismo de conhecimento); portas `found`, `not_found`.
- `ai`: na v1 **entrega a conversa ao Agents e o fluxo termina** (`Delegate`). O contrato e o estado reservam a evolução "IA com retorno ao fluxo" (`FlowStatus.AwaitingAgent`, efeito `DelegateAndResume`), **não implementada**.
- `integration`: operação do catálogo do Capabilities por chave; parâmetros ligados a variáveis; `Caller = Workflow`; **só leitura, `DataClass = Public`, autorizada para `Workflow`**, com escopo da organização da sessão (ADR 0036). Portas `success`, `failure`. Escrita e dado do cliente: fora desta etapa.
- `handoff`: transferência por `RequestHandoff` (idempotente), com resumo das variáveis coletadas para o atendente.
- `end`: encerra.

Limites de validação (configuráveis): nós, arestas, tamanho do documento, tamanho de texto, passos por execução, profundidade. Todo ciclo precisa passar por nó que **espera entrada** (`question`) ou por contador; nó inalcançável, porta sem destino, variável não declarada e operação inexistente/não elegível impedem a publicação.

### 3. Interpretador puro e efeitos
`Step(versão, estado, entrada) → (estado', efeitos[])`: função **pura, determinística e independente de canal** (sem relógio, sem I/O, sem aleatoriedade: o instante entra como entrada). Os efeitos são dados: `SendMessage`, `CallCapability`, `LookupKnowledge`, `RequestHandoff`, `Delegate`, `Wait`, `Finish`. Um executor impuro os realiza; o **simulador** usa um executor simulado e **nunca** chama IA, ERP nem canal.

### 4. Estado, concorrência e recuperação
- `flow_instances`: uma instância ativa por sessão (índice único parcial), versão **fixada** no início (a conversa termina na versão em que começou), nó atual, variáveis, `row_version`, prazo de espera, última entrada processada, lease com token.
- Cada transição grava **na mesma transação** o novo estado e os **efeitos pendentes** (`flow_effects`) com identificador determinístico `instância:passo:índice` (chave de idempotência de cada realização); o estado de cada efeito é rastreado (Pending, Done, Failed, Unknown).
- O Worker consome as entradas (inbox por `EventId`, lane por sessão), executa os efeitos pendentes e, por varredura, **retoma** instâncias com lease vencido e efeitos pendentes. Resultado `Unknown` segue a porta de falha e **nunca é reenviado**.
- Prazos de espera por varredura de índice. Limites: instâncias simultâneas por tenant, passos por execução, tentativas por pergunta.

### 5. Variáveis, classificação e proteção
Cada variável tem classe: `Public` (vem de operação `Public` ou do próprio fluxo) ou `Personal` (resposta do cliente ou campo marcado). Variáveis `Personal` são **cifradas em repouso** (AES-256-GCM com chaves próprias `Workflows:Secrets`, tenant e instância como dado autenticado; sem chave, a gravação falha fechada). Nunca vão a log, auditoria, métricas nem resposta administrativa (a API devolve só nomes e classes). O resumo ao atendente passa pelo ContactCenter. Retenção: a técnica por empresa existente apaga instâncias encerradas e passos antigos. Variáveis nunca são enviadas a modelo de IA.

### 6. Versionamento e publicação
`WorkflowDefinition` (chave, nome, organização opcional) e `WorkflowVersion` (Draft, Published, Retired; grafo; hash SHA-256 do grafo normalizado). Publicar valida, **congela** (versão publicada imutável, garantida também por gatilho de banco) e aposenta a anterior numa transação; índice único de uma publicada por definição; concorrência otimista (`changed`). Voltar a uma versão = republicar uma cópia.

### 7. Governança
Permissões `WorkflowRead`, `WorkflowManage` (editar rascunho), `WorkflowPublish` (publicar e ativar), `WorkflowTest` (simulador). Administrador: todas; gerente: leitura e teste. Portão de função (403 antes de qualquer consulta), escopo por organização, tenant só do `RequestContext`. Auditoria `workflows.*` sem valores. Métricas em `Meter IaBimeta.Workflows`. Histórico por passo (`flow_step_log`: nó, desfecho, duração, sem valores).

### 8. Independência de canal
O motor emite conteúdo abstrato (texto e escolhas) e consome entradas normalizadas (texto, escolha por id, tipo de mídia). Quem renderiza degrada (opções sem botão viram lista numerada; o `question` aceita número, rótulo ou id). Limites de botões e janelas do canal são configuração do canal, confirmados na documentação oficial antes de depender deles. Instagram e voz entram depois por perfil de capacidades do canal, sem mudar o interpretador.

### 9. Editor e simulador
React Flow (`@xyflow/react`, MIT, versão fixa, compatível com React 19; conferida antes da instalação). O editor não decide semântica: o JSON do grafo é o contrato e o servidor valida. O simulador roda o **mesmo interpretador** no servidor, sem persistir estado, com limites de taxa e de passos e respostas simuladas por cenário.

## Consequências
- Fluxos determinísticos coexistem com o Agents sem mudar o comportamento atual (desligado por padrão).
- Custo: nova superfície de administração e de runtime; tabelas e varredores novos; cifra de variáveis pessoais.
- Fora de escopo: dado privado de integração, escrita, retorno da IA ao fluxo, voz e Instagram.
