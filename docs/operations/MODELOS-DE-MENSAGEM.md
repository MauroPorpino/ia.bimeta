# Modelos de mensagem (templates) do WhatsApp

Como usar o suporte a **modelos aprovados pela Meta** para iniciar ou retomar uma conversa **fora da janela de 24 horas** (ADR 0027). O ia.bimeta **não cria nem aprova** modelos: você cria e aprova na Meta (WhatsApp Manager) e depois **associa** o modelo à conta no ia.bimeta. Só **envio individual** por um atendente; nada de campanha, disparo em massa, editor ou agendamento.

## 1. O que é suportado

| Suportado | Recusado no cadastro (com o motivo) |
|---|---|
| Cabeçalho de **texto**, corpo e rodapé | Cabeçalho de imagem, vídeo, documento ou localização |
| Parâmetros posicionais (`{{1}}`) e nomeados (`{{nome}}`) | Parâmetros misturados (numerados e nomeados) ou numeração com buraco |
| Botões de resposta rápida, telefone e **link com uma variável no fim** | Copiar código, fluxo, catálogo, carrossel e demais botões |
| Categorias **utilidade** e **marketing** | Categoria **autenticação** |
| Até 28 parâmetros por modelo | Mais de 250 modelos por chamada |

Um modelo fica **por conta, nome e idioma**. Só o modelo `Approved` é oferecido ao atendente e enviado.

## 2. Associar os modelos à conta (administrador)

O corpo é o JSON **como a Graph API o devolve** (`GET /{waba-id}/message_templates`): um modelo, uma lista ou a página `{"data":[...]}`. Exige permissão de **gerir canais** na organização da conta.

```http
POST /channels/accounts/{accountId}/templates
Authorization: Bearer <token do administrador>
Content-Type: application/json

{ "data": [ { "name": "resultado_disponivel", "language": "pt_BR", "category": "UTILITY", "status": "APPROVED",
              "components": [ { "type": "BODY", "text": "Olá {{1}}, o resultado do exame {{2}} está disponível." } ] } ] }
```

A resposta lista `created`, `updated`, `unchanged` e `rejected` (cada recusa traz o motivo). **Repetir é seguro:** o que a Meta mudou é atualizado; nada é duplicado.

| Rota | O que faz |
|---|---|
| `GET /channels/accounts/{id}/templates` | lista os modelos da conta, de qualquer situação (quem lê canais) |
| `POST /channels/accounts/{id}/templates` | associa ou atualiza (quem gere canais) |
| `PUT /channels/accounts/{id}/templates/{templateId}/status` | `{"status": "Approved|Pending|Rejected|Paused|Disabled"}`: tira um modelo da oferta (ou o devolve) sem esperar a Meta |

Pelo arquivo da empresa (`tools\ops\empresa-exemplo.json`, bloco `whatsApp.templates`), o comando `Novo-Tenant.ps1` faz o mesmo na criação ou na atualização da empresa (etapa "Modelos de mensagem" do relatório).

## 3. Para o atendente

1. Assuma a conversa. A tela consulta a **janela de 24 horas**, contada da **última mensagem do cliente**.
2. **Janela aberta:** o texto livre vale; o botão **Enviar modelo de mensagem** abre o seletor ("Resposta livre até …" mostra quando fecha).
3. **Janela fechada:** a faixa amarela avisa e o campo de texto fica **bloqueado**. Escolha o modelo, preencha os campos (a prévia mostra exatamente o que o cliente vai ler) e **Enviar modelo**. Os erros aparecem ao lado do campo, antes do envio.
4. A mensagem entra no histórico com a etiqueta **Modelo: nome** e o estado de entrega de sempre (`enviado`, `entregue`, `lido`, `falha no envio`, `envio de resultado incerto`).
5. Se o cliente responder, a janela **reabre** e o texto livre volta.

## 4. Erros que o atendente e o operador podem ver

| Sintoma | Causa | O que fazer |
|---|---|---|
| 422 `ReplyWindowClosed` | o texto livre foi enviado com a janela fechada | usar um modelo; a tela já relê a janela |
| 422 `TemplateParametersInvalid` | parâmetro faltando, a mais, vazio, grande demais, com quebra de linha ou mais de 3 espaços seguidos | corrigir o campo indicado (o valor digitado não é repetido na mensagem) |
| 422 `ReplyTemplateUnavailable` | o modelo não é desta conta, não está aprovado ou foi pausado | escolher outro; o administrador confere o status |
| Falha `send-failed.132015` e o modelo some da lista | a Meta **pausou** o modelo (baixa qualidade) | editar e reaprovar na Meta, e associar de novo (o JSON atualiza a situação) |
| Falha `send-failed.132016` ou `.132001` | a Meta desativou o modelo ou ele não existe naquele idioma | criar outro modelo na Meta |
| Falha `template-not-approved`, `template-changed`, `template-not-found` | o modelo mudou ou saiu do ar **entre o pedido e o envio** (nada foi enviado) | escolher o modelo de novo |
| Falha `account-paused` | a conta está pausada (token expirado, por exemplo) | ver `WHATSAPP-CONEXAO-REAL.md` §5; modelos não são reenviados sozinhos |
| `envio de resultado incerto` (`SendUnknown`) | tempo esgotado ou erro 5xx: a Meta **pode** ter recebido | conferir no celular; **nunca** reenviar às cegas |

Uma falha de **modelo** não pausa a conta. Uma falha de **conta** (token) pausa a conta inteira, como no texto livre.

## 5. Demonstração local (sem Meta nem OpenAI reais)

```powershell
.\tools\dev\Start-DemoAtendimento.ps1
.\tools\dev\Add-ModelosDeDemonstracao.ps1                      # associa os modelos de exemplo (modelos-demonstracao.json)
.\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome Maria -Numero 5511999990301 -HorasAtras 26
```

Na interface (`http://localhost:5173`): a conversa da Maria tem a janela **fechada**; envie o modelo e veja, na janela do **WhatsApp simulado**, `[MODELO resultado_disponivel / pt_BR]` com os parâmetros. Sem `-HorasAtras`, a janela fica aberta.

## 6. Homologação com a Meta real: o que conferir

Hoje **nenhuma chamada real à Meta foi feita**. Antes de produção, na conta de teste e com um modelo **realmente aprovado**:

- [ ] enviar um modelo **fora** da janela (cliente sem escrever há mais de 24 h) e **dentro** dela; conferir a entrega no celular;
- [ ] modelo com **cabeçalho de texto**, com **parâmetros nomeados** e com **botão de link** (a forma do cabeçalho no envio não está na página pública lida em 09/11/2026);
- [ ] valor de parâmetro com quebra de linha, tabulação e mais de 3 espaços: o que a Meta responde (o ia.bimeta recusa antes, de forma conservadora);
- [ ] parâmetro faltando e modelo pausado: o código e o estado HTTP reais (o leitor decide pelo código `132000`, `132012`, `132015`, `132016`, `132001`);
- [ ] os status `sent`, `delivered` e `read` de uma mensagem de modelo chegam pelo webhook e avançam o estado;
- [ ] `Get-DiagnosticoDeEnvio.ps1` sem nada a apontar depois do teste.

## 7. Limites conhecidos

Modelos entram por **JSON colado** (sem sincronização automática com a Graph API nem tela de administração); o rascunho dos campos do modelo não sobrevive a trocar de conversa; sem mídia, sem autenticação, sem iniciar conversa que ainda não existe; o texto do histórico é a **prévia** calculada pelo ia.bimeta. Detalhes e justificativas: ADR 0027.
