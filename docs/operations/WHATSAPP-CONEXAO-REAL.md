# Conectar um número real do WhatsApp (Meta Cloud API) ao ia.bimeta

Procedimento para ligar **uma conta WhatsApp Business real** de um tenant ao ia.bimeta, com segurança e **sem enviar mensagem alguma antes de a configuração ser provada**. Vale para o MVP (texto, um número por conta, chat). Fatos da Meta conferidos na documentação oficial em 05/11/2026 (`developers.facebook.com/documentation/business-messaging/whatsapp/...`: `webhooks/create-webhook-endpoint`, `webhooks/override`, `access-tokens`, `throughput`, `support/error-codes`); a Meta muda essas páginas, então confirme antes de um go-live.

> **Regra de ouro:** token de acesso, App Secret, token de verificação e a URL do webhook são **segredos**. Nunca em arquivo versionado, log, ticket, chat ou captura de tela. Os scripts pedem os valores sem eco e o ia.bimeta os guarda **cifrados** (AES-256-GCM, amarrados ao tenant); nenhuma resposta da API os devolve.

## 1. O que precisa existir na Meta (você faz no painel)

| Item | Onde / observação |
|---|---|
| Aplicativo Meta (tipo Empresa) com o produto **WhatsApp** | App Dashboard |
| **WABA** (conta do WhatsApp Business) e o **número** registrado nela | WhatsApp Manager. Anote o **WABA ID** e o **Phone Number ID** (o ID numérico do número, **não** o telefone) |
| **Usuário do sistema** no Business Manager, com acesso (Parcial ou Total) à conta WhatsApp e ao aplicativo | Business Settings > Users > System Users. Sem esse acesso, a API responde erro 200 |
| **Token de acesso do usuário do sistema** com as permissões `whatsapp_business_messaging`, `whatsapp_business_management` e `business_management` | Gerar com "Generate token". Token de **usuário comum** expira em horas e não serve em produção |
| **App Secret** do aplicativo | App Dashboard > Configurações > Básico. É ele que assina os webhooks |
| Números em coexistência com o app WhatsApp Business têm vazão fixa de **20 mensagens/s** (o padrão é 80) | Ajuste a vazão da conta (seção 6) |

## 2. O que precisa existir no ia.bimeta (ambiente)

1. **Api e Worker no ar**, com o banco migrado (a migração `AccountSendState` do Channels cria a tabela do balde/pausa; faça backup antes de aplicar em banco existente).
2. **Endereço público HTTPS do Api** com certificado válido (a Meta **não aceita** certificado autoassinado). Configure `Channels:Webhook:PublicBaseUrl` (por exemplo `https://atendimento.exemplo.com.br`). Em desenvolvimento no Windows isso exige um túnel/proxy HTTPS; **não é instalado automaticamente**.
3. **Chave mestra das credenciais**: `Channels:Secrets:ActiveKeyId` e `Channels:Secrets:Keys:<id>` (32 bytes em base64) no cofre/variáveis de ambiente do Api **e** do Worker. Sem ela, nenhuma credencial é gravada nem lida. Em dev: `tools\dev\Set-ChaveMestraDosCanais.ps1`.
4. **Api e Worker com a mesma configuração** de `Channels:*` e a mesma chave mestra.
5. Parâmetros de envio (opcionais): `Channels:Sending:DefaultThroughputPerSecond` (padrão 80), `BurstSeconds` (1), `MaxLocalWait` (20 s), `DefaultRateLimitCooldown` (5 s), `MaxRateLimitCooldown` (5 min). Versão da Graph API: `Channels:Providers:WhatsApp:GraphApiVersion` (padrão `v26.0`).
6. Um usuário com papel de **administrador da organização** (permissão `ChannelManage`) e uma **unidade** (matriz ou filial) para atender o número.

## 3. Passo a passo

### 3.1 Cadastrar a conta, o número e as credenciais (sem enviar nada)

```powershell
.\tools\ops\Conectar-ContaWhatsApp.ps1 `
  -Url https://atendimento.exemplo.com.br -Tenant meu-tenant -Email admin@meu.com `
  -OrganizacaoId <guid> -UnidadeId <guid> `
  -WabaId 123456789012345 -PhoneNumberId 109876543210987 `
  -NomeDoNumero 'Atendimento Matriz' -NumeroDeExibicao '+55 11 3000-0000' `
  -UrlPublica https://atendimento.exemplo.com.br
```

O script pede (sem eco) a senha, o **token de acesso** e o **App Secret**; **gera** o token de verificação do webhook e o mostra **uma vez**. Ele cria a conta e o número (**inativo**), autoriza a unidade, grava as três credenciais cifradas e cria o identificador opaco do webhook. Pode ser repetido: reaproveita o que existe e **mantém as credenciais já cadastradas** (repetir para ativar **não** troca o token de verificação que a Meta já conhece; para trocar credenciais use `-RegravarCredenciais`, e o novo token de verificação precisará ser cadastrado de novo na Meta).

### 3.2 Cadastrar o webhook na Meta

No painel: **App Dashboard > WhatsApp > Configuração > Webhook**:

1. **URL de callback** = a URL impressa pelo script (`https://<público>/webhooks/whatsapp/<id-opaco>`).
2. **Verify token** = o valor impresso pelo script.
3. Confirme (o botão de verificar/salvar do painel): a Meta faz um `GET` com `hub.mode=subscribe`, `hub.verify_token` e `hub.challenge`; o ia.bimeta compara o token em tempo constante e devolve o `hub.challenge`.
4. **Assine o campo `messages`** (mensagens recebidas e status de entrega).

**Alternativa (sobrescrever só esta WABA ou só este número, sem mexer no webhook do aplicativo):** a Meta permite `POST /<WABA_ID>/subscribed_apps` com `{"override_callback_uri": "<URL>", "verify_token": "<TOKEN>"}` (URL de até 200 caracteres) ou `POST /<PHONE_NUMBER_ID>` com `{"webhook_configuration": {"override_callback_uri": "...", "verify_token": "..."}}`. A precedência é número > WABA > aplicativo. Use o token **digitado por você** nessas chamadas; nunca o cole em histórico compartilhado.

### 3.3 Teste de prontidão (só leitura)

```powershell
.\tools\ops\Test-ProntidaoWhatsApp.ps1 -Url https://atendimento.exemplo.com.br -Tenant meu-tenant -Email admin@meu.com -ContaId <guid> -Live
```

Sai com código **0** (pronta) ou **1**. Sem `-Live` confere só a configuração local. Com `-Live` faz **uma leitura** do número na Graph API (`GET /<PHONE_NUMBER_ID>?fields=id,display_phone_number,verified_name,quality_rating`) para provar que o token é aceito e que o número existe: **nenhuma mensagem é enviada**.

| Verificação | Falha quando |
|---|---|
| `secret-store` | a chave mestra não está configurada ou não funciona |
| `provider.adapter` | o adaptador do WhatsApp não está registrado (o webhook responderia 503) |
| `credential.*` | falta o token, o App Secret ou o token de verificação; está ilegível (chave mestra mudou); tem espaço/caractere inválido |
| `credential.distinct` | dois valores são iguais (erro comum de colagem) |
| `instance.*` | sem número, sem número ativo, sem unidade autorizada, ou a **rota de entrada** não leva do Phone Number ID à instância e ao tenant certos |
| `webhook.id` / `webhook.public-url` | falta o identificador do webhook / o endereço público HTTPS |
| `sending.state` | o envio da conta está **pausado** (aviso: a Meta mandou esperar) |
| `provider.live.<número>` | o provedor recusou o token (190, 0, 3, 10, 200–299, 368, 131031), não achou o número (803, 100) ou não respondeu |

Aviso (não bloqueia): número sem unidade padrão, qualidade **RED**, número exibido pela Meta diferente do cadastrado.

### 3.4 Ativar e fazer o primeiro teste

Só com a prontidão **aprovada com `-Live`**: ative o número (`.\tools\ops\Conectar-ContaWhatsApp.ps1 ... -Ativar` ou `POST /channels/instances/{id}/activate`). Depois mande **uma** mensagem de um celular para o número e acompanhe em **Atendimento** (interface do atendente). Primeiro teste com o **número de teste da Meta** é o item 5.2 do plano.

## 4. Como o ia.bimeta valida o webhook (associação segura da conta ao tenant)

1. A URL traz só um **identificador opaco**; ele resolve **conta e tenant** no banco. O tenant **nunca** vem do conteúdo do webhook.
2. A assinatura `X-Hub-Signature-256` (HMAC-SHA256 com o App Secret) é verificada sobre os **bytes brutos**, em tempo constante, **antes de interpretar** o corpo. Inválida: **401**, nada é gravado. Identificador desconhecido: **404**.
3. Cada evento é conferido: o Phone Number ID do payload precisa ter **rota** para uma instância **desta conta e deste tenant** e **ativa**; senão é descartado (e contado), com resposta 200 para evitar tempestade de reenvios.
4. O evento é gravado no Inbox durável (deduplicado) e só então responde **200**. O processamento acontece no Worker.

## 5. Operação: vazão, limite de taxa e pausa

- **Vazão (SND-2):** balde de fichas **por conta**, guardado no banco (vale entre instâncias do Worker). Padrão 80 mensagens/s; ajuste por conta com `PUT /channels/accounts/{id}/sending/throughput` `{"messagesPerSecond": 20}` (1 a 1000; `null` volta ao padrão). Uma espera curta é feita dentro do envio; uma espera longa devolve a entrega ao relay.
- **Limite de taxa da Meta (429):** os códigos de limite da conta (130429, 80007, 4) e qualquer HTTP 429 com corpo de erro completo fazem a **conta inteira** esperar o `Retry-After` (ou 5 s, no máximo 5 min), sem gastar tentativa das outras conversas; a mensagem é repetida (até 3 chamadas). O limite do **par** remetente–destinatário (131056, 1 mensagem a cada 6 s para o mesmo cliente) repete só aquela mensagem.
- **Resultado ambíguo** (tempo esgotado, 5xx, 429 sem corpo de erro): `SendUnknown`, **nunca** reenviado automaticamente.
- **Pausa (SND-6):** erro que atinge a **conta** (token expirado/inválido 190/0, permissão 3/10, conta restrita 368, HTTP 401/403) **pausa a conta**. Pausada, as próximas mensagens **falham na hora** (`account-paused`, visíveis ao atendente) e **não chegam à Meta**. O erro fica na auditoria e na métrica `channels.account.paused`.
- **Estado:** `GET /channels/accounts/{id}/sending`. **Pausar à mão:** `POST .../sending/pause` (por exemplo, suspeita de token vazado).

### Incidente: token expirado ou revogado

1. O envio falha com `account-error`, a conta é pausada e os atendentes veem a falha.
2. Gere um novo token no Business Manager e grave-o: `PUT /channels/accounts/{id}/secrets/AccessToken` (ou rode `Conectar-ContaWhatsApp.ps1`). **Trocar o token não retoma sozinho.**
3. `Test-ProntidaoWhatsApp.ps1 -Live` até passar.
4. **Retome:** `POST /channels/accounts/{id}/sending/resume`. A retomada **só ocorre** se o provedor aceitar o token para cada número ativo (uma leitura, nenhuma mensagem); senão responde 409 e a conta continua pausada. A vazão volta de forma gradual (o balde recomeça com 1 ficha).
5. As mensagens que falharam **não são reenviadas sozinhas**: o atendente responde de novo ao cliente.

## 6. Desfazer / parar

- Parar de receber e enviar por um número: `POST /channels/instances/{id}/suspend`.
- Parar só o envio da conta (continua recebendo): `POST /channels/accounts/{id}/sending/pause`.
- Remover o webhook: no painel da Meta (ou, no override, `override_callback_uri` vazio).
- Girar o App Secret: gere o novo na Meta, grave com `PUT .../secrets/AppSecret` e **imediatamente** confirme que os webhooks voltam a ser aceitos (webhooks assinados com o segredo antigo respondem 401).

## 7. Limitações conhecidas do MVP

Só texto (sem mídia, template nem mensagem fora da janela de 24 h: a Meta responde 131047 e a mensagem falha); um token por conta; limite do par apenas reativo (não há balde por destinatário); sem alerta ativo por e-mail/Slack (a pausa aparece na API de estado, na auditoria, na métrica e como falha ao atendente); reenvio de falhas só por nova resposta do atendente; o App Secret só é provado quando a Meta entrega um webhook assinado com ele (o teste de prontidão não tem como conferi-lo sozinho): depois de conectar, envie uma mensagem de teste e confira que o Receiver responde 200 e não 401 (métrica `channels.webhook.rejected`).
