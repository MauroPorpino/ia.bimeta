# ADR 0008 — Autorização de unidade por canal, mediada pelo host

- **Status:** Aceito (28/10/2026), com os ajustes pedidos pelo decisor: testes de falha da autorização, análise da consulta sob a trava da sessão e registro da divergência com a REC-2
- **Data:** 28/10/2026
- **Decisor:** Mauro Porpino
- **Referências:** `docs/architecture/BOUNDARIES.md` v0.4.2 (seções 1, 3, 4, 6 e 11), `docs/architecture/module-dependencies.json`, ADRs 0005, 0006 e 0007
- **Preserva:** a matriz `module-dependencies.json` (**nenhuma aresta nova**), os ADRs 0001 a 0007 e a regra de que a lista de unidades autorizadas de cada número pertence ao `Channels`.
- **Registra:** uma **exceção** ao espírito da COM-4 (módulo inferior não chama superior), nos termos da seção 11 do `BOUNDARIES.md` ("exceção a uma regra" exige ADR).

## Contexto

Um tenant pode ter vários números de WhatsApp (instâncias de canal), e cada número só pode atender as unidades (matriz e filiais) autorizadas para ele. A decisão do produto (28/10/2026) é que essa lista pertence ao `Channels` e vale em dois momentos: no roteamento inicial de uma conversa nova e em toda transferência posterior entre unidades.

Quem grava a sessão e executa a transferência é o `Conversations`, então é ele que precisa da resposta à pergunta "este número pode atender esta unidade?". A matriz permite `Channels → Conversations`, mas **não** `Conversations → Channels` nem `ContactCenter → Channels`. Acrescentar a aresta `Conversations → Channels` criaria um **ciclo** com a aresta existente e violaria a CIC-1 (o grafo é um DAG). Ler as tabelas do `Channels` é proibido (DAD-2), e o `Conversations` não pode aceitar uma sessão numa unidade que o número não atende, nem transferir para ela.

## Decisão

1. **A porta é do `Conversations`.** `IChannelUnitAuthorization` (`Conversations.Core`, namespace `IaBimeta.Conversations.Application`) faz a pergunta `IsBusinessUnitAuthorizedAsync(channelInstanceId, businessUnitId)`. A implementação padrão, `DenyAllChannelUnitAuthorization`, **nega tudo**, para que um host sem o `Channels` ligado nunca aceite uma unidade por omissão. A interface é permitida pela PRJ-3: é uma fronteira obrigatória entre módulos e há teste que exige substituição.
2. **A consulta é do `Channels`.** `IChannelRouting` (`Channels.Contracts.Queries`) devolve os fatos de roteamento da instância e responde `IsBusinessUnitAuthorizedAsync`. O tenant é sempre o do `RequestContext`: instância inexistente, de outro tenant ou sem a unidade respondem o mesmo, **falso**. A consulta não aplica permissões de usuário (quem transfere com `ConversationManage` não precisa de `ChannelRead`) e não depende da situação do canal (a associação vale com o canal suspenso).
3. **O host liga as duas.** O adaptador `ChannelsBackedUnitAuthorization` (`IaBimeta.Api/Channels`) só delega a pergunta ao `IChannelRouting`. **Não contém regra de negócio**: o host só compõe (`BOUNDARIES.md` §2), e só ele conhece os dois módulos. Ele é registrado antes de `AddConversations`, que só acrescenta o padrão que nega tudo se não houver outro (`TryAdd`).
4. **Sem cópia.** Cada pergunta consulta o `Channels` na hora. Não há projeção, cache nem evento: retirar uma unidade da lista vale na pergunta seguinte.
5. **Falha fechada.** Só um "sim" inequívoco autoriza. Se a consulta lança exceção (por exemplo, banco do `Channels` indisponível), a exceção **sobe**: a operação não acontece e pode ser repetida. A falha nunca vira "sim" nem "não".
6. **Onde a regra se aplica, e só aí:**
   - **Ingestão:** para uma sessão **nova** que traga unidade (direta ou derivada do setor), sob a trava da sessão. Recusa lança `ChannelUnitNotAuthorizedException` e **nada é gravado**; nunca se escolhe outra unidade no lugar. Sessão já aberta **não** revalida nem é reencaminhada, e a mensagem do cliente numa sessão aberta nunca se perde. Mensagem sem unidade não pergunta nada.
   - **Transferência:** em toda `TransferSession`, resultado `TargetNotAuthorizedForChannel`, inclusive para quem tem `ConversationManage`. Repetição idempotente de uma operação já aplicada não reavalia a lista.
7. **Outros consumidores.** `ContactCenter` e `Agents` pedem a transferência ao `Conversations` e já recebem a regra. Se algum deles precisar, por exemplo, **listar** as unidades elegíveis de um número para uma tela, **define a sua própria porta** e o host a liga ao `IChannelRouting`, da mesma forma. Não nasce aresta na matriz nem acesso a tabela do `Channels`.

## Alternativas descartadas

- **Aresta `Conversations → Channels` na matriz.** Cria ciclo com `Channels → Conversations` (CIC-1) e inverte a hierarquia.
- **Projeção local no `Conversations`, alimentada por eventos do `Channels` (DAD-3, COM-4).** Abre uma janela de inconsistência numa **autorização**: uma unidade retirada continuaria aceita até o evento chegar. Duplica um estado que é do `Channels`, e o evento durável (outbox) só existe a partir da etapa 4 (COM-3).
- **Aresta `ContactCenter → Channels`.** Mesmo problema, e o `ContactCenter` já fala com o `Conversations` para transferir.
- **O chamador informar que a unidade está autorizada.** Confiaria num dado de entrada, contra o princípio 5 (autorização vem do código, nunca de argumento).
- **Validar só no `Channels`, na entrada.** Não cobre a transferência, que acontece depois, no `Conversations`.
- **Mover a lista de unidades para o `Conversations`.** A decisão do produto é que ela pertence ao `Channels`.

## Consequências

- **Exceção à COM-4, delimitada.** O `Conversations` (inferior na matriz) passa a perguntar ao `Channels` (superior) em tempo de execução, mas só por **leitura**, mediada pelo host, sem referência de compilação entre os módulos e sem escrita. Qualquer uso novo desse padrão precisa de justificativa própria; se ele se tornar regra geral, deve virar ADR para a matriz ou para eventos.
- **Acoplamento em tempo de execução.** Se o banco do `Channels` estiver indisponível, falham a ingestão de sessão **nova com unidade** e as transferências (falha fechada). Mensagens em sessão aberta e mensagens sem unidade continuam. Quando o Inbox existir (etapa 5), a ingestão repete a operação em vez de perder a mensagem.
- **Consulta sob a trava da sessão (analisada em 28/10/2026; mantida).** A consulta roda com a trava da linha da sessão do `Conversations` mantida, em outro `DbContext` e outra conexão, e não há transação entre módulos (COM-5). **Não há alternativa simples que preserve a segurança** e reduza essa região crítica:
  - Não existe atomicidade entre o `Channels` e o `Conversations`: a autorização vale no instante da consulta, com ou sem trava, e a trava não impede uma revogação logo depois. Consultar o mais perto possível da gravação é o que dá a resposta mais fresca.
  - Consultar antes de abrir a transação ou antes da trava só alarga a janela entre a resposta e a gravação. Além disso, a sessão só se sabe nova depois da trava, então seria preciso perguntar também para as mensagens de sessões já abertas, e uma falha do `Channels` passaria a atingir mensagens que hoje seguem.
  - Cache com validade aceitaria uma autorização já revogada.
  - **Risco técnico registrado:** a consulta alonga a trava em uma ida ao banco, e cada requisição passa a segurar **duas conexões** (a da transação do `Conversations` e a da consulta). Com muita concorrência de sessões novas com unidade, o pool de conexões pode esgotar e as operações esperam até o tempo limite; elas falham fechadas, sem perda de mensagem aceita.
  - **Reavaliação:** antes do worker de ingestão da etapa 5, medir a latência da trava e o uso do pool. Mitigações previstas, sem mudança estrutural agora: limite de concorrência por tenant (LIM-1) e tamanho do pool. Qualquer outra forma de verificar exige novo ADR.
- **Limites conhecidos.** Canal que atende unidades de **outra organização** não é suportado: toda unidade autorizada é da organização dona do canal. O vínculo explícito de contato entre números também não existe: as conversas são independentes por número.
- **Testes de integração em três composições do host:** a padrão, com dublê que autoriza qualquer unidade (para os testes que não tratam de canal); a de "canais reais", com o adaptador sobre o `Channels` verdadeiro; e a de "canais indisponíveis", com o adaptador sobre um `Channels` cujo banco não responde.
- **Documentação.** O `BOUNDARIES.md` v0.4.2 registra a criação dos projetos do `Channels` (§1) e a exceção aprovada como COM-7 (§4), que remete a este ADR. A matriz e o JSON não mudam.

## Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Unidade aceita por omissão de registro | Padrão `DenyAll`; só um "sim" inequívoco autoriza; teste da implementação padrão |
| Autorização com cópia velha | Sem cópia: consulta na hora; teste que retira a unidade e vê a mudança na pergunta seguinte |
| Mensagem do cliente perdida porque a associação foi retirada depois | Só sessão nova valida; sessão aberta não revalida; teste da mensagem em sessão já aberta |
| Adaptador do host ganhar regra de negócio | O adaptador só delega; revisão de código; a regra vive no `Channels` (consulta) e no `Conversations` (aplicação) |
| Falha do `Channels` derrubar todo o atendimento | Só atinge sessão nova com unidade e transferência; sessão aberta e mensagem sem unidade seguem (teste com o banco do `Channels` fora do ar) |
| Trava da sessão mantida durante a consulta e pool de conexões esgotado | Falha fechada, sem perda; medição antes do worker de ingestão; limite de concorrência por tenant (LIM-1) |
| O padrão se espalhar sem controle | Cada novo uso define a própria porta e justifica; uso recorrente leva a ADR (matriz ou eventos) |

## Critérios de aceitação

Cobertos por testes existentes (`tests/conversations/.../ChannelUnitAuthorizationTests.cs` e `tests/IaBimeta.IntegrationTests/Channels/ChannelUnitAuthorizationTests.cs`):

1. A implementação padrão nega toda unidade em todo canal.
2. A pergunta só responde "sim" para a unidade autorizada; instância inexistente, de outro tenant ou sem unidades respondem "não".
3. Ingestão de sessão nova com unidade não autorizada (ou setor de unidade não autorizada) é recusada sem gravar nada e sem escolher outra unidade.
4. Sessão já aberta continua recebendo a mensagem do cliente mesmo com a associação retirada depois; o evento repetido do provedor segue idempotente.
5. Transferência para unidade não autorizada é recusada sem gravar nada nem mudar a versão, inclusive para quem tem só `ConversationManage`, e com o canal suspenso a regra continua valendo.
6. A resposta acompanha a lista do canal na hora (autorizar e retirar mudam a resposta seguinte).
7. A mesma pessoa em dois números é avaliada pelo número de cada sessão.
8. A matriz e o grafo de projetos permanecem iguais (ARQ-06, `GrafoDeProjetosTests`).

Cobertos pelos testes de falha (`ChannelUnitAuthorizationFailureTests.cs`, mesmo diretório):

9. O adaptador devolve exatamente o "sim" ou o "não" do `Channels`, e a exceção da consulta sobe **a mesma**, sem embrulho (a tarefa que falha e a chamada que lança antes de devolver a tarefa).
10. Com o banco do `Channels` fora do ar (falha real, não dublê), a ingestão de sessão nova com unidade, a ingestão por setor e a transferência **lançam a causa real do banco**: não viram "sim" nem "não" (`ChannelUnitNotAuthorizedException`, `TargetNotAuthorizedForChannel`).
11. Nessas falhas, nada é gravado nem publicado: nenhuma sessão, contato, evento ou auditoria, e a sessão fica igual (unidade, setor e `StateVersion`). O mesmo evento e o mesmo pedido de transferência (mesmo `transferId` e mesma versão) valem depois, com o `Channels` de volta.
12. Com o `Channels` fora do ar, a mensagem em sessão já aberta e a mensagem sem unidade continuam registradas.
13. As mutações "converter a exceção em sim" e "converter a exceção em não", no adaptador, na ingestão e na transferência, são detectadas pelos testes.

## Pendências

- **Divergência registrada com a REC-2 (`BOUNDARIES.md` §12.2).** A REC-2 descreve a resolução do canal por uma projeção local `channels.channel_account`, e a REC-1 prevê localizar o canal por um identificador opaco da URL. O cadastro implementado resolve a rota de entrada por `channels.channel_routes` (chave: provedor e identificador externo), sem endpoint de webhook. **A conciliação é obrigatória antes da implementação do Receiver da Meta**, sob o gate PRV-2 (§12.11), e deve ser registrada no ADR do `Channels`.
- Reavaliar a consulta sob a trava da sessão antes do worker de ingestão da etapa 5 (ver Consequências).
