# Etapa 05 — Interface de configuração dos provedores de inteligência artificial

Status: **implementada e validada somente com simuladores** (27/11/2026). Sem migration, sem chamada paga, sem chave real, sem instalar o Ollama, sem commit. A validação real dos provedores (OpenAI, Anthropic, Ollama) continua na **Etapa 13**; nada aqui comprova compatibilidade com as APIs comerciais.

## 1. Estratégia comercial aprovada (modalidade C, híbrida)

O ia.bimeta suportará, progressivamente, **A) BYOK** (o cliente usa a credencial do próprio contrato e paga direto) e **B) IA fornecida pela BImeta** (provedores comerciais ou infraestrutura própria). **Esta etapa implementa só o necessário para a modalidade A e para o Ollama em infraestrutura autorizada**: OpenAI e Anthropic com credencial própria, Ollama sem chave. Nada foi construído "para o futuro": não há credencial comercial compartilhada, repasse, plano, franquia, crédito, cobrança por token, faturamento nem limite monetário (Etapa 12). A convivência futura das duas modalidades fica aberta porque o campo `supply` de cada provedor (`Byok` ou `Infrastructure`) já distingue quem fornece, e a origem da credencial (`AiCredentialSource`) continua com `PlatformManaged` reservada e recusada.

## 2. Diagnóstico e componentes reaproveitados

| Já existia (reaproveitado, não duplicado) | Uso nesta etapa |
|---|---|
| `AiTenantConfigurationService` (BYOK cifrado AES-GCM por tenant+provedor, padrão, auditoria, conflito por versão) | Cadastro, substituição e remoção de credencial; escolha do padrão |
| `AiGatewayService` (suspensão, limites, ledger, adaptadores) | Teste de geração |
| `AiModelCatalog` / `AiModelSelector` / `AiProviderRegistry` | Provedores e modelos autorizados |
| `IAiProvider.RequiresCredential` (Etapa 04) | Ollama sem chave, OpenAI e Anthropic exigem |
| `OllamaServerProbe` (Etapa 04) | Verificação do servidor Ollama (agora também usada pelo teste de conectividade) |
| Permissões híbridas e catálogo de permissões (Etapa 01); suspensão (Etapa 02) | Permissões de IA; regras de suspensão |
| `Configuracoes` no React, `createApi`, `criarFetch` dos testes | Nova aba "Inteligência artificial" |

**Novo:** `AiProviderAdminService` (Core do AiGateway, casos de uso da tela), `AiAccess`, `AiDiagnosticStore`, `AiDiagnosticThrottle`, a porta opcional `IAiProviderDiagnostics` nos três adaptadores, `AiGatewayService.GenerateDiagnosticAsync`, o projeto `IaBimeta.AiGateway.Presentation` (rotas) e o componente `IaProvedores`. A `Presentation` é a C1.5 prevista no ADR 0017 (a API administrativa); a matriz de dependências entre módulos **não mudou** (teste de arquitetura atualizado: Presentation referencia só o SharedKernel e o Core).

## 3. Responsabilidades

| BImeta (plataforma) | Tenant |
|---|---|
| Disponibilizar provedores; administrar o catálogo autorizado de modelos; configurar endpoints; administrar a infraestrutura Ollama; definir capacidades e restrições técnicas; (futuro) modalidades comerciais | Cadastrar, substituir e remover **credenciais próprias**; testar conectividade e geração; escolher **provedor e modelo padrão** dentre os autorizados; consultar a situação |

O tenant **não** pode: alterar endpoints, informar URL, habilitar modelo fora do catálogo, alterar preços ou limites comerciais, acessar credencial de outro tenant, nem recuperar uma chave já guardada. **Operadores da plataforma não recebem nenhuma das permissões de IA e nenhuma rota devolve o valor de uma chave.** O catálogo e os endereços continuam sendo configuração de infraestrutura (`AiGateway:Catalog`, `AiGateway:Providers:*`), sem interface nesta etapa.

## 4. Permissões

Cinco permissões novas, de **escopo do tenant** (nunca de organização), delegáveis pelo mecanismo da Etapa 01 (catálogo "Inteligência artificial"; proibição de escalada preservada), concedidas **por padrão só ao administrador do tenant**:

| Permissão | Faz |
|---|---|
| `AiSettingsRead` | consultar provedores, modelos, situação das credenciais e diagnósticos |
| `AiSettingsManage` | escolher o provedor e o modelo padrão |
| `AiCredentialManage` | cadastrar, substituir e remover credenciais |
| `AiConnectivityTest` | testar conectividade (sem geração) |
| `AiGenerationTest` | testar geração (pode gerar cobrança) |

A permissão ampla `TenantSettingsManage` (que já abrangia a IA antes desta etapa e continua só de administrador) também vale para todas (`AiAccess`), para não quebrar o que existia. Supervisores, atendentes e usuários **não** recebem nada por padrão; o Embedded não alcança as rotas (não estão na lista de rotas permitidas e as permissões não entram na política da sessão embutida). **A segurança está no servidor** (`AiAccess.Require` em cada caso de uso, 403); a interface só esconde o que não pode.

## 5. API administrativa (`/ai`, `IaBimeta.AiGateway.Presentation`)

| Rota | Faz | Permissão |
|---|---|---|
| `GET /ai/settings` | padrão atual, provedores, modelos autorizados, credenciais (só presença, impressão digital e data), diagnósticos, avisos | `AiSettingsRead` |
| `PUT /ai/settings/default` `{providerId, modelId}` | escolhe o padrão | `AiSettingsManage` |
| `PUT /ai/providers/{id}/credential` `{apiKey}` → 204 | cadastra ou substitui | `AiCredentialManage` |
| `DELETE /ai/providers/{id}/credential` | remove; devolve `{revoked, wasDefaultProvider}` | `AiCredentialManage` |
| `POST /ai/providers/{id}/connectivity-test` `{modelId?}` | verificação sem geração | `AiConnectivityTest` |
| `POST /ai/providers/{id}/generation-test` `{modelId, confirmed}` | geração sintética | `AiGenerationTest` |

DTOs próprios (nunca entidades). O tenant vem do contexto da sessão: um `tenantId` no corpo é ignorado, e **não existe** rota para informar URL, identificador livre de modelo, preço ou limite. Erros: 401 sem sessão, 403 sem permissão, 400 validação (código estável), 409 conflito (`credential-required`, `tenant-suspended`, `test-in-progress`, `config-changed`), 429 `rate-limited` com `Retry-After`, 503 `secrets-unavailable`.

## 6. Credenciais

- **Reaproveitamento integral do BYOK**: cifra AES-256-GCM em repouso, vinculada a tenant+credencial+provedor; isolamento por tenant. A substituição é **uma só gravação** do registro (o valor novo é cifrado antes; a anterior não é removida antes de a nova estar gravada).
- A API **nunca devolve** a chave. A interface mostra só: cadastrada ou não, **impressão digital** (12 hex, já existente), data de atualização e o resultado do último teste.
- Sem chave em log, auditoria ou mensagem de erro (testes de integração varrem as tabelas do esquema, a auditoria e os logs do Api e do Worker; testes de interface conferem o DOM, o `localStorage` e o `sessionStorage`).
- Frontend: campo de **senha** (`autocomplete="new-password"`), enviado uma vez e **apagado do estado** antes mesmo do fim do envio (sucesso, falha e cancelamento); nunca é lido de volta para preencher edição, nem guardado em URL, armazenamento do navegador ou estado global.
- **Ollama não tem chave** (`RequiresCredential = false`): a API recusa cadastrar credencial para ele (`credential-not-used`) e a interface não mostra campo. **OpenAI e Anthropic continuam exigindo** (`RequiresCredential = true`, padrão).
- Cadastrar ou substituir ou remover a credencial **invalida** os diagnósticos daquele provedor.

## 7. Múltiplos provedores e padrão

Um tenant mantém simultaneamente credencial OpenAI, credencial Anthropic e acesso ao Ollama (quando a plataforma ativou o modelo no catálogo). **Há um único provedor/modelo padrão por tenant** (por agente é a Etapa 09). Cadastrar credencial ou testar outro provedor **nunca altera o padrão**. **Não há fallback automático nem troca silenciosa de modelo.**

`PUT /ai/settings/default` valida **no servidor**: adaptador registrado (`provider-not-implemented`), modelo **ativo no catálogo** (`model-not-authorized`; identificador livre é recusado), credencial presente quando exigida (`credential-required`) e coerência (a situação habilitada/desabilitada da IA é preservada; `Blocked` continua só da plataforma). **Configuração elegível não é configuração testada:** salvar **não faz chamada** ao provedor; a resposta traz avisos (`connectivity-not-verified`, `generation-not-tested`, `model-unavailable-per-last-check`). Só um diagnóstico **fresco** que diga que o modelo **não serve à conversa** recusa a escolha (`model-not-conversational`); sondagem negativa ou inconclusiva **nunca** é tomada como confirmação de disponibilidade.

**Remoção da credencial do provedor padrão:** é **sempre permitida** (inclusive por segurança). A interface avisa o impacto antes de confirmar; depois, o aviso `default-credential-missing` fica visível. O atendimento **falha fechado** (o AiGateway devolve `credential-missing` sem chamar nenhum provedor), e a conversa segue o tratamento existente de indisponibilidade da IA: transferência ao atendente. **Limitação documentada:** uma chamada de IA já admitida (em voo) antes da remoção termina com a credencial que já foi resolvida; as seguintes falham. Nenhum outro provedor é usado (teste de integração).

## 8. Estados e diagnósticos

A interface **não apresenta provedor como operacional só porque existe adaptador**. Estados mostrados, separados: implementado, autorizado (modelos ativos), configurado, credencial cadastrada, conectividade não verificada / verificada / falha de autenticação / servidor inacessível / modelo indisponível / não suportada / inconclusiva, modelo autorizado, geração testada / falha / não executada. **Cada resultado leva a data e hora**; fica **desatualizado** (e nunca confirma) quando a credencial muda ou é removida depois do teste, ou passa de 24 horas. Para o **Ollama** a tela mostra os seis estados separados: cadastrado no catálogo, habilitado, servidor acessível, modelo instalado, capacidade conversacional **declarada** e compatibilidade funcional **validada** (só o teste de geração a confirma).

**Armazenamento dos resultados:** em memória do Api (`AiDiagnosticStore`), por tenant, provedor, modelo e tipo. **Sem migration.** Limites assumidos: o resultado não sobrevive a um reinício do Api nem é compartilhado entre instâncias; nesses casos volta a "não verificado", nunca a "verificado". O histórico durável fica na auditoria e no ledger (os testes de geração). Se um dia for preciso persistir, é migration aditiva à parte (com autorização).

### 8.1 Teste de conectividade (sem geração)

Cada adaptador implementa a porta opcional `IAiProviderDiagnostics` com **só o que a documentação oficial oferece** (lida em 27/11/2026):

| Provedor | Verificação | Referência | O que a documentação NÃO diz |
|---|---|---|---|
| OpenAI | `GET {base}/models/{modelo}` com `Authorization: Bearer` (objeto `id`, `object`, `created`, `owned_by`) | https://developers.openai.com/api/reference/resources/models | códigos 401/403/404 desta chamada; se é gratuita ou limitada por taxa; escopo de chave restrita (um 403 vira *inconclusivo*, nunca "indisponível"); **não há campo de capacidades** (conversa fica "não verificado") |
| Anthropic | `GET {base}/models/{modelo}` com `Authorization: Bearer` e `anthropic-version` (`id`, `lifecycle`, `capabilities`...) | https://platform.claude.com/docs/en/api/models | se é gratuita; o 404 específico do modelo inexistente; se `x-api-key` serve aqui; nenhuma capacidade diz "serve à conversa" (fica "não verificado"); `retired` = indisponível |
| Ollama | `GET /api/version`, `GET /api/tags`, `POST /api/show` (`OllamaServerProbe`) | docs.ollama.com (ver `ETAPA-04-OLLAMA.md`) | o diagnóstico é instantâneo; "declara `completion`" não é compatibilidade funcional |

**Que seja gratuita não está documentado**: a chamada não gera texto nem consumo registrado no ledger, mas a cobrança, se houver, não é afirmada. Regras: **não substitui por geração** (provedor sem verificação própria responde `supported: false` e nada é verificado); timeout de 10 s, cancelamento, sem redirecionamento, **sem dado de cliente**, não altera o padrão, proteção contra abuso (10 por 5 minutos por empresa e **um teste por vez por provedor**), respeita a suspensão e a autorização. Nunca devolve o endereço do servidor nem texto do provedor.

### 8.2 Teste de geração

Operação **distinta**: `AiGatewayService.GenerateDiagnosticAsync(provedor, modelo)`. Provedor e modelo são escolhidos **explicitamente** (do catálogo) **sem tocar o padrão** do tenant, passando pela **mesma sequência** do atendimento: suspensão, habilitação (IA desabilitada ou bloqueada recusa), limites do tenant (os padrão da plataforma se ele ainda não tem configuração), credencial **do tenant**, vaga e **ledger**, adaptador. A mensagem é **sintética e fixa** (sistema e usuário definidos no código, sem conversa, canal, workflow, ferramenta nem consulta externa), com **saída limitada a 64 tokens** e tempo de 30 s. **Exige confirmação explícita** (`confirmed: true`; a interface avisa que provedores comerciais podem cobrar). O consumo é registrado como qualquer chamada, com `correlation_id = diagnostic:<id>` (a finalidade diagnóstica no ledger, **sem coluna nova**). Não faz fallback. Proteção contra abuso: 5 por 10 minutos por empresa e um por vez por provedor (429 com `Retry-After`). Falha **antes** de chamar o provedor (limite, suspensão, configuração) aparece como "não executado" e não vira "falha do provedor" guardada.

## 9. Consumo e custos

O **ledger não foi alterado**. Os testes de geração são operações reais de consumo: `Confirmed`/`Reported` com tokens do provedor, ou `Indeterminate`/`Unknown` quando o resultado é incerto (cobra a reserva). Preservada a distinção entre consumo confirmado, desconhecido, ausência comprovada e **ausência de tarifa comercial por token** (Ollama: custo estimado 0, que não significa custo operacional zero). Gestão financeira: Etapa 12.

## 10. Auditoria

Reaproveita o módulo Audit. Entradas (tenant, usuário, data, correlação do contexto): `ai-gateway.credential.set` e `.revoke` (recurso `provedor:credencial`), `ai-gateway.settings.set` (recurso `provedor:modelo`), `ai-gateway.connectivity.test` e `ai-gateway.generation.test` (recurso `provedor:modelo`, resultado `Success`/`Failed`). **Nunca** chave, texto cifrado, prompt nem resposta. **As recusas por permissão (403) também são auditadas**, com resultado `Denied` (operação e tipo de recurso, sem provedor, modelo nem dado), como nos demais módulos.

## 11. Concorrência

- Troca da credencial **durante** um teste: o teste guarda o marcador (impressão digital) da credencial com que começou; ao terminar, **relê sem rastreamento** o que está gravado; se mudou, o resultado **não é guardado como validado** (`superseded: true`). Cobre também a remoção.
- Dois testes do mesmo provedor ao mesmo tempo: o segundo é recusado (`test-in-progress`); excesso de testes: 429.
- Alteração concorrente de padrão e credencial: o token de versão e os índices únicos existentes viram `409 config-changed`; a interface recarrega.
- Suspensão: empresa suspensa recusa os dois testes (409 `tenant-suspended`) sem chamar o provedor nem gravar ledger; a reativação **não refaz nada** sozinha.
- Sem arquitetura distribuída: o limitador e o armazenamento de diagnósticos são do processo (ver limites na seção 8).

## 12. Interface

Configurações → **Inteligência artificial** (só com `AiSettingsRead`; a área Configurações abre também para quem só tem essa permissão). Duas áreas:

- **Provedores**: um cartão por provedor (OpenAI, Anthropic, Ollama) com nome, tipo de fornecimento ("credencial própria da empresa" ou "servidor da infraestrutura, sem chave"), situação, credencial (cadastrada/não, identificador, data), modelos autorizados com janela de contexto, limite de saída e resultados dos diagnósticos (com data e "desatualizado"), selo de provedor padrão. Ações por permissão: cadastrar/substituir credencial, remover (com confirmação e aviso de impacto), testar conexão, testar geração (com confirmação de cobrança). **Nenhum campo de endereço de servidor.**
- **Modelo padrão**: escolha de provedor e modelo autorizado, características técnicas, avisos de indisponibilidade e de "não testado".

Estados claros: carregando, sucesso, falha, ausência de configuração, permissão insuficiente (403), serviço indisponível (com "tentar de novo"). Reaproveita os estilos e componentes existentes.

## 13. Testes

Todos com **servidores HTTP simulados** (nenhuma rede, chave ou modelo real).

| Suíte | Resultado (27/11/2026, implementador) |
|---|---|
| Integração da Etapa 05 (`ProvedoresDeIaAdminTests` 19 + `ProvedoresDeIaApiTests` 4) | 23 aprovados |
| Integração direcionada ampla (Identity, Usuários, Permissões, Embedded, AiGateway, Agents, Provision, DevelopmentAi, Tenancy, Retenção, Plataforma), antes dos ajustes finais de dois testes e da auditoria de recusas | 734 de 736; as 2 falhas eram **dos próprios testes novos** (dependiam de contagens globais dos simuladores e da ordem de auditorias com o relógio controlado), corrigidos e reexecutados (23 aprovados). **A faixa ampla não foi reexecutada depois** da auditoria de recusas (alteração só no `AiProviderAdminService`) |
| OpenAI / Anthropic / Ollama (adaptadores) | 98 / 94 / 106 |
| AiGateway unitário / Identity / SharedKernel / Tenancy / Agents unitário | 197 / 338 / 112 / 116 / 908 |
| Arquitetura | 92 |
| Frontend (todas as suítes; 26 novas) | 541 (38 arquivos) |

- **Backend (integração, PostgreSQL real):** `ProvedoresDeIaAdminTests` (19): consulta sem estados positivos inventados; isolamento entre empresas; permissões específicas; cadastro/substituição/remoção sem exposição (tabelas, auditoria) e invalidação de diagnósticos; credenciais simultâneas sem mudar o padrão; Ollama sem chave; validações de padrão; elegível × testado; conectividade sem geração e sem ledger; falhas de autenticação/modelo/inconclusivo; teste de geração (confirmação, AiGateway, ledger `diagnostic:`, padrão intacto, 64 tokens); ausência de fallback; suspensão; credencial trocada durante o teste; concorrência e limite; revogação da credencial do provedor padrão. `ProvedoresDeIaApiTests` (4): 401/403 em todas as rotas, sem chave em resposta ou log, validação do servidor, tenant do corpo ignorado, ausência de rotas de URL/preço/limite.
- **Adaptadores (unidade):** `OpenAiConnectivityTests`, `AnthropicConnectivityTests`, e os de conectividade do Ollama em `OllamaChatProviderTests`.
- **Contratos/arquitetura:** `GatewaySemCredencialTests` e a atualização do grafo de projetos.
- **Frontend (26):** `IaProvedores.test.tsx` cobre provedores, estados, permissões, formulários de credencial, limpeza, confirmações de remoção e de geração, seleção do padrão, carregamento, falhas e atualização após cada operação.

## 14. Procedimento de validação funcional (simuladores)

1. `Start-WhatsAppSimulado.ps1` (5198), `Start-OpenAiSimulada.ps1` (5199), `Start-AnthropicSimulada.ps1` (5200) e `Start-OllamaSimulado.ps1` (5201); PostgreSQL 5433.
2. Api e Worker com `AiGateway__Providers__OpenAi__BaseAddress=http://127.0.0.1:5199/v1/`, `AiGateway__Providers__Anthropic__BaseAddress=http://127.0.0.1:5200/v1/`, `AiGateway__Providers__Ollama__BaseAddress=http://127.0.0.1:5201/` e `AiGateway__Catalog__Models__3__Status=Active` (o modelo do Ollama vem desativado).
3. `.\tools\dev\Novo-TenantDeProvedoresDeDemonstracao.ps1` cria a empresa `demo-provedores` (administrador com as permissões de IA e uma atendente sem elas).
4. Interface (http://localhost:5173): entrar como o administrador e percorrer **Configurações → Inteligência artificial**: cadastrar a chave sintética da OpenAI e a da Anthropic (qualquer texto serve nos simuladores); ver o Ollama sem campo de chave; testar conexão e geração de cada um; escolher o padrão; enviar uma mensagem por `Send-MensagemWhatsApp.ps1 -Tenant demo-provedores` e ver a resposta do provedor escolhido; trocar o padrão e repetir; substituir e remover credenciais; parar o simulador do provedor padrão e ver o atendimento transferir ao atendente sem usar outro provedor; entrar como a atendente e confirmar que a aba não existe.

## 15. Limitações e pendências

1. **Nada foi validado com as APIs reais**: formatos seguem a documentação oficial (seção 8.1). Pendência da **Etapa 13** (incluindo a verificação de modelos da OpenAI e da Anthropic, o 404 de modelo inexistente, a eventual cobrança e os limites de taxa das chamadas de modelos).
2. Resultados dos diagnósticos **em memória** (seção 8): não persistem, não são compartilhados entre instâncias.
3. Os limites de abuso são **do processo** (não distribuídos).
4. Uma chamada de IA já admitida antes da remoção da credencial termina com a credencial já resolvida.
5. O tenant não liga ou desliga a IA pela tela (só escolhe o padrão); a situação habilitada/desabilitada é preservada.
6. Nomes comerciais dos provedores vivem na interface (o catálogo não os traz); capacidades dos modelos só quando o provedor as informa (Ollama) ou o catálogo as declara (janela e saída).
7. Catálogo, endpoints e infraestrutura Ollama continuam sem interface (configuração da plataforma).
8. Preservadas as pendências das Etapas 01 a 04, 12 e 13, e as jurídicas (retenção, política de uso dos provedores, licença do modelo local).

## 16. Resultado da validação funcional local (27/11/2026, empresa `demo-provedores`, simuladores)

Feito **pela interface real** (navegador): login do administrador, aba Configurações → Inteligência artificial, cadastro da chave sintética da OpenAI (campo de senha, mensagem "Credencial salva (cifrada)... não será exibida de novo"), teste de conexão da OpenAI ("Conectividade verificada em 27/11/2026 11:35:47", com os itens acessível, autenticação, modelo disponível e capacidade "não verificado"). O restante foi exercido pelas **mesmas rotas que a interface chama** (curl, com o token do administrador): atendente sem permissão recebe 403; Anthropic (chave sintética, conectividade `Verified`, geração `Succeeded` com 900/40 tokens); Ollama sem chave (conectividade com os itens acessível/instalado/conversa declarada, geração 300/40; cadastro de chave recusado `credential-not-used`); OpenAI (geração); padrão trocado para OpenAI (sem avisos, pois conectividade e geração estavam válidas) e mensagem de WhatsApp respondida por `openai/gpt-5-mini`; padrão trocado para a Anthropic e nova mensagem respondida por `anthropic/claude-haiku-5-5` (ledger `Confirmed`/`Reported`); **substituição da credencial** (diagnósticos do provedor voltam a "não verificado/não testado"); **remoção da credencial do provedor padrão** (`wasDefaultProvider: true`, aviso `default-credential-missing`) e mensagem seguinte **sem nenhuma chamada** a provedor nem linha nova no ledger (5 antes e 5 depois), sem fallback; **indisponibilidade**: com o Ollama simulado desligado e como padrão, a mensagem gerou 3 tentativas `Indeterminate`/`Unknown` (`server-unavailable`), nenhuma chamada à OpenAI nem à Anthropic, e a conversa seguiu ao atendente. Nenhuma API comercial foi acessada. As telas dos demais passos (remoção e geração com confirmação, modelo padrão) estão cobertas pelos 26 testes de interface; **não foram todas percorridas no navegador nesta rodada**.
