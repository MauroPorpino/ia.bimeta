# Chatbot Visual (módulo `Workflows`)

Documento operacional do Chatbot Visual: fluxos de atendimento desenhados pela própria empresa (mensagens, perguntas, condições, consulta ao conhecimento, consulta a sistemas, transferência e entrega ao agente de IA), executados de forma **determinística** e **independente de canal**. Decisões: [ADR 0037](../adr/0037-chatbot-visual-modulo-workflows-grafo-versionado-interpretador-deterministico-e-efeitos-duraveis.md) e [ADR 0038](../adr/0038-workflows-roteamento-convivencia-com-o-agents-ativacao-e-mensagens-de-fluxo-no-conversations.md).

> **O chatbot visual nasce DESLIGADO.** Sem `Workflows__Options__Enabled=true` no Worker, nenhum fluxo atende ninguém e o comportamento é exatamente o de antes (a administração, a validação e a simulação funcionam sempre).

## 1. O que é

- Um **editor visual** (React Flow) na área **Fluxos** da aplicação web, para administradores e gerentes: nós, conexões, variáveis, validação pelo servidor e **simulador** sem nenhuma chamada real.
- Um **interpretador determinístico** (`FlowInterpreter`): `(grafo, estado, entrada, instante) → (estado', efeitos)`. Função pura: sem I/O, sem relógio, sem aleatoriedade e sem saber o canal.
- Um **runtime** (no Worker): instâncias por conversa, efeitos duráveis com identificador determinístico, recuperação após queda, prazos de pergunta.
- **Versões imutáveis**: publicar congela a versão (também por gatilho do banco); editar depois cria uma versão nova. Uma conversa termina na versão em que começou.
- **Um único dono por sessão**: o roteador decide, uma vez, se a conversa é de um fluxo ou do agente de IA. Os dois nunca respondem juntos.

## 2. Módulos e fronteiras

| Módulo | Papel |
|---|---|
| `Workflows` (Contracts, Core, Infrastructure, Presentation) | grafo, validador, interpretador, simulador, versões, ativações, roteador, runtime, administração e API |
| `Conversations` | comando próprio de **mensagem de fluxo** (`IFlowCommands`, autor `Bot`) com as mesmas cercas da resposta de IA; evento `FlowReleasedToAgentV1` |
| `Agents` | consulta o roteador antes de abrir turno; implementa a porta de conhecimento (`IFlowKnowledgeLookup`) sobre o FAQ aprovado e o índice semântico; reavalia a entrada quando o fluxo devolve a sessão |
| `Capabilities` | o nó de integração usa `ICapabilityExecutor` com `Caller = Workflow` (só leitura, `DataClass = Public`, autorizada a `Workflow`, escopo da organização da sessão) |
| `ContactCenter` | a transferência (`RequestHandoff`) chega pelo adaptador do host (`FlowHandoffAdapter`), idempotente |
| Host (Api / Worker) | composição; o Worker roda o executor e os consumidores de evento |

Matriz de dependências: `Workflows` usa Conversations, Capabilities e Audit (arestas que a matriz já permitia); `Agents` usa `Workflows.Contracts`. O Workflows **não** referencia Agents, Knowledge, AiGateway nem ContactCenter (portas em `Workflows.Contracts`; testes de arquitetura garantem).

## 3. O fluxo (grafo)

Documento JSON `{ schemaVersion, start, nodes[], edges[], variables[] }`, o **mesmo** que o editor grava e o servidor valida de forma estrita (campo desconhecido, documento grande ou profundo demais e enumeração inválida são recusados).

| Nó | O que faz | Saídas |
|---|---|---|
| `message` | envia um texto (variáveis `{{nome}}`) | `next` |
| `question` | pergunta e valida (texto, escolha, número, e-mail, telefone, data, padrão), com tentativas e prazo opcionais | `answered`, `invalid`, `timeout` (se houver prazo) |
| `condition` | ramos **declarativos** (igual, diferente, contém, vazio, maior, menor, lista, dentro do horário), sem script | um por ramo e `else` |
| `knowledge` | resposta aprovada do FAQ/Knowledge da empresa, sem modelo de geração | `found`, `not_found` |
| `integration` | operação **pública de leitura** do catálogo do Capabilities | `success`, `failure` |
| `ai` | entrega a conversa ao agente de IA e **encerra** o fluxo | — |
| `handoff` | transfere para um atendente (aviso opcional) | — |
| `end` | encerra (texto opcional) | — |

Validação na publicação (zero problemas): um início, nós alcançáveis, todas as saídas conectadas, ciclo só com pergunta no meio, variáveis declaradas, resposta livre do cliente em variável **pessoal**, operações existentes e elegíveis, limites de tamanho (nós, conexões, texto, opções, variáveis).

**Canal:** o motor emite texto e opções abstratos. Em cada passo as mensagens viram **uma** mensagem; as opções viram uma **lista numerada** (`1) Preço`), e a pergunta de escolha aceita o número, o rótulo ou o id. Botões interativos de cada canal ficam para os adaptadores (limites do canal são configuração, a confirmar na documentação oficial antes de depender deles).

## 4. Execução e recuperação

1. O evento `InboundMessageRegisteredV1` chega ao `Workflows` (inbox por `EventId`): a **primeira** mensagem da sessão pode escolher um fluxo (ativação casando organização, unidade e canal; parcela determinística). A decisão e a instância são gravadas juntas (chave primária por tenant e sessão).
2. O **executor** (Worker) reivindica instâncias devidas (lease com token e revisão otimista, teto por tenant) e roda cada uma: lê a próxima mensagem do cliente, chama o interpretador e grava, **na mesma transação**, o estado novo, os efeitos pendentes (`passo:índice`) e o histórico. Só depois realiza os efeitos, um a um.
3. **Mensagem**: junta as mensagens do passo, abre um turno (`BeginTurn`) e registra com `RegisterFlowMessage` (cercas de turno, época, estado, modo e entrada; idempotente por turno determinístico). Entrada defasada: reabre o turno e repete o mesmo efeito (`StaleRetries`); esgotado, nada é enviado e vale a política de falha.
4. **Integração**: `ICapabilityExecutor` com orçamento (`CapabilityBudget`); os campos escalares do resultado viram variáveis `prefixo.campo`. Resultado **ambíguo** (queda no meio da chamada) segue a porta de falha e **nunca é reenviado**.
5. **Transferência**: confere antes (`CheckOnly`) para não avisar o cliente de algo que as regras recusariam; depois pede com chave idempotente. Recusada, vale a política de falha da ativação.
6. **Atendente assume / sessão encerra**: o consumidor do evento pede o cancelamento; o executor cancela com segurança (efeitos abertos viram "substituídos", nada é enviado).
7. **Fim da instância**: a sessão passa ao agente (decisão regravada para "agente", definitivamente). Em `ai` (entrega) o evento `FlowReleasedToAgentV1` faz o Agents responder à última pergunta uma vez.

Recuperação: uma queda entre a gravação do efeito e a realização é recuperada pelo executor sem perda nem duplicidade (mensagem e transferência são idempotentes; consulta externa em andamento vira "ambíguo").

## 5. Ativação e migração gradual

- Área **Fluxos → Ativações**: por organização (obrigatória), unidade e canal (opcionais); o mais específico vence, depois a maior prioridade; **parcela** 0–100 % determinística (o mesmo cliente cai sempre do mesmo lado); política de falha (`Handoff` ou `Agent`). Nasce **desligada**; ligar exige versão publicada.
- Só conversas **novas** entram. Uma conversa em andamento nunca muda de dono por uma ativação criada depois.
- Roteiro sugerido: publicar → simular → ativar para um canal/unidade de teste com parcela pequena → ampliar → (reverter = desligar a ativação).
- **Desligar uma ativação** não afeta instâncias em andamento (terminam na versão em que começaram). **Desligar o runtime** (`Enabled=false` no Worker) suspende as instâncias: antes disso, desligue as ativações e deixe as instâncias terminarem (ou aguarde o prazo de retenção). Religar retoma as suspensas.
- **A ativação é conferida no servidor**: a organização precisa existir no tenant, a unidade pertencer a ela e o canal pertencer à organização e atender a unidade; caso contrário a criação é recusada (400) e nada é gravado.

### Procedimento de drenagem (desativar fluxos com instâncias ativas)

Antes da drenagem, aplique a migração `FlowDrainOutbox` e atualize o comando e todos os Workers que executam fluxos. A trava de execução é compartilhada entre eles.

1. **Parar a entrada**: desligue todas as ativações do fluxo (Fluxos → Ativações). Conversas novas voltam ao agente; as em andamento seguem no fluxo.
2. **Acompanhar**: `sudo ./scripts/cli.sh workflows status` lista as instâncias em execução ou pendentes de finalização. Uma instância esperando pelo cliente termina por resposta, pelo prazo do nó ou pela drenagem.
3. **Drenar o que sobrar**, quando não puder esperar: `sudo ./scripts/cli.sh workflows drain --confirm [--tenant <slug>]`. O comando fixa as instâncias no início da transação. Cancela o conjunto selecionado (`operator-drain`), descarta os efeitos pendentes, limpa as variáveis e os conteúdos cifrados e grava no outbox do Workflows a retomada pelo Agents. Sem `--confirm`, o comando recusa a alteração.
4. Confira `workflows status` de novo. Uma instância criada após a seleção deve terminar ou entrar na próxima drenagem. Para desligar o runtime globalmente, aguarde o status zerar em todas as empresas, ajuste `Workflows__Options__Enabled=false` em `deploy/app.env` e, em `deploy`, recrie o Worker com `docker compose up -d --no-deps --force-recreate worker`. O relay de retomada do Agents continua operando com os fluxos desligados.

Se houver execução ativa ou efeito iniciado sem resultado confirmado, a drenagem inteira é recusada. Mantenha o executor habilitado para terminar ou recuperar o efeito e repita. Se houver transferência por falha sem finalização, confira o atendimento no ContactCenter antes de repetir. Mensagens já registradas no Conversations seguem o envio normal. Efeitos concluídos e efeitos com resultado incerto mantêm seus estados no histórico. Uma consulta externa ambígua segue a recuperação existente e seu resultado deve ser reconciliado no provedor.

Se a confirmação do comando se perder após o commit, consulte `workflows status` e repita o comando. O evento de retomada já gravado será entregue pelo Worker. O comando operacional `status` monitora o outbox `workflows`.

Para voltar a usar fluxos, ajuste `Workflows__Options__Enabled=true` em `app.env`, recrie o Worker com o mesmo comando e religue as ativações para conversas novas.

## 6. Segurança e privacidade

- **Permissões** (por organização do fluxo, com portão de função: sem permissão nenhuma, 403 antes de qualquer consulta): `WorkflowRead`, `WorkflowManage` (editar rascunho), `WorkflowPublish` (publicar, aposentar, ativar), `WorkflowTest` (simular). Administrador: todas; gerente: ler e simular.
- **Tenant** só do contexto do host; recurso de outro tenant responde 404; o filtro global do EF e a conferência no `SaveChanges` valem como nos demais módulos.
- **Variáveis**: cada uma é `Public` ou `Personal`. Na instância, as variáveis ficam **cifradas** (AES-256-GCM, chaves próprias `Workflows:Secrets`, tenant e instância como dado autenticado). O conteúdo dos efeitos também. **Ao encerrar**, as variáveis e o conteúdo dos efeitos são **apagados** (ficam só caminho, estados e códigos). Nenhuma variável vai a log, auditoria, métricas, resposta administrativa ou modelo de IA. Sem chave configurada, nenhuma instância é criada (falha fechada).
- **Retenção por empresa**: categoria `FlowInstances` (padrão 30 dias, `Retention__Days__FlowInstances`, sobrescrevível por empresa) apaga instâncias **encerradas**, passos e efeitos. Instância ativa nunca expira.
- **Auditoria** (`workflows.definition.*`, `workflows.activation.*`, negativas incluídas): sem conteúdo do grafo nem de cliente.
- **Integração**: só operações de leitura, públicas, autorizadas para `Workflow` e do escopo da organização da sessão; o texto do cliente é **valor** de parâmetro declarado, nunca URL, cabeçalho ou operação.
- **Simulador**: o mesmo interpretador no servidor, sem nenhuma porta de IA, conhecimento, integração ou canal; limite de simulações por minuto por tenant.

## 7. Configuração e implantação

Variáveis (`deploy/app.env.example` tem o bloco comentado):

```text
Workflows__Secrets__ActiveKeyId=wf1
Workflows__Secrets__Keys__wf1=<32 bytes em Base64: openssl rand -base64 32>
Workflows__Options__Enabled=true            # só para LIGAR o runtime (Worker); padrão: desligado
Workflows__Options__MaxActiveInstancesPerTenant=1000
Workflows__Options__MaxRunIterations=30
Workflows__Options__StaleRetries=3
Workflows__Options__CapabilityBudget=00:00:08
Workflows__Options__LeaseDuration=00:02:00
Workflows__Options__ExecutorBatchSize=20
Workflows__Options__ExecutorPerTenantBatch=5
Workflows__Options__ExecutorParallelism=4
Workflows__Options__Limits__MaxNodes=200
```

Ordem para ligar em um ambiente: (1) aplicar as migrations (`migrate`: Conversations e Workflows têm migrations novas) em Api e Worker; (2) configurar `Workflows__Secrets__*` **no Worker** (e a política de saída/segredos do Capabilities, se houver nós de integração); (3) publicar um fluxo e criar a ativação **desligada**; (4) ajustar `Workflows__Options__Enabled=true` em `app.env` e recriar o Worker conforme a §5; (5) ligar a ativação para um canal/unidade de teste. O Worker precisa do mesmo banco e das mesmas chaves do Capabilities. O Api não executa fluxos.

## 8. API (`/workflows`)

| Rota | Permissão | O que faz |
|---|---|---|
| `GET /definitions`, `GET /definitions/{id}`, `GET /definitions/by-key/{key}/versions` | `WorkflowRead` | fluxos e versões |
| `POST /definitions` | `WorkflowManage` | cria/atualiza o **rascunho** (`expectedRevision`); devolve os problemas por nó |
| `POST /definitions/{id}/publish`, `/retire` | `WorkflowPublish` | publica (zero problemas) / aposenta |
| `POST /validate`, `POST /simulate` | `WorkflowRead` / `WorkflowTest` | valida; simula (sem chamada real) |
| `GET /operations?organizationId=` | `WorkflowRead` | operações elegíveis para o nó de integração |
| `GET/POST /activations`, `PUT /activations/{id}`, `POST /activations/{id}/enable`, `/disable` | `WorkflowRead` / `WorkflowPublish` | ativações |
| `GET /instances`, `GET /instances/{id}` | `WorkflowRead` | histórico de execução (sem variáveis nem conteúdo) |
| `GET /metrics`, `GET /metrics/funnel` | `WorkflowRead` | contagens por fluxo/versão e funil por nó |

## 9. Histórico, métricas e observabilidade

- Telas **Fluxos → Histórico** (conversas que passaram por um fluxo, o caminho por nós, os efeitos e os códigos) e **Métricas** (por fluxo e versão: começaram, em andamento, concluídas, transferidas, ao agente, falhas, canceladas; funil por nó).
- `Meter` `IaBimeta.Workflows` (por processo): `workflows.instances.started`, `workflows.instances.ended` (etiqueta `status`), `workflows.effects` (`kind`, `result`), `workflows.instance.duration`. A evidência durável é o banco.
- Códigos de falha usuais: `step-limit`, `version-missing`, `handoff-<código>`, `handoff-failed`, `message-rejected`, `stale-input`; cancelamento: `human-takeover`, `session-closed`, `not-ai-responder`, `session-missing`.

## 10. Exemplo mínimo de grafo

```json
{
  "schemaVersion": 1,
  "start": "ola",
  "nodes": [
    { "id": "ola", "type": "message", "config": { "text": "Olá! Posso ajudar?" } },
    { "id": "menu", "type": "question", "config": { "prompt": "Escolha:", "answerType": "choice", "variable": "opcao", "maxAttempts": 2,
        "choices": [ { "id": "horario", "label": "Horário" }, { "id": "humano", "label": "Falar com atendente" } ] } },
    { "id": "rota", "type": "condition", "config": { "branches": [ { "port": "humano", "all": [ { "variable": "opcao", "op": "equals", "value": "humano" } ] } ] } },
    { "id": "horario", "type": "message", "config": { "text": "Atendemos de segunda a sexta." } },
    { "id": "atendente", "type": "handoff", "config": { "text": "Vou chamar um atendente.", "reasonCode": "pedido_do_cliente" } },
    { "id": "fim", "type": "end", "config": { "text": "Até logo!" } }
  ],
  "edges": [
    { "from": "ola", "port": "next", "to": "menu" },
    { "from": "menu", "port": "answered", "to": "rota" }, { "from": "menu", "port": "invalid", "to": "atendente" },
    { "from": "rota", "port": "humano", "to": "atendente" }, { "from": "rota", "port": "else", "to": "horario" },
    { "from": "horario", "port": "next", "to": "fim" }
  ],
  "variables": [ { "name": "opcao", "class": "public" } ]
}
```

## 11. Testes

Interpretador, validador, serializador e simulador (determinismo, ciclos, limites, condições, tipos de resposta); administração (rascunho/publicação, concorrência otimista, permissões, isolamento de tenant, simulação limitada); API com PostgreSQL real (papéis, portão de função, imutabilidade pelo gatilho do banco, concorrência de primeira gravação); **runtime ponta a ponta** (um dono por sessão, uma mensagem por passo, repetição sem duplicar, entrada inválida, transferência e recusa, atendente assume, entrega ao agente, conhecimento, integração com ERP simulado, falha e resultado ambíguo, queda recuperada, executores simultâneos, limite por tenant, cifra); mensagem de fluxo no Conversations (cercas, idempotência, isolamento); arquitetura (arestas, ausência de rede/IA/banco/relógio no motor); frontend (editor, simulador, ativações, histórico, métricas, visibilidade por papel).

## 12. Limitações conhecidas

- **IA com retorno ao fluxo** não existe: o nó `ai` entrega e encerra (contratos e estado `AwaitingAgent` reservados).
- Opções viram **lista numerada em texto**; botões interativos por canal, Instagram, voz e telefonia **não** estão implementados.
- Só integração de **leitura pública**; escrita e dado do cliente dependem de ADR próprio (fase 2) e autenticação do cliente.
- O **resumo de variáveis ao atendente** não é entregue à parte (o histórico da conversa já traz as respostas do cliente).
- Mensagens de um passo são **juntas em uma**; passo que passe de 4096 caracteres é recusado pelo Conversations e cai na política de falha.
- O editor foi verificado por testes automatizados (jsdom) e, uma vez, no navegador embutido com uma API SIMULADA (lista, canvas com nós e conexões, painel do nó, validação com nós em vermelho, simulador com o caminho destacado); a verificação com a API real, o arraste de nós e a conexão por mouse ficam para a homologação.
- Desligar o runtime com instâncias ativas as suspende (ver §5).
- Métricas do `Meter` são por processo; a visão durável vem do histórico no banco (retenção padrão de 30 dias).

## 13. Requisitos para homologação

1. Migrations aplicadas (Conversations: autor `Bot`; Workflows: tabelas, gatilho de imutabilidade, índices).
2. `Workflows__Secrets__*` no Worker e backup da chave (sem ela, instâncias existentes não abrem).
3. Teste com **WhatsApp real** (Meta): primeira mensagem entra no fluxo, lista numerada legível, respostas por número/rótulo, janela de 24 h, falha de envio registrada sem reprocessar fluxo.
4. Teste de **transferência** com fila real e atendente (aviso único, item de fila, atendente vê o histórico).
5. Teste de **entrega ao agente** com a IA real (resposta única à última pergunta).
6. Teste de **integração** com o ERP real (operação pública, escopo da organização, falha/timeout).
7. Conferir o **editor no navegador com a API real** (arraste de nós, conexão por mouse, painel, simulador) e a acessibilidade por teclado.
8. Definir os prazos de retenção por empresa (`Retention__Days__FlowInstances`) e revisar o texto de privacidade.
9. Plano de reversão: o procedimento de drenagem da §5 (desligar ativações, `workflows status`, `workflows drain --confirm`, `Enabled=false`).
10. Teste funcional do editor com a API real: `npm run test:e2e` (feito pelo `EditorDeFluxosComApiRealTests` quando há Node); o arraste e a conexão por mouse continuam manuais (item 7).
