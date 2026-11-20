# Prompt para a auditoria independente da E2 (Codex, somente leitura)

> Preparado em 19/11/2026 pela E2.6. **Não executar sem autorização expressa do proprietário.** Limite de duas rodadas: a 1.ª é a auditoria completa e objetiva da E2; a 2.ª só existe se houver P0/P1 corrigidos, e apenas valida essas correções e a regressão nos caminhos tocados.

## Prompt (copiar a partir daqui)

Você é o auditor independente da **E2 do Embedded** do ia.bimeta (monólito modular .NET 10, PostgreSQL, React/TypeScript). Trabalhe em **SOMENTE LEITURA**: não edite arquivos, não faça commit, não rode migrations em banco algum, não instale nada, não chame serviços externos (Meta, OpenAI, Anthropic, ERPs). Pode ler código, migrations e documentos, e rodar `dotnet build`/`dotnet test` **apenas** se o PostgreSQL de teste local estiver disponível; se não estiver, diga que não rodou.

**Onde está o código.** Worktree `C:\Projetos\ferramentas\worktrees\ia.bimeta-embedded`, branch `feature/v1-embedded`, base `2be8f77` (a E1-R já aprovada com ressalvas). **A E2 está no diretório de trabalho, sem commit**: use `git status` e `git diff` contra `2be8f77` (arquivos novos aparecem como não rastreados). Não use `main` nem `v1-rc1`.

**O que auditar (E2.0 a E2.5), lendo o CÓDIGO e as MIGRATIONS, não só os documentos:**
1. **Vínculo cliente-contato** (E2.2): `PUT/DELETE /embed/v1/customer-links` (HMAC) e `POST/DELETE /contact-center/items/{id}/customer-link[/{ref}]`. Código em `backend/modules/conversations/**/ContactLink*`, `ConversationsIntegrationEndpoints.cs`, `ContactCenter.Core/Application/CustomerLinkService.cs`.
2. **Histórico por cliente e `CustomerOnly` somente leitura** (E2.3): `GET /contact-center/customers/{ref}/history`, `QueueAccess.GrantedCustomerSession`, `QueueItemService`, `EmbeddedRoutePolicy`.
3. **Consentimento** (E2.4): `PUT /embed/v1/customers/{ref}/consents` (HMAC; caminho assinado canônico), `POST/GET /contact-center/items/{id}/consents`, `optout-reviews`; `ConsentService`, `ConsentQueryService`, `ConsentStore`, `ItemConsentService`; permissão `ConsentManage`.
4. **Palavras-chave e revogação** (E2.5): `OptOutDecider`, `InboundMessageService.ApplyOptOutAsync`, marcador `opt_out_command`, supressão em Agents e Workflows, sonda `consentimento`.
5. **Migrations** (`backend/modules/conversations/IaBimeta.Conversations.Infrastructure/Migrations/`): `ContactLinksAndConsents`, `OptOutReviews`, `MessageOptOutCommandMarker` (e o snapshot). Confira constraints, índices únicos parciais, gatilhos (somente acréscimo; resolução única), `Down` e coerência com o modelo.
6. **Autenticação HMAC por porta** (`Identity.Contracts/Integration`, `IntegrationRequestAuthenticatorPort`): `Denied` (401) x `Unavailable` (503) x `RateLimited` (429).

**Prioridades, nesta ordem:** (1) isolamento multi-tenant e entre organizações; (2) autorização (permissões, 403 x 404, `ConsentManage` só administrador e gestor, sessão embutida nunca a recebe, `CustomerOnly` nunca escreve); (3) segurança (HMAC, enumeração, dado pessoal em log ou auditoria, injeção, evidência exposta); (4) integridade (somente acréscimo, atomicidade com a auditoria no outbox, idempotência, concorrência, ordem de travas, ChangeTracker em falha); (5) funcionamento dos fluxos essenciais ponta a ponta.

**Decisões JÁ RATIFICADAS (não reabrir):** ADR 0041 (`docs/adr/0041-*.md`), em especial: D3 (PARAR inequívoca; SAIR e CANCELAR contextuais; linguagem natural gera `PossibleOptOut` sem revogar); resposta defasada sempre rejeitada; 409 `consent-superseded` para `Granted` não posterior à revogação; migrations `OptOutReviews` e `MessageOptOutCommandMarker`; assinatura HMAC do caminho canônico (hexadecimal em maiúsculas, ambiguidades recusadas antes da autenticação); rotas de consentimento **fora** da lista Embedded; sem prazo nem expurgo de retenção (pendência jurídica); meta de 24 h corridas para `PossibleOptOut` é alerta provisório, não "dia útil". Limitações já conhecidas e registradas (não reporte como novas): `docs/architecture/EMBEDDED-BACKLOG.md` §1.2 (BL-10 a BL-23), `EMBEDDED-E2-PLANO.md` §14 a §17.

**Regras de relato (importante):**
- Aponte **somente problemas concretos**, cada um com: **arquivo e linha**, **cenário reproduzível** (passos ou teste que o demonstre), **impacto concreto** e a **menor correção** possível.
- Classifique: **P0** falha crítica de segurança, integridade ou perda grave de dados; **P1** falha comprovada que compromete funcionalidade essencial, isolamento, autorização ou segurança; **P2** relevante, mas não impede o encerramento técnico; **P3** melhoria, limitação conhecida ou baixo impacto. Separe **bloqueadores (P0/P1)** de **melhorias (P2/P3)**.
- **Não sugira reestruturação arquitetural, refatoração ou funcionalidade nova** sem um defeito demonstrado. Riscos hipotéticos sem caminho alcançável vão, no máximo, como P3 marcado "hipotético".
- Não repita achados de rodadas anteriores da E1/E1-R nem os do backlog.

**Formato da resposta:** (a) veredito: **APROVADO**, **APROVADO COM RESSALVAS** ou **REPROVADO**; (b) lista de P0/P1 (vazia se não houver); (c) lista de P2/P3; (d) o que você leu e o que rodou, e o que **não** conseguiu verificar; (e) uma linha por critério de aceite da E2 (atendido/pendente). Seja conciso.
