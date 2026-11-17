# Pacote de reauditoria da E1-R (Identity federado do Embedded)

Para o Codex (ou outro revisor independente). A auditoria da E1 (`8cf9ffd`) a **reprovou**. A etapa corretiva E1-R está na branch `feature/v1-embedded`, em commits pequenos depois de `8cf9ffd` (lista abaixo). O objetivo desta reauditoria é **confirmar a eliminação das vulnerabilidades e procurar regressões**; a E1 só é considerada aprovada sem bloqueadores. Decisões: ADR 0039 (seção "E1-R"). Nada foi enviado à Meta, à OpenAI nem a ERP real; sem push; `main` e a tag `v1-rc1` intactas.

> **Observação de rastreabilidade.** Os achados abaixo seguem a lista de correções obrigatórias que o proprietário repassou a partir do seu relatório; o texto integral do relatório não estava no repositório quando a E1-R foi feita. Se o relatório trouxer um cenário de exploração que não esteja coberto por um teste abaixo, trate como lacuna e reporte.

## Achado, correção, prova

| # | Achado | Correção (onde) | Teste que falha sem a correção |
|---|---|---|---|
| 1 | Organização da conexão não limitava participação em fila, responsável nem recibos: usuário federado membro de fila de OUTRA organização do mesmo tenant via itens e conversas dela e operava sobre eles | `ActorPrincipal.Embedded` + `AccessPolicy.IsOutsideEmbeddedScope` (SharedKernel); `QueueAccess`, `ListAsync`, `ListMineAsync`, `FindReceiptAsync` (ContactCenter.Core); escopo montado em `SessionAuthenticator` e levado pelo host | `EmbeddedOrganizacaoApiTests` (lista, itens, transferência, recibo, serviço). Com o limite desligado, 5 dos 8 falham |
| 2 | `CustomerOnly` alcançava a fila geral | Sem permissões, escopo que nega toda via não-concessão, e subconjunto exato na lista de rotas (`EmbeddedRoutePolicy.IsAllowed(..., customerOnly)`); o modo é lido a cada requisição | `Modo_CustomerOnly_*`, `No_servico_o_modo_CustomerOnly_*`, `Alterar_o_modo_*`, `O_subconjunto_da_lista_para_CustomerOnly_*` |
| 3 | Revogação: logout não derrubava ticket pendente; corrida troca × revogação deixava sessão viva; chave revogada e origem retirada não derrubavam | Marcas `sessions_revoked_at` (vínculo e conexão) conferidas na troca e a cada requisição; revogação consome tickets pendentes; `issuer_key_id`/`key_id` ligam sessão e ticket à chave; sessão exige a origem na lista | `Logout_do_ERP_*`, `Troca_e_revogacao_concorrentes_*` (25 rodadas), `Revogar_a_chave_*`, `A_rotacao_de_chave_*`, `Tirar_a_origem_*`, `A_revogacao_de_emergencia_*` |
| 4 | Versão antiga da API poderia aceitar o token embutido como interativo | Formato `ibe_` e hash `SHA-256("iab-embedded-session-v1\n"+token)`; a nova versão exige formato e tipo coincidentes | `O_token_embutido_tem_formato_e_hash_que_uma_versao_antiga_*`, `O_usuario_federado_nao_tem_senha_*`, unidade `EmbeddedHardeningTests` |
| 5 | Ticket de contexto não estava preso à sessão exata | `launch_tickets.session_id`; emissão valida a sessão; resgate exige igualdade | `Ticket_de_contexto_so_vale_na_sessao_para_a_qual_foi_emitido_*`, `Ticket_de_contexto_exige_a_sessao_*`, `Ticket_de_contexto_de_um_tenant_nunca_aponta_*` |
| 6 | Tetos de sessões e de clientes em foco furáveis por concorrência | `FOR UPDATE` no vínculo (sessões) e na sessão (clientes) | `Trocas_simultaneas_do_mesmo_usuario_externo_*` (14 trocas, teto 5), `Resgates_simultaneos_de_contexto_*` (30, teto 20) |
| 7 | Limite de taxa com cota independente por identificador de chave do cabeçalho | Cota anterior à autenticação só por origem de rede; por chave CONHECIDA (falhas) e por conexão autenticada no Identity; 429 com `Retry-After` | `Inventar_identificadores_*`, `Falhas_de_autenticacao_de_uma_chave_conhecida_*`, `Chamadas_autenticadas_de_uma_conexao_*`, unidade `So_as_falhas_*` |
| 8 | Corpo do `/embed/v1/exchange` materializado pelo framework antes do teto | Leitura com teto (8 KB) antes do JSON, também em pedaços e sem `Content-Length`; mesma regra no resgate de contexto; rotas assinadas recusam consulta | `O_exchange_recusa_corpo_acima_do_teto_*` (com e sem `Content-Length`), `O_resgate_do_contexto_tambem_tem_teto_*` |
| 9 | Caddy registrava cabeçalhos de autenticação do Embedded | `log { format filter { wrap json; fields { request>headers delete } } }`; canário em `aceitacao.sh` | `O_registro_de_acesso_do_proxy_apaga_*` (só o texto). **O comportamento implantado só se prova na VM** |
| 10 | Timestamp inválido gerava 500 | Aritmética em segundos inteiros; valor absurdo é assinatura fora da janela (401 controlado) | `Timestamp_invalido_ou_absurdo_*` (8 valores) |
| 11 | Exceções de rota pública por prefixo, métodos e canonicalização | Dispensa de contexto por caminho EXATO (e prefixo do webhook); o prefixo `/api` não é mais ignorado na assinatura; sem consulta | `A_dispensa_de_contexto_vale_so_para_o_caminho_exato_*`, `Rotas_assinadas_recusam_consulta_*`, `Sessao_embutida_nao_alcanca_nada_por_metodo_alternativo_*` |
| 12 | Corrida preexistente na redefinição de senha (500) | `RecordLoginFailureAsync`: `ON CONFLICT DO UPDATE` trava a linha na mesma instrução | `Falhas_concorrentes_com_a_limpeza_da_contagem_*` (reproduziu `Sequence contains no elements` antes da correção) |

## O que atacar (invariantes novas)

1. **Nenhum caminho de acesso a recurso do Contact Center ignora `IsOutsideEmbeddedScope`.** Procure uso de `FindMembership*`, `ListMemberships*`, `ListQueuesOfMember*`, `Principal.UserId` ou recibo (`FindOperation*`) fora de `QueueAccess`/`ResolveAccessAsync`, em qualquer serviço que a sessão embutida alcance (lista de rotas: 23). Procure também módulos fora do ContactCenter que tomem decisão por `UserId` (Conversations, Channels).
2. **Revogação é monotônica.** A marca só avança; sessão com `created_at <= marca` nunca volta a valer; ticket com `created_at <= marca` nunca troca. Tente reordenar: troca começando antes/depois da revogação, relógios com pequena diferença entre instâncias (a marca usa o relógio da instância que revoga).
3. **Tipo de token × tipo de sessão.** `ibs_` só abre sessão interativa, `ibe_` só embutida; confira `FindSessionByTokenHashAsync` (índice único global) e que nada mais lê `sessions` por hash.
4. **O ticket de contexto só serve à sessão para a qual foi emitido**, e a emissão só aceita sessão da mesma conexão e do mesmo usuário externo, ativa.
5. **Limite de taxa**: a única partição que o chamador escolhe é nenhuma antes da autenticação. Atenção a `ThrowIfKeyThrottled` (consulta, não gasta) e a `RecordKeyFailure` (só em falha); avalie se a cota por chave permite bloquear de fora a chave de um ERP legítimo sabendo só o identificador público (o custo é uma janela de 1 min por 30 falhas; o limite por origem de rede contém quem ataca de um só lugar).
6. **`SemContexto`** (host) é exato: confira que nenhuma rota pública ficou de fora e que nenhuma rota de usuário ganhou a dispensa por sufixo.

## Riscos residuais conhecidos

- O limite de taxa por chave e por conexão é em memória, por instância (um único `api` no piloto); com várias instâncias a cota multiplica.
- Atrás do Caddy, o limite por origem de rede só enxerga o cliente real com `ForwardedHeaders` ligado (ADR 0033); sem isso todos compartilham o IP do proxy.
- Nada foi provado com Docker, Caddy real, navegador real nem ERP real; o canário do Caddy é verificado só na VM.
- As marcas de revogação usam o relógio do servidor; entre instâncias com relógios muito diferentes a janela de corrida seria de alguns milissegundos.
- XSS no ERP continua sendo risco aceito (o token nunca chega à página; o verifier fica no iframe).

## Como reproduzir

```bash
dotnet test backend/IaBimeta.slnx
dotnet test tests/IaBimeta.IntegrationTests/IaBimeta.IntegrationTests.csproj --filter "FullyQualifiedName~IntegrationTests.Embedded"
dotnet test tests/identity/IaBimeta.Identity.Tests/IaBimeta.Identity.Tests.csproj
dotnet test tests/IaBimeta.IntegrationTests/IaBimeta.IntegrationTests.csproj --filter "FullyQualifiedName~LoginThrottlePersistenceTests|FullyQualifiedName~ArtefatosDeImplantacaoTests"
```

Para ver um teste de regressão falhar, desligue a correção correspondente (por exemplo, faça `AccessPolicy.IsOutsideEmbeddedScope` devolver `false`: cinco testes de `EmbeddedOrganizacaoApiTests` ficam vermelhos).

## Commits da E1-R (sobre `8cf9ffd`)

| Commit | Conteúdo | Achados |
|---|---|---|
| `7a07d91` | Corrida da redefinição de senha (`AuthenticationStore`, `LoginThrottlePersistenceTests`) | 12 |
| `c33f1b9` | Log de acesso do Caddy sem cabeçalhos; canário em `aceitacao.sh` | 9 |
| `011dac1` | Limite de organização, `CustomerOnly`, revogação, tokens `ibe_`, ticket de contexto, tetos, taxa, corpo, timestamp, rotas públicas, migration `EmbeddedHardening` e testes | 1 a 8, 10, 11 |
| (este commit) | ADR 0039, plano, `IMPLANTACAO-LINUX.md` e este pacote | documentação |

Os commits de código não compilam isoladamente em todos os casos (o `011dac1` agrupa peças interdependentes de SharedKernel, Identity, ContactCenter e Host); revise-os em conjunto.

## Resultado do gate (terceiro, sobre `011dac1`)

- Backend: integração **1919 aprovados, 14 ignorados, 0 falhas** (18 min 29 s); Identity 249; arquitetura 80; ContactCenter 164; Agents 897; WhatsApp 419; Conversations 382; Capabilities 305; demais projetos sem falha. Saída `exit=0`.
- Frontend: 458 testes e build sem falha.
- Histórico do gate: o 1.º gate teve 1 falha; o 2.º foi invalidado porque o PostgreSQL de teste (porta 5433) caiu quando a sessão foi interrompida (falhas de conexão em massa, ambientais); o 3.º passou inteiro.

## Falha intermitente ainda sem causa comprovada

No primeiro gate, `Resgates_simultaneos_de_contexto_na_mesma_sessao_respeitam_o_teto_de_clientes_em_foco` (30 resgates simultâneos) respondeu 500 em 2 das 30 chamadas. **Não reproduziu** em cerca de 16 execuções isoladas e em lote; o log do gate não registra a exceção. A hipótese (esgotamento das 100 conexões do PostgreSQL de teste) **não está provada**. O teste foi reduzido para 24 requisições e agora captura os erros do servidor (`CapturaDeErros`) para a mensagem de falha. **Se reaparecer, deve ser investigada** (olhar deadlock `40P01`, `53300` e o `FOR UPDATE` da sessão em `AddCustomerGrantAsync`), e não tratada como ruído. Existe também a instabilidade preexistente, não reproduzida, de `Usos_simultaneos_do_mesmo_token_tem_um_unico_vencedor`; sua corrida real (achado 12) foi encontrada e corrigida, mas isso não prova que seja a única causa.

## Segunda rodada da E1-R (achados da 2.ª auditoria do Codex)

Complementa os achados 3, 6 e 12 acima. Decisão e raciocínio: ADR 0039, seção "E1-R, segunda rodada".

| Achado | Causa | Correção (onde) | Teste (falha sem a correção) |
|---|---|---|---|
| Ticket de contexto emitido durante a revogação da chave foi aceito | o resgate do contexto não conferia a chave do ticket; a gravação do ticket não era atômica com o estado da chave | `EmbeddedExchangeService.RedeemContextTicketAsync` (chave, conexão, vínculo, marcas); `EmbeddedStore.AddTicketAsync` (`FOR SHARE` da chave e releitura); `EmbeddedTicketService` (chave morta não emite) | `Ticket_de_contexto_de_chave_revogada_nao_e_resgatado_*`, `Emissoes_de_contexto_concorrentes_com_a_revogacao_da_chave_*`; unidade `Chave_revogada_entre_a_autenticacao_e_a_gravacao_*` |
| Deadlock `40P01` no logout concorrente com revogação de chave | travas em ordem oposta (sessões→tickets × tickets→sessões) e `UPDATE` multilinha na ordem do plano | `EmbeddedStore`: revogação de uma linha; limpeza em transações curtas por `ORDER BY id FOR UPDATE`; ordem canônica documentada no topo da classe | `Logout_e_revogacao_de_chave_concorrentes_*` (12 rodadas, 4 operações simultâneas) |
| 500 e esgotamento de conexões sob 100 resgates | pool do Npgsql (100) igual ao `max_connections` (100); requisições seguram conexão esperando a trava | `Maximum Pool Size=30;Timeout=15` em `docker-compose.yml`; `lock_timeout` (`Identity:EmbeddedLockTimeout`); `EmbeddedBusyException` → 503 + `Retry-After` | `Trava_presa_numa_sessao_vira_503_*`, `Resgates_de_contexto_em_rajada_na_mesma_sessao_nunca_dao_500_e_o_que_nao_cabe_vira_503` (guarda: 60 resgates no pool padrão, cada resposta 200 ou 503), `O_pool_de_conexoes_e_explicito_*`, `O_tempo_maximo_de_espera_por_trava_*` |

**O que atacar na segunda rodada**

1. Qualquer caminho que trave linhas fora da ordem canônica (chave, vínculo, tickets por id, sessões por id): procure `FOR UPDATE`, `FOR SHARE` e `ExecuteUpdate` multilinha em `EmbeddedStore`, `AuthenticationStore` e `PasswordService.RevokeSessionsAsync` (esta última, de sessões interativas, é de outra família e não foi alterada).
2. A limpeza física pode ficar incompleta quando esbarra em contenção: confirme que NENHUM uso depende dela (sessão: marca e chave a cada requisição; ticket de abertura: troca; ticket de contexto: resgate).
3. `AddTicketAsync` lê o estado da chave depois da trava: confirme que a leitura é de um novo instantâneo (READ COMMITTED) e que não há outro caminho que grave `launch_tickets`.
4. A resposta 503 não revela estado (mesmo corpo para qualquer contenção) e não é emitida para falha que não seja transitória.

**Riscos residuais novos:** o `lock_timeout` de 5 s mantém até o teto de requisições esperando uma conexão (pool de 30, `Timeout=15`); sob rajada muito maior que o pool, o ERP recebe 503 e deve repetir. O pool de 30 por processo é um valor de partida para a VM do piloto, sem medição de produção. A hipótese de exaustão de conexões para a falha intermitente do primeiro gate **continua sem prova**.

**Resultado do gate da segunda rodada.** Quatro execuções anteriores falharam (2 a 4 falhas; 21 a 565 recusas do PostgreSQL; pico de até 97) por saturação das conexões do banco de teste após o teste de rajada, não por defeito de revogação. A correção foi só de infraestrutura de testes: `Maximum Pool Size=40` em `TestDatabase` (sem mexer em `max_connections`, sem poda de ociosas, sem pool privado no teste). Gate único com amostrador: integração **1925 aprovados, 14 ignorados, 0 falhas**; Identity 256, arquitetura 80 e demais projetos sem falha; **pico de 85 conexões, 0 recusas, 0 deadlocks**; frontend 458 testes e build verdes (execução anterior, sem alteração de frontend desde então). **Riscos residuais:** a folga contra o limite do servidor é estreita (85 de 97) e o teto de 40 vale por pool (EF, conexões diretas dos testes, processo do Worker), então a soma não é limitada a 40; o compartilhamento do pool entre hosts de teste foi inferido do código; o gate depende do PostgreSQL de teste com `max_connections=100`; nada foi exercitado com Docker, Caddy nem ERP real.

## Terceira rodada da E1-R (auditoria final do Codex, HEAD `a3fda12`)

Decisão, raciocínio e evidências completas: ADR 0039, seção "E1-R, terceira rodada".

| Achado | Gravidade | Correção (onde) | Teste (falha sem a correção) |
|---|---|---|---|
| Marca `SessionsRevokedAt` podia retroceder | alta | `EmbeddedStore.RevokeEmbeddedSessionsAsync`: atualização monotônica no banco (conexão e vínculo) | `A_marca_de_revogacao_nunca_retrocede_*` |
| Ticket consumido antes da transação que conclui | alta | `EmbeddedExchangeService` (lê, confere, queima só se recusa) e `EmbeddedStore.AddEmbeddedSessionAsync`/`AddCustomerGrantAsync` (consumo na mesma transação; `PeekTicketAsync` novo) | `Contencao_ao_resgatar_o_contexto_*`, `Contencao_ao_trocar_o_ticket_de_abertura_*`, unidade `Contencao_ao_concluir_a_troca_*` |
| Saturação fora dos endpoints do Identity virava 500 | alta | `TransientDatabaseMiddleware` (host, antes do `RequestContextMiddleware`); `PostgresErrors` percorre as exceções internas e reconhece o pool esgotado (`NpgsqlException`) | `Saturacao_do_pool_na_autenticacao_da_sessao_*`, `TransientDatabaseMiddlewareTests` |
| Ordem de travas rotação de chave × emissão de ticket × FK da conexão | média | `EmbeddedStore.AddTicketAsync`: conexão (`FOR KEY SHARE`) antes da chave; ordem canônica atualizada | `Emissao_de_ticket_e_criacao_de_chave_*` |

**O que atacar na terceira rodada**

1. Qualquer INSERT ou UPDATE no Embedded que tome travas implícitas de FK fora da ordem conexão, chave, vínculo, tickets, sessões.
2. `RedeemContextTicketAsync` e `ExchangeAsync`: confirmar que NENHUM caminho de sucesso consome o ticket fora da transação final e que toda recusa de conferência o queima.
3. `TransientDatabaseMiddleware.IsDefinitelyNotExecuted`: confirmar que só entram falhas em que o comando não executou (nenhum erro ambíguo, nenhuma falha de programação).
4. Monotonicidade: nenhuma outra escrita de `sessions_revoked_at` fora de `RevokeEmbeddedSessionsAsync`.

**Gate:** integração 1940 aprovados, 14 ignorados, 0 falhas; pico de 82 conexões, 0 recusas, 0 deadlocks; Identity 257; arquitetura 80. **Ressalva:** 4 testes de `Capabilities.Tests.GuardedHttpTransportTests` expiraram (5 s) durante o gate e passam isolados (305/305); causa provável contenção de CPU, não provada; módulo não alterado.

**Gate sequencial controlado (`dotnet test backend/IaBimeta.slnx -m:1`, uma execução, sem alterar código, testes nem timeouts):** 5.209 aprovados, 14 ignorados, 0 falhas em 22 min 56 s. Integração 1.940 aprovados; Capabilities 305 aprovados (3 s); Identity 257; arquitetura 80. PostgreSQL: pico de 86 de 97 conexões, 0 recusas, 0 deadlocks. CPU: pico de 100% (10 amostras de 2 s em 90% ou mais, na suíte de integração); no máximo 3 `dotnet` e 1 `testhost`. **Sobre as 4 falhas do gate paralelo em `GuardedHttpTransportTests` (timeout de 5 s):** a hipótese de contenção entre projetos simultâneos é consistente com o resultado, mas **não está comprovada** e a falha não foi reproduzida (cinco execuções isoladas e duas sob carga artificial de CPU passaram 305 de 305). O módulo Capabilities não foi alterado. **Validação pendente:** Docker, Caddy e ERP real (nunca exercitados); sem isso a E1 não pode ser homologada. A E1 segue **não aprovada** até a revisão independente final do Codex CLI.

## Quarta rodada da E1-R (auditoria final do Codex, HEAD `1a373ae`)

Decisão, alternativas e evidências completas: ADR 0039, seção "E1-R, quarta rodada".

| Achado | Gravidade | Correção (onde) | Teste (falha sem a correção) |
|---|---|---|---|
| Auditoria do sucesso gravada DEPOIS do commit: falha transitória da trilha vira 503 com o ticket já consumido (repetição = 401) | alta | `identity.audit_outbox` (migration `AuditOutbox`); `EmbeddedStore.AddEmbeddedSessionAsync`/`AddCustomerGrantAsync` gravam a linha na mesma transação; `EmbeddedExchangeService` sem gravação síncrona de sucesso; Worker (`IdentityAuditReconciler`) entrega pelo `IAuditRecoveryWriter` | `Falha_transitoria_da_auditoria_depois_da_operacao_*` (troca) e `Falha_transitoria_da_auditoria_depois_do_resgate_*` (contexto); unidade `O_sucesso_da_troca_e_auditado_pelo_outbox_*` |
| Entrega pelo menos uma vez, sem duplicar, sem remover antes da confirmação, com isolamento por tenant | requisito | `IdentityAuditOutbox.FlushAsync` (`FOR UPDATE SKIP LOCKED`, apaga só depois de a trilha aceitar) | `Workers_simultaneos_entregam_cada_linha_uma_vez_*`; reentrega da mesma linha no teste da troca |
| Rollback da operação inteira se a linha não persistir | requisito | linha na mesma transação | `A_auditoria_so_existe_se_a_operacao_confirmou_*` |
| Chave revogada entre a conferência e a gravação (hipotético) | baixa | `EmbeddedStore.TicketKeyStillValidAsync` (`FOR SHARE`, ordem canônica) | `Chave_revogada_entre_a_conferencia_e_a_gravacao_*` |
| Verifier malformado e origem inválida não queimavam o ticket | baixa | `EmbeddedExchangeService.ExchangeAsync` queima e registra com ticket identificável, mesma resposta | `Verifier_malformado_ou_origem_invalida_*`; unidade `Toda_falha_da_troca_*` (casos novos) |
| Migration e retenção | requisito | `RetentionInventory` (`LegalRecord`); migration aditiva | `A_migration_cria_e_remove_a_tabela_do_outbox_*` e o teste de inventário existente |

**O que atacar na quarta rodada**

1. Qualquer caminho de sucesso do Embedded que ainda chame o `IAuditWriter` DEPOIS de um commit com efeito (procure `_audit.Write` em `EmbeddedExchangeService`, `EmbeddedTicketService`, `IntegrationAdminService`).
2. A atomicidade: a linha do outbox só pode existir se o ticket foi consumido E a sessão (ou o cliente em foco) foi gravada, e vice-versa.
3. A entrega: nunca apagar antes da confirmação da trilha; o `id` da linha como `IdempotencyKey`; o tenant vindo do contexto do Worker; sem mistura entre tenants; com dois Workers.
4. A ordem de travas da reconferência da chave (chave, vínculo, tickets, sessões) e a ausência de ciclo com a criação de chave.
5. A uniformidade da resposta 401 no malformado (sem oráculo entre ticket desconhecido, queimado e malformado).

**Gate sequencial da quarta rodada (`dotnet test backend/IaBimeta.slnx -m:1`, uma execução):** 5.219 aprovados, 14 ignorados, 0 falhas em 22 min 18 s. Integração 1.947; Capabilities 305; Identity 260; arquitetura 80. PostgreSQL: pico de 79 de 97 conexões, 0 recusas, 0 deadlocks. CPU: pico de 98%, média de 32,6%. **Riscos residuais:** a auditoria de sucesso chega à trilha com atraso de até um ciclo do Worker; o Worker precisa estar no ar (as linhas ficam como registro de referência, sem expurgo); perda da resposta HTTP depois do commit exige novo ticket (contrato do cliente); recusas continuam com auditoria síncrona; conexão e vínculo desativados seguem conferidos por requisição, não dentro da transação; Docker, Caddy e ERP real nunca foram exercitados e essa validação é obrigatória antes de qualquer homologação. A E1 segue **não aprovada** até a próxima revisão independente.
