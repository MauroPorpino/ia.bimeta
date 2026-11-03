# ADR 0016 — WebChat nativo e continuidade de atendimento entre canais (`ChannelHandoff`)

- **Status:** **Aceito parcialmente (03/11/2026): somente a seção M (encerramento do atendimento) e os critérios AC-ENC01 a AC-ENC09.** Todo o restante deste ADR (WebChat, `ChannelHandoff`, sessão satélite, convite, recuperação, continuidade no ContactCenter, custos, indicadores e as demais decisões DP-WC) **permanece Proposto**. Nada deste ADR está implementado. **A aprovação conceitual das decisões DP-WC não autoriza implementação**: a implementação de cada fatia depende da aprovação formal dos ADRs (DP-01) e do plano da própria fatia.
- **Registro de aprovação (03/11/2026, decisor):** aprovados a **seção M** e os critérios **AC-ENC01 a AC-ENC09**, referentes ao encerramento do atendimento (`CloseAttendance`, 5.1b), que dependem da 9ª operação do ADR 0009 (item 10, aceito sem a extensão do satélite); o planejamento da migração 14 (ContactCenter, valor `Close`) acompanha a seção M (DP-07, sem execução). O critério AC-ENC07 foi ajustado para distinguir o **total de operações previsto** (nove, com a 8ª Proposta no planejamento futuro) do **total implementado no MVP** (oito). **Permanecem Propostas** todas as demais seções e critérios, as migrações 11 a 13 e a extensão do satélite no encerramento (`SatelliteDecision`, `SatelliteAckThroughSeq`, 5.2w). A aprovação de uma parte não aprova o restante.
- **Data:** 03/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` (seções 4, 5, 6 e 12), `docs/architecture/ETAPA-5.md`, ADRs 0008 a 0015, decisões conceituais D-WC01 a D-WC09, `CLAUDE.md`
- **Depende de:** a **segunda emenda ao ADR 0009** (9ª operação delegada, `CloseSession`; item 10 daquele ADR), **aprovada em 03/11/2026** sem a extensão do satélite, que é pré-requisito do encerramento do atendimento (5.1b) e, portanto, do `Completed` (5.2w).
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**), as decisões C1 a C7, as garantias G1 e G2 do e-mail, as garantias da etapa 4 (`HandlingPeriod`, `PlacementEpoch`, `ScopeProof`, cerca, idempotência, obrigação de ressincronização), os ADRs 0010 a 0015 e o isolamento entre tenants. Em 03/11/2026, o ADR 0009 foi aceito parcialmente e o ADR 0010, aceito (ver o status de cada um).
- **Registra:** um canal nativo (de primeira parte) no `Channels`; um agregado de continuidade (`ChannelHandoff`) e a sessão satélite no `Conversations`; atributos de continuidade no item de fila do `ContactCenter`; a extensão aditiva `MessageAuthor.System`; o razão genérico de custos de mensagens; e a **dependência** da 9ª operação do gateway.

## Contexto

O WhatsApp tem custo relevante por mensagem para tenants com grande volume. O produto oferece um canal próprio: o cliente inicia pelo WhatsApp, recebe um convite com link seguro, continua no navegador (sem instalação obrigatória), e o atendimento prossegue no ContactCenter existente. **Não se presume redirecionamento automático do aplicativo WhatsApp nem retorno automático do cliente.**

O vocabulário atual importa. O `ARCHITECTURE.md` (§4.1) chama `ServiceSession` de "atendimento lógico, potencialmente multicanal" e `Conversation` de "conversa de um canal"; o `Conversation.cs` registra que várias conversas na mesma sessão é ponto em aberto (`CONVERSATION-LIFECYCLE.md` §17). Este ADR **não** usa a multicanalidade da sessão: no MVP cada `ServiceSession` tem uma conversa, e a continuidade entre canais é um **vínculo entre sessões distintas** (D-WC04). Esse vínculo não se confunde com `session_links` (G2 do e-mail).

Dois fatos do código condicionam o desenho (lidos em 30/10/2026 e 03/11/2026): o item de fila é 1:1 com a sessão e amarra `SessionId`, `HandlingPeriod`, `PlacementEpoch` e a fronteira certificada a ela; e **nenhum caminho de produção encerra uma sessão** (`CloseSessionAsync` existe no contrato do Conversations, mas só o próprio `HandlingService` o referencia).

## Decisão

### A. Estrutura e propriedade

1. **WebChat é um canal nativo do `Channels`** (`ChannelProvider.WebChat`, valor novo só junto com a implementação). **Não há projeto de adaptador**: não existe API de terceiros a isolar. O contrato de canal (capacidades, Receiver, Sender) é implementado dentro do módulo.
2. **`ChannelHandoff` é do `Conversations`**: o estado da continuidade é estado de atendimento (autoridade do Conversations). Não guarda conteúdo de mensagem nem segredo.
3. **O `ContactCenter`** decide filas, horários e atribuição (continuidade de fila e preferência de atendente) e dispara a oferta quando o critério é dele (fila e horário).
4. **Configuração por tenant** (D-WC02):

| Item | Dono | Forma |
|---|---|---|
| Habilitação do WebChat | Channels | instância `WebChat` (`Inactive` → `Active` por ação explícita de administrador); **tenants existentes não mudam**; habilitar exige ao menos um método de recuperação configurado |
| Política de migração (integral, direcionada, seletiva) | Channels | `webchat_config.migration_mode`; padrão **integral** (sem ofertas) |
| Critério seletivo por fila e horário | ContactCenter | atributo da fila + `SchedulePolicy` existente |
| Critério por assunto, tipo de atendimento, regra comercial | futuro (Agents, Workflows) | declarado **não suportado** no MVP |
| Domínio e subdomínio | Channels | rota (`channel_routes`, REC-2); MVP: subdomínio da plataforma; domínio próprio fora do MVP |
| Identidade visual | Channels | `webchat_config` (nome, cores, logotipo por referência, textos); sem HTML livre |
| Convite: validade, usos, máximo de ofertas, intervalo | Channels (valores) → `policy_snapshot` no handoff | o `Conversations` aplica o snapshot recebido e impõe tetos fixos da plataforma |
| Política do WhatsApp durante o WebChat; regras de exceção e retorno | Channels (valores) → `policy_snapshot` | `KeepWebChatActive` (padrão), `ReturnOnExplicitRequest`, `ReturnOnCustomerMessage` |
| Métodos de recuperação e política de recuperação administrativa | Channels (valores) → `policy_snapshot` | ver seção L |
| Limites de anexos | Channels | dentro do teto da plataforma (DP-05) |
| Notificações e controle de custo | Channels | `webchat_config` + razão e orçamento |
| Horários e filas de atendimento | ContactCenter | já existe; o WebChat não os duplica |

Nenhum módulo lê tabela de outro: valores de política viajam no comando, e a disponibilidade do canal chega ao ContactCenter pela porta de capacidades do host (padrão do ADR 0008; DP-04).

### B. Sessões técnicas e atendimento lógico

1. Cada canal tem a **sua `ServiceSession`** (D-WC04). Nunca há fusão física de sessões nem de contatos.
2. O **atendimento lógico** é a **cadeia de continuidade**, identificada por `root_session_id` (a primeira sessão). Ele fica **aberto** enquanto existir sessão primária aberta na cadeia ou handoff `Active`. O encerramento da sessão WhatsApp na ativação (motivo `channel-handoff`) **não encerra o atendimento lógico** e não é desfecho para indicadores. O número público do atendimento é o `session_number` da **raiz**; cada sessão mantém o próprio número técnico (ADR 0012 inalterado), e a sequência dos números públicos tem lacunas por construção (DP-WC18).
3. A sessão de origem é **encerrada na ativação** pelo encerramento existente, com fronteira certificada, e o item dela fecha pelo mecanismo existente. **A continuidade da fila, do atendente e do tempo é feita no ContactCenter pela entrada por continuação** (seção I), sem herdar prova de identidade. Alternativa descartada: manter a origem "estacionada" exigiria um estado novo de sessão e uma regra nova em `RegisterAiReply`, `RegisterHumanReply` e `BeginTurn`, além de dois itens abertos por cliente.
4. **O vínculo é evidência de continuidade, não concessão** (D-WC05). Ele não dá acesso ao histórico da outra sessão. Quem lê a sessão anterior precisa da autorização normal dela, com os limites históricos dela.
5. `ChannelHandoff` é distinto de `session_links` (G2): outra tabela, outro propósito, outro rótulo na interface.

### C. Contrato do `ChannelHandoff` (Conversations.Contracts)

```text
conversations.channel_handoffs(
  tenant_id, id,
  root_session_id, source_session_id, source_contact_id, source_channel_instance_id,
  target_channel_instance_id, target_session_id NULL, target_contact_id NULL,
  satellite_session_id NULL,
  state: Invited | Active | Expired | ReturnedToWhatsApp | Completed | Cancelled,
  invite_version, expires_at,                  -- relógio do banco
  policy_snapshot (versão, tetos aplicados, política do WhatsApp durante o WebChat, métodos de recuperação disponíveis),
  source_boundary_seq NULL,                    -- LastMessageSeq da origem ao ativar
  claim_id NULL, return_requested_at NULL, recovery_count,
  terminal_reason NULL, created_at, activated_at, ended_at, version)

conversations.service_sessions += role: Primary | Satellite, continuity_handoff_id NULL
  CHECK ((role = 'Satellite') = (continuity_handoff_id IS NOT NULL))
```

O contador de entrada do satélite é o `service_sessions.last_inbound_seq` **que já existe** (`InboundSeq` por sessão, com índice único `(tenant_id, session_id, inbound_seq)`). Nenhuma coluna nova é criada para ele, e o reconhecimento do encerramento é conferido contra ele.

Índice único parcial `(tenant_id, source_session_id) WHERE state IN ('Invited','Active')`: **um handoff vivo por origem**. O retorno ao WhatsApp é uma **transição do mesmo handoff**, não um handoff novo.

**Estados.** `Activated` e `WebChatActive` viram um só, **`Active`**, porque a ativação cria a sessão destino de forma atômica e não existe estado intermediário observável. Os demais ficam como propostos.

**Transições** (comandos de sistema, `RequestContext` do host):

| Comando ou fato | Transição | Efeito atômico |
|---|---|---|
| `OfferChannelHandoff(SessionId, TargetChannelInstanceId, ReasonCode, PolicySnapshot, OperationId)` | ∅ → `Invited` | cria o handoff; grava a mensagem de saída de convite (`Author=System`, conteúdo fixo com marcador, **sem link**); emite `OutboundMessageRequestedV1` e `ChannelHandoffOfferedV1`. Recusa se a origem não está aberta, se a unidade não é autorizada para a instância destino (porta do ADR 0008), se excede `max_offers` da raiz ou se o snapshot não traz método de recuperação (`NotEligible(NoRecoveryPath)`). Repetição devolve `AlreadyOffered`. |
| `ActivateChannelHandoff(HandoffId, InviteVersion, ClaimId)` | `Invited` → `Active` | confere versão e validade; **relê a unidade e o setor efetivos da origem e revalida, sob a trava da origem e antes de qualquer gravação, que a instância WebChat de destino autoriza essa unidade (porta existente do ADR 0008, falha fechada)**; só então cria contato, conversa e sessão destino (herdando organização, unidade, setor, modo e idioma da origem, pelo protocolo de numeração); encerra a origem; publica `ChannelHandoffActivatedV1` (com `SourceResponderRef`, `RootSessionId`, `RootSessionNumber`, `SourceBoundarySeq`) e `SessionClosedV1`. **Recusa definitiva (`TargetNotAuthorizedForChannel`):** o handoff vai a `Cancelled(TargetNotAuthorizedForChannel)` e publica `ChannelHandoffEndedV1`; a origem **não** fecha e o destino **não** é criado. **Falha da consulta (exceção):** propaga, nada é gravado e o claim fica repetível. O mesmo `ClaimId` repetido devolve a mesma sessão. |
| expiração (varredura, relógio do banco) | `Invited` → `Expired` | a origem segue normalmente |
| encerramento da origem (comando existente) | `Invited` → `Cancelled(SourceClosed)` | na mesma transação do encerramento |
| encerramento direto do destino (comando existente) | `Active` → `Completed` (sem satélite) | na mesma transação do encerramento; publica `ChannelHandoffEndedV1(HandoffId, State, Reason)`; com satélite, recusado (`SatelliteDecisionRequired`) |
| `CancelChannelHandoff(HandoffId, Reason, OperationId)` | `Invited` → `Cancelled` | revogação por política ou administrador |
| `RequestReturn(HandoffId)` (portal) | `Active` (marca) | grava `return_requested_at`, com validade. Não muda o estado. |
| promoção por política (tenant) | `Active` → `ReturnedToWhatsApp` | só quando o tenant habilitou `ReturnOnExplicitRequest` (com pedido válido) ou `ReturnOnCustomerMessage`: é **autorização explícita de configuração**, nunca consequência do encerramento |
| encerramento delegado (seção M) | `Active` → `Completed` (sem satélite, ou decisão `CloseSatellite`) ou `ReturnedToWhatsApp` (decisão `PromoteSatellite`) | encerra o destino e trata o satélite **somente** conforme a decisão explícita recebida |

**Concorrência.** Toda transição acontece sob a **trava da sessão guarda**: a de origem enquanto `Invited`, a de destino enquanto `Active`. A ordem de travas é a existente (contador antes da sessão), sem trava nova e sem inversão. O `version` do handoff é uma segunda defesa (CAS). Expiração decide pelo relógio do banco.

**Encerramento direto.** O `CloseSession` direto do Conversations (que exige `ConversationManage`) segue **inalterado** para toda sessão que não participa de handoff `Active`. Para as que participam, e **somente** para elas, valem estas regras sob a trava da sessão guarda: (1) **destino de handoff `Active` sem satélite:** o encerramento **conclui o handoff** (`Active` → `Completed`) na mesma transação, sem estado intermediário com o destino fechado e o handoff `Active`; (2) **destino com satélite:** o encerramento direto é **recusado** (`SatelliteDecisionRequired`), sem gravar nada, e o encerramento só ocorre pela operação delegada (seção M), que exige a decisão explícita; (3) **sessão `Satellite` vinculada a handoff `Active`:** o encerramento direto é **proibido** (`SatelliteDecisionRequired`), sem gravar nada, **inclusive para quem tem `ConversationManage`**. O satélite nunca é fechado nem promovido por omissão.

**Autorização do canal na ativação.** A revalidação usa **a porta que já existe** (`IChannelUnitAuthorization`, ADR 0008): sem sonda nova, sem marca de versão e sem confirmação adicional. O `Channels` confere, **antes** de chamar a ativação, que a instância WebChat da rota está `Active` (dado dele); o `Conversations` confere a unidade efetiva da origem, sob a trava dela. **Limitação reconhecida (a mesma do ADR 0008):** não há atomicidade entre o `Channels` e o `Conversations` (COM-5), e a autorização vale no instante da consulta; uma revogação logo depois dela não é impedida. O efeito é o de qualquer revogação posterior a uma sessão aberta: a situação `Suspended` interrompe o tráfego do canal, e a retirada de unidade vale para as próximas perguntas. Para manter fechado o acesso do cliente nesse caso, o `Channels` **reconfere a situação da instância (dado dele)** imediatamente antes de entregar a primeira credencial e, se ela deixou de estar `Active`, não a entrega, mantém a sessão do navegador em `AwaitingProof` e alerta o administrador. Não se propõem mecanismos adicionais de sincronização, porque não poderiam prometer a atomicidade que os módulos não têm. Risco residual não crítico, a validar na 5.2w com o teste de falha da consulta do ADR 0008.

### D. Convite seguro e sessão do navegador (D-WC03)

1. **Capacidade assinada, não armazenada.** O convite é `v1.<kid>.<handoffId>.<inviteVersion>.<exp>.<mac>`, com `mac = HMAC-SHA256(chave do tenant derivada de chave-mestra com rotação por kid, tenant|handoffId|inviteVersion|exp)`. O tenant está na assinatura. Nada é guardado em claro. A revogação é feita por `invite_version` ou por estado.
2. **O link nunca fica em histórico, contexto de IA, log nem linha de tabela.** A mensagem do `Conversations` guarda só o marcador. O **Sender do `Channels` monta o link na hora do envio**.
3. **Transporte no fragmento da URL** (`https://<subdomínio>/i#<token>`): não chega a servidor, proxy nem cabeçalho `Referer`. O app lê o fragmento, envia por POST e o remove da barra de endereço.
4. **Só POST consome.** A pré-visualização do WhatsApp, de antivírus ou de robô (GET) não consome o convite. A ativação exige um gesto explícito do usuário.
5. **Reivindicação idempotente, sem transação distribuída.** (1) O navegador gera `claim_verifier` (256 bits, `crypto.getRandomValues`) e calcula `challenge = SHA-256(verifier)`. O verificador fica **só em memória** da aba que reivindica, com cópia em `sessionStorage` apenas para sobreviver a recarga da mesma aba. **Nunca** em `localStorage`, cookie, URL ou log. Outra aba do mesmo navegador o obtém por `BroadcastChannel` enquanto o claim está em andamento. (2) O `Channels` valida a assinatura e a validade do convite, resolve o tenant pelo domínio e grava `channels.webchat_claim` com chave única `(tenant_id, handoff_id, invite_version)`, guardando `claim_id`, `challenge`, estado `Started` e validade curta. Mesmo `challenge` é retomada; `challenge` diferente é `AlreadyClaimed` (resposta uniforme); `Started` vencido libera o convite enquanto o handoff estiver `Invited`. (3) O `Channels` confere que a instância WebChat da rota está `Active` (`Inactive` ou `Suspended` leva o claim a `Failed(ChannelUnavailable)`, e o handoff segue `Invited` até expirar) e chama `ActivateChannelHandoff(handoffId, inviteVersion, ClaimId = claim_id)`, idempotente por `claim_id`, gravado no handoff (único). (4) O `Channels` avança o claim para `Activated` (CAS) e cria `channels.webchat_session` com chave única `(tenant_id, handoff_id)` e `claim_id` único, em `AwaitingProof`: **uma sessão de navegador por handoff**. (5) A resposta entrega a primeira geração de credencial, depois de o `Channels` reconferir a situação da instância (seção C). Se ela se perder, a repetição com o `verifier` (prova de posse: `SHA-256(verifier) = challenge`) **reemite** credencial para a **mesma** sessão. (6) A primeira chamada autenticada (`POST /session/confirm`) leva o claim a `Established`, **queima o verificador no servidor** e o navegador o **apaga** (`sessionStorage` e variável sobrescrita) na mesma resposta.
6. **Falha e recuperação.** O verificador perdido (aba fechada, queda do navegador) tem destino previsível: claim `Started` vencido com handoff `Invited` libera o convite para nova reivindicação; claim `Activated` sem `Established` mantém a sessão em `AwaitingProof`, o convite fica `AlreadyClaimed` e o acesso só volta pelos métodos de recuperação (seção L). Uma conciliação do `Channels` completa claim e sessão quando o handoff está `Active` e o vínculo ficou incompleto, e registra o indicador "ativou e não estabeleceu", com alerta ao passar do prazo (`establish_timeout`). **Nenhuma dessas rotinas encerra, cancela ou promove atendimento**: só alertam e registram indicador.
7. **Posse do link não é identidade civil.** A sessão nasce com `assurance_level = L0` (só posse do link). Os níveis são: **L0** (posse do link), **L0A** (atestado por funcionário, `StaffChecklist`), **L1** (controle da linha de WhatsApp) e **L2** (verificação do tenant). Regra imposta já agora: nenhum dado pessoal sensível ou clínico sai pelo WebChat com garantia abaixo da exigida pela operação, e **L0 e L0A nunca satisfazem operação sensível**. No MVP nenhuma operação sensível existe.
8. **CSP e cabeçalhos do portal.** O portal é um app estático em origem própria do tenant, sem HTML, CSS ou script fornecido por tenant (identidade visual por tokens de design e imagens por referência). Cabeçalhos: `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self' wss://<host>; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'; require-trusted-types-for 'script'; report-to`. **Sem** `unsafe-inline` nem `unsafe-eval`. Também: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Resource-Policy: same-origin`, `Strict-Transport-Security` e `Permissions-Policy` mínimo. O endpoint de relatório de violações é limitado por taxa e não recebe dado pessoal. O Service Worker nunca guarda resposta autenticada nem página com token.
9. **XSS.** Todo conteúdo de mensagem é renderizado como **texto** (sem `innerHTML`, sem `dangerouslySetInnerHTML`, sem Markdown ou HTML no MVP; uma regra de lint e um teste proíbem). Links em mensagem viram texto ou âncora com `rel="noopener noreferrer"`. Anexos nunca são renderizados inline fora de tipos de imagem permitidos, e HTML, SVG e PDF saem como download (`Content-Disposition`) com `nosniff`. A credencial de sessão fica só no cookie `HttpOnly`; o ticket do hub fica só em memória. Qualquer formato rico futuro exige sanitização por lista de permissões (biblioteca sujeita a aprovação).

### E. Mensagens e transporte (SignalR é só transporte)

1. **Recebimento (cliente → plataforma), papel Receiver do WebChat.** Autentica a conexão pela sessão, valida tamanho e taxa e grava em `channels.inbox_event` com `dedupe_key = (webchat_session_id, client_message_id)`. Só confirma ao cliente **depois do commit**. **Não chama outro módulo.** A ingestão por lane chama `IngestInboundMessage` com `ProviderEventId = "{webchat_session_id}:{client_message_id}"`, e o `dedupe_key` do Inbox é o mesmo par. O `client_message_id` é validado como UUID (sintaxe fixa, sem o separador `:`), e o identificador resultante (73 caracteres) cabe no limite de 200. A idempotência do Conversations (índice único `(tenant_id, channel_instance_id, provider_event_id)`) fica **inalterada**: a identidade do evento é única por sessão do navegador, então o mesmo `client_message_id` em outra sessão é **outra mensagem** e nunca é suprimido nem devolve a mensagem de outra pessoa. O relógio do cliente **não** é confiável: vale o do servidor.
2. **Envio (plataforma → cliente), papel Sender.** Consome `OutboundMessageRequestedV1`. Despachar é tornar a mensagem disponível ao cliente: estratégia de efeito (a), idempotente **por construção** (o cliente deduplica por `MessageId`), então `SendUnknown` não existe para o WebChat. O Worker grava uma marca mínima (`session_id`, `message_id`), e o hub da API a lê por polling configurável e empurra. O hub **não** carrega conteúdo que o banco não tenha. `LISTEN/NOTIFY` só depois de medir (`BOUNDARIES.md` §12.5).
3. **Leitura.** O portal lê o histórico **da sessão destino** pelo `Conversations` (consulta em contexto de sistema, com a sessão tirada da `webchat_session`, nunca do pedido), com cursor `afterSeq`. O cliente só vê mensagens da sessão WebChat. Nada do WhatsApp é copiado.
4. **Estados de entrega.** `Sent` ao gravar a marca. `Delivered` por confirmação do cliente (evento de status pelo Inbox, `RecordDeliveryStatus`). `Read` é opcional. Mensagem ao cliente com sessão encerrada vira `Expired` por `expires_at` operacional.
5. **Reconexão e ordem.** Backoff exponencial no cliente. Na conexão, `Resume(lastSeq)` devolve as mensagens seguintes, **limitadas pela fronteira inicial da sessão**; `lastSeq` é só cursor. A ordem é `MessageSeq`. A deduplicação é por `MessageId`. Várias abas compartilham o cookie, cada uma com a própria conexão (teto configurável); os envios das abas têm `client_message_id` próprios.
6. **Segurança do hub.** Ticket de conexão de uso único e vida curta, trocado do cookie por POST, para não colocar credencial em URL; verificação de `Origin`; toda chamada de método do hub reautoriza a sessão (revogada ou encerrada desconecta); grupos nomeados pelo servidor com tenant e sessão; querystring nunca é registrada em log.
7. **Dois papéis no `Channels.Presentation`.** O **Receiver do WebChat** só grava no Inbox. A **API do portal** (ativação, histórico, sessão, recuperação) pode chamar o `Conversations` (aresta já permitida). Ficam em namespaces separados, e o teste "o Receiver não referencia outro módulo" continua válido.
8. **Ingestão vinculada: sem sessão implícita.** O `IngestInboundMessageCommand` ganha o campo opcional **`BoundSessionId`** (último parâmetro, nulo por padrão), e o `Conversations` distingue dois modos, **definidos pela instância do canal e não pelo chamador**: **`SessionResolution`** (WhatsApp, Instagram, Messenger, e-mail e SMS: pode localizar ou criar sessão; `BoundSessionId` deve ser nulo; comportamento atual inalterado) e **`BoundSession`** (WebChat: `BoundSessionId` é obrigatório e a sessão **nunca é criada** por ingestão). O modo vem de uma porta pequena do `Conversations`, `IChannelIngestionPolicy`, ligada pelo host ao `IChannelRouting` (padrão do ADR 0008: `ChannelRoutingView` ganha `IngestionMode`, derivado do provedor da instância, que é imutável); a porta padrão devolve `SessionResolution`. Em ordem, antes de gravar: (0) modo da instância (a exceção da consulta propaga: falha fechada); (1) coerência do modo com `BoundSessionId` (violação lança `BoundSessionIngestionException`, sem gravar); (2) idempotência por `ProviderEventId` (mensagem já registrada devolve `Duplicate`, mesmo com a sessão fechada depois); (3) a sessão vinculada precisa ser do tenant, da instância e do contato (senão, a mesma exceção) e estar **aberta**: se estiver fechada, o resultado é `IngestOutcome.BoundSessionClosed`, **sem gravar nada**. No momento da aceitação, o Receiver grava no `inbox_event` o `target_session_id` autorizado da `webchat_session`.
   **Tratamento rastreável de `BoundSessionClosed`:** o evento vai para `channels.inbox_quarantine` (motivo `session-closed-after-accept`), com auditoria, métrica e alerta operacional ao administrador do tenant, rastreável por `inbox_event_id`, `webchat_session_id`, `target_session_id` e `handoff_id`; o conteúdo fica retido pela política de retenção (DP-06); o portal mostra a mensagem como **não entregue**. **Nenhuma resposta automática é enviada ao WhatsApp** e nenhuma sessão é criada. O Receiver recusa (409) novas mensagens quando já sabe que a sessão terminou. Os desvios de modo são exceções (configuração incorreta), tratadas pelo `Channels` como falha permanente do evento.

### F. Abuso, anexos, experiência e custo de oferta

- **Limites de taxa:** por IP e por tenant na ativação; por sessão em mensagens e anexos; teto de conexões por sessão e por tenant (LIM-1); teto de ofertas por raiz e por tenant e período (cada convite é uma mensagem paga).
- **Respostas uniformes** a convite inválido (sem oráculo de enumeração).
- **Anexos:** o mesmo modelo do ADR 0014 (D): `Clean` antes de o atendente abrir; lista de tipos e tetos por tenant dentro do teto da plataforma; nome sanitizado; download por URL curta revalidada. Depende do DP-05.
- **Experiência e notificações (D-WC06).** O portal é responsivo e utilizável sem instalação. **PWA é opcional** (manifesto e Service Worker restrito, sem guardar resposta autenticada). **Web Push é opcional e fora do MVP**: depende de suporte do navegador e de permissão do usuário, e as limitações em Android e iPhone são **indicativas e não confirmadas** (PRV-2-WC). A ausência ou a revogação da permissão de push não afeta o atendimento: a mensagem fica disponível e o portal recupera o estado ao reabrir. A notificação pelo WhatsApp fica **desligada** e só seria permitida quando a política da Meta o permitir e houver justificativa econômica, com orçamento.

### G. Sessão satélite, políticas e encerramento do WebChat

**Mensagem do WhatsApp com handoff `Active`.** É registrada em **sessão satélite** (técnica, não operacional), criada pelo caminho normal de ingestão quando o contato é o `source_contact` de um handoff `Active`. A sessão encerrada **não é reaberta** e nenhum atendimento operacional é criado. O satélite tem regras fixas no Conversations: sem turno de IA, sem resposta, sem transição de modo, sem `Enter` e sem número público. Nada é enviado ao cliente por causa dela: nenhum convite e nenhuma resposta automática. O WebChat permanece ativo.

Contratos atuais respeitados: o `IngestInboundMessageCommand` **não muda para o satélite** (a decisão é interna ao Conversations; o único acréscimo, `BoundSessionId` opcional, é do WebChat, seção E, item 8); a idempotência é por `ProviderEventId`; a numeração segue o protocolo do ADR 0012 (o satélite consome um número técnico, nunca público); o índice de chat garante um único satélite aberto por contato; a autorização de unidade (ADR 0008) vale como hoje (se recusar, a mensagem segue no Inbox para nova tentativa, sem perda). `InboundMessageRegisteredV1` carrega `SessionRole = Satellite`, e os consumidores ignoram o processamento operacional.

**Visibilidade e limites de leitura do satélite.** O Conversations levanta uma ocorrência deduplicada por satélite, com contador (`SessionAttentionRaisedV1` com motivo novo `CustomerWroteOnOtherChannel`), e o ContactCenter a marca no item da sessão WebChat. A leitura das mensagens do satélite é um caso de uso do ContactCenter, pela operação de leitura de histórico que o gateway já tem, e **reaproveita a autoridade efetiva do item WebChat** (item visível, `CanReadConversation` sobre o responsável efetivo, mesmo `HandlingPeriod` e `PlacementEpoch`, como na etapa 4), **mais** handoff `Active` (ou o satélite dentro da fronteira). Sem autoridade, 404 (item invisível) ou 403 auditado.
- **Limite inferior.** O satélite nasce depois da ativação, então não contém nada anterior a ela, e o histórico da sessão de origem nunca é exposto. Dentro dele, o limite da **supervisão** é o início do período do item (`satellite_base_seq + 1`, gravado no item ao nascer: a `LastMessageSeq` do satélite nesse instante, ou 0 se ele ainda não existe). O limite do **responsável efetivo** é o `satellite_read_from_seq` do item, gravado quando alguém passa a ser responsável (atribuição, `ClaimNext`, transferência concluída, continuidade), com a `LastMessageSeq` do satélite lida nesse passo mais um (ou o limite de base, se o satélite não existe). Assim, **quem assume depois não lê o que chegou antes de assumir**, sem tabela de concessões. Participantes futuros (5.7) seguem a regra de concessão do ADR 0013, aplicada à sequência do satélite. Os limites do item WebChat (`history_from_seq`, `HistoryLimitSeq`) não se aplicam ao satélite nem o ampliam. Se a `LastMessageSeq` não puder ser lida ao gravar o limite, ele nasce sem leitura (falha fechada) até a supervisão resolver.
- **Limite superior.** O menor entre a sequência vista na mesma resposta do Conversations e, com o satélite fechado ou promovido, a **fronteira certificada** `SatelliteBoundarySeq`, gravada no item no encerramento (`satellite_closed_through_seq`). Depois da promoção, as mensagens novas da sessão promovida pertencem ao item novo dela, nunca ao item encerrado.
- **Revalidação.** O `ReadAuthority` existente é estendido com `SatelliteSessionId`, estado e `version` do handoff, `SatelliteBoundarySeq`, `satellite_base_seq` e `satellite_read_from_seq`; terminada a leitura, o item e o handoff são relidos, e se algo que decide o acesso mudou, a página é descartada (409).
- **Sem herança.** O vínculo nunca concede leitura da sessão de origem nem das sessões anteriores da cadeia (D-WC05).
- **Risco residual, a validar na 5.2w.** O limite do responsável é calculado pelo ContactCenter a partir de uma leitura do satélite no passo de conclusão, e não de forma atômica com a mudança de responsável no Conversations. O erro só ocorre na direção conservadora (exclui mensagens que chegaram entre os dois instantes). Ocorrências de períodos anteriores não reconhecidas só são alcançadas pela supervisão.

**Reconhecimento.** O atendente reconhece as ocorrências depois de lê-las: o reconhecimento só cobre o que a leitura dele **entregou** (`delivered_through_seq` por item e usuário), é monotônico, auditado e guardado no ContactCenter por satélite (`acked_through_seq`). **Só avança de forma contígua**: para reconhecer até N, o leitor precisa ter recebido tudo em (`acked_through_seq`, N] dentro do seu limite inferior. Quem tem limite inferior acima da lacuna não reconhece além dela, e o encerramento fica recusado até o reconhecimento cobrir o `last_inbound_seq` existente do satélite.

**Encerramento normal do WebChat.** O satélite **nunca é promovido automaticamente**. O encerramento exige uma decisão explícita sobre o satélite: `CloseSatellite` (exige reconhecimento até a última mensagem do cliente, conferido de forma atômica pelo Conversations) ou `PromoteSatellite` (continuidade pelo WhatsApp, decisão autorizada com permissão própria e justificativa). Com mensagens não reconhecidas e sem promoção, o encerramento é **recusado**. A recusa por `PendingSatelliteOccurrences` é **terminal** para aquela tentativa: o ContactCenter cancela a operação pendente `Close` (o item volta a ficar ocioso), grava o recibo `Cancelled` com a `LastInboundSeq` observada e audita a negação; a repetição do **mesmo** `RequestId` devolve a mesma recusa, sem reavaliar. Uma nova tentativa exige **novo `RequestId`**, nova leitura e novo reconhecimento (ADR 0009, item 10). Todas as mensagens e o vínculo permanecem preservados e rastreáveis.

**Retorno por política.** Cada tenant escolhe entre `KeepWebChatActive` (**padrão**: só registra e sinaliza), `ReturnOnExplicitRequest` (retorna somente após pedido do cliente no portal, "Continuar pelo WhatsApp", e mensagem posterior) e `ReturnOnCustomerMessage` (retorna na primeira mensagem; automático, só se o tenant habilitar). As duas últimas são **políticas configuráveis e explicitamente habilitadas pelo tenant**: o retorno por política é autorização explícita de configuração, distinta do encerramento. Em qualquer retorno, a promoção do satélite e a entrada por continuação do ContactCenter acontecem como na seção I, a sessão WebChat é encerrada com fronteira certificada e o portal avisa e fica somente leitura. `max_offers` e o intervalo mínimo entre ofertas evitam ciclo.

**Exceções de convite.** Convite expirado: a origem continua no WhatsApp e **nada é reenviado**. Cliente que não migra: nada além da expiração. Atendimento encerrado antes da ativação: o handoff vai a `Cancelled(SourceClosed)` e o link mostra o estado sem dado algum. Mudança de política vale só para ofertas **novas** (o handoff guarda o snapshot); a desativação do tenant cancela convites pendentes por ação explícita de administrador; a instância `Suspended` interrompe todo o tráfego do canal (semântica existente de `ChannelStatus`), sem confundir com mudança de política.

### H. Autorização e isolamento (D-WC05)

O tenant vem do domínio (rota do `Channels`) e é assinado no convite, nunca de parâmetro. A sessão WebChat autoriza por tenant, contato e atendimento. O atendente da sessão destino só lê a sessão anterior se já tinha acesso a ela; o vínculo é exibido só a quem pode ler as duas sessões. Toda transição é auditada, com o ator original ou o componente de sistema.

### I. Continuidade no ContactCenter

**Evidência.** A fila ordena por `Priority desc, EnteredAt asc, Id` (`QueueItemRepository`), e `QueueItem.Enter` fixa `EnteredAt = now`. Um item novo para a sessão destino entraria **atrás de todos**, com a prioridade da rota nova, sem responsável (o `RequestHandoffAsync` não recebe atendente preferido, e `ClaimNextAsync` deixa qualquer membro assumir), e a contagem de indicadores por item contaria dois atendimentos. Não há código de indicadores hoje; sem uma chave de cadeia no modelo, corrigir depois exigiria backfill. Reapontar o item de origem para a sessão destino é descartado: o item amarra `SessionId`, `HandlingPeriod`, `PlacementEpoch` e a fronteira certificada à sessão.

**Princípio.** A continuidade é **atributo da cadeia de itens**, nunca autoridade. Nenhum atributo de continuidade entra em função de prova ou de autorização.

| # | Extensão | Onde |
|---|---|---|
| E1 | Atributos no item: `ContinuityRootSessionId`, `PredecessorItemId`, `ContinuityOrdinal`, `AttendanceStartedAt` (início da raiz) | ContactCenter |
| E2 | `QueueOrderAt` (âncora de posição), usada na ordenação. Padrão = `EnteredAt`; no item de continuação, a âncora da cadeia. `EnteredAt` mantém a semântica atual (criação do item). | ContactCenter |
| E3 | **Entrada por continuação:** a prioridade vem do predecessor (dado do próprio ContactCenter, nunca do pedido). A fila é a do predecessor, se ainda atende a sessão destino (caminho de "fila pedida" do roteador). O item nasce com a **operação pendente `Enter` já carregando o atendente-alvo**, no padrão da transferência composta. **A capacidade usa a reserva existente:** com `MaxItemsPerAgent` na fila, o item é gravado por `SaveWithinCapacityAsync`, sob a mesma trava `(fila, atendente)` do `ClaimNext`, e `CountOccupiedAsync` passa a contar também a pendência `Enter` com atendente-alvo. Sem vaga, o item é gravado **sem atendente-alvo** (mesmo `OperationId`), fica `Waiting` na posição original, e o resultado é `ResponderUnavailable(capacidade)`. Como `ListWaitingAsync` exclui itens com pendência, **ninguém o reivindica** antes do fim da cadeia, e entre o `ClaimNext` do atendente-alvo e a continuidade vence quem tomar a trava primeiro, sem exceder o máximo. | ContactCenter (extensão do executor e da contagem) |
| E4 | `ChannelHandoffActivatedV1` leva `SourceResponderRef` (a autoridade é o Conversations, ADR 0010, D.19), `RootSessionId`, `RootSessionNumber` e `SourceBoundarySeq`. `SessionView` ganha `RootSessionNumber`. | Conversations |
| E5 | Regras de indicador **por cadeia** (seção J). | ADR |
| E6 | Histórico: cada item guarda o seu. Linhas de vínculo `continuity.started` e `continuity.handed-off` com os ids; a visão da cadeia junta só o que o leitor pode ler, item a item. Nada é copiado. | ContactCenter |

**Preferência não é garantia.** O resultado da continuidade é registrado no histórico do item novo e exposto à supervisão:
- `SameResponder`;
- `ResponderUnavailable(motivo)`: não é membro, sem capacidade, fila pausada ou fora do horário, conflito de atribuição, predecessor sem responsável;
- `QueueFallback(motivo)`: fila do predecessor indisponível, ou operação pendente no predecessor esgotando as tentativas.

Em qualquer degradação o item fica `Waiting` na **posição original** (`QueueOrderAt`) e com a prioridade herdada, já reivindicável. O atendimento nunca volta ao fim da fila. Nenhum documento promete o mesmo atendente.

**Garantias preservadas:**

| Garantia | Como |
|---|---|
| `HandlingPeriod`, `PlacementEpoch` | O item novo usa os da sessão destino, que nasce do zero. Nada é herdado. |
| `ScopeProof` | Funções de prova inalteradas. Teste: o código de autorização não lê atributo de continuidade (mutação). |
| Autorização | A atribuição passa pela decisão do ContactCenter sobre o atendente (membro, `CanWork`, capacidade) e pela pré-condição atômica no Conversations. |
| Leitura da sessão anterior | Só pelo item de origem, com a fronteira certificada, porque o encerramento da origem é atômico com a ativação. |
| Idempotência | `OperationId` determinístico `continuity:{handoffId}` e o inbox do consumidor. |
| Ordem dos eventos | O handler lê o predecessor por sessão, em qualquer estado, então não depende de `SessionClosedV1` da origem ter sido projetado antes. Operação pendente no predecessor: nova tentativa com recuo e, esgotado o limite, `QueueFallback`. |

**Alternativas descartadas:** reapontar o item de origem; reserva do item por TTL (conceito novo; a pendência já resolve); manter a origem aberta e "estacionada".

### J. Custos, Meta e indicadores (D-WC08)

1. **Razão genérico** `channels.cost_entry`: `kind` (mensagem de provedor, template, armazenamento, notificação push, tráfego), provedor, categoria, país, `billable`, quantidade, preço aplicado, valor **estimado** e **confirmado** (quando o provedor informar), moeda e vínculo com handoff e raiz. **Nenhuma tarifa fixa em código:** `channels.price_table` versionada por vigência, com valores da plataforma e sobrescrita por tenant.
2. **Operação humana** não vira cobrança: entra como duração (do ContactCenter).
3. **Indicadores por tenant**, em agregados horários nos módulos donos (padrão do `BOUNDARIES.md` §12.10): convites enviados, ativação, abandono (`Invited` → `Expired`), "ativou e não estabeleceu", concluídos, retornos por motivo (Conversations); mensagens pagas por tipo (Channels). A **economia efetiva** é composta no host e rotulada **estimativa** com premissas explícitas (linha de base configurável); a metodologia exige aprovação (DP-WC11).
4. **Indicadores de atendimento por cadeia.** Calculados por `ContinuityRootSessionId`, nunca por item: atendimentos = raízes distintas; espera = soma dos intervalos `Waiting`; atendimento = soma dos intervalos `Assigned → Closed`; idade = `now − AttendanceStartedAt`; fechamento com motivo `channel-handoff` não é desfecho. Item sem cadeia é uma cadeia de um elemento.
5. **Meta:** itens novos da PRV-2 (**PRV-2-WC**, `ETAPA-5.md` §9.1), **não cumpridos**: política para convite com link e retorno, templates (categoria, aprovação, preço), janela, link de conversa do WhatsApp e Web Push em Android e iPhone.

### K. Fora do MVP

Aplicativos nativos; vídeo; voz em tempo real; migração automática sem ação do cliente; fusão física de sessões; agentes avançados de IA; **convite iniciado por atendente** (sem decisão de continuidade); `ReissueInvite` automático; domínio próprio; WebChat iniciado sem convite; notificação ativa pelo WhatsApp; Web Push. **Entra no MVP:** o encerramento do atendimento com a decisão sobre o satélite (seção M).

### L. Recuperação do WebChat

**A perda do cookie ou da sessão do navegador não encerra nem perde o atendimento.** O convite consumido **nunca** é reutilizado: toda reentrada exige prova e emite uma nova `invite_version`, que invalida os links anteriores.

**Métodos de recuperação.** Contrato `IWebChatRecoveryMethod` (identificador, prova, nível de garantia). A disponibilidade do número de WhatsApp **não é presumida**: cada método declara se está disponível para o tenant.

| Método | Prova | Garantia |
|---|---|---|
| `WhatsAppLineProof` (se a instância estiver `Active` e a política da Meta permitir) | o cliente envia um código gerado pelo portal pelo WhatsApp; o `Channels` confere que o remetente é o `source_contact` do handoff | L1 (controle da linha, não identidade civil) |
| `AdministrativeRecovery` (condicionada à política do tenant; ver L.1) | verificação de identidade exigida pela política, mais permissão, justificativa e auditoria | `L0A` no MVP (`StaffChecklist`) |
| `TenantAuthentication` (futuro, definido pelo tenant) | autenticação do cliente no provedor de identidade do tenant | L2, conforme o tenant |

**L.1 Recuperação administrativa.** É um método **condicionado à política do tenant** e **desligado enquanto o tenant não a configurar** (falha fechada). **A autorização do atendente (permissão e papel) não é, isoladamente, prova da identidade do cliente**: ela habilita a *execução* do procedimento, e a identidade é verificada pelo método que a política exige.

- **Política do tenant:** o método de verificação exigido; se há segundo aprovador; os canais de entrega permitidos; o limite de usos por handoff e por período.
- **Métodos de verificação** (contrato `IIdentityVerificationMethod`: identificador, esquema de evidência e nível de garantia resultante). No MVP existe `StaffChecklist`: o tenant define a lista de dados que o atendente confere com o cliente (por exemplo, dados cadastrais e o protocolo do atendimento), e o sistema registra **quais itens foram conferidos e o resultado, sem copiar os valores**. A garantia resultante é **L0 atestado** (`L0A`). **`StaffChecklist` é verificação de baixa garantia (`L0A`) e é insuficiente, isoladamente, para qualquer operação sensível**: o nível `L0A` nunca satisfaz operação que exija L1 ou superior, e nenhum dado pessoal sensível ou clínico sai pelo WebChat com garantia inferior à exigida. `SecondApproval` é opcional ou obrigatório por política: o aprovador é **outra pessoa** com a mesma permissão e nunca o solicitante. São futuros: código de uso único enviado a ponto de contato **verificado** (L1 ou L2, conforme a origem do dado) e autenticação do tenant (L2).
- **Registro obrigatório** (`webchat_recovery_grant` e auditoria durável, **sem o link**): método de verificação e resultado; solicitante e aprovador (permissão e papel); **justificativa** (texto obrigatório); **destinatário** (`CustomerAtVerifiedContactPoint`, com referência mascarada, ou `StaffRelayedToCustomer`, com a declaração de quem recebeu); **entrega do acesso** (canal, instante e resultado, ou "exibido uma vez ao solicitante"); nível de garantia concedido e janela de histórico; nova `invite_version`. O link **não é armazenado**; quando entregue por canal, vai pelo Sender com marcador, como o convite.
- **Histórico por nível de garantia.** L1 e L2: mensagens da sessão WebChat **desde o limite original**. `L0A`: **somente a partir da recuperação**. Em nenhum nível o portal expõe sessão diferente da sessão WebChat vinculada ao navegador, **nem as sessões anteriores da cadeia**. Ampliar a janela exige compartilhamento explícito, autorizado e auditado, com garantia suficiente (futuro).
- A recuperação administrativa **substitui** a credencial anterior (um dispositivo por handoff), conta no limite de recuperações e gera alerta ao responsável do tenant.

**Credencial.** Cookie `__Host-`, `HttpOnly`, `Secure`, `SameSite=Strict`, opaco, guardado só como hash, **rotacionado por geração** com janela curta de tolerância para abas concorrentes. Uma **credencial antiga fora da janela é recusada e auditada** (`webchat.credential.stale`, com taxa limitada) e **não revoga a sessão válida**. Repetições acima de um limite geram alerta, nunca revogação automática.

**Revogação** por: saída do usuário; recuperação (a credencial anterior deixa de valer); estado terminal do handoff (somente leitura e expiração); ação administrativa; teto de inatividade ou de vida absoluta. Toda chamada reautoriza a sessão, e a revogação desconecta o hub.

**Outro dispositivo.** Convite consumido não serve. Só um método de recuperação, que **substitui** a credencial anterior. No MVP vale **um dispositivo por handoff**; abas do mesmo navegador compartilham o cookie.

**Proteção.** Verificação de `Origin` e cabeçalho próprio contra CSRF; limites de recuperação por handoff, por contato e por tenant (cada recuperação por WhatsApp custa uma mensagem); troca de IP ou agente de usuário é registrada, sem bloquear, porque redes móveis variam.

**Indisponibilidade dos métodos.** Se todos os métodos de recuperação ficarem indisponíveis para um handoff `Active`, o sistema **somente alerta**. Não encerra, não cancela, não promove nem reverte atendimento automaticamente; a decisão continua sendo do atendimento.

### M. Encerramento do atendimento

O encerramento é uma **capacidade geral do ContactCenter** (`CloseAttendance`), planejada na **subetapa 5.1b**, e não uma funcionalidade do WebChat. Depende da **nona operação delegada** do gateway (`CloseSession`), definida na **segunda emenda proposta ao ADR 0009** (item 10), cuja aprovação é **pré-requisito** (DP-01). O contrato, a ordem de validação e os efeitos atômicos ficam nessa emenda. A **5.2w usa** a capacidade e acrescenta somente o que é específico do WebChat: a decisão sobre a sessão satélite (`SatelliteDecision`), o reconhecimento das ocorrências (`SatelliteAckThroughSeq`), a conclusão do handoff (`Completed` ou `ReturnedToWhatsApp`) e o fechamento ou a promoção do satélite, conforme a seção G. Até a 5.2w, qualquer decisão diferente de `None` é recusada (`DecisionNotAllowed`). Sem a 5.1b, o estado `Completed` não é alcançável.

### N. Invariantes

1. O vínculo entre canais não concede leitura de histórico.
2. Uma sessão satélite nunca é operacional.
3. O handoff só é ofertado quando o snapshot informa **ao menos um método de recuperação disponível**. Se um handoff `Active` perder todas as vias, o sistema **gera alerta e nunca encerra, cancela, promove nem reverte nada automaticamente**; a decisão continua sendo do atendimento.
4. Um claim por `(handoff, invite_version)` e uma sessão de navegador por handoff.
5. O convite consumido nunca é reutilizável.
6. Credencial antiga é recusada e auditada, sem revogar a sessão válida.
7. O satélite nunca é promovido por omissão.
8. Encerrar o WebChat com ocorrências não reconhecidas e sem promoção é recusado.
9. Nenhum atributo de continuidade entra em `ScopeProof`, `HandlingPeriod`, `PlacementEpoch` nem em autorização.
10. O verificador nunca é persistido fora de `sessionStorage` e é apagado ao estabelecer a sessão.
11. A autorização do atendente não é prova de identidade do cliente.
12. A recuperação nunca expõe a sessão de origem nem sessões anteriores da cadeia.
13. `StaffChecklist` (`L0A`) é insuficiente, isoladamente, para operação sensível.

## Alternativas descartadas

- **Uma `ServiceSession` multicanal (várias conversas na mesma sessão).** Tocaria o núcleo (destino da resposta, contexto de IA, leitura por conversa) e faria o vínculo entre canais conceder histórico automaticamente, contra a D-WC05.
- **Reapontar o item de fila para a sessão destino.** Quebra a prova de identidade do item.
- **Manter a sessão de origem aberta e estacionada.** Estado novo e regras novas no núcleo, dois itens por cliente.
- **Reabrir silenciosamente a sessão encerrada** ou criar um segundo atendimento operacional para registrar a mensagem do WhatsApp. O satélite resolve sem nenhum dos dois.
- **Promover o satélite ao encerrar o WebChat por omissão.** Cria atendimento operacional sem decisão de ninguém.
- **Link de convite em claro (mensagem, log, tabela), no caminho da URL ou consumido por GET.** Vaza para atendentes, IA, proxies e pré-visualizações.
- **Verificador em `localStorage`; revogar a sessão válida ao ver credencial antiga.** O primeiro amplia a janela de exfiltração por XSS; o segundo permite derrubar a sessão alheia.
- **Recuperação administrativa só pela autorização do atendente.** A permissão não prova a identidade do cliente.
- **Conceder `ConversationManage` ao atendente para encerrar.** Abre todas as conversas da organização.

## Consequências

- A fila ganha uma âncora de posição (`queue_order_at`) e atributos de cadeia: a ordenação muda, então a regressão da etapa 4 (`ListWaiting`, `ClaimNext`) é obrigatória.
- A sequência dos números públicos tem lacunas por construção.
- O satélite acrescenta uma sessão técnica por handoff com mensagem do WhatsApp, sempre não operacional.
- O WebChat depende da 9ª operação do gateway (encerramento) e das decisões de gate (REC-2, PRV-2, PRV-2-WC) antes do convite real.
- O portal é um app separado, com CSP estrita e pacotes novos que exigem aprovação.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Link vazado ou compartilhado | fragmento, uso único por sessão, prova de posse, expiração, `invite_version`, respostas uniformes |
| Preview ou robô consome o convite | só POST com gesto explícito consome |
| Handoff concede histórico por engano | vínculo = evidência; teste de leitura cruzada; nenhum conteúdo no handoff |
| Ciclo WhatsApp ↔ WebChat | `max_offers`, intervalo, nenhuma resposta automática |
| Corrida entre ativação, encerramento, expiração e retorno | trava da sessão guarda, CAS e relógio do banco |
| Convite proibido ou caro na Meta | itens da PRV-2-WC como gate; teto de ofertas; orçamento |
| SignalR esgotar recursos | tetos por sessão e tenant; polling em vez de estado em memória; medição antes de otimizar |
| Dado sensível exposto a quem só tem o link ou a recuperação atestada | `assurance_level`; `L0` e `L0A` nunca satisfazem operação sensível |
| Item de continuação perde posição, atendente ou cadeia de indicadores | entrada por continuação, `QueueOrderAt`, pendência que impede a reivindicação, indicadores por cadeia |
| Satélite virar atendimento operacional | regras fixas no Conversations, uma por teste, com mutação |
| Perda de mensagem do satélite | ingestão idempotente pelo caminho normal e Inbox durável |
| Recuperação sequestrada | prova por método, rotação por geração, limites, auditoria e alerta |
| Recuperação administrativa por engano ou abuso de funcionário | política do tenant, `StaffChecklist` (`L0A`), segundo aprovador, janela de histórico, justificativa, alerta |
| Verificador exfiltrado por XSS | memória e `sessionStorage`, janela curta, CSP com Trusted Types, queima no servidor |
| Handoff `Active` sem caminho de recuperação | oferta condicionada a método disponível; alerta; nenhuma ação automática |
| Encerramento indevido, duplicado ou com ocorrências pendentes | 9ª operação com idempotência antes da cerca, pré-condição imutável e reconhecimento conferido pelo Conversations |
| Mensagem aceita e processada depois do encerramento cria sessão implícita ou some | ingestão vinculada (`BoundSessionId`, modo da instância), `BoundSessionClosed` sem gravar, quarentena rastreável |
| Capacidade excedida por corrida entre continuidade e `ClaimNext` | reserva existente (`SaveWithinCapacityAsync`) sob a mesma trava `(fila, atendente)` e contagem que inclui `Enter` com alvo |
| Revogação do canal entre a consulta e a aplicação da ativação | revalidação sob a trava (porta do ADR 0008), falha fechada, credencial só depois de reconferir a situação da instância; a atomicidade entre módulos não é prometida |
| Ampliação do privilégio do gateway | segunda emenda ao ADR 0009, inventário de emissores e mutações |

## Critérios de aceitação

### Encerramento do atendimento (5.1b)

| ID | Critério |
|---|---|
| AC-ENC01 | Autorização do encerramento (ADR 0010, D.19): 404 para item invisível, 403 auditado, responsável efetivo e do item, supervisão. |
| AC-ENC02 | Idempotência: o mesmo `RequestId` devolve o resultado original mesmo com a autorização alterada; na operação 9, `OperationId` com efeito diferente é `OperationIdReused`. |
| AC-ENC03 | Item com operação pendente: `ConcurrentChange`, nada gravado; `Close` pendente bloqueia as demais. |
| AC-ENC04 | Pré-condição imutável (`HandlingChanged`); só a versão é renovável (`StaleState`); idempotência antes da cerca (`FencedOut` só sem efeito prévio). |
| AC-ENC05 | Aplicação atômica com fronteira certificada; o item fecha pela projeção; `AlreadyClosed` converge sem erro; reconciliação após queda por `GetEffectAsync(SessionClose)`. |
| AC-ENC06 | Auditoria durável com ator original, origem e instante. |
| AC-ENC07 | O teste de superfície do gateway espera o número de operações **implementadas**: **oito no MVP** (as seis atuais, a 7ª e a 9ª); o total **previsto** é **nove**, com a 8ª (`RequestDelegatedResend`, ADR 0014) Proposta e fora do MVP, mantida no planejamento futuro. O inventário de emissores tem **um** emissor para o encerramento; o `CloseSession` direto do Conversations fica inalterado. |
| AC-ENC08 | Só atendimento humano com item; sessão em modo IA ou sem item não é encerrável por este caso de uso. |
| AC-ENC09 | Mutações vermelhas: cerca antes da idempotência; pré-condição renovada; autorização reavaliada antes de reconhecer a repetição; item não ocioso aceito. |

### WebChat (5.2w)

| ID | Tema | Critério |
|---|---|---|
| AC-WC01 | multi-tenancy | Convite do tenant A recusado no domínio do tenant B; handoff, sessão e marcas de A invisíveis a B; nomes de grupo do hub incluem tenant e sessão; cenário com dois tenants em paralelo não mistura. |
| AC-WC02 | multi-tenancy | Tenant sem WebChat habilitado: nenhum comportamento muda (regressão da suíte); migrações aditivas; padrão `integral`. |
| AC-WC03 | idempotência | Ofertas concorrentes ou repetidas geram **1** handoff e **1** convite; `AlreadyOffered` nas demais. |
| AC-WC04 | idempotência | Ativação repetida com o mesmo `ClaimId` devolve a mesma sessão; `ClaimId` diferente com handoff `Active` devolve `AlreadyActivated`. **Revalidação:** com a unidade efetiva da origem (relida na ativação, inclusive depois de transferência enquanto `Invited`) não autorizada para a instância WebChat, a ativação é recusada com `TargetNotAuthorizedForChannel`: o handoff vai a `Cancelled` (com `ChannelHandoffEndedV1`), a origem **permanece aberta** e **nenhuma** sessão, contato ou conversa de destino é criado; com a consulta falhando (banco do `Channels` fora do ar), a exceção propaga, nada é gravado e a repetição com o mesmo `ClaimId` ativa depois; instância `Inactive` ou `Suspended`: o claim vai a `Failed(ChannelUnavailable)` e o handoff segue `Invited`; se a instância deixa de estar `Active` antes da entrega da credencial, nenhuma credencial é entregue e há alerta. Mutações vermelhas: ativar sem reler a unidade; fechar a origem antes da autorização. |
| AC-WC05 | idempotência | `client_message_id` repetido **na mesma sessão do navegador** grava **1** linha no Inbox e **1** mensagem; confirmação duplicada é inofensiva. **Identidade:** o `ProviderEventId` é `{webchat_session_id}:{client_message_id}`; o mesmo `client_message_id` em **duas sessões** gera **duas** mensagens independentes, e nenhuma devolve a da outra; identificador fora de UUID é rejeitado. **Ingestão vinculada:** instância `BoundSession` sem `BoundSessionId`, instância `SessionResolution` com `BoundSessionId` e sessão de outro contato ou instância lançam `BoundSessionIngestionException` sem gravar nem criar sessão; com a sessão fechada, `IngestOutcome.BoundSessionClosed` sem gravar, e o evento vai à quarentena com auditoria, métrica e alerta, **sem resposta ao WhatsApp**; repetição de mensagem já registrada devolve `Duplicate` mesmo com a sessão fechada; os canais existentes seguem inalterados (suíte atual verde). Mutações vermelhas: `ProviderEventId = client_message_id`; decidir o modo pelo comando e não pela instância; criar sessão no modo `BoundSession`; descartar `BoundSessionClosed` sem rastro. |
| AC-WC06 | concorrência | Ativação × encerramento da origem × expiração × cancelamento: exatamente um resultado válido, determinístico. |
| AC-WC07 | concorrência | Retorno × encerramento do destino: um vence; o outro é inofensivo. |
| AC-WC08 | concorrência | Ordem de travas verificada: sem deadlock entre ativação e ingestão com criação de sessão. |
| AC-WC09 | segurança | Token adulterado, vencido, de outra versão ou de outro tenant: resposta uniforme e nada consumido. |
| AC-WC10 | segurança | Requisição GET (pré-visualização) não consome o convite. |
| AC-WC11 | segurança | Segundo navegador sem prova de sessão não estabelece sessão; mesma sessão em duas abas retoma. |
| AC-WC12 | segurança | Token ausente de log, linha de tabela, mensagem do Conversations, histórico e contexto de IA. |
| AC-WC13 | segurança | Ticket do hub de uso único e vida curta; sessão revogada desconecta; querystring fora do log. |
| AC-WC14 | segurança | Limites de taxa por IP, tenant e sessão; teto de conexões; teto de ofertas por raiz e período. |
| AC-WC15 | segurança | Com garantia `L0` ou `L0A`, nenhuma operação sensível é atendida; só garantia igual ou superior à exigida pela operação (teste de contrato). |
| AC-WC16 | continuidade | A sessão destino **não** contém mensagens da origem; leitura da origem pelo atendente da sessão destino exige a autorização normal dela; o vínculo só aparece a quem lê as duas. |
| AC-WC17 | continuidade | A origem é encerrada com fronteira certificada; o item da origem fecha pelo mecanismo existente. |
| AC-WC18 | continuidade | Transferência de fila ou atendente durante `Invited` e `Active` não quebra o handoff; operação pendente no item de origem gera nova tentativa com recuo. |
| AC-WC19 | continuidade | Mudança de política vale só para ofertas novas; instância `Suspended` para o tráfego; ciclos limitados por `max_offers`. |
| AC-WC20 | transporte | Reconexão devolve exatamente as mensagens após `lastSeq`, sem duplicata nem omissão, na ordem de `MessageSeq`. |
| AC-WC21 | custo | Convite e retorno geram lançamentos no razão com preço da tabela vigente; nenhuma tarifa em código; custo estimado e confirmado separados. |
| AC-WC22 | indicadores | Funil por tenant consistente com os estados do handoff; economia rotulada **estimativa** com premissas visíveis. |
| AC-WC23 | continuidade de fila | Item de continuação em espera ocupa a posição da cadeia: com outros itens de mesma prioridade, fica à frente dos que entraram depois da raiz. |
| AC-WC24 | continuidade de fila | Durante a operação pendente, `ClaimNext` concorrente **nunca** reivindica o item (teste determinístico com `Interceptador`). **Capacidade:** `ClaimNext` do atendente-alvo (em outro item) concorrente com a entrada por continuação **nunca excede** `MaxItemsPerAgent`: a mesma trava `(fila, atendente)`, a contagem inclui a pendência `Enter` com atendente-alvo, e quem perde a disputa degrada (item sem alvo, `Waiting` na posição original). Mutação vermelha: contagem sem a pendência `Enter`, ou continuidade fora da trava. |
| AC-WC25 | continuidade de atendente | Atendente-alvo elegível assume (`SameResponder`); não elegível por membro, **capacidade (reserva negada)**, pausa ou conflito gera `ResponderUnavailable(motivo)` com o item `Waiting` na posição original; sem `MaxItemsPerAgent` na fila, a capacidade não é limite. |
| AC-WC26 | continuidade de atendente | O responsável vem do evento (Conversations), não de `I1.AssignedUserId`: se divergem, vale o do Conversations. |
| AC-WC27 | indicadores | Indicadores por cadeia: um atendimento, espera e atendimento somados sem duplicar; fechamento `channel-handoff` não conta como concluído. |
| AC-WC28 | continuidade | Número público igual nas duas sessões; tempo acumulado contínuo na visão do atendente. |
| AC-WC29 | regressão | Regressão da etapa 4 (`ListWaiting`, `ClaimNext`, ordenação, `ScopeProof`) verde; **mutação** "ler atributo de continuidade em autorização" fica vermelha. |
| AC-WC30 | continuidade | Handler idempotente e independente da ordem entre `SessionClosedV1` da origem e `ChannelHandoffActivatedV1`. Predecessor com operação pendente gera nova tentativa e depois `QueueFallback`. |
| AC-WC31 | satélite | Mensagem do WhatsApp com handoff `Active` e origem encerrada cria **exatamente um** satélite, não reabre a origem e não cria item, número público nem turno de IA. |
| AC-WC32 | satélite | N mensagens repetidas ou concorrentes: 1 satélite, N mensagens, repetição por `ProviderEventId` inofensiva. |
| AC-WC33 | satélite | `KeepWebChatActive`: nenhuma mensagem de saída é gerada, o handoff segue `Active`, e a ocorrência é única e coalescida. |
| AC-WC34 | satélite | `ReturnOnExplicitRequest`: sem pedido não retorna; com pedido válido e mensagem posterior, retorna. `ReturnOnCustomerMessage` retorna na primeira mensagem. |
| AC-WC35 | satélite | O satélite recusa resposta, transição, `Enter`, turno de IA **e encerramento direto** (um teste por regra, com mutação por regra). |
| AC-WC36 | satélite | Leitura das mensagens do satélite exige a autoridade de leitura do item da sessão destino, com revalidação, e handoff `Active` (ou o satélite dentro da fronteira). O histórico da origem **não** é exposto. **Limites:** inferior = início do período do item (`satellite_base_seq + 1`) para a supervisão e `satellite_read_from_seq` para o responsável efetivo (quem assume depois **não lê** o que chegou antes de assumir); superior = o menor entre a sequência vista e `SatelliteBoundarySeq` (satélite fechado ou promovido); os limites do item WebChat não se aplicam ao satélite nem o ampliam; depois da promoção, o item encerrado não lê mensagens novas; mudança de fila, responsável, período, colocação, estado ou `version` do handoff durante a leitura descarta a página (409); sem autoridade, 404 ou 403 auditado; falha ao ler a `LastMessageSeq` deixa o limite sem leitura. Mutações vermelhas: aplicar `history_from_seq` do item ao satélite; omitir a fronteira após a promoção; não revalidar o handoff; gravar `satellite_read_from_seq = 1` a quem assume depois. |
| AC-WC37 | satélite | A promoção do satélite só ocorre por (a) política habilitada pelo tenant ou (b) decisão explícita `PromoteSatellite`. **Nunca por omissão** no encerramento. Atômica e idempotente. |
| AC-WC38 | satélite | Nenhum ciclo de convite: teto de ofertas e intervalo respeitados após o retorno. |
| AC-WC39 | recuperação | Cada método de recuperação exige a sua prova (WhatsApp: remetente igual ao `source_contact` e código válido; administrativa: política, verificação, permissão, justificativa e auditoria). Prova inválida: resposta uniforme, sem custo. |
| AC-WC40 | recuperação | Após recuperar, o convite e a credencial anteriores deixam de valer (`invite_version` e geração). |
| AC-WC41 | recuperação | Credencial antiga fora da janela é **recusada e auditada** e **não revoga** a sessão válida. Repetições excessivas geram alerta, não revogação. |
| AC-WC42 | recuperação | Limite de recuperações por handoff e por contato; excedido: recusa sem enviar mensagem. |
| AC-WC43 | recuperação | Um dispositivo por handoff: a recuperação desconecta a conexão anterior. |
| AC-WC44 | recuperação | Registrar um método de recuperação adicional não altera o protocolo (teste de contrato com método fake). |
| AC-WC45 | ativação | Queda injetada (`Interceptador`) em cada passo do protocolo: ao final, exatamente **uma** sessão do navegador, **um** claim e **uma** ativação. |
| AC-WC46 | ativação | Segundo navegador (outro `challenge`) nunca assume convite em `Started`, `Activated` ou `Established`. |
| AC-WC47 | ativação | Resposta perdida: repetição com o verificador reemite credencial para a mesma sessão. Ao estabelecer, o verificador é **apagado do navegador** (`sessionStorage` e memória) e **queimado no servidor**; nunca em `localStorage`, cookie, URL ou log. |
| AC-WC48 | ativação | A conciliação repara claim `Activated` sem sessão e claim `Started` vencido com handoff `Active`. |
| AC-WC49 | ativação | Duas abas do mesmo navegador retomam o mesmo claim. |
| AC-WC50 | ativação | **Mutações** vermelhas: claim sem índice único (2 sessões); `ClaimId` gerado só no Conversations (perde o vínculo); verificador aceito depois de `Established`; sessão sem chave única por handoff; conciliação ausente; verificador em `localStorage`; revogar sessão por credencial antiga; promover satélite no encerramento por omissão; encerramento sem reconhecimento. |
| AC-WC51 | recuperação | Com o WhatsApp indisponível, o método administrativo **configurado** restaura o acesso; sem método disponível, o convite não é ofertado (`NotEligible(NoRecoveryPath)`). |
| AC-WC52 | recuperação | Histórico por garantia: L1 e L2 no limite original; `L0A` a partir da recuperação; nenhuma expõe a sessão de origem nem sessões anteriores da cadeia. |
| AC-WC53 | recuperação | Recuperação administrativa: desligada sem política; exige o método de verificação da política; segundo aprovador distinto do solicitante quando exigido; registro completo (verificação, solicitante, aprovador, justificativa, destinatário, entrega, garantia, janela); link exibido uma vez e **nunca armazenado**; limite por taxa; alerta; a permissão sozinha **não** concede acesso; **`L0A` não satisfaz operação sensível** (teste de contrato). |
| AC-WC54 | satélite | Encerramento do WebChat com ocorrências não reconhecidas e sem promoção é recusado (`PendingSatelliteOccurrences`), inclusive na corrida entre reconhecimento e mensagem nova; o reconhecimento é conferido contra o `last_inbound_seq` **existente** da sessão satélite (nenhuma coluna nova). **Recusa terminal:** o ContactCenter cancela a operação pendente `Close` (item ocioso de novo), grava o recibo `Cancelled` com a `LastInboundSeq` observada, a repetição do mesmo `RequestId` devolve a mesma recusa sem reavaliar, e a nova tentativa exige novo `RequestId`, nova leitura e novo reconhecimento. **Encerramento direto:** o destino de handoff `Active` sem satélite conclui `Completed` na mesma transação; o destino com satélite e a sessão `Satellite` vinculada a handoff `Active` são recusados (`SatelliteDecisionRequired`) sem gravar, mesmo para quem tem `ConversationManage`; sessões sem handoff `Active` mantêm o comportamento atual (suíte existente verde). Mutações vermelhas: manter o item com `Close` pendente depois da recusa; encerrar direto o satélite; deixar o handoff `Active` com o destino fechado. |
| AC-WC55 | satélite | `CloseSatellite` fecha sessão e satélite e conclui `Completed`; `PromoteSatellite` exige permissão própria e justificativa, conclui `ReturnedToWhatsApp` e aciona a continuidade; todas as mensagens ficam preservadas. |
| AC-WC56 | satélite | O reconhecimento só cobre o que a leitura entregou ao usuário; sem leitura prévia, é recusado; avança só de forma **contígua**: quem tem limite inferior acima da lacuna não reconhece além dela. |
| AC-WC57 | segurança | Cabeçalhos do portal presentes em toda resposta (CSP sem `unsafe-inline` nem `unsafe-eval`, `nosniff`, `no-referrer`, `frame-ancestors 'none'`). |
| AC-WC58 | segurança | Corpus de cargas XSS em mensagem, nome de arquivo e identidade visual renderiza inerte; anexo HTML, SVG ou PDF sai como download; lint proíbe `innerHTML`. |
| AC-WC59 | ativação | Falha de ativação: verificador perdido gera os resultados da seção D.6 (claim vencido libera o convite; sessão em `AwaitingProof` só recupera pelos métodos), com indicador e alerta de "ativou e não estabeleceu". |
| AC-WC60 | recuperação | Indisponibilidade de **todos** os métodos de recuperação de um handoff `Active` gera **somente alerta**: nenhum encerramento, cancelamento, promoção ou reversão automática (teste e mutação "encerrar quando a recuperação falha"). |

### Testes arquiteturais (novos)

1. A matriz e o grafo de projetos permanecem iguais (ARQ-06).
2. O namespace do Receiver do WebChat não referencia outro módulo; a API do portal só referencia `Conversations.Contracts`.
3. O `Conversations` não referencia o `Channels`; o `ContactCenter` não referencia `Channels.Contracts`.
4. A tabela `channel_handoffs` não tem coluna de conteúdo de mensagem nem de segredo.
5. Teste de esquema: nenhuma coluna de token ou segredo em `webchat_session` além de hashes.
6. Todo método do hub exige autorização (varredura por atributo).
7. `MessageAuthor.System` tem um único ponto de criação (`OfferChannelHandoff`).
8. Teste de log: o token não aparece em saída de log nas rotas do portal.
9. O inventário de emissores dos comandos privilegiados ganha **um** emissor, o do encerramento (AC-ENC07).

## Migrações propostas (nenhuma criada)

| # | Módulo | Conteúdo | Fatia | Risco |
|---|---|---|---|---|
| 11 | Channels | valor `WebChat`, `webchat_config`, `webchat_claim`, `webchat_session`, gerações de credencial, `webchat_recovery_grant`, marcas de despacho, `cost_entry`, `price_table` | 5.2w | médio |
| 12 | Conversations | `channel_handoffs`, `role` e `continuity_handoff_id` na sessão (**reaproveita o `last_inbound_seq` existente**), `MessageAuthor.System`, motivo de encerramento | 5.2w | médio |
| 13 | ContactCenter | `queue_order_at` (com novo índice de ordenação), colunas de continuidade, `satellite_base_seq` e `satellite_read_from_seq` no item, `satellite_acknowledgements` (por satélite), marcador `delivered_through_seq`, `satellite_closed_through_seq`, atributo de fila "oferta de WebChat"; `ck_queue_items_pending` admite atendente-alvo em `Enter` | 5.2w | médio (altera a ordenação da fila) |
| 14 | ContactCenter | valor `Close` na operação pendente e em `ck_queue_items_pending`, recibo `Close` | 5.1b | baixo |

Todas aditivas em dados, com `Down` e plano de rollback; aplicação só com autorização expressa (DP-07).

## Pendências

- **Gates:** REC-2 (cobre também a resolução do tenant por domínio do WebChat), PRV-2 e **PRV-2-WC** seguem **pendentes**; nenhum é declarado cumprido. A PRV-2-WC bloqueia a 5.2w-b (convite real), não a 5.2w-a.
- **Dependência:** a segunda emenda ao ADR 0009 (9ª operação) e os ADRs 0009 (parcial), 0010 e 0011 foram aprovados em 03/11/2026 (DP-01), o que libera, depois da autorização de implementação, o encerramento da 5.1b. Os ADRs 0013, 0015 e as demais partes deste ADR seguem Propostos, e a aprovação do restante do 0016 (DP-01) continua pendente para a 5.2w. O DP-04 (porta de capacidades do host) passa a ser necessário à 5.2w-a.
- **Decisões do WebChat:** situação em `ETAPA-5.md` §8.3. **Aprovação conceitual não autoriza implementação.**
- **A definir na 5.2w:** validade do convite e do claim, `establish_timeout`, validade do código de recuperação, limites de recuperação, janela de rotação de credencial, limiar de alerta de credencial antiga, valores de `max_offers` e do intervalo mínimo entre ofertas, tempo de espera da continuidade do predecessor.
- **Riscos residuais não críticos (a validar na implementação):** a autorização da ativação vale no instante da consulta, porque não há atomicidade entre `Channels` e `Conversations` (ADR 0008); o limite de leitura do responsável é calculado a partir de uma leitura do satélite, de forma conservadora e não atômica; ocorrências do satélite de períodos anteriores só são alcançadas pela supervisão; a mensagem em quarentena é tratada por sinalização operacional, sem resposta ao WhatsApp. **Já aprovadas conceitualmente (03/11/2026):** a continuidade respeita `MaxItemsPerAgent`; a recusa definitiva na ativação cancela o convite; o evento `ChannelHandoffEndedV1`; a sinalização rastreável da quarentena.
- **Verificações antes de implementar:** se o executor de intenções do ContactCenter aceita ator de sistema ao atribuir o item de continuação (a `PendingActorOrigin` existe, mas não há atribuição por sistema hoje); e se o motivo `channel-handoff` do encerramento não tem efeito colateral inesperado no ContactCenter.
- **Frontend e pacotes novos** (portal React, cliente SignalR, PWA): exigem aprovação (CLAUDE.md, DP-11).
