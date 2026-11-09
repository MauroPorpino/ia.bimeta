# ADR 0030 — Lacunas do FAQ: aprendizado supervisionado, com registro só de referências, revisão administrativa e envio ao rascunho

- **Status:** **Proposto para ratificação.** Implementado em 09/11/2026 sob a autorização do decisor ("Pacote B: aprendizado supervisionado da base de conhecimento, sem treinamento automático de modelos"), depois da ratificação do ADR 0029. Nenhuma chamada real à Meta ou à OpenAI; a migração só **cria duas tabelas** (backup do banco de teste feito antes); `main` intocada; sem push.
- **Data:** 09/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0029 (resposta direta do FAQ; `FaqMatcher`, aliases, `AutoReply`, idioma), 0017 (seção F: definição do agente com rascunho e publicação; a `Presentation` do Agents estava prevista), 0007 e `CONVERSATION-LIFECYCLE.md` (turno e cercas), 0025 (retenção) e `RetentionInventory`, 0026 (`status`), 0024 (`provision`), `docs/architecture/BOUNDARIES.md` (nenhum texto normativo alterado).
- **Preserva:** nenhuma aresta nova na matriz de dependências (o Agents já usa o `Conversations.Contracts`; nenhuma edição de `BOUNDARIES.md`, `ARCHITECTURE.md` ou `module-dependencies.json`); **nenhum módulo Knowledge**; tenant e permissões só pelo `RequestContext`; o modelo nada executa; a resposta defasada continua **sempre** recusada; **`AutoReply` continua desligado por padrão e só um administrador o liga, entrada por entrada**; **nada é treinado, publicado nem alterado sozinho**. Duas mudanças deliberadas e registradas aqui: nasce o projeto **`IaBimeta.Agents.Presentation`** (uma regra de arquitetura do Agents mudou de propósito) e o **`GetHistoryQuery`** do Conversations ganha um parâmetro **aditivo**.

## Contexto

O ADR 0029 corta tokens respondendo direto a pergunta que o tenant cadastrou. Mas o tenant só cadastra o que já sabe que os clientes perguntam, e a economia depende de descobrir **o que eles de fato perguntam** e o FAQ não cobre. Esta decisão dá a **aprendizagem supervisionada**: o sistema registra, sem copiar texto de cliente, as perguntas que não tiveram resposta direta; um administrador as revisa, vê a resposta que um atendente deu (como sugestão, editável) e as envia ao **rascunho** da definição do agente; publicar continua sendo um ato explícito do administrador. **Nenhum modelo é treinado e nenhuma resposta humana é presumida adequada.**

**Fora do escopo (decidido):** módulo Knowledge, busca textual ou semântica, agrupamento por similaridade (as lacunas se agrupam pela **igualdade** do texto normalizado, o mesmo princípio do ADR 0029), sugestão de texto pela IA, publicação automática, `AutoReply` automático, painel de métricas, rota para criar a primeira definição do agente, remover entrada do rascunho, reabrir lacuna descartada, expurgo por prazo das lacunas (só classificação de retenção).

## Decisão

### 1. O que vira lacuna (e o que não vira)

No `TurnRunner`, no mesmo ponto do ADR 0029 (antes de montar o prompt), a mensagem só é **elegível** quando o grupo é **uma única mensagem de texto pronta, no idioma do FAQ**. Elegível, a busca (`FaqMatcher`) decide: resposta direta, ou **lacuna** de um dos três tipos (`KnowledgeGapKind`):

| Tipo | Quando |
|---|---|
| `NoMatch` | nenhuma entrada tem a pergunta ou um alias igual: a oportunidade de uma entrada nova |
| `Ambiguous` | duas ou mais entradas correspondem: há duplicata no FAQ |
| `NotAutoReply` | uma única entrada corresponde, mas sem `AutoReply`: o FAQ já sabe responder e a resposta direta está desligada nela |

**Não elegíveis, nunca viram lacuna:** várias mensagens no grupo, mídia (com ou sem legenda), conversa em outro idioma que o do FAQ, texto só de símbolos ou emoji (nada a revisar); a pergunta que o FAQ respondeu direto, e a que está no FAQ com resposta direta desligada pela chave de desligamento (ela não é lacuna, só a função está desligada). **O registro independe da resposta direta:** um tenant que ainda não ligou nenhum `AutoReply` também descobre o que os clientes perguntam.

### 2. O que é guardado: só referências e um hash com segredo do tenant

- **`agents.knowledge_gaps`** (uma por pergunta e por tenant): `question_hash` (**HMAC-SHA256** do texto normalizado com uma chave **derivada por tenant**, 32 bytes, único por `(tenant, hash)`), `kind` (o da última ocorrência), `status` (`Open`, `Dismissed`, `Promoted`), `first_seen_at`, `last_seen_at`, a decisão (`decided_at`, `decided_by`, `promoted_version`) e `revision` (concorrência otimista da decisão).
- **`agents.knowledge_gap_occurrences`** (uma por **turno**): chave `(tenant, turn_id)`, a lacuna e **só referências** (`session_id`, `conversation_id`, `message_id`) e a data. Sem chave estrangeira para outro esquema (DAD-2). **A quantidade de ocorrências é contada, não guardada**: exata por turno.
- **Nenhum texto de cliente** em nenhuma das duas (um teste lê as duas tabelas inteiras, em JSON, e prova que nenhuma palavra da pergunta está lá). O texto é lido do Conversations **na revisão**, com a autorização de quem lê.
- **O segredo do hash:** `Agents:Options:KnowledgeGapHashKey` (Base64, pelo menos 32 bytes), do ambiente, **nunca de arquivo versionado nem de log**. A chave do tenant é `HMAC(mestra, "ia.bimeta/knowledge-gap/v1|" + tenant)`: o **mesmo texto dá hashes diferentes em empresas diferentes** (nenhum cruzamento nem por comparação de hashes) e, sem a chave, não se confirma um palpite nem se volta ao texto. **Sem chave o registro fica desligado, sem erro**; chave inválida ou curta impede o host de partir (sem ecoar o valor); a partida do Worker registra, em log, se o registro está LIGADO. Trocar a chave separa os grupos de antes dos de depois.
- **Idempotente e atômico** (`KnowledgeGapRecorder`, **um comando SQL**): a lacuna é criada ou atualizada pelo hash (`ON CONFLICT`, então turnos concorrentes da mesma pergunta caem na **mesma** lacuna) e a ocorrência é gravada pela chave do turno (`DO NOTHING`, então o turno repetido depois de uma queda conta uma vez). O tenant do candidato é conferido contra o do contexto (SQL à mão não passa pelo filtro global do EF). **Teto por tenant** (`KnowledgeGapMaxPerTenant`, 5.000): atingido, pergunta **nova** não abre lacuna e as existentes continuam contando (um cliente que manda mil textos diferentes não enche o banco).
- **Melhor esforço:** o `TurnRunner` **engole** qualquer falha do registrador (só o cancelamento do próprio turno passa): o atendimento nunca falha, atrasa ou muda por causa dele. O registro vem **antes** da chamada ao modelo, no máximo uma vez por execução.
- **Retenção:** as duas tabelas entram no `RetentionInventory` como **dado pessoal (só relatório)**; nenhum expurgo automático (a decisão de prazo é jurídica; veja `RETENCAO-DE-DADOS.md`, item 7). A **referência a mensagem removida** é tratada: a fonte fica "indisponível" e a lacuna continua revisável.

### 3. A revisão administrativa (`KnowledgeGapService`, Agents.Core)

Só **`TenantSettingsManage`** (o administrador de escopo do tenant); o tenant e o ator vêm do contexto confiável; lacuna de outro tenant é **inexistente** (404), em toda operação.

- **Listar:** das mais frequentes para as menos, filtro por situação, paginado (até 100). **Nunca traz texto.**
- **Consultar:** as ocorrências recentes (referências) e a **fonte**, lida do Conversations **com a autorização de quem pede**: `IConversationQueries.GetHistoryAsync` sob o contexto do **próprio usuário** (leitura de conversas na organização da sessão; **nenhuma elevação de contexto**). Mensagem removida, sessão fora do alcance, sem permissão de leitura, mídia ou sem texto: a fonte vem `Unavailable` e a lacuna segue revisável (o administrador escreve a pergunta). Ler a fonte é **auditado** (`agents.knowledge-gap.source-read`, sem conteúdo).
- **Resposta humana como sugestão:** a **primeira mensagem de saída de um atendente (`MessageAuthor.Human`, nunca a da IA) depois da pergunta**, olhando 30 mensagens, junto de **quantas mensagens do cliente vieram no meio** (com mais de zero, a resposta pode ser de outra pergunta e a interface avisa). **Nunca é usada sozinha**: a interface só a copia por ação do administrador, que a edita. Para achar o que veio depois da pergunta sem percorrer o histórico, o `GetHistoryQuery` do Conversations ganhou o parâmetro **aditivo** `AfterMessageSeq` (o corte vale na própria consulta, antes da paginação, como o `ThroughMessageSeq`); nenhum outro contrato do Conversations mudou.
- **Descartar:** a decisão vale para o hash (as próximas ocorrências só contam); repetir não muda nada; uma lacuna já enviada ao rascunho não se descarta (409).
- **Enviar ao rascunho:** o corpo é o que o **administrador montou** (pergunta, aliases, resposta, `autoReply` falso por padrão). Recusa pergunta ou alias **igual (normalizado)** a qualquer um já existente no FAQ do rascunho (409 `faq-conflict`: duas respostas para a mesma pergunta nunca dariam resposta direta) e respeita todos os limites (300, 1.500, 10 aliases, 50 entradas, 30.000 caracteres). Parte do rascunho que já existe, senão de uma **cópia da versão publicada**; sem definição alguma, 409 `no-definition`. **Marca a lacuna e grava o rascunho na MESMA transação, a decisão primeiro** (dois administradores na mesma lacuna: um vence e o outro recebe conflito antes de qualquer gravação do rascunho). **Reaproveita** `AgentDefinitionService.SaveDraftAsync` (versionamento e auditoria existentes). **Não publica e não toca a versão publicada.** Auditoria: `agents.knowledge-gap.promoted` (e `agents.definition.draft-saved`), sem conteúdo.
- **Publicar** continua sendo a ação explícita, auditada e versionada do `AgentDefinitionService` (`POST /agents/definition/publish`): cria uma versão a partir do rascunho e aposenta a anterior. Nenhuma rota publica por revisar.

### 4. API e interface

`IaBimeta.Agents.Presentation` (`AgentsEndpoints`, só `SharedKernel` e `Agents.Core`):

| Rota | |
|---|---|
| `GET /agents/knowledge-gaps?status&take&skip` | lista |
| `GET /agents/knowledge-gaps/{id}` | detalhe com a fonte |
| `POST /agents/knowledge-gaps/{id}/dismiss` | descarta |
| `POST /agents/knowledge-gaps/{id}/promote` | `{ question, aliases[], answer, autoReply=false }`, envia ao rascunho |
| `GET /agents/definition/draft` e `/published` | as versões (404 se não há) |
| `POST /agents/definition/publish` | publica o rascunho |

`401` sem sessão; `403` sem a permissão; `404` fora do tenant; `400` dado inválido; `409` com `code` (`gap-not-open`, `gap-changed`, `no-definition`, `faq-conflict`, `definition-changed`). **Interface** (React, `frontend/`): a alternância **Atendimento | Conhecimento** só para o administrador de escopo do tenant; lista com filtro, painel de revisão (pergunta lida, **sugestão** do atendente claramente identificada e **nunca preenchida sozinha**, formulário editável com `autoReply` **desmarcado** e o aviso do que não pode ter resposta direta, enviar ao rascunho e descartar) e o painel do **rascunho** ao lado da publicada (marcas "nova", "alterada" e "resposta direta") com **Publicar rascunho** e confirmação. Sem dashboard, sem biblioteca nova.

**Regra de arquitetura alterada de propósito:** `AgentsArquiteturaTests` passou a esperar o `IaBimeta.Agents.Presentation` (antes só Core e Infrastructure), com as referências permitidas `SharedKernel` e `Agents.Core`, como a `Presentation` dos outros módulos; o ADR 0017 já previa o projeto. A matriz de dependências não mudou.

### 5. Operação

`status` (seção **FAQ direto**): uma linha por empresa com as **lacunas abertas** esperando revisão (só a contagem; informativa, nunca atenção). O guia `docs/operations/CONHECIMENTO-DO-FAQ.md` descreve a chave, os limites e a revisão.

## O que mudou

| Onde | Mudança |
|---|---|
| `Conversations` | `GetHistoryQuery.AfterMessageSeq` (aditivo) no contrato, no serviço de consulta e no armazenamento |
| `Agents.Core` | `KnowledgeGap`, `KnowledgeGapOccurrence`, `KnowledgeGapHasher`, `IKnowledgeGapRecorder`/`IKnowledgeGapStore`, `KnowledgeGapService`, opções (`KnowledgeGapsEnabled`, `KnowledgeGapHashKey`, `KnowledgeGapMaxPerTenant`), o registro no `TurnRunner` |
| `Agents.Infrastructure` | as duas tabelas e a migração `AgentsKnowledgeGaps`, `KnowledgeGapStore`, `KnowledgeGapRecorder` |
| `Agents.Presentation` | **projeto novo**: as rotas |
| host | `MapAgentsEndpoints`, `RetentionInventory`, `status` (lacunas abertas), log de partida do Worker |
| `frontend` | área "Conhecimento" |
| ContactCenter, Channels, AiGateway | **nada** |

## Verificação

- **Gate completo** (`dotnet test backend/IaBimeta.slnx`, 09/11/2026): **4072 testes aprovados, 0 falhas** (2569 de unidade e arquitetura + 1503 de integração com PostgreSQL real); frontend **297** aprovados (251 anteriores + 46) e `npm run build` sem erro; verificação de formato dos arquivos sem falha. **+101 testes** sobre os 3971 anteriores: 72 de unidade (`KnowledgeGapTests` 14, `TurnRunnerGapTests` 18, `KnowledgeGapServiceTests` 38, mais 2 nas opções do Agents) e 29 de integração (`KnowledgeGapsTests` 23, `ConhecimentoDoFaqNaOperacaoTests` 5, 1 no relatório do `status`).
- **O que os testes provam:** o hash (estável, por tenant, sem o texto, chave inválida recusada sem ecoar o valor); a máquina de situação da decisão; o registro no turno (os três tipos, cada caso **não elegível**, a pergunta respondida direto nunca é lacuna, o registro **antes** do modelo, **falha do registrador não muda a resposta**, cancelamento passa); o registro com PostgreSQL (**nenhum texto gravado**, turno repetido conta uma vez, a mesma pergunta cai na mesma lacuna com a normalização, 24 turnos concorrentes, 8 repetições do mesmo turno em paralelo, tenants com hashes diferentes, teto, chave ausente, tenant do candidato diferente do contexto); o isolamento entre empresas e a permissão (`TenantSettingsManage`, 404 e 403 em toda operação); a leitura da pergunta e da resposta humana (a resposta da IA nunca é sugestão, mensagens intercaladas contadas, mensagem e sessão removidas, sem leitura de conversas); o filtro `AfterMessageSeq`; o envio ao rascunho (`AutoReply` desligado por padrão, **nada publicado**, conflito com o FAQ existente, lacuna não aberta, sem definição, limites, **decisão concorrente**: quatro administradores na mesma lacuna e lacunas diferentes ao mesmo tempo sem perder entrada em silêncio, e três requisições HTTP simultâneas, só uma recebe 200); a auditoria sem conteúdo; a classificação de retenção (o teste de inventário de todas as tabelas).
- **Demonstração ponta a ponta** (`ConhecimentoDoFaqNaOperacaoTests`, Api e Worker reais, Meta e OpenAI simuladas por HTTP, PostgreSQL de teste), na ordem em que aconteceu:

  ```text
  [cliente 0901] Preciso levar documento para a coleta?            -> a IA simulada transfere (1 requisição à OpenAI)
  [atendente]    Sim, leve um documento oficial com foto.
  [revisão]      pergunta lida: "Preciso levar documento para a coleta?" | sugestão do atendente: "Sim, leve um documento oficial com foto."
  [revisão]      enviada ao rascunho v2 com autoReply=true e resposta editada: "Sim. Leve um documento oficial com foto (RG ou CNH)."
  [cliente 0902] Preciso levar documento para a coleta?            -> "Resposta gerada pela IA simulada."  (OpenAI: 2 requisições; o rascunho não vale)
  [administrador] rascunho publicado: versão 2
  [cliente 0903] preciso levar documento para a coleta
  [cliente 0904] Preciso de documento para coletar???              (alias)
  [resposta ao cliente] Sim. Leve um documento oficial com foto (RG ou CNH).   (OpenAI: 2 requisições no total, nenhuma nova)
  ```

  No mesmo teste: a lista só aparece para o administrador (atendente 403, sem token 401); a lacuna foi vista pelo `status` como "1 lacuna(s) do FAQ aberta(s)"; antes de publicar a versão publicada continuou a 1 com uma entrada; depois, o `status` contou **2 turnos respondidos direto pelo FAQ**.
- **Banco de teste:** backup (`tools\ops\Backup-Banco.ps1 -Origem Teste`) antes da migração; a migração só cria `agents.knowledge_gaps` e `agents.knowledge_gap_occurrences`; nenhum dado foi alterado nem apagado; `iabimeta_dev` não foi tocado.

## Limites e riscos aceitos

- **Igualdade exata.** "Qual o horário" e "qual o horário de vocês" são lacunas **diferentes**: sem agrupamento por similaridade (mesmo princípio do ADR 0029). A lista pode ter variações da mesma dúvida; o administrador as une cadastrando aliases.
- **Ruído.** Saudações e agradecimentos viram lacunas como qualquer texto: o administrador descarta. O teto por tenant protege o banco, não a lista.
- **A sugestão pode errar.** É a primeira resposta de um atendente depois da pergunta, em uma janela curta; por isso é só sugestão, sempre editável, com o aviso de mensagens intercaladas. **Uma resposta humana nunca é presumida adequada para uso automático:** `AutoReply` desligado, e o aviso do que não deve ter resposta direta (preço, agenda, cobertura, orientação clínica, referência ao contexto).
- **Dado pessoal pode ir parar no FAQ por cópia do administrador** (a resposta do atendente, ou a pergunta, pode citar uma pessoa): o guia manda não copiar dado pessoal; o sistema não o detecta.
- **A leitura da fonte depende da autorização do Conversations.** O administrador de uma única organização não tem `TenantSettingsManage` e não vê a área; sem `ConversationRead` a fonte fica indisponível (a lacuna continua utilizável).
- **A contagem é das ocorrências retidas** (derivada): uma futura retenção que apague ocorrências a reduz.
- **Sem rota** para criar a primeira definição (nasce no `provision`), remover entrada do rascunho ou reabrir lacuna descartada; correção pelo arquivo da empresa. Uma lacuna promovida e depois perdida (rascunho substituído por fora) continua "enviada".
- **Trocar a chave** do hash separa os grupos; o Api não precisa dela (a revisão lê por identificador).
- **Interface verificada** por testes de componente (Vitest) e por uma conferência visual do agente com a API simulada em servidor local, **não** contra o Api real em navegador; o contrato HTTP é o que os testes de integração provam (formato e códigos).

## Plano de reversão

1. **Sem implantar nada:** `Agents:Options:KnowledgeGapsEnabled = false` (ou remover a chave do hash) no Worker: nada mais é registrado. O que já existe fica, sem efeito algum sobre os clientes.
2. **Interface e API:** remover o `MapAgentsEndpoints` e a alternância "Conhecimento" (a definição continua editável como antes, pelo `provision`).
3. **Código:** reverter os commits desta decisão. A migração é reversível (`Down` apaga as duas tabelas; elas só têm referências e contagens, nunca a fonte da verdade); o parâmetro `AfterMessageSeq` é aditivo e opcional.

## Pendências e decisões abertas

- Ratificar este ADR.
- **Homologação:** configurar a chave do hash no Worker de cada ambiente; revisar com uma empresa real a lista e o texto dos avisos; decidir o prazo de guarda das ocorrências (`RETENCAO-DE-DADOS.md`, item 7).
- Conferir a interface no navegador contra o Api real (esta versão a provou por testes de componente e integração).
- Evoluções fora do pacote: criar a primeira definição e remover entrada do rascunho pela API; reabrir lacuna; agrupar variações por **aliases sugeridos** (nunca por similaridade automática); expurgo por prazo; o módulo Knowledge e a busca, quando a economia medida os justificar.
