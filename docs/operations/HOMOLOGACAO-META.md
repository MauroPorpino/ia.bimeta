# Homologação com a Meta: primeiro teste real de recebimento e envio

Procedimento para o **primeiro teste real** do ia.bimeta com o WhatsApp Cloud API da Meta, a partir do computador de desenvolvimento (Windows 10, VS Code), com o **número de teste da Meta**, **IA simulada** (nenhuma chamada paga à OpenAI) e o atendente na interface React. Complementa `WHATSAPP-CONEXAO-REAL.md` (referência da conta, vazão, pausa e diagnóstico). Fatos da Meta e dos túneis conferidos em 06/11/2026 na documentação oficial; o que a documentação não diz está marcado como **confirme no painel**.

## 1. Estado do ambiente (verificado em 06/11/2026)

| Item | Situação | O que fazer |
|---|---|---|
| .NET 10, PostgreSQL 17 (porta 5433), saída HTTPS para a Graph API | **Prontos** | nada |
| Api, Worker, interface React, perfil `homologacao-meta`, scripts de conexão, prontidão, diagnóstico, teste do webhook público | **Prontos e ensaiados** contra simulações (nenhuma chamada à Meta) | nada |
| `cloudflared.exe` (túnel HTTPS) | **Não instalado** | `.\tools\ops\Start-TunelPublico.ps1 -Baixar -SomenteBaixar` (binário único oficial, sem instalador) |
| Chave mestra dos canais (cifra o token e os segredos) | **Ausente** nos user-secrets | `.\tools\dev\Set-ChaveMestraDosCanais.ps1` |
| Modelo de IA para a demonstração (user-secrets vazios) | **Ausente** | `.\tools\dev\Set-ConfiguracaoDeIa.ps1 -ModeloId gpt-5-mini -PrecoEntradaPorMilhao 0.25 -PrecoSaidaPorMilhao 2` (só catálogo: a IA é o **simulador local**) |
| Migrações novas no `iabimeta_dev` (Channels, ContactCenter) | **Pendentes**: o Api as aplica ao subir | **backup antes** (passo 0) |
| Node (interface React) | Fora do PATH | `$env:PATH = 'C:\Projetos\ferramentas\node-v24.22.1-win-x64;' + $env:PATH` |
| Conta, app e número na Meta | **Você cria** (seção 3) | seção 3 |

## 2. HTTPS público em desenvolvimento: recomendação

**Recomendado: Cloudflare Quick Tunnel** (`cloudflared tunnel --url http://localhost:5156`).

| Critério | Quick Tunnel (Cloudflare) | Dev Tunnels (Microsoft / VS Code) | ngrok (grátis) |
|---|---|---|---|
| Conta | **Não precisa** | Precisa (Microsoft ou GitHub) | Precisa (conta; plano grátis) |
| Instalação | **Binário único**, sem instalador | `winget install Microsoft.devtunnel` ou VS Code | Binário + authtoken |
| URL | **Muda a cada execução** | Estável (túnel persistente) | Domínio de desenvolvimento automático |
| POST de servidor (webhook) | Sem página intermediária documentada | Página anti-phishing **não** vale para POST | não documentado nas páginas lidas |
| Limites | 200 requisições simultâneas, sem garantia de disponibilidade, não é para produção | prévia pública, não é para produção | 20.000 requisições/mês, 1 GB |

Por que o Quick Tunnel: é o **mais simples** (zero conta, zero cadastro) e só abre **saída** de rede (nenhuma porta de entrada no seu roteador). O custo é a URL mudar a cada vez: cadastre o callback de novo na Meta a cada sessão (1 minuto). **Alternativa** se isso incomodar nas sessões longas: Dev Tunnel persistente (`devtunnel create --allow-anonymous`), que mantém a URL; exige login e instalar a ferramenta.

### Segurança do túnel (importante)

No ambiente de desenvolvimento o Api tem `X-Dev-Tenant` e as rotas `/simulator` ligados, e um túnel entrega as requisições ao Api **como se viessem do loopback**. Expor a porta 5155 inteira seria entregar isso à internet. Por isso:

- o túnel aponta para a **porta pública 5156**, nunca para a 5155 (o script recusa a 5155);
- o Api, com o perfil `homologacao-meta`, serve nessa porta **só** `/webhooks/whatsapp/...`; todo o resto responde **404** (guarda `PublicIngressGuard`, provado em teste e no Kestrel real);
- uma requisição com rastro de túnel (`Cf-Ray`, `Cf-Connecting-Ip`, `X-Forwarded-For`) só alcança o webhook **em qualquer porta** (túnel apontado por engano para a 5155);
- o webhook continua exigindo a **assinatura do corpo** (HMAC com o App Secret) e o identificador opaco na URL.

## 3. O que você precisa fornecer ou configurar na Meta

1. **Conta de desenvolvedor Meta** (developers.facebook.com) e um **Meta Business Portfolio** (conta empresarial).
2. **Criar um app** (tipo Business, caso de uso **"Connect with customers through WhatsApp"**) e abrir **WhatsApp > API Setup**.
3. Anotar, no painel:
   - **Phone Number ID** do número de **teste** (aparece no seletor "From"; não é o telefone);
   - **WhatsApp Business Account ID** (no painel aparece como "Messaging account ID"/WABA ID);
   - **Token de acesso temporário** ("Generate access token"): expira rápido (**confirme no painel a validade**) e serve para o primeiro teste. Para uso contínuo: token de **usuário do sistema** do Business Manager com `whatsapp_business_messaging`, `whatsapp_business_management` e `business_management`, com acesso à conta WhatsApp.
   - **App Secret**: configurações do app > **Básico** (confirme o caminho no painel).
4. **Cadastrar o seu celular como destinatário** do número de teste (o painel pede o número em "To"; **confirme no painel** a verificação por código e o limite de destinatários).
5. O **callback do webhook** (URL + Verify token): você o cadastra no passo 5; **assine o campo `messages`**.
6. O app em modo de **Desenvolvimento** basta para o número de teste (**confirme**: a Meta diz que "alguns webhooks não são enviados" em modo Dev; se as mensagens não chegarem, veja a seção 6).

Os **três segredos** (token, App Secret, verify token) são digitados **só nos scripts** (sem eco) e ficam cifrados no banco; nunca em arquivo, chat ou log.

## 4. Passo a passo

Todos os comandos no PowerShell, na raiz do repositório (`C:\Projetos\ferramentas\worktrees\ia.bimeta-c1`). Um terminal por bloco.

### 4.0 Preparação (uma vez)

```powershell
# Backup do banco de desenvolvimento ANTES de o Api aplicar as migrações novas
.\tools\ops\Backup-BancoDeDesenvolvimento.ps1 -Rotulo pre-homologacao-meta

# Chave mestra dos canais e modelo de IA (user-secrets; nenhum segredo e impresso)
.\tools\dev\Set-ChaveMestraDosCanais.ps1
.\tools\dev\Set-ConfiguracaoDeIa.ps1 -ModeloId gpt-5-mini -PrecoEntradaPorMilhao 0.25 -PrecoSaidaPorMilhao 2

# Baixa o cloudflared.exe (binario unico oficial) e mostra o SHA-256 para voce conferir na pagina da release
.\tools\ops\Start-TunelPublico.ps1 -Baixar -SomenteBaixar
```

### 4.1 Túnel HTTPS (terminal 1)

```powershell
.\tools\ops\Start-TunelPublico.ps1
```

Imprime a **URL pública** (`https://....trycloudflare.com`). Deixe aberto. **Anote a URL.**

### 4.2 IA simulada, Api e Worker (terminais 2, 3 e 4)

```powershell
# Terminal 2: a "OpenAI" local (nenhuma chamada paga)
.\tools\dev\Start-OpenAiSimulada.ps1
```

```powershell
# Terminal 3: o Api (perfil homologacao-meta: portas 5155 local e 5156 publica, tenant "homologacao")
$env:Channels__Webhook__PublicBaseUrl = 'https://SUA-URL.trycloudflare.com'      # a do passo 4.1
$env:AiGateway__Providers__OpenAi__BaseAddress = 'http://127.0.0.1:5199/v1/'
dotnet run --project backend\host\IaBimeta.Api --launch-profile homologacao-meta
```

```powershell
# Terminal 4: o Worker (relay, Sender, IA)
$env:DOTNET_ENVIRONMENT = 'Development'
$env:AiGateway__Providers__OpenAi__BaseAddress = 'http://127.0.0.1:5199/v1/'
dotnet run --project backend\host\IaBimeta.Worker
```

```powershell
# Chave FICTICIA da IA para o tenant (vai so para a OpenAI simulada local)
.\tools\dev\Set-ChaveDaOpenAI.ps1 -Tenant homologacao      # cole qualquer texto, por exemplo: sk-ficticia
```

Usuário do atendente: tenant `homologacao`, e-mail `admin@homologacao.local`, senha `DevLocal-Admin-2026!` (ou a de `$env:IABIMETA_DEV_ADMIN_PASSWORD`, se você a definir antes de subir o Api; ela vale para todos os administradores de desenvolvimento).

### 4.3 Cadastrar a conta (sem enviar nada)

```powershell
.\tools\ops\Conectar-ContaWhatsApp.ps1 -Tenant homologacao -Email admin@homologacao.local `
  -WabaId <WABA ID> -PhoneNumberId <Phone Number ID> -NomeDoNumero 'Numero de teste Meta' `
  -UrlPublica https://SUA-URL.trycloudflare.com
```

Pede a senha, o **token de acesso** e o **App Secret** (sem eco), cria a unidade "Matriz" se faltar, grava tudo cifrado e **imprime a URL de callback e o Verify token** (o token aparece **só agora**). O número fica **inativo**. Anote a URL de callback e o Verify token.

### 4.4 Provar o webhook SEM a Meta

```powershell
.\tools\ops\Test-WebhookPublico.ps1 -CallbackUrl https://SUA-URL.trycloudflare.com/webhooks/whatsapp/<id>
```

Pede o Verify token e o App Secret. Verifica: HTTPS válido até o seu Api, o desafio (verify token certo/errado), a assinatura (corpo assinado aceito; assinatura errada recusada) e o identificador desconhecido. **Só chama a sua URL.** Se a assinatura falhar com 401, o App Secret digitado difere do cadastrado: corrija antes de seguir.

### 4.5 Cadastrar o webhook na Meta

No app: **WhatsApp > Configuration > Webhook**:
1. **Callback URL** = a URL de callback do passo 4.3; **Verify token** = o do passo 4.3 → verificar e salvar (a Meta faz um `GET` com `hub.challenge`; o ia.bimeta responde).
2. **Assine o campo `messages`**.

### 4.6 Prontidão com leitura na Meta e ativação

```powershell
# UMA leitura do numero na Graph API (so leitura, nenhuma mensagem): prova token e Phone Number ID
.\tools\ops\Test-ProntidaoWhatsApp.ps1 -Tenant homologacao -Email admin@homologacao.local -ContaId <id da conta> -Live

# Ativa o numero (mantem as credenciais ja cadastradas)
.\tools\ops\Conectar-ContaWhatsApp.ps1 -Tenant homologacao -Email admin@homologacao.local `
  -WabaId <WABA ID> -PhoneNumberId <Phone Number ID> -NomeDoNumero 'Numero de teste Meta' -Ativar
```

### 4.7 O primeiro teste real

Terminal 5 (interface):

```powershell
$env:PATH = 'C:\Projetos\ferramentas\node-v24.22.1-win-x64;' + $env:PATH
cd frontend
npm run dev
```

Abra `http://localhost:5173` e entre com o atendente do passo 4.2.

1. **Recebimento + IA + envio.** Do **seu celular** (o cadastrado como destinatário), mande **"Olá"** para o **número de teste**. Esperado em poucos segundos: no celular chega **"Resposta simulada da IA: …"** (prova: webhook real recebido → Conversations → IA simulada → Sender → Graph API → seu celular).
2. **Transferência.** Mande **"Quero falar com um atendente"**: chega o aviso de transferência e a conversa aparece em **Aguardando** na interface.
3. **Atendente.** Na interface: **Assumir**, escreva e **envie** uma resposta: ela chega **no celular**.
4. **Resposta do cliente.** Do celular, responda: a mensagem aparece na interface (a IA não fala mais).
5. **Encerrar** o atendimento.
6. **Conferir**: `.\tools\ops\Get-DiagnosticoDeEnvio.ps1 -Tenant homologacao -Email admin@homologacao.local` deve terminar com **OK: nada a apontar** (código 0).

**Critério de sucesso:** os 6 passos acima, sem 401 no Receiver, sem conta pausada e sem falhas no diagnóstico.

### 4.8 Modelo de mensagem (só com um modelo aprovado na sua conta de teste)

Opcional e **independente** dos passos acima; exige um modelo **realmente aprovado** na Meta (o número de teste não tem os do seu negócio). Siga o checklist da seção 6 de `docs/operations/MODELOS-DE-MENSAGEM.md`: associe o JSON do modelo à conta (`POST /channels/accounts/{id}/templates`), deixe o cliente **sem escrever por mais de 24 horas** (ou use um número cuja última mensagem seja antiga), assuma a conversa, envie o modelo e confira no celular e no estado de entrega. Nenhum passo dos itens 4.1 a 4.7 depende deste.

## 5. Se algo falhar

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| A Meta não verifica o webhook | URL/verify token diferentes; túnel fora do ar; URL do túnel mudou | refaça o 4.4; confira a URL do túnel; cadastre o callback de novo |
| Mensagem do celular não chega ao ia.bimeta | `messages` não assinado; app em modo Dev e número não habilitado; callback antigo | confira o 4.5; veja **Receiver** no log do Api (401 = App Secret; 404 = identificador do webhook) |
| Chega e a IA não responde | Worker parado; chave fictícia não cadastrada (`Set-ChaveDaOpenAI.ps1`); simulador parado | veja o terminal do Worker; `Get-DiagnosticoDeEnvio.ps1` |
| Resposta não chega ao celular, conta **pausada** | token temporário **expirou** (190) ou sem permissão | gere novo token e rode `Conectar-ContaWhatsApp.ps1 ... -RegravarTokenDeAcesso`; retome: `POST /channels/accounts/{id}/sending/resume` (exige a prova do provedor) |
| `window-closed` (131047) | mais de 24 h desde a última mensagem do cliente (a interface já bloqueia o texto livre fora da janela; isto só aparece se o relógio da Meta divergir) | envie um **modelo aprovado** (`docs/operations/MODELOS-DE-MENSAGEM.md`) ou mande uma nova mensagem do celular |
| Falha `send-unknown` | tempo esgotado/5xx: a Meta pode ter recebido | confira no celular; **nunca reenvie às cegas** |

## 6. Encerrar e limpar

- Ctrl-C nos terminais (o **túnel** e a URL deixam de existir).
- Revogue o token temporário na Meta se não for mais usar; remova ou troque o callback do webhook no painel.
- `POST /channels/instances/{id}/suspend` suspende o número no ia.bimeta.

## 7. Limites deste teste

Número de **teste** da Meta (destinatários verificados, sem templates aprovados do seu negócio); só **texto**; IA **simulada** (a chamada real à OpenAI continua suspensa até os créditos); túnel **de desenvolvimento** (sem garantia de disponibilidade). Para um número real de produção: domínio/URL estável com TLS válido, token de usuário do sistema, app em modo Live e revisão dos requisitos da Meta.
