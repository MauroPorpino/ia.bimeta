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
