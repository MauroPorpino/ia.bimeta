# ADR 0036 — Agents consulta operações PÚBLICAS do Motor de Integração: esquema `lookup`, escopo de organização e falha segura

- **Status:** **Aceito** (decisões do proprietário em 12/11/2026; ratificado em 12/11/2026 depois de conferir que a implementação corresponde ao decidido; ver "Conferência da ratificação" ao final). Fase 1 apenas; a fase 2 (dados do cliente) está adiada e exigirá ADR próprio e autenticação do cliente.
- **Data:** 12/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0017 (Agents; seção C.5, lista FECHADA de ações do modelo), 0035 (Motor de Integração), 0007 (ciclo de vida dos turnos), `CLAUDE.md` (o modelo interpreta e sugere; o Capability Execution Engine valida e executa).
- **Altera:** a lista fechada de ações do modelo do ADR 0017 C.5 (de `reply` e `handoff` para `reply`, `handoff` e `lookup`), **somente** quando a função está ligada e há operações elegíveis. **Preserva:** o esquema e o comportamento atuais com a função desligada (padrão), as cercas do Conversations, a matriz de dependências (a aresta `Agents → Capabilities` já constava; só `Capabilities.Contracts` é usado) e o contrato aprovado do ContactCenter.

## Contexto

O agente hoje transfere qualquer pergunta que dependa de dado do sistema da empresa (preço, horário). O Motor de Integração (ADR 0035) já executa operações cadastradas com validação, limites, idempotência e auditoria, mas nenhum chamador de IA o usa. É preciso permitir que o agente consulte **dado público** (não pessoal) sem lhe dar poder sobre URLs, credenciais, tenant, organização ou escrita.

## Decisão

### 1. Esquema estruturado `lookup`, sem *tool calling*
Com a função ligada e ao menos uma operação elegível, o turno usa um esquema de saída com cinco campos obrigatórios: `action` (`reply`, `handoff`, `lookup`), `reply_text`, `handoff_reason`, `operation_key` (enum montado **por turno** com as chaves elegíveis e `none`) e `parameters_json` (texto JSON de um objeto de até 2 KB). Só o mínimo comum aos dois provedores (todos os campos obrigatórios, sem `anyOf` na raiz). O `DecisionParser` continua falhando fechado: ação `lookup` com chave fora do conjunto elegível, parâmetros que não são um objeto JSON ou campo extra é saída inválida (conta como falha da tentativa, nunca executa).

### 2. Dois passos, no mesmo turno
`lookup` → o Agents valida e executa pelo `ICapabilityExecutor` → **segunda chamada** ao modelo com o resultado e o esquema SEM `lookup` (só `reply` ou `handoff`; não há laço). Limite inicial: **uma consulta por turno** (`Agents:Options:LookupMaxPerTurn`, padrão 1). A resposta é registrada com as duas `AiRequestId`. As cercas do Conversations (turno ativo, época, `StateVersion`, `basedOnInboundSeq`) valem como sempre: uma consulta é leitura, então um turno substituído descarta o resultado sem efeito algum.

### 3. O que a IA NUNCA decide
URL, credencial, cabeçalho, método, conexão, tenant, organização, origem do chamador e efeito. O modelo escolhe uma **chave** de um enum e informa **valores** de parâmetros declarados; o molde da operação monta a chamada (ADR 0035). `Caller = Agent`, `ServiceSessionId`, `CorrelationId` (o `TurnId`) e o escopo vêm do código do Agents.

### 4. Quem é oferecido ao modelo (todas as condições)
Operação **publicada, habilitada, em conexão habilitada, `Agent` entre as origens autorizadas, efeito `Read`, `DataClass = Public`** e do escopo de organização do atendimento. O Agents confere isso ao montar o catálogo e **de novo** antes de executar; o motor confere pela terceira vez (defesa em profundidade). A classe `Public` é **declarada pelo administrador** e é obrigatória no cadastro quando `Agent`, `Chatbot` ou `Workflow` estão entre as origens; a IA nunca a atribui. O padrão de toda operação é `CustomerData`.

### 5. Escrita proibida
Operação de escrita nunca é oferecida nem executada para `Agent`: recusada no catálogo, no Agents e no motor, e o cadastro não aceita `Agent` entre as origens de uma escrita.

### 6. Escopo de organização obrigatório
`TurnSnapshot.OrganizationId` (campo aditivo; vem da sessão autorizada) é o escopo. `CapabilityInvocation.ScopeOrganizationId` é **obrigatório para chamadores não humanos**; ausente, a execução é recusada (`scope-required`). A conexão precisa ser do tenant inteiro (sem organização) ou **da mesma organização** da sessão (`scope-denied`). O catálogo para chamador não humano sem escopo é vazio.

### 7. Resultado do ERP é dado não confiável
Entra no prompt da segunda chamada como bloco delimitado de **dado**, depois de `instruções` e `regras da plataforma`, com a regra explícita de que ele não pode alterar instruções nem autorizações; só os campos mapeados existem (o resto nunca chega ao Agents). Nenhum campo marcado `Personal` e nenhuma operação `CustomerData` chegam ao modelo na fase 1.

### 8. Falha fecha em handoff determinístico
Qualquer falha da consulta (indisponível, timeout, orçamento estourado, limite, desabilitada, resposta inválida, parâmetros inválidos, escopo recusado, exceção) vira **handoff com motivo `integration_unavailable`**, sem segunda chamada ao modelo: o aviso de transferência aprovado do tenant sai pelo fluxo de handoff existente (o histórico e o contexto do atendimento seguem com o ContactCenter, que já os recebe). O turno nunca morre por falha do ERP. **Orçamento** da consulta inteira: `Agents:Options:LookupBudget` (padrão 8 s, 1 a 30 s).

### 9. Auditoria e métricas próprias, sem dado pessoal
Cada consulta (sucesso ou falha, inclusive a que o Agents recusou antes do motor) gera uma entrada de auditoria `agents.integration-lookup.<código>` com o `TurnId` como recurso (a trilha não tem campo livre) e incrementa contadores do `Meter` `IaBimeta.Agents` (`agents.integration_lookup.attempts` com a etiqueta `outcome`, e um histograma de duração). O motor registra a execução com origem `Agent`, a sessão e o `TurnId` (`CorrelationId`). Nenhum parâmetro, resposta ou texto de cliente vai à trilha.

### 10. Chave desligada por padrão e preparação da fase 2
`Agents:Options:IntegrationLookupsEnabled = false`. O contrato reserva `CapabilityParameterSource.Session` (parâmetro injetado a partir da identidade autenticada do contato) e a classe `CustomerData`; **o motor recusa** qualquer operação que use `Session` enquanto a fase 2 não existir. A fase 2 exigirá ADR próprio (identidade autenticada, minimização, base legal).

## Consequências
- O agente passa a responder preço, horário e catálogo com dado da empresa, sem tool calling e sem mudar os adaptadores de IA. Custo: uma segunda chamada ao modelo nos turnos com consulta.
- O Worker passa a compor o módulo Capabilities (chave de cifra e política de saída no ambiente do Worker, que usa o mesmo `app.env`).
- Riscos conhecidos: uma descrição de operação mal escrita pode induzir consultas inúteis (o administrador é responsável); o dado `Public` mal classificado vaza a quem perguntar (por isso o padrão é `CustomerData` e a classificação é manual e auditada); as métricas do `Meter` são por processo (a evidência durável é a trilha e o registro de execuções).
- Fora de escopo: dados do cliente, escrita, mais de uma consulta por turno, *tool calling*, chatbot visual (usará o mesmo motor com `Caller = Workflow`).

## Registro da implementação (12/11/2026)

- Fatias entregues: contratos aditivos (ADR, `DataClass`, `ScopeOrganizationId`, `CorrelationId`, `Source`, `TurnSnapshot.OrganizationId`), regras e migration no motor, integração no Agents (esquema `lookup`, parser, prompt, serviço, falha segura), composição no Worker com testes ponta a ponta e a tela de administração com a classe do dado.
- **Ajuste em relação ao texto acima:** `LookupMaxPerTurn` existe como opção mas só aceita 1 (a partida recusa outro valor). Com a consulta ligada, `TurnMaxDuration` precisa passar `2 × ModelCallTimeout + LookupBudget` (validado na partida).
- **Decisão adicional:** as origens não humanas (`Agent`, `Workflow`, `Chatbot`) só executam dado `Public` no motor; o agente também não executa escrita; o catálogo para elas é vazio sem escopo.
- **Linhas existentes:** a migration marca toda operação já cadastrada como `CustomerData` (padrão seguro).

## Conferência da ratificação (12/11/2026)

Conferido contra a implementação, ponto a ponto: (1) esquema `lookup` de cinco campos com o enum por turno e o parser fechado; (2) duas chamadas, sem laço, resposta citando as duas `AiRequestId`; (3) o modelo só escolhe chave e valores; (4) oferta só de operação publicada, habilitada, em conexão habilitada, com `Agent` autorizado, de leitura, `Public` e do escopo, conferida no motor, no Agents e de novo ao executar; (5) escrita recusada no cadastro, no catálogo, no Agents e no motor; (6) escopo obrigatório para origem não humana (`scope-required` e `scope-denied`); (7) resultado como dado delimitado, sem campo pessoal; (8) falha em handoff `integration_unavailable` com aviso específico configurável por tenant e retorno ao aviso de transferência; (9) auditoria `agents.integration-lookup.*` e `Meter`; (10) função desligada por padrão e `Source = Session` recusado. Divergência já registrada: `LookupMaxPerTurn` só aceita 1. Aviso específico por tenant adicionado em 12/11/2026 (coluna `integration_unavailable_notice_text`, provisionamento `ai.integrationUnavailableNotice`).
