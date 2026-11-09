# ADR 0028 — Mídia do WhatsApp (imagem, áudio e documento): recebimento, armazenamento privado e visualização pelo atendente

- **Status:** **Aceito para o MVP (ratificado pelo decisor em 09/11/2026).** Implementado em 09/11/2026 sob a autorização do decisor ("suporte mínimo a mensagens multimídia do WhatsApp: imagens, áudios e documentos"), depois da ratificação do ADR 0027. Nenhuma chamada real à Meta ou à OpenAI; nenhum banco existente foi apagado (a migração só **cria** uma tabela; backup do banco de teste feito antes); `main` intocada; sem push.
- **Data:** 09/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md`, 0006 (Receiver, Inbox e o `expires_at`), 0007 e `CONVERSATION-LIFECYCLE.md` §10 (conteúdo pendente), 0008 e 0009 (ligações mediadas pelo host), 0010 (fronteira histórica do atendimento), 0011 (Channels, DP-04), 0017 e 0018 (Agents e fluxo do MVP), 0020 (relay: mortos tratados), 0022 e 0023 (interface do atendente), 0025 (retenção e backup), `docs/operations/MIDIA-DO-WHATSAPP.md`
- **Preserva:** nenhuma aresta nova na matriz de dependências (nem edição de `BOUNDARIES.md`, `ARCHITECTURE.md` ou `module-dependencies.json`); tenant e permissões só pelo `RequestContext`; o Receiver continua sem chamar a Meta, a IA ou o Conversations no caminho do webhook; **nenhum arquivo, nem texto dele, vai a um provedor de IA**; o token da conta continua só nos cabeçalhos das chamadas ao provedor; nenhum segredo em log, resposta ou tela.

## Contexto

Até aqui a mensagem de mídia do cliente chegava como metadados (`media_id`, tipo, legenda) num `ChannelData` e o atendente via "mídia recebida (arquivo indisponível no MVP)". Esta decisão dá o **suporte mínimo a receber e visualizar**: o cliente manda imagem, áudio ou documento; o sistema busca o arquivo na Meta, valida, guarda em armazenamento **privado** e o atendente autorizado o vê, ouve ou baixa na interface React.

**Fora do escopo (decidido):** envio de arquivos pelo atendente; transcrição de áudio; reconhecimento de imagem, OCR ou IA multimodal; vídeo, figurinha e demais tipos (seguem como mídia sem arquivo: política de conteúdo pendente de sempre); armazenamento em nuvem real (S3, Azure Blob); miniatura gerada no servidor; antivírus.

## Decisão

### 1. Fluxo e responsabilidades

1. **Receiver** (sem mudança): valida a assinatura, resolve canal e tenant, deduplica, grava no Inbox e responde 200. O parser lê só **metadados de texto** (`media_id`, `mime_type`, `filename`, `caption`, `voice`); a `url` e o `sha256` do webhook **não são lidos** (a documentação diz que o endereço do webhook não é confiável).
2. **Inbox handler (Channels, Worker):** entrega a mensagem ao Conversations com `Attachment` (`InboundAttachment`: identificador no provedor, tipo declarado, nome, legenda). O Conversations grava a mensagem e o registro do arquivo (**`Pending`**) **na mesma transação**; o atendente já vê "recebendo". O Channels então grava, no **próprio outbox**, o job `channels.media-fetch-requested` (EventId determinístico por mensagem; faixa própria `media:{messageId}`), **inclusive quando a ingestão é duplicata**: se o processo caiu entre a mensagem e o job, a reentrega do evento recria o job sem duplicar nada.
3. **`MediaFetchRequestedHandler` (Channels, Worker):** pede ao Conversations o **plano** (identificador, tipo declarado, tamanho máximo e tipos permitidos **do tipo de mídia**), baixa pelo `IChannelProviderAdapter.DownloadMediaAsync` com o token da conta lido na hora, e entrega os bytes (`CompleteAsync`) ou registra por que não deu (`FailAsync`). É idempotente: arquivo já resolvido não é baixado de novo; reentrega do relay ou reinício do Worker termina no mesmo estado.
4. **Conversations (dono do arquivo):** `AttachmentService` valida (seção 3), guarda e muda o estado. Arquivo **nunca** sai de `Pending` mais de uma vez.
5. **ContactCenter:** `GET /contact-center/items/{id}/messages/{messageId}/attachment` abre o arquivo **só** para quem veria aquela mensagem no histórico do item (seção 5). **Interface React:** miniatura, player, download (seção 6).

### 2. Download pelo adaptador (sem expor credencial)

`DownloadMediaAsync` faz **duas** chamadas `GET`: os dados da mídia pelo identificador (`/{versão}/{media-id}`: endereço do conteúdo, tipo, resumo, tamanho) e o conteúdo no endereço devolvido, ambas com `Authorization: Bearer` **só no cabeçalho** (como a documentação exige). Regras:

- O endereço do conteúdo vem na resposta do provedor, então o token **só vai** a: o endereço da Graph API configurada (mesmo esquema e autoridade) ou **HTTPS de um domínio de uma lista** (o próprio domínio ou subdomínio, porta padrão), sem usuário, sem fragmento, nunca endereço IP. Lista padrão: `fbsbx.com`, `facebook.com`, `fbcdn.net`, `whatsapp.net`, `whatsapp.com`; configurável (`Channels:Providers:WhatsApp:MediaHostSuffixes`). Fora disso: `Rejected`/`media-url-not-allowed` e **o segundo pedido (com o token) nunca sai**.
- **Sem redirecionamento** (`AllowAutoRedirect = false`; 3xx é recusa). O tamanho é conferido **antes** (o declarado) e **durante** a leitura (o medido), em blocos: nunca mais que o limite entra na memória. Tempo limite por etapa (`MediaTimeout`, 60 s).
- O identificador da mídia e o token têm formato conferido antes de qualquer chamada (o identificador entra no caminho da URL; o token entra num cabeçalho).
- O desfecho vem do **estado HTTP** e do `error.code`, nunca de texto do provedor: 404 e 410 = expirada (definitivo); 401, 403 e os códigos de credencial = erro da conta (repete com espera, o administrador pode corrigir a tempo); 5xx, 429, rede e tempo = passageira (repete); o resto = recusa definitiva. **Nenhum texto do provedor** vai a log, resultado ou tela.
- O adaptador **continua sem log, sem SDK e com o token lido em um só arquivo** (a fábrica do pedido); `new HttpRequestMessage(` e `HttpMethod.Get` aparecem **só** na fábrica.

### 3. Armazenamento seguro e validação do conteúdo

- **Porta `IAttachmentContentStore`** (`WriteAsync` e `OpenReadAsync`, por tenant e arquivo, **sem URL**): trocar por S3 ou Azure Blob é trocar a implementação. A de hoje é a pasta privada do servidor (`Conversations:Attachments:RootPath`, padrão `LocalApplicationData/IaBimeta/attachments`): objeto `{tenant}/{arquivo}.bin`, **chave gerada** (nunca texto do cliente), gravação atômica (temporário e renomeação, com nova tentativa curta no Windows quando dois Workers trocam o mesmo destino ao mesmo tempo) e idempotente por arquivo, e a leitura confere a **forma exata da chave e o tenant** e que o caminho resolvido fica **dentro da raiz**. Api e Worker precisam apontar para a **mesma** pasta (o Worker guarda, o Api serve).
- **Política antes de guardar** (`AttachmentPolicy`, sem I/O): vazio recusado; **tamanho máximo por tipo** (padrões do WhatsApp: imagem 5 MB, áudio 16 MB, documento 20 MB; teto absoluto 100 MB; `MaxImageBytes`, `MaxAudioBytes`, `MaxDocumentBytes`); **tipos permitidos por tipo de mídia** (listas configuráveis, só tipos que a validação sabe conferir); **assinatura do conteúdo** conferida contra o tipo (`ContentSniffer`: PNG, JPEG, WebP, Ogg, AMR, MP4, MP3, AAC, PDF, OLE, pacote OOXML, texto sem byte nulo); **resumo SHA-256** conferido contra o que o provedor informou (hexadecimal ou Base64); **nome saneado** (só o último trecho, sem controle, separador, inversão de texto nem caractere que o sistema de arquivos recusa; 120 caracteres) e **legenda saneada**. O tipo efetivo é o que o provedor informou ao entregar (vale sobre o declarado na mensagem). Motivos gravados como código fixo: `empty`, `too-large`, `type-not-allowed`, `content-mismatch`, `hash-mismatch`, `malware`.
- **Varredura de código malicioso:** há o ponto de ligação (`IAttachmentScanner`, padrão `NotScanned`), **sem mecanismo no MVP**. A proteção do MVP é a política de tipos, a assinatura, o limite de tamanho, o isolamento por tenant e a entrega ao navegador **sem execução** (seção 5). SVG, HTML, executáveis e arquivos compactados **não são tipos permitidos**.
- **Tabela `conversations.message_attachments`** (migração `MessageAttachments`, só cria a tabela; `Down` a remove): um arquivo por mensagem (índice único); chave estrangeira composta `(tenant_id, message_id)` (o banco recusa arquivo ligado à mensagem de outro tenant); `CHECK` do estado e de que **só o disponível tem chave, tipo verificado, tamanho e resumo** e o indisponível ou recusado tem o motivo. A chave de armazenamento e o identificador do provedor **nunca** saem do módulo: `AttachmentView` (estado, tipo verificado, nome, tamanho, motivo, legenda) é o que o histórico mostra.
- **Estado do arquivo ≠ estado do conteúdo.** `ContentState` é a interpretação do texto para a IA (o MVP não transcreve): a mídia **segue `Pending`** para a IA mesmo com o arquivo disponível ao atendente.

### 4. Conversations: contratos aditivos

`IngestInboundMessageCommand.Attachment` (último parâmetro, opcional); `MessageView.Attachment`; `GetHistoryQuery.MessageId` e `ThroughMessageSeq` (opcionais); contratos novos `IAttachmentCommands` (plano, concluir, falhar; contexto de **sistema**) e `IAttachmentQueries` (abrir; exige leitura da conversa na organização da sessão **e** que a mensagem seja **da sessão informada**). O histórico, a paginação e a releitura periódica trazem o arquivo de cada mensagem com o estado dele; **nenhuma interface fixada foi alterada** (o ContactCenter usa uma porta própria, `IAttachmentGateway`, ligada pelo host ao Conversations com o contexto de sistema do **mesmo** tenant, como o ADR 0009).

### 5. ContactCenter: servir o arquivo

`QueueItemService.OpenAttachmentAsync` reaproveita **as mesmas regras do histórico** (acesso ao item, período, colocação, fronteira do atendimento, ADR 0010): só abre o arquivo de uma mensagem que o histórico mostraria **àquele usuário agora**, e só se `Available`. O endpoint devolve o fluxo com **tipo verificado**, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox`, `Cross-Origin-Resource-Policy: same-origin`, `Cache-Control: private, no-store`, `Content-Disposition` `inline` só para imagem e áudio (o resto é `attachment`, com nome em `filename*`). **Não existe URL pública nem permanente**: sem sessão, 401; outro tenant, outra organização, mensagem de outro atendimento ou inexistente, 404 ou 403 sem corpo do arquivo.

### 6. Interface React

Em cada mensagem de mídia: **"Recebendo imagem/áudio/documento…"** enquanto `Pending`; **miniatura** da imagem (baixada sozinha, com no máximo 4 ao mesmo tempo; clicar amplia); **player de áudio** nativo (baixa só quando o atendente pede); **documento** com nome, tipo, tamanho e botão **Baixar**; **legenda** do cliente. O arquivo é baixado com a sessão (`Authorization`) como **Blob local**, exibido por `blob:` e liberado ao sair; tipos que não são imagem nem áudio **nunca** são exibidos na página. `Unavailable` e `Rejected` mostram o motivo em linguagem clara ("o arquivo expirou no WhatsApp…", "bloqueado: o conteúdo não corresponde ao tipo informado…"), nunca texto do provedor. A releitura periódica **traz a mudança de estado do arquivo** (antes comparava só texto, conteúdo e entrega). Responsivo (sem rolagem horizontal em 375 px).

### 7. Agents: nada de arquivo para a IA

Nenhuma linha do Agents mudou. A política de conteúdo pendente (MED-1 a MED-4, `CONVERSATION-LIFECYCLE.md` §10) já cobre o caso: mídia fica com `ContentState = Pending`, o prompt **só inclui texto `Ready`**, e vencida a espera por tipo: **áudio** → resposta informativa aprovada do tenant (ou o atendente, sem texto aprovado); **imagem, documento e demais** → encaminhamento ao atendente (`content-unavailable`), **inclusive a legenda** (o grupo todo segue a política mais restritiva). Foi provado de ponta a ponta que o modelo recebe **zero** pedido para mídia nova.

### 8. Simulação e demonstração

`ServidorGraphLoopback` serve a consulta de dados e o conteúdo da mídia (exigindo o token no cabeçalho) com comportamentos programáveis (expirada, resumo errado, tamanho declarado, falha passageira, resposta presa). `Start-WhatsAppSimulado.ps1` serve os arquivos da pasta de mídia (e responde 404 para a marca de expirada); `Send-MensagemWhatsApp.ps1 -Midia imagem|audio|documento` gera o arquivo sintético (PNG, MP3 silencioso de 3 s, PDF de uma página) ou usa `-Arquivo`, `-Legenda`, `-NomeDoArquivo` e `-Expirada`.

## Regras de arquitetura alteradas (testes fixados)

1. **Adaptador do WhatsApp** (`AdaptadorWhatsAppArquiteturaTests`): "nunca baixa mídia" passou a "só baixa mídia pelo identificador, com endereço validado". São **3** chamadas `_http.SendAsync(` (envio, sonda e mídia), `new HttpRequestMessage(` só na fábrica, e `IsAllowedMediaUrl` obrigatório no download. O parser continua **sem ler** `url` e `sha256`. O token continua lido em **um** arquivo.
2. **Conversations** (`ServiceGuardsTests`): a única classe `*Options` do assembly passou a ser `AttachmentOptions` (configuração do armazenamento, não de comportamento de aceitação de resposta; as regras fixas de consistência e aceitação continuam sem chave por tenant).

## O que foi conferido na documentação oficial (09/11/2026) e o que não foi

Conferido: o identificador da mídia recebida vale **7 dias**; o endereço de download vale **5 minutos** e exige o token no cabeçalho; limites de tamanho (imagem 5 MB, áudio 16 MB, documento 100 MB); a consulta devolve `url`, `mime_type`, `sha256`, `file_size`, `id`; o webhook de mídia traz `id`, `mime_type`, `sha256` e conforme o tipo `caption`, `filename` e `voice`. **Não afirmado nas páginas lidas** (implementado de forma conservadora e a confirmar na homologação): **os domínios** do endereço de conteúdo (os exemplos usam `lookaside.fbsbx.com`); o **formato do `sha256`** (aceitamos hexadecimal e Base64; formato desconhecido não bloqueia, só deixa de conferir); se a `url` do webhook pode existir (não é lida de propósito); o comportamento quando o endereço expira no meio de uma tentativa (tratado como 404/expirada).

## O que fica fora (pendências para a homologação com a Meta real)

1. Baixar **arquivos reais** de cada tipo (imagem, nota de voz em Ogg/Opus, PDF, planilha) e conferir **o domínio do endereço de conteúdo**, o formato do resumo e o tipo devolvido; ajustar `MediaHostSuffixes` se preciso.
2. **Antivírus**: nenhum mecanismo; decidir se é exigido antes da operação real e ligar o `IAttachmentScanner`.
3. **Armazenamento em nuvem** e **criptografia em repouso** da pasta de arquivos; hoje a pasta é do disco do servidor, **fora do backup do banco** e **fora do expurgo técnico** (RETENCAO-DE-DADOS.md): definir prazo de guarda e rotina de cópia e apagamento dos arquivos.
4. **Retenção e direito de apagamento** de arquivos de cliente (a tabela é "pessoal" no inventário de retenção; o objeto no disco ainda não é apagado por nenhuma rotina).
5. Vídeo, figurinha e outros tipos; miniaturas geradas no servidor (hoje a imagem inteira, até 5 MB, é baixada pelo navegador); transcrição de áudio e leitura de imagem (IA multimodal) com consentimento e política própria.
6. Cota de armazenamento por tenant e limite de arquivos por conversa (hoje só o limite por arquivo).
7. ~~Envio de arquivos pelo atendente.~~ Feito em 09/11/2026 no ADR 0031 (Proposto): `docs/operations/ENVIO-DE-ARQUIVOS.md`.

## Consequências

- O atendente vê e usa o que o cliente enviou, sem sair da interface. A IA **não** interpreta arquivo: mídia nova vai ao atendente (imagem e documento) ou recebe a resposta informativa (áudio).
- **Risco aceito:** sem antivírus, um arquivo malicioso **do tipo permitido e com a assinatura certa** (por exemplo, um PDF com código) é guardado e pode ser baixado pelo atendente. Mitigações: tipos restritos, nada executável, entrega sem execução (`nosniff`, `sandbox`, `attachment` para o que não é imagem ou áudio) e o navegador é quem abre o arquivo baixado. A decisão de exigir varredura antes da operação real é do decisor.
- **Risco aceito:** o arquivo fica no disco do servidor sem criptografia própria e fora do backup do banco: perder o disco perde os arquivos (a mensagem e o estado ficam no banco, com o arquivo marcado como disponível). Mitigação: pendência 3.
- Cada mensagem de mídia gera **um job** no outbox do Channels e **dois** pedidos à Meta (dados e conteúdo); o relay trata a repetição. O identificador vale 7 dias: depois disso, só o atendente pedir ao cliente que reenvie.
- A tela baixa as miniaturas com a sessão do atendente (não há URL assinável): um histórico longo com muitas fotos faz mais pedidos ao Api (limitado a 4 simultâneos).

## Plano de reversão

A migração só **cria** `conversations.message_attachments` (`Down` a remove). Reverter o código não perde dado do Conversations (a coluna do `ChannelData` com os metadados da mídia segue como antes); os objetos em disco ficam órfãos e podem ser apagados com a pasta. Sem o registro do arquivo, a mensagem volta a mostrar "mídia recebida (arquivo não disponível)".

## Testes

Unidade: `AttachmentPolicyTests` (54: tamanho, tipo, assinatura de cada formato, resumo, nome, legenda, opções e ciclo do domínio), `MediaDownloadTests` (59: duas chamadas, token só no cabeçalho, domínios permitidos e recusados, limites declarado e medido, cada estado HTTP, tempo e cancelamento), arquitetura do adaptador. Integração com PostgreSQL real e pasta privada de teste: `AnexosDeMidiaTests` (nascimento junto da mensagem, duplicata, conclusão, reinício no meio da gravação, cada recusa, isolamento entre tenants, autorização por organização e por sessão, paginação com arquivo, garantias do banco) e `ArmazenamentoDeAnexosTests` (chave adulterada, tenant, atomicidade, concorrência). Ponta a ponta com Api e Worker reais e a Graph API simulada por HTTP (`MidiaDoWhatsAppTests`): imagem, áudio e documento por webhook assinado e abertura pelo Api com os mesmos bytes e os cabeçalhos de entrega; webhook repetido (uma consulta, um download, um objeto); sete arquivos inválidos (conteúdo diferente do tipo, grande demais, expirado, resumo errado, tipo proibido, endereço fora da Meta); falha passageira que repete e conclui; falha persistente que vira "indisponível"; **Worker derrubado no meio do download**; outra empresa, outra organização e visitante sem login; cliente novo que manda imagem, documento ou áudio sem nenhum pedido ao modelo. Interface: 41 testes novos (Vitest).
