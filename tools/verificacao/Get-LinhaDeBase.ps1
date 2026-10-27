<#
.SYNOPSIS
    Emite a linha de base do repositorio em texto (stdout). Somente leitura: quem chama decide onde gravar.
    Inclui HEAD, branches, manifesto dos arquivos rastreados (modo, hash do blob, caminho), versoes das
    ferramentas, projetos, grafo e, com -ExecutarTestes, o resultado dos testes atuais.

.EXAMPLE
    .\Get-LinhaDeBase.ps1 -ExecutarTestes
#>
[CmdletBinding()]
param(
    [string]$Raiz = '',
    [switch]$ExecutarTestes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Comum.ps1"

if (-not $Raiz) { $Raiz = Get-RaizDoRepositorio }

Write-Output '== LINHA DE BASE DO REPOSITORIO =='
Write-Output ('Data/hora: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Output ('Raiz: ' + $Raiz)
Write-Output ('Branch atual: ' + ([string](git -C $Raiz --no-optional-locks rev-parse --abbrev-ref HEAD)).Trim())
Write-Output ('HEAD: ' + ([string](git -C $Raiz --no-optional-locks rev-parse HEAD)).Trim())
Write-Output ('main: ' + ([string](git -C $Raiz --no-optional-locks rev-parse main)).Trim())
Write-Output ('Ultimo commit: ' + ([string](git -C $Raiz --no-optional-locks log -1 --format=%s)).Trim())
$status = @(Get-StatusDaArvore -Raiz $Raiz)
Write-Output ('Arvore limpa: ' + ($status.Count -eq 0) + ' (diferencas: ' + $status.Count + ')')
foreach ($s in $status) { Write-Output ('  ' + $s) }

Write-Output ''
Write-Output '-- Ferramentas --'
Write-Output ('git: ' + ([string](git --version)).Trim())
Write-Output ('dotnet: ' + ([string](dotnet --version)).Trim())
foreach ($sdk in @(dotnet --list-sdks)) { Write-Output ('  SDK ' + $sdk) }

Write-Output ''
Write-Output '-- Projetos da solucao (dotnet sln list) --'
$slnRel = @(git -C $Raiz --no-optional-locks ls-files -- '*.slnx' '*.sln')
foreach ($rel in $slnRel) {
    Write-Output ('Solucao: ' + $rel)
    foreach ($linha in @(dotnet sln (Join-Path $Raiz ($rel.Replace('/', '\'))) list | Where-Object { $_ -like '*.csproj' })) { Write-Output ('  ' + $linha) }
}

Write-Output ''
Write-Output '-- Grafo de projetos (.csproj rastreados) --'
foreach ($p in (@(Get-GrafoDeProjetos -Raiz $Raiz) | Sort-Object Nome)) {
    $refs = ($p.Referencias -join ', ')
    if (-not $refs) { $refs = '(nenhuma)' }
    Write-Output ('  {0} ({1}) -> {2}' -f $p.Nome, $p.Caminho, $refs)
}

Write-Output ''
$manifesto = @(git -C $Raiz --no-optional-locks ls-tree -r HEAD)
Write-Output ('-- Manifesto dos arquivos rastreados (git ls-tree -r HEAD): ' + $manifesto.Count + ' arquivos --')
foreach ($m in $manifesto) { Write-Output ('  ' + $m) }

Write-Output ''
Write-Output '-- Pendencia registrada --'
Write-Output 'O teste de aceitacao Mauro/Pedro (Contexto_de_um_atendimento_nao_vaza_para_outro) estava IGNORADO (Skip) em 27/10/2026.'
Write-Output 'Deve ser ATIVADO (ou substituido) antes da validacao funcional multi-tenant. Ignorado conta como pendencia, nao como cobertura.'
Write-Output 'A situacao atual aparece na secao de testes abaixo (linhas INFO).'

if ($ExecutarTestes) {
    Write-Output ''
    Write-Output '-- Testes atuais (dotnet test --no-build) --'
    $saidaTestes = @(& "$PSScriptRoot\Test-TestesDaSolucao.ps1" -Raiz $Raiz 6>&1)
    $codigoTestes = $LASTEXITCODE
    foreach ($l in $saidaTestes) { Write-Output ([string]$l) }
    Write-Output ('Codigo de saida da verificacao de testes: ' + $codigoTestes)
}
