# Conhecimento híbrido (ADR 0034): operação, infraestrutura e plano de homologação

Ordem do turno: **FAQ determinística → busca semântica (embeddings locais) → modelo comercial (AiGateway) → atendente humano**. A IA local só **propõe** (sugestões revisadas por pessoa); nada é publicado por ela. Tudo é **desligado por padrão**.

## 1. Chaves e configuração

| Chave | Padrão | Efeito |
|---|---|---|
| `Knowledge:Semantic:Enabled` | `false` | Índice semântico (pgvector): indexa, vetoriza e permite buscar. Desligado, o banco do módulo nem é tocado e a migration do Knowledge não entra no `migrate`. |
| `Agents:Options:SemanticAnswerEnabled` | `false` | Chave global da resposta direta por similaridade. |
| Modo do tenant (`PUT /agents/semantic/settings`) | `Off` | `Off` / `Evaluate` (mede, nunca responde) / `Active`. É o kill switch por empresa (vale no próximo turno). |
| `Agents:Options:LocalSuggestionsEnabled` | `false` | Sugestões da IA local (sob pedido do administrador). |
| `Knowledge:Providers:Ollama:*` | ver abaixo | `BaseUrl`, `BearerToken` (segredo, em `app.env`), `EmbeddingModelId` (`qwen3-embedding:0.6b`), `GenerationModelId` (vazio = sem geração local). |

Limiares do tenant: padrão **0,90** (similaridade) e **0,05** (margem para outra entrada). **Não calibrados com o modelo real**: a calibração é parte da homologação.

## 2. Infraestrutura (Compose)

- `db`: imagem `pgvector/pgvector:pg17` (`DB_IMAGE` no `.env`). A extensão `vector` é criada no `init` do banco (superusuário). **Banco já existente**: `docker compose exec db psql -U postgres -d iabimeta -c "CREATE EXTENSION IF NOT EXISTS vector"` antes de ligar o modo, depois `scripts/cli.sh migrate` (com backup).
- `ollama` (perfil opcional `ia-local`): em `deploy`, execute `docker compose --profile ia-local up -d ollama`, depois `bash scripts/ia-local.sh pull` e `bash scripts/ia-local.sh check` (confere a dimensão 1024 e mede a 1ª chamada). A rede `back` fica sem porta publicada. Comece com `OLLAMA_MEM_LIMIT` (6g) e `OLLAMA_CPUS` (3).
- **Outra máquina**: `deploy/ollama/` (Ollama + Caddy exigindo `Authorization: Bearer`, só os caminhos `/api/embed`, `/api/chat`, `/api/tags`, porta 11435 ligada a um IP **privado**). Na aplicação: `OLLAMA_URL=http://<ip-privado>:11435` e o token em `Knowledge__Providers__Ollama__BearerToken`. Texto puro na rede privada: usar VPN/VLAN; TLS entre máquinas é decisão pendente.
- Recursos (valores **de referência, a medir**): o modelo de embeddings `qwen3-embedding:0.6b` ocupa 639 MB em disco (fonte: página do Ollama) e o contexto é de 32K; a página não informa RAM/CPU. Reserve algo na faixa de 1–2 GB de RAM para ele e mais 2–4 GB se houver um modelo generativo pequeno, sem GPU na V1. **A inferência não pode comprometer API, Worker, PostgreSQL e ClamAV**: por isso o limite de memória/CPU do contêiner e a opção de máquina separada. Se a VM não tiver folga, use outra máquina.
- Fatos do modelo (fontes: páginas do Ollama e da Hugging Face): licença Apache-2.0 (HF); dimensão de até 1024, com dimensões menores suportadas (MRL); "100+ idiomas", **português não é citado nominalmente**: a qualidade em pt-BR **precisa ser medida** (seção 4); a consulta aceita instrução (formato `Instruct: ...\nQuery: ...`, instruções em inglês recomendadas; documentos sem instrução).

## 3. Operação

- Publicar o conhecimento só **grava** o que vetorizar; o Worker vetoriza (reserva `SKIP LOCKED`, repetição com espera crescente). Serviço de vetores fora do ar: o atendimento segue pelos fluxos anteriores; o índice só deixa de crescer. Trocar de modelo: os textos voltam à fila, e o modelo antigo nunca é consultado.
- Métricas: `GET /agents/semantic/metrics` (contagens exatas, latência p95) e a área Conhecimento do React (bloco "busca semântica e sugestões da IA"). Tokens e custo evitados: **estimativa**, no comando `status`/relatório operacional, com o custo da IA local **não descontado**.
- Retenção: decisões semânticas e sugestões já decididas, 90 dias (`Retention:Days:SemanticDecisions|FaqSuggestions`); pendentes esperam a revisão.
- Desligar: modo do tenant `Off` (imediato) → `SemanticAnswerEnabled=false` → `Knowledge:Semantic:Enabled=false`.

## 4. Validações que exigem pgvector e Ollama reais (NÃO feitas no Windows de desenvolvimento)

Nenhum teste simulado equivale a esta homologação.

1. `IABIMETA_TEST_PGVECTOR_CONNECTION=<banco descartável com pgvector>` → `PgvectorStoreTests` (11 testes: migration, ciclo, isolamento entre tenants, reserva concorrente, troca de modelo, CHECKs).
2. `IABIMETA_TEST_OLLAMA_URL=<url>` (+ `IABIMETA_TEST_OLLAMA_TOKEN`, `IABIMETA_TEST_REPORT_DIR`) → `QualidadeSemanticaOllamaTests`: dimensão 1024, vetores repetíveis, **zero falsos positivos** no limiar padrão no conjunto pt-BR (`tests/quality/dataset-ptbr.json`, ~60 casos: paráfrase, sinônimo, digitação, negação, números, várias perguntas, ambíguas, respostas parecidas diferentes, sem resposta, sem AutoReply, conhecimento desatualizado, manipulação), latência p95 e o relatório `qualidade-semantica-ptbr.md` com a varredura de limiares e o **menor limiar com zero falsos positivos**.
3. Compose real: `pgvector/pgvector:pg17`, serviço `ollama`, `deploy/ollama/`, `aceitacao.sh`, memória e CPU sob carga com API/Worker/ClamAV ativos.
4. Conferir a licença e a versão pinada da imagem do Ollama (`OLLAMA_IMAGE` está em `latest` até a 1ª homologação).

## 5. Plano de homologação com empresas reais

1. **Ambiente de teste**: itens 1–4 acima passam; o relatório de qualidade é anexado ao ADR 0034.
2. **Avaliação (modo `Evaluate`) em uma empresa piloto**, sem responder nada ao cliente, por pelo menos 2 semanas de tráfego: as decisões registradas ("teria respondido") são conferidas por amostragem por um revisor humano; meta: **0 respostas erradas** na amostra e cobertura útil. Ajustar limiar e margem **dessa empresa**.
3. **Ativação gradual** (`Active`) só para entradas com `AutoReply` já aprovado, com acompanhamento diário das métricas e do relatório de lacunas; qualquer resposta errada reportada → `Off` imediato e análise.
4. **Sugestões da IA local**: ligar `LocalSuggestionsEnabled` só depois de a qualidade do modelo generativo ser conferida em pt-BR; começar por duplicatas/contradições e aliases, revisados por pessoa.
5. Critério de saída para a V1: precisão 100% no conjunto e na amostra real, latência p95 aceitável, infraestrutura sem competir com API/Worker/PostgreSQL/ClamAV, e ratificação do ADR 0034 pelo proprietário.

## 6. Independência de canal

O Knowledge, o índice e as sugestões não conhecem canais: servem ao chatbot de texto hoje e a uma IA de voz ou integrações com ERP/legado depois (a entrada é um texto e a saída é o texto aprovado). Nenhuma mudança em Channels, ContactCenter, Conversations, Sender, outbox ou transferência para humano.
