# ADR 0026 — Automação operacional, supervisão e recuperação do ambiente de homologação

- **Status:** **Aceito para o MVP (ratificado pelo decisor; status sincronizado em 09/11/2026).** Implementado em 06/11/2026 sob a autorização do decisor ("automação operacional e recuperação do ambiente de homologação"), depois da ratificação do ADR 0025. Nenhuma chamada real à Meta ou à OpenAI; nenhum banco existente foi alterado ou apagado; nenhuma tarefa foi registrada no Agendador da máquina.
- **Data:** 06/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0006 e 0007 (processamento assíncrono e turnos), 0011 (DP-06), 0020 (confiabilidade do envio), 0025 (senhas, retenção, backup), `docs/operations/OPERACAO-E-SUPERVISAO.md`, `BACKUP-E-RESTAURACAO.md`, `RETENCAO-DE-DADOS.md`
- **Preserva:** a fronteira entre módulos (cada sonda lê só as tabelas do próprio módulo), o contexto de tenant só pelo host, a regra de que segredo não entra em log, arquivo, estado, alerta nem commit, a regra de que dado pessoal e registro de referência não se apagam, e a regra de que nada destrutivo roda em banco existente sem confirmação explícita.

## Contexto

O MVP já recebia, respondia e atendia, e o ADR 0025 deu retenção, senhas e scripts de backup manuais. Para operar uma homologação sem alguém olhando o tempo todo, faltava: backup que roda sozinho e avisa quando falha, backup protegido e fora da máquina, expurgo técnico agendado, uma forma simples de saber se o Api, o Worker, o PostgreSQL e as filas estão andando, e a prova de que, quando o Worker cai, nada se perde nem se duplica.

## Decisão

### 1. Backup automático (reaproveita `Backup-Banco`, `Verificar-Backup`, `Restaurar-Backup`)

`Backup-Agendado.ps1`: cada execução cria a versão `agendado-AAAAMMDD-HHMMSS` (dump + `manifesto.json`), **verifica** (SHA-256, tamanho, `pg_restore --list`), opcionalmente **cifra**, **copia** para fora, **ensaia a restauração** de tempos em tempos e aplica a **política de versões**. Configuração por parâmetros ou por JSON (`backup-agendado.exemplo.json`). O agendamento é do **Agendador de Tarefas do Windows** (`Registrar-Agendamento.ps1`, que só mostra o que faria sem `-Registrar`, roda como o usuário atual com a sessão aberta e **não guarda senha**). Estado em `estado-backup.json` e log em `backup-agendado.log`, sem segredo.

**Alerta de falha:** código de saída 1 (o Agendador mostra), `ALERTA-BACKUP.txt` (some no próximo sucesso) e, se configurado, POST JSON num webhook cuja URL vem **só** de variável de ambiente. A versão que falhou no meio fica marcada `INVALIDO.txt`: nunca conta como backup bom e nunca é apagada sozinha.

**Política de versões explícita:** mantém as N mais recentes (N ≥ 3) e nunca apaga versão com menos de `ManterDiasMinimos` dias; só atua em pastas `agendado-*` (nunca em backups manuais) e só depois de um backup novo verificado. **Sem N configurado, nada é apagado.**

### 2. Proteção dos backups

- **Criptografia** só com o .NET do PowerShell 5.1: AES-256-CBC + HMAC-SHA256 (cifra e depois autentica; o MAC é conferido antes de gerar qualquer byte de texto claro), chave derivada por PBKDF2-SHA256 (600.000 iterações, sal novo por arquivo) de uma **frase lida de variável de ambiente** (nunca impressa, nunca em argumento). A ida e volta é conferida (decifra, compara o SHA-256 e lê o dump) **antes** de apagar o dump em texto claro. Frase errada e arquivo adulterado falham do mesmo modo. **Perder a frase é perder o backup cifrado.**
- **Cópia externa** para uma pasta (outro disco, compartilhamento UNC, pasta de nuvem sincronizada): sem credencial no script (quem autentica é o sistema operacional), **nunca sobrescreve**, copia para `.parcial` e só renomeia depois de conferir o SHA-256, nunca cria a raiz do destino e **recusa copiar backup sem cifra** (salvo `-PermitirExternoSemCriptografia`, explícito). Apagar cópias externas só com `ExternoManterUltimos` explícito.
- Verificar e restaurar aceitam `-ChaveEnv`; sem a frase a verificação é **parcial** e avisa, e restaurar o cifrado é recusado.

### 3. Expurgo técnico agendado

O Worker roda, uma vez por dia a partir de `Retention:Schedule:TimeOfDayUtc`, as **mesmas tarefas** do comando `retention purge`, com os prazos por empresa e os bloqueios de segurança (marcas do Inbox bloqueadas por evento morto antigo). **Desligado por padrão** (`Retention:Schedule:Enabled`). Só dado técnico: mensagens, contatos, atendimentos, usuários, trilha de auditoria e ledger de IA continuam **sem** tarefa de expurgo. Quem estava fora do ar na hora marcada executa ao voltar; depois de um sucesso, só no dia seguinte; falha registra só o tipo e tenta de novo depois de `RetryAfter`. O log traz só contagens.

### 4. Supervisão

- **Sondas de saúde por módulo** (`IHealthProbe`, no SharedKernel): cada módulo lê **só as próprias tabelas**, somente leitura, sem conteúdo de mensagem. Outbox (evento elegível parado, lease vencido, mortos), Channels (conta com envio **pausada**, saída sem desfecho, chamada sem desfecho, resultado ambíguo), Agents (turno vencido, executor interrompido, turnos mortos com o motivo), ContactCenter (conversa esperando além do limite, auditoria parada). Limites em `Health:*`, validados na partida.
- **PostgreSQL, migrations e Api** são verificados pelo host (conexão, versão, tamanho, conexões, migrations pendentes ou à frente do código, `GET /health`).
- **Worker:** não tem porta. Os hosts passam a nomear as conexões (`Application Name=IaBimeta.Worker` / `IaBimeta.Api`) e o laço do relay consulta o banco a cada poucos segundos, então a **última atividade** das conexões do Worker (`pg_stat_activity.state_change`) diz se ele anda: nenhuma conexão = parado; conexões e silêncio longo = travado. Não exige tabela nem migração nova. Limite conhecido: o papel do banco só enxerga as sessões do próprio papel (Api, Worker e o diagnóstico devem usar o mesmo).

### 5. Diagnóstico

`status` (comando do host, sem rota HTTP) e `tools/ops/Status-Ambiente.ps1` (acrescenta o que é do sistema operacional: backup agendado, alerta, cópia externa, disco, tarefas `ia.bimeta-*`). Cada falha traz **o que fazer**. Saída 0 (em ordem), 1 (atenção), 2 (intervenção). Com `-AlertarSeCritico` serve de **monitor agendado**: `ALERTA-AMBIENTE.txt` e webhook opcional, sem repetir o mesmo alerta antes de 6 h.

### 6. Recuperação provada

- **Worker derrubado:** com Api e Worker reais, o webhook segue respondendo 200 e gravando no Inbox; o `status` aponta o Worker e os eventos parados; ao voltar, cada mensagem é processada **uma vez** (uma sessão por cliente, uma resposta da IA, um envio, nenhum duplicado ou perdido, inclusive com a repetição do webhook pela Meta).
- **Worker morto à força (kill do processo do sistema operacional, sem parada graciosa):** no meio da chamada ao modelo (outro processo retoma com uma resposta e um envio) e no meio do envio ao provedor (resultado **ambíguo**, nunca reenviado, apontado pelo `status`).
- **Restauração em instância isolada**, inclusive de backup cifrado, com contagens idênticas; recusa de banco existente, de frase errada e de arquivo adulterado.

## O que fica fora (pendências)

- **Hospedagem e supervisão do processo:** serviço do Windows (ou `systemd`) para Api e Worker com reinício automático, logs persistentes e rotação, e o cofre de segredos para as chaves mestras: infraestrutura, decisão do decisor.
- **Chave de cifra do backup guardada fora da máquina** (cofre ou cópia protegida) e **destino externo real** (outro disco ou compartilhamento): configuração do ambiente. Sem eles o backup cifrado só protege contra roubo do arquivo, não contra perda da máquina.
- **PITR** (arquivamento de WAL) e backup físico: o backup é lógico (`pg_dump`). A janela de perda é o intervalo entre backups.
- **Métricas exportadas e painéis** (por decisão: sem sistema de monitoramento complexo); **alerta por e-mail** (hoje: arquivo e webhook).
- **Descarte autorizado de eventos mortos** e **expurgo de dado pessoal** (aguardam decisão jurídica, ADR 0025).
- Velocidade da cifra em PowerShell: cerca de 0,7 s por MB entre cifrar e verificar (90 MB em ~65 s); para bancos de dezenas de GB, medir e, se preciso, mover a cifra para um utilitário compilado.

## Consequências

- Código novo: `Health.cs` e `Retention.cs` (opções do agendamento) no SharedKernel; sondas nos módulos Channels, Agents e ContactCenter e no Messaging; `RetentionRunner` movido para o Messaging; `StatusCommand`/`StatusCollector` e `RetentionSchedulerService` nos hosts; nome da aplicação nas conexões. Nenhuma migração, nenhum projeto novo, nenhuma aresta nova na matriz de dependências.
- Scripts em `tools/ops`: `Backup-Agendado`, `Status-Ambiente`, `Registrar-Agendamento`, `Testar-Automacao-De-Backup` e as bibliotecas `_Cifra`, `_Backup`, `_Comum`.
- Testes (ver `docs/operations/PRONTIDAO-MVP.md` para os totais do gate): integração do `status` e das sondas (PostgreSQL real), do expurgo agendado (Worker real), da recuperação com Api e Worker reais e com o Worker como processo morto à força; unitários da saúde e do agendamento; e `Testar-Automacao-De-Backup.ps1` (45 verificações do backup, da cifra, da cópia externa, das versões, do alerta e da ausência de segredos, numa instância PostgreSQL descartável).
