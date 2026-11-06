# Operação, supervisão e recuperação (ADR 0026)

Como saber se o ambiente está de pé, o que cada alerta significa e o que fazer. Sem sistema de monitoramento: um comando, tarefas do Agendador e alertas em arquivo ou webhook.

## O comando que importa

```powershell
.\tools\ops\Status-Ambiente.ps1                      # visão geral (aplicação + sistema)
.\tools\ops\Status-Ambiente.ps1 -Empresa lab-exemplo # só a empresa (sem a retenção, que é do ambiente todo)
.\tools\ops\Status-Ambiente.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -ArquivoDeConfiguracaoDoBackup D:\config\backup-agendado.json
```

(a parte da aplicação é o comando do host `dotnet run --project backend/host/IaBimeta.Api --no-launch-profile -- status [--tenant <empresa>] [--api-url <url>] [--skip api,worker]`). Somente leitura. **Saída:** 0 tudo em ordem, 1 pontos de atenção, 2 falha que precisa de intervenção (64 = uso inválido do comando). Cada falha vem com a ação. Nenhuma senha, frase ou URL é impressa.

## O que é verificado

| Seção | O que olha | Crítico quando |
|---|---|---|
| PostgreSQL | conexão, latência, versão, tamanho, conexões usadas | inacessível |
| Migrations | as de cada módulo contra o código | pendentes (código novo contra banco antigo falha); atenção se o banco está à frente do código |
| Api | `GET /health` (padrão `http://localhost:5155/health`, ou `--api-url`) | não responde ou responde erro |
| Worker | atividade das conexões dele no banco (veja abaixo) | nenhuma conexão (parado) ou silêncio maior que o limite (travado) |
| `outbox:conversations`, `outbox:channels` | eventos entregues pelo relay | evento elegível parado além do limite; entrega interrompida (lease vencido); **atenção** para mortos |
| `channels` | contas e saída | conta com o envio **PAUSADO**; saída sem desfecho; **atenção** para chamada sem desfecho e resultado ambíguo |
| `agents` | turnos de IA | turno vencido sem executar; executor interrompido; **atenção** para turnos mortos (com o motivo) |
| `contact-center` | fila | **atenção** para conversa esperando atendente além do limite e auditoria parada |
| Retenção | o que o expurgo removeria agora, bloqueios, agendamento | atenção se há linhas vencidas e o agendamento está desligado, ou se o expurgo do Inbox está bloqueado |
| Backup, Cópia externa, Disco, Agendador | `estado-backup.json`, `ALERTA-BACKUP.txt`, destino externo, espaço livre, tarefas `ia.bimeta-*` | última execução falhou; último bom mais velho que o limite; destino inacessível; < 1 GB livre; tarefa com código de erro |

**Como o Worker é visto:** ele não tem porta. Os hosts nomeiam as conexões (`Application Name=IaBimeta.Worker` e `IaBimeta.Api`) e o laço do relay consulta o banco a cada poucos segundos, então a **última atividade** das conexões `IaBimeta.Worker` em `pg_stat_activity` diz se ele anda. Sem tabela nem migração. Cuidados: o papel do banco só enxerga as sessões do próprio papel (use o mesmo para Api, Worker e o diagnóstico); se você aumentar muito `Messaging:PollInterval`, aumente `Health:WorkerSilenceAfter`.

## Limites (seção `Health`)

| Chave | Padrão | O que é |
|---|---|---|
| `Health:EventStuckAfter` | 5 min | evento elegível (ou com lease vencido) sem ser entregue; auditoria parada |
| `Health:OutboundStuckAfter` | 5 min | mensagem de saída sem desfecho |
| `Health:TurnStuckAfter` | 5 min | turno de IA vencido ou com lease vencido |
| `Health:QueueWaitingAfter` | 30 min | conversa esperando atendente |
| `Health:WorkerSilenceAfter` | 60 s | silêncio das conexões do Worker |
| `Health:RecentFailureWindow` | 24 h | janela de resultado ambíguo e de turnos mortos |
| `Health:ApiUrl` | — | URL de verificação da Api (ou `--api-url`) |

Valor zero ou negativo impede a partida. Os limites servem à homologação; ajuste com o que a vazão real mostrar.

## Monitor e backup agendados

1. Defina **variáveis de ambiente do usuário** (não em arquivo versionado): a conexão do banco (`IABIMETA_HML_CONNECTION`), a **frase de cifra** do backup (`IABIMETA_BACKUP_KEY`, 16+ caracteres, guardada também **fora da máquina**) e, se quiser, a URL do webhook de alerta (`IABIMETA_ALERTA_WEBHOOK`: Slack, Teams, ntfy ou outro que aceite `POST {"text": ...}`).
2. Copie `tools\ops\backup-agendado.exemplo.json` para fora do repositório e ajuste (pasta, destino externo, versões, ensaio periódico). O JSON só guarda os **nomes** das variáveis.
3. Compile o Api uma vez (`dotnet build backend\host\IaBimeta.Api`) e rode `.\tools\ops\Registrar-Agendamento.ps1 -ArquivoDeConfiguracao D:\config\backup-agendado.json` (mostra o que faria). Com `-Registrar` cria `ia.bimeta-backup` (diária, 02:30 por padrão) e `ia.bimeta-monitor` (a cada 15 min: `Status-Ambiente.ps1 -AlertarSeCritico`). As tarefas rodam como o **usuário atual, só com a sessão aberta** (nenhuma senha é guardada); o computador desligado na hora roda ao ligar. `-Remover` apaga só essas duas tarefas.
4. O Worker pode agendar o **expurgo técnico** (`Retention:Schedule:Enabled=true`, hora em `TimeOfDayUtc`): veja `RETENCAO-DE-DADOS.md`.

**Alertas:** `ALERTA-BACKUP.txt` (falha do backup; some no próximo sucesso) e `ALERTA-AMBIENTE.txt` (falha crítica do monitor; some quando o ambiente volta ao normal) na pasta de backups; e o webhook, se configurado, sem repetir o mesmo alerta antes de 6 h. Todo texto passa por um filtro que remove valores das variáveis citadas, senhas de string de conexão e URLs.

## O que fazer, falha por falha

| Você vê | O que significa | O que fazer |
|---|---|---|
| **Worker PARADO** (nenhuma conexão) | o processo caiu ou não foi iniciado | Suba o Worker. Nada se perde: o webhook continua gravando no Inbox durável, e ao subir o relay entrega o que ficou, uma vez só (idempotência por evento). |
| **Worker travado** (silêncio longo) | processo vivo sem atividade | Confira o log e reinicie. A retomada é segura. |
| **evento(s) elegível(is) sem ser entregue(s)** | o relay não anda | Quase sempre o Worker parado. Reiniciar entrega o que ficou. |
| **entrega interrompida (lease vencido)** | o relay caiu no meio de um evento | Suba o Worker: o evento volta a ser elegível e é reentregue sem duplicar o efeito. |
| **evento(s) morto(s)** | falha permanente ou tentativas esgotadas | `tools\ops\Get-DiagnosticoDeEnvio.ps1` mostra o motivo. Reenfileirar ou descartar só com autorização; nunca apagar à mão. Mortos antigos bloqueiam o expurgo das marcas do Inbox. |
| **conta ... PAUSADO** | o provedor recusou a conta (token expirado ou revogado, credencial) | Corrija a causa, `Test-ProntidaoWhatsApp.ps1 -Live` e retome (`POST /channels/accounts/{id}/sending/resume`). Enquanto pausada, o atendente não consegue responder ao cliente por ela. |
| **saída sem desfecho** | o Sender não anda | Worker parado; ao subir, retoma sem duplicar (a intenção é gravada antes de chamar o provedor). |
| **resultado ambíguo** | o provedor pode ter entregue a mensagem | Nunca é reenviada sozinha. Confira com o cliente ou pela confirmação de entrega; reenvio só por ação autorizada do atendente. |
| **turno(s) de IA vencido(s)** | o executor não anda, ou a IA está indisponível | Worker no ar? IA do tenant configurada (`ai-not-configured`)? Cota esgotada (`ai-quota-or-billing`)? O cliente fica sem resposta enquanto isso. |
| **turno(s) de IA morto(s)** (com o motivo) | a IA falhou de vez; a conversa foi ao atendente | Cota e credencial: ação do tenant na conta do provedor. `ai-not-configured`: configuração de IA e agente publicado (`NOVA-EMPRESA.md`). |
| **conversa(s) esperando atendente** | sem atendente disponível na fila | Presença dos atendentes, membros e horários da fila. |
| **migrations pendentes** | código novo contra banco antigo | Backup e aplique (`provision` ou o Api em Development). |
| **PostgreSQL inacessível** | nada processa | Suba o PostgreSQL (porta 5433), confira a conexão. |
| **Backup: última execução FALHOU** | veja a etapa na mensagem | Corrija a causa e rode `Backup-Agendado.ps1`; o alerta some no próximo sucesso. |
| **Backup: último bom ha N h** (acima do limite) | a tarefa não roda | Confira o Agendador e `backup-agendado.log`. |
| **ensaio de restauração reprovou** | não confie nesse backup | `Ensaiar-Restauracao.ps1` e investigue. |

## Por que não se perde nem se duplica

- O **webhook** só valida a assinatura, deduplica e grava no Inbox durável antes do 200: com o Worker caído a Meta continua entregando e nada se perde.
- Os eventos são entregues **pelo menos uma vez**, em ordem, com **idempotência por `EventId`**: reentregar depois de uma queda não repete o efeito.
- O turno de IA só tem a resposta aceita **uma vez**, por cerca de versão: a chamada cortada pela queda é refeita, a resposta ao cliente sai uma vez.
- A **intenção** de enviar é gravada **antes** de chamar o provedor. Se o processo cai no meio da chamada, o desfecho é **ambíguo** e a mensagem **nunca é reenviada** sozinha (o custo é um aviso para o atendente, não duas mensagens ao cliente).

Provado por testes com Api e Worker reais: `RecuperacaoDoAmbienteTests` (Worker derrubado; Worker como processo morto à força no meio da chamada ao modelo e no meio do envio).

## Restaurar o ambiente a partir de um backup

1. Pare o Api e o Worker. Faça um backup do estado atual, se ainda houver banco.
2. `Verificar-Backup.ps1 -Pasta <versão> -ChaveEnv IABIMETA_BACKUP_KEY` (completa) e, de preferência, `Ensaiar-Restauracao.ps1`.
3. `Restaurar-Backup.ps1 -Pasta <versão> -BancoDeDestino iabimeta_hml_restaurado -Origem Ambiente -VariavelDeConexao ... -ChaveEnv IABIMETA_BACKUP_KEY` (papel com `CREATEDB`; **nunca** sobre banco existente).
4. Confira o resultado (o script compara as contagens), aponte a conexão para o banco novo (ou renomeie os bancos) e suba o Api e o Worker. `Status-Ambiente.ps1` deve voltar a 0.
5. Mensagens recebidas depois do backup e antes da queda **não** estão no backup (backup lógico: a perda é o intervalo entre backups). A Meta reenvia webhooks não confirmados por até 7 dias, e o Inbox deduplica.
