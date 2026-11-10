# Enviar imagem, documento e áudio ao cliente (ADR 0031)

O atendente que assumiu uma conversa de WhatsApp pode enviar **uma imagem, um documento ou um áudio**, com legenda quando o tipo a aceita. O envio é uma **resposta como o texto**: mesma autorização, mesma idempotência, mesmo outbox e mesmo Sender. A IA **nunca** envia arquivo e **nunca** vê o arquivo, o nome nem a legenda. Este guia é para quem opera e homologa; as decisões estão no ADR 0031.

## 1. O que o atendente faz

1. Na conversa que assumiu e com a **janela de 24 horas aberta**, clica em **Anexar arquivo** e escolhe o arquivo.
2. O cartão mostra o nome, o tipo, o tamanho e, para imagem e documento, o campo de **legenda** (até 1024 caracteres). Áudio **não tem legenda**.
3. Clica em **Enviar arquivo**. A mensagem aparece no histórico com o estado de entrega de sempre (**enviando, enviado, entregue, lido**) e o arquivo pode ser aberto ali.
4. **Fora da janela de 24 horas** o arquivo, como o texto livre, não é aceito (`ReplyWindowClosed`): só um **modelo de mensagem aprovado** (`MODELOS-DE-MENSAGEM.md`).

O texto digitado no campo de resposta **não** vai junto com o arquivo e **não** se perde.

## 2. O que é aceito

| Tipo | Formatos | Máximo (padrão) | Legenda |
|---|---|---|---|
| **Imagem** | JPEG, PNG | 5 MB | sim (até 1024) |
| **Áudio** | AAC, AMR, MP3, M4A, **Ogg com Opus em mono** | 16 MB | **não** |
| **Documento** | PDF, TXT, Word (doc, docx), Excel (xls, xlsx), PowerPoint (ppt, pptx) | 20 MB | sim (até 1024) |

O servidor decide pelo **conteúdo** do arquivo, não pelo nome nem pelo tipo que o navegador informa: um PDF com nome de imagem, um executável renomeado para `.png`, um pacote do Word declarado como planilha ou um script com extensão `.txt` trocada são **recusados**. WebP, vídeo, figurinha, ZIP, HTML, SVG e executáveis **não** são enviados. A tela confere tamanho e tipo antes de enviar, mas só como aviso: a decisão é do servidor.

### Recusas e o que fazer

| Motivo (`reason`) | Mensagem ao atendente | O que fazer |
|---|---|---|
| `empty` | O arquivo está vazio. | Escolher outro arquivo. |
| `too-large` | O arquivo é maior que o limite para este tipo. | Reduzir ou dividir o arquivo. |
| `type-not-allowed` | Este tipo de arquivo não pode ser enviado. | Converter para um formato aceito (por exemplo, PDF). |
| `content-mismatch` | O conteúdo não corresponde ao tipo ou à extensão. | Salvar o arquivo de novo no formato certo; não renomear. |
| `audio-codec-unsupported` | O áudio Ogg precisa usar Opus, em mono. | Gravar de novo ou converter para MP3, M4A ou AAC. |
| `caption-not-supported` | Áudio não aceita legenda. | Enviar o áudio sem legenda e o texto à parte. |
| `caption-too-long` | A legenda passa de 1024 caracteres. | Encurtar. |
| `malware` | Bloqueado pela verificação de segurança. | (só com um mecanismo de varredura ligado) |

A recusa responde `422` (`code: MediaRejected`, `reason`) e **nada** é guardado nem enviado. `413` é o pedido grande demais (teto de 25 MB do formulário) e `400` é formulário inválido.

## 3. Por onde passa e o que pode dar errado

```text
Anexar  ▶  Conversations valida e guarda  ▶  mensagem + arquivo + evento (uma transação)  ▶  Sender  ▶  Meta: (1) sobe o arquivo  (2) envia a mensagem
```

| Situação | O que acontece | O atendente vê |
|---|---|---|
| Tudo certo | a Meta aceita; o status volta pelo webhook | enviado, depois entregue e lido |
| A subida falha de forma passageira (5xx, rede, tempo, limite de taxa) | **nada saiu**: o Sender tenta de novo (até 3 vezes, com a espera da Meta) e sobe o arquivo de novo | enviando… depois enviado, ou **falha no envio** (`send-failed.upload-*`) se esgotar |
| A Meta recusa o arquivo (por exemplo, `131053`, tipo que não confere) | falha definitiva visível, **nenhuma mensagem sai** | **falha no envio**: envie o arquivo de novo |
| Erro de conta (token inválido) | a conta é **pausada** (`channels`), a mensagem falha; as seguintes falham sem tocar na Meta | **falha no envio** e o alerta de número pausado |
| A mensagem (2ª etapa) fica em dúvida (tempo, rede, 5xx) | **`SendUnknown`: nunca é reenviada** | **envio de resultado incerto**: confira com o cliente antes de reenviar |
| A Meta diz que a janela fechou (`131047`) | falha definitiva | **falha no envio** |
| O Worker cai no meio (na subida ou na mensagem) | **nunca reenvia**: vira `SendUnknown` | **envio de resultado incerto** |
| O arquivo já não existe quando o Sender chega (prazo de guarda vencido) | falha visível, **sem chamar a Meta** | **falha no envio** (`attachment-unavailable`) |

Para **reenviar**, o atendente escolhe o arquivo de novo: é uma resposta nova (outro pedido), que recolhe o aviso de falha. O reenvio automático de mensagem **não** vale para arquivo.

Diagnóstico: `status` (`OPERACAO-E-SUPERVISAO.md`) e a lista de mensagens sem desfecho mostram o que está preso; o motivo da falha aparece como `send-failed.<código>` (`upload-timeout`, `upload-network-error`, `upload-http-503`, `upload-invalid-response`, `upload-131053`, `attachment-unavailable`, `account-error`, `account-paused`, `window-closed`...). Nenhum conteúdo, legenda, nome de arquivo nem token vai a log.

## 4. Onde o arquivo fica e por quanto tempo

- Na **pasta privada** do servidor (`Conversations:Attachments:RootPath`, a mesma do arquivo recebido; **Api e Worker apontam para a mesma pasta**), em `{tenant}/{arquivo}.bin`. Não existe URL pública nem permanente; o atendente abre pelo Api com a sessão dele.
- **Prazo de guarda:** **30 dias** (`Retention:Days:OutboundAttachments`; por empresa em `Retention:Tenants:<slug>`). Passado o prazo, o **expurgo técnico** (`retention purge --confirm` ou o agendamento do Worker, `RETENCAO-DE-DADOS.md`) **apaga o objeto**; o histórico mantém a mensagem, o **nome** e a **legenda** e diz que o arquivo foi apagado.
- A mesma rotina recolhe **objetos órfãos** (gravados, sem linha de arquivo, mais antigos que o prazo), que só nascem se o processo cair no meio de um envio.
- O arquivo **recebido do cliente** tem o mecanismo de prazo de guarda (`InboundAttachments`, ADR 0032), **desligado até o proprietário decidir o prazo**. A pasta entra no backup com `Backup-Banco.ps1 -PastaDeAnexos` (`BACKUP-E-RESTAURACAO.md`).
- **Segurança do arquivo enviado (ADR 0032):** com a verificação exigida, o arquivo do atendente só é guardado e enviado se o verificador o declarou limpo (infectado: `422` com o motivo `malware`; verificador fora do ar: `503` com `scanner-unavailable`, sem guardar nem enviar nada e repetindo o mesmo pedido com segurança); conta na **cota da empresa** (`quota-exceeded`); fica **cifrado em disco**. Configuração e operação em `SEGURANCA-DOS-ARQUIVOS.md`.

Configuração (todas as chaves já existiam, exceto a categoria de retenção):

| Chave | Padrão | O que é |
|---|---|---|
| `Conversations:Attachments:RootPath` | `LocalApplicationData/IaBimeta/attachments` | pasta privada; a mesma no Api e no Worker |
| `Conversations:Attachments:MaxImageBytes` / `MaxAudioBytes` / `MaxDocumentBytes` | 5 MB / 16 MB / 20 MB | máximo por tipo (valem para o envio e para a recepção) |
| `Conversations:Attachments:AllowedImageTypes` / `AllowedAudioTypes` / `AllowedDocumentTypes` | os padrões | listas de tipos; o envio só usa o subconjunto que a Cloud API aceita |
| `Retention:Days:OutboundAttachments` | 30 | prazo de guarda do arquivo **enviado** |
| `Channels:Providers:WhatsApp:MediaTimeout` | 60 s | tempo limite da **subida** do arquivo (e dos downloads) |
| `Channels:Providers:WhatsApp:RequestTimeout` | 30 s | tempo limite da etapa da **mensagem** |

Subir `MaxDocumentBytes` acima de 20 MB exige subir também o teto do pedido (25 MB, constante `MaxReplyMediaRequestBytes` do ContactCenter) e o limite de corpo do servidor.

## 5. Simulação local (sem Meta nem OpenAI reais)

`tools\dev\Start-WhatsAppSimulado.ps1` é a "Graph API" local: aceita a **subida** (`POST /{versão}/{número}/media`, exigindo o token da conta no cabeçalho, que nunca é impresso), guarda o arquivo em `-PastaDeEnvios` e **imprime o que o cliente receberia**: `[IMAGE]`, `[AUDIO]` ou `[DOCUMENT]` com tipo, tamanho, nome e legenda. O Worker aponta para ela por `Channels__Providers__WhatsApp__BaseAddress = http://127.0.0.1:5198/`. Os testes automáticos usam o `ServidorGraphLoopback` (HTTP de verdade, em porta local).

## 6. Homologação com a Meta (pendências)

Antes de operar com a Meta real, em uma conta de teste: (1) enviar uma imagem JPEG, um PNG, um PDF e um áudio Ogg Opus mono **dentro da janela** e conferir o recebimento no celular, a legenda, o nome do documento e o status (`sent`, `delivered`, `read`); (2) provocar cada recusa da subida (tipo errado, Ogg que não é Opus, imagem acima de 4096 px) e registrar o **estado HTTP e o código** reais, ajustando `WhatsAppSendResponseReader.ReadUpload` se preciso; (3) confirmar que o identificador do arquivo subido vale 30 dias e que o envio logo depois da subida funciona; (4) decidir antivírus, criptografia em repouso, cota e backup da pasta. A lista completa está no ADR 0031 ("O que fica fora").

## 7. Demonstração local pela interface (09/11/2026)

Procedimento para repetir, sem nenhuma chamada paga: o **mesmo ambiente** de `HOMOLOGACAO-DO-CONHECIMENTO.md` (seções 1 a 3: PostgreSQL descartável na porta 5441, `provision` de uma empresa com a Meta em modo `simulated`, OpenAI e Meta simuladas, Api, Worker e Vite), com duas diferenças: **Api e Worker recebem a MESMA pasta de arquivos** (`Conversations__Attachments__RootPath`) e a chave do hash das lacunas não é necessária.

Roteiro: (1) o cliente escreve (`Send-MensagemParaEmpresa.ps1 -Texto 'Quero falar com um atendente ...'`) e a IA simulada transfere; (2) a atendente entra na interface, abre a conversa e **assume**; (3) **Anexar arquivo**: uma imagem PNG com legenda, um PDF `Laudo março.pdf` com legenda e um áudio Ogg Opus (sem campo de legenda); (4) um executável com nome e tipo de imagem; (5) conferir o terminal da Meta simulada, o banco, a pasta privada, os logs e `retention report`.

| Passo | Resultado |
|---|---|
| Atendente assume | aparece **Anexar arquivo** e a dica dos limites; com o campo de texto e o botão Responder como antes |
| Imagem com legenda | o cartão mostra nome, tipo e tamanho e o campo de legenda; **Enviar arquivo** ⇒ o histórico mostra a miniatura (baixada com a sessão da atendente), a legenda e **enviado**; a Meta simulada imprime `[upload] ... image/png (token da conta no cabeçalho: sim)` e `[IMAGE] ... legenda: ...` |
| PDF com legenda | nome com acento preservado no histórico e na mensagem; a subida usa o nome genérico `upload.pdf`; **Baixar** e **enviado** |
| Áudio | o cartão mostra "Áudio não aceita legenda" e não há campo; o histórico traz "Carregar áudio" e o player abre com a sessão da atendente |
| Executável com nome de imagem | a checagem do navegador (consultiva) deixa passar; **o servidor recusa pelo conteúdo** (`422 MediaRejected`, `type-not-allowed`) e a tela explica ao lado do cartão **sem perder o arquivo**; nada foi guardado nem subido |
| Banco | as mensagens têm `kind` imagem, documento e áudio, **texto nulo**, arquivo `direction = 2`, `Available`, tipo verificado, nome saneado, legenda, sem identificador no provedor e com objeto guardado |
| Pasta privada | um objeto por arquivo, em `{tenant}/{arquivo}.bin`, sem nome nem tipo no caminho |
| Logs do Api e do Worker | nenhuma legenda, nome de arquivo, token ou segredo |
| `retention report` | a tarefa `conversations.outbound-attachment-objects [padrão, 30 d]` aparece, sem nada elegível (os arquivos têm minutos) |

**Defeito encontrado e corrigido:** o leitor de formulário do simulador local da Meta (`Start-WhatsAppSimulado.ps1`) só aceitava `name="campo"` com aspas e rejeitava a subida do adaptador (o .NET escreve `name=campo` sem aspas): as três mensagens ficaram `Failed` na primeira execução, e o Sender se comportou como previsto (subida recusada, nenhuma mensagem enviada, falha visível). O leitor aceita agora as duas formas. **Limite da demonstração:** o painel do navegador não abre o seletor nativo de arquivos; o arquivo foi colocado no campo `input[type=file]` por script, e o restante (cartão, legenda, envio, recusa, histórico, miniatura e player) foi feito pelos componentes reais, com Api, Worker e PostgreSQL reais e a Meta e a OpenAI simuladas. Nenhuma chamada real à Meta ou à OpenAI.
