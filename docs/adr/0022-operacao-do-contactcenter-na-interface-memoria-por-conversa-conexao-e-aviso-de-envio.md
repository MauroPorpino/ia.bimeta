# ADR 0022 — Operação do ContactCenter na interface: memória por conversa, concorrência entre atendentes, conexão e aviso de envio

- **Status:** **Aceito para o MVP (ratificado pelo decisor; status sincronizado em 09/11/2026).** Implementado em 06/11/2026 sob a autorização do decisor ("consolidar a operação funcional do ContactCenter no frontend React"), com autonomia para decisões técnicas rotineiras. Nenhuma chamada real à Meta ou à OpenAI.
- **Data:** 06/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0018 (fluxo do MVP e ações do atendente), 0019 (pausa de conta, SND-6) e 0020 (sinal de falha na fila, SND-7); `docs/architecture/BOUNDARIES.md` (módulos, matriz de dependências)
- **Preserva:** a matriz `module-dependencies.json` (nenhuma aresta nova: o front fala com cada módulo pela API do host), a autoridade do Conversations sobre o estado da conversa e o contrato das ações do ContactCenter (idempotência por `requestId`, 409 para conflito, 403 para quem não é o responsável).

## Contexto

A interface do atendente (React) já listava a fila, mostrava a conversa e executava assumir, responder, devolver e encerrar. A operação diária com **dois atendentes ao mesmo tempo** mostrou lacunas: trocar de conversa perdia o histórico lido e o rascunho; a releitura periódica **substituía** o histórico (nada era guardado entre conversas); a queda do servidor aparecia como uma pilha de erros genéricos; quando um colega assumia uma conversa, a tela do outro ficava com o botão "Assumir" parado e um erro vermelho de acesso negado; e o atendente comum não tinha como saber que o WhatsApp estava pausado (ele não lê canais).

## Decisão

1. **Memória por conversa, dona da área logada.** `CacheDeConversas` guarda, por atendimento, o item, o histórico já lido e o **rascunho** da resposta (no máximo 30 conversas, descartando as menos recentes). Trocar de conversa e voltar mostra tudo **na hora**; o polling só atualiza. A memória nasce no login e some no logout (nada vai para `localStorage`, nada sobrevive a outro usuário na mesma aba). O rascunho é a fonte da verdade da resposta: ao concluir um envio, só é limpo se o atendente não o editou, **mesmo que ele tenha trocado de conversa no meio do envio**.
2. **Histórico sem duplicar.** A releitura (as últimas 100 mensagens, a cada 2 s) é **mesclada** ao que a tela já tem, por id da mensagem e ordem de `messageSeq`: mensagens antigas continuam, o estado de entrega avança no lugar e, sem novidade, o mesmo array é reaproveitado (a tela não se redesenha nem rola).
3. **Fila: as duas listas juntas.** A cada 3 s a fila lê "Aguardando" e "Meus"; as abas mostram o contador (`Aguardando (3)`), a troca de aba não pisca e o título da página mostra quantas conversas esperam, para o atendente em outra janela ver que chegou trabalho. Continua polling, sem WebSocket (a decisão de tempo real fica para quando houver necessidade medida).
4. **Concorrência entre atendentes.** Perder a corrida ao assumir (409) não é erro: a tela explica "outro atendente assumiu antes de você" e relê. Para quem **não é o responsável**, a leitura do item e das mensagens passa a responder 403 (404 quando fora do alcance): a tela trata ambos como "esta conversa não está mais disponível para você", **esquece o histórico guardado** (a conversa de outro atendente não fica na tela nem na memória), esconde as ações e avisa a fila uma vez. Se a conversa voltar à fila, a próxima leitura a traz de novo. Responder/encerrar depois de perder a conversa (409 ou 403) mantém o texto no campo e relê o estado.
5. **Conexão.** Falha de conexão é: rede caída, 502/503/504 do proxy e **5xx sem corpo de problema** (o proxy do Vite com a API desligada responde 500 vazio); 5xx **com** corpo é erro da própria API. A área logada mostra **uma** faixa ("sem conexão, tentando reconectar", sem esvaziar a tela) e, quando a conexão volta, "conexão restabelecida" por 4 s e uma **releitura imediata** (também ao voltar a aba ao primeiro plano e no evento `online` do navegador). O erro de uma ação que falhou por falta de conexão some sozinho na volta. Com a API fora **ao abrir a tela**, a identificação do usuário e as leituras únicas (filas e presença) se repetem sozinhas até darem certo: nada fica preso na faixa. Repetir o **mesmo** texto depois da queda reaproveita o `requestId`: a resposta sai uma vez só, mesmo que a primeira tentativa tenha chegado ao servidor sem a resposta voltar.
6. **Aviso de contas de WhatsApp pausadas para quem atende.** Nova rota `GET /channels/sending-alerts` (Channels, `AccountSendingService.ListAlertsAsync`): lista as contas **pausadas ou esperando o provedor** das organizações do usuário. Autorização: **leitura da organização** (o mínimo de qualquer papel), e não leitura de canais, porque o atendente comum não tem `ChannelRead`; em troca a resposta é mínima (id e nome da conta, se está pausada, até quando espera e os números afetados) e **nunca** traz o motivo técnico da pausa, a vazão ou credencial. A interface mostra uma faixa no topo e, na conversa cujo número está afetado, um aviso junto ao campo de resposta. A consulta é secundária: se falha, a tela segue sem ela e sem erro.

## O que fica fora (pendências)

- **"Carregar mensagens anteriores":** conversas com mais de 100 mensagens mostram só as mais recentes (a API já tem o cursor; a tela ainda não o usa).
- **Tempo real (WebSocket/SSE), notificação sonora e do navegador:** só o contador no título.
- **Reenvio de mensagem com falha definitiva:** continua sendo o atendente responder de novo (ADR 0020).
- **Rascunhos entre recarregamentos da página:** a memória é só da sessão em memória (recarregar a página os perde).
- **Autorização por unidade no aviso de envio:** o recorte é a organização; filtrar por unidade/fila exigiria o ContactCenter informar ao Channels (aresta nova), o que não se justifica ainda.

## Consequências

- Nenhuma migração. Nenhuma mudança de contrato de ações. Uma rota nova (leitura) no Channels e um parâmetro novo (`TimeProvider`) no `AccountSendingService`.
- Testes: 103 testes do frontend (cache e mesclagem, troca de conversa, rascunho, concorrência, queda e volta de conexão, aviso de envio) e, no backend, `OperacaoDoContactCenterTests` (dois atendentes e várias conversas por HTTP real: corrida com um vencedor, cada resposta vai só ao cliente certo, histórico sem mistura, não responsável recusado, repetir não duplica, devolver e reassumir, encerrar; e o aviso de conta pausada com o que o atendente comum recebe).
