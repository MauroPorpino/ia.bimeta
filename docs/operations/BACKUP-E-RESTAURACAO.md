# Backup e restauração do PostgreSQL (ADR 0025 e 0026)

Scripts em `tools\ops\` para o banco de desenvolvimento, o de teste ou o de homologação, **e para a pasta privada de anexos** (arquivos de mídia do WhatsApp, ADR 0028 e 0031) quando se informa `-PastaDeAnexos`. Servem a qualquer origem pela mesma interface: `-Origem Desenvolvimento | Teste | Ambiente` (com `-VariavelDeConexao NOME`, o nome da variável de ambiente que guarda a string de conexão da homologação). Só o PostgreSQL do projeto (porta **5433**) é aceito. Segredos nunca são impressos: a senha vive numa variável de ambiente **deste processo**, durante cada chamada.

| Script | O que faz | Altera o banco? |
|---|---|---|
| `Backup-Agendado.ps1` | **o backup automático**: backup + verificação + cifra + cópia externa + ensaio periódico + política de versões + alerta (veja abaixo); com `-PastaDeAnexos` (ou `pastaDeAnexos` no JSON) leva os anexos no mesmo backup | não (leitura) |
| `Backup-Banco.ps1` | `pg_dump -Fc` + `manifesto.json` (SHA-256, tamanho, entradas do dump, contagem exata de linhas por tabela) em `C:\Projetos\ferramentas\backups\ia.bimeta\<rótulo>-<data>\`; com `-PastaDeAnexos`, copia os anexos **depois** do dump (`anexos\` + `anexos-manifesto.json`) | não (leitura) |
| `Verificar-Backup.ps1` | confere manifesto, tamanho, SHA-256 e a leitura do dump (`pg_restore --list`); recusa dump corrompido ou truncado; com `-ChaveEnv`, a verificação do backup **cifrado** é completa; se o backup leva anexos, confere também o `anexos-manifesto.json` e cada arquivo (tamanho e SHA-256, nenhum a mais ou a menos) | não |
| `Restaurar-Backup.ps1` | verifica o backup, cria um banco **novo**, restaura (`--no-owner --no-privileges --exit-on-error`) e compara as contagens com o manifesto; `-ChaveEnv` para o cifrado; `-PastaDeAnexosDestino` restaura os anexos numa pasta **nova** (sem ele, um backup com anexos **avisa** que os arquivos não foram restaurados) | só cria o banco novo; **recusa** destino existente |
| `Restaurar-Anexos.ps1` | restaura **só** os anexos de um backup numa pasta nova (banco já restaurado sem os arquivos, ou pasta de anexos perdida) | não |
| `Ensaiar-Restauracao.ps1` | backup + restauração + conferências em **banco isolado**, mede os tempos e remove o que criou; `-ChaveEnv` cifra, decifra e restaura; com `-PastaDeAnexos` restaura também os anexos numa pasta temporária e confere o SHA-256 contra o manifesto e a origem; `-SimularPerdaDaOrigem` prova a recuperação **sem a pasta original** | não |
| `Registrar-Agendamento.ps1` | registra (ou remove) as tarefas `ia.bimeta-backup` e `ia.bimeta-monitor` no Agendador; sem `-Registrar` só mostra | só as duas tarefas |
| `Status-Ambiente.ps1` | estado geral, inclusive do backup (`OPERACAO-E-SUPERVISAO.md`) | não |
| `Testar-Automacao-De-Backup.ps1` | teste de ponta a ponta de tudo isto numa instância descartável, com anexos sintéticos | não (nenhum banco real) |
| `Backup-BancoDeDesenvolvimento.ps1` | o script anterior (só desenvolvimento), mantido | não |

`_Anexos.ps1` (como `_Comum.ps1`, `_Cifra.ps1` e `_Backup.ps1`) é biblioteca dos scripts acima, não se executa sozinho.

## Regras que os scripts impõem

- **Nunca restaura sobre banco existente.** Não há opção para sobrescrever e nada é apagado. "Restaurar no lugar" é uma decisão humana: restaure em um banco novo, confira, e só então troque a string de conexão ou renomeie os bancos (`ALTER DATABASE ... RENAME TO ...`), com Api e Worker parados. **Os anexos seguem a mesma regra:** a pasta de destino precisa ser nova (ou existir vazia); existente e com conteúdo é recusada, sem sobrescrever nem apagar.
- O nome do banco de destino aceita só minúsculas, números e sublinhado (3 a 63 caracteres, começando por letra).
- O ensaio só apaga o que **ele mesmo** criou: a instância descartável (pasta `_instancia-ensaio-*` dentro da pasta de backups), a pasta temporária de anexos restaurados (`_ensaio-anexos-*`) ou o banco `iabimeta_ensaio_*` criado nesta execução. `-SimularPerdaDaOrigem` só **renomeia** a pasta de origem e a devolve; nunca a apaga.
- Um backup sem manifesto, ou cujo SHA-256, tamanho ou número de entradas não bate, **não é restaurado**. O mesmo vale para os anexos: manifesto de anexos alterado, arquivo alterado, ausente, truncado ou a mais, ou chave fora do formato, e nada é restaurado.
- **Nenhum segredo** (senha do banco, frase de cifra, URL de webhook) em log, estado, alerta, manifesto ou saída: só os **nomes** das variáveis de ambiente. Os scripts também **nunca veem a chave de cifra dos anexos** (ela é do Api/Worker): só leem os 4 primeiros bytes de cada objeto, para saber se está cifrado.

## Backup automático (`Backup-Agendado.ps1`)

```powershell
.\tools\ops\Backup-Agendado.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION `
    -ChaveEnv IABIMETA_BACKUP_KEY -DestinoExterno \\servidor\backups\ia.bimeta -ManterUltimos 14 `
    -EnsaiarRestauracaoACadaDias 7 -AlertaWebhookEnv IABIMETA_ALERTA_WEBHOOK `
    -PastaDeAnexos D:\ia.bimeta\anexos
.\tools\ops\Backup-Agendado.ps1 -ArquivoDeConfiguracao D:\config\backup-agendado.json   # o mesmo, por arquivo (backup-agendado.exemplo.json, com "pastaDeAnexos")
```

Cada execução faz, em ordem, e para na primeira falha:

0. **Anexos, antes do dump** (só com `-PastaDeAnexos` e cifra exigida, veja a regra abaixo): confere que a pasta existe e que **todo objeto está cifrado na origem**; se não, falha já aqui, sem gastar o dump.
1. **Backup** (`agendado-AAAAMMDD-HHMMSS`): o `pg_dump` e, **depois dele**, a cópia dos anexos; **verificação** (SHA-256, tamanho, leitura do dump e, para os anexos, o SHA-256 e o tamanho de cada arquivo contra o `anexos-manifesto.json`).
2. **Cifra** (só com `-ChaveEnv`): AES-256 + HMAC; confere a ida e volta (decifra, compara o SHA-256 e lê o dump) **antes** de apagar o dump em texto claro. Depois só o `.dump.enc` fica no disco. **Os anexos não são cifrados de novo**: os que já vêm cifrados do Api (cabeçalho `IABA`) são copiados como estão (cifra dupla seria lenta e sem ganho).
3. **Cópia externa** (só com `-DestinoExterno`): outra pasta, disco, compartilhamento UNC ou pasta de nuvem sincronizada. Sem credencial no script: quem autentica é o Windows (compartilhamento mapeado ou `cmdkey`). **Nunca sobrescreve**, copia para `.parcial`, confere o SHA-256 (do dump **e de cada anexo**) e só então renomeia; não cria a raiz do destino; **recusa copiar backup sem cifra** (salvo `-PermitirExternoSemCriptografia`) e **recusa copiar qualquer anexo em texto claro**.
4. **Ensaio de restauração** (só com `-EnsaiarRestauracaoACadaDias N`): restaura numa instância descartável e compara as contagens; com anexos, restaura-os numa pasta temporária **nova** (`_ensaio-anexos-*`, apagada no fim) e confere o SHA-256 de cada objeto contra o manifesto; reprovou = falha.
5. **Política de versões** (só com `-ManterUltimos N`, N ≥ 3): mantém as N mais recentes e **nunca apaga uma com menos de `-ManterDiasMinimos` dias** (padrão 7). Só toca pastas `agendado-*` (nunca backups manuais) e só depois de um backup novo verificado. **Sem N explícito, nada é apagado.** Na cópia externa, só com `-ExternoManterUltimos`. `-SoSimularVersoes` mostra o que removeria. A pasta **inteira** da versão sai, **anexos incluídos**.

**Estado e alerta.** `estado-backup.json` (último sucesso, última falha, versão, tamanho, cifra, ensaio e, com anexos, `anexosObjetos`, `anexosTamanhoEmBytes`, `anexosCifradosNaOrigem` e `anexosLegadosSemCifra`) e `backup-agendado.log` ficam na pasta de backups, sem segredo. **Falha:** código 1, `ALERTA-BACKUP.txt` na pasta (some no próximo sucesso) e, com `-AlertaWebhookEnv NOME` (a variável guarda a URL), um POST JSON `{"text": ...}`. A versão que falhou no meio fica marcada `INVALIDO.txt`: nunca conta como backup bom nem entra na política, e **nunca é apagada sozinha**.

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
.\tools\ops\Backup-Banco.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -Rotulo homologacao -PastaDeAnexos D:\ia.bimeta\anexos
.\tools\ops\Verificar-Backup.ps1 -Pasta C:\Projetos\ferramentas\backups\ia.bimeta\homologacao-20261106-130000
```

As contagens do manifesto saem logo depois do dump. Para comparação **exata** na restauração, o banco de origem deve estar em repouso (Api e Worker parados) durante o backup; se recebeu gravações, uma diferença é esperada e o script avisa. **Antes de uma migração, de um expurgo ou de uma atualização, faça o backup manual com `-PastaDeAnexos`**: o backup só do banco deixa os arquivos sem proteção.

## Ensaio de restauração (prova de que o backup restaura)

```powershell
.\tools\ops\Ensaiar-Restauracao.ps1 -Origem Teste
.\tools\ops\Ensaiar-Restauracao.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -ChaveEnv IABIMETA_BACKUP_KEY
.\tools\ops\Ensaiar-Restauracao.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -PastaDeAnexos D:\ia.bimeta\anexos -SimularPerdaDaOrigem   # Api e Worker parados
```

Modos (`-Modo`):

- **`Instancia` (padrão):** sobe uma **instância PostgreSQL descartável** com os mesmos binários (`initdb` numa pasta temporária, porta livre, só loopback, senha aleatória), restaura nela e a apaga no fim. Não exige privilégio no servidor do projeto e não toca em nenhum banco real.
- **`Servidor`:** cria `iabimeta_ensaio_<data-hora>` no próprio servidor (exige um papel com `CREATEDB`; os papéis `iabimeta_dev` e `iabimeta_test` não têm) e o remove no fim.

`-Manter` não apaga nada no fim (para inspeção; a limpeza passa a ser manual).

**Resultados medidos (06/11/2026, banco de teste, ~90 MB, 54 tabelas, 290 entradas):** ensaio simples: backup 14,5 s, restauração 54,6 s; backup agendado **cifrado + cópia externa + ensaio de restauração**: 267 s no total (cifra e verificação ~65 s, ensaio ~100 s), **54 tabelas com contagem idêntica**, nenhuma restrição pendente, instância apagada; segunda execução (sem ensaio) em 87 s. Também exercitados: recusa de banco existente, de dump com 1 byte alterado (SHA-256), de dump truncado (tamanho), de frase errada e de restauração do cifrado sem a frase.

**Anexos (10/11/2026, `Testar-Automacao-De-Backup.ps1`, instância descartável, anexos sintéticos de até 2,6 MB, máquina com outro processo compilando):** 103 verificações, 0 falhas, 321 s no total (as seções de anexos levam cerca de 2 min dos 321 s e incluem dois ensaios com instância descartável nova e as cifras do dump). Provados: backup com 2 tenants (cifrado e legado) com manifesto determinístico e 5 ignorados contados; dump terminando antes do primeiro anexo copiado; reprovação por byte alterado, arquivo ausente, extra, truncado, manifesto adulterado e chaves `..\x`, `../../x.bin`, `C:\...` e `/etc/...` (recusadas antes de copiar); restauração em pasta nova e recusa de destino não vazio; recuperação **sem a pasta original** (renomeada), também pelo ensaio com `-SimularPerdaDaOrigem`; backup cifrado com anexo legado falhando com alerta, e com anexos já cifrados copiando para fora **sem texto claro e sem cifra dupla**; ensaio agendado restaurando os anexos; política de versões levando os anexos junto; nenhum segredo nem caminho de origem nos manifestos, logs, estado e alertas. **Não medido:** volume real de anexos (o tempo de cópia e de verificação cresce com o número e o tamanho dos objetos: refaça o ensaio com os dados da homologação).

## Restaurar de verdade (homologação)

1. Pare o Api e o Worker.
2. `Backup-Banco.ps1` do estado atual (se ainda houver banco) e `Verificar-Backup.ps1 -ChaveEnv ...` do backup a restaurar.
3. `.\tools\ops\Restaurar-Backup.ps1 -Pasta <pasta do backup> -BancoDeDestino iabimeta_hml_restaurado -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -ChaveEnv IABIMETA_BACKUP_KEY -PastaDeAnexosDestino D:\ia.bimeta\anexos-restaurados` (o papel da conexão precisa de `CREATEDB`; `-ChaveEnv` só para backup cifrado; `-PastaDeAnexosDestino` só para backup com anexos, e a pasta tem de ser **nova**).
4. Confira (o script já compara contagens e o SHA-256 de cada anexo), aponte `Conversations:Attachments:RootPath` do Api/Worker para a pasta restaurada **e configure a mesma chave de cifra dos anexos da origem**, suba o Api contra o banco novo e teste o login, uma conversa e a abertura de um anexo antigo. **Depois de restaurar banco e arquivos**, rode o comando `attachments check` do host (veja "Conferir o banco contra a pasta restaurada" em "Anexos privados no backup").
5. Só então troque a conexão (ou renomeie os bancos). O banco antigo fica até você decidir removê-lo, por conta própria.

## Rotina sugerida para homologação

- **Backup agendado diário**, cifrado, com **a pasta de anexos** (`pastaDeAnexos`), cópia externa, ensaio de restauração semanal e política de versões (14 versões, nunca menos de 7 dias). Antes de agendar, cifre os anexos antigos na origem (`attachments encrypt-existing --confirm` no Api); sem isso o backup cifrado falha de propósito.
- Backup **manual antes** de cada migração, de cada expurgo (`Retencao-De-Dados.ps1 -Expurgar`) e de cada atualização de versão (os manuais nunca entram na política de versões), **com `-PastaDeAnexos`**.
- Olhar `Status-Ambiente.ps1` (ou o monitor agendado) e **tratar o `ALERTA-BACKUP.txt`** como incidente.
- O RPO de um backup diário é de até 24 h; o RTO observado foi de cerca de 1 a 2 minutos para 90 MB (a escala muda com o volume: refaça o ensaio com dados reais de homologação).

## Anexos privados no backup

O `pg_dump` leva a tabela `conversations.message_attachments` (estado, tipo, tamanho, resumo), mas os **bytes** dos arquivos (imagem, áudio, documento recebidos ou enviados, ADR 0028 e 0031) ficam numa pasta do servidor, `Conversations:Attachments:RootPath`. Sem copiá-la, depois de restaurar o banco as linhas dizem "disponível" e o arquivo não existe (o Api responde 404). Por isso **o mesmo backup leva o banco e a pasta de anexos**, e a restauração recupera os dois em ambiente novo, **inclusive depois da perda da pasta original**.

### O que entra

- Informe a pasta com `-PastaDeAnexos <pasta>` (ou `"pastaDeAnexos"` no JSON do backup agendado). A ferramenta **não** a descobre: ela é o valor de `Conversations:Attachments:RootPath` do Api/Worker.
- Entram só os **objetos**: `<raiz>\<tenant: 32 hex minúsculos>\<arquivo: 32 hex minúsculos>.bin` (o formato do armazenamento; qualquer caminho que não case com ele é ignorado). **Tudo o mais é ignorado e contado** (por exemplo `*.parcial` de gravações em andamento, arquivos estranhos, nomes em maiúsculas, subpastas): o número sai no resumo do backup e no `anexos-manifesto.json`.
- Os objetos são **imutáveis** (gravados uma vez) e só saem da pasta pela rotina de retenção. Por isso copiar de novo um objeto que já existe no destino com o mesmo SHA-256 é seguro (a cópia é idempotente); com SHA-256 **diferente**, a cópia falha e **nunca sobrescreve**.
- Estrutura do backup (a pasta da versão): `<banco>.dump` (ou `.dump.enc`), `manifesto.json`, `anexos\<tenant>\<arquivo>.bin` e `anexos-manifesto.json`. Sem `-PastaDeAnexos`, nada disso muda: o backup e o `manifesto.json` são os de sempre, sem o bloco `anexos`.

### A ordem: primeiro o dump, depois os arquivos

O `pg_dump` roda **antes** da cópia dos anexos, sempre. Assim toda linha de `message_attachments` que está no dump tem o seu objeto na cópia (salvo o que a retenção tiver apagado no meio); os objetos criados **depois** do dump são "órfãos" inofensivos (existem na cópia, mas nenhuma linha do banco restaurado os referencia; entram de novo no próximo backup). A ordem inversa criaria o problema que se quer evitar: linhas "disponíveis" sem arquivo, justamente as mais recentes. O teste automatizado prova a ordem pelos instantes dos arquivos: o dump termina antes de o primeiro anexo ser copiado.

### O formato do manifesto

`anexos-manifesto.json` (na pasta da versão; gravado por arquivo temporário e troca; **determinístico**: o mesmo conteúdo gera o mesmo texto, salvo o instante `criadoEmUtc`; **sem caminho absoluto da origem**):

```json
{
  "versaoDoManifesto": 1,
  "criadoEmUtc": "2026-11-09T12:00:00Z",
  "objetos": 4, "tamanhoTotalEmBytes": 1512186, "tenants": 2, "ignorados": 1,
  "cifradosNaOrigem": 4, "legadosSemCifra": 0,
  "arquivos": [
    { "chave": "<tenant>/<arquivo>.bin", "tamanhoEmBytes": 8000, "sha256": "<64 hex>", "cifradoNaOrigem": true }
  ]
}
```

`arquivos` vem ordenado por `chave`. O `manifesto.json` principal ganha, **só quando houve `-PastaDeAnexos`**, o bloco `"anexos": { "manifesto": "anexos-manifesto.json", "objetos": 4, "tamanhoTotalEmBytes": 1512186, "sha256DoManifesto": "..." }`: o SHA-256 do manifesto de anexos fica preso ao manifesto principal, então adulterar um sem o outro é detectado.

A **verificação** (`Verificar-Backup.ps1`, o backup agendado, o ensaio e a restauração) confere o SHA-256 do `anexos-manifesto.json` contra o bloco e, depois, cada arquivo: presente, com o **tamanho** e o **SHA-256** do manifesto, **nenhum arquivo a mais**, chaves no formato exato (nenhuma com `..`, barra invertida ou caminho absoluto), contagens e total coerentes. Qualquer divergência reprova (código de saída diferente de 0).

### A regra de cifra

Os objetos novos são cifrados pelo Api (AEAD, cabeçalho de 4 bytes ASCII `IABA` no início do arquivo); os **legados** ficam em texto claro. Os scripts só **leem os 4 primeiros bytes** para classificar; nunca decifram nada e nunca veem a chave.

- **Com `-ChaveEnv` (backup cifrado), ou com destino externo** (salvo `-PermitirExternoSemCriptografia`, a decisão explícita de mandar texto claro para fora, que vale também para os anexos), **todo objeto precisa estar cifrado na origem.** Havendo legado, o backup agendado **falha** (alerta) com a mensagem: *"há N anexo(s) sem cifra em repouso; rode 'attachments encrypt-existing --confirm' no Api antes de fazer backup cifrado ou com destino externo"*. Nada é cifrado nem copiado em texto claro para fora; a checagem acontece **antes do dump** e de novo sobre o que foi de fato copiado, e a própria cópia externa recusa texto claro de novo (defesa em profundidade).
- Objetos **já cifrados na origem são copiados como estão**: nada de cifra dupla (seria lenta e sem ganho; a proteção é a do Api).
- Sem `-ChaveEnv` e sem destino externo, o backup **local** de texto claro é permitido, com **AVISO** no log e na saída: o backup local fica na mesma máquina.

### Como restaurar

Com o banco, na restauração de verdade (veja "Restaurar de verdade"):

```powershell
.\tools\ops\Restaurar-Backup.ps1 -Pasta <pasta do backup> -BancoDeDestino iabimeta_hml_restaurado -Origem Ambiente `
    -VariavelDeConexao IABIMETA_HML_CONNECTION -ChaveEnv IABIMETA_BACKUP_KEY -PastaDeAnexosDestino D:\ia.bimeta\anexos-restaurados
```

Só os arquivos (banco já restaurado sem eles, ou a pasta perdida):

```powershell
.\tools\ops\Restaurar-Anexos.ps1 -Pasta <pasta do backup> -Destino D:\ia.bimeta\anexos-restaurados
```

- A restauração **verifica tudo primeiro**, **recusa** destino existente e **não vazio** (nunca sobrescreve nem apaga; um destino existente mas vazio é aceito), recusa **antes de copiar** qualquer chave fora do formato (defesa contra manifesto adulterado: *path traversal*), copia com `.parcial` + SHA-256 + renomeação e, no fim, **relê** o que ficou no disco e confere o número de objetos e o SHA-256 de cada um contra o manifesto.
- `Restaurar-Backup.ps1` verifica os anexos e o destino **antes** de criar o banco (falha cedo) e restaura os arquivos **depois** do banco. Se o backup tem anexos e `-PastaDeAnexosDestino` **não** foi informado, o script restaura só o banco e imprime um **AVISO** claro de que os arquivos não foram restaurados e como fazê-lo (sem falhar).
- Depois: aponte `Conversations:Attachments:RootPath` do Api/Worker para a pasta restaurada e use a **mesma chave de cifra dos anexos** da origem.

### Conferir o banco contra a pasta restaurada (`attachments check`)

Os scripts provam que **cada arquivo** voltou idêntico ao manifesto; quem confere o **banco contra a pasta** (cada linha "disponível" tem o seu objeto, cada objeto tem a sua linha) é o comando do host `attachments`, que existe para quem opera o armazenamento. **Rode-o DEPOIS de restaurar o banco e os arquivos**, com o Api e o Worker parados, o Api configurado para o banco restaurado, `Conversations:Attachments:RootPath` apontando para a pasta restaurada e a mesma chave de cifra dos anexos da origem:

```powershell
dotnet run --project backend\host\IaBimeta.Api -- attachments check                                # só relata (nada é alterado)
dotnet run --project backend\host\IaBimeta.Api -- attachments check --repair --confirm              # depois de conferir o relato
dotnet run --project backend\host\IaBimeta.Api -- attachments check --tenant <ID da empresa>        # uma empresa só
```

(ou o executável publicado do Api, com os mesmos argumentos.) O relato traz as duas situações de consistência:

- **Linhas sem objeto** (`lost`): a linha diz "disponível" mas o arquivo não está na pasta. Depois de uma restauração são os objetos que a retenção apagou depois do dump (ou que sumiram durante a cópia). `--repair --confirm` as marca como `lost` (indisponíveis), para a tela não oferecer um arquivo que não abre.
- **Objetos sem linha (órfãos)**: o arquivo está na pasta mas nenhuma linha do banco restaurado o referencia; depois de uma restauração são os objetos criados depois do dump. São inofensivos. `--repair --confirm` apaga só os mais antigos que `--orphan-days` (padrão 1 dia), e apenas na pasta **restaurada**: a cópia do backup não é tocada.

Cuidados: (1) **sempre rode primeiro sem `--repair`** e leia o relato; (2) se **quase todas** as linhas aparecem sem objeto, quase certamente `RootPath` aponta para a pasta errada (ou vazia): **não use `--repair`** antes de corrigir isso, senão o reparo marcaria tudo como `lost` (quando **todas** as linhas estão sem objeto e a pasta não tem **nenhum** objeto para o alcance pedido, o comando **recusa** o reparo sozinho, sem alterar nada; a perda real se confirma com `--allow-empty-folder`); (3) o reparo exige `--confirm` e altera o banco e a pasta restaurados: faça-o só no ambiente restaurado, nunca no de produção sem um backup novo.

### O ensaio: a perda da pasta original

`Ensaiar-Restauracao.ps1 -PastaDeAnexos <pasta>` faz o backup (banco e anexos), restaura o banco na instância descartável e os anexos numa pasta temporária **nova**, e confere: as contagens do banco, o SHA-256 de cada objeto restaurado contra o **manifesto** e contra a **origem**. Os anexos são restaurados **só do backup**: o ensaio não lê a pasta de origem para restaurar.

`-SimularPerdaDaOrigem` **prova** isso: durante toda a restauração a pasta de origem é **renomeada** para `<nome>.perdida-ensaio-<data>` (qualquer leitura dela falharia), e é devolvida ao nome original no fim, antes das comparações. Nunca apaga nada; se não conseguir devolver, o ensaio avisa o caminho exato da pasta renomeada. Use com o **Api e o Worker parados** (a pasta fica sem o nome durante o ensaio, e o Windows pode recusar o renome se algum processo a estiver usando; nesse caso o ensaio falha sem alterar nada).

O ensaio periódico do backup agendado (`-EnsaiarRestauracaoACadaDias`) também restaura os anexos numa pasta temporária e confere o SHA-256 contra o manifesto (a pasta é apagada no fim).

### Inconsistências possíveis e o que fazer

| Situação | Efeito | O que fazer |
|---|---|---|
| Objeto **apagado pela retenção depois do dump** | no banco restaurado a linha diz "disponível", mas o arquivo não existe (404 ao abrir) | rode `attachments check` no ambiente restaurado (veja a próxima subseção): ele lista essas linhas ("sem objeto no armazenamento") e, com `--repair --confirm`, as marca como `lost` (indisponíveis). É a retenção agindo, não perda de backup. Se a cópia ainda tem o objeto (retenção depois da cópia), restaure-o de lá |
| Objeto sumiu **durante a cópia** (retenção no meio) | o backup segue e **avisa** (`AVISO: N objeto(s) sumiram durante a copia`) | o mesmo `attachments check` depois de restaurar |
| **Órfãos**: objetos criados depois do dump | existem na cópia, nenhuma linha do banco restaurado os referencia | inofensivos; o `attachments check` os lista como "objetos sem linha (órfãos)" e o reparo só apaga os mais antigos que `--orphan-days` (padrão 1 dia), na pasta **restaurada**, nunca na cópia do backup; não apague a pasta toda |
| Pasta de anexos **vazia** no backup | `AVISO: nenhum objeto de anexo foi encontrado` | confira se `-PastaDeAnexos` é a pasta certa (`Conversations:Attachments:RootPath`) |
| Restaurou **sem a chave de cifra dos anexos** | os arquivos cifrados estão lá, mas não abrem | configure no Api/Worker novo a **mesma** chave da origem (veja abaixo) |
| Backup falhou **depois** de copiar parte dos anexos | pasta da versão com `INVALIDO.txt` (nunca conta como backup bom e nunca é apagada sozinha) | examine e remova a pasta por conta própria |

### Crescimento do espaço

Cada versão é uma **cópia completa** dos anexos (sem incremental ainda): o espaço de uma versão é `dump + anexos`, e com a política de versões de 14 versões a pasta de backups chega a cerca de 14 vezes o tamanho dos anexos (mais a cópia externa, se houver). O backup agendado confere o espaço livre antes (o tamanho dos anexos da última execução, uma vez, ou duas se o ensaio estiver devido). Como os objetos são imutáveis, um backup **incremental** (copiar só os objetos que o manifesto anterior não tem, com *hard links* ou uma pasta única de objetos referenciada pelo manifesto de cada versão) é possível no futuro: **P2**, a fazer só quando o volume justificar.

### A chave de cifra dos anexos

**A chave de cifra dos anexos (configuração do Api/Worker) precisa ser guardada junto da frase de cifra do backup, fora da máquina do banco (cofre de senhas ou cópia protegida, separada do destino externo).** Os scripts nunca a veem e o backup não a contém: **sem ela, os anexos restaurados não abrem**, mesmo com a pasta e o banco perfeitos. Perder a chave é perder o conteúdo dos anexos cifrados, como perder a frase é perder o backup cifrado.

## Limites conhecidos

- Backup lógico (`pg_dump`): não faz recuperação ponto no tempo (PITR); isso pede arquivamento de WAL e é decisão de infraestrutura.
- O destino externo é uma **pasta**: destinos de nuvem entram como pasta sincronizada ou compartilhamento mapeado (a credencial fica no sistema operacional). Um cliente próprio de S3/SFTP exigiria guardar credencial: não foi feito.
- O backup cifrado protege contra roubo do arquivo; contra perda da máquina só protege se a cópia externa existir **e a frase estiver guardada fora dela**.
- O Agendador de Tarefas só roda com a sessão do usuário aberta (por não guardar senha). Para rodar sem sessão, é preciso registrar a tarefa com conta de serviço: decisão de infraestrutura.
