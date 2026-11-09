# Mídia do WhatsApp: imagem, áudio e documento

Como o ia.bimeta **recebe** os arquivos que o cliente manda pelo WhatsApp e como o atendente os **vê, ouve e baixa** (ADR 0028). Só **recebimento e visualização**: o atendente **não envia** arquivos, e não há transcrição de áudio, reconhecimento de imagem, OCR, IA multimodal nem armazenamento em nuvem.

## 1. O que o atendente vê

Na conversa, cada mensagem de mídia mostra o arquivo no próprio histórico (também na paginação e na releitura periódica):

| Estado | O que aparece |
|---|---|
| Recebendo | "Recebendo imagem/áudio/documento…" (o servidor ainda busca o arquivo na Meta) |
| Imagem disponível | **miniatura** (clique para ampliar), nome e tamanho, legenda do cliente |
| Áudio disponível | botão **Carregar áudio** e então o **player** (o áudio só é baixado quando se pede) |
| Documento disponível | nome, tipo, tamanho e botão **Baixar**, legenda do cliente |
| Indisponível | "O arquivo expirou no WhatsApp…" ou "Não foi possível baixar… depois de várias tentativas" |
| Bloqueado | o motivo: grande demais, tipo não permitido, conteúdo diferente do tipo informado, integridade não confirmada |

Mensagens de mídia **antigas** (de antes desta função) continuam como "mídia recebida (arquivo não disponível)": o arquivo nunca foi guardado.

## 2. Como funciona (resumo)

1. A Meta avisa pelo webhook assinado; o Receiver só grava no Inbox (sem baixar nada).
2. O Worker grava a mensagem (arquivo **pendente**) e cria o job de busca. O arquivo é pedido à Meta em **duas chamadas** (dados e conteúdo), com o token da conta **só no cabeçalho**.
3. O Conversations **valida** (tamanho, tipo, assinatura do conteúdo, resumo SHA-256, nome) e só então guarda, em **pasta privada**, com chave gerada. O estado vira disponível, indisponível ou bloqueado.
4. O Api serve o arquivo **só** a quem veria aquela mensagem no histórico (`GET /contact-center/items/{id}/messages/{messageId}/attachment`), sem URL pública e sem cache.

**A IA nunca recebe o arquivo nem a legenda.** A política de conteúdo pendente continua valendo: **áudio** recebe a resposta informativa aprovada do tenant (ou vai ao atendente, se não houver texto aprovado); **imagem e documento** vão ao atendente com o motivo "conteúdo indisponível para a IA".

## 3. Configuração

Seção `Conversations:Attachments` (Api **e** Worker, **a mesma pasta nos dois**) e `Channels:Providers:WhatsApp`:

| Chave | Padrão | Para quê |
|---|---|---|
| `Conversations:Attachments:RootPath` | `LocalApplicationData\IaBimeta\attachments` | a pasta privada dos arquivos (fora de qualquer pasta servida) |
| `Conversations:Attachments:MaxImageBytes` | 5 MB | tamanho máximo de imagem |
| `Conversations:Attachments:MaxAudioBytes` | 16 MB | tamanho máximo de áudio |
| `Conversations:Attachments:MaxDocumentBytes` | 20 MB | tamanho máximo de documento (a Meta aceita até 100 MB; teto absoluto 100 MB) |
| `Conversations:Attachments:AllowedImageTypes` / `AllowedAudioTypes` / `AllowedDocumentTypes` | listas abaixo | tipos permitidos por tipo de mídia (só os que a validação sabe conferir) |
| `Channels:Providers:WhatsApp:MediaTimeout` | 60 s | tempo de cada etapa do download |
| `Channels:Providers:WhatsApp:MediaHostSuffixes` | `fbsbx.com`, `facebook.com`, `fbcdn.net`, `whatsapp.net`, `whatsapp.com` | os domínios para onde o token da conta pode ser enviado |

Tipos permitidos por padrão: **imagem** JPEG, PNG, WebP; **áudio** AAC, AMR, MP3, MP4 (M4A), Ogg; **documento** PDF, texto simples, Word, Excel e PowerPoint (antigos e OOXML). **Não são permitidos:** SVG, HTML, executáveis, arquivos compactados. Um valor inválido derruba a partida (nunca no meio do processamento).

## 4. Segurança: o que está garantido e o que não está

**Garantido (com teste):** isolamento por tenant (o banco recusa arquivo ligado à mensagem de outro tenant; o armazenamento confere o tenant em toda leitura; chave adulterada não sai da raiz); o token da conta só em cabeçalho e só para a Graph API ou um domínio da Meta, sem redirecionamento; limite de tamanho medido durante a leitura; tipo conferido pela **assinatura** do conteúdo e pelo resumo; nome saneado; o arquivo só é guardado **depois** de aprovado; entrega ao navegador com o tipo verificado, `nosniff`, `Content-Security-Policy: sandbox`, `no-store`, e `attachment` para o que não é imagem nem áudio; sem URL pública; nada de arquivo para a IA; nenhum texto do provedor em log, resposta ou tela.

**Não garantido (pendente):** **não há antivírus** (o ponto de ligação existe, sem mecanismo); um arquivo do tipo permitido e com a assinatura certa, mas malicioso por dentro, é guardado e pode ser baixado; a pasta de arquivos **não é cifrada** nem entra no backup do banco.

## 5. Operação

- **Backup:** `Backup-Banco.ps1` copia o **banco**, **não** a pasta de arquivos. Sem a cópia da pasta, uma restauração deixa mensagens com o arquivo "disponível" que não abre (o Api responde 404). Faça a cópia da pasta `RootPath` junto com o backup do banco (cifrar a cópia é decisão sua, como a do backup).
- **Retenção:** a tabela `conversations.message_attachments` é classificada como **dado pessoal** (só relatório); **nenhuma rotina apaga** os arquivos do disco ainda (`RETENCAO-DE-DADOS.md`).
- **Mídia que não chega:** abra o item e veja o motivo na tela. "Expirou": o identificador da mídia vale **7 dias** na Meta; peça ao cliente para enviar de novo. "Depois de várias tentativas": a Meta ou a rede falhou todas as vezes; o evento morreu no relay (veja `Status-Ambiente.ps1`). Credencial recusada pela Meta: o job **repete com espera** (o administrador pode corrigir o token a tempo); esgotadas as tentativas, o arquivo fica indisponível.
- **Worker derrubado no meio do download:** o novo Worker reassume o job quando o lease vence e conclui, sem duplicar arquivo.

## 6. Demonstração local (sem Meta nem OpenAI reais)

Com a demonstração no ar (`tools\dev\README-demo-atendimento.md`):

```powershell
.\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente sobre o meu exame'
.\tools\dev\Send-MensagemWhatsApp.ps1 -Midia imagem -Legenda 'Foto da requisicao do exame'
.\tools\dev\Send-MensagemWhatsApp.ps1 -Midia audio
.\tools\dev\Send-MensagemWhatsApp.ps1 -Midia documento -NomeDoArquivo 'laudo-anterior.pdf' -Legenda 'Laudo do ano passado'
.\tools\dev\Send-MensagemWhatsApp.ps1 -Midia imagem -Expirada            # arquivo indisponível
.\tools\dev\Send-MensagemWhatsApp.ps1 -Midia documento -Arquivo C:\temp\falso.pdf   # um HTML com extensão .pdf: bloqueado
```

Sem `-Arquivo`, o script gera um arquivo sintético (PNG, MP3 **silencioso** de 3 s, PDF de uma página). Para ouvir algo, use `-Arquivo` com um áudio seu. O WhatsApp simulado imprime cada consulta de mídia **sem** o token.

## 7. Checklist da homologação com a Meta real (nada disto foi provado)

1. Mande do celular uma **foto**, uma **nota de voz**, um **PDF** e uma **planilha**; confira que cada um chega como **disponível** e abre.
2. Anote o **domínio do endereço de conteúdo** que a Meta devolveu (no log do Worker não aparece por desenho; use o `Status` do arquivo e, se ficar "indisponível" com `media-url-not-allowed`, o domínio precisa entrar em `MediaHostSuffixes`).
3. Confirme o **formato do `sha256`** (hexadecimal ou Base64): se for outro, o arquivo é aceito sem conferir o resumo.
4. Confirme o **tipo** que a Meta informa para a nota de voz (`audio/ogg; codecs=opus`) e para o PDF.
5. Mande um arquivo **maior que o limite** configurado e confirme "bloqueado: grande demais"; espere o identificador **expirar** (7 dias) e confirme "expirou".
6. Decida sobre **antivírus**, **cópia e criptografia da pasta** e o **prazo de guarda** dos arquivos antes de operar com clientes reais.
