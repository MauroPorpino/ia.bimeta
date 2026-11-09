# ADR 0027 — Modelos de mensagem (templates) do WhatsApp para iniciar ou retomar conversa fora da janela de 24 horas

- **Status:** **Proposto para ratificação.** Implementado em 09/11/2026 sob a autorização do decisor ("suporte mínimo a templates oficiais do WhatsApp Cloud API"), depois da ratificação do ADR 0026. Nenhuma chamada real à Meta ou à OpenAI; nenhum banco existente foi apagado (a migração só **cria** uma tabela; backup do banco de teste feito antes); `main` intocada; sem push.
- **Data:** 09/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md` (matriz de dependências), 0007 (reenvio delegado), 0008 (ligação mediada pelo host), 0011 (item A.2.6 e DP-04: porta do host para as capacidades do canal; A.2.8 a A.2.10: erros e janela de 24 horas), 0014 (resposta humana), 0018 a 0020 (Sender, vazão, pausa e `SendUnknown`), `docs/operations/MODELOS-DE-MENSAGEM.md`
- **Preserva:** nenhuma aresta nova na matriz de dependências (nem edição de `BOUNDARIES.md`, `ARCHITECTURE.md` ou `module-dependencies.json`); tenant e permissões só pelo `RequestContext`; o outbox, a intenção gravada antes da chamada, a idempotência por `OperationId`, a vazão e a pausa por conta, o `SendUnknown` nunca reenviado e o aviso ao atendente; o LLM não envia nada (o modelo é escolhido e preenchido por uma pessoa); nenhum segredo em log, arquivo ou resposta.

## Contexto

O WhatsApp só aceita texto livre até 24 horas depois da última mensagem do cliente; depois disso, só **modelo (template) aprovado pela Meta**. Até aqui o atendente fora da janela descobria pelo erro `131047` depois de tentar. Esta decisão dá o **suporte mínimo**: associar à conta modelos que **já existem e estão aprovados na Meta**, deixar o atendente escolher um, preencher os parâmetros e enviar, mostrar com clareza que a janela fechou e impedir o texto livre fora dela.

**Fora do escopo (decidido):** campanhas, disparo em massa, editor ou aprovação de modelos, agendamento comercial, automação de marketing; sincronização automática com a Graph API; cabeçalho de imagem, vídeo ou documento, botão de copiar código, fluxos, catálogo e a categoria de autenticação (o cadastro **recusa com o motivo**); **iniciar uma conversa que ainda não existe** (sem sessão nem mensagem do cliente: é envio individual a partir de uma conversa já aberta).

## Decisão

### 1. Cadastro e associação à conta certa (Channels)

- Tabela `channels.channel_templates` (migração `ChannelTemplates`, só cria a tabela): **um modelo por conta, nome e idioma**; chave estrangeira composta `(tenant_id, account_id)` para a conta (o banco recusa modelo ligado à conta de outro tenant); `CHECK` de categoria (1 e 2: a autenticação não entra) e de situação; filtro global por tenant e verificação no `SaveChanges`, como nas demais tabelas.
- Registra **nome, idioma, categoria, situação, identificador na Meta, textos (cabeçalho, corpo, rodapé) e a estrutura de parâmetros** (jsonb). A situação é a da Meta (`Approved`, `Pending`, `Rejected`, `Paused`, `Disabled`); **só `Approved` é oferecido e enviado**.
- A entrada é o JSON **como a Graph API o devolve** (um modelo, uma lista ou a página `{"data":[...]}`). O `TemplateDefinitionParser` extrai os parâmetros posicionais (`{{1}}`) ou nomeados (`{{nome}}`) do cabeçalho de texto, do corpo e do botão de link, e **recusa com o motivo** o que o MVP não sabe enviar. Um modelo recusado não impede os demais; se nenhum for aceito, a operação falha com o motivo. Repetir o JSON é idempotente (`Unchanged`) e atualiza o que a Meta mudou (`Updated`).
- Administração: `GET` e `POST /channels/accounts/{id}/templates` e `PUT .../templates/{templateId}/status`. Ler exige `ChannelRead`; associar e mudar a situação exigem `ChannelManage` sobre a organização da conta. Conta de outra organização ou tenant responde 404 (TEN-2); negação é auditada (`channels.template.register`, `channels.template.status`, sem o texto do modelo). O comando `provision` aceita `whatsApp.templates` no arquivo da empresa.

### 2. Ligação do ContactCenter por uma porta do host (DP-04 do ADR 0011)

O ContactCenter define a porta `IChannelReplyPolicy` (janela de resposta, modelos do número e preparo de um envio). O **host do Api** a implementa com a consulta `IChannelTemplates` do `Channels.Contracts` (`ChannelsBackedReplyPolicy`), como no ADR 0008: **nenhuma aresta nova na matriz**. A consulta do Channels é de código confiável (tenant do contexto, sem permissão de usuário); **quem autoriza o atendente é o ContactCenter** (só o responsável responde, como no texto livre). Sem a porta (Worker, testes), o padrão não conhece janela nem modelo e o provedor continua sendo a defesa final.

### 3. A janela de 24 horas

- A janela conta da **última mensagem do cliente** pelo relógio do provedor (`max(provider_timestamp ?? occurred_at)` das mensagens de entrada da sessão), exposta como `SessionView.LastInboundAt` **só na leitura de uma sessão** (não na listagem e **fora do JSON** da API do Conversations). **Nenhuma migração do Conversations.** A duração vem do adaptador (`IChannelProviderAdapter.ReplyWindow`: 24 horas no WhatsApp; nulo = o provedor não tem janela).
- `GET /contact-center/items/{id}/reply-options` devolve se o texto livre é aceito, quando a janela fecha e os modelos aprovados do número. **Texto livre fora da janela é recusado antes de gravar** (422, `code: ReplyWindowClosed`). O `131047` do provedor continua como defesa (`window-closed`, falha definitiva). Cliente que nunca escreveu (sem entrada) é janela fechada.
- A janela de resposta livre é do **provedor** e **não** se confunde com `expires_at` (validade operacional, ADR 0006).

### 4. Envio pelo ContactCenter

`POST /contact-center/items/{id}/reply-template` (`requestId`, `templateId`, `parameters` por chave: `body.1`, `body.nome`, `header.1`, `button.0`). Mesmas regras da resposta de texto: só o responsável, base lida no Conversations na hora, `requestId` como chave de efeito. **Os parâmetros são validados antes de gravar** (`TemplateRenderer`): faltando, a mais, vazio, acima do tamanho do componente (cabeçalho 60, corpo 1024, botão 512), com controle (quebra de linha e tabulação) ou mais de 3 espaços seguidos, e texto final ou bloco de canal grandes demais. Todos os erros voltam juntos (422, `code: TemplateParametersInvalid`, lista `errors` por chave) e **a mensagem de erro nunca repete o valor digitado**. Modelo inexistente, de outra conta ou não aprovado: 422 `ReplyTemplateUnavailable`.

A mensagem é gravada no Conversations como resposta humana comum (`MessageKind.Text`), com **texto = o que o cliente lerá** (cabeçalho, corpo e rodapé com os valores) e um **bloco de canal** (`ChannelData` versão 1: `tpl.id`, `tpl.name`, `tpl.lang` e um `tpl.p.<chave>` por parâmetro). `ReplyContent.ChannelData`, antes recusado, passa a ser aceito com os limites estruturais da mensagem do cliente, **entra na comparação de idempotência** (mesmo `OperationId` com outro bloco é outro efeito: `OperationIdReused`) e o **reenvio** (`CreateResend`) o copia. O Conversations não interpreta o bloco, **com uma exceção registrada**: `tpl.name` vira `MessageView.TemplateName` (`ChannelDataKeys.TemplateName`), o rótulo do histórico.

### 5. Sender e adaptador

O **mesmo** `OutboundMessageRequestedHandler` trata o modelo: se o bloco traz `tpl.id`, o Sender **relê o modelo na hora** e confere que existe, que é da conta do número, que continua `Approved` e que a estrutura de parâmetros é a que foi validada (`template-not-found`, `template-account-mismatch`, `template-not-approved`, `template-changed`: falha visível, **sem gastar ficha, tentativa nem chamada**). Depois segue o caminho de sempre: intenção gravada antes da chamada, vazão e pausa por conta, `SendTemplateAsync`, `Accepted` → enviado, recusa transitória → `Defer`, erro de conta → conta pausada, resultado ambíguo → `SendUnknown` **nunca reenviado**, notificação ao Conversations e sinal ao atendente. As recusas `132015` (pausado) e `132016`/`132001` (desativado ou não aprovado) tiram o modelo da oferta (melhor esforço, depois de o desfecho da mensagem estar gravado) e **não pausam a conta**. O leitor de resposta passou a tratar os códigos `132000`, `132001`, `132005`, `132007`, `132012`, `132015` e `132016` como recusa definitiva qualquer que seja o estado HTTP (a página oficial não o fixa).

O adaptador do WhatsApp monta `type: "template"` (`name`, `language.code`, componentes de cabeçalho, corpo e botão de link, `parameter_name` nos nomeados) pelo **mesmo ponto único** de `POST` do texto livre (a regra de arquitetura do adaptador, que limita as chamadas HTTP, continua valendo). Recusa localmente, sem chamar a Meta, nome ou idioma fora do formato, parâmetro vazio, com controle ou acima de 1024.

### 6. Interface (React)

Na conversa assumida: faixa **"Janela de 24 horas fechada"** com a idade da última mensagem do cliente; o campo de texto livre **desabilitado** com o motivo; seletor de **modelo aprovado**; **um campo por parâmetro** (rótulo e exemplo da aprovação), **prévia** do que o cliente vai receber, erro ao lado do campo (local e do servidor, sem a faixa vermelha), envio com `requestId` reaproveitado se a rede cair e a mensagem com a etiqueta **"Modelo: nome"** e o estado de entrega de sempre. Com a janela aberta, o texto livre vale e um botão abre o seletor ("Resposta livre até …"). A janela fecha com o relógio da tela sem esperar a releitura; se a consulta das opções falhar, a tela segue como antes (o servidor decide). Quando o servidor recusa por janela fechada ou modelo indisponível, a tela explica, relê as opções e não perde o texto digitado.

### 7. Simulação e demonstração

`ServidorGraphLoopback` captura o payload de modelo (`Tipo`, `NomeDoModelo`, `Componentes`); `Start-WhatsAppSimulado.ps1` imprime o modelo e os parâmetros (e recusa modelo sem nome ou idioma, como a Meta); `Send-MensagemWhatsApp.ps1 -HorasAtras 26` fecha a janela de um cliente; `Add-ModelosDeDemonstracao.ps1` associa os modelos de exemplo (`modelos-demonstracao.json`) à conta do tenant de demonstração.

## O que foi conferido na documentação oficial (09/11/2026) e o que não foi

Conferido nas páginas públicas da Meta (leitura apenas; nenhuma chamada à API): o corpo `type: "template"` com `template.name`, `template.language.code`, componentes `body` com parâmetros `{type: "text", text}`, parâmetros **nomeados** com `parameter_name`, o botão de link (`type: "button"`, `sub_type: "url"`, `index` como **texto** `"0"`, um parâmetro de texto), o limite de 60/1024 caracteres do cabeçalho e do corpo **na criação**, o modelo só enviável com situação aprovada, e os códigos `131047`, `132000`, `132001`, `132005`, `132007`, `132012`, `132015`, `132016`. **Não afirmado nas páginas lidas** (implementado de forma conservadora e a confirmar na homologação): a forma do parâmetro do **cabeçalho de texto** no envio; as restrições de quebra de linha, tabulação e espaços consecutivos e o tamanho máximo de um valor de parâmetro; o número máximo de parâmetros; o estado HTTP de cada código; se o modelo pode ser enviado com a janela **aberta** (a página diz que é o único tipo aceito fora dela; dentro dela, o código o permite).

## O que fica fora (pendências para a homologação com a Meta real)

1. Um modelo **aprovado de verdade** na conta de teste e o envio real, dentro e fora da janela; conferir cabeçalho de texto, parâmetros nomeados e botão de link.
2. Confirmar na Meta as restrições de valor de parâmetro (quebra de linha, tabulação, espaços, tamanho) e o estado HTTP e o código quando faltar parâmetro ou o modelo estiver pausado.
3. Conferir como a Meta mostra o status (`sent`, `delivered`, `read`) e o `biz_opaque_callback_data` de uma mensagem de modelo (o fluxo de status do texto livre vale, mas **não foi provado com a Meta real**).
4. Sincronizar os modelos da conta pela Graph API (`GET /{waba-id}/message_templates`) em vez de colar o JSON, e acompanhar mudanças de situação (por webhook de atualização de modelo): hoje a administração cola o JSON e o Sender aprende o que foi pausado ou desativado pelo erro.
5. Cabeçalho de mídia, copiar código, categoria de autenticação, fluxos; iniciar conversa nova (sem sessão); idioma preferido do cliente escolhendo entre idiomas do mesmo modelo; tela de administração dos modelos (hoje API e arquivo da empresa).
6. Custo por categoria (marketing, utilidade) e limites de envio de marketing por usuário (`131049`): hoje é falha definitiva visível ao atendente.
7. Rascunho dos campos do modelo por conversa (hoje só em memória da tela; trocar de conversa limpa).

## Consequências

- O atendente fala com o cliente fora da janela sem tentar e falhar. A decisão é humana: nenhuma IA escolhe ou preenche modelo.
- Cada modelo pertence a **uma conta**; trocar de conta ou recadastrar com outra estrutura de parâmetros faz a mensagem **ainda não enviada** falhar de forma visível (`template-changed`), nunca sair pela metade.
- **Risco aceito:** o Conversations conhece uma chave do bloco de canal (`tpl.name`) para o rótulo do histórico. Alternativa descartada: expor o bloco todo na visão (alarga a superfície).
- **Risco aceito:** o texto da mensagem no histórico é a **prévia** calculada pelo Channels; se a Meta renderizar diferente (por exemplo, normalizar espaços), o histórico mostra o que o atendente viu na prévia, não o byte a byte da Meta.
- Os valores dos parâmetros ficam no bloco de canal da mensagem (dado do cliente, como o texto): mesma retenção e mesmo cuidado de log da mensagem; nenhum valor é registrado em log, auditoria ou mensagem de erro.

## Testes

Unidade: `TemplateDefinitionParserTests` e `TemplateRendererTests` (Channels), `TemplateSendTests` (adaptador: forma exata do corpo, recusas locais, códigos), contratos e domínio do Conversations. Integração com PostgreSQL real: `ChannelTemplatesTests` (cadastro, idempotência, permissão, isolamento entre tenants, contas e organizações, garantias do banco), `RespostaHumanaPorModeloTests` (bloco, idempotência, limites, janela), `ProvisionamentoDeEmpresaTests` (modelos por empresa). Ponta a ponta com Api e Worker reais e a Meta simulada por HTTP (`ModelosDeMensagemTests`): fora e dentro da janela, texto livre bloqueado, parâmetros inválidos, modelo pausado, de outra conta e pendente, recusa da Meta (`132015`) que tira o modelo da oferta, falha transitória que sai uma vez, resultado ambíguo (`SendUnknown`) sem reenvio, conta pausada sem chamar a Meta, modelo desativado entre o pedido e o envio, e Worker derrubado no meio da chamada. Interface: 19 testes novos (Vitest).
