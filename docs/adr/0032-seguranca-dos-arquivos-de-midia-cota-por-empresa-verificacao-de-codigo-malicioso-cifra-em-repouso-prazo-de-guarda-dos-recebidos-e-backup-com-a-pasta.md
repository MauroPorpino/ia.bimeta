# ADR 0032 — Segurança dos arquivos de mídia para o primeiro piloto: cota por empresa, verificação de código malicioso, cifra em repouso, prazo de guarda dos recebidos e backup junto da pasta

- **Status:** **Aceito para o MVP** (implementado em 10/11/2026 sob a autorização do decisor, depois da ratificação do ADR 0031; **ratificado pelo decisor em 10/11/2026**, com os commits `8f9b46e`, `9230c4e`, `f9145c3` e `beb33f8`). Nenhuma chamada real à Meta ou à OpenAI; a migração só **acrescenta** duas colunas e uma restrição (backup do banco de teste feito antes: `pre-migracao-varredura-20261109-220605`); `main` intocada; sem push.
- **Data:** 10/11/2026
- **Decisor:** Mauro Porpino
- **Referências:** ADR 0005 e `BOUNDARIES.md`, 0009 (ligações mediadas pelo host), 0025 (retenção e backup), 0026 (operação e recuperação), 0028 (mídia recebida), 0031 (envio de arquivos, aceito), `docs/operations/SEGURANCA-DOS-ARQUIVOS.md`, `BACKUP-E-RESTAURACAO.md`, `RETENCAO-DE-DADOS.md`
- **Preserva:** nenhuma aresta nova na matriz de dependências (nem edição de `BOUNDARIES.md`, `ARCHITECTURE.md` ou `module-dependencies.json`); o Conversations continua dono do estado e do conteúdo dos arquivos; o Channels só busca no provedor e entrega os bytes; **um só caminho** de recebimento e de envio (nada paralelo); tenant e permissões só pelo `RequestContext`; nenhum arquivo, legenda ou nome vai a um provedor de IA; nenhum segredo, chave, conteúdo ou nome de arquivo em log, resposta, relatório ou trilha de auditoria.

## Contexto

O ADR 0028 e o ADR 0031 deixaram, como risco aceito para o MVP, quatro lacunas que não podem ir ao primeiro piloto com uma empresa real: **sem verificação de código malicioso**, **arquivos em texto claro no disco**, **sem limite de espaço por empresa** e **pasta de arquivos fora do backup** (e o arquivo do cliente sem prazo de guarda). A diretriz de 10/11/2026 (primeiro piloto desejado para 30/11/2026, condicionado a infraestrutura e credenciais externas) pede a **menor implementação segura e sustentável** de cada uma, sem comprometer o isolamento entre empresas, a autorização, a integridade nem a proteção contra arquivo malicioso.

**Fora do escopo (decidido):** busca semântica e embeddings, IA sobre o conteúdo do arquivo, rotação sofisticada de chaves (re-cifrar objetos com a chave nova), armazenamento em nuvem real, cota por usuário, prazo de guarda por tipo de arquivo, quarentena com interface, e um segundo mecanismo de varredura além do `clamd`.

## Decisão

### 1. Cota de armazenamento por empresa

- `Conversations:Attachments:TenantQuotaBytes` (padrão **5 GiB**; `0` desliga). Conta os bytes dos arquivos **disponíveis** da empresa, **recebidos e enviados juntos** (o tamanho do conteúdo, não o do objeto cifrado).
- **Exata sob concorrência:** a conferência e a gravação do estado acontecem na mesma transação, sob uma trava consultiva do banco **por empresa** (`pg_advisory_xact_lock`); duas conclusões ao mesmo tempo nunca passam do limite, e a trava de uma empresa nunca bloqueia outra.
- **Arquivo recebido que não cabe:** o objeto recém-gravado é apagado e a linha passa a `Unavailable` com o motivo `quota-exceeded` (a mensagem, o nome e a legenda ficam; o atendente vê o motivo e pede que o cliente reenvie). **Arquivo enviado pelo atendente que não cabe:** recusado antes de gravar qualquer coisa (`422 MediaRejected`, motivo `quota-exceeded`).
- **Espaço devolvido:** o prazo de guarda (seção 4) e o reparo da conferência (seção 5) liberam cota, porque só o arquivo `Available` conta.
- **Diagnóstico:** `attachments report` e a seção "Arquivos" do `status` (atenção a 80%, falha ao atingir 100%, só números por empresa).

### 2. Verificação de código malicioso

- **Mecanismo do piloto: o daemon do ClamAV (`clamd`) por TCP, pelo protocolo `INSTREAM`** (conferido na documentação oficial do protocolo em 10/11/2026: comando `zINSTREAM\0`, blocos com 4 bytes de tamanho em ordem de rede, terminador de tamanho zero, respostas `stream: OK` e `stream: <assinatura> FOUND`, e o erro `INSTREAM size limit exceeded` quando passa de `StreamMaxLength`). O conteúdo vai por soquete: **nenhum arquivo temporário** é criado, nada do conteúdo ou da assinatura vai a log. Funciona em Linux e em qualquer lugar onde o `clamd` rode (`Conversations:Attachments:Scanner:{Mode=ClamAv,Host,Port,Timeout}`).
- **Só `OK` e `FOUND` são respostas definitivas.** Conexão recusada, tempo esgotado, erro do daemon (inclusive o limite de tamanho), resposta vazia, cortada ou ilegível são `Unavailable`, **nunca "limpo"**. O cancelamento de quem chamou propaga como cancelamento.
- **Detecção embutida da amostra EICAR** (teste padrão e inofensiva por desenho), **sempre** e **antes** de qualquer mecanismo: é com ela que os testes e a homologação provam o bloqueio sem código malicioso de verdade. A cadeia é montada em tempo de execução e um teste garante que ela nunca aparece inteira no código-fonte (os antivírus das máquinas tratariam o repositório como infectado).
- **`Scanner:Required = true` (o ambiente do piloto): falha fechada.** Só o arquivo que o verificador declarou **limpo** vira disponível, abre ou é enviado ao Sender (`message_attachments.scan_status`, coluna nova: `0` não verificado, `1` limpo). Verificador fora do ar nunca libera:
  - arquivo **recebido**: não é guardado, o evento de busca repete com espera de 2 minutos; esgotadas as tentativas, o arquivo fica `Unavailable` com o motivo `scan-unavailable`;
  - arquivo **enviado**: recusado antes de gravar, com `503 MediaRejected` e o motivo `scanner-unavailable` (o servidor não guardou nada e repetir o mesmo pedido é seguro); infectado é `422` com `malware`;
  - arquivo **guardado antes** de o verificador existir (`scan_status = 0`): **não abre nem vai ao Sender** até a varredura posterior (`attachments rescan --confirm`).
- Sem `Required`, o verificador fora do ar deixa o arquivo passar como **não verificado** (o padrão do desenvolvimento). `Required` sem mecanismo configurado **impede a partida**.
- **Limite do daemon:** `StreamMaxLength` do `clamd.conf` precisa ser **maior ou igual ao maior limite de tipo** configurado (padrão: documento de 20 MB); abaixo disso o daemon responde erro e o arquivo grande fica indisponível, sempre sem liberar.

### 3. Cifra em repouso

- **AES-256-GCM**, as mesmas convenções de chave das credenciais do Channels (Base64 de 32 bytes, identificador da chave ativa, chaves antigas mantidas só para decifrar), em seção própria `Conversations:Attachments:Encryption:{ActiveKeyId,Keys:<id>,Required}`; **sem referência ao módulo Channels**. As chaves vêm do ambiente (nunca do repositório nem do banco). `Required` sem chave ativa impede a partida.
- **Chave derivada por empresa** (HKDF-SHA256 sobre a chave mestra e o tenant) e **dado autenticado adicional** com o tenant, o arquivo e o identificador da chave: o objeto de uma empresa **não abre sob outra**, um objeto copiado para outro arquivo não abre, e qualquer alteração ou truncamento é detectado. Formato: `IABA | versão | tamanho do id | id da chave | nonce(12) | tag(16) | texto cifrado` (nonce aleatório a cada gravação).
- **Chave ausente, errada ou objeto adulterado nunca devolvem conteúdo** (nem o texto cifrado como se fosse o arquivo): a abertura devolve "indisponível", a conferência relata "ilegível" e nada é alterado. O sistema operacional que recusa a leitura (por exemplo, o antivírus do servidor pôs o objeto em quarentena) é tratado do mesmo modo, nunca como erro do servidor.
- **Integridade na abertura:** além da cifra, todo arquivo aberto tem o tamanho e o SHA-256 conferidos contra os gravados na linha; conteúdo alterado ou sumido não é entregue (vale também sem cifra).
- **Migração do que já existe:** objetos em texto claro continuam legíveis até `attachments encrypt-existing --confirm` convertê-los no lugar (atômico, idempotente, por empresa se pedido).
- **Rotação simples:** nova chave ativa, antigas mantidas em `Keys`. Re-cifrar com a chave nova **não** foi implementado (decisão: sem rotação sofisticada).
- **Perder a chave é perder os arquivos cifrados.** A chave é guardada fora da máquina, junto da frase de cifra do backup, e **é a mesma** para restaurar em outro ambiente.

### 4. Prazo de guarda dos arquivos e o que o expurgo audita

- Duas categorias da retenção técnica, mesma rotina, mesmo comando (`retention`) e mesmo agendamento do Worker: **`OutboundAttachments`** (enviados, 30 dias, ratificado no ADR 0031) e **`InboundAttachments`** (recebidos, **padrão 0 = desligado**). Prazo `0` nunca apaga nada, só informa: **nenhum dado pessoal é apagado sem política expressamente definida**. O prazo dos recebidos é **decisão do proprietário** (a recomendação técnica é 90 dias, a confirmar com a base legal e o setor do cliente); com a sobrescrita por empresa de sempre (`Retention:Tenants:<slug>`).
- O expurgo apaga **só o objeto**; a linha vira `Unavailable` com o motivo `retention`, sem chave, tipo, tamanho nem resumo (a mensagem, o nome e a legenda ficam no histórico). Idempotente; a passada de uma empresa nunca toca a de outra; um sentido nunca apaga o do outro.
- **Auditável, com o registro ANTES de apagar** (`IRetentionAuditSink`, porta do SharedKernel que o Api e o Worker implementam com o `IAuditWriter`, no contexto de sistema de cada empresa, origem `retention`): cada lote vai para a trilha de auditoria (`conversations.attachment.expired`, `.orphan-removed`, `.malware-blocked`, `.lost`; só identificadores) **antes** de o objeto sair. Se a trilha falhar, **nada é apagado** e a próxima rodada tenta de novo; se apagar falhar depois, a próxima rodada repete e a trilha pode ter uma entrada repetida, nunca uma lacuna.

### 5. Manutenção e consistência banco × pasta (`attachments`)

Comando de operação do host (`report`, `check [--repair --confirm] [--orphan-days N] [--tenant ID]`, `rescan [--confirm] [--tenant ID]`, `encrypt-existing [--confirm] [--tenant ID]`), sem rota HTTP, que roda com a MESMA configuração do Api e do Worker. Só identificadores e números saem. O que altera algo só age com `--confirm`.

- **`check`** confere cada linha disponível contra o objeto (existência, decifragem, tamanho e SHA-256) e lista os objetos sem linha. **Só o reparo age:** a linha **sem objeto** vira `Unavailable`/`lost` e o órfão **mais antigo que o prazo mínimo** é apagado (ambos com a trilha antes). O que **não decifra** ou **não confere** nunca é alterado (pode ser só a chave errada no ambiente). `--tenant` limita o alcance do reparo a uma empresa. **Trava contra o erro mais comum:** se todas as linhas conferidas estão sem objeto e a pasta não tem nenhum objeto para o alcance pedido (quase sempre `RootPath` errado ou pasta não restaurada), o reparo **recusa** sem alterar nada, porque marcar tudo como perdido apagaria no banco a chave, o tamanho e o resumo que permitem reencontrar os arquivos; a perda real se confirma com `--allow-empty-folder`.
- **`rescan`** verifica os arquivos nunca verificados: limpo vira verificado; **infectado é bloqueado e apagado** (com a trilha); verificador fora do ar deixa como está; sem mecanismo configurado, recusa rodar.
- **`status`**, seção "Arquivos": cota usada por empresa, arquivos guardados sem verificação enquanto a varredura é exigida e se o verificador responde (crítico se exigido e fora do ar).

### 6. Backup e restauração dos arquivos (PowerShell, sem dependência nova)

- `Backup-Banco.ps1 -PastaDeAnexos` copia a pasta **no mesmo backup**, **depois** do `pg_dump` (toda linha do dump encontra o seu objeto, salvo o que a retenção apagar no meio; objetos criados depois do dump são órfãos inofensivos), como estão (os cifrados continuam cifrados: a ferramenta nunca vê a chave), com um manifesto determinístico (SHA-256 e tamanho de cada objeto) cujo resumo vai no `manifesto.json`. Falha da cópia marca o backup como inválido. O backup agendado, a cifra do backup, a cópia externa, o ensaio de restauração e a política de versões **cobrem os anexos**; com cifra do backup configurada, anexo em texto claro **não sai** da máquina sem decisão explícita.
- `Verificar-Backup.ps1` reprova (código 1) byte alterado, objeto ausente, objeto a mais, truncado ou manifesto adulterado. `Restaurar-Backup.ps1 -PastaDeAnexosDestino` e `Restaurar-Anexos.ps1` restauram **só em pasta nova** (nunca sobrescrevem nem apagam), conferem o SHA-256 de cada objeto e, sem o parâmetro, **avisam** que os arquivos não foram restaurados. `Ensaiar-Restauracao.ps1 -SimularPerdaDaOrigem` prova a restauração depois da **perda da pasta original**.
- **Depois de restaurar**, o procedimento manda rodar `attachments check` (primeiro sem `--repair`) para tratar as linhas "disponíveis" sem arquivo; a restauração só abre arquivo cifrado se o ambiente novo tiver a **mesma chave**.

## Contratos e regras de arquitetura alterados (testes fixados)

1. `AttachmentChangeOutcome.ScanUnavailable` (membro novo e aditivo do contrato do Conversations, que o Channels já consome); `ScanVerdict.Unavailable`; `RetentionCategory.InboundAttachments`; `IRetentionAuditSink` e `RetentionAuditItem` no SharedKernel (o host liga a implementação); `AttachmentContentUnreadableException` e `StoredAttachmentObject` nas portas do Conversations.
2. `RetentionInventory.ObjectStoreTasks` lista as **duas** tarefas de objeto (a regra "só tabela técnica tem expurgo" continua valendo, com as exceções nomeadas).
3. A migração `20261110005153_AttachmentScanStatus` **acrescenta** `scan_status` (smallint, padrão `0`, com `CHECK` em `0` ou `1`) e `scanned_at` em `conversations.message_attachments`; as linhas existentes ficam "não verificadas".

## O que foi conferido e o que não foi

Conferido: o protocolo `INSTREAM` do `clamd` (página oficial do protocolo, 10/11/2026) e o de cada resposta que o cliente interpreta. **Não conferido (a fazer no ambiente de homologação):** o cliente **contra um `clamd` real**: não há ClamAV instalado nesta máquina (nem se instalou nada sem autorização), então a integração foi provada contra um daemon **falso que fala o protocolo de verdade** (enquadramento em blocos de 64 KB, cada resposta e cada falha). A disponibilidade do `clamd` no Windows 10/Server não foi confirmada (a página oficial de pacotes lida lista Linux, BSD e macOS): o piloto deve rodar em Linux ou apontar `Host` para um daemon em outra máquina; **Windows Defender (varredura por linha de comando) fica como alternativa a avaliar (P2)**. Também não foi medido o desempenho com muitos objetos ou gigabytes (a conferência e o backup crescem linearmente).

## Consequências

- O primeiro piloto roda com **verificação exigida, cifra exigida, cota por empresa, backup com a pasta e restauração ensaiada**, sem aresta nova entre módulos.
- **Risco aceito:** a verificação **exigida** depende do `clamd` estar de pé: com ele fora, o arquivo do cliente fica pendente (e, esgotadas as tentativas, indisponível) e o atendente não consegue **enviar** arquivo. É o custo da falha fechada; o `status` acusa o verificador que não responde.
- **Risco aceito:** sem verificação, ou com o verificador desatualizado, o ClamAV não detecta tudo. A proteção continua em camadas (tipo restrito pelo conteúdo, estrutura conferida, tamanho limitado, nada executável, entrega sem execução no navegador).
- **Risco aceito:** o arquivo passa **inteiro pela memória** do Api e do Worker (até o limite do tipo), agora também pela varredura.
- **Ligar a verificação exigida num ambiente com arquivos antigos os bloqueia** até `attachments rescan --confirm` (é o comportamento desejado: arquivo nunca verificado não abre).
- **Prazo dos recebidos desligado por padrão:** sem decisão do proprietário os arquivos dos clientes ficam no disco (cifrados e dentro da cota), nunca apagados por política suposta.
- A cota por empresa serializa só as **conclusões de arquivo da mesma empresa** (uma consulta de soma por conclusão); não há custo para empresas diferentes.

## Plano de reversão

A migração só **acrescenta** colunas e uma restrição (`Down` as remove sem apagar linha). Reverter o código não perde dado: com a cifra ligada, os objetos cifrados só abrem com o código novo e a chave (guardar a chave antes de reverter); desligar `Required` e a cifra faz os arquivos novos voltarem a ser gravados como antes, e os já cifrados continuam exigindo a chave. Os backups de anexos são pastas comuns do backup, sem efeito sobre o resto.

## Testes

Unidade e integração sem banco: `ArmazenamentoCifradoTests` (ida e volta, texto claro nunca no disco, nonce aleatório, empresa e arquivo autenticados, adulterado ou truncado, chave errada ou ausente sem repetir a chave, rotação simples, objeto antigo em texto claro, recusa do sistema operacional, validação da configuração) e `VerificadorDeArquivosTests` (cliente contra o `clamd` falso: enquadramento, cada resposta, prazo, cancelamento, EICAR embutida sem chamar o daemon, EICAR nunca inteira no código-fonte). Integração com PostgreSQL real e pastas de teste: `SegurancaDeAnexosTests` (cota exata sob concorrência, por empresa, recebidos e enviados, devolvida pelo prazo de guarda; verificação exigida e opcional nos dois sentidos; cifra e integridade na abertura) e `ManutencaoDeAnexosTests` (prazo dos dois sentidos com auditoria antes de apagar e falha da trilha que não apaga nada, alcance por empresa, órfãos, conferência com reparo, varredura posterior, conversão para a cifra, uso por empresa). Ponta a ponta com **Api, Worker e PostgreSQL reais** (Meta e OpenAI simuladas por HTTP, `clamd` falso): `SegurancaDeAnexosEmOperacaoTests` (verificador fora do ar e nova tentativa, esgotamento com o motivo, **Worker derrubado com a varredura em andamento**, infectado, cota do cliente e do atendente, configuração) e `ComandoDeAnexosTests` (o comando `attachments` e o `status`). Scripts: `Testar-Automacao-De-Backup.ps1` (103 verificações em instâncias PostgreSQL descartáveis: anexos no backup, verificação, restauração, perda da pasta original, backup agendado cifrado e cópia externa). Interface: textos dos motivos novos e o `503` da verificação (o painel explica a recusa ao lado do arquivo). **Gate completo de 10/11/2026: 4231 testes aprovados, 0 falhas, 0 ignorados** (2630 de unidade e arquitetura + 1601 de integração), frontend com 385 testes e build sem erro, grafo de projetos (10 regras) e formato dos arquivos em ordem.

## Demonstração (10/11/2026)

Feita pelos testes de ponta a ponta acima, com os **mesmos binários** do Api e do Worker em processo, PostgreSQL real (banco de teste, com backup antes da migração), a Meta e a OpenAI **simuladas** e o `clamd` falso: o arquivo do cliente ficou pendente e **sem objeto** com o verificador fora do ar, foi liberado **cifrado em disco** quando ele voltou, o infectado nunca abriu nem foi guardado, o Worker derrubado no meio da varredura foi substituído sem duplicar, a cota recusou o que não coube dos dois lados e o comando `attachments` acusou e reparou um arquivo sumido. Detalhes e resultados em `docs/operations/SEGURANCA-DOS-ARQUIVOS.md`. **Defeito encontrado e corrigido durante os testes:** o antivírus real da máquina (Windows Defender) bloqueou a leitura de um objeto com a amostra de teste que o próprio teste gravou, e o armazenamento deixava a `IOException` subir (erro do servidor); agora é tratada como objeto ilegível.
