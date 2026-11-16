# ADR 0039 — ia.bimeta Embedded: painel incorporável, autenticação federada por ticket de uso único, vínculo cliente-contato e contexto do ERP

- **Status:** **Aceito** (planejamento aprovado em 16/11/2026, parte obrigatória da V1; texto ratificado pelo decisor ao fim da E0, em 16/11/2026). Ajustes de cada etapa entram no registro de implementação abaixo.
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
### E1 — Identity federado (16/11/2026)

Implementado como o texto acima, com estes ajustes e esclarecimentos (nenhum muda uma decisão aprovada):

- **Nonce assinado:** o `nonce` entra na string assinada (`v1 ⏎ timestamp ⏎ nonce ⏎ MÉTODO ⏎ caminho ⏎ sha256hex(corpo)`), além do timestamp, método, caminho e corpo; só é registrado DEPOIS de a assinatura conferir. O caminho assinado é o que o Api enxerga, **sem** o prefixo `/api` do proxy.
- **Sem teto absoluto de 8 h:** a sessão embutida dura 30 min e **nunca é estendida** (renovar é trocar outro ticket); logo não há coluna de teto absoluto. A 8 h do texto é o limite máximo aceito na configuração (`Identity:EmbeddedSessionLifetime`).
- **Sem `RequestContext` nas rotas assinadas:** `launch-tickets`, `context-tickets` e `sessions/revoke` não criam contexto algum (nem de sistema, que passaria por toda permissão): os serviços recebem o tenant explícito da conexão autenticada.
- **Ticket queimado em qualquer tentativa:** o ticket é consumido de forma atômica ANTES das demais conferências; verifier, origem do pai, conexão ou vínculo errados também o invalidam. O ticket de contexto fica preso ao usuário externo e à conexão da sessão que o resgata (sem coluna de sessão).
- **Papel novo `Attendant`:** `Role.Attendant = 4` (OrganizationRead, StructureRead, ConversationRead, ConversationManage, ChannelRead, QueueRead), concedido pelo `JitAttendant`. `Permission.EmbedManage = 25` só na concessão de escopo do tenant do administrador.
- **Teto de permissões e elegibilidade:** a sessão embutida só carrega as permissões de `EmbeddedSessionPolicy.AllowedPermissions`, só da organização da conexão (nunca escopo de tenant). Conta administrativa (papel `OrganizationAdmin` em qualquer organização ou qualquer concessão de escopo de tenant) ou sem acesso à organização da conexão não entra por federação nem mapeada, e perde a sessão embutida na hora se passar a sê-lo (a sessão é conferida contra o estado atual a CADA requisição, inclusive conexão e vínculo).
- **Usuário federado:** e-mail sintético `fed.<guid>@federated.invalid`, `AuthMode = Federated`, hash que nenhuma senha confere; o login por senha o recusa e uma sessão interativa dele não vale.
- **Lista fechada de rotas:** `EmbeddedRoutePolicy` (Host), 23 rotas (atendimento, `/embed/v1/session` e `/embed/v1/context`), default negar, inclusive rota inexistente. Protegida por `EmbeddedRoutePolicyTests` (lista aprovada literal, nenhuma rota administrativa, toda rota da lista existe e **toda rota da API fora dela responde 403** à sessão embutida).
- **Onde ficou:** a administração (`/embedded/connections…`) e o protocolo (`/embed/v1…`) estão em `Identity.Presentation`; o Host só registra os limites de taxa, as chaves mestras (`Identity:Secrets`), a origem de contexto `embedded-session` e o guarda de rotas. Sem aresta nova na matriz.
- **Migration** `EmbeddedFederation` (aditiva): `identity.integration_connections`, `integration_keys`, `integration_nonces`, `launch_tickets`, `external_identities`, `session_customer_grants`; colunas `users.auth_mode`, `sessions.kind`, `sessions.integration_id`, `sessions.external_user_id`, `sessions.parent_origin` (com padrão para as linhas existentes). Reversão: `Down` remove as colunas e as tabelas.
- **Retenção:** categoria `EmbeddedTickets` (1 dia) para tickets e nonces; sessões embutidas seguem a retenção das sessões; clientes em foco morrem com a sessão. Todas as tabelas novas estão classificadas no `RetentionInventory`.
- **Pendente (E2 em diante):** a consulta por cliente, o vínculo cliente-contato e o histórico (E2); o painel e o `postMessage` (E4); `ConversationStart` e a flag `AllowStartConversation` são só colunas/permissão futura (E3); a verificação em navegador real (E5); a revisão independente do Codex.

### E1-R — Segurança, isolamento e consistência (16/11/2026)

A auditoria independente (Codex) **reprovou a E1**. A etapa corretiva E1-R tratou cada achado; a E1 só será considerada aprovada depois de nova auditoria sem bloqueadores. Substitui, onde divergir, o registro da E1 acima:

- **Organização da conexão como limite duro (achado: participação em fila abria outra organização).** `ActorPrincipal.Embedded` (`EmbeddedScope`: organização da conexão e `CustomerOnly`) vai do Identity ao `RequestContext` pelo host. Toda decisão de acesso que NÃO passa por concessão (participação em fila, responsável pela conversa, recibo de operação, listas de itens e de filas) consulta `AccessPolicy.IsOutsideEmbeddedScope`. Equipe compartilhada entre organizações continua valendo no painel interativo; a sessão embutida só alcança a organização da conexão.
- **`CustomerOnly` bloqueado por padrão.** Sem permissão alguma, escopo que nega toda via fora das concessões e, na lista de rotas, subconjunto exato (`/auth/me`, `/auth/logout`, `/embed/v1/session`, `/embed/v1/context`). O modo é lido a cada requisição. A autorização por cliente nasce na E2; até lá o modo é seguro porque fechado.
- **Revogação sem corrida.** Marca `sessions_revoked_at` por vínculo (logout do ERP) e por conexão (emergência): sessão criada ATÉ a marca e ticket emitido ATÉ a marca não valem, conferidos na troca e a cada requisição; a revogação também consome os tickets pendentes. Chave revogada derruba as sessões abertas e os tickets pendentes dela (`launch_tickets.key_id`, `sessions.issuer_key_id`); a chave que só expirou na rotação não derruba. A sessão deixa de valer se a origem do pai sai da lista da conexão.
- **Tokens embutidos nunca se passam por interativos.** Formato próprio `ibe_` e hash com separação de domínio (`SHA-256("iab-embedded-session-v1\n" + token)`); o `ibs_` interativo mantém o formato e o hash de sempre. Uma versão antiga da API (só `ibs_`, SHA-256 simples) não acha nem aceita uma sessão embutida; a nova exige que o formato do token e o tipo da sessão coincidam. Com rollback para a candidata, as sessões embutidas ficam inertes e o usuário federado continua sem senha utilizável; a migração é aditiva e reversível.
- **Ticket de contexto preso à sessão exata.** O ERP informa `sessionId` ao emitir; o ticket guarda tenant, conexão, usuário externo e sessão (`launch_tickets.session_id`), e só essa sessão o resgata. A emissão recusa sessão inexistente, de outro usuário, de outra conexão ou revogada. A resposta da troca passou a trazer `sessionId`.
- **Tetos sob concorrência.** O teto de sessões por usuário externo serializa pelo vínculo (`FOR UPDATE`); o teto de clientes em foco serializa pela sessão.
- **Limites de taxa sem cota que o chamador escolhe.** Antes da autenticação, só por origem de rede (nenhum cabeçalho define a partição). Depois, por chave CONHECIDA (só as falhas gastam: `Identity:IntegrationPermitsPerKey`, padrão 30/min) e por conexão autenticada (`Identity:IntegrationPermitsPerConnection`, padrão 600/min), com 429 e `Retry-After`. Chave inexistente é recusada sem cota própria.
- **Corpo.** `/embed/v1/exchange` e `/embed/v1/context` leem o corpo com teto de 8 KB ANTES de qualquer JSON, inclusive em pedaços e sem `Content-Length` (teto também na feature do servidor). As rotas assinadas recusam consulta (dado não assinado).
- **Timestamp.** Aritmética em segundos inteiros: valor absurdo é só uma assinatura fora da janela (401 controlado), nunca exceção de data.
- **Canonicalização e rotas públicas.** O caminho assinado é exatamente o que o Api recebe (o prefixo `/api` NÃO é mais ignorado). A dispensa de contexto vale só para o caminho exato (e o prefixo do webhook), nunca para sufixos.
- **Caddy.** O registro de acesso apaga todos os cabeçalhos da requisição (`request>headers delete`). A prova do comportamento implantado é o canário em `aceitacao.sh` na VM; no PC só o texto da configuração é conferido.
- **Redefinição de senha (preexistente).** `RecordLoginFailureAsync` fazia `INSERT … DO NOTHING` e `SELECT … FOR UPDATE`; a limpeza do vencedor apagava a linha entre os dois e a perdedora falhava com `Sequence contains no elements` (500). Agora `DO UPDATE` garante e trava a linha na mesma instrução. O comportamento da autenticação tradicional não muda.
- **Migration** `EmbeddedHardening` (aditiva): `sessions.issuer_key_id`, `launch_tickets.key_id`, `launch_tickets.session_id`, `integration_connections.sessions_revoked_at`, `external_identities.sessions_revoked_at`. `Down` remove as colunas.
- **Commits e gate da E1-R:** `7a07d91`, `c33f1b9`, `011dac1` e o commit de documentação. Terceiro gate: integração 1919 aprovados/14 ignorados/0 falhas, demais projetos sem falha, frontend 458 e build. **Ressalva:** uma falha intermitente (500 em 2 de 30 resgates de contexto simultâneos) apareceu uma vez no primeiro gate e **não teve a causa comprovada**; deve ser investigada se reaparecer. A E1 continua **não aprovada** até a reauditoria independente sem bloqueadores.
