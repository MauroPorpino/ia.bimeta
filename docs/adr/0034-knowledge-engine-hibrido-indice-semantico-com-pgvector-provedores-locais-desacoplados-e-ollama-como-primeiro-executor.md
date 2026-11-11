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

### 4. O que este ADR ainda não decide

Resposta direta por similaridade, limiares e ambiguidade, kill switch por tenant, modo de avaliação, métricas e economia (Pacote 2); classificação, agrupamento e sugestões por IA local com revisão humana (Pacote 3); interface administrativa, suíte de qualidade em pt-BR e avaliação de prontidão (Pacote 4). Serão acrescentados a este ADR conforme implementados.

## Validação

- **Feito no Windows de desenvolvimento (sem pgvector, Ollama nem Docker):** testes de unidade do serviço, do indexador e do gancho de publicação (com armazenamento em memória e provedor falso); testes do adaptador contra um serviço HTTP **simulado**; regras de arquitetura e grafo de projetos. Os testes simulados **não equivalem à homologação**.
- **Pendente para a homologação (exige pgvector e/ou Ollama reais):** `tests/IaBimeta.IntegrationTests/Knowledge/PgvectorStoreTests.cs` (11 testes, ignorados sem `IABIMETA_TEST_PGVECTOR_CONNECTION`: migration, ciclo, isolamento entre tenants, reserva concorrente, troca de modelo, restrições do banco); qualidade do Qwen3-Embedding em pt-BR, latência e memória em hardware real; o Compose (`pgvector/pgvector:pg17`, serviço `ollama`, `deploy/ollama/`) **nunca rodou com Docker**.

## Consequências

- Mais um serviço opcional na pilha (IA local) e uma imagem de banco diferente; o modo semântico desligado mantém o comportamento anterior.
- Memória e CPU do serviço de IA local competem com a VM se ficarem na mesma máquina: por isso o serviço é um endereço, e o Compose o põe atrás de um perfil opcional com limites.
- A busca exata torna a latência previsível e elimina erro de recall do índice, ao custo de não escalar para milhares de textos por tenant (fora do escopo da V1).
