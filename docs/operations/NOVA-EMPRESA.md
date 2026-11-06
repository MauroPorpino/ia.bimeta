# Colocar uma empresa (tenant) em funcionamento

Este é o procedimento único e repetível para uma empresa nova usar o ia.bimeta em homologação: **tenant, administrador, organização, fila de atendimento humano, agente de IA, IA do tenant (modelo e chave), conta de WhatsApp (simulada ou real) e atendentes**. Tudo parte de **um arquivo JSON** e de **um comando**. Nenhum segredo vai no arquivo.

> Decisão registrada no ADR 0024. O comando roda na sua máquina, com acesso ao banco, e **não abre rota HTTP**. É idempotente: repetir completa o que faltou e nunca duplica.

## 1. Uma vez por ambiente

1. PostgreSQL no ar (porta 5433) e a string de conexão nos user-secrets do Api (`ConnectionStrings:Tenancy`). Para outro banco, defina `ConnectionStrings__Default` no terminal.
2. Chaves mestras e modelo de IA (user-secrets; nenhum segredo é impresso):

   ```powershell
   .\tools\dev\Set-ChaveMestraDosCanais.ps1
   .\tools\dev\Set-ConfiguracaoDeIa.ps1 -ModeloId gpt-5-mini -PrecoEntradaPorMilhao 0.25 -PrecoSaidaPorMilhao 2.00
   ```

   O modelo do arquivo da empresa (`ai.modelId`) precisa constar no catálogo (`AiGateway:Catalog:Models`): é o que o segundo script faz.

## 2. Descrever a empresa

Copie `tools\ops\empresa-exemplo.json` e ajuste (o arquivo aceita comentários `//`). Os campos que terminam em `Env` são o **nome** de uma variável de ambiente, nunca o valor.

| Bloco | O que define |
|---|---|
| `tenant` | `slug` (o que se digita na entrada: minúsculas, números e hífens) e `name` |
| `administrator` | e-mail, nome e `passwordEnv`; sem a variável, uma senha forte é **gerada** e mostrada uma única vez |
| `organization` | nome da organização e da unidade (matriz) |
| `queue` | a fila de atendimento humano (a regra que manda toda conversa a ela é criada junto) |
| `ai` | `providerId`, `modelId`, `instructions`, `handoffNotice`, `faq` (até 50 perguntas) e `apiKeyEnv` (a chave BYOK) |
| `whatsApp` | `mode`: `simulated` (conta sintética, para a Meta simulada) ou `real` (veja a seção 5) |
| `attendants` | lista de atendentes (e-mail, nome, `passwordEnv`), todos colocados na fila |

## 3. Conferir e executar

```powershell
# Confere o arquivo e mostra quais variáveis faltam. Não abre o banco e não altera nada.
.\tools\ops\Novo-Tenant.ps1 -Arquivo .\minha-empresa.json -SoValidar

# Executa. -PedirSegredos pergunta, sem eco, o valor das variáveis ainda não definidas (só para este processo).
.\tools\ops\Novo-Tenant.ps1 -Arquivo .\minha-empresa.json -PedirSegredos
```

O relatório lista cada etapa (`CRIADO`, `JA EXISTE`, `ATUALIZ.`, `FALHOU`), o caminho do webhook do WhatsApp e, **uma única vez**, as senhas e o token de verificação que foram gerados. Se uma etapa falha (por exemplo, modelo fora do catálogo ou chave mestra ausente), o relatório diz o que corrigir; corrija e **rode de novo**.

Códigos de saída: `0` tudo certo; `1` alguma etapa falhou; `2` arquivo ou uso inválido.

## 4. Subir o ambiente e operar (demonstração sem serviços externos)

```powershell
.\tools\dev\Start-DemoAtendimento.ps1      # OpenAI simulada, WhatsApp simulado, Api, Worker e interface
```

1. Abra `http://localhost:5173` e entre com o `slug`, o e-mail e a senha de um atendente (ou do administrador).
2. Faça o papel de um cliente (o `-Tenant` escolhe a empresa):

   ```powershell
   .\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant minha-empresa -Texto 'Ola, tudo bem?' -Nome Marta -Numero 5511999990501
   .\tools\dev\Send-MensagemWhatsApp.ps1 -Tenant minha-empresa -Texto 'Quanto custa o hemograma?' -Nome Marta -Numero 5511999990501
   ```

   A primeira mensagem recebe a resposta da IA; a segunda é transferida (o cliente recebe o `handoffNotice`) e a conversa aparece em **Aguardando** para os atendentes da empresa. O que o cliente recebe aparece na janela do WhatsApp simulado.
3. O atendente assume, responde e encerra pela interface. Duas empresas no mesmo banco **não se enxergam**.

## 5. WhatsApp real (Meta)

No arquivo, `"mode": "real"` com `wabaId`, `phoneNumberId`, `appSecretEnv` e `accessTokenEnv` (e, opcionalmente, `verifyTokenEnv`; sem ele o token de verificação é gerado e mostrado uma vez para colar no painel da Meta). O comando cadastra a conta, o número e as credenciais **cifradas** e deixa o número **INATIVO**: nada sai para clientes reais antes de provar a credencial. Depois, siga `docs/operations/WHATSAPP-CONEXAO-REAL.md` a partir da seção 3.2 (cadastrar o webhook na Meta, `tools\ops\Test-ProntidaoWhatsApp.ps1 -Live`, ativar) e `docs/operations/HOMOLOGACAO-META.md` para o HTTPS público. Isso depende de credenciais e de um endereço público que só você fornece.

## 6. Depois da primeira execução

| Preciso de... | Como |
|---|---|
| Novo atendente | acrescente-o em `attendants` e repita o comando (ou `POST /organizations/{id}/users` + `PUT /contact-center/queues/{id}/members/{userId}` como administrador) |
| Mudar instruções, aviso de transferência ou perguntas frequentes | edite `ai` e repita: publica uma **versão nova** do agente |
| Trocar a chave da IA | defina a variável de `ai.apiKeyEnv` com a nova chave e repita |
| Pausar ou diagnosticar o envio do WhatsApp | `POST /channels/accounts/{id}/sending/pause`, `tools\ops\Get-DiagnosticoDeEnvio.ps1` |

## 7. O que este procedimento ainda não faz

- Tela do administrador para emitir o token e entrega do token por e-mail. As **rotas** existem e a interface do atendente já troca a própria senha e usa o token (ADR 0025): a pessoa troca a própria senha, o administrador emite um token de redefinição e o operador recupera uma conta com `tools\ops\Redefinir-Senha.ps1 -Empresa <slug> -Email <e-mail>` (veja `docs/operations/SENHAS-E-SESSOES.md`). `DELETE .../users/{id}/role` só revoga o papel, não libera o e-mail para outro cadastro.
- Retenção de dados pessoais (depende de decisão jurídica) e backup agendado: o relatório e o expurgo técnico estão em `RETENCAO-DE-DADOS.md`, e os scripts de backup, restauração e ensaio, em `BACKUP-E-RESTAURACAO.md`.
- Remover ou suspender uma empresa inteira, e horários de atendimento da fila (a API de horários existe; o arquivo não os descreve).
- Qualquer coisa fora do ambiente de homologação: não há hospedagem de produção, certificado nem cofre de segredos (dependem de infraestrutura).
