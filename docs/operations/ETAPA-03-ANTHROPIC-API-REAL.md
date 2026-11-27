# Etapa 03 — Roteiro de validação com a API REAL da Anthropic (referência para a Etapa 13)

> ## SITUAÇÃO ATUAL: NÃO EXECUTAR
> - O proprietário **não dispõe de conta de API Anthropic neste momento**.
> - A validação real foi **transferida para a Etapa 13**.
> - **Não existe autorização vigente** para executar chamadas pagas; nenhuma foi feita.
> - O orçamento de **US$ 0,10** abaixo foi apenas **proposto** (histórico de 27/11/2026) e **deverá ser reconfirmado** antes do futuro teste real, junto com nova autorização.
> - Os testes simulados **não comprovam** compatibilidade com a API comercial; a utilização comercial da Anthropic **depende** desta validação real.
> - Antes do teste real (requisito da Etapa 13): verificar a separação entre ambientes demonstrativos e produção, garantir que endpoints simulados e credenciais sintéticas não cheguem ao ambiente comercial, conferir a configuração segura dos endpoints dos provedores e validar TLS e conectividade com a API oficial.
>
> O restante deste documento é o roteiro **preparado**, preservado como referência.

> **Histórico:** em 27/11/2026 o proprietário havia autorizado o teste, com orçamento máximo de US$ 0,10, condicionado à inserção da chave **por ele**, pelo mecanismo seguro (`Set-ChaveDaAnthropic.ps1`, agora com validação de URL: HTTP só em loopback e sem redirecionamentos). A chave nunca é colada no chat, em arquivo, em comando registrado, em log nem em variável de ambiente persistente. Só a empresa `demo-anthropic`, conteúdo sintético, WhatsApp **simulado**. Essa autorização foi superada pela decisão acima.

## 1. O que você precisa (a cargo do proprietário)

| Item | Detalhe | Fonte oficial |
|---|---|---|
| Conta no Console | "Sign in to the Claude Console" | https://platform.claude.com/docs/en/get-api-key |
| Criar a chave | https://platform.claude.com/settings/keys → **Create key**: nome (ex.: `iabimeta-teste-etapa03`), **expiração curta (1 dia)**, vinculada a você (ou *service account*), *workspace* de teste se houver. A chave começa com `sk-ant-` e **aparece uma única vez** | idem |
| Limite de gasto | https://platform.claude.com/settings/spend-limits → "Monthly spend limit" → **Set limit**. Coloque o **menor valor que o Console aceitar** (se o mínimo for acima de US$ 0,10, use o mínimo: a garantia do orçamento vem dos limites do item 2, não deste). Ao atingir o limite a API responde 400 e o adaptador classifica como cota esgotada | https://platform.claude.com/docs/en/api/rate-limits |
| Créditos / cobrança | A documentação oficial lida **não** diz se é preciso saldo ou meio de pagamento nem como comprar crédito. **Confirme no Console (Billing) que há saldo/forma de pagamento** e anote o saldo antes e depois do teste; sem isso a API pode responder 402 (`billing_error`) | **não confirmado na documentação** |
| Modelo | `claude-haiku-5-5` | https://platform.claude.com/docs/en/about-claude/models/overview |

## 2. Orçamento: custo estimado × limite efetivamente garantido

**Quantas chamadas por mensagem.** O Agents faz **1 chamada ao modelo por turno** (mensagens enviadas dentro da janela de agrupamento viram um turno só). A 2ª chamada do Agents (consulta a sistema externo) e a resposta semântica ficam **desligadas** por padrão e a empresa não tem integração, então não existem chamadas adicionais de "decisão" ou "geração". Uma falha **transitória** pode repetir o turno: **até 3 tentativas** (`MaxTurnAttempts`), cada uma = 1 chamada nova e 1 linha no ledger. A decisão de responder ou transferir vem **na mesma chamada** (saída estruturada).

**Preço (haiku-5-5, por 1M):** entrada 0,10 · saída 0,50 · escrita de cache 0,125 · leitura de cache 0,01. O adaptador **não** liga cache (`CacheSystemPrompt = false`): esperado cache zero; o pior caso abaixo ainda considera entrada inteira tarifada como escrita de cache.

**Teto de saída real:** o Agents pede **600** tokens; o limite do tenant é 1.000; vale **600** (não 1.000).

| Cenário | Entrada | Saída | Custo por chamada |
|---|---|---|---|
| **Realista** (instruções curtas + histórico de poucas mensagens) | ≈ 1.500–2.500 | ≈ 100–600 (inclui raciocínio com `effort: low`) | **≈ US$ 0,0003 a 0,0005** |
| Pior caso com os **limites padrão** (20.000 tokens por chamada, sem cache) | 19.400 | 600 | US$ 0,00194 + 0,00030 = **US$ 0,0022** |
| Pior caso padrão, toda a entrada tarifada como escrita de cache | 19.400 | 600 | US$ 0,00243 + 0,00030 = **US$ 0,0027** |
| Pior caso **com os limites deste teste** (8.000 tokens por chamada) | 7.400 | 600 | US$ 0,00074 + 0,00030 = **US$ 0,0010** (com escrita de cache: 0,0012) |

(A estimativa informada pelo proprietário, US$ 0,0024, usa 1.000 tokens de saída; com o teto real de 600 o pior caso padrão é US$ 0,0022.)

**Total do teste:** 6 mensagens (1 de compatibilidade + 5 funcionais), uma por vez, sem agrupar:

| | Chamadas | Custo realista | Pior caso (limites padrão) | Pior caso (limites do teste) |
|---|---|---|---|---|
| Sem nenhuma nova tentativa | 6 | ≈ US$ 0,002 a 0,003 | US$ 0,013 a 0,016 | US$ 0,006 a 0,007 |
| Todas as 6 com 3 tentativas | 18 | — | US$ 0,040 a 0,049 | (bloqueado: ver abaixo) |

**O que é estimativa e o que é garantido:**
- **Estimativa** do ia.bimeta: a tabela acima (preços do catálogo × tokens reportados). Não é fatura.
- **Garantido pelo AiGateway antes de cada chamada** (mecanismo já existente, sobrescritas de limite **só da empresa `demo-anthropic`**, aplicadas por `tools\dev\Set-LimitesDoTesteAnthropic.ps1`): **no máximo 10 chamadas** e **40.000 tokens** por período de 24 h (2 chamadas simuladas anteriores já contam na janela, restam 8 e ~38.000 tokens) e **8.000 tokens por chamada** (reserva = entrada estimada + 600). Esgotado o limite, o gateway **recusa antes de chamar a Anthropic** (custo zero). Com esses limites: **no máximo 8 chamadas ao provedor ⇒ ≤ US$ 0,0098**, e mesmo o teto teórico de 40.000 tokens todos tarifados como saída seria US$ 0,020. Ressalva honesta: a reserva usa uma **estimativa** de entrada (caracteres ÷ divisor × margem 1,3), não o tokenizador da Anthropic; o consumo real pode divergir um pouco, mas não em ordem de grandeza.
- **Garantido pela Anthropic:** só o **limite de gasto do Console** (item 1): é a rede de segurança externa.
- **Conclusão:** a quantidade de chamadas e os limites **permitem respeitar o orçamento de US$ 0,10 com folga de mais de 10 vezes** (pior caso com os limites do teste ≈ US$ 0,01; até sem os limites, 18 chamadas ≈ US$ 0,05).

## 3. Procedimento

Preparação que o teste futuro exigirá (nada disso está ativo hoje: em 27/11/2026 os limites do teste foram **removidos** da `demo-anthropic` com `Set-LimitesDoTesteAnthropic.ps1 -Remover`, a chave da empresa é a **sintética** e Api/Worker não estão apontando para a API real): aplicar os limites à `demo-anthropic`, subir Api e Worker apontando para a **API real** (sem a variável da simulada).

**Você (no seu terminal, na raiz do worktree):**

1. Crie a chave e o limite no Console (item 1) e confirme o saldo.
2. Insira a chave com segurança (pede **sem eco**; vai num único `PUT` ao Api local, que a cifra com AES-256-GCM; nunca é impressa, registrada nem devolvida):
   ```powershell
   .\tools\dev\Set-ChaveDaAnthropic.ps1
   ```
   Cole a chave **somente neste prompt**. Se a chave aparecer em qualquer outro lugar, revogue-a no Console imediatamente.
3. Avise-me que a chave foi inserida. **Eu** executo o resto.

**Eu, depois do seu aviso:**

4. **Operação única de compatibilidade** (1 mensagem sintética: `Ola, tudo bem?`). Verifico: conectividade, autenticação Bearer, `anthropic-version`, modelo, `output_config.effort`, saída estruturada, formato da resposta, `stop_reason`, `request-id`, tokens e cache, e a linha do ledger. **Em qualquer erro (autenticação, versão, modelo, parâmetro ou esquema) eu paro, apresento o diagnóstico e não tento outras combinações nem altero o adaptador.**
5. **Somente se a operação 4 der certo**, 4 cenários, um por vez (WhatsApp simulado → Agents → AiGateway → adaptador → API real):
   - `Voces atendem aos sabados?` (resposta + saída estruturada),
   - `E aos domingos?` (continuidade de contexto),
   - `Quero falar com um atendente` (transferência ao humano),
   - 1 mensagem extra apenas se algum critério ficar inconclusivo, sem ultrapassar 6 mensagens no total.
6. Em todas: conferir o ledger (uma linha por chamada), a ausência de chamadas à OpenAI e a correspondência dos tokens.

## 4. Resultado esperado

- `anthropic` / `claude-haiku-5-5`, `state = Confirmed`, `certainty = Reported`, `stopReason = Completed`.
- IA responde às duas primeiras; transfere a terceira ao atendente (aparece em **Aguardando**).
- `request-id` (`req_…`) no ledger; tokens de entrada (total) e saída coerentes com a resposta da API; cache zero (a menos que a API informe outra coisa).
- Nenhuma chamada à OpenAI (o servidor simulado da OpenAI não recebe a credencial desta empresa).
- **Custo estimado** pelo ia.bimeta = soma do `estimatedCost` do ledger. **Cobrança efetiva** = o que o Console mostrar; **não presumo** detalhamento por `request-id` no Console: comparo o total e o saldo antes e depois.

## 5. O que ainda NÃO foi validado e esta rodada confirma

1. `Authorization: Bearer` com chave `sk-ant-…` aceito (método principal na documentação; `x-api-key` é o legado).
2. `anthropic-version: 2026-10-21` aceito.
3. `output_config.effort = "low"` aceito pelo `claude-haiku-5-5` (a página do modelo não lista os níveis aceitos).
4. `output_config.format` (JSON schema) aceito com o esquema de decisão do Agents.
5. Ausência de `thinking` aceita (raciocínio adaptativo padrão).
6. `usage` real: `input_tokens` exclusivo das parcelas de cache e `output_tokens` incluindo o raciocínio.
7. Cabeçalho `request-id` presente.

## 6. Encerramento (obrigatório)

Eu: revogo a credencial do tenant (`Set-ChaveDaAnthropic.ps1 -Revogar`), removo as sobrescritas de limite (`Set-LimitesDoTesteAnthropic.ps1 -Remover`), reinicio Api e Worker apontando para a Anthropic **simulada**, recadastro a chave **sintética** e confirmo os serviços.
**Você:** apague a chave de teste no Console (https://platform.claude.com/settings/keys) e registre o saldo final.

## 7. Precauções

- Chave dedicada, de expiração curta, com limite de gasto baixo; apagada ao final. Nunca reutilizar chave de produção de ninguém.
- Sem dados reais de clientes; só frases genéricas. Pendências jurídicas (retenção de dados da Anthropic, política de uso para saúde: ADR 0017, Anexo A.2) continuam abertas e valem antes de operar com clientes reais.
- Se o custo reportado ou o saldo do Console indicar algo muito acima do esperado, **pare**, revogue a chave e informe.
- A Anthropic orienta guardar a chave em gerenciador de segredos e apagar a que for suspeita de vazamento (https://platform.claude.com/docs/en/manage-claude/authentication).
