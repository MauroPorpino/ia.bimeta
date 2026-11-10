# Retenção de dados (ADR 0025)

Política mínima de retenção por ambiente e por empresa, com relatório e expurgo **técnico** seguros. **Dados pessoais e registros de referência não são expurgados**: dependem de decisão jurídica (veja "Decisões que dependem do negócio").

## O que a política expurga hoje (só dado técnico)

| Categoria (`Retention:Days:<Categoria>`) | Padrão | O que sai | Tabelas |
|---|---|---|---|
| `Sessions` | 30 dias | sessões vencidas ou revogadas há mais que o prazo | `identity.sessions` |
| `PasswordResetTokens` | 30 dias | tokens de redefinição usados, revogados ou vencidos há mais que o prazo | `identity.password_reset_tokens` |
| `LoginThrottles` | 7 dias | contadores de falha de login parados (sem bloqueio vigente) | `identity.login_throttles` |
| `ProcessedEvents` | 7 dias | eventos do outbox **já processados** (o payload do evento do canal carrega o texto do cliente: é aqui que ele expira) | `conversations.outbox_message`, `channels.outbox_message` |
| `InboxMarkers` | 30 dias | marcas de idempotência (só identificadores) | `channels.inbox_message`, `agents.inbox_message`, `contact_center.inbox_message` |
| `OutboundAttachments` | 30 dias | o **conteúdo** dos arquivos que o atendente **enviou** (ADR 0031): o objeto na pasta privada é apagado e o arquivo passa a "indisponível" no histórico (a mensagem, o nome e a legenda ficam); recolhe também objetos órfãos antigos (sem linha) | tarefa `conversations.outbound-attachment-objects`: os objetos da pasta de arquivos (a linha de `conversations.message_attachments`, de saída, só muda de estado; nunca é apagada) |
| `InboundAttachments` | **0 (desligado)** | o **conteúdo** dos arquivos que o **cliente** enviou (ADR 0032): só roda com prazo definido (1 a 3650 dias), por decisão do proprietário; apaga o objeto da pasta privada e o arquivo passa a "indisponível" (a mensagem, o nome e a legenda ficam). **Prazo 0 nunca apaga nada** | tarefa `conversations.inbound-attachment-objects`: os objetos da pasta de arquivos (a linha de `conversations.message_attachments`, de entrada, só muda de estado; nunca é apagada) |

Os padrões de `ProcessedEvents` (7) e `InboxMarkers` (30) são os da política inicial aprovada (DP-06, ADR 0011). O de `OutboundAttachments` (30) é o prazo de guarda do arquivo enviado pelo atendente (ADR 0031), o mesmo prazo do identificador do arquivo subido na Meta. **O de `InboundAttachments` é 0 de propósito:** nenhum dado pessoal é apagado sem política expressamente definida (a recomendação técnica é 90 dias, a confirmar com a base legal e o setor do cliente). Cada lote apagado vai para a **trilha de auditoria antes** de sair (ADR 0032); se a trilha estiver fora do ar, o expurgo dos arquivos para sem apagar nada (`SEGURANCA-DOS-ARQUIVOS.md`).

**Nunca são expurgados por esta rotina:** eventos **pendentes, em andamento ou mortos** (os mortos ficam até reenfileirar ou descartar com autorização), sessão ou token ainda válidos, bloqueio de login vigente, e **todo** dado pessoal ou de referência: mensagens, traduções, contatos, conversas, atendimentos, histórico e operações do ContactCenter, usuários e papéis, trilha de auditoria e ledger de IA.

**Payload bruto do webhook:** não é guardado. O Receiver grava só o evento normalizado no outbox do Channels. O texto do cliente também fica em `conversations.messages` (dado pessoal, fora do expurgo).

## Configuração (por ambiente e por empresa)

Seção `Retention` (arquivo de configuração ou variáveis de ambiente, por exemplo `Retention__Days__Sessions=60`):

```json
{
  "Retention": {
    "Days": { "Sessions": 30, "ProcessedEvents": 7, "InboxMarkers": 30 },
    "Tenants": {
      "lab-exemplo": { "ProcessedEvents": 14, "InboxMarkers": 45 }
    }
  }
}
```

- `Days` troca só as categorias citadas; as demais ficam com o padrão. `Tenants:<slug>` sobrescreve **só** as categorias citadas para aquela empresa (o slug do login).
- Intervalo: 1 a 3650 dias. A retenção das marcas do Inbox tem de ser **maior** que a dos eventos processados (padrão e por empresa): a marca nunca expira antes do evento que ela protege. Valor inválido **impede a partida** do Api e do Worker.
- Uma empresa citada que **não existe** faz o comando recusar a execução (nunca se aplica um prazo suposto, que poderia apagar mais cedo que o combinado).
- O **relay do Worker** expurga os eventos processados do outbox, de hora em hora, com o prazo de cada empresa (sem a seção `Retention`, vale o padrão de 7 dias). A chave antiga `Messaging:ProcessedRetention` só vale onde **não** há política registrada (relay isolado, testes); nos hosts, use `Retention:Days:ProcessedEvents`. As demais categorias só são expurgadas pelo comando, nunca sozinhas.

## Expurgo agendado (ADR 0026)

O Worker pode rodar o expurgo técnico **sozinho, uma vez por dia**: as mesmas tarefas do comando `retention purge`, com os prazos por empresa e os bloqueios de segurança, sem tocar em dado pessoal ou de referência. **Desligado por padrão.**

```json
{ "Retention": { "Schedule": { "Enabled": true, "TimeOfDayUtc": "06:00:00", "RetryAfter": "00:15:00" } } }
```

- A execução fica devida a partir de `TimeOfDayUtc` (padrão 06:00 UTC, madrugada de Brasília). Se o Worker estava fora do ar na hora marcada, executa assim que sobe; depois de um sucesso, só no dia seguinte (o expurgo é idempotente). Falha: registra só o **tipo** da falha e tenta de novo depois de `RetryAfter` (mínimo 1 minuto); nada é apagado numa execução que falhou antes de começar (por exemplo, empresa inexistente na política).
- O log do Worker traz só contagens ("N linha(s) removida(s) por M tarefa(s)") e, se houver, o aviso de tarefa **bloqueada** (marcas do Inbox por evento morto antigo). Nunca nome de empresa ou dado de cliente.
- O `Status-Ambiente.ps1` mostra na seção **Retenção**: quanto está vencido esperando expurgo, os bloqueios e se o agendamento está ligado (vencido com o agendamento desligado é atenção).
- Faça um **backup antes de ligar** a primeira vez, rode `retention report` e confira os números: o expurgo é irreversível.

## Relatório e expurgo

```powershell
.\tools\ops\Retencao-De-Dados.ps1                        # relatório: nada é alterado
.\tools\ops\Retencao-De-Dados.ps1 -Expurgar -Confirmar   # expurgo técnico
```

(ou `dotnet run --project backend/host/IaBimeta.Api --no-launch-profile -- retention report` e `... retention purge --confirm`). Sem `--confirm` o expurgo recusa. O comando roda na máquina de quem opera, com acesso ao banco; não há rota HTTP. Para outro banco, defina `ConnectionStrings__Default` antes.

O relatório mostra, por categoria e por passada (cada empresa com prazo próprio e "padrão" para as demais), quantas linhas **seriam** removidas; os eventos mortos de cada outbox e a data do mais antigo; e o **inventário** de todas as tabelas (classe, linhas estimadas, tamanho). Guarde a saída do expurgo no registro da mudança: ela é, por enquanto, o registro do que foi removido.

**Rotina sugerida:** faça um backup (`tools\ops\Backup-Banco.ps1`), rode o relatório, confira os números e só então o expurgo.

### Bloqueio do Inbox por evento morto

Reenfileirar um evento morto depois de a marca de idempotência ter sido apagada poderia repetir o efeito dele. Por isso o expurgo das marcas do Inbox é **bloqueado** (o relatório diz `BLOQUEADO` e por quê) enquanto houver evento morto mais antigo que o corte. Trate os mortos (reenfileirar ou descartar com autorização) e rode de novo.

## Inventário: toda tabela tem uma classe

| Classe | Significado | Expurgo |
|---|---|---|
| técnica | estado operacional sem valor depois de um prazo | por política (tabela acima) |
| PESSOAL | conteúdo de cliente ou dado de colaborador | **nenhum**: só relatório |
| REFERÊNCIA | trilha de auditoria, ledger de IA | **nenhum**: só relatório |
| configuração | cadastros do tenant (organizações, filas, contas, credenciais cifradas...) | não expira por tempo; sai com o encerramento da empresa |
| controle do banco | histórico de migrations | nenhum |

Um teste de integração falha quando aparece uma tabela **sem classificação** (`RetentionInventory`), e garante que só as técnicas têm tarefa de expurgo: adicionar uma tabela obriga decidir a retenção dela.

## Decisões que dependem do negócio (não implementadas de propósito)

1. **Prazo e forma para dados pessoais** (apagar, anonimizar ou pseudonimizar): mensagens e traduções, contatos, conversas, atendimentos e histórico, usuários que saíram da empresa. Considerar a base legal, o prazo de guarda exigido para o setor (laboratórios, no caso do LabNet) e o direito de eliminação do titular.
2. **Trilha de auditoria:** prazo mínimo de guarda e se algum dia se arquiva fora do banco.
3. **Ledger de IA:** a correlação com o atendimento é a parte pessoal; pseudonimizar depois de N dias mantendo o consumo agregado.
4. **Numeração de sessões:** o número nunca é reemitido (ADR 0012), então um expurgo cria lacuna conhecida; as referências cruzadas (ContactCenter, Agents) precisam ser tratadas junto.
5. **Mortos:** quem autoriza o descarte, com que trilha, e depois de quanto tempo.
6. **Arquivos de mídia** (ADR 0028) e o **payload bruto** (se um dia for guardado): a tabela `conversations.message_attachments` já é classificada como dado pessoal (só relatório). **O conteúdo dos arquivos que o atendente ENVIA já tem prazo de guarda e expurgo** (categoria `OutboundAttachments`, ADR 0031) e **o mecanismo para os que o CLIENTE mandou existe** (categoria `InboundAttachments`, ADR 0032), **mas está desligado (prazo 0) até o proprietário decidir o prazo**: definir o prazo (recomendação técnica: 90 dias), o apagamento por titular (pedido de eliminação de um contato) e se o expurgo da linha, e não só do objeto, também é necessário. O payload bruto ganha categoria e prazo próprios se um dia for guardado.

7. **Lacunas do FAQ** (ADR 0030): `agents.knowledge_gaps` (hash com segredo do tenant de uma pergunta de cliente, contagem, datas e a decisão do administrador) e `agents.knowledge_gap_occurrences` (**só referências** ao atendimento, à conversa e à mensagem; nenhum texto) são classificadas como **dado pessoal (só relatório)**: nenhuma rotina as expurga. Definir o prazo de guarda das ocorrências (a contagem de uma lacuna é derivada delas e cairia junto) e o que fazer com a lacuna cujas referências foram eliminadas: hoje a revisão trata a mensagem removida ("indisponível") e a lacuna continua utilizável. A chave mestra do hash é segredo do ambiente (`CONHECIMENTO-DO-FAQ.md`).

Quando o negócio decidir, cada item vira uma categoria nova com tarefa própria, sempre com relatório antes, confirmação e teste de que o que é de referência continua intacto.
