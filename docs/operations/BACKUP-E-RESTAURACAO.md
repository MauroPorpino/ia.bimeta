# Backup e restauração do PostgreSQL (ADR 0025 e 0026)

Scripts em `tools\ops\` para o banco de desenvolvimento, o de teste ou o de homologação. Servem a qualquer origem pela mesma interface: `-Origem Desenvolvimento | Teste | Ambiente` (com `-VariavelDeConexao NOME`, o nome da variável de ambiente que guarda a string de conexão da homologação). Só o PostgreSQL do projeto (porta **5433**) é aceito. Segredos nunca são impressos: a senha vive numa variável de ambiente **deste processo**, durante cada chamada.

| Script | O que faz | Altera o banco? |
|---|---|---|
| `Backup-Agendado.ps1` | **o backup automático**: backup + verificação + cifra + cópia externa + ensaio periódico + política de versões + alerta (veja abaixo) | não (leitura) |
| `Backup-Banco.ps1` | `pg_dump -Fc` + `manifesto.json` (SHA-256, tamanho, entradas do dump, contagem exata de linhas por tabela) em `C:\Projetos\ferramentas\backups\ia.bimeta\<rótulo>-<data>\` | não (leitura) |
| `Verificar-Backup.ps1` | confere manifesto, tamanho, SHA-256 e a leitura do dump (`pg_restore --list`); recusa dump corrompido ou truncado; com `-ChaveEnv`, a verificação do backup **cifrado** é completa | não |
| `Restaurar-Backup.ps1` | verifica o backup, cria um banco **novo**, restaura (`--no-owner --no-privileges --exit-on-error`) e compara as contagens com o manifesto; `-ChaveEnv` para o cifrado | só cria o banco novo; **recusa** destino existente |
| `Ensaiar-Restauracao.ps1` | backup + restauração + conferências em **banco isolado**, mede os tempos e remove o que criou; `-ChaveEnv` cifra, decifra e restaura | não |
| `Registrar-Agendamento.ps1` | registra (ou remove) as tarefas `ia.bimeta-backup` e `ia.bimeta-monitor` no Agendador; sem `-Registrar` só mostra | só as duas tarefas |
| `Status-Ambiente.ps1` | estado geral, inclusive do backup (`OPERACAO-E-SUPERVISAO.md`) | não |
| `Testar-Automacao-De-Backup.ps1` | teste de ponta a ponta de tudo isto numa instância descartável | não (nenhum banco real) |
| `Backup-BancoDeDesenvolvimento.ps1` | o script anterior (só desenvolvimento), mantido | não |

## Regras que os scripts impõem

- **Nunca restaura sobre banco existente.** Não há opção para sobrescrever e nada é apagado. "Restaurar no lugar" é uma decisão humana: restaure em um banco novo, confira, e só então troque a string de conexão ou renomeie os bancos (`ALTER DATABASE ... RENAME TO ...`), com Api e Worker parados.
- O nome do banco de destino aceita só minúsculas, números e sublinhado (3 a 63 caracteres, começando por letra).
- O ensaio só apaga o que **ele mesmo** criou: a instância descartável (pasta `_instancia-ensaio-*` dentro da pasta de backups) ou o banco `iabimeta_ensaio_*` criado nesta execução.
- Um backup sem manifesto, ou cujo SHA-256, tamanho ou número de entradas não bate, **não é restaurado**.
- **Nenhum segredo** (senha do banco, frase de cifra, URL de webhook) em log, estado, alerta, manifesto ou saída: só os **nomes** das variáveis de ambiente.

## Backup automático (`Backup-Agendado.ps1`)

```powershell
.\tools\ops\Backup-Agendado.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION `
    -ChaveEnv IABIMETA_BACKUP_KEY -DestinoExterno \\servidor\backups\ia.bimeta -ManterUltimos 14 `
    -EnsaiarRestauracaoACadaDias 7 -AlertaWebhookEnv IABIMETA_ALERTA_WEBHOOK
.\tools\ops\Backup-Agendado.ps1 -ArquivoDeConfiguracao D:\config\backup-agendado.json   # o mesmo, por arquivo (backup-agendado.exemplo.json)
```

Cada execução faz, em ordem, e para na primeira falha:

1. **Backup** (`agendado-AAAAMMDD-HHMMSS`) e **verificação** (SHA-256, tamanho, leitura do dump).
2. **Cifra** (só com `-ChaveEnv`): AES-256 + HMAC; confere a ida e volta (decifra, compara o SHA-256 e lê o dump) **antes** de apagar o dump em texto claro. Depois só o `.dump.enc` fica no disco.
3. **Cópia externa** (só com `-DestinoExterno`): outra pasta, disco, compartilhamento UNC ou pasta de nuvem sincronizada. Sem credencial no script: quem autentica é o Windows (compartilhamento mapeado ou `cmdkey`). **Nunca sobrescreve**, copia para `.parcial`, confere o SHA-256 e só então renomeia; não cria a raiz do destino; **recusa copiar backup sem cifra** (salvo `-PermitirExternoSemCriptografia`).
4. **Ensaio de restauração** (só com `-EnsaiarRestauracaoACadaDias N`): restaura numa instância descartável e compara as contagens; reprovou = falha.
5. **Política de versões** (só com `-ManterUltimos N`, N ≥ 3): mantém as N mais recentes e **nunca apaga uma com menos de `-ManterDiasMinimos` dias** (padrão 7). Só toca pastas `agendado-*` (nunca backups manuais) e só depois de um backup novo verificado. **Sem N explícito, nada é apagado.** Na cópia externa, só com `-ExternoManterUltimos`. `-SoSimularVersoes` mostra o que removeria.

**Estado e alerta.** `estado-backup.json` (último sucesso, última falha, versão, tamanho, cifra, ensaio) e `backup-agendado.log` ficam na pasta de backups, sem segredo. **Falha:** código 1, `ALERTA-BACKUP.txt` na pasta (some no próximo sucesso) e, com `-AlertaWebhookEnv NOME` (a variável guarda a URL), um POST JSON `{"text": ...}`. A versão que falhou no meio fica marcada `INVALIDO.txt`: nunca conta como backup bom nem entra na política, e **nunca é apagada sozinha**.

### Proteção: frase de cifra

- A frase vem de uma **variável de ambiente do usuário** (16+ caracteres), nunca de argumento, arquivo versionado ou log. Cifra: AES-256-CBC + HMAC-SHA256 (cifra e depois autentica; o MAC é conferido **antes** de qualquer texto claro), chave por PBKDF2-SHA256 (600.000 iterações, sal novo por arquivo), tudo com o .NET do PowerShell 5.1.
- **Perder a frase é perder o backup cifrado.** Guarde-a **fora da máquina do banco** (cofre de senhas ou cópia protegida, separada do destino externo).
- Frase errada e arquivo alterado ou truncado falham do mesmo modo ("falha na autenticação"), sem produzir texto claro. Sem a frase, `Verificar-Backup.ps1` só confere o arquivo (tamanho e SHA-256) e **avisa** que a verificação é parcial; `Restaurar-Backup.ps1` recusa o cifrado sem `-ChaveEnv`.
- Velocidade: cerca de 0,7 s por MB entre cifrar e verificar (90 MB em ~65 s). Para bancos de dezenas de GB, meça antes de agendar; o limite é do PowerShell, não do algoritmo.
- Espaço: durante a execução existem, no pior caso, o dump, o arquivo cifrado e uma cópia decifrada temporária (~3 vezes o tamanho); o script confere o espaço antes e a pasta temporária `_tmp-*` é apagada.

### Agendar

`Registrar-Agendamento.ps1 -ArquivoDeConfiguracao <json>` mostra o que criaria; com `-Registrar`, registra no Agendador de Tarefas do Windows as tarefas `ia.bimeta-backup` (diária, `-HoraDoBackup`, padrão 02:30) e `ia.bimeta-monitor` (a cada `-MonitorACadaMinutos`, padrão 15). Rodam como o **usuário atual com a sessão aberta** (nenhuma senha guardada); computador desligado na hora = roda ao ligar. Recusa registrar se as variáveis citadas no JSON não existem. Passo a passo em `OPERACAO-E-SUPERVISAO.md`.

## Fazer um backup (manual)

```powershell
.\tools\ops\Backup-Banco.ps1 -Origem Desenvolvimento -Rotulo pre-migracao
.\tools\ops\Backup-Banco.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -Rotulo homologacao
.\tools\ops\Verificar-Backup.ps1 -Pasta C:\Projetos\ferramentas\backups\ia.bimeta\homologacao-20261106-130000
```

As contagens do manifesto saem logo depois do dump. Para comparação **exata** na restauração, o banco de origem deve estar em repouso (Api e Worker parados) durante o backup; se recebeu gravações, uma diferença é esperada e o script avisa.

## Ensaio de restauração (prova de que o backup restaura)

```powershell
.\tools\ops\Ensaiar-Restauracao.ps1 -Origem Teste
.\tools\ops\Ensaiar-Restauracao.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -ChaveEnv IABIMETA_BACKUP_KEY
```

Modos (`-Modo`):

- **`Instancia` (padrão):** sobe uma **instância PostgreSQL descartável** com os mesmos binários (`initdb` numa pasta temporária, porta livre, só loopback, senha aleatória), restaura nela e a apaga no fim. Não exige privilégio no servidor do projeto e não toca em nenhum banco real.
- **`Servidor`:** cria `iabimeta_ensaio_<data-hora>` no próprio servidor (exige um papel com `CREATEDB`; os papéis `iabimeta_dev` e `iabimeta_test` não têm) e o remove no fim.

`-Manter` não apaga nada no fim (para inspeção; a limpeza passa a ser manual).

**Resultados medidos (06/11/2026, banco de teste, ~90 MB, 54 tabelas, 290 entradas):** ensaio simples: backup 14,5 s, restauração 54,6 s; backup agendado **cifrado + cópia externa + ensaio de restauração**: 267 s no total (cifra e verificação ~65 s, ensaio ~100 s), **54 tabelas com contagem idêntica**, nenhuma restrição pendente, instância apagada; segunda execução (sem ensaio) em 87 s. Também exercitados: recusa de banco existente, de dump com 1 byte alterado (SHA-256), de dump truncado (tamanho), de frase errada e de restauração do cifrado sem a frase.

## Restaurar de verdade (homologação)

1. Pare o Api e o Worker.
2. `Backup-Banco.ps1` do estado atual (se ainda houver banco) e `Verificar-Backup.ps1 -ChaveEnv ...` do backup a restaurar.
3. `.\tools\ops\Restaurar-Backup.ps1 -Pasta <pasta do backup> -BancoDeDestino iabimeta_hml_restaurado -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -ChaveEnv IABIMETA_BACKUP_KEY` (o papel da conexão precisa de `CREATEDB`; `-ChaveEnv` só para backup cifrado).
4. Confira (o script já compara contagens), suba o Api contra o banco novo e teste o login e uma conversa.
5. Só então troque a conexão (ou renomeie os bancos). O banco antigo fica até você decidir removê-lo, por conta própria.

## Rotina sugerida para homologação

- **Backup agendado diário**, cifrado, com cópia externa, ensaio de restauração semanal e política de versões (14 versões, nunca menos de 7 dias).
- Backup **manual antes** de cada migração, de cada expurgo (`Retencao-De-Dados.ps1 -Expurgar`) e de cada atualização de versão (os manuais nunca entram na política de versões).
- Olhar `Status-Ambiente.ps1` (ou o monitor agendado) e **tratar o `ALERTA-BACKUP.txt`** como incidente.
- O RPO de um backup diário é de até 24 h; o RTO observado foi de cerca de 1 a 2 minutos para 90 MB (a escala muda com o volume: refaça o ensaio com dados reais de homologação).

## Limites conhecidos

- **Arquivos de mídia do WhatsApp (ADR 0028) ficam FORA do backup do banco.** O `pg_dump` leva `conversations.message_attachments` (estado, tipo, tamanho, resumo), mas os bytes estão na pasta `Conversations:Attachments:RootPath`. Copie essa pasta junto com o backup (e proteja a cópia como o backup); sem ela, depois de restaurar, o arquivo aparece como disponível e o Api responde 404 ao abri-lo. O ensaio de restauração isolado **não** valida a pasta de arquivos.

- Backup lógico (`pg_dump`): não faz recuperação ponto no tempo (PITR); isso pede arquivamento de WAL e é decisão de infraestrutura.
- O destino externo é uma **pasta**: destinos de nuvem entram como pasta sincronizada ou compartilhamento mapeado (a credencial fica no sistema operacional). Um cliente próprio de S3/SFTP exigiria guardar credencial: não foi feito.
- O backup cifrado protege contra roubo do arquivo; contra perda da máquina só protege se a cópia externa existir **e a frase estiver guardada fora dela**.
- O Agendador de Tarefas só roda com a sessão do usuário aberta (por não guardar senha). Para rodar sem sessão, é preciso registrar a tarefa com conta de serviço: decisão de infraestrutura.
