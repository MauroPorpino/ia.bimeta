# ADR 0017 — Etapa 6a: fatia mínima de IA para o MVP (AiGateway e Agents)

- **Status:** **Aceito (03/11/2026)**, com o escopo exato do registro de aprovação abaixo. Criado em 03/11/2026 como Proposto; as diretrizes de produto (seção A.1) foram aprovadas na mesma data, e a aprovação arquitetural, incluindo DP-12 e DP-13, foi concedida em seguida. **A aprovação não autoriza criar os projetos nem iniciar a implementação:** cada fatia depende de plano aprovado e de autorização expressa. Nada deste ADR está implementado.
- **Registro de aprovação (03/11/2026, decisor):** aprovados arquiteturalmente, incluindo **DP-12** e **DP-13**: (1) a antecipação de AiGateway e Agents para a **etapa 6a**, em paralelo à etapa 5; (2) OpenAI e Anthropic como provedores iniciais; (3) o contrato comum `IAiProvider`, independente dos provedores; (4) a configuração de provedor e modelo por tenant; (5) credenciais BYOK criptografadas e isoladas por tenant, com solução mínima própria no AiGateway; (6) instruções e FAQ publicados por tenant; (7) o registro de consumo, custo estimado, latência e erros; (8) os limites de utilização; (9) o processamento assíncrono dos turnos de IA; (10) a integração com o motor de turnos existente no Conversations; (11) a transferência para atendimento humano pelo ContactCenter; (12) a proteção contra respostas de IA depois de o humano assumir; (13) a ausência de fallback automático entre provedores; (14) a ausência de Knowledge, Capabilities, Workflows e integração com ERP no MVP; e (15) o **planejamento** dos projetos mínimos da seção B (AiGateway, Agents, adaptadores OpenAI e Anthropic, `ContactCenter.Contracts` e testes). **Fora do escopo aprovado:** a **criação** desses projetos; a recomendação de modelo do Anexo A.3 (recomendação, não escolha); as migrações 15 e 16 (só planejadas, nenhuma criada nem aplicada, DP-07); e as Pendências (verificação jurídica, aviso de IA, valores iniciais).
- **Data:** 03/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` v0.4.7 (seções 1, 3, 6, 7, 10, 11 e 12), `docs/architecture/CONVERSATION-LIFECYCLE.md` (seções 4, 10, 13 e 14), `docs/architecture/module-dependencies.json`, `docs/architecture/ETAPA-5.md`, `ARCHITECTURE.md` (seções 5, 6 e 11), ADRs 0005, 0006, 0007, 0009, 0010 e 0011, `CLAUDE.md`
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**: `Agents → {AiGateway, Conversations, ContactCenter (só RequestHandoff), Identity, Tenancy, Audit}` e `AiGateway → {Identity, Tenancy, Audit}` já existem); o ADR 0007 (cercas de turno, resposta defasada **sempre rejeitada**, sem `StaleReplyMode`); os ADRs 0009 e 0010; o motor de turnos, a sessão `AiAutonomous` e `RequestHandoff` já implementados, que **não são recriados**.
- **Registra:** (a) a **antecipação** de AiGateway e Agents, que passam da etapa 6 para a etapa **6a**, em paralelo à etapa 5, limitada ao atendimento inicial por IA; (b) a criação de `ContactCenter.Contracts`, porque o `Agents` é o primeiro consumidor (PRJ-2); (c) os adaptadores de IA já previstos no `BOUNDARIES.md` §7 (`IAiProvider`); (d) que instruções e FAQ do MVP são **configuração do `Agents`**, e não o Knowledge Engine; (e) o resultado da verificação da documentação oficial de OpenAI e Anthropic (Anexo A).

## Contexto

O MVP exige a IA como primeiro atendente do WhatsApp: Cliente → WhatsApp → IA → resolução automática ou transferência a humano → React → encerramento. O motor de turnos do `Conversations` (`BeginTurn`, `RegisterAiReply`, `EndTurn`, `CancelTurn`, `RaiseSessionAttention`), a sessão `AiAutonomous`, o evento `InboundMessageRegisteredV1` e o `RequestHandoffAsync` do `ContactCenter` existem, mas **nenhum código os chama**: não existem `Agents`, `AiGateway` nem adaptadores de IA. O `ARCHITECTURE.md` §11 (item 6) e o `BOUNDARIES.md` §12.12 colocavam esses módulos na etapa 6, depois do WhatsApp. O decisor decidiu que o MVP contempla **OpenAI e Anthropic**, BYOK como primeira modalidade, e que a fatia contém só o necessário ao atendimento inicial por IA.

## Decisão

### A. Faseamento e diretrizes de produto

1. **Diretrizes de produto aprovadas (03/11/2026):** OpenAI e Anthropic como provedores iniciais; contrato comum `IAiProvider`; provedor e modelo configuráveis por tenant; BYOK como primeira modalidade operacional; previsão de credenciais gerenciadas pela plataforma, **sem faturamento centralizado no MVP**; instruções e FAQ publicados por tenant; resposta automática ou transferência para humano; respeito às cercas e aos estados do motor de turnos existente; registro de consumo, custo estimado, latência e erros; limites básicos por tenant; **ausência de fallback automático entre provedores**; **nenhum treinamento de modelos com dados de clientes**.
2. A etapa 6 divide-se em **6a** (este ADR: AiGateway e Agents mínimos, em paralelo à etapa 5) e **6b** (Knowledge, Capabilities, Workflows, integrações com ERP, voz, imagem e agentes avançados, **fora do MVP e sem data**). A 6a contém **apenas** o necessário ao atendimento inicial por IA.
3. **Fora da 6a:** execução de operações no ERP, agentes autônomos complexos, workflows de várias ferramentas, interpretação de imagens e documentos, voz em tempo real, treinamento ou ajuste de modelos, faturamento centralizado, fallback automático entre provedores, `AiSupervised` e política `Queue` de resposta defasada (ADR 0007).

### B. Projetos mínimos (DP-13) e fronteiras

A criação dos projetos abaixo é planejamento: **depende da autorização de implementação** (BOUNDARIES §10). A matriz e o grafo de projetos não mudam além dos projetos previstos na seção 1 do `BOUNDARIES.md`.

| Projeto | Conteúdo | Observação |
|---|---|---|
| `IaBimeta.AiGateway.Contracts` | `IAiProvider` (porta de adaptador, `Ports`), pedido, resultado e erros normalizados, consulta e comando de configuração de IA do tenant | primeiro consumidor: o `Agents` e os adaptadores |
| `IaBimeta.AiGateway.Core` | casos de uso: seleção de provedor e modelo, credenciais, limites, registro de consumo, política de erro; Domain e Application em pastas | não conhece provedor pelo nome |
| `IaBimeta.AiGateway.Infrastructure` | `DbContext` e esquema `ai_gateway`, migrations, cifra das credenciais, orquestração das chamadas | só os hosts a referenciam |
| `IaBimeta.AiGateway.Presentation` | endpoints de configuração e de consumo do tenant | sem `tenantId` na entrada |
| `IaBimeta.Agents.Core` | `AiTurn`, agrupamento, montagem do prompt, validação da decisão, política de mídia (MED-2), retentativa, pedido de handoff | sem `Contracts`: nenhum consumidor (PRJ-2) |
| `IaBimeta.Agents.Infrastructure` | `DbContext` e esquema `agents`, inbox do módulo, handlers de evento, executor de turnos (hospedado no Worker), reconciliador | |
| `IaBimeta.Agents.Presentation` | endpoints de instruções e FAQ por tenant (rascunho, publicação, versão) | |
| `IaBimeta.Adapters.Ai.OpenAI` e `IaBimeta.Adapters.Ai.Anthropic` | um adaptador por provedor, `HttpClient` direto, **sem SDK** | referenciam só `AiGateway.Contracts` e o `SharedKernel` (DEP-2) |
| `IaBimeta.ContactCenter.Contracts` | só `RequestHandoff` (comando e resultado) | nasce porque o `Agents` é o primeiro consumidor; a fachada chama o `QueueItemService.RequestHandoffAsync` existente, **sem regra nova**; o teste de arquitetura que afirma a ausência do projeto passa a afirmar que **só o `Agents`** o referencia |
| projetos de teste | um por módulo novo e por adaptador, nos locais do `BOUNDARIES.md` §1, mais os testes de integração (PostgreSQL, porta 5433) e de arquitetura | dublês de provedor de IA com latência e falha injetáveis |

Os hosts compõem: o `Api` hospeda os endpoints, e o `Worker` hospeda os consumidores de evento e o executor de turnos. Adaptadores só entram nos hosts (DEP-3). Nenhum pacote novo: `HttpClient` e `System.Text.Json` da plataforma. **SDKs oficiais não são adotados** (exigiriam aprovação).

### C. Contrato comum do AiGateway

1. **`IAiProvider.CompleteAsync`** (BOUNDARIES §7), um por provedor. O contrato é **independente de provedor**: o `Agents` e o `AiGateway.Core` não contêm nome de provedor (regra de arquitetura com conjunto não vazio).
2. **Pedido:** modelo, instrução de sistema, mensagens (usuário e assistente, **só texto**), teto de tokens de saída, tempo limite, `AiRequestId` (um por chamada), correlação opaca (`TurnId`), modo de saída (texto ou **decisão estruturada**) e a credencial em memória, resolvida na hora. **O contrato não tem `temperature`, `top_p` nem controle de raciocínio por nome de provedor**: parâmetros de sustentação de qualidade ficam por **perfil de modelo** na configuração do adaptador (Anexo A: a Anthropic rejeita `temperature` na versão atual da API, e a OpenAI exige removê-la com raciocínio ligado).
3. **Resultado:** texto ou decisão estruturada, motivo de parada normalizado (`Completed`, `Truncated`, `Refused`), uso normalizado (tokens de entrada total, em cache, de escrita de cache, de saída incluindo raciocínio, e de raciocínio quando informado), latência e o identificador da requisição do provedor.
4. **Erros normalizados:** `Transient` (timeout, 5xx, sobrecarga, limite de taxa com ou sem espera sugerida), `InvalidCredentials` (401 e 403), `QuotaOrBilling` (cobrança, gasto e crédito esgotados: **não retentável**), `InvalidRequest`, `ContextTooLong`, `Refused` e `TenantLimitExceeded` (limite interno). Cada adaptador mapeia os códigos do provedor para essa lista; o `Agents` só conhece a lista.
5. **Decisão estruturada.** O modelo **sugere**: `reply` (texto ao cliente) ou `handoff` (motivo de uma lista fechada). O schema é o **mínimo comum** aos dois provedores: objeto na raiz, todos os campos obrigatórios, sem campos opcionais, sem `anyOf` na raiz e sem `minimum`, `maximum`, `minLength` nem `maxLength`. O `Agents` **valida** a resposta contra a própria lista fechada de ações, falha fechada (saída inválida conta como falha da tentativa) e executa a ação pelos contratos. Nenhuma capacidade externa é executada pelo modelo (CLAUDE.md).
6. **Idempotência.** Nenhum dos dois provedores oferece chave de idempotência na geração de texto (Anexo A). Gerar texto não tem efeito externo; a duplicidade de **resposta** é impedida pelo `TurnId` e pela cerca do `Conversations` (ADR 0007), e uma chamada repetida só custa tokens.

### D. Configuração por tenant

1. **Provedor e modelo** por tenant, entre os **modelos suportados** de um catálogo em configuração (identificador, provedor, perfil, janela de contexto, preço por tipo de token e estado). Modelo fora do catálogo é recusado. A escolha de modelo **não é presumida**: a recomendação inicial está no Anexo A.3 e a escolha do tenant é dele.
2. **Origem da credencial:** `Byok` (única implementada) e `PlatformManaged` (**reservada**: o valor existe, a configuração é recusada como não implementada e não há faturamento).
3. **Permissão:** `TenantSettingsManage` (escopo do tenant). Nenhuma permissão nova. Só quem a tem configura provedor, modelo, credencial, instruções e FAQ; toda alteração é auditada.
4. **Seleção sem fallback.** Falha do provedor do tenant **não** muda de provedor. O `Agents` aplica a política de falha (retentativa e handoff).

### E. Proteção das credenciais (solução mínima, isolada no AiGateway)

1. As credenciais BYOK ficam no esquema do `AiGateway`, cifradas com **AES-256-GCM** (biblioteca da plataforma), nonce aleatório por cifra e **dado autenticado adicional** com tenant, identificador da credencial e papel, de modo que o texto cifrado de um tenant **não abre** sob outro tenant, outra referência ou outro papel. Formato versionado, chaves de cifra **só no ambiente** (variável de ambiente, user-secrets em desenvolvimento, depois cofre), com chave ativa e chaves antigas para rotação, sob uma seção de configuração **própria** do `AiGateway`.
2. A chave **nunca** é devolvida por API, nunca vai a log, frontend, mensagem de erro ou repositório: a API expõe só metadados (existe, data, fingerprint não reversível). É resolvida **na hora do uso**, mantida em memória pelo menor tempo possível e zerada. Falha sem chave ativa configurada **falha fechada**.
3. **Não há extração do `AesGcmSecretProtector` do `Channels` para o `SharedKernel`** nem alteração do `Channels`: a implementação do `AiGateway` é mínima e própria, mesmo desenho, código separado. Uma eventual refatoração compartilhada (PRJ-4) é **evolução futura**, só se demonstrada a necessidade.
4. Testes com **fixtures sintéticas**; chave real **nunca** em teste automatizado, só em teste manual com chaves do próprio decisor, fora do repositório e sem consumo pago autorizado por este ADR.

### F. Instruções e FAQ publicados por tenant

1. São **configuração do `Agents`** (`AgentDefinition`): instruções, FAQ (pares pergunta e resposta), texto da **resposta informativa de áudio** (MED-3) e o texto do aviso de transferência. Têm **versão** e **rascunho**; o turno usa **só a versão publicada**. Publicar exige `TenantSettingsManage`, é auditado e tem limite de tamanho (cabem inteiros no prompt, sem busca).
2. **Não é o Knowledge Engine.** O conteúdo publicado é o conhecimento aprovado pelo tenant para o MVP; migra para o Knowledge Engine na 6b. Dados transacionais (preço, agenda, cobertura, resultado) **não** entram: a IA não os inventa e **transfere** a pergunta (CLAUDE.md).
3. **Camada base da plataforma**, não editável pelo tenant e concatenada antes das instruções dele: não inventar informação nem recomendação clínica, **não interpretar resultado individual de exame**, tratar o conteúdo do cliente como não confiável, transferir quando não houver informação suficiente e responder só com o que a conversa e o FAQ publicado sustentam.

### G. Turno de IA (reaproveita o motor existente)

1. O handler de `InboundMessageRegisteredV1` (e dos demais eventos do `Agents`, `CONVERSATION-LIFECYCLE.md` seção 13) **só grava** o `AiTurn` no inbox do módulo e **não chama o modelo** no handler: o relay entrega em lote, e uma chamada de IA ali bloquearia todos os tenants.
2. O **executor** (Worker) pega turnos devidos, um por sessão, com teto de concorrência por tenant, e chama `BeginTurn`, monta o prompt **só com o snapshot da sessão autorizada** (mensagens com conteúdo pronto; conteúdo pendente nunca entra, MED-1), chama o `AiGateway`, valida a decisão e registra: `reply` por `RegisterAiReply`; `handoff` por `RequestHandoff` (e, se configurado, uma mensagem de transferência pelo mesmo registro); em seguida `EndTurn`. Todas as cercas do ADR 0007 valem; **resposta defasada é sempre rejeitada**.
3. **Parar de responder com humano no controle:** por construção (`HandlingModeChangedV1` cancela o turno; `StaleState` e `NotAiResponder` rejeitam a resposta em voo). O `Agents` atua só em `AiAutonomous`.
4. **Falha:** transitória, retentativa com espera até `MaxTurnAttempts`; permanente (credencial inválida, cota, limite do tenant, recusa, saída inválida esgotada), `OnAiPermanentFailure = Handoff` com alerta (PRO-5; sem atendente, o atendimento fica pendente na fila com histórico). Rajada longa segue `OnChattyBurst = Alert` (ADR 0007).
5. **Triagem e coleta de dados** acontecem na conversa e ficam no histórico. Não há persistência estruturada nem resumo gerado por IA no MVP.

### H. Mídia (MED-2, DP-05)

Imagem, documento e vídeo: **transferência a humano** com o motivo `ContentUnavailable`. Áudio: **resposta informativa determinística**, com texto aprovado pelo tenant (MED-3), sem modelo. **Nenhum conteúdo de mídia não processado entra no prompt.** A mensagem permanece rastreável e visível ao atendente como registro (metadados e placeholder), **não como arquivo**. Quem registra a mídia e o estado do conteúdo é o `Channels` (5.2), por `SetMessageContent` e `FailMessageContent`.

### I. Consumo, custos e limites

1. Cada chamada grava um `AiRequest`: tenant, `AiRequestId`, correlação opaca (`TurnId`), provedor, modelo, tokens por tipo, **custo estimado** (a partir do catálogo de preços em configuração; é estimativa, não fatura), latência, resultado e tipo de erro, e o identificador da requisição do provedor. **Sem prompt e sem resposta** no registro. Consulta de consumo por tenant, por período, para quem tem `TenantSettingsManage`.
2. **Limites básicos por tenant**, em configuração e sem redeploy: chamadas simultâneas, chamadas e tokens por período, mensagens de entrada por turno e tokens de saída por resposta. Estourado, o turno termina como falha permanente (`TenantLimitExceeded`, handoff e alerta). Os valores iniciais são referência a calibrar.

### J. Privacidade e uso

1. **Nenhum treinamento** de modelos com dados de clientes: é requisito contratual do tenant com o provedor (BYOK) e restrição do produto; o AiGateway não envia a terceiros nada além do texto da sessão autorizada e dos prompts do tenant. No adaptador da OpenAI, o armazenamento de estado da resposta fica **desligado** (Anexo A).
2. Documentos e respostas externas são conteúdo não confiável (proteção contra prompt injection). A 6a não executa capacidade externa, o que reduz a superfície.
3. A política de uso da Anthropic classifica saúde e acesso a serviços médicos como **alto risco** e exige aviso de IA ao consumidor (Anexo A). Consequência de produto: a camada base proíbe a interpretação individual de resultado, e o **aviso de que o cliente fala com IA** é requisito a definir antes da operação com clientes reais (Pendências).

## Alternativas descartadas

- **Aguardar a etapa 6 inteira.** Sem a IA o fluxo do MVP não fecha: o motor de turnos existe sem chamador.
- **Antecipar Knowledge, Capabilities e Workflows.** Fora do escopo do atendimento inicial; o FAQ por tenant basta no MVP.
- **FAQ no Knowledge Engine.** Cria um módulo antes do tempo (matriz, esquema, aprovação, busca); o FAQ do MVP cabe no prompt.
- **Extrair o `AesGcmSecretProtector` para o `SharedKernel`.** Altera o `Channels` já entregue e antecipa uma abstração por reutilização; decidido: solução mínima e isolada.
- **SDKs oficiais.** Pacote novo e superfície maior; `HttpClient` direto cobre o texto.
- **Fallback automático entre provedores.** Fora do MVP por decisão; mistura contratos, custos e dados de dois provedores.
- **Chamar o modelo dentro do handler do evento.** Bloqueia o relay e todos os tenants.
- **Uso forçado de ferramenta (tool use) como mecanismo único da decisão.** A documentação da Anthropic o recusa em parte dos modelos; a saída estruturada nativa é comum aos dois provedores.
- **Guardar prompts e respostas no `AiRequest`.** Duplicaria dado de saúde fora do `Conversations`, sem necessidade.

## Consequências

- O motor de turnos passa a ter chamador, e o MVP passa a ter o fluxo completo de IA. A etapa 6 divide-se em 6a e 6b; o `ARCHITECTURE.md` §11 e o `BOUNDARIES.md` §1 e §12.12 registram a divisão.
- O `ContactCenter` ganha um projeto `Contracts` (só `RequestHandoff`) e um teste de arquitetura atualizado.
- Dois adaptadores aumentam o esforço de integração e de teste (estimativa de planejamento: +1 semana na trilha de IA e +0,5 na estabilização), compensados por um contrato único e uma suíte de contrato compartilhada.
- O `AiGateway` tem implementação própria da cifra de credenciais, com duplicação consciente do desenho do `Channels`.
- O FAQ publicado no `Agents` terá de migrar para o Knowledge Engine na 6b.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Saída estruturada diferente entre provedores | schema mínimo comum; adaptador mapeia o mecanismo nativo; validação no `Agents` com falha fechada; suíte de contrato nos dois adaptadores e no fake |
| Raciocínio ou texto indevido na resposta (Anthropic) e tokens de raciocínio cobrados como saída (os dois) | perfil de modelo por adaptador (esforço baixo, teto de saída com folga); seleção do bloco de texto por tipo; teste manual com conversas reais |
| Latência sem dado oficial | medir com conversas reais; o executor tem `TurnMaxDuration`; relay (2 s) e agrupamento (3 s) somam-se à chamada |
| Chave do tenant esgotada, revogada ou com limite de gasto | `QuotaOrBilling` e `InvalidCredentials` são permanentes: handoff e alerta, sem retentativa |
| Política de uso e de terceiros do provedor mudar ou ser mal enquadrada | verificação jurídica antes da operação com clientes reais (Pendências); nada bloqueia a versão para testes |
| Conteúdo clínico sem fonte aprovada | camada base, FAQ publicado, transferência de preço, agenda e resultado; revisão humana do FAQ publicado |
| Custo imprevisto no BYOK | limites por tenant, consumo e custo estimado visíveis ao tenant |
| Prompt injection pelo cliente | conteúdo do cliente marcado como não confiável; ações em lista fechada; nenhuma capacidade externa |

## Critérios de aceitação (6a)

Todos com PostgreSQL real (porta 5433), dublê de provedor de IA e HTTP simulado, sem credencial real:

1. **Contrato:** uma única suíte roda contra o dublê e contra os dois adaptadores (corpo e cabeçalhos corretos, normalização de uso, erros e motivos de parada); provedor novo exige só adaptador e catálogo (teste de nome de provedor ausente do `Agents` e do `AiGateway.Core`).
2. **Seleção:** tenant A com OpenAI chama só o adaptador da OpenAI; tenant B com Anthropic, só o da Anthropic; modelo fora do catálogo é recusado; `PlatformManaged` é recusado.
3. **Credenciais:** o texto cifrado do tenant A não abre sob o tenant B; a API nunca devolve a chave; canário ausente de log, resposta e erro; sem chave ativa, falha fechada.
4. **Contexto:** teste Mauro/Pedro com IA (a requisição de Mauro só contém o contexto do #1001; a resposta vai só ao #1001).
5. **Cercas:** resposta com `StateVersion`, época ou `basedOnInboundSeq` defasados é rejeitada e nunca enviada; humano assume com chamada em voo: nada é registrado; um turno por sessão; processo morto no meio do turno gera uma só resposta.
6. **Decisão:** ação fora da lista, JSON inválido ou recusa não executam nada e seguem a política de falha; `handoff` cria o item na fila, e sem atendente o atendimento fica pendente com histórico.
7. **Falhas:** transitório com espera e `MaxTurnAttempts`; credencial inválida e cota sem retentativa; limite do tenant termina o turno com handoff e alerta; nenhum fallback entre provedores.
8. **Mídia:** imagem, documento e vídeo transferem com `ContentUnavailable`; áudio recebe a resposta informativa; nenhum conteúdo pendente entra no prompt (teste que inspeciona a requisição).
9. **Consumo:** cada chamada grava `AiRequest` com tokens, custo estimado, latência e resultado, sem prompt nem resposta; o limite de simultaneidade por tenant nunca é excedido.
10. **Isolamento:** canários entre tenants em turnos, credenciais, configuração, FAQ e consumo; evento sem tenant falha fechado.
11. **Arquitetura:** matriz e grafo inalterados (ARQ-06); `ContactCenter.Contracts` só com `RequestHandoff` e referenciado só pelo `Agents`; o `Agents` não referencia `OutboundMessageFailedV1` (ARQ-24); adaptadores só referenciam `AiGateway.Contracts` e o `SharedKernel`.
12. **Geral:** suíte completa e mutações da etapa 4 verdes; nenhuma garantia aprovada enfraquecida.

**Manual, fora da suíte automática:** o mesmo fluxo com chave real de cada provedor, primeiro pelo simulador (marco de 18/12/2026) e depois pelo WhatsApp (marco de 22/01/2027). Exige chaves do próprio decisor e **autorização expressa de consumo pago**, que este ADR não concede.

## Migrações propostas (nenhuma criada nem aplicada)

Aditivas, com `Down` e plano de rollback; **só aplicadas com autorização expressa** (DP-07). Seguem a numeração de `ETAPA-5.md` §7.

| # | Módulo | Conteúdo | Fatia | Risco |
|---|---|---|---|---|
| 15 | AiGateway | esquema `ai_gateway`: configuração por tenant, credencial cifrada, `ai_requests`, contadores e limites | 6a | baixo (esquema novo) |
| 16 | Agents | esquema `agents`: `agent_definitions`, versões publicadas e rascunho, `ai_turns` (índice único parcial de turno não terminal por sessão), inbox do módulo | 6a | médio |

## Pendências

- **DP-12** (aprovação arquitetural deste ADR) e **DP-13** (planejamento dos projetos da seção B): **aprovados em 03/11/2026**. A criação efetiva dos projetos, das migrações 15 e 16 e de qualquer código depende da **autorização de implementação** de cada fatia (C1 e C2 primeiro), com plano aprovado.
- **Verificação jurídica antes da operação com clientes reais** (não bloqueia a versão para testes): política de uso da OpenAI (segundo relatos de imprensa, atualizada em 21/10/2026 e vigente em 20/11/2026, sobre "roteadores de modelo" e "gateways de IA", **não confirmada** em fonte oficial), política da Anthropic (vigência 12/11/2026, posterior à consulta), cláusulas de BYOK e de revenda, e termos da Meta sobre provedores de IA (cláusula 4.7; ADR 0011, A.2).
- **Aviso de IA ao cliente** (texto e momento): requisito de produto a definir antes da operação com clientes reais.
- **Retenção de dados na Anthropic:** duas páginas oficiais divergem (Anexo A.2, item 9); confirmar com o provedor antes de prometer retenção ao tenant.
- **Valores iniciais** de agrupamento, tentativas, tetos de saída e limites por tenant: referência de `CONVERSATION-LIFECYCLE.md` §14, a calibrar.
- **Perfil de cada modelo** (esforço, teto de saída, cache do prompt de sistema): decidido no plano da fatia, com teste.
- **`ADR 0008`** (pendência da REC-2 resolvida pelo ADR 0011, A.1) e **ADRs 0013 e 0015** (referências a status): ajuste textual **não feito** nesta rodada; depende de autorização.

## Anexo A. Verificação da documentação oficial dos provedores de IA (03/11/2026)

**Método e limites.** Leitura somente de páginas oficiais, em 03/11/2026, sem chave, sem conta e sem chamada paga. A leitura foi feita por agentes de pesquisa, e os **identificadores e os preços dos quatro modelos recomendados foram reconferidos diretamente** nas páginas de preços. As páginas `openai.com/policies` e correlatas responderam 403 e **não foram lidas** na fonte primária. Latência: nenhum dos dois provedores publica número oficial. `D` = `https://developers.openai.com/api/docs`; `R` = `https://developers.openai.com/api/reference`; `P` = `https://platform.claude.com/docs/en`. Confiança: C = confirmado em página oficial; I = inferido; N = não encontrado.

### A.1 OpenAI

| # | Item | Resultado | Fonte | Conf. |
|---|---|---|---|---|
| 1 | Autenticação | `Authorization: Bearer <chave>`. `OpenAI-Organization` e `OpenAI-Project` são opcionais. Sem outro cabeçalho obrigatório | `R/overview`, `D/guides/error-codes` | C |
| 2 | Endpoint | A **Responses API** (`POST /v1/responses`) é recomendada para novos projetos; Chat Completions segue suportada, sem data de desligamento. `instructions`, `input` (string ou lista), `max_output_tokens` (mínimo 16, inclui raciocínio), `status` e `incomplete_details.reason` (`max_output_tokens` ou `content_filter`). Com raciocínio ligado nos modelos GPT-6, `temperature` e `top_p` devem ser removidos | `D/guides/migrate-to-responses`, `D/guides/text`, `D/guides/reasoning` | C |
| 3 | Saída estruturada | `text.format = {type: "json_schema", name, schema, strict: true}`. Raiz objeto, sem `anyOf` na raiz, todos os campos em `required`, `additionalProperties: false`, máximo de 5.000 propriedades e 10 níveis. Recusa como item `type: "refusal"`; truncamento como `status: "incomplete"`. Primeira requisição de um schema novo tem latência extra | `D/guides/structured-outputs` | C |
| 4 | Erros | 400 (contexto, parâmetro), 401, 403 (país), 413, 429 (limite: retentável após `Retry-After`; **cota e cobrança**: `credit_balance_exhausted`, `organization_spend_limit_exceeded`, `project_spend_limit_exceeded`, `organization_usage_limit_exceeded`: **não retentável**; usar `error.code`, pois `insufficient_quota` pode vir no tipo), 500 e 503 (retentáveis). Formato do corpo: `error.code`, `error.type`, `error.param` (esquema completo não está na página). Recusa por política em nível HTTP: N | `D/guides/error-codes`, `D/guides/rate-limits` | C, com lacunas |
| 5 | Limites e retentativa | RPM, TPM, RPD e TPD, por organização e projeto, variam por modelo. Níveis automáticos por crédito (Free, Build, Launch, Grow). Recomendação oficial: `Retry-After`, ou espera exponencial com jitter, limitando tentativas e tempo total, sem retentar cota. Falhas contam no limite. Timeout numérico: N. Como é BYOK, **vale o nível de cada tenant** | `D/guides/rate-limits` | C |
| 6 | Consumo e identificadores | `usage`: `input_tokens`, `input_tokens_details.cached_tokens`, `output_tokens`, `output_tokens_details.reasoning_tokens`, `total_tokens`; raciocínio cobrado como saída. `x-request-id` na resposta; `X-Client-Request-Id` opcional na requisição (ASCII, até 512 caracteres). **Chave de idempotência na geração: N** | `R/overview`, `D/guides/prompt-caching` | C, exceto idempotência |
| 7 | Modelos e preços (por 1M tokens, padrão; **reconferidos na página de preços**) | `gpt-6-luna`: entrada 0,10, em cache 0,01, saída 0,50. `gpt-6.1-terra`: 1 / 0,05 / 5. `gpt-6.1-sol`: 2 / 0,10 / 10. Contexto de 1,05M e saída máxima de 128K nos quatro modelos da linha. Luna e Terra aceitam `reasoning.effort` de `none` a `max`; Sol não aceita `none`. Sem aviso de descontinuação nesses modelos | `D/pricing`, `D/models`, `D/deprecations` | C |
| 8 | Dados da API | Sem treino com dados da API por padrão. Logs de abuso por até 30 dias. Retenção zero e monitoramento modificado exigem aprovação. Na Responses, `store` omitido ou verdadeiro retém o estado por pelo menos 30 dias: **usar `store: false`**. DPA: indício secundário, **não lido** | `D/guides/your-data` | C, exceto DPA |
| 9 | Políticas de uso (saúde, aviso de IA) | **Não lidas na fonte primária** (403). Fontes secundárias: proibido aconselhamento que exige licença sem profissional licenciado e automação de ação de alto risco sem revisão humana. Boas práticas oficiais: pessoa no ciclo em alto risco e `safety_identifier` por usuário (não obrigatório) | `D/guides/safety-best-practices` (oficial); demais, secundárias | N na fonte primária |
| 10 | BYOK e terceiros | **Não confirmado** em fonte oficial. Fontes secundárias: o cliente não pode comprar, vender ou transferir chaves de API e responde pelos usuários finais; relatos de imprensa sobre política atualizada em 21/10/2026, vigente em 20/11/2026, com aprovação prévia para "roteadores de modelo" e "gateways de IA" que dão acesso direto a terceiros (BYOK não mencionado) | secundárias | N |

### A.2 Anthropic

| # | Item | Resultado | Fonte | Conf. |
|---|---|---|---|---|
| 1 | Autenticação | Obrigatórios: `anthropic-version` e `content-type`. Chave por `Authorization: Bearer` (`x-api-key` segue como alternativa legada). Versão atual da API: `2026-10-21`. Nessa versão, `temperature`, `top_p` e `top_k` retornam 400 | `P/api/overview`, `P/api/versioning` | C |
| 2 | Messages API | `POST https://api.anthropic.com/v1/messages`; `model`, `max_tokens` e `messages` obrigatórios; `system` em string ou blocos; API sem estado. Turnos consecutivos do mesmo papel são fundidos; prefill é recusado; a última mensagem deve ser do usuário. `stop_reason`: `end_turn`, `max_tokens`, `refusal`, entre outros. **Raciocínio adaptativo vem ligado por padrão**, seus tokens contam em `max_tokens` e são cobrados como saída; selecionar o bloco de texto por tipo. Controle por `output_config.effort` | `P/api/messages/create`, `P/build-with-claude/effort` | C |
| 3 | Saída estruturada | Nativa, sem cabeçalho beta: `output_config.format = {type: "json_schema", schema}`; `additionalProperties: false` obrigatório; `anyOf`, `const` e `enum` funcionam; não funcionam recursão, `minimum`, `maximum`, `minLength`, `maxLength`. Recusa: HTTP 200 com `stop_reason: "refusal"` (tokens cobrados). Truncamento: `max_tokens`. Grammar em cache por 24 h. Uso forçado de ferramenta retorna 400 nos modelos Sonnet 5.5, Opus 5.5 e Fable 5.1 | `P/build-with-claude/structured-outputs`, `P/agents-and-tools/tool-use/define-tools` | C |
| 4 | Erros | 400, 401, 402 (`billing_error`), 403, 404, 409, 413, 429, 500, 504 e 529 (sobrecarga). Corpo `{type: "error", error: {type, message}, request_id}`. Limite de gasto do cliente: 400; teto mensal do nível: 429 com `enforced_spend_limit_reached` e **sem** `retry-after`: não retentar. Tabela oficial de "retentável": N; classificar 529 e 504 como retentáveis é inferência | `P/api/errors` | C; retentável: I |
| 5 | Limites e retentativa | RPM, ITPM e OTPM por classe de modelo e por organização dona da chave (no BYOK, de cada tenant, I). Balde de fichas. Haiku 5.5, Sonnet 5.5 e Opus 5.5 compartilham os limites por nível (Start, Build, Scale). Cabeçalhos `retry-after` e `anthropic-ratelimit-*`. Backoff do SDK: duas retentativas. Timeout numérico recomendado: N | `P/api/rate-limits` | C |
| 6 | Consumo | `usage`: `input_tokens` (após o último ponto de cache), `output_tokens` (inclui raciocínio), `cache_creation_input_tokens`, `cache_read_input_tokens`, detalhe de raciocínio. Entrada total = soma das três de entrada. Cabeçalho `request-id`. **Chave de idempotência: N** | `P/api/messages/create` | C, exceto idempotência |
| 7 | Cache de prompt | `cache_control: {type: "ephemeral"}`, até 4 pontos; mínimo de 512 tokens nos modelos 5.5; TTL de 5 min (renovado por uso) ou 1 h; escrita 1,25x (5 min) ou 2x (1 h); leitura a 0,1x no Haiku 5.5 e 0,05x no Sonnet 5.5. Mudar `effort` no topo ou `tool_choice` invalida o cache das mensagens | `P/build-with-claude/prompt-caching` | C |
| 8 | Modelos e preços (por 1M tokens; **reconferidos na página de preços**) | `claude-haiku-5-5`: entrada 0,10 (prompt até 100 mil tokens; 0,50 acima), saída 0,50 (2,50 acima), escrita de cache 0,125 (5 min) e 0,20 (1 h), leitura 0,01. `claude-sonnet-5-5`: 2 / 10, escrita 2,50 e 4, leitura 0,10. Contexto de 1M e saída máxima de 128K. Haiku 5.5 descrito como o mais rápido, Sonnet 5.5 como rápido. Retirada não antes de 07/10/2027 (Haiku 5.5) e 28/09/2027 (Sonnet 5.5). Todos suportam saída estruturada. Haiku 5.5 aceita desligar o raciocínio; Sonnet 5.5 não | `P/models/overview`, `P/about-claude/pricing`, `P/about-claude/model-deprecations` | C |
| 9 | Dados | Termos comerciais: a Anthropic não treina modelos com o conteúdo do cliente. DPA incorporado por referência (texto **não lido**). **Retenção diverge entre duas páginas oficiais:** a de dados da API diz que prompts e saídas não são retidos por padrão (exceção: modelos "cobertos", entre eles Fable 5.x, com 30 dias); a de privacidade diz que entradas e saídas da API são apagadas em até 30 dias. **Confirmar com a Anthropic.** Retenção zero por organização, via vendas. Brasil consta como país suportado. Residência de dados no Brasil: N | `anthropic.com/legal/commercial-terms`, `P/manage-claude/api-and-data-retention`, `privacy.claude.com` (artigo de retenção), `P/api/supported-regions` | C, com divergência |
| 10 | Política de uso | Saúde e acesso a serviços médicos são **alto risco**: exige revisão por pessoa qualificada e aviso claro de que houve IA. Interpretar sintomas ou resultados de exame de um indivíduo é "Medical"; informação geral e educativa fica fora. Chatbot ao consumidor deve informar que é IA, no mínimo no início da sessão. A página mostra vigência em 12/11/2026 (posterior à consulta) | `anthropic.com/legal/aup` | C |
| 11 | BYOK e revenda | Nenhuma cláusula explícita de BYOK em SaaS. Os termos permitem alimentar produtos para os usuários finais do cliente e **proíbem revender** o serviço sem aprovação; o cliente responde pela atividade da conta. Chave do tenant, faturada ao tenant, sem repasse de tokens pelo ia.bimeta, é **coerente por analogia (I)**; confirmar por escrito | termos comerciais | I |

### A.3 Recomendação objetiva de modelo inicial (não é decisão)

| | OpenAI | Anthropic |
|---|---|---|
| **Inicial** | `gpt-6-luna` na Responses API, `reasoning.effort: none`, `text.format` json_schema estrito, `store: false`, `max_output_tokens` baixo, `safety_identifier` por usuário (hash) | `claude-haiku-5-5` na Messages API, `output_config.format` json_schema, esforço baixo, raciocínio adaptativo padrão, `max_tokens` com folga para o raciocínio, cache do prompt de sistema (TTL de 5 min ou 1 h) |
| **Por quê** | o mais barato (0,10 de entrada e 0,50 de saída) e descrito pela OpenAI como o mais rápido e econômico; aceita `none`, o que evita tokens de raciocínio, latência e custo | o mais barato (0,10 e 0,50) e documentado como o mais rápido; entrada em cache a 0,01 favorece o prompt estático de instruções e FAQ por tenant |
| **Alternativa de qualidade maior** | `gpt-6.1-terra` com `none` (1 e 5) | `claude-sonnet-5-5` (2 e 10); não aceita raciocínio desligado |
| **Custo estimado por resposta** | ordem de US$ 0,0005 a 0,0013 (prompt de cerca de 12 mil tokens, resposta de cerca de 150) | ordem de US$ 0,0005 (com cache) a 0,0014 (sem cache) |

Os custos são **estimativas dos pesquisadores, não oficiais**, e dependem de acerto de cache que não foi medido. A recomendação vale para o atendimento por WhatsApp (texto curto, FAQ no prompt, decisão estruturada, latência e custo importam) e exige validação própria de **qualidade, latência e comportamento do handoff** com conversas reais; a avaliação clínica não está coberta por nenhum dado oficial. Modelos de nível superior (Opus, Fable, Astra) são desproporcionais para esse caso e não foram avaliados.

### A.4 Lacunas

Latência oficial (nenhum provedor publica); timeout numérico recomendado (nenhum); idempotência de geração (nenhum tem); texto dos DPAs (não lidos); políticas de uso e de terceiros da OpenAI (fonte primária inacessível); divergência de retenção da Anthropic; enquadramento de BYOK nos dois provedores; comportamento de `temperature` com `reasoning.effort: none` na OpenAI (não documentado). Nenhuma dessas lacunas bloqueia a versão para testes; as de política e de retenção devem ser fechadas antes da operação com clientes reais.
