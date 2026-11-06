# Backup e restauração do PostgreSQL (ADR 0025)

Scripts em `tools\ops\` para o banco de desenvolvimento, o de teste ou o de homologação. Servem a qualquer origem pela mesma interface: `-Origem Desenvolvimento | Teste | Ambiente` (com `-VariavelDeConexao NOME`, o nome da variável de ambiente que guarda a string de conexão da homologação). Só o PostgreSQL do projeto (porta **5433**) é aceito. Segredos nunca são impressos: a senha vive numa variável de ambiente **deste processo**, durante cada chamada.

| Script | O que faz | Altera o banco? |
|---|---|---|
| `Backup-Banco.ps1` | `pg_dump -Fc` + `manifesto.json` (SHA-256, tamanho, entradas do dump, contagem exata de linhas por tabela) em `C:\Projetos\ferramentas\backups\ia.bimeta\<rótulo>-<data>\` | não (leitura) |
| `Verificar-Backup.ps1` | confere manifesto, tamanho, SHA-256 e a leitura do dump (`pg_restore --list`); recusa dump corrompido ou truncado | não |
| `Restaurar-Backup.ps1` | verifica o backup, cria um banco **novo**, restaura (`--no-owner --no-privileges --exit-on-error`) e compara as contagens com o manifesto | só cria o banco novo; **recusa** destino existente |
| `Ensaiar-Restauracao.ps1` | backup + restauração + conferências em **banco isolado**, mede os tempos e remove o que criou | não |
| `Backup-BancoDeDesenvolvimento.ps1` | o script anterior (só desenvolvimento), mantido | não |

## Regras que os scripts impõem

- **Nunca restaura sobre banco existente.** Não há opção para sobrescrever e nada é apagado. "Restaurar no lugar" é uma decisão humana: restaure em um banco novo, confira, e só então troque a string de conexão ou renomeie os bancos (`ALTER DATABASE ... RENAME TO ...`), com Api e Worker parados.
- O nome do banco de destino aceita só minúsculas, números e sublinhado (3 a 63 caracteres, começando por letra).
- O ensaio só apaga o que **ele mesmo** criou: a instância descartável (pasta `_instancia-ensaio-*` dentro da pasta de backups) ou o banco `iabimeta_ensaio_*` criado nesta execução.
- Um backup sem manifesto, ou cujo SHA-256, tamanho ou número de entradas não bate, **não é restaurado**.

## Fazer um backup

```powershell
.\tools\ops\Backup-Banco.ps1 -Origem Desenvolvimento -Rotulo pre-migracao
.\tools\ops\Backup-Banco.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION -Rotulo homologacao
.\tools\ops\Verificar-Backup.ps1 -Pasta C:\Projetos\ferramentas\backups\ia.bimeta\homologacao-20261106-130000
```

As contagens do manifesto saem logo depois do dump. Para comparação **exata** na restauração, o banco de origem deve estar em repouso (Api e Worker parados) durante o backup; se recebeu gravações, uma diferença é esperada e o script avisa.

## Ensaio de restauração (prova de que o backup restaura)

```powershell
.\tools\ops\Ensaiar-Restauracao.ps1 -Origem Teste
.\tools\ops\Ensaiar-Restauracao.ps1 -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION
```

Modos (`-Modo`):

- **`Instancia` (padrão):** sobe uma **instância PostgreSQL descartável** com os mesmos binários (`initdb` numa pasta temporária, porta livre, só loopback, senha aleatória), restaura nela e a apaga no fim. Não exige privilégio no servidor do projeto e não toca em nenhum banco real.
- **`Servidor`:** cria `iabimeta_ensaio_<data-hora>` no próprio servidor (exige um papel com `CREATEDB`; os papéis `iabimeta_dev` e `iabimeta_test` não têm) e o remove no fim.

`-Manter` não apaga nada no fim (para inspeção; a limpeza passa a ser manual).

**Resultado do ensaio de 06/11/2026** (banco de teste, 88,5 MB, 54 tabelas, 290 entradas): backup 14,5 s, restauração 54,6 s, **54 tabelas com contagem idêntica** ao manifesto, nenhuma restrição pendente, instância apagada. Também exercitados: recusa de banco existente, de dump com 1 byte alterado (SHA-256) e de dump truncado (tamanho).

## Restaurar de verdade (homologação)

1. Pare o Api e o Worker.
2. `Backup-Banco.ps1` do estado atual (se ainda houver banco) e `Verificar-Backup.ps1` do backup a restaurar.
3. `.\tools\ops\Restaurar-Backup.ps1 -Pasta <pasta do backup> -BancoDeDestino iabimeta_hml_restaurado -Origem Ambiente -VariavelDeConexao IABIMETA_HML_CONNECTION` (o papel da conexão precisa de `CREATEDB`).
4. Confira (o script já compara contagens), suba o Api contra o banco novo e teste o login e uma conversa.
5. Só então troque a conexão (ou renomeie os bancos). O banco antigo fica até você decidir removê-lo, por conta própria.

## Rotina mínima sugerida para homologação

- Backup **antes** de cada migração, de cada expurgo (`Retencao-De-Dados.ps1 -Expurgar`) e de cada atualização de versão.
- Um **ensaio de restauração** depois de cada mudança de esquema relevante e, no mínimo, a cada mês.
- Guardar os backups **fora da máquina** do banco (infraestrutura: cópia externa, retenção dos backups e criptografia são decisões de hospedagem, ainda não feitas).
- O RPO de um backup manual é o intervalo entre backups; o RTO observado foi de cerca de 1 minuto para 88 MB (a escala muda com o volume: refaça o ensaio com dados reais de homologação).

## Limites conhecidos

- Backup lógico (`pg_dump`): não faz recuperação ponto no tempo (PITR); isso pede arquivamento de WAL e é decisão de infraestrutura.
- Não há agendamento (Agendador de Tarefas do Windows ou serviço): hoje o backup é manual.
- O backup **não é criptografado** e fica na máquina local; contém dados de cliente quando houver dados reais. Proteja a pasta e cifre antes de copiar para fora.
