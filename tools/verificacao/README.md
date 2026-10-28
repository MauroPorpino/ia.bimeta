# Verificadores de etapa (`tools/verificacao/`)

Scripts PowerShell **somente leitura** que protegem cada etapa da migração modular (plano aprovado em 27/10/2026, etapa M0). Nasceram do incidente de 27/10/2026, em que a função `Rd` de um script de verificação foi resolvida como o alias de `Remove-Item` e apagou documentos ainda não commitados.

## Regra de validação das etapas

| Momento | Script | Exigência |
|---|---|---|
| Antes da etapa | `Test-EntradaDeEtapa.ps1` | árvore **limpa**, branch e HEAD esperados, `main` intocada, backup válido |
| Depois das alterações, antes do commit | `Test-SaidaDeEtapa.ps1` | a árvore difere do HEAD **somente** pelas diferenças autorizadas |
| Depois do commit aprovado | `Test-PosCommit.ps1` | árvore **limpa**, um único commit sobre o pai esperado, com exatamente as diferenças autorizadas |

Nenhum commit é criado sem aprovação específica, e nunca há `push`.

## Scripts

| Arquivo | Função |
|---|---|
| `Comum.ps1` | funções compartilhadas (carregado por dot-source) |
| `Test-SegurancaDeScript.ps1` | analisa scripts pelo parser AST: alias, colisão de função com alias/cmdlet, comando fora da lista permitida, comando dinâmico, redirecionamento para arquivo, método .NET de escrita, `git` e `dotnet` fora da lista, caractere não ASCII |
| `Test-EntradaDeEtapa.ps1` | gate de entrada |
| `Test-SaidaDeEtapa.ps1` | gate de saída, antes do commit |
| `Test-PosCommit.ps1` | gate após o commit aprovado |
| `Test-GrafoDeProjetos.ps1` | grafo de `ProjectReference` × `module-dependencies.json` (ARQ-04, ARQ-05, ARQ-06, DEP-4/6). A regra já é protegida também pelos testes C# de `tests/IaBimeta.ArchitectureTests`, que rodam em `dotnet test`; este script segue como conferência independente, sem depender do SDK de testes |
| `Test-FormatoDeArquivos.ps1` | LF no que será commitado, sem BOM nem falta de nova linha em arquivo novo, scripts ASCII |
| `Test-TestesDaSolucao.ps1` | executa `dotnet test` e confere aprovados, ignorados e falhas |
| `Get-LinhaDeBase.ps1` | emite a linha de base em texto (quem chama decide onde gravar) |

## Regras para escrever scripts aqui

- PowerShell 5.1, **somente ASCII** (o 5.1 lê script sem BOM como ANSI), com `Set-StrictMode -Version Latest` e `$ErrorActionPreference = 'Stop'`.
- Nomes `Verbo-Substantivo`. **Sem aliases** (`ls`, `rd`, `ri`, `del`, `cat`, `%`, `?`...) e sem função com nome de alias ou cmdlet: o alias vence a função.
- **Sem comando de escrita ou exclusão**: nada de `Remove-*`, `Set-Content`, `Out-File`, `New-Item`, `Copy-Item`, `Move-Item` nem métodos .NET equivalentes. Saída só em tela; quem chama redireciona para fora do repositório.
- `git` somente com subcomandos de leitura e com `--no-optional-locks` (nem o `git status` atualiza o índice). Nunca `git clean`, `git reset`, `git checkout --`, `git restore`, `git stash`, `git rm` ou `git mv` aqui.
- Todo script novo ou alterado passa por `Test-SegurancaDeScript.ps1` **antes** de ser executado.

## Perfis do verificador AST

| Perfil | Uso | Acrescenta |
|---|---|---|
| `Leitura` (padrão) | todos os scripts desta pasta | nada |
| `Preparacao` | scripts de operação pontual, fora desta pasta (backup, criação de branch) | `New-Item -ItemType Directory`, `Copy-Item`, `Set-Content`, `Add-Content`, `Out-File`, `WriteAllText`/`WriteAllLines`, `git bundle create`, `git switch -c` |

Remoção e movimentação nunca são permitidas em nenhum perfil.

## Lista de diferenças autorizadas

Um arquivo de texto, uma diferença por linha. Linhas vazias e iniciadas por `#` são ignoradas.

```text
A caminho/relativo           arquivo novo (adicionado ou não rastreado)
M caminho/relativo           arquivo modificado
D caminho/relativo           arquivo excluído (só quando autorizado)
R origem => destino          renomeação (use git mv, para o git reconhecer)
```

## Como executar

A partir da raiz do repositório:

```powershell
.\tools\verificacao\Test-SegurancaDeScript.ps1 -Caminho .\tools\verificacao
.\tools\verificacao\Test-EntradaDeEtapa.ps1 -BranchEsperada feature/migracao-modular -HeadEsperado <hash> -MainEsperada <hash> -Bundle <arquivo.bundle> -Legado IaBimeta.Domain,IaBimeta.Application,IaBimeta.Infrastructure,IaBimeta.Domain.Tests
.\tools\verificacao\Test-SaidaDeEtapa.ps1 -Autorizadas <lista.txt> -MainEsperada <hash>
.\tools\verificacao\Test-PosCommit.ps1 -PaiEsperado <hash> -Autorizadas <lista.txt> -MainEsperada <hash>
.\tools\verificacao\Test-TestesDaSolucao.ps1                        # usa os binários existentes
.\tools\verificacao\Test-TestesDaSolucao.ps1 -Compilar -ExigirSemIgnorados # compila antes (grava só em bin/obj) e exige zero ignorados
.\tools\verificacao\Get-LinhaDeBase.ps1 -ExecutarTestes
```

Todos terminam com código 0 (aprovado) ou 1 (reprovado).

**Se o Windows PowerShell recusar rodar os scripts** ("a execução de scripts foi desabilitada neste sistema"), que é a política padrão `Restricted`, execute-os com a política liberada **apenas naquele processo**, sem alterar a configuração do computador nem do usuário:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\verificacao\Test-TestesDaSolucao.ps1 -Compilar -ExigirSemIgnorados
```

Os scripts não são assinados. Os testes também rodam sem eles, com `dotnet test backend\IaBimeta.slnx`.

## Limites conhecidos

- A análise AST é estática: ela não impede um comando digitado fora de um script. Continua valendo a disciplina de nunca usar comandos de remoção nem `git clean`, `git reset --hard` ou `git checkout -- .` sem autorização.
- `dotnet test` sem `--no-build` grava em `bin/obj` (ignorados pelo Git) e no cache do NuGet.
- A cópia de trabalho de vários arquivos está em CRLF; o `.gitattributes` (`text eol=lf`) normaliza para LF no commit. As pendências de BOM dos `.csproj` antigos acabaram com a remoção dos projetos legados. As migrations geradas pelo `dotnet ef` saem com BOM e CRLF e precisam ser normalizadas antes do commit (o `Test-FormatoDeArquivos.ps1` acusa).

## Pendência registrada na linha de base (resolvida)

O teste de aceitação Mauro/Pedro (`Contexto_de_um_atendimento_nao_vaza_para_outro`) estava **ignorado** (`Skip`) na linha de base de 27/10/2026: ignorado conta como pendência, não como cobertura. Ele foi **substituído** por testes ativos com PostgreSQL real: o isolamento entre tenants (`MauroPedroIsolamentoDeTenantsTests`, etapa 1) e, no nível da conversa, `MauroPedroConversasTests` (etapa 3: sessões #1001 e #1002, tenants 1001 e 1002, contexto do turno, resposta só para o atendimento certo). `Test-TestesDaSolucao.ps1` **registra** a situação atual (linhas `INFO`: testes ignorados e estado do nome antigo, que agora aparece como removido), mas **não exige** nada sobre ele. Exigências opcionais: `-AprovadosEsperados` e `-IgnoradosEsperados` (`-1` = não verificar) e `-ExigirSemIgnorados` (zero testes ignorados).
