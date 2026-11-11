# ADR 0034 — Knowledge Engine híbrido: índice semântico com pgvector, provedores locais desacoplados e Ollama como primeiro executor

- **Status:** **Proposto** (decisão de produto do proprietário em 11/11/2026: o Knowledge Engine híbrido é obrigatório na V1; arquitetura definitiva com pgvector, sem armazenamento vetorial provisório). Ratificação pendente, depois da homologação.
- **Data:** 11/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md` (módulos, camadas, matriz), 0017 (Agents), 0029 (FAQ determinístico, AutoReply, medição), 0030 (lacunas do FAQ, aprendizado supervisionado), 0033 (implantação em VM Linux), `docs/operations/CONHECIMENTO-HIBRIDO.md`.
- **Preserva:** a FAQ determinística (igualdade normalizada, aliases, AutoReply opt-in, idioma) **antes** de qualquer busca semântica; a revisão, a publicação e a versionamento do ADR 0030; o AiGateway como fallback comercial; Channels, ContactCenter, Conversations, Sender, outbox, idempotência e transferência para humano; o isolamento por tenant; a regra de que a IA só **propõe** e o humano publica. Nenhuma aresta nova na matriz (`Knowledge: [Identity, Tenancy, Audit]` e `Agents → Knowledge` já constam).

## Contexto

A FAQ determinística (ADR 0029) responde só quando o texto do cliente é, depois de normalizado, igual a uma pergunta ou alias aprovado. Perguntas parafraseadas, com sinônimos ou erros de digitação seguem para o modelo comercial, que custa e demora. O proprietário decidiu implementar um motor **híbrido**: FAQ determinística → busca semântica com embeddings locais → modelo comercial → atendente humano, com aprendizado supervisionado e métricas de economia por tenant. A prioridade é **não responder errado**: a similaridade nunca é, sozinha, garantia de correção.

## Decisão

### 1. Módulo `Knowledge`, provedores desacoplados

Novo módulo (`Contracts`, `Core`, `Infrastructure`; esquema `knowledge`, `KnowledgeDbContext` e migrations próprios). Nenhuma lógica de negócio conhece o Ollama:

- **Portas** em `Knowledge.Contracts`: `IKnowledgeIndexing` (sincronizar, limpar, estado), `IKnowledgeSearch` (buscar no tenant do contexto), `IEmbeddingProvider` e `ILocalGenerationProvider`.
- **Adaptador** `IaBimeta.Adapters.Ai.Ollama` (só referencia `Knowledge.Contracts`, regra DEP-2): `/api/embed` e `/api/chat` (saída JSON estruturada, `stream:false`, temperatura 0). Trocar de executor ou de modelo é trocar o adaptador ou a configuração; o ContactCenter, o Conversations e o Sender não mudam. O serviço de inferência é só um endereço (`Knowledge:Providers:Ollama:BaseUrl`): pode estar na mesma pilha, em outra VM ou outro contêiner, com **token de portador** opcional e proxy autenticado (`deploy/ollama/`).
- **Falha nunca atrapalha o atendimento**: serviço fora do ar, tempo esgotado, resposta inválida ou dimensão errada viram `Unavailable` com um **código curto fixo** (`timeout`, `unreachable`, `unauthorized`, `http-NNN`, `invalid-response`, `dimension-mismatch`, `not-configured`, `index-not-ready`, `store-failed`), nunca o texto do cliente nem o corpo da resposta; quem chama segue os fluxos existentes.
- **Modelo inicial:** `qwen3-embedding:0.6b`, dimensão **1024**. A licença (Apache-2.0, a confirmar na homologação) e a qualidade em pt-BR **ainda não foram validadas em hardware real**; ver `docs/operations/CONHECIMENTO-HIBRIDO.md`. Sem GPU na V1.

### 2. PostgreSQL com pgvector (definitivo)

Tabela `knowledge.semantic_texts`, uma linha por texto aprovado (pergunta ou alias), chave `(tenant_id, entry_ordinal, kind, alias_ordinal)`, com `definition_number` (a busca só vale contra a versão publicada vigente), idioma, elegibilidade de AutoReply, o texto aprovado, `text_hash` (SHA-256 de `modelo|dimensão|texto`), estado (`Pending`, `Ready`, `Failed`), `embedding vector(1024)`, tentativas, código do último erro, próxima tentativa e reserva (lease). `CHECK`: pronto ⇔ tem vetor. **Só conhecimento aprovado é indexado**: nunca texto de cliente (a consulta é vetorizada em memória e descartada), logo sem dado pessoal no índice.

- **Busca exata** (`ORDER BY embedding <=> @q LIMIT n`, cosseno) **dentro do tenant** (no máximo ~550 textos por tenant: 50 entradas × 11). Sem índice aproximado (HNSW/IVFFlat): com filtro por tenant eles perdem resultado e não há ganho nesta escala; reavaliar com ADR se algum tenant passar de milhares de textos.
- **Isolamento:** todo comando que toca dado de tenant filtra por `tenant_id` do `RequestContext`; o tenant nunca vem de parâmetro. A única visão de sistema é a fila de vetorização (devolve só texto aprovado a vetorizar).
- **Migration** cria a extensão `vector`. Exige PostgreSQL com pgvector (a imagem `pgvector/pgvector:pg17` do Compose; o `init` cria a extensão como superusuário, pois o papel da aplicação não é superusuário). Ela só entra no `migrate`/`provision` quando `Knowledge:Semantic:Enabled` é verdadeiro, para que um PostgreSQL sem pgvector (o do Windows de desenvolvimento) continue migrando os demais módulos.
- A dimensão é a da coluna: `Dimension ≠ 1024` é recusado na partida; trocar de dimensão exige migration e ADR. Trocar de **modelo** (mesma dimensão) é só configuração: os textos voltam à fila e o modelo antigo nunca é consultado.

### 3. Indexação idempotente e recuperável

- **Publicar** (`AgentDefinitionService.PublishAsync`) só **grava** o que deve ser vetorizado, numa transação, por tenant (trava consultiva): texto igual (mesmo hash, modelo e dimensão) reaproveita o vetor; texto novo ou de outro modelo fica `Pending`; o que saiu da publicação é removido. Falha do índice **não** falha nem desfaz a publicação (melhor esforço; o índice é derivado e se reconstrói). Rascunho nunca é indexado.
- **Vetorizar** é assíncrono, no Worker (`SemanticIndexer`): reserva um lote (`FOR UPDATE SKIP LOCKED` + lease; se o Worker cair, a reserva vence e outro retoma), pede os vetores em lote, grava só se o texto continua o mesmo (o hash confere). Falha: nova tentativa com espera exponencial (30 s, dobrando, até 10 min) até `MaxAttempts`; depois fica `Failed` e a próxima publicação o recoloca na fila.
- **Interruptor global:** `Knowledge:Semantic:Enabled`, desligado por padrão; desligado, nada é indexado, vetorizado nem consultado e o banco do módulo não é tocado.

### 4. Resposta direta por similaridade (Pacote 2)

Ordem do turno: **FAQ determinística → busca semântica → AiGateway → atendente humano**. A busca semântica só roda quando a FAQ determinística **não achou nada igual** (`NoMatch`); duplicata, entrada sem `AutoReply` ou resposta direta desligada nunca vão a ela. Elegibilidade igual à do FAQ: uma única mensagem de texto pronta, no idioma do FAQ.

- **Três chaves independentes, todas desligadas por padrão:** `Agents:Options:SemanticAnswerEnabled` (global), `Knowledge:Semantic:Enabled` (índice) e o **modo do tenant** (`Off`, `Evaluate`, `Active`; tabela `agents.semantic_answer_settings`, sem linha = `Off`). Qualquer uma desligada devolve o turno ao fluxo anterior sem consultar nada. O modo do tenant é o **kill switch por tenant**: vale no próximo turno, sem publicar nada, auditado (`agents.semantic-settings.mode-off|evaluate|active`, sem conteúdo).
- **Modo de avaliação:** a busca roda e a decisão é registrada (o que **teria** respondido), mas o turno segue ao modelo. É assim que os limiares são calibrados com tráfego real sem risco. Não se ativa `Active` para empresa real antes de a avaliação e a suíte de qualidade (Pacote 4) provarem a precisão.
- **Política pura** (`SemanticAnswerPolicy`), na ordem: índice utilizável e **da mesma versão** da definição publicada (número e conteúdo lidos juntos; versão diferente = `Stale`, nunca responde); melhor candidato com similaridade ≥ limiar do tenant (padrão **0,90**, conservador e **ainda não calibrado** com o modelo real); nenhuma **outra** entrada a menos da margem do tenant (padrão **0,05**; as entradas sem `AutoReply` também competem, a busca não filtra por elegibilidade); a entrada do melhor candidato tem `AutoReply`; salvaguardas determinísticas.
- **A similaridade nunca é a única garantia:** `SemanticGuards` recusa mensagem com mais de uma pergunta, longa demais (> 30 palavras ou 240 caracteres), com **polaridade diferente** do texto aprovado (negação só de um lado: "preciso de jejum" × "não preciso de jejum") ou com **números diferentes** ("exame 2" × "exame 3"). A resposta é **sempre o texto aprovado** da entrada, sem reescrita generativa, pelo mesmo `RegisterAiReply` do FAQ (as cercas do Conversations valem: resposta defasada continua recusada). O texto do cliente só entra na conferência; não altera instruções, políticas nem conhecimento.
- **Falha segura:** erro da busca, da configuração ou do registro da métrica devolve o fluxo normal; o atendimento nunca depende da camada semântica.
- **Métricas por tenant** (`agents.semantic_decisions`, chave `(tenant, turno)`, **só números e códigos, nenhum texto de cliente**): decisões por desfecho, acertos do FAQ e da busca, respostas do modelo, transferências, latência da etapa (média e p95) e "chamadas comerciais evitadas" (soma **exata** de FAQ + busca). Tokens e custo evitados são **estimativa**, só no relatório operacional, com rótulo próprio e o aviso de que o **custo da IA local não está descontado** (infraestrutura própria).
- API administrativa (`TenantSettingsManage`): `GET|PUT /agents/semantic/settings` e `GET /agents/semantic/metrics`.

### 5. O que este ADR ainda não decide

Classificação, agrupamento e sugestões por IA local com revisão humana (Pacote 3); interface administrativa, suíte de qualidade em pt-BR e avaliação de prontidão (Pacote 4). Serão acrescentados a este ADR conforme implementados.

## Validação

- **Feito no Windows de desenvolvimento (sem pgvector, Ollama nem Docker):** testes de unidade do serviço, do indexador e do gancho de publicação (com armazenamento em memória e provedor falso); testes do adaptador contra um serviço HTTP **simulado**; regras de arquitetura e grafo de projetos. Os testes simulados **não equivalem à homologação**.
- **Pendente para a homologação (exige pgvector e/ou Ollama reais):** `tests/IaBimeta.IntegrationTests/Knowledge/PgvectorStoreTests.cs` (11 testes, ignorados sem `IABIMETA_TEST_PGVECTOR_CONNECTION`: migration, ciclo, isolamento entre tenants, reserva concorrente, troca de modelo, restrições do banco); qualidade do Qwen3-Embedding em pt-BR, latência e memória em hardware real; o Compose (`pgvector/pgvector:pg17`, serviço `ollama`, `deploy/ollama/`) **nunca rodou com Docker**.

## Consequências

- Mais um serviço opcional na pilha (IA local) e uma imagem de banco diferente; o modo semântico desligado mantém o comportamento anterior.
- Memória e CPU do serviço de IA local competem com a VM se ficarem na mesma máquina: por isso o serviço é um endereço, e o Compose o põe atrás de um perfil opcional com limites.
- A busca exata torna a latência previsível e elimina erro de recall do índice, ao custo de não escalar para milhares de textos por tenant (fora do escopo da V1).
