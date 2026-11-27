# Primeiro teste de IA pelo simulador (`tools/dev/`)

Scripts de **desenvolvimento local** para exercitar o fluxo *mensagem do cliente → Agents → OpenAI → resposta (ou atendimento humano)* pelo simulador, com a chave **BYOK** do tenant cifrada no banco, e ver o consumo (tokens, custo **estimado**, resultado) no ledger.

> **Nada aqui faz chamada paga sozinho.** Subir o Api e o Worker, preparar o tenant e cadastrar a chave **não chamam** a OpenAI. Só dois scripts falam com a OpenAI, e só quando você os roda: `Test-ModeloDaOpenAI.ps1` (consulta de metadados, sem tokens) e `Invoke-TesteDeIa.ps1` (**uma chamada paga pequena** por execução: o teto de saída é de 600 tokens).

Todos os scripts são PowerShell 5.1, somente ASCII, e **rodam no seu terminal** (eles usam `dotnet user-secrets`, que grava no seu perfil).

## Como a chave fica protegida

| Segredo | Onde vive | Nunca |
|---|---|---|
| Chave da OpenAI (BYOK do tenant) | Cifrada (AES-256-GCM) na tabela de credenciais do `AiGateway`; entra pelo `PUT /simulator/ai/credential`, que só devolve a *impressão digital*. | em arquivo, variável de ambiente persistente, log, resposta HTTP, auditoria ou commit |
| Chave mestra que cifra as credenciais | `dotnet user-secrets` do Api e do Worker (mesmo `UserSecretsId`), gerada por `Set-ConfiguracaoDeIa.ps1` (32 bytes aleatórios, nunca exibida). | em arquivo versionado |
| String de conexão | `dotnet user-secrets` (já existente) | no repositório |

`Set-ChaveDaOpenAI.ps1` pede a chave **sem eco** (`Read-Host -AsSecureString`), manda no corpo de **um** `PUT` para o Api local e descarta a variável.

## Antes de começar

1. **PostgreSQL de desenvolvimento no ar** (porta 5433) e a string de conexão nos user-secrets do Api (`ConnectionStrings:Tenancy`).
2. **Atenção ao banco:** ao subir em Development o Api **aplica as migrations pendentes** no banco configurado, inclusive as duas novas, `AiGatewayInitial` (esquema `ai_gateway`) e `AgentsInitial` (esquema `agents`). Faça o backup que quiser antes, por exemplo:

   ```powershell
   C:\Projetos\ferramentas\postgresql-17.11\bin\pg_dump.exe -h 127.0.0.1 -p 5433 -U <usuario> -Fc -f C:\Projetos\backups\iabimeta_dev-pre-ia.dump iabimeta_dev
   ```

3. **O Worker processa o backlog do outbox de todos os tenants do banco.** Por isso a IA de demonstração usa um tenant **próprio e sem histórico**, `demo-ia` (criado pelo perfil `simulador-ia`): conversas antigas de `demo-a` ou `demo-b` não têm configuração nem chave de IA, então nunca viram chamada paga.

## Passo a passo

### 1. Confirmar o modelo da sua conta *(chamada real de metadados — só depois de autorizado)*

Não presuma o identificador do modelo. Confirme que ele existe na **sua** conta:

```powershell
.\tools\dev\Test-ModeloDaOpenAI.ps1 -ModeloId <id-do-modelo>
```

Pede a chave sem eco, faz `GET /v1/models/<id>` (não gera texto nem consome tokens) e diz `DISPONIVEL`, `NAO ENCONTRADO` (e lista os ids da conta que começam com `gpt`) ou `CHAVE RECUSADA`.

### 2. Chave mestra e modelo (uma vez)

```powershell
.\tools\dev\Set-ConfiguracaoDeIa.ps1 -ModeloId <id-do-modelo> -PrecoEntradaPorMilhao <usd> -PrecoSaidaPorMilhao <usd>
```

Grava nos user-secrets: a chave mestra (só se ainda não existir), a entrada do modelo no catálogo (`AiGateway:Catalog:Models:50`) com os **preços que você informa** (o custo do ledger é **estimado** por esta tabela; não é fatura) e `Tenancy:Development:Ai:ModelId`. Opções úteis: `-PrecoEntradaEmCachePorMilhao`, `-JanelaDeContexto`, `-TetoDeSaidaDoModelo`, `-Moeda`.

`-EsforcoDeRaciocinio`: o padrão (`padrao`) **não envia** o campo `reasoning` — serve a qualquer modelo. Use `none`, `low`... só se o modelo aceitar o valor (um modelo que rejeita o parâmetro responde 400 e o atendimento vai para o humano, sem custo).

### 3. Subir o simulador

```powershell
.\tools\dev\Start-SimuladorDeIa.ps1
```

Abre o **Api** (perfil `simulador-ia`: Development, simulador ligado, tenant `demo-ia` e a IA de demonstração preparada) e, depois que ele responde, o **Worker** (relay do outbox + executor de turnos de IA). Cada um na sua janela; feche as janelas para parar. À mão, é o mesmo que:

```powershell
dotnet run --project backend\host\IaBimeta.Api --launch-profile simulador-ia      # janela 1
$env:DOTNET_ENVIRONMENT = 'Development'; dotnet run --project backend\host\IaBimeta.Worker   # janela 2, depois que o Api subiu
```

Na partida o Api prepara, de forma idempotente, o tenant `demo-ia`: organização, fila de atendimento humano com a regra de roteamento que a escolhe, provedor/modelo e uma definição de agente **publicada** (instruções de demonstração, um item de FAQ e o aviso de transferência). Confira em `GET http://localhost:5155/simulator/ai/status` (cabeçalho `X-Dev-Tenant: demo-ia`).

Na janela do **Worker**, procure a linha `Configuração de IA deste Worker: ... chave mestra ativa: sim; limites padrão completos: sim` — se algo aparecer como `NÃO`, a IA vai terminar em "não configurada".

### 4. Cadastrar a chave da OpenAI

```powershell
.\tools\dev\Set-ChaveDaOpenAI.ps1
```

Resposta esperada: `chave presente: True (impressao digital ...)`. `503` = chave mestra ausente (passo 2, e reinicie).

### 5. A primeira chamada real *(paga — só depois de autorizado)*

```powershell
.\tools\dev\Invoke-TesteDeIa.ps1 -Mensagem 'Ola, tudo bem?'
```

Confere que o tenant está pronto, envia a mensagem de um cliente simulado, espera a resposta e mostra: o que a IA respondeu (ou a transferência ao humano), a linha do **ledger** (tokens de entrada com cache, saída, custo estimado, resultado, latência, id da requisição na OpenAI) e o estado do **turno**. Para provar que a IA para depois que o humano assume (faz só a chamada da primeira mensagem):

```powershell
.\tools\dev\Invoke-TesteDeIa.ps1 -VerificarHumanoAssume
```

### 6. Encerrar

```powershell
.\tools\dev\Set-ChaveDaOpenAI.ps1 -Revogar     # apaga o texto cifrado da chave
```

E feche as janelas do Api e do Worker.

## Rotas do simulador (Development, cabeçalho `X-Dev-Tenant`)

| Rota | Para quê |
|---|---|
| `PUT /simulator/ai/credential` `{apiKey}` | cadastra/troca a chave (cifrada); devolve só a situação |
| `DELETE /simulator/ai/credential` | revoga a chave |
| `GET /simulator/ai/status` | IA do tenant, organização, fila, agente publicado, catálogo |
| `GET /simulator/ai/requests` | ledger: uma linha por chamada ao modelo (sem prompt, sem resposta, sem chave) |
| `GET /simulator/agents/turns` | turnos de IA: estado, tentativas, motivo da transferência |
| `GET /simulator/sessions/{id}` | a sessão e o histórico (onde a resposta da IA aparece: ainda não há envio real pelo canal) |
| `POST /simulator/inbound` | mensagem de um cliente simulado |
| `POST /simulator/handling/transition` | o humano assume (`to: HumanAssisted`) |

## Se algo falhar

| Sintoma | Causa provável |
|---|---|
| `TEMPO ESGOTADO` no teste | o Worker não está no ar, ou o banco está inacessível (veja a janela dele) |
| Transferido ao humano, turno `Dead (ai-not-configured)` | o Worker não enxerga o catálogo, os limites ou a chave mestra (linha `Configuração de IA deste Worker`), ou o modelo do tenant não está no catálogo |
| `ai-invalid-credentials` | a OpenAI recusou a chave (401/403): cadastre outra; o ledger registra `NoneIncurred` (sem custo) |
| `ai-quota-or-billing` | cota ou cobrança da conta esgotadas |
| `ai-invalid-request` (400) | o modelo rejeitou o pedido (por exemplo, o esforço de raciocínio): rode `Set-ConfiguracaoDeIa.ps1` com outro `-EsforcoDeRaciocinio` |
| 5xx / tempo esgotado do provedor | falha transitória: o turno tenta de novo (até 3 vezes, com recuo); o ledger marca a chamada como **Indeterminate** (cobra a reserva até reconciliar) |

## Como isto é provado sem a OpenAI

`tests/IaBimeta.IntegrationTests/Api/SimuladorDeIaTests.cs` sobe o **Api real, o Worker real**, PostgreSQL, o AiGateway com a chave cifrada e o **adaptador real da OpenAI**, trocando só o provedor por um servidor HTTP local (`127.0.0.1`) com chave canário. Cobre: a preparação do tenant (idempotente), o cadastro da chave sem exposição (resposta, log, banco, ledger), a resposta com tokens e custo no ledger, a decisão de transferir, a IA calada depois que o humano assume (inclusive no meio da chamada ao modelo) e as falhas (sem chave, chave recusada, 5xx seguido de sucesso, sem chave mestra).

## Anthropic (Etapa 03)

Validação do adaptador da Anthropic **sem chamada paga**: `Start-AnthropicSimulada.ps1` (porta 5200, Messages API simulada), `Novo-TenantDeAnthropicDeDemonstracao.ps1` (empresa sintética `demo-anthropic`, chave sintética) e `Set-ChaveDaAnthropic.ps1` (só para a validação real, **mediante autorização**; pede a chave sem eco). Procedimento completo em `docs/operations/ETAPA-03-ANTHROPIC.md`; roteiro da API real em `docs/operations/ETAPA-03-ANTHROPIC-API-REAL.md`.

## Ollama (Etapa 04)

Validação do adaptador conversacional **sem instalar o Ollama nem baixar modelo**: `Start-OllamaSimulado.ps1` (porta 5201: `/api/version`, `/api/tags`, `/api/show`, `/api/chat` simulados), `Novo-TenantDeOllamaDeDemonstracao.ps1` (empresa sintética `demo-ollama`, **sem chave**) e `Test-ServidorOllama.ps1` (sondagem somente leitura de um servidor real ou simulado). O Api, o Worker e o `provision` precisam de `AiGateway__Providers__Ollama__BaseAddress=http://127.0.0.1:5201/` e `AiGateway__Catalog__Models__3__Status=Active` (o modelo vem desativado no catálogo). Procedimento completo e limites em `docs/operations/ETAPA-04-OLLAMA.md`.
