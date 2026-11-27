# Etapa 03 — Adaptador Anthropic

Status: **implementada e validada somente com simulador** (27/11/2026); revisão independente do Codex: *aprovado com ressalvas não impeditivas* (sem P0/P1). Sem migration, sem chamada paga, sem chave real. Retenção: ver seção 16.

> **Situação da validação real (decisão do proprietário, 27/11/2026).**
> - O proprietário **não dispõe de conta de API Anthropic neste momento**.
> - A **validação real foi transferida para a Etapa 13**.
> - **Não existe autorização vigente para executar chamadas pagas** nesta etapa.
> - O **orçamento de US$ 0,10** anteriormente proposto **deverá ser reconfirmado** antes do futuro teste real.
> - Os testes simulados (servidor HTTP simulado, ponta a ponta local) **não comprovam compatibilidade efetiva com a API comercial**: reproduzem o formato lido na documentação.
> - **A utilização comercial da Anthropic depende da validação real.**
> - **Requisito obrigatório da Etapa 13:** verificar a separação entre ambientes demonstrativos e produção; garantir que endpoints simulados não sejam usados inadvertidamente em produção e que credenciais sintéticas não sejam usadas no ambiente comercial; verificar a configuração segura dos endpoints dos provedores; validar TLS e a conectividade com a API oficial. Nenhuma infraestrutura nova de configuração foi criada nesta etapa. A decisão de produto (Anthropic como provedor inicial, `HttpClient` direto, sem SDK, BYOK) já está no **ADR 0017**; esta etapa só a implementa. Nenhum ADR novo foi necessário.

## 1. Arquitetura

```
Agents ──► AiGateway (IAiGateway) ──► AiProviderRegistry ──► AnthropicProvider ──► POST {base}/messages
            │ seleciona pelo tenant        │ por ProviderId       │ credencial BYOK só no cabeçalho
            │ credencial (AES-GCM), ledger │                      │ DAQUELA requisição
            └ suspensão (ITenantOperationGate)
```

- **Projeto:** `backend/adapters/IaBimeta.Adapters.Ai.Anthropic` (4 arquivos de código, sem pacote NuGet, só `AiGateway.Contracts`, DEP-2/DEP-3). Só `IaBimeta.Api` e `IaBimeta.Worker` o referenciam e registram (`AddAnthropicProvider`).
- **`AnthropicProvider`** implementa `IAiProvider` (`ProviderId = "anthropic"`): UMA chamada por vez, falha do provedor é **resultado** (nunca exceção), sem retentativa própria, sem troca de provedor, sem estado entre chamadas. Só o cancelamento do chamador sai como `OperationCanceledException` (o AiGateway decide).
- **`AnthropicRequestFactory`** monta o pedido; **`AnthropicResponseReader`** normaliza resposta, uso e erros; **`AnthropicOptions`** é a configuração (seção `AiGateway:Providers:Anthropic`, sem segredo).
- **Reaproveitado sem alteração:** `IAiProvider`, `AiGatewayService` (reserva, limites, tempo limite, ledger, política de erro), `AiModelSelector`/catálogo, credenciais BYOK cifradas (`AesGcmCredentialProtector`), `ITenantOperationGate`.

## 2. API usada (documentação oficial lida em 27/11/2026)

| Item | Valor | Fonte |
|---|---|---|
| Endpoint | `POST https://api.anthropic.com/v1/messages` | platform.claude.com/docs/en/api/overview |
| `anthropic-version` | `2026-10-21` (a mais recente documentada; configurável em `ApiVersion`) | …/api/versioning |
| Autenticação | `Authorization: Bearer <chave>` (o `x-api-key` é fallback legado; o adaptador usa só o Bearer) | …/api/overview |
| `content-type` | `application/json` | …/api/overview |
| Corpo | `model`, `max_tokens`, `system` (texto), `messages` (`user`/`assistant`, texto), `output_config` (`effort`, `format`) | …/api/messages |
| Saída estruturada | `output_config.format = {type:"json_schema", schema}`; o JSON volta num bloco `text` | …/build-with-claude/structured-outputs |
| Removidos na versão 2026-10-21 | `temperature`, `top_p`, `top_k` (HTTP 400): o adaptador **nunca** os envia | …/api/versioning |
| Resposta | `type:"message"`, `content[]` (`text`, `thinking`…), `stop_reason`, `usage` | …/api/messages |
| `usage` | `input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens`, `output_tokens_details.thinking_tokens` | …/api/messages |
| Identificador | cabeçalho `request-id` (e `request_id` no corpo de erro) | …/api/errors |
| Erros | 400 `invalid_request_error`, 401 `authentication_error`, 402 `billing_error`, 403 `permission_error`, 404 `not_found_error`, 409 `conflict_error`, 413 `request_too_large`, 429 `rate_limit_error`, 500 `api_error`, 504 `timeout_error`, 529 `overloaded_error` | …/api/errors |

**Pontos em que o contrato da Anthropic difere do da OpenAI (e como o adaptador os trata):**
- A **entrada vem em três parcelas** (`input_tokens` NÃO inclui o cache): o adaptador soma `input_tokens + cache_creation + cache_read` para o total do contrato, mantendo leitura e escrita de cache discriminadas.
- O **raciocínio adaptativo** vem ligado por padrão nos modelos 5.5 e seus tokens contam em `max_tokens` e são cobrados como saída. O adaptador **não envia `thinking`** (a tabela oficial indica que nem todo modelo aceita desligá-lo) e controla o custo/latência pelo `effort` do perfil (`atendimento-rapido` = `low`). Blocos `thinking` nunca entram na resposta.
- **Recusa do modelo** é HTTP 200 com `stop_reason: "refusal"` (tokens cobrados): vira `AiStopReason.Refused`, sem repassar texto.
- **Sem chave de idempotência** na geração (igual à OpenAI): nenhuma retentativa interna.
- Não existe `store`/estado no servidor na Messages API (API sem estado): nada a desligar.

## 3. Modelos suportados (catálogo `AiGateway:Catalog:Models`)

Confirmados na página oficial de modelos e de preços em 27/11/2026. Preços por 1 milhão de tokens (USD):

| Modelo (`ModelId`) | Contexto | Saída máx. | Entrada | Escrita de cache (5 min) | Leitura de cache | Saída | Observação |
|---|---|---|---|---|---|---|---|
| `claude-haiku-5-5` | 1M | 128K | 0,10 | 0,125 | 0,01 | 0,50 | recomendado para atendimento (ADR 0017); **acima de 100 mil tokens de prompt** a Anthropic cobra 0,50/0,625/0,05/2,50 (o catálogo guarda a faixa até 100 mil; o limite padrão por chamada do tenant é 20 mil, então a faixa maior não é alcançada). Aposentadoria: não antes de 07/10/2027 |
| `claude-sonnet-5-5` | 1M | 128K | 2,00 | 2,50 | 0,10 | 10,00 | qualidade maior; aposentadoria não antes de 28/09/2027 |

Ambos usam o perfil `atendimento-rapido` do adaptador (`effort: low`). Outros modelos (Opus 5.5, Fable 5.2, Haiku 4.5…) **não** estão no catálogo: um modelo fora do catálogo é recusado (`ModelNotSupported`). Para incluir outro, acrescente a entrada ao catálogo com os preços oficiais; não há descoberta dinâmica de modelos.

## 4. Catálogo × adaptador (correção)

Antes: o catálogo (`appsettings.json`) listava `anthropic/claude-haiku-5-5` sem existir adaptador, e o tenant podia **gravar** essa escolha (o erro só aparecia na chamada: `provider-not-registered`).
Agora:
- o adaptador existe e é registrado no **Api** e no **Worker**;
- `AiTenantConfigurationService.SetConfigurationAsync` e `SetCredentialAsync` **recusam** (`AiGatewayValidationException`) provedor do catálogo **sem adaptador registrado naquele host**: nada é aceito em silêncio e a IA nunca é configurada para um provedor inexistente;
- o seletor de chamada (`AiModelSelector`) já recusava modelo inexistente, desativado ou sem adaptador, **sem fallback** para outro provedor (inalterado);
- o relatório de partida do Worker marca `(SEM ADAPTADOR: indisponivel)` ao lado de cada modelo do catálogo cujo provedor não tem adaptador.

## 5. Configuração técnica

| O quê | Onde | Padrão |
|---|---|---|
| Endereço da API | `AiGateway:Providers:Anthropic:BaseAddress` (só HTTPS; HTTP só em laço local) | `https://api.anthropic.com/v1/` |
| Versão da API | `AiGateway:Providers:Anthropic:ApiVersion` | `2026-10-21` |
| Tamanho máx. da resposta, tempos de conexão | `MaxResponseBytes`, `ConnectTimeout`, `PooledConnectionLifetime` | 1 MiB, 10 s, 2 min |
| Perfis de modelo | `AiGateway:Providers:Anthropic:Profiles:<nome>` com `Effort` (`low`/`medium`/`high`/`xhigh`/`max`) e `CacheSystemPrompt` | `atendimento-rapido`: `Effort=low`, sem cache |
| Credencial | **BYOK por tenant**, cifrada AES-256-GCM pelo AiGateway (mesmo mecanismo da OpenAI); nada em configuração | — |

Cache do prompt de sistema (`CacheSystemPrompt`) está **desligado** por padrão: a escrita custa 1,25x a entrada e só compensa com prompt estático acima do mínimo cacheável (512 tokens nos modelos 5.5) e chamadas frequentes. Ligue só depois de medir.

## 6. Consumo e custo (ledger)

O adaptador devolve ao ledger existente, por chamada: provedor, modelo, tokens de entrada (**total**, com cache), leitura de cache, escrita de cache, saída, raciocínio (quando informado), `request-id` e a **certeza** do consumo.
- Resposta sem `usage` válido **nunca** vira sucesso: consumo `Unknown`, e o AiGateway cobra a reserva (limite superior). Consumo desconhecido nunca é apresentado como zero.
- `NoneIncurred` (consumo zero) só em rejeição de admissão com corpo de erro completo (400, 401, 402, 403, 404, 413, 422).
- **Limitações** (para a Etapa 12, não implementadas): (a) a escrita de cache de **1 hora** custa 2x a entrada e a tabela do catálogo tem uma só tarifa de escrita (5 min): como o adaptador só usa cache de 5 min e só quando o perfil o liga, o custo estimado é exato nesse caso; (b) a faixa acima de 100 mil tokens do Haiku 5.5 não é modelada (inalcançável com o limite padrão); (c) `service_tier`, `inference_geo` e modo rápido não são enviados nem precificados. O custo do ledger é **estimado**, não fatura.

## 7. Tratamento de falhas

| Situação | Classe (`AiErrorKind`) | Retentável? | Consumo |
|---|---|---|---|
| Credencial ausente / formato inválido | `InvalidCredentials` (nada enviado) | não | nenhum |
| 401, 403 | `InvalidCredentials` | não | nenhum (admissão) |
| 402 (`billing_error`) | `QuotaOrBilling` | não | nenhum |
| 400 (limite de gasto do cliente) / 429 com `enforced_spend_limit_reached` | `QuotaOrBilling` | não | 400: nenhum; 429: indeterminado |
| 400, 404 (modelo inválido), 422 | `InvalidRequest` | não | nenhum (admissão) |
| 413, `model_context_window_exceeded` | `ContextTooLong` | não | nenhum / informado |
| 429 (taxa), 409, 408, 500, 503, 504, 529 | `Transient` (respeita `retry-after`) | sim, pelo **Agents** (limites dele) | indeterminado (cobra a reserva) |
| Tempo esgotado (do adaptador) / falha de rede | `Transient` (`timeout`/`network-error`) | sim | indeterminado |
| Cancelamento do chamador | `OperationCanceledException` → AiGateway | — | indeterminado |
| Corpo inválido / sem `usage` / `type` diferente de `message` | `Transient` (`invalid-response`/`missing-usage`) | sim | indeterminado |
| `stop_reason` `tool_use`, `pause_turn` ou desconhecido | `InvalidRequest` (`unexpected-stop-reason`) | **não** (outra chamada seria outra cobrança) | informado |
| `max_tokens` | sucesso `Truncated` | — | informado |
| `refusal` | sucesso `Refused` (sem texto) | — | informado |
| `end_turn` sem texto | falha `empty-output` | sim | informado |

Retentativa e espera são do **Agents**; o adaptador e o AiGateway nunca tentam de novo sozinhos e **nunca** caem para outro provedor. A mensagem de erro do provedor jamais é repetida (só o `error.type` e o `details.error_code`, saneados).

## 8. Suspensão de tenants (Etapa 02, preservada)

A Anthropic passa pelo mesmo AiGateway: tenant suspenso (ou falha ao consultar o estado operacional) **não chama o provedor** (`tenant-suspended`, sem ledger nem credencial lida); a credencial e a configuração ficam preservadas; reativado, mensagens **novas** voltam a ser atendidas e eventos antigos não disparam atendimento. Provado de ponta a ponta com a Anthropic simulada (`Empresa_suspensa_nao_chama_a_Anthropic…`).

## 9. Validação funcional local (sem chamada paga)

Pré-requisitos: o ambiente de desenvolvimento de sempre (PostgreSQL 5433, WhatsApp simulado 5198). **Não** é preciso chave real.

```powershell
# 1. A "Anthropic" simulada (porta 5200) — numa janela própria
.\tools\dev\Start-AnthropicSimulada.ps1

# 2. Api e Worker apontando para ela (a variável vale só para os processos iniciados deste terminal)
$env:AiGateway__Providers__Anthropic__BaseAddress = 'http://127.0.0.1:5200/v1/'
.\tools\dev\Start-SimuladorDeIa.ps1        # (ou reinicie Api e Worker como de costume, com a variável acima)

# 3. A empresa sintética demo-anthropic (provider anthropic / claude-haiku-5-5, chave SINTÉTICA, WhatsApp simulado)
.\tools\dev\Novo-TenantDeAnthropicDeDemonstracao.ps1

# 4. Mensagens do cliente (a 1ª é respondida pela IA; a 2ª é transferida ao atendente)
.\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant demo-anthropic -Texto 'Ola, tudo bem?' -Nome Marta -Numero 5511999990601
.\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant demo-anthropic -Texto 'Quanto custa o hemograma?' -Nome Marta -Numero 5511999990601
```

O que conferir: (a) a janela da Anthropic simulada mostra `versao=2026-10-21 autenticada=True` e a decisão; (b) o ledger (`GET http://localhost:5155/simulator/ai/requests`, cabeçalho `X-Dev-Tenant: demo-anthropic`) mostra `providerId=anthropic`, `modelId=claude-haiku-5-5`, entrada **900** (300 + 600 do cache), `cachedInputTokens=600`, saída 40, `certainty=Reported` e `providerRequestId=req_simulada`; (c) suspender `demo-anthropic` no painel `/plataforma` e enviar mensagem: nada chega à janela da simulada; reativar: mensagem **nova** é atendida.

`demo-ia`, `demo-usuarios`, os ambientes da plataforma, o simulador da OpenAI (5199) e o do WhatsApp (5198) **não são tocados** (a variável de ambiente só afeta o adaptador da Anthropic).

## 10. Validação com a API real (somente mediante autorização)

**Transferida para a Etapa 13; sem autorização vigente para chamadas pagas.** O roteiro `docs/operations/ETAPA-03-ANTHROPIC-API-REAL.md` fica preservado como referência para a Etapa 13, sujeito a nova autorização e à reconfirmação do orçamento. Nenhuma chamada à API real foi feita.

## 11. Limitações conhecidas

1. **Sem streaming, sem tool calling, sem prefill, sem citações, sem documentos/imagens**: o contrato `IAiProvider` é só texto (decisão do ADR 0017); o adaptador usa o subconjunto necessário ao atendimento comercial inicial. Não houve ampliação do contrato.
2. **Sem `thinking` explícito**: o raciocínio fica no padrão adaptativo do modelo (custo variável de saída); mitigado por `effort: low` e pelo teto de saída do tenant. Se um modelo aceitar desligar o raciocínio, isso é evolução do perfil.
3. **Respostas não incrementais**: o documento oficial recomenda streaming para requisições com `max_tokens` muito alto e duração acima de 10 minutos; o atendimento usa teto de saída de centenas de tokens e tempo limite de segundos, longe desse regime.
4. **5xx com `x-should-retry: false`** não é diferenciado (todo 5xx é `Transient`); o Agents limita as tentativas.
5. **Retenção de dados na Anthropic**: duas páginas oficiais divergem (ADR 0017, Anexo A.2, item 9). **Confirmar com a Anthropic antes de prometer retenção ao tenant** e antes de operar com clientes reais, assim como a política de uso para saúde (aviso de IA ao consumidor).
6. **Faixa de preço acima de 100 mil tokens, cache de 1 hora e modos de serviço**: ver seção 6 (Etapa 12).
7. **Não validado contra a API real** (nenhuma chamada paga foi feita): pendência da Etapa 13. Pendências da Etapa 12: reconciliação financeira, tarifas diferenciadas de cache, faixas superiores de consumo, representação financeira dos resultados indeterminados e limites monetários.

## 12. Resultados dos testes

Resultados **reportados pelo implementador** em 27/11/2026 (HTTP simulado; nenhuma chamada paga). A revisão do Codex foi **estática e independente** (não reexecutou as suítes). **A regressão integral não foi reexecutada após a correção de retenção**; só o teste de retenção (12 aprovados) e os testes do script de credenciais foram rodados depois:

| Suíte | Resultado |
|---|---|
| Adaptador Anthropic (novo: 33 testes, 85 casos com as variações) | 85 aprovados |
| Adaptador OpenAI (regressão) | 89 aprovados |
| Adaptador Ollama (regressão) | 22 aprovados |
| AiGateway (unitários) | 193 aprovados |
| Agents (unitários) | 908 aprovados |
| Arquitetura (80 anteriores + 5 do adaptador Anthropic) | 85 aprovados |
| Integração: AiGateway, Agents, Tenancy, Provision, DevelopmentAi (inclui os 6 ponta a ponta da Anthropic e o de catálogo × adaptador) | 265 aprovados |
| Integração: Workflows, Api, Embedded | 764 de 765 aprovados; 1 falha (retenção, abaixo), corrigida depois |
| Retenção (`RetencaoDeDadosTests`) após a correção | 12 aprovados |
| Validação local com a Anthropic simulada (empresa `demo-anthropic`) | 2 chamadas, ledger correto (ver seção 9) |

**Falha encontrada, não causada por esta etapa (histórico):** `RetencaoDeDadosTests.Toda_tabela_do_banco_esta_classificada…` acusou `tenancy.tenant_status_changes` (tabela criada pela migration da Etapa 02) como não classificada no inventário de retenção. **Resolvida:** ver seção 16 (classificação `LegalRecord` aprovada pelo proprietário; 12 testes de retenção aprovados).

**Script de credenciais (ajuste P2 do Codex):** `tools/dev/Set-ChaveDaAnthropic.ps1` valida a URL do Api como URI **antes** de pedir a chave (HTTP só em loopback `localhost`/`127.0.0.0/8`/`::1`; HTTPS para o resto; protocolos e URLs inválidas e `usuario@host` recusados), não segue redirecionamentos (`-MaximumRedirection 0`) e não desliga a validação de certificado. Validado só com chave sintética e sem API real: 13 casos de URL e o redirecionamento 302 para destino não autorizado (recusado; o destino não recebeu nada).

Cobertura dos testes novos: construção do pedido (cabeçalhos, Bearer, versão, sistema, histórico, modelo, teto, perfil, saída estruturada, UTF-16 inválido), resposta (texto, `thinking` ignorado, três parcelas de entrada, cache, motivos de encerramento `end_turn`/`max_tokens`/`refusal`/contexto excedido/inesperados), erros (400 a 529, teto de gasto, `retry-after`, `request-id`), timeout, cancelamento, rede, sigilo da credencial, isolamento entre 40 chamadas simultâneas de tenants, registro/resolução no DI e no AiGateway, catálogo × adaptador, ponta a ponta Agents → AiGateway → adaptador → servidor HTTP simulado (consumo com cache, duas empresas sem mistura, 529 com nova tentativa e sem fallback, 401 sem consumo, cota esgotada, empresa suspensa/reativada).

Compatibilidade do esquema de decisão do Agents com a saída estruturada da Anthropic: usa só `type`, `enum`, `properties`, `required` e `additionalProperties:false` (todos suportados); nenhuma palavra-chave recusada pela API (`minimum`, `minLength`, recursão…).

## 13. Referências oficiais e grau de confirmação (lidas em 27/11/2026)

**Legenda:** **[DOC]** confirmado na documentação oficial · **[DOC-RESUMO]** consta na página, mas a leitura foi parcial (página longa truncada) · **[SIM]** reproduzido só no simulador (não prova compatibilidade) · **[NÃO VALIDADO]** depende da API real.

| Item | Referência exata | Situação |
|---|---|---|
| `anthropic-version: 2026-10-21` (primeira da lista de versões; a página recomenda a mais recente) | https://platform.claude.com/docs/en/api/versioning | [DOC]; aceita pela API real: [NÃO VALIDADO] |
| `temperature`, `top_p`, `top_k` removidos: "returns a 400 error on every model" | https://platform.claude.com/docs/en/api/versioning | [DOC]; o adaptador nunca os envia (teste de arquitetura) |
| `Authorization: Bearer <token>` (chave de API) e `x-api-key` como "Legacy fallback" | https://platform.claude.com/docs/en/api/overview | [DOC]; Bearer com chave `sk-ant-…`: [NÃO VALIDADO] |
| `POST /v1/messages`, base `https://api.anthropic.com` | https://platform.claude.com/docs/en/api/overview e https://platform.claude.com/docs/en/api/messages/create | [DOC] |
| `output_config.effort` (low, medium, high, xhigh, max) e `output_config.format` = `{type:"json_schema", schema}` | https://platform.claude.com/docs/en/api/messages/create e https://platform.claude.com/docs/en/build-with-claude/structured-outputs | [DOC]; **`low` aceito especificamente pelo Haiku 5.5: não afirmado** (a página do modelo diz só que ele suporta o parâmetro): [NÃO VALIDADO] |
| Saída estruturada suportada para `claude-haiku-5-5` e `claude-sonnet-5-5` (lista `supportedModels` da página) | https://platform.claude.com/docs/en/build-with-claude/structured-outputs | [DOC] |
| Motivos de encerramento: `end_turn`, `max_tokens`, `stop_sequence`, `tool_use`, `pause_turn`, `refusal`, `model_context_window_exceeded` | https://platform.claude.com/docs/en/api/messages/create | [DOC-RESUMO] (a seção de resposta estava na parte truncada) |
| `usage`: `input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens`, `output_tokens_details.thinking_tokens`; entrada total = soma das três de entrada (`input_tokens` = só o que vem **depois do último ponto de cache**) | https://platform.claude.com/docs/en/api/messages/create e https://platform.claude.com/docs/en/build-with-claude/prompt-caching | [DOC-RESUMO]; a palavra "exclusivo" não aparece, mas a fórmula da soma a implica |
| Cache: escrita 5 min = 1,25x; escrita 1 h = 2x; leitura = 0,1x (0,05x no Opus 5.5 e no Sonnet 5.5) | https://platform.claude.com/docs/en/build-with-claude/prompt-caching | [DOC] |
| `claude-haiku-5-5`: contexto 1M, saída máx. 128K; US$ 0,10 entrada, 0,50 saída, 0,125 escrita 5 min, 0,20 escrita 1 h, 0,01 leitura (prompts até 100.000 tokens); acima: 0,50 / 2,50 / 0,625 / 1 / 0,05; esforço padrão `medium` | https://platform.claude.com/docs/en/models/haiku-5-5/overview e https://platform.claude.com/docs/en/about-claude/pricing | [DOC] |
| `claude-sonnet-5-5`: contexto 1M, saída máx. 128K; US$ 2 entrada, 10 saída, 2,50 escrita 5 min, 4 escrita 1 h, 0,10 leitura; esforço padrão `high`; raciocínio não pode ser desligado | https://platform.claude.com/docs/en/about-claude/models/overview e https://platform.claude.com/docs/en/about-claude/pricing | [DOC] (página própria do Sonnet 5.5 não lida) |
| Erros e `request-id` (cabeçalho e `request_id` no corpo) | https://platform.claude.com/docs/en/api/errors | [DOC] |
| `retry-after`, teto de gasto (`enforced_spend_limit_reached`), limite definido pelo cliente (400 "You have reached your specified API usage limits") | https://platform.claude.com/docs/en/api/rate-limits | [DOC] |
| Raciocínio por modelo (Haiku 5.5: adaptativo e ligado; `disabled` rejeitado ou restrito ao esforço ≤ high) | https://platform.claude.com/docs/en/build-with-claude/thinking-troubleshooting | [DOC-RESUMO]; **o adaptador não envia `thinking`**, então não depende disso |

**Divergência documental registrada (não altera o adaptador):** o ADR 0017 (Anexo A.2, item 8) diz que o Haiku 5.5 "aceita desligar o raciocínio"; a página de raciocínio lida hoje indica que `thinking: disabled` é rejeitado nele (ou restrito ao esforço ≤ high). Como o adaptador **nunca** envia `thinking`, nada muda; vale como correção ao ADR quando houver oportunidade.

**Comportamentos só reproduzidos no simulador [SIM]:** o fluxo ponta a ponta, o ledger (900 de entrada, 600 em cache, 40 de saída) e a suspensão. O simulador e os testes seguem **o formato lido na documentação**; não provam o que a API real aceita ou devolve. O que depende da API real está no roteiro `ETAPA-03-ANTHROPIC-API-REAL.md`, seção 5.

## 14. Cálculo do custo estimado (US$ 0,000056 do teste local)

O simulador devolveu `input_tokens = 300`, `cache_creation_input_tokens = 0`, `cache_read_input_tokens = 600`, `output_tokens = 40`. Tarifas do catálogo para `claude-haiku-5-5` (por 1M de tokens): entrada 0,10; escrita de cache 0,125; leitura de cache 0,01; saída 0,50.

1. **Adaptador:** entrada TOTAL do contrato = 300 + 0 + 600 = **900**; `CachedInputTokens` = 600; `CacheWriteTokens` = 0; saída = 40.
2. **Normalização** (`AiUsageNormalizer`): as parcelas de cache nunca passam do total.
3. **Fórmula** (`AiCostEstimator`): entrada comum = total − leitura − escrita = 900 − 600 − 0 = **300**; custo = (300 × 0,10 + 600 × 0,01 + 0 × 0,125 + 40 × 0,50) / 1.000.000 = (30 + 6 + 0 + 20) / 1.000.000 = **US$ 0,000056**.
4. **Ledger:** `estimatedCost = 0,00005600`, `inputTokens = 900`, `cachedInputTokens = 600`, `outputTokens = 40`, `certainty = Reported`.

| Componente | Tokens | Tarifa/1M | Parcela |
|---|---|---|---|
| Entrada comum (não cache) | 300 | 0,10 | US$ 0,000030 |
| Escrita em cache | 0 | 0,125 | US$ 0,000000 |
| Leitura de cache | 600 | 0,01 | US$ 0,000006 |
| Saída | 40 | 0,50 | US$ 0,000020 |
| **Total** | | | **US$ 0,000056** |

**Sem dupla contagem:** o total de entrada (900) inclui os 600 lidos do cache, mas o cálculo cobra a entrada comum por `total − leitura − escrita`; os 600 tokens são cobrados **uma vez**, à tarifa de leitura (teste do adaptador: 80 + 15 + 5 vira `AiUsage(100, 15, 5, 30, 12)`).

**Desconhecido ≠ zero:** quando o consumo não é informado (timeout, 5xx, 529, 429, resposta sem `usage`), o ledger grava `state = Indeterminate`, `certainty = Unknown` e **aplica à janela do tenant o limite superior** (os tokens reservados), nunca zero. `NoneIncurred` (zero) só existe com evidência de rejeição de admissão. **Limitação para a Etapa 12:** nessas linhas o campo `estimatedCost` fica `0` (não há uso para precificar); quem distingue "indeterminado" de "custo zero" é o par `state`/`certainty`, não o valor monetário. O ledger não foi reformulado.

## 15. Nova tentativa após erro 529: quem faz e por que é segura

- **Quem:** o **Agents** (`TurnRunner`), não o adaptador nem o AiGateway. O adaptador devolve `AiError(Transient)`; o AiGateway aplica `AiErrorPolicy` (só `Transient` é retentável, espera sugerida limitada) e devolve a falha; o `TurnRunner.HandleFailureAsync` decide.
- **Política que autoriza:** ADR 0017 (C.4 e G.4) e `AgentsOptions`: `MaxTurnAttempts = 3` por turno, recuo exponencial de 5 s dobrando até 2 min (`RetryBackoff`, `MaxRetryBackoff`), respeitando o `retry-after` (limitado). Esgotadas as 3, o turno falha de forma permanente e **transfere ao atendente**. Falhas permanentes (credencial, cota, pedido inválido, recusa) **não** são repetidas; saturação de chamadas simultâneas do tenant espera sem gastar tentativa.
- **Resultado incerto:** 529, 5xx, timeout e rede não provam que nada foi cobrado: o ledger registra `Indeterminate` e **cobra a reserva** (limite superior) na janela de uso do tenant; o limite de tokens por período protege contra repetição descontrolada.
- **Cobrança duplicada:** a Anthropic não oferece chave de idempotência na geração; uma nova tentativa é uma **nova chamada**, e cada tentativa vira **uma linha própria** no ledger (no teste: `Indeterminate` seguida de `Confirmed`). O risco residual (a 1ª chamada ter sido faturada) é **conhecido, limitado a 3 tentativas por turno** e visível no ledger. Nenhuma política foi alterada. Respostas inesperadas com tokens cobrados (`tool_use`, `pause_turn`) e `refusal` **não** são repetidas.
- **Contabilização:** 529 seguido de sucesso = 1 linha `Indeterminate` (reserva aplicada) + 1 linha `Confirmed` (consumo real). A reserva da linha indeterminada não é abatida automaticamente (lado seguro; reconciliação posterior é evolução da Etapa 12).

## 16. Correção da retenção de dados (inconsistência da Etapa 02, identificada na regressão da Etapa 03)

- **Origem:** a migration da Etapa 02 criou `tenancy.tenant_status_changes`, mas ela não entrou no `RetentionInventory`. O teste `RetencaoDeDadosTests.Toda_tabela_do_banco_esta_classificada…` só aparece na regressão ampla; a regressão direcionada da Etapa 02 não o incluía. Foi identificada durante a regressão da Etapa 03. A Etapa 02 não foi reaberta.
- **O que a tabela é:** histórico imutável (só inclusão) das mudanças de situação da empresa: situação anterior e nova, motivo escrito pelo operador da BImeta (5 a 500 caracteres), operador (identificador) e instante. É a evidência de suspensões e reativações, lida pelo histórico do painel `/plataforma`. Não guarda mensagem, credencial nem dado de cliente.
- **Classificação:** `DataClass.LegalRecord` (registro de referência: só inclusão, "não se apaga por prazo"), como `audit.audit_events` e os consentimentos. **Nenhum prazo foi estabelecido, nenhuma tarefa de expurgo criada, nenhum registro apagado, nenhuma migration necessária; as regras de suspensão e reativação não foram tocadas.** A nota do inventário registra que a ausência de prazo **não autoriza exclusão** e que qualquer prazo ou pseudonimização do operador depende de decisão jurídica e de negócio, que não foi tomada aqui.
- **Teste:** `RetencaoDeDadosTests` (12 testes) aprovados após a correção, incluindo o de inventário completo.
- **Aprovação e pendência:** a classificação `DataClass.LegalRecord` foi implementada e **aprovada pelo proprietário**. A **definição jurídica do prazo de retenção permanece pendente** (antes da operação comercial).
