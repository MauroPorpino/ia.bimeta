# ADR 0011 — Channels: Receiver, Inbox, Sender, conciliação da REC-2 e situação dos gates da etapa 5

- **Status:** **Aceito parcialmente (03/11/2026), para o escopo do MVP.** Nada deste ADR está implementado.
- **Registro de aprovação (03/11/2026, decisor):** aprovados, depois dos ajustes de PRV-2 e REC-2 registrados em A.1 e A.2: a seção A (A.1 e A.2, WhatsApp), a B nos itens 1 a 5 (núcleo comum, adaptadores, capacidades, categorias, com o adaptador fake nos testes e o WhatsApp como único adaptador real), as seções C, D, E e F, a G no item 1 (anexos adiados no MVP, DP-05; o item 2 foi decidido: adiar), a política inicial de retenção do Inbox (DP-06, em C.6) e as migrações 1 e 2 da seção I. **Permanecem Propostos:** a B.6 (capacidades do canal no ContactCenter, DP-04), a H (extensibilidade futura), as migrações 8 e 9, os adaptadores de Instagram, Messenger, e-mail e SMS e os itens estendidos da A.2 (gate por adaptador, 5.3 a 5.6). O WebChat segue no ADR 0016. A aprovação de uma parte não aprova o restante.
- **Data:** 30/10/2026 (aprovação parcial: 03/11/2026)
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` v0.4.6 (seções 1, 3, 4, 6, 11 e 12), `docs/architecture/CONVERSATION-LIFECYCLE.md`, `docs/architecture/module-dependencies.json`, `docs/architecture/ETAPA-5.md`, ADRs 0005, 0006, 0007, 0008, 0009 e 0010, `CLAUDE.md`
- **Relacionados (etapa 5):** 0012 (numeração e associação, aceito parcialmente), 0013 (colaboração e mensagens internas, Proposto), 0014 (resposta humana externa, aceito parcialmente), 0015 (e-mail e SMS, Proposto), 0016 (WebChat nativo e continuidade entre canais, aceito parcialmente: só a seção M), 0017 (fatia mínima de IA, Aceito em 03/11/2026)
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**: `Channels → {Conversations, Identity, Tenancy, Audit}` já existe; não nasce `ContactCenter → Channels` nem `Conversations → Channels`) e os ADRs 0001 a 0010 (em 03/11/2026, o 0010 foi aceito e o 0009, aceito parcialmente; ver o status de cada um).
- **Registra:** (a) a **conciliação proposta da REC-2** com o cadastro implementado (pendência obrigatória do ADR 0008); (b) a **situação dos gates PRV-2 e REC-2**; (c) o desenho do Receiver, do Inbox, do Sender, da idempotência, da cerca e do `SendUnknown` no `Channels`; (d) a generalização do cadastro para os cinco canais; (e) um novo tipo de adaptador (`backend/adapters/IaBimeta.Adapters.*`, `BOUNDARIES.md` §11).

## Contexto

O `Channels` implementado cobre só o **cadastro** do WhatsApp: conta (`ChannelAccount`, a WABA), instância (`ChannelInstance`, o número, com situação `Inactive`, `Active` ou `Suspended`), unidades autorizadas, **rota de entrada** (`channels.channel_routes`: provedor e identificador externo → instância e tenant) e segredos cifrados (`ChannelSecret`: `AccessToken`, `AppSecret`, `WebhookVerifyToken`, com AES-GCM amarrado a tenant, identificador e papel). Não há endpoint de webhook, Inbox, Sender nem adaptador de provedor. A única porta de entrada de mensagens é o simulador de desenvolvimento.

A etapa 5 precisa de cinco canais (WhatsApp Cloud, Instagram Direct, Messenger, e-mail e SMS) sobre **um** núcleo comum, sem que `Conversations` e `ContactCenter` conheçam provedor. O `BOUNDARIES.md` §12 (ADR 0006) já define os três papéis (Receiver, Processing, Sender), o Inbox, a fila de envio, a idempotência e o tratamento do efeito externo. Faltavam: a conciliação com o que foi implementado, o registro honesto da situação dos gates e a extensão dos mesmos mecanismos para canais que não são webhook de chat (e-mail por sincronização, SMS com custo e supressão).

## Decisão

### A. Situação dos gates (30/10/2026; atualizada em 03/11/2026)

#### A.1 REC-2 — divergência registrada, conciliação **aprovada em 03/11/2026**

**Fato.** A REC-2 (§12.2) descreve a resolução do canal por uma projeção local `channels.channel_account`, e a REC-1 prevê localizar o canal por um identificador opaco na URL, verificar a assinatura **antes** de interpretar o conteúdo e só então identificar o tenant. O cadastro implementado resolve por `channels.channel_routes` (chave: provedor e identificador externo do **número**, que vem **no payload**), sem endpoint de webhook e sem identificador opaco de URL. Aplicar a rota antes da assinatura exigiria ler o payload antes de autenticá-lo, contra a REC-1.

**Conciliação aprovada** (DP-03, 03/11/2026):

1. **A rota é a projeção local da REC-2 para o segundo passo.** `channel_routes` (imutável, estreita, sem filtro de tenant na leitura, com chave estrangeira composta para a instância) continua sendo a tabela que leva o identificador do número, da página ou da caixa à instância e ao tenant. **Não se cria `channels.channel_account` paralela**: duplicaria um estado que o cadastro já guarda com integridade, e o ADR 0008 já rejeitou cópia desse tipo.
2. **Primeiro passo novo: identificador opaco por conta.** A conta (`ChannelAccount`) ganha um identificador opaco de webhook, que aparece na URL e leva à conta e à referência do segredo de assinatura (`AppSecret`) dela, **sem ler o corpo**. A verificação da assinatura sobre o corpo bruto (comparação em tempo constante) acontece **antes** de interpretar o conteúdo, como a REC-1 manda.
3. **Conferência cruzada depois da assinatura.** Cada evento do lote é resolvido em `channel_routes` e **conferido contra a conta da URL**: instância de outra conta ou de outro tenant é **descartada** (REC-3, conta desconhecida no payload) e contada, nunca processada.
4. **Estado.** Só instância `Active` grava no Inbox. Instância `Inactive` ou `Suspended`: o evento não é gravado, responde-se 200 e conta-se na métrica (`inactive_channel`), conforme o contrato de `ChannelStatus` ("o Receiver e o Sender recusam as demais"). Reter eventos de canal suspenso para entregar na reativação é decisão de fatia (ver Pendências).
5. **Cache.** Conta, rota e segredo resolvem por cache em memória com validade e invalidação (REC-2), montado na administração do canal, quando o `Channels` consulta `Tenancy.Contracts`, e nunca durante o webhook.
6. **Segredo de assinatura.** O `AppSecret` pertence ao **aplicativo** da Meta (PRV-2, item 3): contas do mesmo aplicativo podem compartilhar o valor, e o que as distingue é o identificador opaco da URL e a rota. A URL por conta depende do override de webhook por conta (WABA) previsto na documentação da Meta, a validar no teste com o número de teste (5.2).
7. **Texto normativo.** A conciliação foi registrada no `BOUNDARIES.md` §12.2 (REC-1, REC-2, REC-3, REC-4 e REC-6, v0.4.7), com uma linha no histórico. As responsabilidades não mudam: o `Channels` resolve, autentica e grava no Inbox, e o `Conversations` não conhece provedor, assinatura nem rota.

**Situação:** divergência **registrada** e conciliação **aprovada** (03/11/2026). O gate REC-2 está **cumprido**. A resolução em duas etapas, a verificação da assinatura sobre o corpo bruto antes da interpretação do payload, o uso de `channels.channel_routes` e a ausência de tabela `channel_account` paralela ficam preservados.

#### A.2 PRV-2 — **WhatsApp: cumprida com ressalvas (consulta de 03/11/2026)**; demais canais: não confirmados

A PRV-2 (§12.11) exige, antes de qualquer implementação do `Channels` na etapa 5, confirmar cada parâmetro do provedor na **documentação oficial** e registrar **data e fonte** neste ADR. Em **03/11/2026** os 11 itens do WhatsApp foram verificados, somente para leitura, na documentação oficial da Meta (versão em inglês). Fontes: `B` = `https://developers.facebook.com/documentation/business-messaging/whatsapp`; `G` = `https://developers.facebook.com/docs/graph-api/webhooks/getting-started`. O que a documentação não define **não** é atribuído à Meta como garantia.

| # | Item (§12.11) | Resultado | Fonte | Tratamento |
|---|---|---|---|---|
| 1 | Reenvio do webhook por até 7 dias | **Confirmado** | `B/webhooks/overview` | Deduplicação obrigatória (pode duplicar). A página genérica de webhooks do Graph (`G`) indica 36 h: usar como horizonte conservador de alerta, sem histórico consultável no provedor. O limite de corpo do Receiver admite lote |
| 2 | Tempo máximo para responder 200 | **Não documentado** | `B/webhooks/overview`, `G` (só pedem resposta 200) | Meta interna configurável (PRV-1), medida na 5.1a. Nenhuma garantia atribuída à Meta |
| 3 | Assinatura `X-Hub-Signature-256` | **Confirmado** | `G` ("Validating Payloads") | `sha256=` calculado sobre o payload com o App Secret **do aplicativo**. A página não detalha o tratamento de bytes e Unicode: teste com payload acentuado na 5.1a. Verificação por GET com `WebhookVerifyToken` (desafio); TLS com certificado válido (autoassinado não aceito) |
| 4 | Ordem de entrega | **Não documentado** | `B/webhooks/overview`, `G` (a ordem do lote "não pode ser garantida") | Nenhuma premissa de ordem: ORD-5 e ORD-6, lanes e status monotônico |
| 5 | Vazão de 80 mensagens por segundo | **Confirmado** | `B/throughput` | 80 mps por padrão, até 1.000 mps; conta entrada e saída; número em coexistência com o aplicativo WhatsApp Business: 20 mps. Parâmetro (PRV-1) e balde de fichas (SND-2) |
| 6 | Limite por par e erro 131056 | **Confirmado** | `B/about-the-platform` (limites de taxa) | 1 mensagem a cada 6 s por destinatário, rajada de 45, erro 131056, espera de 4^X s sugerida. Lane por (conta, destinatário) |
| 7 | Erros a retentar | **Confirmado** | `B/support/error-codes` | 130429, 131056, 131000: retentativa com espera, mapeada em configuração |
| 8 | Erros sem retentativa | **Confirmado** | `B/support/error-codes` | 131047 (janela fechada), 131049, 131026, 131048: falha definitiva (SND-5) |
| 9 | Erros de conta | **Confirmado** | `B/support/error-codes` | 190 e 368 pausam a conta e alertam (SND-6) |
| 10 | Janela de 24 horas e templates | **Confirmado** | `B/messages/send-messages` (janelas de atendimento), `B/about-the-platform` | Mensagem livre só dentro da janela; fora dela, só template aprovado (SND-5) |
| 11 | Chave de idempotência e `biz_opaque_callback_data` | **Parcial** | `B/reference/whatsapp-business-phone-number/message-api`, `B/webhooks/reference/messages/status` | O campo `biz_opaque_callback_data` (até 512 caracteres) é devolvido nos webhooks de status e permite correlação por evento (evidência `ProviderStatus`). **Chave de idempotência de envio: não encontrada.** O efeito do WhatsApp segue a **estratégia (c) (`None`)** (EFX-5); a estratégia (b) só depois de teste em sandbox. Resultado ambíguo é `SendUnknown` e **nunca é reenviado automaticamente** |

**Ressalvas registradas na consulta (não são itens do gate):** a cobrança de mensagens de serviço passa a valer depois de franquia por número, e os webhooks de status trazem o objeto `pricing`; os Termos da plataforma (cláusula 4.7, atualizada em 23/09/2026) condicionam os "Provedores de IA" a critério da Meta e proíbem o uso de dados da plataforma para treinar modelos (requisito para o AiGateway, ADR 0017); o onboarding de produção (aplicativo em modo Live, verificação, forma de pagamento) é prazo externo, e o número de teste da Meta permite desenvolver sem número de produção.

**Itens do WhatsApp: decisão do decisor (DP-02, 03/11/2026).** Itens 1, 3, 5, 6, 7, 8, 9 e 10 **confirmados**; itens 2 e 4 classificados como **não documentados pela Meta**, com tratamento técnico interno (parâmetro configurável e ausência de premissa de ordem); item 11 **parcialmente confirmado**, mantendo a estratégia conservadora e o `SendUnknown`. Isso é a **emenda formal** que a seção 9 do plano exige para os itens sem confirmação.

**Itens estendidos pela etapa 5 (indicativos, não confirmados, sujeitos ao mesmo gate):**

| Canal | Valor indicado | Fonte consultada em 30/10/2026 | O que falta |
|---|---|---|---|
| Instagram Direct | janela padrão de 24 h; etiqueta *Human Agent* (resposta humana até 7 dias) exige App Review próprio, envio por humano e caminho de escalonamento | developers.facebook.com, visão geral do Instagram Messaging | confirmar texto integral, permissões e condições |
| Messenger | janela padrão de 24 h; tags só para casos aprovados; mesma política do Instagram. Uma afirmação de blog sobre janela de 7 dias para anúncios **não foi confirmada** | developers.facebook.com, política da Messenger Platform | confirmar política vigente e casos de tag |
| Microsoft Graph (e-mail) | assinaturas de mensagens com validade de no máximo 4230 minutos; `sendMail` devolve 202 sem identificador (segundo fonte secundária) | learn.microsoft.com, notificações de mudança do Outlook | confirmar `sendMail`, rascunho com id e limites de assinatura |
| Gmail API | `users.watch` com validade de no máximo 7 dias; sincronização por `history.list`; escopos restritos exigem verificação do aplicativo (**não confirmado**) | developers.google.com, `users.watch` | confirmar expiração do `historyId`, verificação OAuth e cotas |
| IMAP/SMTP (Exchange Online) | autenticação básica já desativada para IMAP e POP; a do SMTP AUTH deve sair por padrão ao fim de dezembro de 2026 (segundo fonte secundária) | heise.de (secundária) | confirmar em documentação da Microsoft |
| SMS (Brasil) | sem cadastro central de "não perturbe"; lista de supressão própria; pedidos "SAIR" e "PARE"; diretório de códigos curtos da ABR Telecom; resposta (MO) por rota **não confirmada** | blog de fornecedor (Zenvia) | confirmar regras da Anatel e das operadoras e as condições do provedor escolhido |

**Situação:** gate PRV-2 **cumprido para o WhatsApp** (03/11/2026), nos termos acima. Para os demais canais (a tabela acima), o gate **não está cumprido** e vale **por adaptador** (5.3 a 5.6): para cada item, abrir a documentação oficial, conferir o valor, registrar **data e fonte** aqui e marcar "Confirmado". Isso é acesso externo, que depende de autorização do decisor.

**Alcance do gate (DP-02, decidido em 03/11/2026).** Vale a **leitura literal**: confirmados antes da 5.1a os itens do **WhatsApp** (§12.11) e a assinatura, que definem o Receiver e o Sender, e os itens dos outros canais ficam como gate de **cada adaptador** (5.3 a 5.6). A 5.1b **não** é bloqueada pela PRV-2, porque só toca `Conversations` e `ContactCenter`. A leitura por adaptador ficou descartada.

### B. Núcleo comum e adaptadores

1. **Generalização do cadastro (5.1a).** O identificador do número deixa de ser "número de telefone": a instância passa a ter **identificador externo** e **rótulo de exibição** genéricos (telefone, página, perfil, caixa, remetente), e as restrições `ck_channel_accounts_provider` e `ck_channel_routes_provider` deixam de amarrar o provedor a um valor fixo. `ChannelProvider` só ganha valor novo junto com o adaptador correspondente. Novos papéis de segredo (por exemplo, tokens OAuth de e-mail) usam a mesma proteção AES-GCM e a mesma rotação.
2. **Adaptadores em `backend/adapters/IaBimeta.Adapters.*`**, um projeto por adaptador, criado no primeiro uso e **com aprovação** (`BOUNDARIES.md` §10 e CLAUDE.md; DP-11). Eles verificam a autenticidade, normalizam, enviam e **declaram capacidades**. Portas de adaptador: `IMetaMessagingProvider`, `IEmailProvider`, `ISmsProvider`, atrás de um contrato comum de canal. Um adaptador fake valida a mecânica antes de qualquer provedor real, e o WhatsApp é o primeiro real (já tem cadastro e rota). O **WebChat** é um canal de **primeira parte** e **não** tem projeto de adaptador: o contrato de canal é implementado dentro do `Channels` (ADR 0016, Proposto).
3. **Princípio de isolamento.** `Conversations` e `ContactCenter` não terão `if (canal == X)`. O que é específico entra por três lugares: capacidades declaradas, o bloco opaco `ChannelData` (versionado, validado pelo adaptador) e o Receiver e o Sender.
4. **Capacidades declaradas** (por adaptador e por conta, em código no início, sem tabela própria): receber, enviar, texto, mídias e documentos, templates, status de entrega, confirmação de leitura, janela de atendimento, limites de envio, exigência de consentimento, e **o que não é suportado, de forma explícita**. Nenhum canal presume os recursos de outro. A rota unidirecional de SMS e as categorias `Public` e `Commercial` são **capacidade declarada como não suportada**, não erro.
5. **Categorias de interação.** `Private` (obrigatória e a única implementada), `Public` (comentários e avaliações) e `Commercial` (leads e formulários) são distintas no contrato. Só `Private` entra na etapa 5.
6. **Capacidades para o compositor do ContactCenter.** O ContactCenter precisa saber a janela, os limites e o tipo de compositor. A proposta é uma **porta do próprio ContactCenter, ligada pelo host ao `Channels`** (mesmo padrão do ADR 0008), **sem aresta nova** na matriz. A alternativa, a aresta `ContactCenter → Channels` por ADR, não cria ciclo, mas amplia o grafo. Decisão: DP-04 (recomendada: porta do host).

### C. Receiver (API) e Inbox

1. Aplica a REC-1 a REC-6 com a conciliação de A.1: corpo bruto, identificador opaco da conta, verificação **antes** de interpretar, separação do lote, `INSERT … ON CONFLICT DO NOTHING` no `channels.inbox_event` numa transação, commit, **HTTP 200 só depois do commit**. Sem chamada a outro módulo, à IA ou ao provedor (ARQ-19); sem conteúdo de mensagem em log; limite de tamanho do corpo (que admite o lote do provedor). O pedido de verificação do provedor (GET com desafio, `WebhookVerifyToken`) é atendido pelo mesmo identificador opaco da conta.
2. Respostas: identificador de conta desconhecido ou inativo na URL, **404** sem detalhe; assinatura inválida, **401** e nada gravado; conta ou número desconhecido (ou de outra conta) no payload, descarta, conta e responde 200; falha de gravação, **5xx**.
3. **Colunas e tempos.** As colunas padrão de §12.5, mais `provider_timestamp`, `received_at` e `ingest_seq` (REC-5). A chave de deduplicação é o identificador da mensagem no provedor por conta; para status, o hash de (identificador, status, instante).
4. **Estados do evento de entrada.** Além de `Pending`, `Processing`, `Done` e `Dead`, existe `PendingAssociation` (associação de e-mail que não pôde ser decidida, ADR 0012) e a tabela `channels.inbox_quarantine`, visível ao administrador, para o evento que **não normaliza** ou não associa dentro do limite. Quarentena **nunca** vira sessão de chat nem é anexada por palpite.
5. **Canais sem webhook de chat.** O e-mail por sincronização (Graph, Gmail, IMAP) é **pull**: o adaptador grava os eventos no mesmo Inbox, com o cursor avançando na **mesma transação** (ADR 0015). Callbacks de SMS usam o mesmo Receiver e o método de autenticação do adaptador.
6. **Retenção do Inbox e do payload bruto (DP-06, política inicial aprovada em 03/11/2026):** **payload bruto** retido por 7 dias depois de `Done`; **metadados do Inbox** por 30 dias; **quarentena** até resolução manual; **mensagens mortas** até reenfileiramento ou descarte autorizado. São parâmetros **iniciais e configuráveis**, sujeitos a validação operacional e às obrigações legais aplicáveis. Purga dos processados por política, sem conteúdo em log.

### D. Ingestão (Processing)

1. O worker de ingestão percorre o Inbox **por lane** e chama `IngestInboundMessage` ou `RecordDeliveryStatus` (PRO-1). Lane padrão: (canal, contato). **E-mail:** a lane de associação é a **caixa**, com `pg_advisory_xact_lock` sobre a caixa na transação de ingestão (ADR 0012).
2. `IngestInboundMessage` ganha `AssociationMode`, `ThreadKey`, `SenderContact`, anexos, `Category` e `ChannelData` (ADR 0012). `RecordDeliveryStatus` ganha `EvidenceSource` (seção F). Os dois acréscimos são **aditivos** em `Conversations.Contracts`, dentro de aresta já permitida.
3. **Reavaliação obrigatória do ADR 0008** antes do worker de ingestão: medir a latência da consulta sob a trava da sessão e o uso do pool de conexões. Mitigações previstas, sem mudança estrutural: limite de concorrência por tenant (LIM-1) e tamanho do pool. Qualquer outra forma de verificar exige novo ADR.
4. **Limites por tenant desde o primeiro processamento concorrente** (LIM-1), contados no banco e configuráveis sem redeploy.

### E. Sender, idempotência e cerca

1. **Entrada.** O Sender consome `OutboundMessageRequestedV1` pelo `InboxGate` (idempotente por `EventId`) e cria `channels.outbound_message` (`Pending`) com `operation_id` **igual ao `OperationId` da mensagem** (a chave de idempotência do efeito, que não muda entre tentativas) e índice único `(tenant_id, operation_id)`.
2. **Lane** por (conta, destinatário), envio sequencial: o próximo só sai depois da resposta da API do provedor, não da entrega (SND-1). Limites do provedor são **parâmetros** (PRV-1), com balde de fichas no banco (SND-2).
3. **Estados** `Pending → Sending → Sent`, e `Failed`, `Expired`, `SendUnknown` e `Dead` (SND-3). `expires_at` é validade **operacional** (SND-4), **não** a janela de 24 horas do WhatsApp, verificada à parte antes de enviar mensagem livre (SND-5). Erro de conta pausa a conta e alerta (SND-6).
4. **Protocolo do efeito (EFX-2).** Antes de chamar o provedor, a intenção é gravada e confirmada no banco (`effect_state`: `Intent → Dispatching`); a chamada acontece **sem** manter transação nem conexão aberta; o desfecho grava `Confirmed` ou `Unknown`. A estratégia vem **declarada pelo adaptador**: (a) idempotência comprovada (documentação **e** teste), (b) reconciliação por identificador próprio, ou (c) nenhuma.
5. **Lease e token.** O lease por lane renova por heartbeat, e concluir exige o `lease_token` vigente. **Isso impede processamento concorrente, mas não efeito externo duplicado (EFX-1).** Por isso a cerca de efeito é a **intenção gravada antes** mais o `operation_id` estável, e não o lease. Lease perdido com a chamada possivelmente enviada leva a `SendUnknown`, nunca a `Pending`.
6. **Recuperação de `Dispatching` com lease expirado.** Estratégia (a): repete com a **mesma** chave. Estratégia (b): consulta o provedor pelo identificador próprio, e só a **evidência** resolve. Estratégia (c): `SendUnknown`. **Nenhum componente repete `SendUnknown` automaticamente** (EFX-3).
7. **Falha definitiva** (`Failed`, `Expired`, `Dead`) vai ao `Conversations` por `RecordDeliveryStatus`, que publica `OutboundMessageFailedV1`; não é entrada para a IA (SND-7, `CONVERSATION-LIFECYCLE.md` §11).
8. **Ordem e atraso.** `ingest_seq`, `provider_timestamp` e `received_at` preservados (ORD-5); status segue máquina monotônica e nunca regride; status que chega antes do identificador fica estacionado, com retentativa limitada (ORD-6).

### F. `SendUnknown` e evidência de entrega

1. **Classes de evidência.**

| Classe | Exemplos | Efeito |
|---|---|---|
| **Evidência de envio** | status do provedor com o **nosso** identificador; busca por `Message-ID` próprio na pasta de enviados; consulta por referência do cliente no SMS | `Sent`, `Delivered` ou `Read`, com a origem registrada |
| **Evidência de não envio** | falha definitiva do provedor ligada a **esta** tentativa (status de falha com o nosso identificador; rejeição definitiva da chamada) | `Failed`, `Expired` ou `Dead` |
| **Ausência de confirmação (não é prova)** | tempo esgotado; busca que não encontra algo numa janela; ausência de webhook; **declaração humana** | **`SendUnknown` permanece** |

2. `RecordDeliveryStatus` ganha `EvidenceSource = ProviderStatus | ProviderLookup | ProviderRejection | Manual`. **`Manual` nunca produz `Failed`, `Expired` nem `Dead`**: o domínio recusa (`NotAllowed`).
3. A **declaração humana** ("conferi no console do provedor e não vi a mensagem") é uma **anotação auditada**: não muda o estado e não habilita reenvio. O `ConfirmedSent` manual é possível (DP-08, aprovado em 03/11/2026), com origem `Manual`, **permissão específica**, justificativa e auditoria; só **avança** o estado, mediante a justificativa registrada, e nunca provoca reenvio automático nem habilita duplicidade. Na interface React, `SendUnknown` aparece claramente como **envio de resultado incerto**, sem se confundir com falha definitiva nem com confirmação de entrega.
4. **Horizonte de espera** do `SendUnknown` até exigir ação humana: configurável por canal, padrão conservador (decisão da 5.1a).
5. O reenvio de `SendUnknown` só existe como `AcceptDuplicateRisk` (ADR 0014, 8ª operação, **Proposta e fora do MVP**), com permissão própria, justificativa, idempotência e auditoria. No MVP não há reenvio delegado: o atendente resolve o envio ambíguo pelo `ConfirmedSent` manual ou escreve nova resposta. O `RequestResend` do `Conversations` **continua recusando** `SendUnknown` (`HandlingService`).

### G. Anexos e conteúdo

1. **Já decidido na estrutura:** o anexo pertence a **uma sessão** (busca por `(tenant, sessão, id)`, nunca só por id), tem estado de varredura (`Clean` exigido para sair), origem (`Upload`, `SameSessionExternal` ou `SameSessionOutbound`) e é lido pelo Sender apenas quando externo. Anexo de nota interna vive em outro módulo e é inalcançável pelo Sender (ADR 0013).
2. **Decidido no MVP (DP-05, 03/11/2026): adiar o armazenamento e o processamento de anexos.** Preservam-se **metadados e placeholders** (tipo, mime, tamanho e identificador do provedor, no bloco `ChannelData` da mensagem): o atendente vê o **registro** da mídia, mas **não o arquivo**. Aplica-se a política MED-2 (`CONVERSATION-LIFECYCLE.md` §10): imagem, documento e vídeo são encaminhados a humano com o motivo `ContentUnavailable`, e áudio recebe resposta informativa determinística com texto aprovado pelo tenant. **Conteúdo de mídia não processado nunca entra no prompt da IA.** Onde guardar o conteúdo (sistema de arquivos local, banco, armazenamento de objetos), quem varre e com qual ferramenta, limites de tamanho e tipos, o corpo grande de e-mail (o limite de 4096 caracteres de `Message.Text` não o comporta) e as bibliotecas novas (sanitização de HTML, varredura, que exigem **aprovação**, CLAUDE.md) ficam para evolução posterior e para a 5.5.

### H. Extensibilidade futura (nada implementado)

- **Interação interempresarial** como **provedor de canal** `Network`: o outro tenant é um interlocutor externo, e a mensagem atravessa Inbox, Outbox, idempotência, cerca e histórico como em qualquer canal, **sem exceção de isolamento**. Um módulo próprio futuro (`Ecosystem`) guardaria só o vínculo (correlação, as duas empresas, tipo, estado, prazo, referências opacas); o identificador de correlação **não concede acesso a dado algum**; cada empresa escreve só no próprio tenant. Hoje não existe correlação entre tenants no código, só proteções que a impedem (`CrossTenantAccessException`, filtros globais).
- **Federação** de agentes (internos, de outros tenants, externos): fora da etapa 5; nada deve impedi-la.
- **Regra para a etapa 5:** nenhum contrato novo assume que número de atendimento, identificador ou sessão sejam globais.
- **Canais posteriores** (TikTok, depois Telegram, YouTube, Google Business Profile, LinkedIn; opcionais Discord e LINE): só se prepara a arquitetura (adaptador + capacidades + categoria).

### I. Migrações propostas do `Channels` (nenhuma criada nem aplicada)

| # | Migração | Risco |
|---|---|---|
| 1 | generalizar identificador e rótulo da instância; relaxar `ck_*_provider`; novos papéis de segredo; identificador opaco de webhook na conta | baixo (muda restrições) |
| 2 | `inbox_event`, `outbound_message`, `inbox_quarantine`, `lane` e índices por lane | médio |
| 8 | estado de caixa e índice de mensagens de e-mail (ADR 0015) | baixo |
| 9 | orçamento, reserva, razão de custo e supressão de SMS (ADR 0015) | baixo |

Todas aditivas, com `Down` e plano de rollback; aplicação só com autorização expressa (DP-07).

## Alternativas descartadas

- **Criar `channels.channel_account` conforme a letra da REC-2.** Duplica a rota e o cadastro, e a cópia pode divergir.
- **Resolver o tenant pelo payload antes de verificar a assinatura.** Interpreta conteúdo não autenticado (REC-1).
- **Aresta `ContactCenter → Channels` para as capacidades.** Funciona e não cria ciclo, mas amplia o grafo sem necessidade; a porta do host resolve.
- **Adaptador que chama `Conversations` direto no webhook.** Fere REC-4 e ARQ-19.
- **Reenvio automático de `SendUnknown` depois de um horizonte.** Ausência de confirmação não é prova de não envio (EFX-3).
- **Broker, escalonador justo avançado ou limitador distribuído agora.** O `Messaging` só cresce com necessidade comprovada por teste (§12.12).

## Consequências

- O gate REC-2 foi **conciliado e aprovado** e o PRV-2 **cumprido para o WhatsApp** (03/11/2026, A.1 e A.2): o Receiver e o Sender podem ser implementados no escopo do MVP, **depois da autorização de implementação** da fatia (projetos, migrações e dependências). Os demais canais seguem com o gate por adaptador. A 5.1b (só `Conversations` e `ContactCenter`) nunca dependeu desses gates.
- A conta ganha um identificador opaco de webhook: um segredo operacional a mais por conta, nunca exibido em log, resposta ou front-end.
- A conferência cruzada conta-instância reduz o risco de um webhook válido de uma conta injetar evento em outra.
- O Sender depende de o adaptador **declarar corretamente** a estratégia de efeito. Declarar (a) sem prova é defeito grave; por isso a prova (documentação **e** teste) é requisito do adaptador.
- E-mail e SMS acrescentam estados e tabelas ao mesmo Inbox e à mesma fila de envio, sem mecanismo paralelo.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Implementar com parâmetros do provedor não confirmados | PRV-1 (configuração, não constante); PRV-2 como gate; efeito (c) até a confirmação |
| Webhook de uma conta injetar evento em outra | identificador opaco por conta, assinatura antes do payload e conferência cruzada |
| Perda de mensagem por indisponibilidade do Receiver | alerta crítico de taxa de erro do webhook; Inbox durável; limites por tenant no acúmulo |
| Estratégia de efeito declarada sem prova | teste do adaptador que exige prova para (a) e (b); mutação "declarar (a) sem chave" |
| Reenvio duplicado por lease perdido | intenção antes da chamada, `operation_id` estável e `SendUnknown` sem retentativa |
| Pool de conexões esgotado pela consulta sob a trava | medição antes do worker; LIM-1; falha fechada sem perda |
| Segredo ou token em log, resposta ou front-end | testes de ausência (bloqueador 8); segredos só cifrados |

## Critérios de aceitação (5.1a)

Todos com PostgreSQL real, adaptador fake e payloads sintéticos, sem credencial real:

1. **Receiver:** 100 repetições do mesmo evento geram 1 linha; assinatura inválida grava zero linhas e responde 401; identificador desconhecido responde 404; número de outra conta ou de outro tenant é descartado e contado, com 200; banco indisponível ou falha no commit **não** responde 200; lote com várias mensagens; corpo acima do limite; **o Receiver não referencia outro módulo** (teste de arquitetura).
2. **Ordem e isolamento:** ordem estrita por lane; no máximo uma lease ativa por lane; o teto por tenant nunca é excedido; canários entre tenants nos workers; evento sem tenant falha fechado.
3. **Sender:** estratégia (a) sem duplicata; (b) com reconciliação; (c) sem reenvio automático; timeout depois do aceite vira `SendUnknown` e **não** é reenviado (mutação "reenviar `SendUnknown`" fica vermelha); janela fechada gera `Failed(WindowClosed)` e `expires_at` independe dela; erro de conta pausa a conta; status duplicado ou fora de ordem não regride.
4. **Evidência:** `Manual` nunca produz `Failed`, `Expired` nem `Dead`; tempo esgotado e busca sem resultado deixam `SendUnknown`.
5. **Gates:** REC-2 conciliada **e aprovada** (cumprido em 03/11/2026); PRV-2 confirmada para os itens do WhatsApp com data e fonte neste ADR (cumprido em 03/11/2026, com os itens 2 e 4 não documentados e o 11 parcial, por emenda formal). Soma-se o teste da assinatura com payload acentuado (A.2, item 3).
6. **Medição do ADR 0008** registrada antes do worker de ingestão.
7. **Geral:** suíte completa e mutações da etapa 4 verdes; nenhuma garantia aprovada enfraquecida; matriz e grafo de projetos inalterados (ARQ-06, `GrafoDeProjetosTests`).

## Pendências

- **Decididas em 03/11/2026:** DP-02, DP-03, DP-05 (adiar anexos), DP-06 (retenção inicial), DP-07 (planejamento das migrações; **nenhuma aplicada**, cada aplicação exige autorização específica), DP-08 e DP-11 (planejamento de `IaBimeta.Adapters.WhatsApp` e do projeto de testes; o adaptador fake fica na infraestrutura de testes; **a criação efetiva dos projetos depende da autorização de implementação**). Ver `docs/architecture/ETAPA-5.md` §8.0.
- **Ainda pendente:** DP-04 (capacidades do canal no ContactCenter, B.6).
- **Pode permanecer para a fatia:** reter ou descartar eventos de canal suspenso (5.1a); horizonte do `SendUnknown` (5.1a); biblioteca compartilhada dos adaptadores Meta (5.2).
- O `BOUNDARIES.md` (§12.2, §12.11, §12.12) foi atualizado em 03/11/2026 (v0.4.7). O ADR 0008 (pendência da REC-2) **não foi alterado nesta rodada**: a pendência dele está resolvida por A.1 e depende de ajuste autorizado separadamente.
