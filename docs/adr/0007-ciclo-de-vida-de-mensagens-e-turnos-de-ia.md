# ADR 0007 — Ciclo de vida de mensagens e turnos de IA

- **Status:** Aceito
- **Data:** 26/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/CONVERSATION-LIFECYCLE.md` v1.0, `docs/architecture/BOUNDARIES.md` v0.4 (seções 6 e 12), `ARCHITECTURE.md` (seção 4.1), ADRs 0005 e 0006
- **Preserva:** a matriz `docs/architecture/module-dependencies.json` (nenhuma aresta nova), os ADRs 0001 a 0006 e a separação entre Conversations, Agents, Channels e ContactCenter.

## Contexto

O Conversation Engine precisa decidir quando a IA responde, o que acontece quando o cliente escreve de novo durante o processamento, como um turno é substituído ou cancelado, e como evitar respostas desencontradas, duplicadas ou enviadas à pessoa errada. O `BOUNDARIES.md` v0.3 já tinha a `StateVersion`, as lanes por sessão e a regra de que mensagens pendentes "podem virar um turno" (ORD-4), mas sem especificar o agrupamento, as cercas de entrada, a substituição de turnos, a cobertura lógica da resposta nem o tratamento de mídia pendente.

## Decisão

1. **Agrupamento.** Mensagens próximas viram um único turno de IA, com janela quieta e teto de espera configuráveis. Valores iniciais de referência: `QuietWindow = 3 s`, `MaxWait = 10 s`, `MaxSupersedesPerBurst = 2`, `MaxTurnAttempts = 3`, todos configuráveis e sujeitos a calibração.
2. **Política `Supersede`, a única implementada no MVP**, com limite configurável de interrupções. Mensagem nova durante o processamento interrompe o turno e cria um sucessor, exceto na fase de efeito externo, que nunca é cancelada. **Atingido o limite**, o turno atual termina sem novas interrupções; se sua resposta estiver defasada, é **recusada** e um novo turno cobre as mensagens pendentes. O limite de tentativas e as proteções contra ciclos infinitos são preservados: cada descarte exige entrada nova do cliente, e a rajada longa é sinalizada ao ContactCenter (decisão 12).
3. **Cerca de entrada obrigatória.** Toda resposta da IA carrega `basedOnInboundSeq`, `StateVersion` e `TurnId` (e a época do turno ativo). O Conversations só aceita a resposta que considerou todas as mensagens do cliente até aquele momento.
4. **Resposta defasada é sempre rejeitada pelo Conversations, por regra fixa.** Vale também para turnos com efeito colateral ou dado sensível. **Não existe configuração para isso no MVP**: nem `StaleReplyMode`, nem chave por tenant, nem `allowStaleInput` no contrato, e o Conversations **não consulta o Audit** para aceitar respostas. A possibilidade de exceções (`allowStaleInput`, a configuração que a habilitaria, a verificação independente por auditoria) e a política `Queue` ficam **exclusivamente como evolução futura documentada** no `CONVERSATION-LIFECYCLE.md` §6.3, **dependem de novo ADR** com a aprovação do decisor, e o MVP não deixa chave nem código preparatório para elas.
5. **Turno logicamente ativo único e substituição atômica**, decididos pelo Conversations (`BeginTurn`, `EndTurn`, `CancelTurn`, com época e token de lane). Um turno substituído, cancelado ou de época antiga **não registra resposta, mesmo que sua execução física continue**. **A correção depende apenas das cercas transacionais do Conversations. O cancelamento físico de uma chamada de IA é somente uma otimização de custo e latência**, e o sistema deve ser correto com ele desligado.
6. **Cobertura lógica separada de entrega.** `AnsweredThroughSeq` representa a cobertura lógica da resposta aceita. Os estados reais de envio e entrega são do Channels. **Falha definitiva de envio é registrada, sinalizada ao ContactCenter e recuperada por ação autorizada do atendente** (reenviar, enviar outra mensagem, assumir ou dispensar), **sem disparar novo turno de IA automaticamente**. O reenvio é o contrato `RequestResend`, com estas garantias: autorização explícita do atendente; vínculo auditável entre a mensagem original e a nova solicitação; idempotência; regras e janela do canal aplicadas pelo Channels, sem contorno; **nenhuma repetição automática de `SendUnknown` sem confirmação segura**; e nenhuma nova execução da IA por causa da falha de envio. O handoff automático por falha de envio é evolução futura.
7. **Mídia com conteúdo pendente nunca é interpretada pela IA.** O turno não começa com conteúdo indisponível. Ao esgotar o prazo, aplica-se política explícita por tipo: espera adicional, resposta informativa determinística, ou encaminhamento a humano.
8. **Efeitos externos** seguem a EFX do ADR 0006: idempotência comprovada ou reconciliação, sem reenvio automático de resultado ambíguo. O resultado de ação executada por turno substituído chega ao cliente pelo turno sucessor.
9. **Reconciliador** garante que nenhuma mensagem pendente fique sem turno enquanto a IA estiver no comando.
10. **Propriedade das configurações.** O **Agents** controla agrupamento, interrupções, tentativas e política de conteúdo. O **Conversations** aplica regras fixas de consistência e de aceitação de respostas, **sem configuração por tenant no MVP**. O **ContactCenter** controla o que é do atendimento humano. Nenhum módulo lê a configuração de outro.
11. **Contratos novos** no `Conversations.Contracts` (`BeginTurn`, `EndTurn`, `CancelTurn`, `RegisterAiReply` com a cerca de entrada, `RequestResend`, `RaiseSessionAttention` e eventos de conteúdo, de falha de envio e de atenção da sessão), todos dentro de arestas **já existentes** da matriz. A consulta de evidência ao `Audit` é evolução futura.
12. **Rajada longa (`OnChattyBurst`).** Ao atingir o limite configurável de mensagens ou de interrupções, o Agents pede ao Conversations que sinalize a sessão (`RaiseSessionAttention`, um sinal por rajada), e o ContactCenter cria o item de atenção e **permite a transferência para humano por ação do atendente**. O comportamento inicial é `Alert`. **Não há transferência automática** sem configuração explícita do tenant (`OnChattyBurst = Handoff`), que não é herdada de `OnAiPermanentFailure`. O Agents continua chamando o ContactCenter somente por `RequestHandoff`.

## Alternativas descartadas

- Aceitar resposta defasada por padrão.
- Implementar já no MVP a exceção `allowStaleInput`, a política `Queue` e a consulta de evidência ao Audit (adiadas).
- Manter no MVP uma chave `StaleReplyMode` de valor único (ponto de extensão sem uso, que convida a ligar a exceção por configuração).
- Decidir o turno ativo no Agents (exigiria transação entre módulos ou duplicaria a autoridade).
- Depender do cancelamento físico da chamada de IA para a correção.
- Tratar falha de envio como entrada para a IA, ou recuperá-la por handoff ou reenvio automáticos.
- Repetir automaticamente um envio em `SendUnknown`, ou reenviar sem vínculo auditável, sem idempotência ou contornando a janela do canal.
- Transferir para humano automaticamente quando a rajada ficar longa, sem configuração explícita do tenant.
- Mandar o modelo interpretar mídia ainda não processada.
- Retratar resposta já aceita e ainda não enviada (adiado).

## Consequências

- O `Conversations` ganha estado por sessão (`active_turn_id`, `turn_epoch`, `last_lease_token`, `InboundSeq`, `AnsweredThroughSeq`), três comandos de turno, o `RequestResend` (com as garantias da decisão 6) e o `RaiseSessionAttention`.
- O MVP fica mais simples: não há consulta ao Audit no aceite, nem `StaleReplyMode`, nem `allowStaleInput`, nem `Queue`. Se alguma exceção defasada vier a ser adotada, dependerá de novo ADR, e então de o `Capabilities` e do `Knowledge` gravarem auditoria síncrona com `TurnId`.
- Com a rejeição fixa, um cliente muito falante pode gerar descartes e custo. A mitigação é o limite de interrupções, o turno não interrompível, a sinalização da rajada longa ao ContactCenter (que permite a transferência humana, sem transferir por conta própria) e as métricas de desperdício. Sem atendente atuando e sem `OnChattyBurst = Handoff` configurado, o cliente pode esperar até a rajada terminar.
- Os limites da rajada (`ChattyBurstMaxMessages` e `ChattyBurstMaxInterruptions`) são valores de referência a calibrar com o piloto.
- A janela de agrupamento acrescenta latência ao cliente que escreve uma só mensagem. O benefício esperado (menos chamadas e melhor compreensão) é **hipótese não medida**; as métricas da seção 15 do `CONVERSATION-LIFECYCLE.md` a confirmam ou não.
- `BOUNDARIES.md` v0.4, `ARCHITECTURE.md` v0.6 e `CLAUDE.md` passam a referenciar este ADR.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Resposta defasada aceita indevidamente | A rejeição é regra fixa e não existe configuração que a altere: nenhuma resposta defasada é aceita, e testes provam que nenhuma declaração ou chave a faz passar. |
| Dois turnos ativos ou turno substituído respondendo | Coluna única no Conversations, época e token de lane; testes de substituição atômica. |
| Cancelamento físico da chamada de IA falhar | A correção não depende dele: as cercas transacionais recusam a resposta. Teste com o cancelamento desligado. |
| Falha de envio acionar IA em laço | Evento de falha não é consumido pelo Agents (ARQ-24); reconciliador ignora estado de envio; recuperação só por ação do atendente. |
| Reenvio duplicar mensagem ou contornar o canal | `RequestResend` só por ação humana, com `requestId` idempotente, um reenvio ativo por mensagem, vínculo auditável, regras e janela aplicadas pelo Channels e recusa de `SendUnknown` até haver confirmação segura. |
| IA interpretar conteúdo indisponível | Turno não começa; testes em que o dublê do modelo não recebe chamada. |
| Cliente muito falante gerar custo ilimitado | Limite de interrupções, turno não interrompível e sinalização da rajada longa (`OnChattyBurst`, padrão `Alert`); o ciclo é limitado pela entrada. |
| Transferência automática indevida por rajada longa | Só com `OnChattyBurst = Handoff` configurado explicitamente; valor ausente ou inválido vale `Alert`; não herda de `OnAiPermanentFailure`. |
| Latência da janela de agrupamento | Valores curtos e configuráveis; tipos de disparo imediato; métrica de ganho e de custo. |

## Critérios de aceitação

1. Um turno substituído que continua executando recebe `TurnNotActive` e nenhuma resposta dele sai.
2. Dois `BeginTurn` concorrentes deixam um só turno ativo.
3. **Toda resposta defasada é rejeitada**, inclusive a de turno com efeito colateral ou dado sensível e mesmo que o Agents declare o contrário. Não existe chave de configuração, de tenant ou de ambiente que altere isso, e o Conversations não consulta o Audit para aceitar respostas.
4. Atingido o limite de interrupções, o turno termina, a resposta defasada é recusada e um novo turno cobre as pendentes; sem entrada nova do cliente, não nasce novo turno.
5. O resultado lógico (respostas aceitas e recusadas) é o mesmo com o **cancelamento físico das chamadas de IA desligado**, e um modelo que ignora o cancelamento não faz sair nenhuma resposta indevida.
6. Falha definitiva de envio é registrada e sinalizada ao ContactCenter, só é recuperada por ação autorizada do atendente, e nenhum turno de IA é criado.
7. Com mídia pendente, o modelo não recebe nenhuma chamada, e ao esgotar o prazo vale a política do tipo.
8. `AnsweredThroughSeq` nunca decresce e não depende de envio ou entrega.
9. O cenário Mauro/Pedro ampliado passa: 3 mensagens em 2 s geram 1 turno, e a resposta vai somente ao atendimento correto.
10. `RequestResend` exige autorização explícita do atendente, vincula a nova solicitação à mensagem original de forma auditável, é idempotente, deixa o Channels aplicar as regras e a janela, recusa `SendUnknown` e não cria turno de IA.
11. Atingido o limite de mensagens ou de interrupções, a sessão é sinalizada ao ContactCenter (um sinal por rajada) e o atendente pode transferir; nenhuma transferência automática ocorre sem `OnChattyBurst = Handoff` configurado explicitamente.

## Pendências

- Quem processa a mídia e os prazos por tipo.
- Especificação do modo `AiSupervised` e da cobertura em `PendingApproval`.
- Calibração dos valores iniciais, inclusive dos limites da rajada longa, com o piloto.
- Verificação, em documentação oficial, dos eventos de reação, edição e exclusão do WhatsApp.
- Decisão futura, por novo ADR, sobre a exceção de resposta defasada, a política `Queue` e o handoff automático por falha de envio.

## Revisão

Reavaliar se a janela de agrupamento prejudicar a experiência, se a taxa de interrupção ou de descarte for alta a ponto de custar caro, se surgir caso real que exija a exceção defasada, ou quando o fluxo `AiSupervised` for especificado.
