# Pacote para a auditoria independente da E1 (Identity federado do Embedded)

Para o Codex (ou outro revisor independente). A E1 é código de segurança: autenticação federada, isolamento entre tenants e uma lista fechada de rotas. **Revise o commit da E1 na branch `feature/v1-embedded`** (o commit que contém este arquivo), contra a base `6e515ef` (fim da E0) e a candidata histórica `v1-rc1` (`32cf06d`). Decisões: `docs/adr/0039-…` (texto e "Registro da implementação — E1") e `docs/architecture/BOUNDARIES.md` §13. Nada foi enviado à Meta, à OpenAI nem a nenhum ERP; sem push.

## O que a E1 entrega

Identity (módulo existente, sem aresta nova na matriz): conexões de integração, chaves de assinatura (HMAC-SHA256, segredo cifrado AES-GCM com vínculo tenant/conexão/chave), tickets opacos de uso único (hash SHA-256) com verifier (S256) e origem do pai, sessão embutida curta, identidades externas (mapeadas ou JIT), usuários federados sem senha, clientes em foco por ticket de contexto, revogação, retenção, auditoria, limites de taxa, e a **lista fechada de rotas** no Host.

Arquivos principais:

| Área | Arquivos |
|---|---|
| Domínio | `Identity.Core/Domain/{IntegrationConnection,IntegrationKey,LaunchTicket,ExternalIdentity,SessionCustomerGrant,Session,User,EmbeddedEnums}.cs` |
| Aplicação | `Identity.Core/Application/{IntegrationRequestAuthenticator,EmbeddedTicketService,EmbeddedExchangeService,IntegrationAdminService,EmbeddedViews(EmbeddedSessionPolicy),EmbeddedTokens,EmbeddedPorts,SessionAuthenticator,AuthenticationService}.cs` |
| Persistência | `Identity.Infrastructure/{EmbeddedStore,AesGcmIntegrationSecretProtector,IdentityRetentionTasks}.cs`, `Configurations/EmbeddedConfigurations.cs`, migration `*_EmbeddedFederation` |
| HTTP | `Identity.Presentation/{EmbeddedFederationEndpoints,EmbeddedAdminEndpoints,IdentityEndpoints}.cs` |
| Host | `Api/Embedded/EmbeddedRoutePolicy.cs` (lista, guarda), `Api/RequestContexts/{SessionTenantContextSource,RequestContextMiddleware}.cs`, `Api/ApiHost.cs` |
| Testes | `tests/IaBimeta.IntegrationTests/Embedded/*` (107, API real + PostgreSQL), `tests/identity/IaBimeta.Identity.Tests/Embedded*.cs` (unidade) |

## Invariantes a tentar quebrar

1. **O ERP só afirma identidade.** Papel, grupo, permissão, tenant e organização nunca vêm do ERP. O tenant sai da conexão da chave; o `parentOrigin` é conferido contra a lista da conexão no ticket e no `exchange`.
2. **Nenhuma vinculação por e-mail.** O usuário federado novo tem e-mail sintético `fed.<guid>@federated.invalid`; o mapeamento a conta existente só pelo administrador (`EmbedManage`, escopo de tenant) e nunca para conta administrativa (papel `OrganizationAdmin` em qualquer organização ou concessão de escopo de tenant), federada ou sem acesso à organização da conexão.
3. **Ticket de uso único.** Consumo atômico (`UPDATE … WHERE used_at IS NULL AND expires_at > now`), ANTES das demais conferências; qualquer tentativa errada o queima; verifier errado, origem errada, conexão/vínculo/tenant inativos: a mesma exceção e a mesma resposta (401, `embedded-authentication-failed`).
4. **Assinatura.** `v1 ⏎ timestamp ⏎ nonce ⏎ MÉTODO ⏎ caminho(sem /api) ⏎ sha256hex(corpo)`; janela de ±2 min; nonce único por chave (índice único), registrado só depois de a assinatura conferir; chave revogada/vencida, conexão desativada e tenant suspenso recusam; comparação em tempo constante.
5. **Teto da sessão embutida.** Só `EmbeddedSessionPolicy.AllowedPermissions`, só da organização da conexão; conferida contra o estado atual (conexão, vínculo, elegibilidade, tenant) a cada requisição.
6. **Lista fechada de rotas.** Default negar, inclusive rota inexistente. Verifique a ordem do pipeline (`UseRateLimiter` → `RequestContextMiddleware` → `EmbeddedRouteGuard`), a normalização de caminho (maiúsculas, barra final, `..`, `//`) e se existe rota fora do `RotasSemContexto` que o guarda não alcança.
7. **Isolamento entre tenants.** Chave e ticket de A nunca alcançam B (`launch_tickets.token_hash` e `integration_keys.id` são únicos globais; o tenant é o da linha achada); contexto de A não resgata ticket de B.
8. **Segredos.** O segredo da chave só aparece na criação (`no-store`); no banco só cifrado; nenhuma exceção, log ou auditoria o repete (`identity.embed.*` sem token, ticket, verifier nem segredo).
9. **Sem `RequestContext` nas rotas assinadas.** Um contexto de sistema passaria por toda permissão (`AccessPolicy.IsAllowed` devolve `true` sem principal); os serviços recebem o tenant explícito.
10. **Concorrência.** Trocas simultâneas do mesmo ticket (1 vencedor), duas primeiras entradas do mesmo usuário externo (1 usuário), criação simultânea de chave (limite de 2 ativas sob `FOR UPDATE` da conexão).

## Pontos para olhar com atenção (dúvidas do implementador)

- `IntegrationRequestAuthenticator` audita cada recusa de chave conhecida: o limite de taxa por chave (padrão 120/min) basta contra inundação da trilha?
- `ExchangeAsync` consome o ticket e só depois confere verifier/origem: aceitável (o ticket não é segredo contra quem o roubou, o verifier é)? A recusa de origem malformada/verifier mal formado ocorre ANTES de consumir (não queima o ticket).
- `EmbeddedSessionPolicy.IsEligible` trata `Manager` como elegível (não é administrativo); é o desejado?
- A sessão embutida de um `Manager` leva as seis permissões do teto, mas as rotas do ContactCenter ainda aplicam fila/equipe: confira que nada na lista de rotas abre ação de supervisor (`assign`, `priority`, `handoffs` estão fora).
- O `exchange` é anônimo e sem CSRF por não usar cookie; confira o cabeçalho `Cache-Control: no-store` e a ausência do token em qualquer log (`OperationsLogging`, logs de acesso do Caddy).
- Pendente fora desta etapa: `frame-ancestors`/CSP (E7), `postMessage` e `HostBridge` (E4), SDK (E5).

## Como reproduzir

```bash
dotnet test backend/IaBimeta.slnx                       # gate completo (PostgreSQL de teste na 5433)
dotnet test tests/IaBimeta.IntegrationTests/IaBimeta.IntegrationTests.csproj --filter "FullyQualifiedName~IntegrationTests.Embedded"
dotnet test tests/identity/IaBimeta.Identity.Tests/IaBimeta.Identity.Tests.csproj
```
