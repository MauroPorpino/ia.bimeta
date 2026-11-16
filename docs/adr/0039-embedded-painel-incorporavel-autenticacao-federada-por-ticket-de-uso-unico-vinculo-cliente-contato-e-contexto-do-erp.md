# ADR 0039 — ia.bimeta Embedded: painel incorporável, autenticação federada por ticket de uso único, vínculo cliente-contato e contexto do ERP

- **Status:** **Aceito em princípio** (planejamento aprovado pelo decisor em 16/11/2026, parte obrigatória da V1). O texto detalhado será ratificado ao fim da etapa E0; ajustes de cada etapa entram no registro de implementação abaixo.
- **Data:** 16/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md` (módulos, camadas, matriz), 0035 e 0036 (Capabilities e Agents), 0027 (modelos de mensagem), 0040 (início ativo e consentimento), `docs/architecture/EMBEDDED-SPIKES.md`
- **Preserva:** o app independente (login por senha), o `RequestContext` criado só pelo Host, o isolamento por tenant e organização, o Conversation Engine como autoridade do estado, o Capabilities como único caminho para sistemas externos, os módulos já entregues.
- **Altera (aditivamente):** `Identity` (sessão embutida, conexões de integração, identidades externas), `Conversations` (vínculo cliente-contato), `ContactCenter` (rotas por cliente), `Capabilities` (origem e parâmetro de contexto), o Host (rotas, guarda de rotas, cabeçalhos) e o frontend (entrada de painel).

## Contexto

O ia.bimeta Embedded permite que um ERP ou sistema web legado incorpore o painel do Contact Center, sem duplicar o backend nem obrigar o usuário a deixar o sistema dele. A distribuição é a mesma aplicação React, servida em um iframe da origem do ia.bimeta, comandada por um SDK JavaScript independente de framework. O BImeta ERP (ASP.NET WebForms) é a primeira integração de referência; nenhum código do núcleo depende dele. Existe também um ERP fictício independente para os testes automatizados.

Fatos do código (16/11/2026) que condicionam a decisão: a sessão é um token opaco Bearer (hash SHA-256 no banco, 8 h), sem cookie e sem CORS (a API é atendida na mesma origem em `/api`); o Host cria o `RequestContext` a partir da sessão; não há SSE nem WebSocket (a interface usa polling); o Caddy não define `frame-ancestors`, `X-Frame-Options` nem CSP; o `App.tsx` mistura login, áreas e navegação sem router.

## Decisão

### 1. Sem módulo novo; sem aresta nova na matriz
As responsabilidades ficam nos módulos existentes (matriz `module-dependencies.json` v0.3: nenhuma aresta nova, só contratos novos):

| Módulo | Responsabilidade nova |
|---|---|
| Identity | conexões de integração, chaves, tickets, identidades externas, sessão embutida, revogação; contrato `IEmbeddedContextQuery` (consumido pelo Capabilities, aresta já existente) |
| Conversations | vínculo cliente do ERP ↔ contato; consulta por cliente (contratos) |
| ContactCenter | rotas de conversas e histórico por cliente (`Presentation`), orquestração do início ativo (ADR 0040) |
| Capabilities | origem `EmbeddedPanel`, parâmetro de contexto, operações do painel |
| Host (Api) | rotas `/embed/v1/*`, autenticação HMAC, guarda de rotas da sessão embutida, cabeçalhos de segurança |

Alternativa rejeitada: módulo `Embedding` separado (alto fan-out: Identity, Conversations, ContactCenter e Capabilities; a emissão de sessão passaria a cruzar a fronteira do Identity por comando de contrato). O decisor condicionou a escolha a que os **testes de arquitetura continuem garantindo os limites** (seção 14).

### 2. Modelo de confiança
O **ERP afirma a identidade** de quem usa o painel; o **ia.bimeta decide as permissões**. Papéis, grupos e permissões enviados pelo ERP são ignorados. O escopo efetivo é a interseção entre o que o ERP mapeou (usuário, cliente em foco) e as concessões do Identity. O tenant sai sempre da **conexão de integração** autenticada, nunca de parâmetro de requisição.

### 3. Conexão de integração e credencial servidor-a-servidor
`IntegrationConnection` (por tenant e organização): nome, origens permitidas do ERP, modo (`FullAttendant` ou `CustomerOnly`), política de provisionamento (`MappedOnly` ou `JitAttendant`), flags (`AllowStartConversation`, desligada por padrão), escopo de organizações, situação. Até duas chaves ativas (`kid` + segredo de 256 bits **cifrado em repouso** como os segredos de canal; rotação sem parada). A permissão nova `EmbedManage` (só com concessão de escopo do tenant) administra conexões e chaves. O valor do segredo é mostrado uma vez, na criação.

Autenticação das chamadas do servidor do ERP: `X-IaB-Key-Id`, `X-IaB-Timestamp` (janela de ±2 min), `X-IaB-Nonce` (único na janela) e `X-IaB-Signature = HMAC-SHA256(segredo, timestamp ⏎ método ⏎ caminho ⏎ SHA-256(corpo))`, comparação em tempo constante. Simples em .NET Framework, PHP e Java. Limite de taxa por conexão. O Host cria um `RequestContext` próprio (origem `embed-integration`, sem permissões de usuário) só para essas rotas.

### 4. Ticket, verifier e troca por sessão
1. O iframe nasce, gera um **verifier** aleatório que fica só na memória dele e envia o **challenge** (S256 do verifier) ao host pela ponte `postMessage`.
2. O servidor do ERP chama `POST /embed/v1/launch-tickets` com `externalUserId`, `challenge`, `parentOrigin` (uma das origens permitidas da conexão), modo e, opcionalmente, o cliente em foco. Resposta: **ticket opaco** de 256 bits, gerado pelo ia.bimeta (só o hash é guardado), de **uso único** (consumo atômico) e **60 s**.
3. O iframe troca: `POST /embed/v1/exchange { ticket, verifier, parentOrigin }`. O servidor confere hash, validade, uso único, challenge×verifier e `parentOrigin` (o iframe o obtém do `event.origin` da mensagem de handshake, que o navegador garante) contra o que o ERP declarou. Qualquer divergência responde o mesmo erro, sem detalhe.
4. Resultado: **sessão embutida**, token opaco Bearer **só em memória** do iframe (nunca `sessionStorage`, nunca na página do ERP).

O verifier impede que um XSS na página do ERP, que enxerga o ticket, o troque. O ticket não vai em query string (log) nem cookie: chega por `postMessage` pela porta privada.

### 5. Sessão embutida
`Session` ganha `Kind` (`Interactive` ou `Embedded`), `IntegrationId`, `ExternalUserId` e teto absoluto. Duração **30 min**, renovável por novo ticket (evento `session-expiring`), teto absoluto de 8 h. Revogação: `POST /embed/v1/sessions/revoke` (logout do ERP), revogação administrativa e a revogação de sessões do usuário já existente. A origem do contexto é `embedded-session`; as rotas protegidas continuam respondendo 401 sem sessão válida.

### 6. Identidades externas e usuários federados
`ExternalIdentity (tenant, conexão, externalUserId → UserId)`. `MappedOnly`: o administrador mapeia; usuário não mapeado recebe recusa genérica. `JitAttendant`: cria o usuário federado na primeira entrada, com o papel de atendente nas unidades configuradas na conexão e nada além. Usuário federado tem `AuthMode = Federated` e **não faz login por senha**. Nenhum papel vem do ERP.

### 7. Foco no cliente assinado pelo servidor
O cliente em foco só entra na sessão por **ticket de contexto** emitido pelo servidor do ERP (`POST /embed/v1/context-tickets`, amarrado ao usuário externo e à sessão; resgate em `POST /embed/v1/context`). O JavaScript do navegador não forja o acesso a um cliente. Os *grants* de cliente ficam na sessão (lidos por `IEmbeddedContextQuery`). O nome, o telefone e demais dados enviados pelo ERP são **conteúdo não confiável**: dica de interface, nunca vão ao LLM nem decidem autorização por si.

### 8. Vínculo cliente-contato
`contact_external_links` no Conversations: `(tenant, conexão, customerRef, contactId, origem, autor, data)`, único por conjunto. O vínculo dá **descobribilidade**; a **visibilidade** continua determinada pelas concessões do usuário (organização, unidade, fila): fora do escopo, 404. Criação: (a) o ERP por `PUT /embed/v1/customer-links` com telefone normalizado (E.164), resolvido a contato **existente** do tenant; (b) o atendente, na conversa aberta (`ConversationManage`). Telefone compartilhado por vários clientes é sinalizado, não resolvido por suposição. Toda criação é auditada. Alias de contato por `wa_id` devolvido pela Meta (ADR 0040) evita duplicar contatos.

### 9. Modos e guarda de rotas
Por conexão: **`FullAttendant`** (fila completa do atendente mais contexto do cliente; padrão do BImeta) e **`CustomerOnly`** (só o cliente em foco). O Host aplica uma **lista fechada de rotas** (padrões HTTP) permitidas à sessão embutida: `/auth/me`, `/contact-center/*` de atendimento (itens, resposta, mídia, modelos, histórico, início ativo), `/embed/v1/*` de sessão e `/embed/v1/context/*` do Capabilities. Todo o resto (Fluxos, Conhecimento, Integrações, usuários, senha, administração) responde **403**, mesmo que a interface não o mostre. No modo `CustomerOnly` as rotas de itens ficam restritas aos grants. A lista **não pode ficar vazia nem conter rota administrativa** (teste de arquitetura).

### 10. Cabeçalhos e rede
- Sem cookies (o Bearer em memória evita CSRF e o bloqueio de cookies de terceiros) e sem CORS (o iframe é carregado da origem do ia.bimeta; a API é da mesma origem). A API não é exposta ao navegador do ERP.
- App independente: `frame-ancestors 'none'` (corrige a ausência atual) e CSP restritiva. `/embed/*`: `frame-ancestors` com a lista de origens da instalação (variável de ambiente) e CSP equivalente. A origem **por conexão** é validada no ticket e no handshake; cabeçalho por conexão fica como endurecimento posterior (o Caddy é estático).
- HTTPS obrigatório; ERP em HTTP não é suportado. CSP começa em modo `Report-Only` até os testes de navegador passarem.
- Polling continua (sem SSE/WebSocket na V1). Rascunhos seguem `localStorage` com o fallback já existente: no Safari o storage de terceiros é particionado por site e efêmero (ver spikes), por isso nada essencial depende dele.

### 11. Protocolo `postMessage` v1
Envelope `{ v, id, type, payload }`. Handshake: o iframe envia `iab:ready { challenge }` (público, sem segredo); o SDK confere `event.source` e `event.origin` do iframe, obtém o ticket do servidor do ERP e responde `iab:init { ticket }` com `targetOrigin` exato, transferindo um `MessagePort`. **Todo o resto trafega pela porta privada.** Iframe → host: `unread-count`, `resize`, `conversation-opened`, `session-expiring`, `session-ended`, `error`. Host → iframe: `open { customerRef }`, `setMode`, `setTheme`, `provide-ticket`, `close`. Mensagem fora do esquema, sem handshake ou de origem diferente é descartada.

### 12. Capabilities no painel
A operação ganha `ShowInContextPanel` (título e ordem) e a origem permitida `EmbeddedPanel`; **nada aparece sem decisão explícita do administrador**. O parâmetro tem `Source = Input | Context.CustomerRef`; o `customerRef` vem do *grant* da sessão, nunca do corpo. Classe do dado (`Public`/`CustomerData`), autorização do **usuário** (não da conta técnica), auditoria com o hash do cliente, resposta exibida e **não guardada**. V1: leitura. Escrita pelo painel não entra (a tela de operação do Capabilities continua sendo do administrador).

### 13. Frontend e SDK
- `AtendimentoShell` é extraído do `App.tsx` (fila, conversa e conexão) e usado pelo app independente e pela nova entrada `embed.tsx` (segunda página do Vite, mesmo código compartilhado). Os componentes `QueuePanel`, `ConversationPanel`, `MessageList` etc. não são duplicados. Layout estreito (320–480 px) e `HostBridge` entram. Fluxos, Conhecimento e Integrações não existem no painel.
- SDK `iabimeta-embed` (JavaScript sem dependências, UMD e ESM, `.d.ts`), servido em `/embed/sdk/<versão>/`: `IaBimeta.mount({ container, baseUrl, mode, ticketProvider })`, `open`, `setMode`, `on`, `destroy`. O que muda por plataforma é só a **rota do servidor do ERP que emite o ticket**; entregam-se exemplos (WebForms `.ashx`, PHP, Java, React) e um ERP fictício C# em outra origem para os testes.

### 14. Testes de arquitetura e segurança
Os testes existentes continuam obrigatórios. Novos: a lista de rotas da sessão embutida (não vazia, sem rota administrativa); toda tabela nova na classificação de retenção (`RetentionInventory`); nenhum módulo lê tabela de outro; o `Capabilities` só lê o contexto pelo contrato do Identity. A lista de rotas nasce na etapa E1 (a lista não existe antes), com o teste.

### 15. Auditoria e retenção
Eventos: emissão de ticket, troca, revogação, mapeamento de identidade, vínculo, ticket de contexto, execução de capacidade em contexto. Sem token, sem ticket, sem segredo, sem parâmetros. Tickets expiram em 60 s e são apagados em 24 h; nonces em 10 min; sessões embutidas seguem a retenção das sessões.

## Fora do escopo da V1
SSE/WebSocket; escrita pelo painel; SSO por OIDC/SAML de provedores externos; cabeçalho `frame-ancestors` por conexão; disparos automáticos pelo ERP (ADR 0040).

## Consequências
- **Residual aceito:** um XSS no ERP age como aquele usuário dentro do ERP. Mitigações: o token nunca chega à página, o verifier fica no iframe, a lista de rotas limita o dano, o servidor do ERP comprometido tem escopo de organizações, limite de taxa, rotação e auditoria.
- O app independente ganha `frame-ancestors 'none'`: **muda cabeçalhos da candidata**, por decisão do decisor feita dentro da V1 ampliada.
- A V1 só é concluída com o Embedded aprovado (documentos de homologação atualizados na E0).

## Testes
Isolamento entre tenants (chave, ticket, identidade, vínculo, grants), replay, expiração, challenge e origem errados, nonce repetido, chave revogada, usuário federado sem senha, lista de rotas, histórico por cliente no escopo, e testes de navegador no Edge com `playwright-core` contra o ERP fictício em outra origem (embutir de origem não autorizada, logout que revoga, ponte `postMessage`).

## Registro da implementação
(a preencher por etapa)
