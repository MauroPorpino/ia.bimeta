# ADR 0040 — Início ativo de conversas: modelos aprovados, consentimento rastreável, limites e recuperação sem duplicidade

- **Status:** **Aceito** (planejamento aprovado em 16/11/2026, parte obrigatória da V1; texto ratificado pelo decisor ao fim da E0, em 16/11/2026). Ajustes de cada etapa entram no registro de implementação abaixo.
- **Data:** 16/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0027 (modelos de mensagem, que deixou o início ativo fora do escopo), 0007 e `CONVERSATION-LIFECYCLE.md` (reenvio delegado, falha definitiva de envio), 0014, 0018 a 0020 (Sender, vazão, pausa, `SendUnknown`), 0039 (Embedded), `BOUNDARIES.md` §6 e §12, `docs/architecture/EMBEDDED-SPIKES.md`
- **Altera (aditivamente):** `Conversations` (sessão iniciada pela empresa, consentimento, alias de contato), `ContactCenter` (orquestração e rota), `Channels` (somente expõe a categoria do modelo e o `wa_id` devolvido pelo provedor). **Preserva:** o Sender, o outbox, a idempotência por `OperationId`, a vazão e a pausa por conta, o `SendUnknown` nunca reenviado, a janela de 24 h como regra do provedor, e nenhuma aresta nova na matriz.

## Contexto

Hoje a conversa só nasce de mensagem recebida; fora da janela de 24 horas só se envia modelo aprovado (ADR 0027), e o cliente que nunca escreveu é "janela fechada". Para o ERP abrir um atendimento a partir do cliente selecionado, mesmo sem atendimento aberto, é preciso criar a sessão, o item de fila e a primeira mensagem (um modelo aprovado), com consentimento, autorização, auditoria e tratamento de falhas, sem enviar nada real durante o desenvolvimento.

Documentação oficial da Meta (consultada em 16/11/2026, página "Send messages" da Cloud API): só se envia mensagem a quem **optou por receber**; fora da janela de 24 h só modelo; o campo `wa_id` da resposta **pode não coincidir** com o número enviado (`input`); com identificadores de usuário por empresa (BSUID) o `wa_id` pode vir omitido. Códigos de erro, limites de volume e preços **continuam a confirmar** na documentação oficial antes da implementação do envio (E3), como manda o `CLAUDE.md`.

## Decisão

### 1. O início é sempre humano e confirmado
Um atendente escolhe o número, a fila, o modelo e os parâmetros, vê a prévia e confirma. Disparo automático por evento do ERP e campanhas em lote ficam **fora da V1**. A arquitetura não os impede: o serviço recebe um `InitiatedBy` (`Human` hoje); outro valor é **recusado** até haver ADR próprio. Nenhuma rota em lote.

### 2. Mesmo serviço e mesmas regras nos dois portais
O início ativo existe **no painel Embedded e no portal independente**, pelo mesmo serviço do ContactCenter e a mesma autorização. O componente de interface é o mesmo (`IniciarConversa`), exibido conforme permissão e configuração.

### 3. Orquestração idempotente em três passos
`POST /contact-center/customers/{ref}/start-conversation { requestId, channelInstanceId, queueId, templateId, parameters }` (e variante por telefone do contato quando não há cliente do ERP). O `requestId` é a chave de idempotência. O ContactCenter orquestra, cada passo idempotente e retomável:

1. **Validação:** permissão `ConversationStart`, escopo de organização/unidade/fila, número autorizado para a unidade (`IChannelUnitAuthorization`), flag `AllowStartConversation` da integração (quando o início vem do Embedded), consentimento, limites, modelo aprovado do número, telefone E.164.
2. **Sessão:** `Conversations.StartOutboundSession` (chave `OperationId = requestId`): cria o contato se preciso e uma sessão **Human**, responsável o atendente, sem mensagem de entrada. Se já há sessão **aberta** do contato no número, **devolve a existente** (sem duplicar) e a interface oferece abri-la; com a janela aberta, oferece texto livre em vez de modelo.
3. **Item de fila:** criado e atribuído ao atendente (único por sessão).
4. **Mensagem:** o modelo vai pelo caminho do `reply-template` (ADR 0027): gravado no Conversations, outbox, Sender, Channels.

**Recuperação.** Repetir o mesmo `requestId` retoma no primeiro passo incompleto e converge para o mesmo resultado: uma sessão, um item, uma mensagem. Uma falha entre passos (queda do processo) deixa a sessão visível ao atendente com sinal **"envio pendente"** e a ação **Retomar** (mesmo `requestId`). Um resultado **ambíguo** do provedor (`SendUnknown`) é mostrado como ambíguo e **nunca reenviado automaticamente**; só ação autorizada do atendente (a regra do ADR 0007/0020) decide.

### 4. Consentimento rastreável (padrão `Required`)
`contact_consents` no Conversations. Chave: tenant, **organização** (a empresa jurídica que contata), telefone E.164 e **finalidade** (`Utility`, `Marketing`). Cada registro guarda: situação (`Granted`/`Revoked`), **origem** (`ErpAsserted`, `AgentRecorded`, `CustomerKeyword`), **evidência** (texto ou referência e data da coleta), data do registro, autor (usuário ou conexão de integração) e data da revogação. Histórico imutável: mudar a situação acrescenta linha, não sobrescreve.

- **Rastreabilidade obrigatória:** o registro sem evidência, finalidade, origem e data é recusado. **Não se aceita afirmação de consentimento sem rastro**, nem pelo ERP nem pelo atendente.
- O ERP registra e revoga por `PUT /embed/v1/customers/{ref}/consents` (autenticação HMAC, ADR 0039); o atendente, com evidência obrigatória, pela interface (`ConsentManage`).
- Sem consentimento válido para a categoria do modelo (`Utility` ou `Marketing`; `Marketing` exige consentimento de marketing), o início é **recusado antes de gravar qualquer coisa** (`ConsentRequired`); com revogação, `ContactOptedOut`.
- Política por empresa: `Required` (padrão aprovado). A política `AssertedByIntegration` (o ERP afirma com evidência) pode existir como valor configurável, sem alterar a obrigação de rastro.
- A interpretação legal (LGPD, termos da Meta) é do decisor e do jurídico; o produto só impõe o mecanismo.

### 5. Revogação por palavras-chave
Padrão: **PARAR, SAIR e CANCELAR**. Detecção na ingestão do Conversations (mesma transação da mensagem de entrada): normaliza o texto (minúsculas, sem acentos, sem pontuação e espaços nas pontas) e só revoga quando a **mensagem inteira** é uma palavra-chave. Não revoga por trecho ("não quero parar de receber"), nem por mensagem com mais palavras, nem por mídia, nem em resposta a pergunta de fluxo ou de IA. As variações (por exemplo, "PARE", "SAIR AGORA") são **lista configurável por empresa**, vazia por padrão além das três. Efeito: revoga o consentimento do telefone na organização do número que recebeu (todas as finalidades por padrão, configurável para só marketing), registra origem `CustomerKeyword` com a mensagem como evidência e **sinaliza o atendimento** (atenção), sem resposta automática. Testes cobrem os falsos positivos.

### 6. Autorização
Permissão nova `ConversationStart` (concedida por papel) e `ConsentManage`; flag por integração; mais escopo da fila e do número. Fora do escopo: 404; sem permissão: 403 (padrão atual). O telefone de destino vem do **contexto assinado** (ticket de contexto do ERP) ou do contato vinculado, nunca de campo livre digitado no navegador do ERP; no portal independente, do contato existente ou de número digitado pelo atendente com a mesma validação e o mesmo consentimento.

### 7. Limites contra abuso
Configuráveis, com valores a confirmar na documentação da Meta: teto por atendente e por hora; uma iniciação por cliente e modelo na janela de repetição; teto diário por conta (para respeitar o nível de mensagens da Meta). Excedido: recusa visível com o motivo. A vazão e a pausa por conta do Sender continuam valendo.

### 8. Identidade do contato
O telefone é normalizado para E.164 e o `wa_id` devolvido pelo provedor é o **identificador canônico**. Quando difere do número enviado (caso conhecido de números brasileiros com ou sem o nono dígito) e já existe contato, grava-se um **alias** (`contact_aliases`), sem duplicar o contato. O `ExternalRef` do contato é tratado como **opaco** (a Meta introduz BSUID): nenhum código assume que seja telefone.

### 9. Falhas, auditoria e custo
Falhas visíveis e recuperáveis: `ConsentRequired`, `ContactOptedOut`, `TemplateNotApproved`, `ChannelNotAuthorizedForUnit`, limite atingido, `131049`, recusa do provedor, `SendUnknown`. Auditoria por tentativa (decisão, motivo, hash do cliente, modelo, categoria, consentimento usado, autor), **sem** texto nem valores de parâmetro. A categoria do modelo fica registrada para custo futuro (cobrança por mensagem de modelo entregue; não há relatório de custo na V1).

### 10. Resposta do cliente
Entra na mesma sessão e abre a janela de 24 h. Um teste garante que o Agents e o Workflows **não assumem** a conversa por causa dela (a sessão já existe e está em atendimento humano).

### 11. Sem envio real
Todos os testes usam o servidor Graph de loopback. O primeiro envio real só ocorre na homologação, com número de teste e **autorização expressa do decisor**.

## Consequências
- O ADR 0027 deixa de listar o início ativo como fora do escopo (fica registrado aqui).
- Consentimento e alias nascem no Conversations (dono de contato e sessão); o ContactCenter só orquestra; o Channels não ganha aresta nova.
- Risco: a Meta pode mudar limites, códigos e identificadores; por isso são configuração e a documentação é reconferida antes da E3.
- Risco: orquestração sem transação distribuída; mitigado por passos idempotentes e recuperação por `requestId`.

## Testes
Início completo (loopback), sem consentimento, com revogação, conversa existente sem duplicar, mesmo `requestId` repetido e retomado após queda em cada passo, permissão e escopo, isolamento entre tenants, limites, `SendUnknown` sem reenvio, resposta do cliente na mesma sessão sem IA nem fluxo, revogação por palavra-chave (positivos e falsos positivos), alias de `wa_id`, auditoria sem conteúdo.

## Registro da implementação
(a preencher por etapa)
