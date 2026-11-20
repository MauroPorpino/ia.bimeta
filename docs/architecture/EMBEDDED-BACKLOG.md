# Embedded: ressalvas, backlog e pendências obrigatórias antes da produção

Origem: encerramento técnico da E1-R em 18/11/2026 (veredito **APROVADO COM RESSALVAS** da auditoria independente do Codex CLI, HEAD `2be8f7763cd1f2958c4151503dff3ca35176a963`). Documento vivo. O que está aqui **não reabre a E1-R**: cada item entra numa etapa própria, com autorização.

## 1. Ressalvas da E1-R (backlog)

| Id | Prioridade | Item | Onde está o contexto | Tratamento previsto |
|---|---|---|---|---|
| BL-1 | P2 | Inconsistência entre lista e contagem em `KnowledgeGapService.ListAsync` (Agents/Knowledge): lista e conta em duas consultas separadas; o teste `ConhecimentoDoFaqNaOperacaoTests` espera `total >= 1` sem conferir os itens e falhou uma vez no gate da 6.ª rodada. Mecanismo identificado por inspeção, ocorrência não reproduzida; independente do Embedded | ADR 0039 (6.ª rodada), `EMBEDDED-REAUDITORIA-E1R.md` | correção própria no Agents/Knowledge (leitura consistente, ou o teste aguardar `items.Count >= total`), fora das etapas do Embedded |
| BL-2 | P2 | Auditoria síncrona das **recusas** do Embedded: se a trilha falha de forma transitória no momento da recusa, a entrada daquela recusa se perde (o ticket já foi queimado; a repetição responde 401) | ADR 0039, matriz do 503, risco residual (ii) | levar as recusas à outbox `identity.audit_outbox` na etapa de endurecimento (E7), com ADR curto se o contrato mudar |
| BL-3 | P3 | Alertas externos e monitoramento da outbox de auditoria: entrada que a trilha recuse para sempre fica na tabela (sem descarte, por decisão) e só aparece no AVISO do Worker; o intervalo de 30 s é de tentativa, não prazo | ADR 0039, riscos (i), (iv), (vi) | indicador no `status` e alerta na homologação; sem infraestrutura nova agora |
| BL-4 | Risco conhecido | Resposta HTTP perdida ou commit sem resposta (resultado desconhecido) em credenciais de uso único: recuperar por novo ticket; para chaves, revogar a de segredo desconhecido e criar outra | ADR 0039, matriz do 503 | documentado ao integrador na E8 (guia do ERP); sem mudança de código |
| BL-5 | Risco conhecido | Retentativas: criar conexão não tem chave de idempotência (repetir duplica a conexão); demais fluxos conforme a matriz | ADR 0039 | decidir na E4 (administração) se a criação ganha chave de idempotência |
| BL-6 | Risco conhecido | Limpeza física posterior ao commit (sessões e tickets) pode responder 503 ou 500 com o efeito já confirmado; repetir é idempotente; a validade já foi retirada pela marca | ADR 0039, riscos (iii) e matriz | documentado; o cliente do ERP deve tratar a repetição da desativação como segura |
| BL-7 | **P3 informativo. Classificação feita pelo Claude; NÃO é veredito nem ressalva do Codex** | **Origem:** ADR 0039, 6.ª rodada, riscos residuais (viii) e (ix), que o Codex não listou entre as ressalvas finais e que o Claude acrescentou por rastreabilidade: (viii) conexão e vínculo desativados são conferidos a cada requisição, não dentro da transação; (ix) o teste de corrida do JIT depende de gatilho de banco e de atraso de 1 s | ADR 0039 (6.ª rodada, riscos residuais) | **Nenhum caminho concreto de ataque foi identificado** (a janela é a de uma requisição em andamento, e a marca de revogação já a cobre); no teste, a máquina saturada faz o teste **recusar-se a passar**, nunca passar em falso. **Não é bloqueador da E2.** Nenhuma ação; pode ser removido do backlog por decisão do proprietário |

## 1.1. Evoluções da premissa Standalone e Embedded (verificação de 18/11/2026)

Origem e evidências: `EMBEDDED-PLANO.md`, "Premissa: plataforma única". Não são ressalvas da E1-R nem bloqueadores da E2.

| Id | Prioridade | Item | Tratamento previsto |
|---|---|---|---|
| BL-8 | P3 (evolução) | O Standalone não tem visão por cliente do sistema externo (histórico e vínculo dependem do `customerRef` da sessão embutida) | E4 (interface), com ADR se exigir rota nova |
| BL-9 | P3 (decisão do proprietário) | Administradores e usuários JIT não usam as duas modalidades (por desenho do ADR 0039: administrativa nunca entra por federação; JIT é `Federated`, sem login por senha). O caminho suportado é o mapeamento de um usuário interno existente e não administrativo | só se o proprietário quiser mudar; exige ADR |

O Knowledge Engine tem backlog próprio: `KNOWLEDGE-BACKLOG.md`.

## 2. Gate da 6.ª rodada (registro fiel)

O gate completo sequencial (`dotnet test backend/IaBimeta.slnx -m:1`, uma execução, 07:03 a 07:26) terminou com **5.234 testes aprovados, 14 ignorados e 1 falha**, a do FAQ (BL-1), independente do Embedded. **O gate NÃO foi integralmente verde e não deve ser apresentado como tal.** A classe do FAQ passou três vezes isoladamente (5 testes em cada). PostgreSQL: pico de 95 conexões (93 ociosas), 0 recusas, 0 deadlocks novos.

## 3. Pendências OBRIGATÓRIAS antes da produção

Nenhuma delas foi exercitada; o código do Embedded só foi provado com API e PostgreSQL reais no Windows de desenvolvimento. Todas entram na Fase 8 de `docs/operations/HOMOLOGACAO-V1.md`, e a V1 só se declara concluída com cada uma registrada como PASSOU.

1. **Docker Compose / PostgreSQL na VM Ubuntu**: subir a pilha real, aplicar **todas as migrations** (inclusive `EmbeddedFederation` e as colunas/tabelas da outbox de auditoria) em banco novo e em banco com dados, ensaiar o `Down` e o rollback para a candidata `v1-rc1` (sessões `ibe_` inertes).
2. **Worker**: entrega da outbox de auditoria do Identity, recuperação por queda de processo, indicadores de pendência e de falha, comportamento com a trilha indisponível.
3. **Observabilidade**: logs estruturados com correlação, indicadores da outbox (BL-3), alerta de entrada envenenada, conexões do PostgreSQL sob carga real.
4. **Caddy**: cabeçalhos (CSP, `frame-ancestors`), HTTPS obrigatório, ausência de cabeçalhos herdados, rotas públicas exatas.
5. **Integração ERP e navegador**: ERP real (BImeta) ou fictício em outra origem, handshake `postMessage`, logout que revoga, origem não autorizada bloqueada; Edge no PC e **Safari e Chrome** em HTTPS real.
6. **Meta**: número de teste e conta de modelos aprovados; limites, códigos de erro, nono dígito/`wa_id` e preços conferidos na documentação oficial; **primeiro envio real só com autorização expressa**.
7. **Validações relacionadas**: ClamAV real, restauração de backup com dados reais, desempenho e memória sob carga, política de consentimento e prazos legais (decisão do proprietário e do jurídico).

## 1.2. Pendências P2/P3 da E2 (E2.6, 19/11/2026; sem implementação nesta etapa)

| Id | Prioridade | Item | Tratamento previsto |
|---|---|---|---|
| BL-10 | P2 (antes da E3) | **Contato de referência opaca (sem telefone E.164):** o comando de saída confirmado preserva a intenção (mensagem marcada `opt_out_command`, atenção `OptOutRevoked`, resposta automática suprimida, atendimento humano possível) mas **não há linha de consentimento** (não é declarado revogado). Sem comunicação automática indevida hoje (não existe início ativo na E2). Risco futuro: o início ativo da E3 por telefone ignorar esse pedido se o mesmo titular for resolvido por telefone. | A E3 deve recusar início ativo para contato com mensagem `opt_out_command` sem consentimento revogado equivalente; sem tabela nova agora |
| BL-11 | P2 | Lista do `CustomerOnly` considera só as 100 sessões mais recentes dos clientes autorizados (E2.3); itens abertos mais antigos não aparecem na lista (seguem no histórico por cursor e em `GET /items/{id}`) | Paginar a lista com cursor e filtrar status/organização na consulta |
| BL-12 | P2 (antes da produção) | `Down` destrutivo de `ContactLinksAndConsents` e `OptOutReviews` (apagam consentimentos e decisões); `MessageOptOutCommandMarker` remove só o marcador | Procedimento de preservação (exportar antes) aprovado antes de qualquer reversão em produção |
| BL-13 | P2 (antes da produção) | Retenção jurídica de `contact_consents` e `optout_reviews` (`LegalRecord`, sem prazo e sem expurgo) e LGPD | Decisão jurídica; ADR de retenção |
| BL-14 | P3 | Meta de 24 h corridas para `PossibleOptOut` não é dia útil; sem calendário da empresa | Calendário de dias úteis quando existir |
| BL-15 | P3 | Sem tela para as pendências de possível saída e para o consentimento (rotas existem; cartão de consentimento é da E4) | E4 |
| BL-16 | P3 | Histórico por cliente exige sessão embutida; o Standalone não tem visão por cliente (ver BL-8); `CustomerOnly` não lê o histórico de atribuições | Evolução |
| BL-17 | P3 | Busca de contato por `external_ref` sem filtro de canal (varredura por tenant); sem auditoria das tentativas de vínculo não resolvidas; `customerRef` com `..` ou `/` não passa pela rota do atendente | Índice por canal / decisão de auditoria |
| BL-18 | P3 | Linguagem natural: lista fixa e conservadora (formulações não previstas não geram pendência, mas ficam visíveis ao atendente); `Kept` não suprime nova ocorrência em mensagem futura | Ampliar a lista por evidência de uso |
| BL-19 | P3 | Contrato HMAC do caminho canônico (hexadecimal em maiúsculas; ambiguidades recusadas com 400 `invalid-path`) precisa constar do guia do integrador | Documentar na homologação com o sistema externo |
| BL-20 | P3 | Entrega da auditoria do Conversations coberta pelo handler e pelo relay em teste, sem teste com o Worker como processo real (recuperação após falha da trilha) | Homologação operacional |
| BL-21 | P3 | Falha intermitente preexistente `Varias_sessoes_recebendo_em_paralelo_mantem_cada_uma_a_sua_propria_sequencia` (pool): não ocorreu no gate sequencial desta etapa; segue NÃO corrigida e sem relação causal demonstrada com a E2 | Investigar fora da E2 |
| BL-22 | P3 | Resposta de IA em curso, defasada por um comando de saída, é rejeitada pela regra fixa (comportamento desejado); o turno seguinte já exclui o comando do contexto | Nenhum |

## 1.3. Correções dos dois P1 da 1.ª auditoria independente da E2 (19/11/2026)

| Id | Prioridade | Achado (Codex) | Reprodução antes da correção | Correção mínima | Teste de regressão |
|---|---|---|---|---|---|
| BL-23 | P1 (corrigido) | Turno já agendado (ou efeito de fluxo já pendente) respondia depois de `PARAR` | Pergunta comum agenda o turno; `PARAR` chega; o executor roda: 1 chamada ao modelo e resposta. Fluxo: efeito pendente enviava a mensagem do menu depois do comando | `BeginTurn` (sob a trava da sessão): com o comando de saída como ÚLTIMA entrada, o snapshot marca o pendente como coberto, o executor encerra por "nada pendente" sem chamar o modelo (inclui o sucessor de uma resposta defasada). `RegisterFlowMessage`: nova recusa `RegisterReplyOutcome.OptOutCommand`; o fluxo cancela sem falha e a sessão volta ao agente. Mensagem comum depois reabre o processamento | `Turno_agendado_antes_do_PARAR...`, `Efeito_de_fluxo_pendente_antes_do_PARAR...` |
| BL-24 | P1 (corrigido) | Revogação atrasada reduzia a referência de `consent-superseded` e deixava reativar o consentimento | `Revoked` 12h, `Revoked` 10h, `Granted` 11h: os três aceitos, estado final `Granted` | A concessão é comparada com a MAIOR coleta de revogação da chave, sob a mesma trava do telefone (`GetLatestRevocationCollectedAtAsync`); revogações antigas continuam aceitas | `Revogacao_atrasada_nao_reduz...` (unitário) e `Revoked_12h_Revoked_10h_Granted_11h...` (HMAC) |
| BL-25 | P3 | Falha INTERMITENTE de infraestrutura de teste: `O_exchange_recusa_corpo_acima_do_teto_com_ou_sem_content_length_sem_virar_500(semContentLength: True)` (conexão anulada pelo servidor enquanto o cliente ainda envia o corpo em pedaços). Falhou uma vez no gate de 19/11 (após as correções dos P1); aprovou nas 3 reexecuções isoladas e no gate anterior; rota do Identity, fora do código tocado | Estabilizar o teste (tolerar o reset do transporte) fora da E2 |
