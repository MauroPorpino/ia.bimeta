# ADR 0043 — Administração da plataforma BImeta: tenant técnico, empresas, operadores e suspensão efetiva

- **Status:** **Aceito (27/11/2026)** — proposto em 25/11/2026; ratificado pelo decisor após a revisão independente do Codex (APROVADO COM RESSALVAS NÃO IMPEDITIVAS) e a validação funcional. Encerramento **técnico** da Etapa 02; **não** autoriza produção nem exposição pública do painel (ver seção 10).
- **Data:** 25/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md`, ADR 0039/0041 (Embedded), ADR 0042 (usuários, vagas e primeiro acesso, Aceito).
- **Altera (aditivamente):** `Tenancy` (tipo do tenant, dados cadastrais, situação `Provisioning`, histórico de situação, administração de empresas), `Identity` (papéis e permissões de plataforma, operadores, login e sessão separados, revogação de sessões do tenant), `Audit` (consulta paginada), `SharedKernel` (permissões de plataforma e a porta `ITenantOperationGate`), consumidores do processamento automático (`AiGateway`, `Agents`, `Channels`, `Workflows`) e o host (rotas `/platform/*`, comando `platform-init`).
- **Não altera:** a matriz de dependências entre módulos (nenhuma aresta nova: a porta nova vive no `SharedKernel`), E1-R, E2, ADR 0042.

## Contexto

A BImeta precisa administrar as empresas contratantes (cadastro, limite, habilitação de cadastros, suspensão), e cada empresa administra a própria operação. Até a Etapa 01 só havia comandos de operador (`provision`, `tenant-users`, `first-admin`) e `Tenant.Suspend()` sem nenhum efeito além do login.

## Decisões

### 1. Tenant técnico reservado para os operadores (reaproveita o Identity)
- `tenancy.tenants.kind` (1 = Commercial, 2 = Platform) e o identificador técnico reservado `bimeta-platform`, **único** (índice parcial `ux_tenants_platform`). Criado de forma idempotente pelo comando `platform-init`.
- Os operadores são **usuários desse tenant**, com os mesmos mecanismos da Etapa 01 (hash de senha, token de primeiro acesso `ibr_…`, ajustes individuais, revogação de sessões, trava de admissão). Não há sistema paralelo de autenticação.
- **Não é empresa contratante:** nunca aparece em listagens, indicadores nem na consulta de empresas; o tenant técnico não tem política de limite, então os operadores **não ocupam vagas comerciais**.
- **Identificadores reservados** (`bimeta-platform`, `platform`, `plataforma`, `admin`, `api`, `www`, `bimeta`, `system`… — `Tenant.IsReservedSlug`) são recusados no cadastro de empresas, antes de qualquer outra verificação.
- **Alternativas descartadas:** (a) um segundo `DbContext`/esquema de usuários da plataforma (reconstruiria autenticação, sessões e auditoria); (b) permissões de plataforma como concessões de um tenant comercial qualquer (um erro de concessão daria acesso a todas as empresas).

### 2. Autoridades distintas, sessões separadas, rotas protegidas
- Papéis novos no `Role`: `PlatformAdministrator`, `PlatformCommercial`, `PlatformViewer`; permissões `Platform*` no `Permission` (consultar, cadastrar, alterar dados, alterar cota, reemitir primeiro acesso, suspender, gerir operadores, gerir permissões, auditoria, visão geral). Os papéis só valem no **escopo do tenant** e só no tenant técnico.
- **Fora do tenant técnico as permissões de plataforma não existem:** o `SessionAuthenticator` as descarta em qualquer empresa e, no tenant técnico, mantém **só** elas; a concessão de papel de plataforma em uma empresa é recusada pelo `UserAdminService`; sessão embutida não existe na plataforma. O teste grava o papel direto no banco e prova que não vira acesso.
- **Login separado:** `POST /platform/auth/login` autentica só no tenant técnico; o login das empresas (`/auth/login`) trata o slug da plataforma como tenant desconhecido (mesma recusa) e o da plataforma não autentica usuário de empresa.
- **Borda HTTP:** a sessão da plataforma tem origem própria (`platform-session`); `PlatformRouteGuardMiddleware` só a deixa alcançar `/platform/*` e a conta própria (`/auth/me`, senha, logout); a sessão de uma empresa (interativa ou embutida) recebe 403 em `/platform/*`. A autorização de cada operação continua no serviço (a cerca de rota é a primeira camada).
- **Sem executor genérico:** cada operação da plataforma é uma operação **nomeada** (`PlatformCompanyService`): autoriza no tenant técnico (contexto de **usuário**; contexto de sistema não passa), executa o passo do módulo dono num contexto de sistema da empresa-alvo criado pelo host a partir do registro (nunca de parâmetro de requisição) e audita no tenant da plataforma. A plataforma não cadastra usuários da empresa (só o primeiro administrador), não lê usuários, conversas, organizações, filas, agentes nem canais; só vê **contagens** de vagas.

### 3. Provisionamento de empresa (retomável)
`POST /platform/companies`: (1) a empresa nasce em `Provisioning` (não opera: login, sessões e processamento já a tratam como não ativa); (2) política de usuários com **limite obrigatório (≥ 1)** e habilitação; (3) primeiro administrador (ocupa uma vaga; token de uso único, até 24 h, exibido uma vez); (4) a empresa passa a `Active`. Se um passo falhar a empresa continua em `Provisioning` e repetir o cadastro com o mesmo identificador **retoma** (o token é reemitido e o anterior deixa de valer). Nada operacional é criado (fila, agente, canal, IA): é da própria empresa. A reemissão do token reusa a regra da Etapa 01 (só para o administrador **inicial** que não concluiu o primeiro acesso; nunca recupera conta ativa).

### 4. Limite e habilitação
Reaproveitam `identity.tenant_user_policies` e as operações transacionais da Etapa 01 (`ChangeAsync` altera só o campo pedido, sob a trava). Rotas `/platform/companies/{slug}/quota` e `/enrollment` (nomes escolhidos para não colidir com o teste da Etapa 01 que garante que nenhuma rota *do tenant* trata de limite). Redução abaixo do uso não desativa ninguém; novos ingressos continuam bloqueados.

### 5. Suspensão efetiva (e reativação)
Situação `Suspended` com **motivo obrigatório (5–500)**, confirmação (digitar o identificador), operador, instante e situação anterior em `tenancy.tenant_status_changes` (só inclusão) e na auditoria. A porta `ITenantOperationGate` (`SharedKernel`; implementada no Tenancy; **dependência opcional** dos módulos, **falha fechada**) alimenta o processamento automático:

| Caminho | Comportamento suspenso | Depois da reativação |
|---|---|---|
| Login, sessão, ticket/troca Embedded, HMAC de integração, reset de senha | Já bloqueados pelo status do tenant (Identity) | Sessões anteriores **revogadas** na suspensão **e de novo antes de reativar**: novas sessões são exigidas |
| `AiGatewayService` (custo de IA) | Falha `tenant-suspended` sem tocar provedor, credencial nem ledger | normal |
| `TurnScheduler` (mensagem recebida) | Nenhum turno de IA nasce; a mensagem fica preservada no Conversations | A mensagem recebida na suspensão **não** é reprocessada |
| `TurnRunner` / executor / reconciliador (Worker) | Não reivindicam; turno devolvido sem gastar tentativa | Turno anterior à reativação é **cancelado**; o reconciliador ignora sessões cuja última mensagem é anterior à reativação |
| Workflows (mensagem recebida, conteúdo pronto) e executor | Nenhum fluxo escolhido ou acordado; executor pula a empresa | idem |
| Sender de mensagens (Channels) | `Defer` de 2 min **antes** de gravar a intenção e de chamar o provedor (sem gastar tentativa) | Pedido anterior à reativação: **falha definitiva visível** (`tenant-suspended`), recuperável pelo atendente (`RequestResend`); nada é liberado em bloco |
| Recebimento (webhook, Inbox, gravação da mensagem, **download de mídia**) | **Preservado** (a mídia recebida continua sendo baixada: é preservação, não atendimento) | — |
| Dados, usuários, configurações, histórico, limite, habilitação | Preservados | Preservados |

**Limites declarados:** chamadas externas já iniciadas não são canceladas; um turno de IA já em andamento pode falhar (a falha é tratada pelo caminho normal, e qualquer aviso de transferência cai no Sender suspenso); o indexador semântico do Knowledge (Ollama local, sem custo de IA externa) **também respeita a suspensão** (BL-29, corrigido em 26/11/2026): depois de reservar o lote e antes de chamar o provedor de vetores, consulta a porta por empresa (porta própria do módulo sobre o `ITenantOperationGate`, sem aresta nova); empresa suspensa, ou com a consulta falha (falha fechada), tem os textos devolvidos à fila adiados, sem gastar tentativa, sem erro e sem apagar nada, e as demais seguem; reativada, a fila pendente prossegue sozinha, sem gerar turno, resposta nem fluxo.

### 6. Auditoria
Operações da plataforma gravam no tenant técnico (operador, operação, recurso, resultado; **sem campo livre**: nunca senha, token ou chave). Alterações de limite e habilitação codificam anterior e novo no `ResourceId` (`{tenantId}:{antes}.{depois}`, `u` = ilimitado). Consulta `GET /platform/audit` (permissão própria) com filtros por operação, recurso, resultado e período, paginada. Negações (403) também são auditadas.

### 7. Segurança administrativa e 2FA (proposta — **não implementada**)
Implementado: login com limitação de tentativas (a do Identity), sessões independentes e com expiração, revogação, proteção contra elevação (um papel só é atribuído por quem tem todas as permissões dele; sem autoadministração; cobertura das permissões do alvo), proteção do último administrador efetivo sob a trava, auditoria de autenticação, ingresso público barrado (`PublicIngressGuard` só alcança o webhook).
**Enquanto não houver 2FA o painel fica restrito ao ambiente local ou a rede administrativa protegida; não está autorizado para exposição pública.**

**Chave `Platform:Enabled` (decisão de 26/11/2026, implementada).** A disponibilidade da administração é decidida no BACKEND, não na interface:
- Padrão: **ligada só em `Development`; desligada em qualquer outro ambiente** (Production, Staging…). A configuração explícita (`Platform:Enabled = true|false`, variável `Platform__Enabled`) vence o padrão.
- Desligada: **todo caminho sob `/platform`** (login, `auth`, empresas, operadores, auditoria, qualquer outro, em qualquer método) responde **404 sem corpo**, antes da autenticação e do limite de taxa (`PlatformAvailabilityMiddleware`), e as rotas nem são mapeadas. Uma sessão de plataforma que tenha sobrado de antes também recebe 404 em qualquer rota (`PlatformRouteGuardMiddleware`).
- **Não afrouxa o ingresso público:** o `PublicIngressGuard` continua o PRIMEIRO da fila e inalterado (com a porta pública configurada, ou com o rastro de túnel — `Cf-Ray`, `Cf-Connecting-Ip`, `X-Forwarded-For` —, só o webhook é alcançável, com a plataforma ligada ou não).
- **Não afeta as empresas contratantes:** nenhuma rota, permissão ou sessão delas muda; o comando operacional `platform-init` (linha de comando, não HTTP) segue disponível.
- **Não substitui a proteção de rede.** Ligar a chave fora do Development só é aceitável em rede administrativa protegida (VPN, sub-rede de gestão, ingresso privado), sem exposição pública, e **só até** o 2FA (BL-28, obrigatório antes de qualquer exposição pública do painel) existir. O Compose do piloto roda em Production e, portanto, entrega a plataforma DESLIGADA: não há nada a configurar para mantê-la fechada.
- Testes: `PlataformaDisponibilidadeTests` (API real, PostgreSQL real).
**Proposta TOTP (RFC 6238), a aprovar antes da exposição:** tabela aditiva `identity.user_totp_factors` (segredo cifrado com a chave mestra existente, estado `Pending|Active`, último passo aceito para impedir repetição) e `identity.user_recovery_codes` (hash); enrolamento no primeiro acesso do operador (QR + confirmação do primeiro código); o login da plataforma passa a emitir uma sessão em dois passos (`/platform/auth/login` → desafio → `/platform/auth/totp`), sem sessão utilizável antes do segundo fator; recusa de login do operador sem fator ativo quando a política `RequireTotp` da plataforma estiver ligada; redefinição de acesso pelo administrador remove o fator e exige novo enrolamento. Impacto: uma migration do Identity, mudança no fluxo de login só da plataforma (o das empresas não muda). Alternativa avaliada: IdP externo (OIDC) — exige dependência e operação novas, fora do escopo.

### 8. Correções da revisão independente do Codex (26/11/2026, 6 P1, 1 P2, 1 P3)
Todas aditivas, **sem migration**. Cada uma com teste que falha sem a correção e passa com ela.

| Id | Defeito | Decisão |
|---|---|---|
| P1-01 | Remover uma restrição (ou restaurar o perfil) podia conceder ao alvo permissões que o responsável não tem | `RemoveOverrideAsync`/`ResetOverridesAsync` calculam as permissões efetivas **resultantes** e exigem que estejam cobertas pela autoridade do responsável, **dentro da trava** do tenant técnico, relendo as permissões atuais do responsável do banco (vale o menor entre o contexto e o estado persistido). Recusa audita `Denied` e não grava |
| P1-02 | O gate lia o tenant por entidade rastreada (EF reaproveita a entidade já lida no mesmo `DbContext`): `Status` e `ReactivatedAt` ficavam velhos | `ITenantRepository.FindOperationStateAsync`: projeção **sem rastreamento**, consulta o banco a cada chamada; só o gate a usa (repositórios administrativos continuam rastreando). Falha fechada preservada |
| P1-03 | Mensagem recebida na suspensão criava instância de fluxo (via roteador chamado pelo Agents) que executava após a reativação; janela entre reivindicar e iniciar a execução; estímulo antigo entregue tarde | (a) `FlowRouter` não cria instância nem grava decisão com a empresa suspensa **ou** para evento anterior à última reativação (`IAutomationRouter.ResolveOnInboundAsync(..., eventOccurredAt)`); (b) handlers do Workflows e o `TurnScheduler` do Agents aplicam o **corte temporal** (`OccurredAt < OperationalSince` ⇒ consumido sem efeito); (c) `FlowRuntime` confere a empresa no **início efetivo** de cada execução e antes de realizar efeitos: suspensa ⇒ só solta o lease (o próximo olhar **não** é empurrado); instância com gatilho anterior à reativação ⇒ **cancelada com segurança** (`tenant-suspended`; nada enviado, nada apagado, decisão volta ao agente sem acionar a IA); efeito preparado antes da reativação ⇒ `Superseded`, nunca vira pedido novo de envio por ganhar data posterior. Mensagem **nova** depois da reativação funciona normalmente (a IA usa o histórico como contexto; só um fluxo novo exige sessão nova) |
| P1-04 | O Sender consultava a empresa só no início e podia esperar a vazão e chamar o provedor depois da suspensão | Reconsulta o estado persistido **depois das esperas**, antes de registrar a tentativa e de chamar o provedor: suspensa ⇒ `Defer` (sem gastar tentativa); reativada depois do pedido ⇒ falha definitiva visível. **Janela residual declarada (refinada na seção 9):** a verificação final passou a ficar DEPOIS das gravações, imediatamente antes do adaptador; a intercalação entre ela e a invocação imediata não é eliminada, e uma suspensão exatamente aí não cancela a chamada já admitida. Fechá-la por inteiro exigiria segurar uma trava durante a chamada de rede, o que não se faz; a coordenação mínima, se um dia for exigida, é um contador de envios em voo que a suspensão aguarda (drenagem) — não implementado |
| P1-05 | Login iniciado antes da suspensão podia gravar a sessão depois das revogações em lote e ela valer após a reativação | A **marca** é o instante em que a autenticação **começou** (`Session.CreatedAt`). Validação a cada requisição: `CreatedAt < ReactivatedAt` ⇒ sessão inválida (persistente, sem migration, reaproveita `ReactivatedAt`). Login: reconfere o estado persistido antes e **depois** de gravar; se a empresa foi suspensa/reativada depois do início, revoga a sessão recém-gravada e recusa. Embedded: ticket emitido antes da reativação não abre sessão, e a sessão aberta herda a mesma regra de validação |
| P1-06 | Retomada atrasada do provisionamento sobrescrevia `Suspended` com `Active` (entidade rastreada velha) | `TryCompleteProvisioningAsync`: `UPDATE … WHERE status = Provisioning` atômico; só Provisioning vira Active; idempotente; audita só quando faz a transição |
| P2 | `TenantRegistrationService` (cadastro operacional antigo) aceitava `bimeta-platform` e outros reservados | Regra única `Tenant.EnsureNotReserved`, usada pelo cadastro da plataforma e pelo antigo; o tenant técnico continua nascendo só por `EnsurePlatformTenantAsync` (`platform-init`) |
| P3 | `/platform/roles` sem autorização no serviço | Exige operador autenticado com `PlatformOperatorManage` (anônimo 401; empresa e demais perfis 403) |

Seams de teste (sem efeito em produção): `WorkerHost.Build(..., overrideServices)` (como o `ApiHost`) e parâmetro opcional `servicosDoWorker` nos ensaios de integração.

### 9. Correções finais da 2.ª revisão do Codex (26/11/2026; 3 P1 e 1 P2) e decisões de negócio ratificadas
Sem migration.

**P1-01 — Autoridade administrativa concorrente.** A permissão exigida era conferida antes da trava; a releitura sob a trava só cobria as permissões resultantes do alvo. Agora, **dentro da trava**, `RequireAdministrableAsync` (usado por todas as operações de operador) e `RequireResultCoveredAsync` relêem o responsável do banco (sem rastreamento). O responsável precisa continuar **ativo**, e a autoridade efetiva é a **interseção** entre o contexto original e o estado atual. `RequireAdministrableAsync` exige a **permissão própria da operação** pedida; `RequireResultCoveredAsync` exige `PlatformPermissionManage` no estado persistido **e** a cobertura das permissões resultantes do alvo. Recusa audita `Denied` e não grava. Preservados: cobertura das permissões resultantes, último administrador e autoadministração.

**P1-03 / regra dos fluxos após a reativação (ratificada).** A anterioridade de uma instância é a sua **data de criação** (`CreatedAt`), nunca `NextRunAt` (que pode ser um prazo futuro ou renovado por `Wake`). Regra aprovada:
- instâncias de fluxo **criadas antes da última reativação são canceladas** (`tenant-suspended`; nenhum efeito, nada apagado; histórico, mensagens e registros de execução preservados), seja por início de execução, `Wake`, timeout ou novo agendamento;
- mensagens antigas permanecem no histórico e **nenhuma** mensagem antiga dispara atendimento automático;
- uma mensagem **nova** em sessão antiga pode ser atendida pela IA, **respeitando o estado atual do atendimento**: o cancelamento **não** chama o Conversations para devolver a conversa à IA (o modo de atendimento atual, inclusive humano, continua valendo; a IA só responde se a sessão for da IA);
- uma **nova sessão** seleciona normalmente um fluxo. Não há mecanismo novo de roteamento: o roteador apenas deixa de tratar como dono da sessão o fluxo anterior à reativação e pede o cancelamento seguro.

**P1-04 — Ponto de admissão do Sender (decisão de negócio ratificada).** Além das verificações anteriores, o Sender faz uma **verificação final** do estado persistido (falha fechada) e do corte da reativação **depois de todas as gravações** (estado "Enviando" no Conversations, registro da tentativa) e **imediatamente antes de invocar o adaptador**, sem espera de banco, de vazão ou preparação entre as duas. Essa verificação é o **ponto de admissão**:
- **admitida** antes da efetivação da suspensão, a operação **pode concluir**, mesmo que a chamada externa ou a resposta ocorra depois;
- **não admitida** com a empresa suspensa: é bloqueada; a tentativa que não aconteceu é **desfeita e gravada** (`RevertAttempt`: volta à intenção, sem contar tentativa, sem "enviado", sem "resultado incerto") e o pedido é adiado (`Defer`); anterior à reativação: falha definitiva visível, também sem tentativa contabilizada;
- a regra **não** admite antecipadamente mensagens que ainda esperam fila, vazão, gravações ou preparação;
- falha na leitura do estado é tratada como recusa (falha fechada).
**Limite declarado:** a intercalação residual entre a admissão e a invocação **imediata** do adaptador **não é eliminada** (uma suspensão exatamente nesse instante não cancela uma chamada já admitida). Não há contador distribuído de operações em voo, fila nova nem sincronização adicional.

**P2 — Revogação física da sessão recusada.** O login anônimo revogava a sessão recém-gravada por um repositório que depende de `RequestContext` (indisponível ali). Agora usa o armazenamento próprio da autenticação (`IAuthenticationStore.RevokeSessionAsync`): `UPDATE` delimitado por **tenant e sessão**, no escopo interno de autenticação, persistindo `RevokedAt`; nenhum token é entregue e a invalidação por `ReactivatedAt` continua como segunda defesa.

## Migrations (somente bancos de desenvolvimento e teste)

| Módulo | Migration | Mudança | Valores iniciais |
|---|---|---|---|
| Tenancy | `AddPlatformTenantKindAndCompanyData` | `tenants`: `kind smallint not null default 1`, `legal_name`, `document`, `contact_name`, `contact_email`, `updated_at`, `suspended_at`, `reactivated_at`; índice único parcial `ux_tenants_platform` (`kind = 2`); tabela `tenant_status_changes` (id, tenant_id, from/to_status, reason ≤ 500, actor_user_id, changed_at) | todas as empresas existentes: **Commercial**, mesma situação que tinham; dados cadastrais vazios; nenhum registro de histórico; **nenhum usuário recebe privilégio de plataforma** |

**Compatibilidade:** `TenantStatus` ganha o valor 3 (`Provisioning`), nunca gravado em empresas existentes. Aditiva e sem backfill. **Risco de versão anterior sobre o banco atualizado:** a versão anterior ignora `kind` (o tenant técnico, se existir, apareceria como empresa comum), ignora a suspensão em todos os caminhos além do login/sessão (a IA, os fluxos e os envios continuariam) e não conhece os papéis `Platform*` (a leitura do `role` por string falharia para os operadores). **Recuperação:** antes de aplicar, backup (`tools\ops\Backup-BancoDeDesenvolvimento.ps1`); para voltar, restaurar o backup (a coluna nova não tem como ser revertida com segurança sem perder o histórico de suspensões); empresa suspensa deve ser reativada antes de qualquer retorno de versão.

## Consequências
- A BImeta opera as empresas por uma interface própria (`/plataforma`), sem acesso aos dados privados dos clientes; cada empresa continua dona dos seus usuários.
- Pendências: 2FA TOTP (acima, BL-28); redistribuição de atendimentos e demais itens do ADR 0042; testes de integração ponta a ponta do Worker sob suspensão (cobertos hoje por testes de unidade e pela validação funcional).

### 10. Encerramento técnico da Etapa 02 (27/11/2026)
**Aprovações.** (a) Revisão estática independente do Codex: **APROVADO COM RESSALVAS NÃO IMPEDITIVAS**, sem P0/P1 remanescentes nem regressões impeditivas; a única ressalva (P3, redação da seção 9) foi corrigida só na documentação, sem alterar código de produção. (b) Regras de negócio da seção 9 **ratificadas pelo proprietário** (suspensão e admissão de envios, fluxos e atendimento). (c) Validação funcional do proprietário satisfatória.

**Regras ratificadas (resumo).** Operação não admitida é bloqueada na suspensão; a verificação final imediatamente anterior ao adaptador é o ponto de admissão; operação admitida antes pode concluir; a intercalação residual entre admissão e invocação imediata é conhecida e aceita; pedido antigo nunca é enviado automaticamente após a reativação; `SendUnknown` segue conservador, sem reenvio automático. Fluxos antigos são cancelados com histórico e mensagens preservados; mensagem antiga não dispara atendimento; mensagem nova em sessão antiga pode ser atendida pela IA respeitando o estado atual (atendimento humano preservado); sessão nova seleciona fluxo normalmente.

**Testes (executados pelo implementador; a revisão do Codex foi estática e independente).** Tenancy 116, Identity 338, Workflows 60, Channels 159, Agents 908, AiGateway 193, Knowledge 24 e Arquitetura 80 aprovados; integração direcionada 700 aprovados, 12 pulados (pgvector) e 0 falhas. As **suítes completas do backend e do frontend não foram executadas** nesta rodada.

**Limitações e riscos residuais conhecidos.** Intercalação residual admissão → adaptador (seção 9); crash entre a gravação da tentativa e a verificação final resulta em `SendUnknown`; ficha de vazão tomada é perdida no adiamento; BL-26, BL-27, BL-28, BL-30 (parte restante) e BL-31 preservados no backlog.

**Obrigações preservadas (não implementadas no encerramento).** 2FA antes de qualquer exposição pública do painel (BL-28) e, enquanto não houver 2FA, painel restrito a ambiente local ou rede administrativa protegida; validação de `DeferAsync` com PostgreSQL/pgvector real na homologação; homologação operacional dos caminhos de suspensão e recuperação; homologação da migration `AddPlatformTenantKindAndCompanyData` e do procedimento de recuperação.

**Escopo do encerramento.** Encerramento técnico da Etapa 02, **não** autorização de produção nem de exposição pública. Próxima implementação prevista: Etapa 03 — Adaptador Anthropic, **ainda não iniciada**, dependente de autorização específica.
