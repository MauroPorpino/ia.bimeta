# Motor de Integração Empresarial (módulo `Capabilities`)

Guia de arquitetura, contratos, configuração, implantação e exemplos. Decisão: [ADR 0035](../adr/0035-motor-de-integracao-empresarial-modulo-capabilities-conector-rest-governanca-e-protecao-contra-ssrf.md) (Proposto). Nada aqui foi exercitado contra um sistema real: o conector só foi provado contra ERPs **simulados** em loopback (ver "Requisitos para homologação").

## 1. O que é

O ponto único e governado pelo qual o ia.bimeta consulta ou altera dados em sistemas externos (ERPs, legados). O modelo de IA, o Knowledge Engine e o chatbot **nunca** executam uma chamada externa: quem chama é código do produto, com o `RequestContext` confiável, por um catálogo de operações **cadastradas e publicadas pelo administrador do tenant**. O motor é genérico: LabNet e BImeta serão apenas conexões e operações cadastradas por configuração (ou, se o protocolo não for REST/JSON, um adaptador `ICapabilityConnector`).

```text
 Agents / Chatbot / Workflows / Assistente interno / Operador (tela)
        │  ICapabilityExecutor (Contracts)   ← único caminho; tenant e usuário vêm do RequestContext
        ▼
 CapabilityExecutionService ── autorização ─ estado ─ parâmetros ─ idempotência ─ limites ─ registro ─ confirmação
        │  ICapabilityConnector (porta)
        ▼
 RestJsonConnector ── GuardedHttpTransport (SSRF: confere cada endereço NA conexão) ──► sistema externo
```

Três conceitos separados:

| Conceito | O que guarda | Nunca guarda |
|---|---|---|
| **Conexão** (`integration_connections`) | endereço base, autenticação, limites, cabeçalhos permitidos, segredo **cifrado** | regra de operação |
| **Operação** (`capability_definitions`, versionada) | método, caminho, parâmetros tipados, mapeamento da resposta, efeito, idempotência, confirmação, origens permitidas | credencial |
| **Execução** (`capability_executions`) | quem, de que origem, atendimento correlacionado, versão, resultado, código de erro, duração | parâmetros, resposta, segredo |

## 2. Contratos (`IaBimeta.Capabilities.Contracts`)

```csharp
public interface ICapabilityCatalog   // o que está disponível para esta origem e esta pessoa
{
    Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(CapabilityCaller caller, CancellationToken ct = default);
    Task<CapabilityDescriptor?> GetAsync(string key, CapabilityCaller caller, CancellationToken ct = default);
}

public interface ICapabilityExecutor  // o ÚNICO caminho para uma operação externa
{
    Task<CapabilityResult> ExecuteAsync(CapabilityInvocation invocation, CancellationToken ct = default);
    Task<CapabilityResult> ConfirmAsync(Guid executionId, CancellationToken ct = default);   // só usuário com permissão de escrita
    Task<CapabilityResult> CancelAsync(Guid executionId, CancellationToken ct = default);
}

public sealed record CapabilityInvocation(string Key, IReadOnlyDictionary<string, JsonElement> Parameters,
    CapabilityCaller Caller, string? IdempotencyKey = null, Guid? ServiceSessionId = null);
```

- `Key` e `Parameters` são dado **não confiável** (podem vir de um modelo). `Caller` é escolhido pelo **código** que chama (`Operator`, `Agent`, `Workflow`, `EmployeeAssistant`, `Chatbot`).
- `CapabilityResult`: `Status` (`Succeeded`, `Failed`, `Rejected`, `AwaitingConfirmation`, `Unknown`, `InProgress`, `Cancelled`), `Output` (só os campos mapeados), `Error` (`Code` curto e fixo, `Message` fixa, `Retryable`, `UpstreamStatus`, `Problems` com **nome do parâmetro e regra**, nunca valor), `Replayed`, `ConfirmationExpiresAt`. `UntrustedContent` é sempre `true`.
- Porta de conector: `ICapabilityConnector` (`Kind`, `InvokeAsync`, `ProbeAsync`). O conector `rest-json` vem no módulo; outro protocolo entra como adaptador, registrado com outro `Kind`.

Regras de dependência: `Capabilities` referencia só `Audit.Contracts` (aresta já prevista na matriz, sem alteração). Quem consome (Agents etc.) referencia só `Capabilities.Contracts`, e **a aresta correspondente exige ADR** (BOUNDARIES.md §3).

## 3. Execução: ordem fixa, falha segura

1. Portão de função: usuário sem **nenhuma** permissão de integração recebe 403 antes de qualquer consulta (não sonda chaves nem ids).
2. Operação publicada, habilitada, em conexão habilitada; a origem está entre as permitidas da operação (padrão ao cadastrar: só `Operator`). `Operator` exige usuário autenticado.
3. Permissão sobre a **organização da conexão**: leitura `IntegrationExecute`; escrita `IntegrationExecuteWrite`. A conta técnica não amplia o que o colaborador pode.
4. Parâmetros validados contra o esquema (tipo estrito, tamanho, padrão regex não retrocedente, faixa, enumeração; nome desconhecido recusado; parâmetro de caminho só com conjunto seguro e nunca `.`/`..`).
5. Escrita exige **chave de idempotência** (`[A-Za-z0-9._:-]{8,100}`), escolhida por quem chama.
6. Limites (por tenant, por conexão, simultâneas, escritas/min).
7. A execução é **registrada antes** da chamada (índice único `(tenant, chave)` decide disputas).
8. Escrita com confirmação (padrão): fica `AwaitingConfirmation` (parâmetros cifrados, validade `ConfirmationTtlMinutes`); uma **pessoa** com `IntegrationExecuteWrite` confirma ou cancela. Contexto de sistema nunca confirma.
9. O conector chama; a resposta é mapeada em campos tipados e sanitizados; só isso sai.

Resultado **ambíguo** (`Unknown`): escrita sem idempotência comprovada que teve timeout, 5xx, redirecionamento, resposta grande demais ou ilegível depois de enviada; ou execução "em curso" além de `RunningLeaseSeconds`. **Nunca é reenviada**; a pessoa confere no sistema de origem.

## 4. Segurança

- **SSRF** (`OutboundDestinationPolicy` + `GuardedHttpTransport`): só `https` (http só por configuração do operador); sem credencial na URL; nomes internos (`localhost`, `.local`, `.internal`, rótulo único) recusados; DNS resolvido e **cada endereço conferido no momento da conexão** (sem janela para DNS rebinding); loopback, redes privadas, CGNAT, link-local, multicast, reservados, NAT64, 6to4 e IPv4 mapeado recusados; metadados de nuvem e link-local **nunca** liberáveis; **redirecionamentos nunca são seguidos**; sem proxy de sistema nem cookies; TLS validado.
- **Segredos**: AES-256-GCM com chaves próprias (`Capabilities:Secrets`); tenant, conexão, finalidade e versão como dado autenticado; nunca em API, log, auditoria ou erro. Sem chave configurada: gravar segredo → 503 e nenhuma conexão com autenticação executa.
- **Texto livre ≠ URL**: o chamador só escolhe uma chave do catálogo e informa **valores** de parâmetros declarados; caminho, consulta, corpo e cabeçalhos vêm do molde, com escape. Cabeçalhos reservados (`Authorization`, `Host`, `Cookie`...) nunca podem ser definidos por parâmetro.
- **Resposta do sistema externo é dado não confiável**: só os campos mapeados saem, com tamanho limitado e sem caracteres de controle.
- **Auditoria** (módulo Audit, sem valores): criar/alterar/segredo/habilitar/testar conexão; salvar/publicar/aposentar/ligar operação; executar, pedir, confirmar, cancelar e **negativas**. O registro de execuções guarda quem, origem, correlação, versão, resultado e duração.

## 5. Permissões

| Permissão | Para quê | Papéis (padrão) |
|---|---|---|
| `IntegrationRead` | ver conexões, operações e histórico (nunca o segredo) | Gerente, Admin da organização |
| `IntegrationManage` | cadastrar/configurar conexões, definir segredo, criar/publicar/aposentar operações, habilitar/desabilitar | Admin da organização |
| `IntegrationTest` | testar conexão e operações de **leitura** | Admin da organização |
| `IntegrationExecute` | executar **leitura** | Admin da organização |
| `IntegrationExecuteWrite` | executar e **confirmar** escrita | Admin da organização |

O escopo é a organização da conexão (conexão sem organização = do tenant: exige concessão de tenant). Objeto de outro tenant responde 404; função sem permissão responde 403.

## 6. API (`/capabilities`)

Conexões: `GET/POST /connections`, `GET/PUT /connections/{id}`, `PUT /connections/{id}/secret`, `POST /connections/{id}/{enable|disable|test}`.
Operações: `GET /definitions`, `POST /definitions` (salva o rascunho), `GET /definitions/{id}`, `GET /definitions/by-key/{key}/versions`, `POST /definitions/{id}/{publish|retire|enable|disable|test|validate}`.
Execuções: `GET /executions`, `GET /executions/stats`, `GET /executions/{id}`, `POST /executions` (como pessoa), `POST /executions/{id}/{confirm|cancel}`, `GET /catalog`.
Respostas: recusa de negócio vira o status que a descreve (400 validação, 403 origem, 404, 409 conflito/desabilitada, 429 limite); o desfecho de uma chamada que saiu é 200 com o resultado; escrita pendente é 202. Problemas de validação vêm em `problems` (só campo e regra).

## 7. Configuração e implantação

Variáveis (já em `deploy/app.env.example`; `gerar-segredos.sh` gera a chave e `preflight.sh` a confere):

| Chave | Efeito |
|---|---|
| `Capabilities__Secrets__ActiveKeyId` / `Capabilities__Secrets__Keys__<id>` | chave mestra (Base64, 32 bytes) dos segredos das conexões. **Guardar cópia fora da máquina**; perder = recadastrar as credenciais. Rotação: acrescentar uma chave nova, apontá-la como ativa e manter a antiga |
| `Capabilities__Options__Enabled` | interruptor global da **execução** (padrão `true`); a administração continua |
| `Capabilities__Options__Outbound__AllowHttp` / `AllowedPorts__N` / `AllowedPrivateCidrs__N` | política de destino, **decisão do operador**. Padrão: só público, https, porta 443. ERP em rede interna: liberar o CIDR exato |
| `Capabilities__Options__PerTenantPerMinute` (120), `PerConnectionPerMinute` (60), `PerConnectionMaxInFlight` (5), `WritesPerTenantPerMinute` (30), `TestsPerConnectionPerMinute` (10) | limites (contados no banco: valem entre instâncias) |
| `...ConfirmationTtlMinutes` (15), `RunningLeaseSeconds` (180), `MaxTimeoutSeconds` (30), `MaxResponseBytesCeiling` (1 MiB), `RetryBaseDelayMs`/`RetryMaxDelayMs` | prazos e tetos |

Banco: esquema `capabilities` (migrations `CapabilitiesInitial` e `CapabilitiesPublishedUniqueIndex`), aplicado por `migrate` (**depois de backup**, como os demais módulos). O backup do PostgreSQL já cobre o esquema. Só o **Api** usa o módulo hoje; o Worker não precisa de nada novo.

## 8. Exemplos

### 8.1 Cadastrar uma consulta (REST, leitura)

`POST /capabilities/connections` → `PUT /connections/{id}/secret` → `POST /connections/{id}/enable`, depois:

```json
POST /capabilities/definitions
{
  "connectionId": "…", "key": "pedidos.consultar", "name": "Consultar pedido",
  "description": "Consulta a situação de um pedido pelo número.",
  "effect": "Read", "allowedCallers": ["Operator", "Agent", "Chatbot"],
  "spec": {
    "method": "GET", "pathTemplate": "/pedidos/{numero}",
    "parameters": [ { "name": "numero", "type": "Integer", "location": "Path", "required": true, "min": 1, "max": 999999 } ],
    "response":   [ { "name": "situacao", "pointer": "/status", "type": "String", "required": true },
                    { "name": "total",    "pointer": "/total",  "type": "Number", "required": true } ],
    "idempotency": "Unspecified", "maxAttempts": 3
  }
}
```

Testar o rascunho (`POST /definitions/{id}/test` com `{"parameters":{"numero":1001}}`), publicar (`POST /definitions/{id}/publish`).

### 8.2 Cadastrar uma escrita (com idempotência e confirmação)

```json
{ "connectionId": "…", "key": "pedidos.cancelar", "name": "Cancelar pedido", "effect": "Write",
  "requiresConfirmation": true, "allowedCallers": ["Operator", "Agent"],
  "spec": { "method": "POST", "pathTemplate": "/pedidos/{numero}/cancelar",
    "parameters": [ { "name": "numero", "type": "Integer", "location": "Path", "required": true, "min": 1 },
                    { "name": "motivo", "type": "String", "location": "Body", "required": true, "maxLength": 100 } ],
    "response": [ { "name": "protocolo", "pointer": "/protocolo", "type": "String", "required": true } ],
    "idempotency": "Proven", "idempotencyHeader": "Idempotency-Key", "maxAttempts": 1 } }
```

`Proven` é uma **declaração auditada** de que o sistema externo trata a repetição com segurança (chave nativa, ou PUT/DELETE naturalmente idempotente). Sem ela (`None` ou `Reconcilable`), a escrita nunca é reenviada e a confirmação não pode ser dispensada.

### 8.3 Uso por um fluxo de chatbot ou pelo Agents (futuro; C#)

```csharp
// O código do fluxo (não o modelo) escolhe a operação do catálogo e preenche valores já extraídos da conversa.
var catalogo = await catalog.ListAsync(CapabilityCaller.Chatbot, ct);          // só o que o tenant liberou para o chatbot
var r = await executor.ExecuteAsync(new CapabilityInvocation(
    Key: "pedidos.consultar",
    Parameters: new Dictionary<string, JsonElement> { ["numero"] = JsonSerializer.SerializeToElement(1001) },
    Caller: CapabilityCaller.Chatbot,
    ServiceSessionId: sessao.Id), ct);

switch (r.Status)
{
    case CapabilityExecutionStatus.Succeeded:           // r.Output é DADO não confiável: nunca vira instrução de prompt
        await responder($"Seu pedido está {r.Output!.Value.GetProperty("situacao").GetString()}."); break;
    case CapabilityExecutionStatus.Rejected when r.Error!.Code == "validation-failed":
        await pedirDeNovo(r.Error.Problems!); break;     // nome do parâmetro e regra, nunca o valor
    case CapabilityExecutionStatus.AwaitingConfirmation: // escrita: um atendente confirma pela tela de Integrações
        await avisarAtendente(r.ExecutionId); break;
    case CapabilityExecutionStatus.Unknown:              // nunca repetir sozinho
        await transferirParaHumano(); break;
    default:
        await responderIndisponivel(r.Error!.Code); break;
}
```

Para **escrita**, quem chama gera a `IdempotencyKey` a partir da intenção (por exemplo, `"{sessaoId}:cancelar:{numero}"`): a mesma intenção, a mesma chave.

## 9. Testes

`tests/capabilities` (293 unitários, sem rede externa): política SSRF (tabelas de endereços), validador, mapeador, regras do molde, cifra, conector (transporte simulado) e **transporte real contra Kestrel em loopback** (DNS rebinding, redirecionamento, limites de bytes e de compressão, timeout, conexão recusada), motor, governança de escrita, administração e portão de função. `tests/IaBimeta.IntegrationTests/Capabilities` (29, PostgreSQL real + API real + ERP simulado em loopback): permissões por papel, fluxo completo, isolamento entre tenants e organizações, concorrência sobre os índices únicos (mesma chave ×8, confirmações ×6, publicações ×5), SSRF, retentativa, timeout, limites, auditoria e invariantes do banco. Frontend: 32 testes novos.

## 10. Limitações conhecidas

- Só o conector **REST/JSON** (sem SOAP, GraphQL, filas, arquivos).
- O cache do token OAuth é **por processo** (em memória; nunca persistido).
- Sem **disjuntor** persistido: uma conexão fora do ar continua recebendo tentativas até os limites; o limite protege, não isola.
- O resultado de uma escrita **não é guardado**: uma repetição devolve o desfecho, não o corpo (consulte o dado numa operação de leitura).
- Os limites por minuto contam linhas do banco (uma consulta por pedido): adequados à escala do piloto, não a milhares de chamadas por segundo.
- O assistente de criação de operações e os modelos prontos por sistema (LabNet, BImeta) **não existem**: a administração é técnica.
- Não há retenção própria das execuções (não guardam dado pessoal, só metadados).
- O Agents, o chatbot visual e o assistente interno **ainda não chamam** o motor: o contrato existe e está testado; a ligação exige aresta nova na matriz (ADR) e decisão de produto.

## 11. Requisitos para homologação

1. **PostgreSQL**: aplicar `migrate` (com backup) e conferir os índices `ux_capability_definitions_published`, `_draft`, `ux_capability_executions_idempotency`.
2. **Chave mestra** `Capabilities__Secrets__*` gerada, copiada fora da máquina e conferida pelo `preflight.sh`.
3. **Um ERP de homologação** (ou um simulador na VM) com `https` público ou CIDR liberado pelo operador; conferir: conexão (`Testar conexão`), uma leitura, e **uma escrita com a idempotência real do sistema** (provar que repetir a chave não duplica o efeito: sem essa prova, declarar `None`).
4. **TLS**: certificado válido do ERP (o transporte valida; certificado autoassinado é recusado).
5. **Limites**: conferir `MaxTimeoutSeconds`, tamanho de resposta e vazão com o ERP real.
6. **Revisão humana** das operações cadastradas (quem pode usar, confirmação, `Proven`) antes de liberar a `Agent`/`Chatbot`.
7. Decisão do proprietário sobre a ratificação do ADR 0035 e sobre a primeira ligação do Agents ao motor.

## 12. O que falta para integrar um ERP real

1. Levantar a API real (autenticação, paginação, erros, idempotência) e **cadastrar** a conexão e as operações (leitura primeiro).
2. Mapear identificadores (tenant/unidade ↔ filial do ERP) por parâmetros/cabeçalhos permitidos da conexão.
3. Para APIs fora de REST/JSON ou com autenticação não suportada (certificado de cliente, HMAC, token rotativo próprio): adaptador `ICapabilityConnector` dedicado.
4. Homologar a idempotência de cada escrita; só então liberar para o Agents/chatbot, com confirmação humana.
5. Ligar o motor ao Agents/Workflows (ADR da aresta) e ao assistente interno, com testes de isolamento (já há o modelo em `tests`).
6. Dados pessoais: decidir o que o ERP devolve, `Personal` nos campos e a política de minimização antes de qualquer envio a modelos.

## 13. Consulta do agente de IA (ADR 0036, fase 1)

O Agents pode, **só com a chave ligada**, consultar o Motor de Integração dentro do turno. Fase 1: apenas operações **públicas de leitura**.

**Como funciona.** Com a função ligada e ao menos uma operação elegível, o turno usa um esquema com a ação `lookup` (`operation_key` num enum montado por turno, `parameters_json` em texto). Se o modelo pedir uma consulta, o Agents a valida e executa pelo `ICapabilityExecutor` (origem `Agent`, escopo e sessão do código, `TurnId` como correlação) dentro de um orçamento (`LookupBudget`, padrão 8 s) e faz **uma segunda chamada** ao modelo com o resultado como **dado delimitado e não confiável**, sem o esquema `lookup` (não há laço; uma consulta por turno). A resposta cita as duas chamadas ao modelo.

**O que é oferecido ao modelo** (todas as condições): operação publicada, habilitada, em conexão habilitada, `Agent` entre as origens, efeito `Read`, `DataClass = Public` e conexão do tenant inteiro ou da **organização da sessão**. O motor filtra, o Agents confere de novo e o motor confere pela terceira vez ao executar. Escrita nunca é oferecida nem executada para o agente; operação `CustomerData` nunca chega a um modelo; a IA nunca classifica nada.

**O que a IA nunca decide:** URL, credencial, cabeçalho, método, conexão, tenant, organização, origem, efeito. Só uma chave do enum e valores de parâmetros declarados.

**Falha segura.** Qualquer falha (ERP indisponível, timeout, orçamento estourado, limite, operação desligada, resposta inválida, parâmetros inválidos, escopo recusado) vira **handoff `integration_unavailable`** (sem segunda chamada ao modelo): o aviso aprovado do tenant sai e o histórico e o contexto seguem com o ContactCenter. A falha em montar o catálogo não derruba o turno: ele segue sem consulta.

**Auditoria e métricas.** Entrada `agents.integration-lookup.<código>` (`ok`, `unavailable`, `timeout`, `budget-exceeded`, `rate-limited`...) com o `TurnId` como recurso, sem parâmetros nem respostas; o registro de execuções do motor traz origem `Agent`, sessão e `TurnId`. Métricas no `Meter` `IaBimeta.Agents` (`agents.integration_lookup.attempts` por `outcome` e `agents.integration_lookup.duration_ms`), **por processo** (a evidência durável é a trilha e o registro de execuções).

**Configuração:** `Agents__Options__IntegrationLookupsEnabled` (padrão `false`), `LookupBudget` (1 a 30 s), `LookupMaxPerTurn` (só 1). Exige o módulo no Worker (já composto), as chaves `Capabilities__Secrets` e a política de saída no ambiente do Worker, e `TurnMaxDuration` maior que duas chamadas ao modelo mais o orçamento.

**Novos requisitos de cadastro:** a **classe do dado** (`Public` ou `CustomerData`) é obrigatória quando agente, fluxo ou chatbot podem usar a operação (a tela exige a escolha); o padrão é `CustomerData`; operação `Public` não pode ter campo da resposta marcado como pessoal; o agente não pode ser origem de escrita; `Source = Session` (fase 2) é recusado.

**Fase 2 (não implementada):** dados do cliente exigem identidade autenticada do contato, parâmetros injetados pela sessão (`CapabilityParameterSource.Session`), minimização e base legal, com ADR próprio.

**Limitações:** uma consulta por turno; sem *tool calling*; a segunda chamada ao modelo aumenta custo e latência dos turnos com consulta; as descrições das operações são escritas pelo administrador e entram no prompt; métricas só em memória; o aviso ao cliente é o aviso de transferência já aprovado do tenant (não há texto específico para `integration_unavailable`); limite do motor de 5 chamadas simultâneas por conexão faz o excedente transferir ao atendente.

**Homologação:** (1) ligar em um tenant de teste com uma operação pública real do ERP e conferir preço/prazo ponta a ponta no WhatsApp de homologação; (2) derrubar o ERP e conferir o handoff e o aviso; (3) conferir no ledger do AiGateway as duas chamadas por turno e o custo; (4) revisar a descrição das operações; (5) validar com o modelo real que ele só pede consulta com os parâmetros obrigatórios (o prompt orienta, mas isso só a homologação prova).
