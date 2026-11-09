# Demonstração do atendimento de ponta a ponta (sem crédito e sem chamada paga)

Mostra, na sua máquina, o fluxo completo do MVP: **cliente no WhatsApp → Receiver (webhook assinado) → Conversations e Agents → resposta da IA → envio pelo WhatsApp → transferência para a fila → atendente na interface React (assumir, responder, encerrar)**. Os dois provedores externos são **simulações locais**: uma "OpenAI" e uma "Graph API da Meta" em `127.0.0.1`. Nada sai da máquina e nenhuma credencial real é usada (as do canal são sintéticas, só de desenvolvimento).

## Peças

| Peça | O que é |
|---|---|
| `Start-OpenAiSimulada.ps1` (5199) | responde a Responses API. Se o cliente pede um atendente, ou fala de preço, **transfere**; senão, **responde** com um texto simulado |
| `Start-WhatsAppSimulado.ps1` (5198) | a Graph API da Meta: imprime cada mensagem que sairia pelo WhatsApp (é o "celular do cliente"), inclusive a subida e a mensagem de **arquivo** que o atendente envia (`[IMAGE]`, `[AUDIO]`, `[DOCUMENT]`; `docs/operations/ENVIO-DE-ARQUIVOS.md`) |
| Api (5155) e Worker | os de verdade, com o perfil `simulador-ia` (tenant `demo-ia`, IA e canal de demonstração preparados) |
| `frontend/` (5173) | a interface do atendente |
| `Send-MensagemWhatsApp.ps1` | faz o papel da Meta: envia ao Receiver um webhook **assinado** (HMAC-SHA256 com o App Secret) |
| `Send-MensagemParaEmpresa.ps1` | o mesmo papel da Meta para uma empresa colocada em funcionamento pelo `provision` (conta simulada), com o Api fora de Development; veja `docs/operations/HOMOLOGACAO-DO-CONHECIMENTO.md` |

## Antes (uma vez), no seu terminal

1. PostgreSQL de desenvolvimento no ar (porta 5433) e a string de conexão nos user-secrets do Api. O Api **aplica as migrations pendentes** (inclusive as novas do Channels) no banco configurado.
2. Chaves mestras e modelo (qualquer modelo serve, a "OpenAI" é local):

   ```powershell
   .\tools\dev\Set-ChaveMestraDosCanais.ps1
   .\tools\dev\Set-ConfiguracaoDeIa.ps1 -ModeloId gpt-5-mini -PrecoEntradaPorMilhao 0.25 -PrecoSaidaPorMilhao 2.00
   ```

3. Dependências da interface: `cd frontend; npm install` (Node: `C:\Projetos\ferramentas\node-v24.22.1-win-x64`).

## Subir tudo

```powershell
.\tools\dev\Start-DemoAtendimento.ps1
```

Abre uma janela para cada peça. Depois que o Api subir, cadastre uma chave de IA qualquer (ela só vai para a "OpenAI" local) com `.\tools\dev\Set-ChaveDaOpenAI.ps1`.

## Roteiro da demonstração

1. **A IA responde.** `.\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Ola, tudo bem?'`. Na janela do **WhatsApp simulado** aparece a resposta da IA para o cliente. (A mensagem e a resposta também estão em `GET /simulator/sessions/{id}`.)
2. **A IA transfere.** `.\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome Pedro -Numero 5511988887777`. O cliente recebe o aviso da transferência (janela do WhatsApp simulado) e a conversa aparece na fila, em **Aguardando**, na interface.
3. **O atendente atende.** Em `http://localhost:5173`, entre com tenant `demo-ia`, `admin@demo-ia.local` e `DevLocal-Admin-2026!`. Abra a conversa, **Assumir**, escreva a resposta e envie: ela aparece na janela do WhatsApp simulado. O cliente responde com outro `Send-MensagemWhatsApp.ps1` (mesmo `-Numero`): quem responde agora é o humano, a IA não fala.
4. **Encerrar.** **Encerrar** na interface (com confirmação): a sessão fecha e o item sai da fila.

## Mídia do cliente: imagem, áudio e documento (ADR 0028)

O WhatsApp simulado também faz o papel da Graph API para **arquivos**: o `Send-MensagemWhatsApp.ps1 -Midia` deposita o arquivo na pasta de mídia (`%TEMP%iabimeta-midia-demo`, a mesma do simulador) e manda o webhook só com o identificador; o Worker busca o arquivo (com o token no cabeçalho), valida e guarda em pasta privada, e o atendente o vê na interface.

1. Faça a conversa chegar a um atendente (o passo 2 acima) e **assuma**.
2. `.	oolsdevSend-MensagemWhatsApp.ps1 -Midia imagem -Legenda Foto da requisicao do exame` mostra a **miniatura** (clique amplia). `-Midia audio` mostra **Carregar áudio** e o player (o áudio de demonstração é **silencioso**, 3 s; use `-Arquivo` com um áudio seu para ouvir). `-Midia documento -NomeDoArquivo laudo.pdf` mostra o documento com **Baixar**.
3. `-Midia imagem -Expirada` mostra **Arquivo indisponível** (a Meta já não tem o arquivo) e `-Midia documento -Arquivo C:	empalso.pdf` (um HTML com extensão `.pdf`) mostra **Arquivo bloqueado**.
4. A IA **nunca** recebe o arquivo: com um cliente novo mandando só uma imagem, a conversa vai ao atendente (conteúdo indisponível para a IA). O WhatsApp simulado imprime cada consulta de mídia **sem** o token. Api e Worker precisam apontar para a mesma pasta de arquivos (`Conversations__Attachments__RootPath`; por padrão, a mesma pasta do usuário). Guia: `docs/operations/MIDIA-DO-WHATSAPP.md`.

## Fora da janela de 24 horas: modelos de mensagem (ADR 0027)

1. `.\tools\dev\Add-ModelosDeDemonstracao.ps1` associa à conta de demonstração os modelos de `modelos-demonstracao.json` (como se a Meta já os tivesse aprovado; o de cabeçalho de imagem é **recusado com o motivo**, de propósito).
2. `.\tools\dev\Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome Maria -Numero 5511999990301 -HorasAtras 26` manda uma mensagem de cliente **de 26 horas atrás**: a IA transfere e a janela de resposta livre já está fechada.
3. Na interface, **assuma** a conversa da Maria: a faixa **Janela de 24 horas fechada** aparece, o texto livre fica bloqueado e o seletor lista os modelos. Escolha `resultado_disponivel`, deixe os campos vazios e envie (erro ao lado de cada campo), preencha e **Enviar modelo**: a prévia é o texto que o cliente lê, e a janela do **WhatsApp simulado** mostra `[MODELO resultado_disponivel / pt_BR]` com os parâmetros.
4. Com `Send-MensagemWhatsApp.ps1` **sem** `-HorasAtras`, a janela está aberta: o texto livre vale e o botão **Enviar modelo de mensagem** abre o seletor. Guia completo: `docs/operations/MODELOS-DE-MENSAGEM.md`.

## Dois atendentes ao mesmo tempo (operação diária)

Para ver a Central de Atendimento com **dois atendentes** e várias conversas (ADR 0022):

1. `.\tools\dev\Add-AtendenteDeDemonstracao.ps1` cria o segundo atendente (`bruna@demo-ia.local`, papel comum) e o põe na fila.
2. Abra `http://localhost:5173` em **duas abas** (a sessão fica no `sessionStorage` de cada aba): `admin@demo-ia.local` numa e `bruna@demo-ia.local` na outra. Se a porta 5173 já estava em uso, o Vite sobe em 5174.
3. Mande 3 ou 4 clientes que a IA transfere (`Send-MensagemWhatsApp.ps1 -Texto 'Quero falar com um atendente' -Nome Mauro -Numero 5511999990201`, depois `-Nome Pedro -Numero ...202`, e assim por diante). A fila de **Aguardando** aparece nas duas abas sem recarregar (o título da página mostra quantas esperam).
4. Tente assumir a **mesma** conversa nas duas abas: uma assume, a outra vê "outro atendente assumiu antes de você" e a conversa some para ela. Cada um responde às suas: a resposta de cada atendente aparece só na janela do WhatsApp simulado do cliente certo.
5. Troque de conversa no meio de uma resposta: o histórico aparece na hora e o **rascunho** de cada conversa continua no campo.
6. Para ver o aviso de conta pausada: como administrador, `POST /channels/accounts/{id}/sending/pause`; as duas abas mostram a faixa em até 15 s, e a resposta seguinte falha ("falha no envio"). `.../sending/resume` retoma.
7. Para ver a queda de conexão: pare o Api. A faixa de reconexão aparece, a tela não esvazia e o texto digitado fica; ao subir o Api de novo a faixa some e tudo é relido.

## Uma empresa nova (tenant) com tudo configurado

Além do tenant `demo-ia` (preparado pelo perfil `simulador-ia`), qualquer empresa pode ser colocada em funcionamento **por um arquivo JSON e um comando**, sem editar perfil nenhum: tenant, administrador, fila, agente de IA (instruções e perguntas frequentes), modelo e chave da IA, conta de WhatsApp simulada e atendentes. Veja `docs/operations/NOVA-EMPRESA.md` (e `tools\ops\Novo-Tenant.ps1 -Arquivo ... -SoValidar` para só conferir o arquivo). Depois, `Send-MensagemWhatsApp.ps1 -Tenant <slug>` faz o papel do cliente dessa empresa, com os mesmos simuladores.

## O que é de verdade e o que é simulado

- **De verdade:** Receiver com verificação de assinatura, Inbox durável, deduplicação, Conversations, Agents, AiGateway (ledger e limites), ContactCenter (fila, atribuição, resposta e encerramento), Sender com a disciplina de efeito externo (intenção antes da chamada, `SendUnknown` sem reenvio), adaptador do WhatsApp, PostgreSQL.
- **Simulado:** a OpenAI e a Graph API da Meta (servidores locais) e a Meta chamando o webhook (o script).
- **Para o WhatsApp real:** uma URL pública (HTTPS) para o Api, o override do webhook por conta na Meta apontando para o caminho de `POST /channels/accounts/{id}/webhook`, e as credenciais reais cadastradas (`PUT /channels/accounts/{id}/secrets/{AppSecret|AccessToken|WebhookVerifyToken}`). Depende do número de teste da Meta (item 5.2 do ADR 0011).

## Observações

- A mensagem da IA enviada pelo simulador antigo (`/simulator/inbound`) usa um canal sem número cadastrado: o Sender a marca como falha (`channel-inactive`). Use o webhook, como acima.
- O ensaio automatizado dessa mesma cadeia, com os mesmos componentes e a `OpenAI` e a `Graph API` simuladas por HTTP, está em `tests/IaBimeta.IntegrationTests/Api/FluxoWhatsAppTests.cs`.
