# Etapa 04 — Adaptador conversacional do Ollama

Status: **concluída tecnicamente (27/11/2026), validada somente com simulador**. Revisão independente do Codex: **aprovado com ressalvas não impeditivas** (sem P0/P1); a revisão foi **estática** (o Codex não executou testes por conta própria). Correções pontuais autorizadas (P2-01 contexto, P2-02 raciocínio, concorrência, documentação) feitas e descritas abaixo. Sem migration, sem instalar o Ollama, sem baixar modelo. **Encerramento técnico não é autorização para produção:** a validação real do Ollama é requisito obrigatório da Etapa 13 (seção 13). A decisão de produto (IA local como opção ao lado das comerciais) vem do ADR 0017; esta etapa só a implementa, reaproveitando AiGateway, Agents, catálogo, ledger e suspensão.

> **O que isto NÃO prova.** Os testes e a demonstração usam servidores HTTP simulados que seguem o formato lido na documentação oficial. Eles não comprovam compatibilidade com o Ollama real, nem qualidade em português, nem latência e memória neste computador. A validação real (seção 12) é um passo à parte, que exige instalar o Ollama e baixar um modelo, com autorização específica.

## 1. Arquitetura

```
Mensagem → Agents → AiGateway → [catálogo + seleção + limites + ledger] → OllamaChatProvider → POST {base}/api/chat
                                   (sem resolver credencial)             (HttpClient direto, sem SDK, sem streaming)
```

- **Mesmo projeto** `IaBimeta.Adapters.Ai.Ollama` (nenhum projeto novo). Ele já tinha `OllamaProvider` (embeddings e geração local do Knowledge, ADR 0034). Foram acrescentados, **sem tocar nesse**: `OllamaChatProvider` (`IAiProvider`, `ProviderId = "ollama"`), `OllamaChatOptions` (seção `AiGateway:Providers:Ollama`), `OllamaChatRequestFactory`, `OllamaChatResponseReader` e `OllamaServerProbe` (sondagem somente leitura). Os dois registros são independentes (`AddOllamaProvider` × `AddOllamaChatProvider`), com configuração separada e sem estado compartilhado.
- O projeto passou a referenciar também o `AiGateway.Contracts` (DEP-2: um adaptador referencia o `.Contracts` do módulo dono de cada porta que implementa: Knowledge e AiGateway). Só os hosts o referenciam (DEP-3).
- O adaptador não tem estado, não conhece tenant, não tem fallback, não repete chamadas e devolve a falha como **resultado** (só o cancelamento do chamador é exceção), como o da OpenAI e o da Anthropic.

## 2. Componentes reaproveitados

`IAiProvider`, `AiProviderRegistry`, `AiModelSelector` (sem fallback), catálogo `AiGateway:Catalog:Models`, limites e reserva de tokens do AiGateway, ledger (`Confirmed`/`Indeterminate`, `AiUsageCertainty`), `AiErrorPolicy`, retentativas do Agents (`MaxTurnAttempts = 3`, recuo, `retry-after`), regras de suspensão (`ITenantOperationGate`), provisionamento (`provision`), simulador de desenvolvimento (`/simulator/ai/*`). **Não alterados:** `IEmbeddingProvider`, `ILocalGenerationProvider`, `OllamaProvider`, `OllamaOptions` e a seção `Knowledge:Providers:Ollama`.

## 3. Mudança aditiva no contrato (autorizada em 27/11/2026)

O AiGateway recusava toda chamada sem credencial BYOK. Para o Ollama não existir uma "chave fictícia" (vetada), foi acrescentado **um** membro com implementação padrão em `IAiProvider`:

```csharp
bool RequiresCredential => true;   // padrão: OpenAI e Anthropic não mudam
```

- Só `OllamaChatProvider` declara `false`. Então o AiGateway **não resolve credencial** e entrega `AiCredential.None` (string vazia, novo membro estático); o adaptador nunca a lê (teste de arquitetura).
- O serviço de configuração **recusa** `SetCredentialAsync` para provedor que não exige credencial; `GetStatusAsync` devolve `CredentialRequired = false` (novo campo no fim do registro, com padrão `true`) e o provisionamento diz "este provedor não usa chave".
- A exigência dos outros provedores **não foi enfraquecida**: sem credencial continuam falhando fechado, sem chamar o provedor nem ocupar vaga (testes novos no AiGateway e de integração).
- Nenhum ADR novo foi criado: não muda fronteira nem matriz de dependências. Se preferir registrar, é um ADR curto.

## 4. API do Ollama usada e referências oficiais

Lidas em 27/11/2026 (espelho oficial `docs.ollama.com`; as páginas equivalentes do GitHub responderam 503/404 na hora da leitura). Legenda: **[DOC]** documentado; **[NÃO DOC]** a documentação não diz; **[SIM]** reproduzido só no simulador.

| Item | Fonte | Situação |
|---|---|---|
| `POST /api/chat`: `model`, `messages` (system/user/assistant), `stream`, `format` (`"json"` ou esquema), `options` (`num_ctx`, `num_predict`, `temperature`...), `keep_alive`, `think` | https://docs.ollama.com/api/chat | [DOC] |
| Resposta sem streaming: `message.content`, `message.thinking`, `done`, `done_reason`, `prompt_eval_count`, `eval_count`, durações em ns | https://docs.ollama.com/api/chat | [DOC]; valores de `done_reason` além de `stop` e quando as contagens faltam: **[NÃO DOC]** |
| Saída estruturada: `format` com esquema; recomenda também descrever o esquema no prompt; temperatura baixa | https://docs.ollama.com/capabilities/structured-outputs.md | [DOC]; palavras-chave de esquema suportadas e o que ocorre se a saída não bate: **[NÃO DOC]** |
| Contexto padrão: 4096 (FAQ) / 2048 (Modelfile) / por VRAM (página de contexto); `OLLAMA_CONTEXT_LENGTH` ou `options.num_ctx` | https://docs.ollama.com/faq.md , /modelfile.md , /context-length.md | [DOC], **com divergência entre páginas** |
| Prompt maior que `num_ctx` (truncamento silencioso?) | — | **[NÃO DOC]** |
| `num_predict` padrão −1 (infinito) | https://docs.ollama.com/modelfile.md | [DOC] |
| Raciocínio: `think` booleano ou nível; `message.thinking`; `false` num modelo que não consegue desligar usa o nível mais baixo; comportamento de `think` em modelo sem raciocínio | https://docs.ollama.com/capabilities/thinking.md | [DOC]; erro em modelo sem raciocínio: **[NÃO DOC]** |
| `GET /api/tags`, `POST /api/show` (`capabilities`, `model_info.*.context_length`), `GET /api/ps`, `GET /api/version` | https://docs.ollama.com/api/tags.md , /api-reference/show-model-details.md , /api/ps.md , /api-reference/get-version.md | [DOC] |
| Erros: 200/400/404/429/500/502 com `{"error": "..."}`; erro no meio do fluxo mantém 200; `503` com fila cheia (`OLLAMA_MAX_QUEUE`, padrão 512) | https://docs.ollama.com/api/errors.md , /faq.md | [DOC]; corpo exato de 404/500 e de falta de memória: **[NÃO DOC]** |
| Concorrência: `OLLAMA_NUM_PARALLEL`, `OLLAMA_MAX_LOADED_MODELS`, `OLLAMA_MAX_QUEUE`, `keep_alive` (padrão 5 min) | https://docs.ollama.com/faq.md | [DOC] |
| Cancelamento quando o cliente desconecta | — | **[NÃO DOC]** |
| Sem autenticação no servidor local; chave só na nuvem; bind em `127.0.0.1:11434` por padrão (`OLLAMA_HOST`) | https://docs.ollama.com/api/authentication.md , /faq.md | [DOC] |
| Windows 10 22H2 ou mais novo; `OLLAMA_MODELS` e `/DIR` para outro disco | https://docs.ollama.com/windows.md | [DOC]; requisito de AVX/AVX2: **[NÃO DOC]** |

**Não presumido:** que todo modelo instalado conversa, raciocina ou devolve esquema. Cada modelo precisa estar no catálogo e ser sondado.

## 5. Como o adaptador monta e lê a conversa

- **Pedido:** `stream:false`; sistema + histórico (`user`/`assistant`); `model` = o do catálogo do tenant; `options.num_predict` = teto de saída já limitado pelo AiGateway; `options.num_ctx` e `temperature` (padrão 0) do **perfil**; `think` só se o perfil pedir; `keep_alive` só se configurado; `format` = esquema da decisão do Agents (e o esquema também vai no texto do sistema, conforme a recomendação; desligável). Nunca envia ferramentas nem imagens. Substituto UTF-16 sem par vira U+FFFD.
- **Resposta:** só `message.content` é lido; `message.thinking` nunca é lido (teste de arquitetura impede ler `"thinking"`). O raciocínio **marcado** com `<think>` no texto é tratado conforme a subseção "Filtragem de raciocínio" abaixo.
- **Motivos de encerramento:** `stop` → Concluída (vazia → falha `empty-output`, consumo conhecido); `length` → Truncada (o Agents já trata); `load` → falha `model-load-only`; ausente ou novo → `unexpected-done-reason` (não repete); `done` diferente de `true` → `incomplete-response`.
- **Saída estruturada inválida:** o adaptador não valida o JSON; o Agents já o faz e trata como tentativa falha (teste de integração).

### Proteção do contexto (P2-01) — heurística, NÃO prova preservação do histórico

O servidor Ollama pode truncar em silêncio um prompt acima de `num_ctx` (a documentação não diz o contrário nem oferece, que se tenha lido, opção de recusar). Três camadas **proporcionais**, sem tokenizador do modelo e sem nova arquitetura de contexto:

1. **Coerência na partida** (`OllamaCatalogCoherence`, chamada pelo Api e pelo Worker): todo modelo **ativo** do Ollama no catálogo precisa de perfil existente, `ContextWindowTokens` **igual** ao `NumCtx` do perfil e teto de saída menor que a janela; senão o host **não sobe**. Modelos desativados (o de referência) e de outros provedores não são tocados. O catálogo de referência dos hosts é coerente com os perfis padrão (teste).
2. **Orçamento por requisição, ANTES de enviar** (`OllamaChatRequestFactory`): estima os tokens do que o **adaptador** realmente leva (sistema **já com o esquema de saída acrescentado**, histórico, e o esquema de `format` quando ele não está no sistema) como `⌈caracteres ÷ 3,0 × 1,3⌉ + 8 por mensagem + 16`, soma o teto de saída pedido (a reserva da resposta) e **recusa** se passar de `NumCtx` (`context-budget-exceeded`, tipo contexto longo, consumo nenhum, nada enviado). Constantes configuráveis, iguais às do AiGateway (que não enxerga o esquema que o adaptador acrescenta).
3. **Verificação POSTERIOR**: se `prompt_eval_count ≥ num_ctx`, a resposta é descartada (`context-window-exceeded`).

**Limites (não afirmar o contrário):** a estimativa é uma heurística de caracteres, não o tokenizador; ela pode **subestimar** (texto com muitos números, símbolos ou outra língua) e **superestimar**. A contagem posterior também é heurística: um prompt truncado pode devolver `prompt_eval_count` **abaixo** da janela. Nada disso **comprova** que o histórico foi integralmente preservado. O problema **não está resolvido** com evidência real: o comportamento real de truncamento da versão instalada, e uma eventual opção do servidor de **recusar** em vez de truncar, devem ser validados na **Etapa 13**.

### Filtragem de raciocínio (P2-02)

Só o raciocínio **marcado** com `<think>…</think>` no texto é tratado (`OllamaReasoningFilter`; nenhum mecanismo genérico para outros modelos ou formatos). Regras, nesta ordem:

1. Blocos **completos no início** da saída (um ou vários, separados só por espaço, sem diferenciar maiúsculas) são descartados.
2. Nada sobra → saída **só de raciocínio** (`reasoning-only`).
3. Sobra um **JSON** (objeto ou lista) → **não é reescrito**: passa sem alteração, **exceto** se algum texto dele **começa** com marcador de abertura ou **contém** marcador de fechamento (raciocínio no campo de resposta ao cliente) → inutilizável (`reasoning-leak`). Um marcador de abertura no **meio** de um texto de JSON é tratado como conteúdo literal legítimo.
4. Sobra texto comum com **qualquer** marcador (abertura sem fechamento, fechamento isolado, bloco no meio ou no fim, bloco cortado por `length`) → inutilizável (`reasoning-leak`). O texto **não é consertado**.

Saída inutilizável = falha de pedido inválido (**permanente**): não é repetida, o cliente **não** recebe o texto, o Agents transfere ao atendente (tratamento já existente) e os **tokens informados pelo servidor continuam no ledger** (`Reported`; teste de integração). **Limites:** não detecta raciocínio sem marcador `think` nem com outro marcador; o literal legítimo com marcador de abertura no meio de um texto JSON passa (risco aceito, conservador no resto); um texto comum que cite a palavra de marcador de propósito é recusado.

## 6. Configuração e segurança do endereço

Seção `AiGateway:Providers:Ollama` (infraestrutura; **nenhum** tenant, usuário nem modelo de IA informa URL):

| Chave | Padrão | Observação |
|---|---|---|
| `BaseAddress` | `http://127.0.0.1:11434/` | validado na partida |
| `BearerToken` | — | só se houver proxy autenticado; segredo do ambiente; vai por requisição, nunca em cabeçalho padrão |
| `KeepAlive` | — (padrão do servidor: 5 min) | `-1`, `0` ou `5m`, `1h`… |
| `GroundStructuredOutputInPrompt` | `true` | acrescenta o esquema ao sistema |
| `MaxResponseBytes`, `ConnectTimeout`, `PooledConnectionLifetime` | 1 MiB, 5 s, 2 min | |
| `Profiles:<nome>` | `atendimento-local` (`NumCtx` 8192, temp. 0, `think` não enviado) e `atendimento-local-sem-raciocinio` (idem com `think:false`, para qwen3 e outros com raciocínio) | `NumCtx` entre 512 e 1.048.576 |

Regras de endereço (falham na **partida**): URL absoluta, sem usuário, consulta nem fragmento; **HTTP só** para laço local (`localhost`, `127.0.0.0/8`, `::1`), rede privada (10/8, 172.16/12, 192.168/16, 100.64/10, link-local, `fc00::/7`) ou nome de **um rótulo só** (serviço interno, como `ollama` numa rede de contêineres); **qualquer outro destino exige HTTPS**, com validação de certificado intacta (teste de arquitetura proíbe desligá-la). O cliente **não segue redirecionamentos**. Interpretação por URI, nunca por prefixo de texto (`localhost.evil.com`, `127.0.0.1.evil.com` e `usuario@host` são recusados). Limite honesto: um nome com ponto exige HTTPS justamente porque a configuração não consegue provar para onde ele resolve; um nome de um rótulo só é confiado a rede interna sob controle da infraestrutura.

Três cenários, mesmo adaptador, só a configuração muda:

1. **Neste computador:** `http://127.0.0.1:11434/` (o Ollama escuta só em loopback por padrão).
2. **No mesmo servidor do ia.bimeta (VM/Compose):** `http://ollama:11434/` na rede interna do Compose, **sem publicar a porta** no host.
3. **Servidor de IA dedicado:** `https://…` (proxy com TLS e token em `BearerToken`) ou IP privado em rede fechada. **Nunca** expor a porta 11434 à internet: o Ollama local não tem autenticação.

Não há SSRF por tenant porque o endereço não é dado do tenant. Nenhuma infraestrutura nova de produção foi criada.

**A segurança do endpoint depende da fronteira de confiança da INFRAESTRUTURA, não do código.** A validação por URI só barra o óbvio. **Um nome de host de um rótulo só não prova que o destino seja privado**, e endereços **link-local** e **100.64/10** (CGNAT) **não garantem uma rede exclusiva da instalação** (provedores e VPNs também os usam). A homologação (Etapa 13) deve verificar: DNS controlado; destinos autorizados; segmentação de rede; proteção da comunicação entre servidores (TLS ou rede fechada); ausência de exposição pública da porta 11434; e a separação entre simuladores e produção (nenhum endpoint simulado, `127.0.0.1:5201` ou credencial sintética no ambiente comercial). Nenhuma reformulação abrangente da infraestrutura foi feita.

## 7. Catálogo, modelos e capacidades

- O catálogo (`AiGateway:Catalog:Models`) traz `ollama/qwen3:4b` **DESATIVADO** (`Status: Disabled`, preços 0, contexto 8192, saída 1024, perfil `atendimento-local-sem-raciocinio`) como modelo de referência: **nada fica disponível de fábrica** (teste de arquitetura). Ativar é decisão da infraestrutura (`Status: Active`).
- Níveis que o produto distingue: **implementado** (adaptador registrado: `AiProviderRegistry`), **configurado** (modelo ativo no catálogo e escolhido pelo tenant), **acessível** e **instalado/compatível** (`OllamaServerProbe`: `GET /api/version`, `GET /api/tags` e `POST /api/show`; compatível = instalado e com `completion`; informa `thinking` e a janela nativa). A sondagem **só lê**: nunca baixa, cria, apaga nem escolhe modelo.
- O **Worker** sonda na partida (3 s, falha não impede a partida) e registra, para cada modelo **ativo** do Ollama, se está instalado e compatível (sem imprimir o endereço). O script `tools\dev\Test-ServidorOllama.ps1` faz a mesma sondagem pela linha de comando.
- **A sondagem é DIAGNÓSTICA.** Ela só informa (log e script): **não altera** o catálogo, a seleção de modelos, a disponibilidade operacional nem a configuração do tenant, e não bloqueia nem libera nada dinamicamente. Um modelo pode estar cadastrado e habilitado e o servidor, indisponível; o atendimento então falha de forma transitória e transfere ao atendente.
- **Seis estados que não se confundem** (a interface da **Etapa 05** deve exibi-los separados, sem esta etapa implementar a interface nem bloqueio dinâmico):

| Estado | Quem sabe | Como se conhece hoje |
|---|---|---|
| Modelo **cadastrado** | catálogo (configuração) | entrada em `AiGateway:Catalog:Models` |
| Modelo **habilitado** | catálogo + tenant | `Status: Active` e escolhido pelo tenant |
| Servidor **acessível** | servidor | sondagem (`/api/version`) — **instantâneo, pode mudar** |
| Modelo **instalado** | servidor | sondagem (`/api/tags`) |
| **Capacidade conversacional declarada** | servidor | sondagem (`/api/show`: `completion`) — é o que o modelo **declara** |
| **Compatibilidade funcional validada** | ninguém ainda | **só a validação real (Etapa 13)**: respostas, esquema, contexto, contagens |
- Um modelo escolhido que não está instalado **não é baixado nem trocado**: o servidor responde 404 → falha `model-not-installed`, permanente, sem custo, e o cliente vai ao atendente.
- A interface de configuração dos provedores é a Etapa 05.

## 8. Consumo e custo

- Entrada = `prompt_eval_count`; saída = `eval_count` (documentados). `prompt_eval_cached_count` (existe no esquema da resposta) **não é usado** (semântica não confirmada): se o servidor o separar da entrada, a entrada reportada pode subestimar o prompt total: ponto da validação real.
- **Consumo não informado** (falta uma das duas contagens): a resposta é **descartada** e a chamada vira falha transitória de consumo **desconhecido** (`missing-usage`): o ledger grava `Indeterminate`/`Unknown` e cobra a **reserva** na janela do tenant, nunca zero nem número inventado. Risco a observar na validação real: se o Ollama omitir `prompt_eval_count` com prompt em cache, isso vira falha em vez de resposta.
- **A ausência de contagem pode descartar uma resposta VÁLIDA**, e as **retentativas** (até 3 por turno) geram **processamento adicional** no servidor, sem cobrança por token mas com custo operacional.
- **`CachedInputTokens = 0` não comprova ausência de cache**: a métrica de cache do Ollama não está integrada (o adaptador sempre informa 0); o valor é "não medido", não "sem cache".
- **Custo estimado = 0** (preço 0 no catálogo): significa **ausência de tarifa comercial por token**, **não** ausência de custo operacional. Custo de infraestrutura própria (energia, hardware, servidor) **não é calculado** (fora do escopo). A validação real das contagens, do cache e das retentativas fica na Etapa 13; a evolução financeira estrutural, na Etapa 12. O ledger **não** diferencia "sem tarifa" de "desconhecido" pelo valor monetário, e sim pelo par `state`/`certainty` (limitação conhecida; evolução na Etapa 12).
- Sem `request-id` no Ollama: `providerRequestId` fica vazio.

## 9. Falhas

| Situação | Código | Tipo | Consumo |
|---|---|---|---|
| Servidor fora do ar / rede | `server-unavailable` | transitória | desconhecido |
| Tempo esgotado | `timeout` | transitória | desconhecido |
| Modelo não instalado (404) | `model-not-installed` | pedido inválido | nenhum |
| Modelo não suporta o recurso pedido (400) | `model-incompatible` | pedido inválido | nenhum |
| 429 / 503 (fila cheia) | `server-busy` | transitória (respeita `retry-after`) | desconhecido |
| 500 por memória | `insufficient-memory` | transitória | desconhecido |
| Outros 5xx | `http-5xx` | transitória | desconhecido |
| 401/403 (proxy) | `unauthorized` | credencial inválida | nenhum |
| Resposta inválida/incompleta/`error` com 200 | `invalid-response`, `incomplete-response`, `provider-error` | transitória | desconhecido |
| Consumo ausente | `missing-usage` | transitória | desconhecido |
| Prompt na janela inteira | `context-window-exceeded` | contexto longo | informado |
| Cancelamento do chamador | exceção | — | desconhecido (o AiGateway decide) |

Nenhuma retentativa interna: valem o AiGateway e o Agents (3 tentativas por turno, recuo, depois transferência ao atendente). Nenhum texto do servidor é repetido (a mensagem de erro só classifica). Sem fallback para OpenAI ou Anthropic (testes).

## 10. Concorrência, suspensão e limites de capacidade

- Isolamento: o adaptador não guarda estado; 40 chamadas simultâneas de "tenants" diferentes não misturam sistema, histórico nem resposta. O teste agora **comprova a sobreposição**: uma barreira só libera as respostas quando as 40 requisições estão ao mesmo tempo dentro do servidor simulado, e o pico simultâneo medido é afirmado (= 40). (O teste anterior não forçava sobreposição.) Continua sendo teste de unidade do adaptador com servidor simulado: não prova concorrência no Ollama real. O servidor Ollama é **compartilhado** por todos os tenants que o usam, mas cada pedido leva só o contexto do próprio tenant.
- **Capacidade:** o Ollama atende por padrão **1 pedido por modelo de cada vez** (`OLLAMA_NUM_PARALLEL`) e **enfileira** o resto (`OLLAMA_MAX_QUEUE` 512, depois 503). Em CPU, cada resposta ocupa o servidor por segundos a dezenas de segundos; com vários tenants a fila cresce e o **tempo limite da chamada** (padrão do AiGateway 60 s) passa a ser a defesa: o pedido que estoura vira falha transitória de consumo desconhecido (a geração pode continuar no servidor). Não foi introduzida fila distribuída nem escalonador. Para a homologação: medir latência por tamanho de prompt, concorrência real suportada e o limite de chamadas simultâneas por tenant (`MaxConcurrentCalls`) adequado ao hardware.
- **Suspensão/reativação** (Etapa 02): empresa suspensa não chama o Ollama nem grava ledger; a reativação **não** reprocessa mensagens antigas; só a mensagem nova é atendida (teste de integração).

## 11. Testes


Servidores HTTP simulados, nenhum modelo real. **Resultados do implementador**; a revisão do Codex foi estática e não executou testes por conta própria.

| Suíte | Resultado |
|---|---|
| Ollama (22 existentes + conversacional, incl. sondagem, endereços, perfis, contexto e raciocínio) | 103 aprovados (após as correções) |
| Ponta a ponta do Ollama (`AgentsOllamaEndToEndTests`) | 10 aprovados (após as correções) |
| Arquitetura (85 + 7 do adaptador conversacional, agora cobrindo também `OllamaReasoningFilter`) | 92 aprovados (após as correções) |
| Integração: Provision, DevelopmentAi, AiGateway, SenderSuspensao, AgentsAnthropic, Retenção (reexecutada após as correções, por causa da alteração dos hosts) | 143 aprovados |
| AiGateway unitário (193 + 4 do provedor sem credencial) | 197 (antes das correções; o AiGateway não foi alterado depois) |
| Integração direcionada antes das correções (Agents, AiGateway, Tenancy, Provision, DevelopmentAi, Knowledge, Retenção) | 294 aprovados, 15 ignorados |
| OpenAI / Anthropic / Agents unitário / Knowledge unitário | 89 / 85 / 908 / 24 aprovados (antes das correções; esses projetos não foram alterados depois) |

**Os 15 testes ignorados não validam o adaptador conversacional.** São 12 testes de **pgvector**, que exigem `IABIMETA_TEST_PGVECTOR_CONNECTION`, e 3 testes de **embeddings do Ollama real**, que exigem `IABIMETA_TEST_OLLAMA_URL`. Nenhum deles toca o `OllamaChatProvider`: a compatibilidade real do adaptador conversacional **não foi provada** (Etapa 13).

Ponta a ponta (`AgentsOllamaEndToEndTests`): fluxo completo com ledger (provedor, modelo, 140/26 tokens, custo 0, sem credencial); recusa de credencial para o Ollama e exigência mantida na OpenAI; duas empresas no mesmo servidor sem mistura; servidor indisponível (indeterminado, nova tentativa, sem fallback); modelo não instalado (permanente, sem `/api/pull`); consumo não informado; saída estruturada inválida; raciocínio nunca ao cliente (campo separado); **raciocínio vazado no campo de resposta** (permanente, só o aviso de transferência vai ao cliente, tokens 130/44 preservados); suspensão e reativação.

Testes das correções: **contexto** (recusa acima do orçamento, reserva da resposta, esquema acrescentado ao sistema e esquema de `format`, coerência catálogo × perfil, catálogo de referência dos hosts, estimativa inválida); **raciocínio** (campo `message.thinking`, bloco completo, múltiplos blocos, marcador de abertura sem fechamento, fechamento isolado, bloco no meio e no fim, truncamento por `length`, saída só de raciocínio, JSON com literal legítimo, bloco antes de JSON, JSON com raciocínio no campo de resposta, preservação do consumo); **concorrência** (barreira determinística, pico simultâneo = 40).

## 12. Procedimento de validação funcional (com simulador, sem instalar nada)

```powershell
.\tools\dev\Start-OllamaSimulado.ps1        # porta 5201
# Api e Worker com: AiGateway__Providers__Ollama__BaseAddress=http://127.0.0.1:5201/  e  AiGateway__Catalog__Models__3__Status=Active
.\tools\dev\Novo-TenantDeOllamaDeDemonstracao.ps1       # empresa demo-ollama (sem chave)
.\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant demo-ollama -Texto 'Ola, tudo bem?' -Nome Marta -Numero 5511999990701
.\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant demo-ollama -Texto 'Quero falar com um atendente' -Nome Marta -Numero 5511999990701
# GET /simulator/ai/requests  (X-Dev-Tenant: demo-ollama)
```

Resultado obtido em 27/11/2026 (simulador): 2 chamadas, `ollama`/`qwen3:4b`, `Confirmed`, `Reported`, 300 de entrada e 40 de saída cada, custo 0; a 1ª foi respondida pela IA e a 2ª transferida ao atendente; o Worker registrou "modelo qwen3:4b instalado e compatível"; nenhuma requisição chegou à OpenAI nem à Anthropic simuladas.


## 13. Instalação futura e validação real (NÃO executada; requisito OBRIGATÓRIO da Etapa 13 para uso comercial do provedor)

Nenhum Ollama foi instalado e nenhum modelo foi baixado. A validação real do Ollama **e** a da Anthropic (`ETAPA-03-ANTHROPIC.md`) permanecem pendentes na Etapa 13, e a utilização comercial do provedor depende delas. Escopo mínimo da validação do Ollama:

1. **Versão efetiva do Ollama** instalada e **modelo instalado** (passo manual do proprietário, `ollama pull`; nunca feito pelo ia.bimeta). Windows 10 22H2 ou mais novo, sem administrador, ≥ 4 GB para o binário; `OLLAMA_MODELS` e `/DIR` movem os modelos de disco.
2. **Licença de utilização comercial** do modelo escolhido.
3. **Qualidade das respostas em português** e **saída estruturada** (resposta, continuidade de contexto, transferência).
4. **Contexto e truncamento**: o que a versão instalada faz com prompt acima da janela, se há opção de recusar em vez de truncar, e a precisão da estimativa do adaptador (seção 5) contra `prompt_eval_count` real.
5. **Raciocínio**: marcadores realmente produzidos pelo modelo e efeito de `think`; a filtragem tem limites (seção 5).
6. **Contagens de tokens** (inclusive com prompt em cache e ausência de `prompt_eval_count`), **cache** e o efeito das retentativas.
7. **Timeout e cancelamento efetivo** (o servidor continua gerando após o cliente desistir?).
8. **Concorrência, capacidade de processamento, consumo de memória e latência** (`ollama ps`), com o limite de chamadas simultâneas por tenant adequado ao hardware.
9. **Segurança da comunicação** e **separação entre ambientes simulados e produção** (seção 6): DNS controlado, destinos autorizados, segmentação, TLS ou rede fechada, nenhuma exposição pública da porta, nenhum endpoint simulado nem credencial sintética em produção.
10. Confirmar os pontos **[NÃO DOC]** da seção 4.

Passos: ajustar a infraestrutura (`AiGateway:Providers:Ollama:BaseAddress`; ativar o modelo no catálogo com perfil de `NumCtx` **igual** ao `ContextWindowTokens`; `Think:false` para modelo com raciocínio); `Test-ServidorOllama.ps1`; reiniciar Api e Worker (a coerência é conferida na partida); criar a empresa de teste com `Novo-TenantDeOllamaDeDemonstracao.ps1` (com o endereço real) e medir.

## 14. Limitações e pendências

1. Não validado contra o Ollama real (formato lido na documentação). Sem a validação da Etapa 13 o provedor não deve ser usado comercialmente.
2. **Contexto:** a proteção é heurística (seção 5); **não** comprova preservação integral do histórico e o problema não está resolvido com evidência real.
3. **Raciocínio:** só o marcado com `think` é filtrado; raciocínio sem marcador não é detectável.
4. **Contagens e cache:** comportamento quando faltam e `prompt_eval_cached_count` não confirmados; ausência pode descartar resposta válida; cache não medido (0 ≠ sem cache).
5. `think` em modelo sem raciocínio pode ser recusado pelo servidor (a documentação não diz): por isso o perfil padrão não o envia.
6. Sem streaming, sem ferramentas, sem imagens (contrato `IAiProvider` só de texto).
7. Capacidade e concorrência de um servidor compartilhado dependem do hardware (seção 10); o teste de concorrência é de unidade, com servidor simulado.
8. O Ollama não informa identificador de requisição; o ledger não tem `providerRequestId`.
9. Custo de infraestrutura e distinção financeira entre "sem tarifa" e "desconhecido": **Etapa 12**.
10. Sondagem diagnóstica e os seis estados (seção 7): a **Etapa 05** (interface de configuração dos provedores) deve considerá-los; nenhum bloqueio dinâmico foi implementado. Ollama Cloud (com chave) e downloads automáticos de modelo: fora do escopo.
11. Endpoints: a segurança depende da fronteira de confiança da infraestrutura (seção 6).
12. Antes da operação comercial: licença do modelo escolhido, política de dados da hospedagem do servidor de IA e demais pendências jurídicas já registradas.
