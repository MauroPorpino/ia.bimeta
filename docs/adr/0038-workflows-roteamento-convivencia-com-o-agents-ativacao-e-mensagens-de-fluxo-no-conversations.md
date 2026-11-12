# ADR 0038 — Workflows: roteamento, convivência com o Agents, ativação e mensagens de fluxo no Conversations

- **Status:** **Aceito** (decisões do proprietário em 12/11/2026).
- **Data:** 12/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0037 (Workflows), 0007 e `CONVERSATION-LIFECYCLE.md` (turnos e cercas), 0036 (Agents), `BOUNDARIES.md` §6 (modo de atendimento) e §12.
- **Altera (aditivamente):** o contrato `Conversations.Contracts` (novo comando de mensagem de fluxo e novo autor `Bot`). **Preserva:** o comando `RegisterAiReply` e todas as suas regras (resposta defasada de IA continua sempre rejeitada, sem configuração), o modo de atendimento (só o Conversations o muda, por `TransitionHandlingMode`) e o comportamento atual com a função desligada.

## Decisão

### 1. Um único dono por sessão: o roteador
A pergunta "este atendimento é do Fluxo ou do Agente?" tem **uma resposta durável e única**: `workflows.routing_decisions`, índice único por `(tenant, session_id)`, primeiro a gravar vence. O `Agents` consulta a porta `IAutomationRouter` (de `Workflows.Contracts`; a aresta `Agents → Workflows` já existe) antes de abrir um turno e **ignora** sessões decididas para o fluxo; o consumidor do `Workflows` ignora as decididas para o Agente. Assim Workflows e Agents **nunca respondem à mesma sessão**. Com a função desligada o roteador responde "Agente" **sem gravar nada** e sem consulta ao banco (caminho idêntico ao atual).

A decisão vale para a sessão (não muda no meio). Termina quando o fluxo termina (fim, transferência, entrega ao Agents, cancelamento): `Delegate` regrava a decisão para o Agente por transição condicional (`Flow → Agent`), nunca o inverso na mesma instância.

### 2. Ativação
`FlowActivation`: definição publicada, escopo por **organização, unidade e canal** (todos opcionais; o mais específico vence; empate por prioridade), `Enabled`, `RolloutPercent` (0–100) **determinístico** (hash estável do `ContactId`/sessão com o id da ativação: o mesmo cliente sempre cai do mesmo lado) e `OnFailure = Handoff | Agent`. Nasce desligada. Chave global `Workflows:Options:Enabled = false` (padrão). Desativar uma ativação ou a chave não afeta instâncias em andamento além de **encerrá-las com segurança** conforme `OnFailure`; sessões sem decisão seguem com o Agents.

### 3. Mensagens de fluxo no Conversations (comando próprio)
Novo comando aditivo `IFlowCommands.RegisterFlowMessageAsync` e novo autor `MessageAuthor.Bot` (mensagem de fluxo, distinta de IA e de humano na interface e na auditoria). O comando preserva:
- **turno, época e `StateVersion`:** o fluxo executa dentro de um turno aberto por `BeginTurn` (mesma disciplina do ADR 0007); turno substituído, época antiga e versão de estado alterada (por exemplo, atendente assumiu) são recusados em definitivo;
- **modo de atendimento:** só aceita com o modo `AiAutonomous`; `HumanAssisted` recusa;
- **idempotência:** `EffectId` único por `(tenant, sessão)`; repetição devolve a mesma mensagem (`AlreadyRegistered`), nunca duplica;
- **resposta defasada:** a mensagem carrega `BasedOnInboundSeq` do turno e é **recusada se chegou entrada do cliente depois** (`StaleInput`), como na IA. A diferença é a recuperação: o conteúdo de um fluxo não deriva do texto da entrada nova, então o executor **reabre o turno (nova leitura do cursor) e tenta o mesmo efeito de novo, com o mesmo `EffectId`**, no máximo `Workflows:Options:StaleRetries` vezes; a ordem das mensagens é preservada porque a lane da sessão é serial. Esgotadas as tentativas o efeito fica `Superseded` (nada enviado, nada duplicado) e a instância segue a política `OnFailure`. Nenhuma chave de configuração relaxa as cercas.

Mensagens de fluxo contam como cobertura lógica (`AnsweredThroughSeq`) como as de IA, e vão ao Messaging/Channels pelo mesmo outbox.

### 4. Mudança de modo e transferência sem perda nem duplicação
- O consumidor de `HandlingModeChangedV1` cancela a instância ativa (motivo `human-takeover`) e seus efeitos pendentes ficam `Superseded`.
- A transferência usa `RequestHandoff` com chave `instância:nó` (idempotente) e leva o resumo das variáveis coletadas (conferido: classe e permissão; o dado pessoal só vai ao atendente autorizado, pelo ContactCenter).
- O nó `ai` (v1) grava `Delegate`: o roteador passa a "Agente" e o Agents recebe a sessão pelo fluxo normal de entrada (o Agents lê o histórico do Conversations, que já contém as mensagens do fluxo). A retomada de uma instância após queda reexecuta só efeitos `Pending` (idempotentes).

### 5. Migração gradual
Chave desligada → ativação em uma unidade/canal de teste → ampliação por canal, unidade ou percentual → `OnFailure` sempre volta ao Agente ou transfere. Um modelo de fluxo equivalente ao comportamento de hoje (`knowledge → (not_found) → ai`) acompanha o produto. Sessões em andamento nunca mudam de dono.

## Consequências
- O contrato central do Conversations cresce (um comando, um autor, duas tabelas/colunas de idempotência), de forma aditiva.
- O roteador é ponto único de decisão e precisa de índice único e teste de concorrência (duas lanes disputando a mesma sessão).
- Fora de escopo: IA com retorno ao fluxo (os contratos reservam `AwaitingAgent`/`DelegateAndResume`), Instagram, voz, escrita e dado privado.
